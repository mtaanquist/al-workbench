using ALDevToolbox.Services.Workers;
using OpenIddict.Abstractions;

namespace ALDevToolbox.Services.OAuth;

/// <summary>
/// Hosted service that removes dead rows from <c>oauth_tokens</c> and
/// <c>oauth_authorizations</c>. Every hourly refresh of a connected AI
/// assistant writes a new token and leaves the redeemed one behind, and
/// nothing else ever deletes them, so without this the tables only grow
/// (#1259).
///
/// <para>
/// The deleting is OpenIddict's own prune: it removes tokens that are
/// expired, revoked or redeemed, or whose authorization is no longer valid,
/// and then authorizations that are no longer valid and have no tokens left.
/// Tokens go first because an authorization with a token attached is kept.
/// Only rows created more than <see cref="RetentionWindow"/> ago are touched,
/// which keeps recently redeemed refresh tokens around for OpenIddict's reuse
/// detection and leaves something to look at after an incident.
/// </para>
///
/// <para>
/// Same shape as <see cref="Account.LoginAttemptPruneScheduler"/>: polls every
/// minute and runs once <see cref="RunInterval"/> has passed since the last
/// run. Opt out with <c>DISABLE_OAUTH_TOKEN_PRUNE_SCHEDULER=1</c>.
/// </para>
/// </summary>
public sealed class OAuthTokenPruneScheduler : PolledScheduler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(6);

    /// <summary>How old a dead token or authorization must be before the sweep removes it.</summary>
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(14);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<OAuthTokenPruneScheduler> _logger;
    private DateTimeOffset? _lastRunUtc;

    public OAuthTokenPruneScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<OAuthTokenPruneScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
        : base(logger, heartbeats, nameof(OAuthTokenPruneScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromMinutes(10),
            maxIdleSilence: TimeSpan.FromMinutes(5),
            disableEnvVar: "DISABLE_OAUTH_TOKEN_PRUNE_SCHEDULER")
    {
        _services = services;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task TickAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_lastRunUtc is { } last && now - last < RunInterval) return;

        await using var scope = _services.CreateAsyncScope();
        var (tokens, authorizations) = await PruneAsync(
            scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>(),
            scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>(),
            now - RetentionWindow, ct);

        _lastRunUtc = now;
        if (tokens > 0 || authorizations > 0)
        {
            _logger.LogInformation(
                "Pruned {Tokens} OAuth token(s) and {Authorizations} authorization(s) created before {Cutoff:o}.",
                tokens, authorizations, now - RetentionWindow);
        }
    }

    /// <summary>
    /// One prune pass: dead tokens first, then the authorizations they were
    /// holding on to. Rows created at or after <paramref name="threshold"/>
    /// are kept whatever their state.
    /// </summary>
    internal static async Task<(long Tokens, long Authorizations)> PruneAsync(
        IOpenIddictTokenManager tokens,
        IOpenIddictAuthorizationManager authorizations,
        DateTimeOffset threshold,
        CancellationToken ct)
    {
        var prunedTokens = await tokens.PruneAsync(threshold, ct);
        var prunedAuthorizations = await authorizations.PruneAsync(threshold, ct);
        return (prunedTokens, prunedAuthorizations);
    }
}
