using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The <c>state</c> handshake on GitHub's account-link callback, through the real host
/// (#1275). A callback is only honoured when its state was minted by us for this
/// handshake, is inside its lifetime, names the person whose cookie is on the request,
/// and has not been used; and the one place it may send them next is the marker it was
/// sealed with. Without these, a forged or replayed callback could connect GitHub to the
/// wrong person or send them off-site.
///
/// <para>The states are minted here the way the app mints them - the host's own key ring
/// and cache - so each case changes exactly one thing about an otherwise valid state.
/// GitHub is never reached: an accepted state without a <c>code</c> ends in its own
/// "not connected" answer, and the one case that needs GitHub to answer gets the fake.</para>
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class GitHubAppEndpointsTests : IDisposable
{
    private const string AlexEmail = "alex@cronus.example";
    private const string Password = "correct horse battery staple";

    private const string Refused = "Connecting your GitHub account did not finish in time";
    private const string AcceptedWithoutCode = "GitHub was not connected.";

    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public GitHubAppEndpointsTests()
    {
        _factory = new EndpointFactory(_db, services =>
            services.AddHttpClient<GitHubAppClient>().ConfigurePrimaryHttpMessageHandler(() => new FakeGitHubApi()
                .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
                .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson())));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task A_fresh_state_is_accepted_once()
    {
        var (client, userId) = await SignInAsync();
        var state = MintState(GitHubAppEndpoints.LinkStateProtectionPurpose, userId);

        (await CallbackAsync(client, state)).Should().Contain(Uri.EscapeDataString(AcceptedWithoutCode));
        (await CallbackAsync(client, state)).Should().Contain(Uri.EscapeDataString(Refused), "a state is single use");
    }

    [Fact]
    public async Task A_state_minted_for_the_install_handshake_is_refused()
    {
        var (client, userId) = await SignInAsync();
        var state = MintState(GitHubAppEndpoints.StateProtectionPurpose, userId);

        (await CallbackAsync(client, state)).Should().Contain(Uri.EscapeDataString(Refused));
    }

    [Fact]
    public async Task An_expired_state_is_refused()
    {
        var (client, userId) = await SignInAsync();
        var state = MintState(GitHubAppEndpoints.LinkStateProtectionPurpose, userId,
            issuedAt: DateTimeOffset.UtcNow.AddMinutes(-16));

        (await CallbackAsync(client, state)).Should().Contain(Uri.EscapeDataString(Refused));
    }

    [Fact]
    public async Task A_state_issued_to_someone_else_is_refused()
    {
        var (client, userId) = await SignInAsync();
        var colleagueId = await SeedColleagueAsync();
        var state = MintState(GitHubAppEndpoints.LinkStateProtectionPurpose, colleagueId);

        (await CallbackAsync(client, state)).Should().Contain(Uri.EscapeDataString(Refused),
            "a callback pasted into another person's session must not connect anything");
        userId.Should().NotBe(colleagueId);
    }

    [Fact]
    public async Task A_state_that_is_not_ours_is_refused()
    {
        var (client, _) = await SignInAsync();

        (await CallbackAsync(client, "not-a-state")).Should().Contain(Uri.EscapeDataString(Refused));
    }

    [Fact]
    public async Task The_admin_repositories_marker_is_the_only_place_a_link_returns_to()
    {
        await ConfigureGitHubAppAsync();
        var (client, userId) = await SignInAsync();

        var marked = MintState(GitHubAppEndpoints.LinkStateProtectionPurpose, userId, GitHubAppEndpoints.AdminRepositoriesReturn);
        (await CallbackAsync(client, marked, code: "the-code"))
            .Should().Be($"/account/repos?ok=github-linked&return={GitHubAppEndpoints.AdminRepositoriesReturn}");

        var offSite = MintState(GitHubAppEndpoints.LinkStateProtectionPurpose, userId, "https://evil.example/");
        (await CallbackAsync(client, offSite, code: "the-code"))
            .Should().Be("/account/repos?ok=github-linked", "any other return segment is dropped, not followed");
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>The account-link callback; answers the redirect's target.</summary>
    private static async Task<string> CallbackAsync(HttpClient client, string state, string? code = null)
    {
        var query = $"?state={Uri.EscapeDataString(state)}" + (code is null ? "" : $"&code={code}");
        using var response = await client.GetAsync(GitHubAppEndpoints.LinkCallbackPath + query);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return response.Headers.Location!.OriginalString;
    }

    /// <summary>
    /// A state as the link button mints it - <c>orgId|userId|nonce|issuedAt[|return]</c>
    /// under the purpose's protector, with its nonce in the host's cache.
    /// </summary>
    private string MintState(string purpose, int userId, string? returnTo = null, DateTimeOffset? issuedAt = null)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _factory.Services.GetRequiredService<IMemoryCache>()
            .Set($"github-state:{purpose}:{nonce}", true, TimeSpan.FromMinutes(15));
        var payload = $"{TestDb.DefaultOrgId}|{userId}|{nonce}|{(issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()}";
        if (returnTo is not null) payload += $"|{returnTo}";
        return _factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Protect(payload);
    }

    /// <summary>The GitHub App as a deployment sets it up, saved through the host so its key ring can read the secret back.</summary>
    private async Task ConfigureGitHubAppAsync()
    {
        using var rsa = RSA.Create(2048);
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SystemSettingsService>().SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
    }

    private async Task<int> SeedColleagueAsync()
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId, Email = "nils@cronus.example", DisplayName = "Nils",
            PasswordHash = "x", Role = UserRole.User, Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private async Task<(HttpClient Client, int UserId)> SignInAsync()
    {
        int userId;
        await using (var ctx = _db.NewContext())
        {
            var user = new User
            {
                OrganizationId = TestDb.DefaultOrgId, Email = AlexEmail, DisplayName = "Alex Hansen",
                PasswordHash = new AuthService(ctx, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(Password),
                Role = UserRole.User, Status = UserStatus.Active,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync();
            userId = user.Id;
        }

        var client = _factory.CreateClient();
        using var page = await client.GetAsync("/login");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = Regex.Match(await page.Content.ReadAsStringAsync(),
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
        [
            new("Email", AlexEmail), new("Password", Password), new("__RequestVerificationToken", token),
        ]));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return (client, userId);
    }
}
