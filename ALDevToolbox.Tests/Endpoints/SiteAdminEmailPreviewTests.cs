using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The email preview tab and its "Send to me" button (issue #1030), through
/// the real auth and antiforgery stack.
///
/// <para>The cases share one host (#1536, #1582). Each signs in an admin of its own
/// and installs a fresh capturing mail service, which the host forwards to, so one
/// test's sent mail never reaches another's assertions.</para>
/// </summary>
public sealed class SiteAdminEmailPreviewTests : IClassFixture<SiteAdminEmailPreviewTests.Host>
{
    private const string AdminPassword = "correct horse battery staple";

    private readonly Host _host;
    private readonly TestDb _db;
    private readonly EndpointFactory _factory;
    private readonly string _adminEmail = SharedEndpointHost.UniqueEmail("siteadmin");
    private readonly CapturingEmailService _email = new();

    public SiteAdminEmailPreviewTests(Host host)
    {
        _host = host;
        _db = host.Db;
        _factory = host.Factory;
        host.Email = _email;
    }

    /// <summary>The shared host, sending mail through whichever capturing service the running test installed.</summary>
    public sealed class Host : SharedEndpointHost
    {
        private readonly Holder _holder;

        public Host()
            : this(new Holder())
        {
        }

        private Host(Holder holder)
            : base(services => services.AddSingleton<IEmailService>(new ForwardingEmailService(holder)))
        {
            _holder = holder;
        }

        internal CapturingEmailService Email
        {
            get => _holder.Email;
            set => _holder.Email = value;
        }

        private sealed class Holder
        {
            public CapturingEmailService Email { get; set; } = new();
        }

        private sealed class ForwardingEmailService : IEmailService
        {
            private readonly Holder _holder;

            public ForwardingEmailService(Holder holder) => _holder = holder;

            public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => _holder.Email.IsConfiguredAsync(ct);

            public Task SendAsync(string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default) =>
                _holder.Email.SendAsync(toEmail, content, purpose, ct);
        }
    }

    [Fact]
    public async Task The_page_shows_the_chosen_email_in_a_sandboxed_frame_and_as_text()
    {
        using var client = await SignedInSiteAdminAsync();

        using var page = await client.GetAsync("/site-admin/email/previews?email=password-reset");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();

        // An empty sandbox renders as a bare attribute: no scripts, no navigation.
        var frame = Regex.Match(html, "<iframe[^>]*>").Value;
        frame.Should().MatchRegex(@"\ssandbox(="")?[\s>]");
        // Sample links open in a new window, which the sandbox refuses, so a
        // click leaves the preview in place.
        frame.Should().Contain("&lt;base target=&quot;_blank&quot;&gt;");
        html.Should().Contain("Reset your AL Workbench password");
        // The text part, newlines encoded the way Razor writes them.
        html.Should().Contain("Reset password:&#xA;https://workbench.cronus.example/reset-password?token=preview");
    }

    [Fact]
    public async Task Send_to_me_mails_the_preview_to_the_signed_in_admin()
    {
        using var client = await SignedInSiteAdminAsync();
        var token = await TokenFromAsync(client, "/site-admin/email/previews?email=invite");

        using var response = await PostAsync(client, "/site-admin/email/previews/invite/send", token);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/site-admin/email/previews?email=invite&ok=sent");
        var sent = _email.Sent.Should().ContainSingle().Subject;
        sent.To.Should().Be(_adminEmail);
        sent.Subject.Should().StartWith("Preview: ");
        sent.Purpose.Should().Be(EmailPurpose.SiteAdminTest);
    }

    [Fact]
    public async Task Without_email_set_up_nothing_is_sent_and_the_page_says_why()
    {
        _email.Configured = false;
        using var client = await SignedInSiteAdminAsync();
        var token = await TokenFromAsync(client, "/site-admin/email/previews?email=invite");

        using var response = await PostAsync(client, "/site-admin/email/previews/invite/send", token);

        response.Headers.Location!.OriginalString.Should().StartWith("/site-admin/email/previews?email=invite&msg=");
        _email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_send_is_reported_on_the_page()
    {
        _email.Fail = true;
        using var client = await SignedInSiteAdminAsync();
        var token = await TokenFromAsync(client, "/site-admin/email/previews?email=invite");

        using var response = await PostAsync(client, "/site-admin/email/previews/invite/send", token);

        response.Headers.Location!.OriginalString.Should().StartWith("/site-admin/email/previews?email=invite&msg=");
    }

    [Fact]
    public async Task A_send_without_the_antiforgery_token_is_refused()
    {
        using var client = await SignedInSiteAdminAsync();

        using var response = await client.PostAsync("/site-admin/email/previews/invite/send",
            new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()));

        response.Headers.Location?.OriginalString.Should().NotContain("ok=sent");
        _email.Sent.Should().BeEmpty();
    }

    /// <summary>An organisation admin is not a site admin: neither the page nor the send is theirs.</summary>
    [Fact]
    public async Task An_organisation_admin_cannot_see_or_send_previews()
    {
        using var client = await SignedInSiteAdminAsync(isSiteAdmin: false);
        var token = await TokenFromAsync(client, "/account");

        using var page = await client.GetAsync("/site-admin/email/previews");
        using var send = await PostAsync(client, "/site-admin/email/previews/invite/send", token);

        page.StatusCode.Should().NotBe(HttpStatusCode.OK);
        send.Headers.Location?.OriginalString.Should().NotContain("ok=sent");
        _email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_email_is_not_found()
    {
        using var client = await SignedInSiteAdminAsync();
        var token = await TokenFromAsync(client, "/site-admin/email/previews");

        using var response = await PostAsync(client, "/site-admin/email/previews/no-such-email/send", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _email.Sent.Should().BeEmpty();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string token) =>
        await client.PostAsync(url, new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        }));

    private static async Task<string> TokenFromAsync(HttpClient client, string url)
    {
        using var page = await client.GetAsync(url);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        return ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
    }

    private async Task<HttpClient> SignedInSiteAdminAsync(bool isSiteAdmin = true)
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = _adminEmail,
                DisplayName = "Site Admin",
                PasswordHash = _host.HashPassword(AdminPassword),
                Role = UserRole.Admin,
                IsSiteAdmin = isSiteAdmin,
                Status = UserStatus.Active,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var token = await TokenFromAsync(client, "/login");
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Email", _adminEmail),
            new KeyValuePair<string, string>("Password", AdminPassword),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        }));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        match.Success.Should().BeTrue("the page must carry an antiforgery token");
        return match.Groups[1].Value;
    }

    internal sealed class CapturingEmailService : IEmailService
    {
        public List<(string To, string Subject, EmailPurpose Purpose)> Sent { get; } = [];
        public bool Configured { get; set; } = true;
        public bool Fail { get; set; }

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(Configured);

        public Task SendAsync(string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("The mail server said no.");
            Sent.Add((toEmail, content.Subject, purpose));
            return Task.CompletedTask;
        }
    }
}
