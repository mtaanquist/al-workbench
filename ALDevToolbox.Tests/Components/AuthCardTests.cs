using System.Text.Json;
using ALDevToolbox.Components.Pages;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Account;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The state machines PR 11 introduced when it moved the auth family onto the
/// card archetype. Each page used to render one long form with everything on
/// it; now each picks a card, and which card it picks is a rule with no other
/// guard — the pages are unreachable from the rest of the suite because they
/// sit outside the shell and outside authentication.
/// </summary>
public sealed class AuthCardTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly StubEmail _email = new();
    private readonly MutableSingleTenantMode _singleTenant = new();

    public AuthCardTests()
    {
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<AppDbContext>(opts => opts
            .UseNpgsql(_db.ConnectionString)
            .AddInterceptors(_db.CommandTracker)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        _ctx.Services.AddSingleton<IEmailService>(_email);
        _ctx.Services.AddSingleton<ISingleTenantMode>(_singleTenant);
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddSingleton(_db.DataProtectionProvider);
        _ctx.Services.AddScoped<InviteService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _ctx.Services.AddSingleton<IHttpContextAccessor>(_http);
        _ctx.Services.AddSingleton(new ALDevToolbox.Services.Account.WebAuthnConfig(string.Empty, [], "AL Workbench"));
        TestDb.AddToolServices(_ctx.Services);
    }

    private readonly HttpContextAccessor _http = new() { HttpContext = new DefaultHttpContext() };

    private sealed class StubEmail : IEmailService
    {
        public bool Configured { get; set; }
        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(Configured);
        public Task SendAsync(
            string to, string subject, string body, EmailPurpose purpose, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class MutableSingleTenantMode : ISingleTenantMode
    {
        public bool IsEnabled { get; set; }
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    // ---- Signup picks one of four cards ----

    [Fact]
    public void Signup_without_email_asks_for_everything_at_once()
    {
        _email.Configured = false;

        var cut = _ctx.Render<Signup>();

        cut.WaitForAssertion(() =>
        {
            // No SMTP means no confirmation email to send, so there is no
            // two-step flow to offer and the whole form has to be asked for.
            cut.Find("form.auth__card").GetAttribute("action").Should().Be("/auth/signup");
            cut.Find("#su-password").Should().NotBeNull();
        });
    }

    [Fact]
    public void Signup_with_email_asks_only_for_an_address()
    {
        _email.Configured = true;

        var cut = _ctx.Render<Signup>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("form.auth__card").GetAttribute("action").Should().Be("/auth/signup/start");
            cut.FindAll("#su-password").Should().BeEmpty("the password is chosen after the address is confirmed");
        });
    }

    [Fact]
    public void Signup_after_sending_puts_the_code_field_on_the_confirmation()
    {
        _email.Configured = true;

        Navigate("/signup?ok=check-email&email=kirsten.jensen%40cronus.example");
        var cut = _ctx.Render<Signup>();

        cut.WaitForAssertion(() =>
        {
            // The <details>"Already have a code?" this replaced put the field
            // behind a disclosure triangle on the page that had just told you a
            // code was coming. Now the confirmation is where you type it.
            cut.Find(".auth__ok").TextContent.Should().Contain("Check your email");
            cut.Find("#su-code").Should().NotBeNull();
            cut.Find("form.auth__card").GetAttribute("action").Should().Be("/auth/signup/verify-code");
            cut.Find(".auth__mail").TextContent.Trim().Should().Be("kirsten.jensen@cronus.example");
        });
    }

    [Fact]
    public void Signup_offers_a_way_back_to_the_code_card_for_people_who_arrive_later()
    {
        _email.Configured = true;

        Navigate("/signup?code=1");
        var cut = _ctx.Render<Signup>();

        // Someone who closed the tab and came back still has a code in an email
        // and needs somewhere to type it.
        cut.WaitForAssertion(() => cut.Find("#su-code").Should().NotBeNull());
    }

    [Fact]
    public void Signup_without_email_never_shows_the_code_card()
    {
        _email.Configured = false;

        Navigate("/signup?code=1");
        var cut = _ctx.Render<Signup>();

        // No SMTP means no code was ever sent; offering to check one would be
        // a dead end reachable by typing a query string.
        cut.WaitForAssertion(() => cut.FindAll("#su-code").Should().BeEmpty());
    }

    // ---- AcceptInvite ----

    [Fact]
    public void An_invite_link_with_no_token_says_so_rather_than_showing_an_empty_form()
    {
        var cut = _ctx.Render<AcceptInvite>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("That link is missing something");
            cut.FindAll("form.auth__card").Should().BeEmpty();
        });
    }

    [Fact]
    public void An_unknown_invite_token_reads_as_expired_rather_than_broken()
    {
        Navigate("/accept-invite?token=not-a-real-token");
        var cut = _ctx.Render<AcceptInvite>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("This invite has expired");
            cut.FindAll("form.auth__card").Should().BeEmpty();
        });
    }

    [Fact]
    public void A_live_invite_fixes_the_email_and_names_the_organisation()
    {
        var token = SeedInvite();

        Navigate($"/accept-invite?token={token}");
        var cut = _ctx.Render<AcceptInvite>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".auth__title").TextContent.Should().Contain("Default");
            var email = cut.Find("#ai-email");
            email.GetAttribute("value").Should().Be("new.consultant@cronus.example");
            // readonly, not disabled: a disabled input is skipped by the
            // keyboard and read past by screen readers, and this field is the
            // answer to "which account is this?".
            email.HasAttribute("readonly").Should().BeTrue();
            email.HasAttribute("disabled").Should().BeFalse();
            email.ClassList.Should().Contain("input--ro");
        });
    }

    // ---- LoginChallenge picks a method ----

    [Theory]
    // Asked for a method the account has: honoured.
    [InlineData(true, true, "email", "email-code")]
    [InlineData(true, true, "recovery", "rec")]
    [InlineData(true, true, null, "totp")]
    // Asked for one it does not have: falls back to something that works,
    // rather than rendering a form whose endpoint would refuse it.
    [InlineData(true, false, "email", "totp")]
    [InlineData(false, true, "recovery", "email-code")]
    [InlineData(false, true, null, "email-code")]
    public void The_challenge_only_offers_a_method_the_account_actually_has(
        bool totp, bool emailMfa, string? asked, string expectedFieldId)
    {
        GiveMfaCookie(totp, emailMfa);

        Navigate(asked is null ? "/login/challenge" : $"/login/challenge?method={asked}");
        var cut = _ctx.Render<LoginChallenge>();

        cut.WaitForAssertion(() => cut.Find("#" + expectedFieldId).Should().NotBeNull());
    }

    [Fact]
    public void A_single_method_needs_no_tab_strip()
    {
        GiveMfaCookie(totp: false, emailMfa: true);

        var cut = _ctx.Render<LoginChallenge>();

        // Email-only accounts have exactly one way in; a one-tab strip is chrome.
        cut.WaitForAssertion(() => cut.FindAll(".pill-tabs").Should().BeEmpty());
    }

    [Fact]
    public void An_expired_challenge_explains_itself_instead_of_showing_a_dead_form()
    {
        var cut = _ctx.Render<LoginChallenge>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("form").Should().BeEmpty("there is nothing left to submit");
            // Its own card, not the verify card wearing an error: the old shape
            // kept the title "Verify it's you" over a message saying you no
            // longer can, and the only exit was foot text reading "Cancel".
            cut.Find(".auth__title").TextContent.Trim().Should().Be("That took too long");
            cut.Find("a.btn--primary").GetAttribute("href").Should().Be("/login");
        });
    }

    // ---- SignupDetails ----
    //
    // The one page in the family that cannot be driven in a browser without
    // forging a Data-Protection cookie, so its two branches are pinned here
    // instead. Both were changed by the design review: the form branch had no
    // way out of the card at all, and its button interpolated an organisation
    // name up to 80 characters into a 372px control.

    [Fact]
    public void Finishing_signup_always_leaves_a_way_back_out()
    {
        GiveVerifiedEmail("new.consultant@unclaimed.example");

        var cut = _ctx.Render<SignupDetails>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".auth__foot a").GetAttribute("href").Should().Be("/login");
            // Not the organisation name: an 80-character name does not fit the
            // button, and the title already carries it.
            cut.Find("button[type=submit]").TextContent.Trim().Should().Be("Create organisation");
        });
    }

    [Fact]
    public void A_claimed_email_domain_joins_that_organisation_rather_than_starting_one()
    {
        using (var seed = _db.NewContext())
        {
            seed.OrganizationEmailDomains.Add(new OrganizationEmailDomain
            {
                OrganizationId = TestDb.DefaultOrgId,
                Domain = "cronus.example",
            });
            seed.SaveChanges();
        }
        GiveVerifiedEmail("kirsten.jensen@cronus.example");

        var cut = _ctx.Render<SignupDetails>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("button[type=submit]").TextContent.Trim().Should().Be("Join organisation");
            // Joining an existing organisation must not offer to name a new one.
            cut.FindAll("#su-org-name").Should().BeEmpty();
            cut.Find(".auth__foot a").GetAttribute("href").Should().Be("/login");
        });
    }

    private void GiveVerifiedEmail(string email)
    {
        var protector = _db.DataProtectionProvider.CreateProtector(EndpointHelpers.SignupVerifiedProtectionPurpose);
        var state = new EndpointHelpers.SignupVerified(1, email, DateTime.UtcNow);
        var payload = protector.Protect(JsonSerializer.Serialize(state));
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"{EndpointHelpers.SignupVerifiedCookieName}={payload}";
        _http.HttpContext = ctx;
    }

    /// <summary>
    /// Query-string parameters reach these pages through
    /// [SupplyParameterFromQuery], which bUnit will not let you set directly —
    /// the URL is the input, so the test supplies a URL.
    /// </summary>
    private void Navigate(string url) =>
        _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo(url);

    private void GiveMfaCookie(bool totp, bool emailMfa)
    {
        // Protected with the same purpose the writer uses, so the page's own
        // reader is exercised rather than a test-only shape.
        var protector = _db.DataProtectionProvider.CreateProtector(EndpointHelpers.MfaProtectionPurpose);
        var state = new EndpointHelpers.MfaPending(1, totp, emailMfa, DateTime.UtcNow, "/");
        var payload = protector.Protect(JsonSerializer.Serialize(state));
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"{EndpointHelpers.MfaPendingCookieName}={payload}";
        _http.HttpContext = ctx;
    }

    /// <summary>
    /// Issues a real invite through <see cref="InviteService"/> rather than
    /// hand-hashing a token, so the page's lookup is matched against the writer
    /// the app actually uses.
    /// </summary>
    private string SeedInvite()
    {
        using var seed = _db.NewContext();
        var inviter = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = "admin@cronus.example",
            DisplayName = "Admin",
            PasswordHash = "not-a-real-hash",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        seed.Users.Add(inviter);
        seed.SaveChanges();

        _db.OrgContext.CurrentUserId = inviter.Id;
        var invites = new InviteService(seed, _db.OrgContext, TimeProvider.System, NullLogger<InviteService>.Instance);
        var (token, _) = invites.CreateAsync("new.consultant@cronus.example", UserRole.User, null)
            .GetAwaiter().GetResult();
        return token;
    }
}
