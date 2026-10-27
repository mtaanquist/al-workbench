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
/// wrong person or send them off-site. The install callback gets the same matrix, plus its
/// Admin gate and its <c>installation_id</c> handling (#1435).
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

    private const string InstallRefused = "That link has expired or was already used.";
    private const string InstallReplayed = "That setup link had already been used. Nothing changed";
    private const string InstallUnfinished = "GitHub has not finished installing the app yet.";

    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    /// <summary>
    /// One GitHub for the whole host. Besides the account link, it describes installation 42
    /// on <c>cronus-dk</c> and calls the signed-in person an owner there, which is everything
    /// the install callback asks before it connects.
    /// </summary>
    private readonly FakeGitHubApi _api = new FakeGitHubApi()
        .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
        .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson())
        .On(HttpMethod.Get, "app/installations/42", HttpStatusCode.OK,
            """{"id":42,"account":{"login":"cronus-dk","type":"Organization"},"permissions":{"metadata":"read","contents":"write"}}""")
        .On(HttpMethod.Get, "user/installations", HttpStatusCode.OK, FakeGitHubApi.InstallationsJson(42))
        .On(HttpMethod.Get, "user/memberships/orgs/cronus-dk", HttpStatusCode.OK, FakeGitHubApi.OrgMembershipJson());

    public GitHubAppEndpointsTests()
    {
        _factory = new EndpointFactory(_db, services =>
            services.AddHttpClient<GitHubAppClient>().ConfigurePrimaryHttpMessageHandler(() => _api));
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

    // ---- install callback --------------------------------------------------
    //
    // The same handshake on /github/setup, which binds a GitHub organisation's
    // installation to the acting workbench organisation. Every refusal is checked
    // against the settings row as well as the message: the point is that nothing
    // was connected.

    [Fact]
    public async Task An_owners_fresh_install_state_connects_the_installation_once()
    {
        await ConfigureGitHubAppAsync();
        var (client, userId) = await SignInAsync(UserRole.Admin);
        await LinkGitHubAccountAsync(userId);
        var state = MintState(GitHubAppEndpoints.StateProtectionPurpose, userId);

        (await SetupCallbackAsync(client, state, "42"))
            .Should().Be("/admin/administration/repositories?ok=github-connected");
        (await ConnectedInstallationAsync()).Should().Be(42);

        (await SetupCallbackAsync(client, state, "42")).Should().Contain(Uri.EscapeDataString(InstallReplayed),
            "a state is single use, and an organisation that is already connected is told so");
    }

    [Fact]
    public async Task A_state_minted_for_the_link_handshake_is_refused_by_the_install_callback()
    {
        await ConfigureGitHubAppAsync();
        var (client, userId) = await SignInAsync(UserRole.Admin);
        await LinkGitHubAccountAsync(userId);
        var state = MintState(GitHubAppEndpoints.LinkStateProtectionPurpose, userId);

        (await SetupCallbackAsync(client, state, "42")).Should().Contain(Uri.EscapeDataString(InstallRefused));
        (await ConnectedInstallationAsync()).Should().BeNull();
        _api.Calls.Should().NotContain(c => c.Contains("app/installations/"));
    }

    [Fact]
    public async Task An_install_state_issued_to_another_admin_is_refused()
    {
        await ConfigureGitHubAppAsync();
        var (client, userId) = await SignInAsync(UserRole.Admin);
        await LinkGitHubAccountAsync(userId);
        var colleagueId = await SeedColleagueAsync(UserRole.Admin);
        var state = MintState(GitHubAppEndpoints.StateProtectionPurpose, colleagueId);

        (await SetupCallbackAsync(client, state, "42")).Should().Contain(Uri.EscapeDataString(InstallRefused),
            "an install link pasted into another admin's session must not connect anything");
        (await ConnectedInstallationAsync()).Should().BeNull();
    }

    [Fact]
    public async Task An_expired_install_state_is_refused()
    {
        await ConfigureGitHubAppAsync();
        var (client, userId) = await SignInAsync(UserRole.Admin);
        await LinkGitHubAccountAsync(userId);
        var state = MintState(GitHubAppEndpoints.StateProtectionPurpose, userId,
            issuedAt: DateTimeOffset.UtcNow.AddMinutes(-16));

        (await SetupCallbackAsync(client, state, "42")).Should().Contain(Uri.EscapeDataString(InstallRefused));
        (await ConnectedInstallationAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-42")]
    [InlineData("forty-two")]
    public async Task An_install_callback_without_a_usable_installation_id_connects_nothing(string? installationId)
    {
        await ConfigureGitHubAppAsync();
        var (client, userId) = await SignInAsync(UserRole.Admin);
        await LinkGitHubAccountAsync(userId);
        var state = MintState(GitHubAppEndpoints.StateProtectionPurpose, userId);

        (await SetupCallbackAsync(client, state, installationId)).Should().Contain(Uri.EscapeDataString(InstallUnfinished));
        (await ConnectedInstallationAsync()).Should().BeNull();
        _api.Calls.Should().NotContain(c => c.Contains("app/installations/"),
            "GitHub is not asked about an id that was never given");
    }

    [Fact]
    public async Task A_member_who_is_not_an_admin_cannot_complete_an_install()
    {
        await ConfigureGitHubAppAsync();
        var (client, userId) = await SignInAsync(UserRole.User);
        await LinkGitHubAccountAsync(userId);
        var state = MintState(GitHubAppEndpoints.StateProtectionPurpose, userId);

        using var response = await client.GetAsync(
            $"{GitHubAppEndpoints.SetupCallbackPath}?state={Uri.EscapeDataString(state)}&installation_id=42");

        // The cookie handler's access-denied answer: a redirect to sign-in, never
        // the Repositories tab the callback itself redirects to.
        response.StatusCode.Should().Be(HttpStatusCode.Found, "the role gate answers before the callback runs");
        response.Headers.Location!.AbsolutePath.Should().Be("/login");
        (await ConnectedInstallationAsync()).Should().BeNull(
            "a GitHub owner who is only a User here must not be able to bind the organisation");
        _api.Calls.Should().NotContain(c => c.Contains("app/installations/"));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>The install callback; answers the redirect's target.</summary>
    private static async Task<string> SetupCallbackAsync(HttpClient client, string state, string? installationId)
    {
        var query = $"?state={Uri.EscapeDataString(state)}"
            + (installationId is null ? "" : $"&installation_id={Uri.EscapeDataString(installationId)}&setup_action=install");
        using var response = await client.GetAsync(GitHubAppEndpoints.SetupCallbackPath + query);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return response.Headers.Location!.OriginalString;
    }

    /// <summary>The installation the default organisation is connected to, if any.</summary>
    private async Task<long?> ConnectedInstallationAsync()
    {
        await using var ctx = _db.NewContext();
        return await ctx.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == TestDb.DefaultOrgId)
            .Select(s => s.GitHubInstallationId)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Gives a user a GitHub link with a non-expiring token, encrypted under the host's own
    /// key ring so the install gate can read it back.
    /// </summary>
    private async Task LinkGitHubAccountAsync(int userId)
    {
        await using var ctx = _db.NewContext();
        ctx.UserExternalLogins.Add(new UserExternalLogin
        {
            UserId = userId,
            Provider = GitHubAccessService.ProviderName,
            Issuer = GitHubAccessService.IssuerValue,
            Subject = "4711",
            DisplayIdentity = "cronus-dev",
            CreatedAt = DateTime.UtcNow,
            AccessTokenEncrypted = _factory.Services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(GitHubAccessService.AccessTokenProtectionPurpose)
                .Protect("ghu_owner"),
        });
        await ctx.SaveChangesAsync();
    }

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

    private async Task<int> SeedColleagueAsync(UserRole role = UserRole.User)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId, Email = "nils@cronus.example", DisplayName = "Nils",
            PasswordHash = "x", Role = role, Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private async Task<(HttpClient Client, int UserId)> SignInAsync(UserRole role = UserRole.User)
    {
        int userId;
        await using (var ctx = _db.NewContext())
        {
            var user = new User
            {
                OrganizationId = TestDb.DefaultOrgId, Email = AlexEmail, DisplayName = "Alex Hansen",
                PasswordHash = new AuthService(ctx, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(Password),
                Role = role, Status = UserStatus.Active,
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
