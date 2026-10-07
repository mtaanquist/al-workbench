using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// Keeps <see cref="GitHubHookAddressAllowList"/> filled with the address ranges GitHub
/// sends webhooks from (#1201): one read of <c>GET /meta</c> shortly after start-up,
/// then one a day, and an early one when a delivery has been refused since the list
/// loaded - at most one an hour - so a range GitHub has just added is picked up while
/// the recovery sweep still has resends left for the deliveries it cost. A refresh that fails, or that comes back with no usable range,
/// leaves the list that was in use alone and is tried again on the next poll; until
/// the first one succeeds the webhook lets every sender through, as it did before.
///
/// <para>Reads no tenant data and needs no App registration: the route is public and
/// asked without a credential. Opt out with
/// <c>DISABLE_GITHUB_HOOK_ADDRESS_REFRESH=1</c>, which leaves the webhook unchecked.
/// See <c>.design/github-integration-phase2.md</c>.</para>
/// </summary>
public sealed class GitHubHookAddressRefreshScheduler : PolledScheduler
{
    // Short, because the early read after a refusal should land well inside the
    // recovery sweep's five resends (one every five minutes). A poll with nothing due
    // makes no call.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    /// <summary>How old the list in use may get before it is read again.</summary>
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    /// <summary>The least time between two early reads asked for by a refusal.</summary>
    internal static readonly TimeSpan EarlyRefreshInterval = TimeSpan.FromHours(1);

    private readonly IServiceProvider _services;
    private readonly GitHubHookAddressAllowList _allowList;
    private readonly TimeProvider _clock;
    private readonly ILogger<GitHubHookAddressRefreshScheduler> _logger;

    public GitHubHookAddressRefreshScheduler(
        IServiceProvider services,
        GitHubHookAddressAllowList allowList,
        TimeProvider clock,
        ILogger<GitHubHookAddressRefreshScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
        // One call with a thirty-second deadline: two minutes active is a hang.
        : base(logger, heartbeats, nameof(GitHubHookAddressRefreshScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromMinutes(2),
            maxIdleSilence: TimeSpan.FromMinutes(45),
            disableEnvVar: "DISABLE_GITHUB_HOOK_ADDRESS_REFRESH")
    {
        _services = services;
        _allowList = allowList;
        _clock = clock;
        _logger = logger;
    }

    // Only this scheduler's own poll touches it, one tick at a time.
    private DateTimeOffset? _lastEarlyRefresh;

    protected override Task TickAsync(CancellationToken ct) => TickOnceAsync(ct);

    /// <summary>One poll: reads the list when it is due, daily or early. Returns whether it was replaced.</summary>
    internal async Task<bool> TickOnceAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (IsDue(_allowList.LoadedAt, now)) return await RefreshAsync(ct);
        if (!IsEarlyRefreshDue(_allowList.LoadedAt, _allowList.LastRefusal?.At, _lastEarlyRefresh, now)) return false;

        // Counted whether or not the read succeeds, so a refusal flood against an
        // unreachable GitHub still asks at most once an hour.
        _lastEarlyRefresh = now;
        _logger.LogInformation(
            "A GitHub webhook delivery was refused since the address list loaded; reading the list again in case GitHub added a range.");
        return await RefreshAsync(ct);
    }

    /// <summary>Due when nothing was ever loaded, or the list in use is a day old.</summary>
    internal static bool IsDue(DateTimeOffset? loadedAt, DateTimeOffset now) =>
        loadedAt is not { } at || now - at >= RefreshInterval;

    /// <summary>
    /// Due when a delivery was refused after the list in use loaded, and no early read
    /// was made in the last <see cref="EarlyRefreshInterval"/>. Never before a first
    /// load: until then nothing is refused, and the daily rule already retries.
    /// </summary>
    internal static bool IsEarlyRefreshDue(
        DateTimeOffset? loadedAt, DateTimeOffset? lastRefusedAt, DateTimeOffset? lastEarlyRefresh, DateTimeOffset now) =>
        loadedAt is { } loaded
        && lastRefusedAt is { } refused && refused > loaded
        && (lastEarlyRefresh is not { } early || now - early >= EarlyRefreshInterval);

    /// <summary>
    /// Reads GitHub's hook ranges and puts them in use. Returns whether the list was
    /// replaced; on any failure the list in use is kept and the reason logged.
    /// </summary>
    internal async Task<bool> RefreshAsync(CancellationToken ct)
    {
        IReadOnlyList<string> entries;
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var client = scope.ServiceProvider.GetRequiredService<GitHubAppClient>();
            entries = await client.GetHookAddressRangesAsync(ct);
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException)
        {
            LogKept(ex.Message, ex);
            return false;
        }

        var (ranges, invalid) = GitHubHookAddressAllowList.Parse(entries);
        if (invalid.Count > 0)
        {
            _logger.LogWarning(
                "Ignored {Count} GitHub webhook address entries that are not address ranges: {Entries}.",
                invalid.Count, string.Join(", ", invalid));
        }
        if (ranges.Count == 0)
        {
            LogKept("GitHub listed no usable webhook address ranges.");
            return false;
        }

        _allowList.Replace(ranges);
        _logger.LogInformation(
            "Loaded {Count} GitHub webhook address ranges; deliveries from any other address are refused.",
            ranges.Count);
        return true;
    }

    private void LogKept(string reason, Exception? ex = null)
    {
        if (_allowList.LoadedAt is { } loadedAt)
        {
            _logger.LogWarning(ex,
                "Could not refresh GitHub's webhook address ranges ({Reason}); keeping the list loaded at {LoadedAt:O}.",
                reason, loadedAt);
        }
        else
        {
            _logger.LogWarning(ex,
                "Could not load GitHub's webhook address ranges ({Reason}); webhook deliveries are accepted from any address until a load succeeds.",
                reason);
        }
    }
}
