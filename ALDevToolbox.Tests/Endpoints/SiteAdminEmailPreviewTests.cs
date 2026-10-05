using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The email preview tab and its "Send to me" button (issue #1030), through
/// the real auth and antiforgery stack.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class SiteAdminEmailPreviewTests : IDisposable
{
    private const string AdminEmail = "siteadmin@cronus.example";
    private const string AdminPassword = "correct horse battery staple";

    private readonly TestDb _db = new();
    private readonly CapturingEmailService _email = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_page_shows_the_chosen_email_in_a_sandboxed_frame_and_as_text()
    {
        using var factory = NewFactory();
        using var client = await SignedInSiteAdminAsync(factory);

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
        using var factory = NewFactory();
        using var client = await SignedInSiteAdminAsync(factory);
        var token = await TokenFromAsync(client, "/site-admin/email/previews?email=invite");

        using var response = await PostAsync(client, "/site-admin/email/previews/invite/send", token);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/site-admin/email/previews?email=invite&ok=sent");
        var sent = _email.Sent.Should().ContainSingle().Subject;
        sent.To.Should().Be(AdminEmail);
        sent.Subject.Should().StartWith("Preview: ");
        sent.Purpose.Should().Be(EmailPurpose.SiteAdminTest);
    }

    [Fact]
    public async Task Without_email_set_up_nothing_is_sent_and_the_page_says_why()
    {
        _email.Configured = false;
        using var factory = NewFactory();
        using var client = await SignedInSiteAdminAsync(factory);
        var token = await TokenFromAsync(client, "/site-admin/email/previews?email=invite");

        using var response = await PostAsync(client, "/site-admin/email/previews/invite/send", token);

        response.Headers.Location!.OriginalString.Should().StartWith("/site-admin/email/previews?email=invite&msg=");
        _email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_send_is_reported_on_the_page()
    {
        _email.Fail = true;
        using var factory = NewFactory();
        using var client = await SignedInSiteAdminAsync(factory);
        var token = await TokenFromAsync(client, "/site-admin/email/previews?email=invite");

        using var response = await PostAsync(client, "/site-admin/email/previews/invite/send", token);

        response.Headers.Location!.OriginalString.Should().StartWith("/site-admin/email/previews?email=invite&msg=");
    }

    [Fact]
    public async Task A_send_without_the_antiforgery_token_is_refused()
    {
        using var factory = NewFactory();
        using var client = await SignedInSiteAdminAsync(factory);

        using var response = await client.PostAsync("/site-admin/email/previews/invite/send",
            new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()));

        response.Headers.Location?.OriginalString.Should().NotContain("ok=sent");
        _email.Sent.Should().BeEmpty();
    }

    /// <summary>An organisation admin is not a site admin: neither the page nor the send is theirs.</summary>
    [Fact]
    public async Task An_organisation_admin_cannot_see_or_send_previews()
    {
        using var factory = NewFactory();
        using var client = await SignedInSiteAdminAsync(factory, isSiteAdmin: false);
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
        using var factory = NewFactory();
        using var client = await SignedInSiteAdminAsync(factory);
        var token = await TokenFromAsync(client, "/site-admin/email/previews");

        using var response = await PostAsync(client, "/site-admin/email/previews/no-such-email/send", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _email.Sent.Should().BeEmpty();
    }

    private EndpointFactory NewFactory() =>
        new(_db, services => services.AddSingleton<IEmailService>(_email));

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

    private async Task<HttpClient> SignedInSiteAdminAsync(EndpointFactory factory, bool isSiteAdmin = true)
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = AdminEmail,
                DisplayName = "Site Admin",
                PasswordHash = new AuthService(seed, NullLogger<AuthService>.Instance, TimeProvider.System)
                    .HashPassword(AdminPassword),
                Role = UserRole.Admin,
                IsSiteAdmin = isSiteAdmin,
                Status = UserStatus.Active,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var token = await TokenFromAsync(client, "/login");
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

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        match.Success.Should().BeTrue("the page must carry an antiforgery token");
        return match.Groups[1].Value;
    }

    private sealed class CapturingEmailService : IEmailService
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
