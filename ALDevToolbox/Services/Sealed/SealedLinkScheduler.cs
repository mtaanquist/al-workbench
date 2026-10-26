using ALDevToolbox.Data;
using ALDevToolbox.Services.Workers;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Sealed;

/// <summary>
/// Hosted service that runs <see cref="SealedLinkSweepService"/> for every organisation
/// once a minute, each inside its own <see cref="AmbientOrganizationScope"/>: expired
/// links lose their ciphertext within a minute of expiring, and a sender hears about an
/// opened link about as quickly. Modelled on <c>NotificationDigestScheduler</c>. Opt out
/// with <c>DISABLE_SEALED_LINK_SCHEDULER=1</c>; links still refuse to open once expired,
/// because the reveal checks the time itself. See <c>.design/sealed.md</c>.
/// </summary>
public sealed class SealedLinkScheduler : PolledScheduler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SealedLinkScheduler> _logger;

    public SealedLinkScheduler(
        IServiceProvider services,
        ILogger<SealedLinkScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
        : base(logger, heartbeats, nameof(SealedLinkScheduler),
            pollInterval: TimeSpan.FromMinutes(1),
            maxActiveDuration: TimeSpan.FromMinutes(5),
            maxIdleSilence: TimeSpan.FromMinutes(5),
            disableEnvVar: "DISABLE_SEALED_LINK_SCHEDULER")
    {
        _services = services;
        _logger = logger;
    }

    protected override Task TickAsync(CancellationToken ct) => SweepAsync(ct);

    /// <summary>One pass over every organisation with Sealed links. Internal so a test can drive it.</summary>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        List<(int Id, bool IsSystem)> orgs;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Which orgs to sweep; the organisations table carries no tenant filter.
            var rows = await db.Organizations.AsNoTracking()
                .Where(o => !o.IsPending)
                .Select(o => new { o.Id, o.IsSystem })
                .ToListAsync(ct).ConfigureAwait(false);
            orgs = rows.Select(o => (o.Id, o.IsSystem)).ToList();
        }

        var sent = 0;
        foreach (var (orgId, isSystem) in orgs)
        {
            try
            {
                using var ambient = AmbientOrganizationScope.Enter(
                    AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
                await using var scope = _services.CreateAsyncScope();
                sent += await scope.ServiceProvider.GetRequiredService<SealedLinkSweepService>()
                    .SweepAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "SealedLinkScheduler sweep failed for org {OrgId}.", orgId);
            }
        }
        return sent;
    }
}
