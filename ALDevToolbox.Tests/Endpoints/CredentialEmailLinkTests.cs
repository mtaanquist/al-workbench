using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The reset link in a password-reset email must point at this deployment,
/// not at whatever <c>Host</c> the requester chose. /forgot-password is
/// anonymous, so before #670 an attacker could mint their own antiforgery
/// pair, POST the victim's address with <c>Host: attacker.example</c>, and
/// the victim would receive a genuine email whose single-use token pointed at
/// the attacker. With PUBLIC_BASE_URL set the forged host must be ignored.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class CredentialEmailLinkTests : IDisposable
{
    private const string PublicBaseUrl = "https://workbench.cronus.example";
    private const string VictimEmail = "victim@cronus.example";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Reset_link_uses_the_configured_public_base_url_not_the_request_host()
    {
        await SeedVictimAsync();
        var email = new CapturingEmailService();
        using var factory = new EndpointFactory(
            _db,
            services => services.AddSingleton<IEmailService>(email),
            new Dictionary<string, string?> { ["PUBLIC_BASE_URL"] = PublicBaseUrl });

        await PostForgotPasswordAsync(factory, forgedHost: "attacker.example");

        var body = email.Sent.Should().ContainSingle().Subject.HtmlBody;
        body.Should().Contain($"{PublicBaseUrl}/reset-password?token=");
        body.Should().NotContain("attacker.example");
    }

    /// <summary>
    /// The compatibility half of the fix: an existing deployment that has not
    /// set PUBLIC_BASE_URL keeps the request-derived link (and gets a startup
    /// warning instead). Pins that we didn't break those installs.
    /// </summary>
    [Fact]
    public async Task Reset_link_falls_back_to_the_request_host_when_unconfigured()
    {
        await SeedVictimAsync();
        var email = new CapturingEmailService();
        using var factory = new EndpointFactory(
            _db,
            services => services.AddSingleton<IEmailService>(email),
            new Dictionary<string, string?> { ["PUBLIC_BASE_URL"] = null });

        await PostForgotPasswordAsync(factory, forgedHost: "fallback.example");

        email.Sent.Should().ContainSingle().Subject.HtmlBody
            .Should().Contain("https://fallback.example/reset-password?token=");
    }

    private async Task SeedVictimAsync()
    {
        await using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = VictimEmail,
            DisplayName = "Victim",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await seed.SaveChangesAsync();
    }

    private static async Task PostForgotPasswordAsync(EndpointFactory factory, string forgedHost)
    {
        using var client = factory.CreateClient();

        using var form = await client.GetAsync("/forgot-password");
        form.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = ExtractAntiforgeryToken(await form.Content.ReadAsStringAsync());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/forgot-password")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("Email", VictimEmail),
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
            }),
        };
        // The attack: the requester picks the Host header themselves.
        request.Headers.Host = forgedHost;

        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/forgot-password?ok=1");
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        match.Success.Should().BeTrue("the forgot-password form must carry an antiforgery token");
        return match.Groups[1].Value;
    }

    private sealed class CapturingEmailService : IEmailService
    {
        public List<(string To, string Subject, string HtmlBody, EmailPurpose Purpose)> Sent { get; } = [];

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task SendAsync(
            string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default)
        {
            Sent.Add((toEmail, content.Subject, content.HtmlBody, purpose));
            return Task.CompletedTask;
        }
    }
}
