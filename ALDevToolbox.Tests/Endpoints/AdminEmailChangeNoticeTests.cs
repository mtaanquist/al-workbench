using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// When an admin changes someone's email, the person hears about it at the
/// address they have now: once when the change is asked for (with a link to
/// stop it) and once when it goes through. A change whose notice can't be sent
/// is not made at all, and the admin is never handed a confirmation link.
/// </summary>
// Sets PUBLIC_BASE_URL, which startup reads off the environment.
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class AdminEmailChangeNoticeTests : IDisposable
{
    private const string AdminEmail = "sam.berg@cronus.example";
    private const string AdminPassword = "correct horse battery staple";
    private const string MemberEmail = "alex.hansen@cronus.example";
    private const string NewEmail = "alex@elsewhere.example";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Requesting_a_change_tells_the_current_address_who_asked_and_how_to_stop_it()
    {
        var memberId = await SeedAsync();
        var email = new FakeEmail();
        using var factory = NewFactory(email);
        using var client = await SignedInAdminAsync(factory);

        using var response = await PostChangeAsync(client, memberId);

        response.Headers.Location!.OriginalString.Should().Contain("ok=email-pending");
        email.Sent.Should().HaveCount(2);
        var confirm = email.Sent.Single(m => m.Purpose == EmailPurpose.EmailChangeConfirmation);
        confirm.To.Should().Be(NewEmail);
        var notice = email.Sent.Single(m => m.Purpose == EmailPurpose.EmailChangeNotice);
        notice.To.Should().Be(MemberEmail);
        notice.HtmlBody.Should().Contain("Sam Berg").And.Contain(AdminEmail).And.Contain(NewEmail);
        notice.HtmlBody.Should().Contain("/account/email-change/cancel?token=");
        notice.HtmlBody.Should().Contain("will not take effect before");
        confirm.HtmlBody.Should().Contain("be confirmed before");
        notice.HtmlBody.Should().NotContain("/account/email-change/confirm",
            "the current address gets the way to stop the change, not the way to make it");
        response.Headers.TryGetValues("Set-Cookie", out var cookies);
        (cookies ?? []).Should().NotContain(c => c.StartsWith("alwb_invite_link=", StringComparison.Ordinal) && !c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase),
            "the confirmation link is never handed to the admin");
    }

    [Fact]
    public async Task Without_email_the_change_is_refused_and_nothing_is_pending()
    {
        var memberId = await SeedAsync();
        var email = new FakeEmail { Configured = false };
        using var factory = NewFactory(email);
        using var client = await SignedInAdminAsync(factory);

        using var response = await PostChangeAsync(client, memberId);

        Uri.UnescapeDataString(response.Headers.Location!.OriginalString)
            .Should().Contain("err=Email isn't set up on this site");
        email.Sent.Should().BeEmpty();
        var member = await ReadMemberAsync(memberId);
        member.PendingEmail.Should().BeNull();
        await using var read = _db.NewContext();
        (await read.PasswordResetTokens.IgnoreQueryFilters().AnyAsync(t => t.UserId == memberId)).Should().BeFalse();
    }

    [Fact]
    public async Task When_the_notice_cannot_be_sent_the_change_is_withdrawn()
    {
        var memberId = await SeedAsync();
        var email = new FakeEmail { FailFor = EmailPurpose.EmailChangeNotice };
        using var factory = NewFactory(email);
        using var client = await SignedInAdminAsync(factory);

        using var response = await PostChangeAsync(client, memberId);

        Uri.UnescapeDataString(response.Headers.Location!.OriginalString)
            .Should().Contain("err=The email change was not made");
        var member = await ReadMemberAsync(memberId);
        member.PendingEmail.Should().BeNull();
        member.Email.Should().Be(MemberEmail);
        await using var read = _db.NewContext();
        (await read.PasswordResetTokens.IgnoreQueryFilters().Where(t => t.UserId == memberId).ToListAsync())
            .Should().NotBeEmpty().And.OnlyContain(t => t.ConsumedAt != null,
                "the confirmation link already queued for the new address must not work");
    }

    [Fact]
    public async Task Confirming_the_change_tells_the_old_address()
    {
        var memberId = await SeedAsync();
        var email = new FakeEmail();
        using var factory = NewFactory(email);
        using var admin = await SignedInAdminAsync(factory);
        using (await PostChangeAsync(admin, memberId)) { }
        var confirm = email.Sent.Single(m => m.Purpose == EmailPurpose.EmailChangeConfirmation);
        var token = ExtractToken(confirm.HtmlBody, "/account/email-change/confirm?token=");
        email.Sent.Clear();
        using var anonymous = factory.CreateClient();

        // The link opens a page with a button; opening it alone changes nothing,
        // so a mail scanner that follows links can't make the change (#1544).
        using var page = await anonymous.GetAsync($"/account/email-change/confirm?token={Uri.EscapeDataString(token)}");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var antiforgery = ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        (await ReadMemberAsync(memberId)).PendingEmail.Should().Be(NewEmail, "opening the link alone changes nothing");

        // Straight away, the button answers with when it will work and changes nothing.
        using (var early = await PostConfirmAsync(anonymous, token, antiforgery))
        {
            early.Headers.Location!.OriginalString.Should().StartWith("/login?err=email-change-not-yet&at=");
            using var notYet = await anonymous.GetAsync(early.Headers.Location!.OriginalString);
            (await notYet.Content.ReadAsStringAsync()).Should().Contain("be confirmed yet");
        }
        (await ReadMemberAsync(memberId)).Email.Should().Be(MemberEmail);
        email.Sent.Should().BeEmpty();

        await BackdateEmailChangeAsync(memberId);
        using var response = await PostConfirmAsync(anonymous, token, antiforgery);

        response.Headers.Location!.OriginalString.Should().Be("/login?ok=email-change-confirmed");
        var notice = email.Sent.Should().ContainSingle().Subject;
        notice.To.Should().Be(MemberEmail);
        notice.Purpose.Should().Be(EmailPurpose.EmailChangeNotice);
        notice.HtmlBody.Should().Contain(NewEmail);
        (await ReadMemberAsync(memberId)).Email.Should().Be(NewEmail);
    }

    [Fact]
    public async Task The_cancel_link_stops_the_change_from_a_page_with_a_button()
    {
        var memberId = await SeedAsync();
        var email = new FakeEmail();
        using var factory = NewFactory(email);
        using var admin = await SignedInAdminAsync(factory);
        using (await PostChangeAsync(admin, memberId)) { }
        var notice = email.Sent.Single(m => m.Purpose == EmailPurpose.EmailChangeNotice);
        var token = ExtractToken(notice.HtmlBody, "/account/email-change/cancel?token=");

        using var anonymous = factory.CreateClient();
        using var page = await anonymous.GetAsync($"/account/email-change/cancel?token={Uri.EscapeDataString(token)}");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadMemberAsync(memberId)).PendingEmail.Should().Be(NewEmail, "opening the link alone stops nothing");

        var antiforgery = ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        using var stop = await PostCancelAsync(anonymous, token, antiforgery);
        stop.Headers.Location!.OriginalString.Should().Be("/login?ok=email-change-cancelled");
        (await ReadMemberAsync(memberId)).PendingEmail.Should().BeNull();

        using var again = await PostCancelAsync(anonymous, token, antiforgery);
        again.Headers.Location!.OriginalString.Should().Be("/login?err=email-change-cancel-invalid");
    }

    [Fact]
    public async Task A_confirmation_link_mailed_before_the_page_existed_only_forwards_to_it()
    {
        var memberId = await SeedAsync();
        var email = new FakeEmail();
        using var factory = NewFactory(email);
        using var admin = await SignedInAdminAsync(factory);
        using (await PostChangeAsync(admin, memberId)) { }
        var token = ExtractToken(
            email.Sent.Single(m => m.Purpose == EmailPurpose.EmailChangeConfirmation).HtmlBody,
            "/account/email-change/confirm?token=");
        await BackdateEmailChangeAsync(memberId);

        using var anonymous = factory.CreateClient();
        using var response = await anonymous.GetAsync($"/auth/account/email-change/confirm?token={Uri.EscapeDataString(token)}");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be($"/account/email-change/confirm?token={Uri.EscapeDataString(token)}");
        var member = await ReadMemberAsync(memberId);
        member.Email.Should().Be(MemberEmail, "a GET must not spend the token, even once the wait is over");
        member.PendingEmail.Should().Be(NewEmail);
    }

    private static Task<HttpResponseMessage> PostConfirmAsync(HttpClient client, string token, string antiforgery) =>
        client.PostAsync("/auth/account/email-change/confirm", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Token", token),
            new KeyValuePair<string, string>("__RequestVerificationToken", antiforgery),
        }));

    /// <summary>Moves the change's tokens back past the waiting period, as if a day had gone by.</summary>
    private async Task BackdateEmailChangeAsync(int memberId)
    {
        await using var db = _db.NewContext();
        var by = ALDevToolbox.Services.Account.UserAdministrationService.EmailChangeConfirmDelay + TimeSpan.FromMinutes(1);
        await db.PasswordResetTokens.IgnoreQueryFilters().Where(t => t.UserId == memberId)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.CreatedAt, t => t.CreatedAt - by));
    }

    private EndpointFactory NewFactory(FakeEmail email) =>
        new(_db, services => services.AddSingleton<IEmailService>(email),
            new Dictionary<string, string?> { ["PUBLIC_BASE_URL"] = "https://workbench.cronus.example" });

    private static async Task<HttpResponseMessage> PostChangeAsync(HttpClient client, int memberId)
    {
        using var page = await client.GetAsync("/admin/administration/users");
        var token = ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        var response = await client.PostAsync($"/admin/users/{memberId}/email", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("NewEmail", NewEmail),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        }));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return response;
    }

    private static Task<HttpResponseMessage> PostCancelAsync(HttpClient client, string token, string antiforgery) =>
        client.PostAsync("/auth/account/email-change/cancel", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Token", token),
            new KeyValuePair<string, string>("__RequestVerificationToken", antiforgery),
        }));

    private async Task<int> SeedAsync()
    {
        await using var seed = _db.NewContext();
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        seed.Users.Add(new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = AdminEmail,
            DisplayName = "Sam Berg",
            PasswordHash = new AuthService(seed, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(AdminPassword),
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = created,
        });
        var member = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = MemberEmail,
            DisplayName = "Alex Hansen",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = created,
        };
        seed.Users.Add(member);
        await seed.SaveChangesAsync();
        return member.Id;
    }

    private async Task<User> ReadMemberAsync(int id)
    {
        await using var read = _db.NewContext();
        return await read.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == id);
    }

    private static async Task<HttpClient> SignedInAdminAsync(EndpointFactory factory)
    {
        var client = factory.CreateClient();
        using var form = await client.GetAsync("/login");
        var token = ExtractAntiforgeryToken(await form.Content.ReadAsStringAsync());
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Email", AdminEmail),
            new KeyValuePair<string, string>("Password", AdminPassword),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        }));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    private static string ExtractToken(string html, string marker)
    {
        var match = Regex.Match(html, Regex.Escape(marker) + "([^\"&<\\s]+)");
        match.Success.Should().BeTrue($"the email must carry a {marker} link");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        match.Success.Should().BeTrue("the form must carry an antiforgery token");
        return match.Groups[1].Value;
    }

    private sealed class FakeEmail : IEmailService
    {
        public bool Configured { get; init; } = true;
        public EmailPurpose? FailFor { get; init; }
        public List<(string To, string HtmlBody, EmailPurpose Purpose)> Sent { get; } = [];

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(Configured);

        public Task SendAsync(string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default)
        {
            if (purpose == FailFor) throw new InvalidOperationException("The outbox could not be written.");
            lock (Sent) Sent.Add((toEmail, content.HtmlBody, purpose));
            return Task.CompletedTask;
        }
    }
}
