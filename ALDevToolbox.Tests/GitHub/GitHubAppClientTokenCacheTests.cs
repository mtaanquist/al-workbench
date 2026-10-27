using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The installation-token cache in <see cref="GitHubAppClient.GetInstallationTokenAsync"/>.
/// Each organisation connects its own installation, so the cache key is what keeps
/// one organisation's GitHub writes from going out under another's token; and a
/// token is reused only until five minutes before GitHub says it expires.
/// </summary>
public sealed class GitHubAppClientTokenCacheTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly Clock _clock = new(Start);
    private readonly MemoryCache _cache;

    public GitHubAppClientTokenCacheTests()
    {
        _cache = new MemoryCache(Options.Create(new MemoryCacheOptions { Clock = _clock }));
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Each_installation_gets_its_own_token_and_a_second_ask_reuses_it()
    {
        await ConfigureDeploymentAsync();
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, "/app/installations/1/access_tokens", HttpStatusCode.Created, TokenJson("ghs_one", Start.AddHours(1)))
            .On(HttpMethod.Post, "/app/installations/2/access_tokens", HttpStatusCode.Created, TokenJson("ghs_two", Start.AddHours(1)));

        (await TokenAsync(api, 1)).Should().Be("ghs_one");
        (await TokenAsync(api, 2)).Should().Be("ghs_two", "a token is never handed to another installation");
        (await TokenAsync(api, 1)).Should().Be("ghs_one");
        (await TokenAsync(api, 2)).Should().Be("ghs_two");

        Mints(api).Should().Equal(1, 2);
    }

    [Fact]
    public async Task A_token_is_minted_again_once_it_is_within_five_minutes_of_expiry()
    {
        await ConfigureDeploymentAsync();
        var api = new FakeGitHubApi().OnSequence(HttpMethod.Post, "/app/installations/1/access_tokens",
            (HttpStatusCode.Created, TokenJson("ghs_first", Start.AddHours(1))),
            (HttpStatusCode.Created, TokenJson("ghs_second", Start.AddHours(2))));

        (await TokenAsync(api, 1)).Should().Be("ghs_first");
        _clock.Advance(TimeSpan.FromMinutes(54));
        (await TokenAsync(api, 1)).Should().Be("ghs_first", "six minutes are left, more than the margin");
        _clock.Advance(TimeSpan.FromMinutes(2));
        (await TokenAsync(api, 1)).Should().Be("ghs_second");

        Mints(api).Should().Equal(1, 1);
    }

    [Fact]
    public async Task A_token_that_expires_inside_the_margin_is_not_cached()
    {
        await ConfigureDeploymentAsync();
        var api = new FakeGitHubApi().OnSequence(HttpMethod.Post, "/app/installations/1/access_tokens",
            (HttpStatusCode.Created, TokenJson("ghs_short", Start.AddMinutes(3))),
            (HttpStatusCode.Created, TokenJson("ghs_fresh", Start.AddHours(1))));

        (await TokenAsync(api, 1)).Should().Be("ghs_short");
        (await TokenAsync(api, 1)).Should().Be("ghs_fresh");

        Mints(api).Should().Equal(1, 1);
    }

    private static string TokenJson(string token, DateTimeOffset expiresAt) =>
        $"{{\"token\":\"{token}\",\"expires_at\":\"{expiresAt:O}\"}}";

    /// <summary>The installation id of every token mint, in order.</summary>
    private static IEnumerable<int> Mints(FakeGitHubApi api) => api.Calls
        .Where(c => c.EndsWith("/access_tokens", StringComparison.Ordinal))
        .Select(c => int.Parse(c.Split('/')[^2]));

    private async Task<string> TokenAsync(FakeGitHubApi api, long installationId)
    {
        await using var ctx = _db.NewContext();
        // A client per call, as each request scope gets one; the cache is the process's.
        var http = new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri(GitHubAppClient.ApiBaseUrl) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ALDevToolbox");
        var client = new GitHubAppClient(http, _db.NewSystemSettingsService(ctx), _cache, _clock, NullLogger<GitHubAppClient>.Instance);
        return await client.GetInstallationTokenAsync(installationId);
    }

    private async Task ConfigureDeploymentAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
    }

    /// <summary>One clock for both the client and the cache, so expiry is judged on the same time.</summary>
    private sealed class Clock(DateTimeOffset start) : TimeProvider, ISystemClock
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public DateTimeOffset UtcNow => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
