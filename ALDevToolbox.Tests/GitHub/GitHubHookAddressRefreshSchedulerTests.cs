using System.Net;
using ALDevToolbox.Data;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// Loading GitHub's webhook address ranges (#1201): what a good answer puts in use,
/// and that a bad one never takes away a list that loaded.
/// </summary>
public sealed class GitHubHookAddressRefreshSchedulerTests : IDisposable
{
    private const string MetaJson = """
        { "verifiable_password_authentication": false,
          "hooks": ["192.30.252.0/22", "140.82.112.0/20", "2606:50c0::/32"],
          "web": ["140.82.112.0/20"] }
        """;

    private readonly TestDb _db = new();
    private readonly GitHubHookAddressAllowList _allowList = new(TimeProvider.System);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_refresh_puts_the_hooks_ranges_in_use_asked_without_a_credential()
    {
        var api = new FakeGitHubApi().On(HttpMethod.Get, "/meta", HttpStatusCode.OK, MetaJson);
        await using var provider = BuildProvider(api);

        (await NewScheduler(provider).RefreshAsync(CancellationToken.None)).Should().BeTrue();

        _allowList.LoadedAt.Should().NotBeNull();
        _allowList.Ranges.Select(r => r.ToString()).Should().Equal("192.30.252.0/22", "140.82.112.0/20", "2606:50c0::/32");
        _allowList.Check(IPAddress.Parse("2606:50c0::1")).Should().Be(GitHubHookAddressVerdict.Allowed);
        _allowList.Check(IPAddress.Parse("203.0.113.7")).Should().Be(GitHubHookAddressVerdict.Refused);
        api.Credentials.Should().ContainSingle().Which.Token.Should().BeNull("the meta route is public");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{\"message\":\"boom\"}")]
    [InlineData(HttpStatusCode.OK, "{\"hooks\":[]}")]
    [InlineData(HttpStatusCode.OK, "{\"web\":[\"140.82.112.0/20\"]}")]
    [InlineData(HttpStatusCode.OK, "{\"hooks\":[\"nonsense\"]}")]
    [InlineData(FakeGitHubApi.Unreachable, null)]
    public async Task A_failed_refresh_keeps_the_list_that_was_in_use(HttpStatusCode status, string? json)
    {
        _allowList.Replace(GitHubHookAddressAllowList.Parse(["185.199.108.0/22"]).Ranges);
        var loadedAt = _allowList.LoadedAt;
        var api = new FakeGitHubApi().On(HttpMethod.Get, "/meta", status, json);
        await using var provider = BuildProvider(api);

        (await NewScheduler(provider).RefreshAsync(CancellationToken.None)).Should().BeFalse();

        _allowList.LoadedAt.Should().Be(loadedAt);
        _allowList.Check(IPAddress.Parse("185.199.108.9")).Should().Be(GitHubHookAddressVerdict.Allowed);
    }

    [Fact]
    public async Task A_failed_first_load_leaves_the_webhook_open()
    {
        var api = new FakeGitHubApi().On(HttpMethod.Get, "/meta", HttpStatusCode.ServiceUnavailable, "{}");
        await using var provider = BuildProvider(api);

        (await NewScheduler(provider).RefreshAsync(CancellationToken.None)).Should().BeFalse();

        _allowList.Check(IPAddress.Parse("203.0.113.7")).Should().Be(GitHubHookAddressVerdict.NotLoaded);
    }

    [Fact]
    public void A_refresh_is_due_when_nothing_was_loaded_or_the_list_is_a_day_old()
    {
        var now = DateTimeOffset.UtcNow;
        GitHubHookAddressRefreshScheduler.IsDue(null, now).Should().BeTrue();
        GitHubHookAddressRefreshScheduler.IsDue(now.AddHours(-23), now).Should().BeFalse();
        GitHubHookAddressRefreshScheduler.IsDue(now - GitHubHookAddressRefreshScheduler.RefreshInterval, now).Should().BeTrue();
    }

    private GitHubHookAddressRefreshScheduler NewScheduler(IServiceProvider provider) =>
        new(provider, _allowList, TimeProvider.System,
            NullLogger<GitHubHookAddressRefreshScheduler>.Instance, new WorkerHeartbeatRegistry());

    private ServiceProvider BuildProvider(FakeGitHubApi api)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddScoped<IOrganizationContext, HttpOrganizationContext>();
        services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(_db.ConnectionString));
        services.AddScoped(sp => _db.NewGitHubAppClient(sp.GetRequiredService<AppDbContext>(), api));
        return services.BuildServiceProvider();
    }
}
