using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

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
///
/// <para>The cases share one host (#1536, #1582). The install callback connects an
/// organisation, so each test works in an organisation of its own, signs in users of
/// its own and gives them a GitHub link of its own; it gets a fresh fake GitHub too,
/// which the host's pooled HTTP handler forwards to, so the calls one test records
/// never reach another's assertions. The GitHub App itself is configured once for the
/// host: every test that needs it configures the same one, and the tests that run
/// without it never reach anything that reads it.</para>
/// </summary>
public sealed class GitHubAppEndpointsTests : IClassFixture<GitHubAppEndpointsTests.Host>
{
    private const string Password = "correct horse battery staple";

    private const string Refused = "Connecting your GitHub account did not finish in time";
    private const string AcceptedWithoutCode = "GitHub was not connected.";

    private const string InstallRefused = "That link has expired or was already used.";
    private const string InstallReplayed = "That setup link had already been used. Nothing changed";
    private const string InstallUnfinished = "GitHub has not finished installing the app yet.";

    private readonly Host _host;
    private readonly TestDb _db;
    private readonly EndpointFactory _factory;
    private readonly string _alexEmail = SharedEndpointHost.UniqueEmail("alex");

    /// <summary>This test's own organisation, so a connected installation stays with the test that connected it.</summary>
    private readonly int _orgId;

    /// <summary>
    /// This test's GitHub. Besides the account link, it describes installation 42
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

    public GitHubAppEndpointsTests(Host host)
    {
        _host = host;
        _db = host.Db;
        _factory = host.Factory;
        host.Api = _api;
        _orgId = host.SeedOrganization();
    }

    /// <summary>
    /// The shared host. GitHub's HTTP handler is pooled by the client factory and
    /// outlives a test, so it holds no fake of its own: it forwards each request to
    /// whichever fake the running test installed.
    /// </summary>
    public sealed class Host : SharedEndpointHost
    {
        private readonly Holder _holder;
        private readonly SemaphoreSlim _configureGate = new(1, 1);
        private bool _configured;

        public Host()
            : this(new Holder())
        {
        }

        private Host(Holder holder)
            : base(services => services.AddHttpClient<GitHubAppClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new Forwarder(holder)))
        {
            _holder = holder;
        }

        public FakeGitHubApi Api
        {
            get => _holder.Api;
            set => _holder.Api = value;
        }

        /// <summary>
        /// The GitHub App as a deployment sets it up, saved through the host so its key
        /// ring can read the secret back. It is the same app for every test, so the host
        /// saves it once.
        /// </summary>
        public async Task ConfigureGitHubAppAsync()
        {
            await _configureGate.WaitAsync();
            try
            {
                if (_configured) return;
                using var rsa = RSA.Create(2048);
                await using var scope = Factory.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SystemSettingsService>().SaveGitHubAppAsync(new GitHubAppInput(
                    AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
                    ClientSecret: "s3cr3t", ClearClientSecret: false,
                    PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
                _configured = true;
            }
            finally
            {
                _configureGate.Release();
            }
        }

        public override void Dispose()
        {
            _configureGate.Dispose();
            base.Dispose();
        }

        private sealed class Holder
        {
            public FakeGitHubApi Api { get; set; } = null!;
        }

        private sealed class Forwarder : HttpMessageHandler
        {
            private readonly Holder _holder;

            public Forwarder(Holder holder) => _holder = holder;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                using var invoker = new HttpMessageInvoker(_holder.Api, disposeHandler: false);
                return invoker.SendAsync(request, cancellationToken);
            }
        }
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
        await _host.ConfigureGitHubAppAsync();
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
        await _host.ConfigureGitHubAppAsync();
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
        await _host.ConfigureGitHubAppAsync();
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
        await _host.ConfigureGitHubAppAsync();
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
        await _host.ConfigureGitHubAppAsync();
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
        await _host.ConfigureGitHubAppAsync();
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
        await _host.ConfigureGitHubAppAsync();
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

    /// <summary>The installation this test's organisation is connected to, if any.</summary>
    private async Task<long?> ConnectedInstallationAsync()
    {
        await using var ctx = _db.NewContext();
        return await ctx.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == _orgId)
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
            // A subject of this test's own: a GitHub account links to one person only.
            Subject = Random.Shared.NextInt64(1_000_000, long.MaxValue).ToString(),
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
        var payload = $"{_orgId}|{userId}|{nonce}|{(issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()}";
        if (returnTo is not null) payload += $"|{returnTo}";
        return _factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Protect(payload);
    }

    private async Task<int> SeedColleagueAsync(UserRole role = UserRole.User)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = _orgId, Email = SharedEndpointHost.UniqueEmail("nils"), DisplayName = "Nils",
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
                OrganizationId = _orgId, Email = _alexEmail, DisplayName = "Alex Hansen",
                PasswordHash = _host.HashPassword(Password),
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
            new("Email", _alexEmail), new("Password", Password), new("__RequestVerificationToken", token),
        ]));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return (client, userId);
    }
}
