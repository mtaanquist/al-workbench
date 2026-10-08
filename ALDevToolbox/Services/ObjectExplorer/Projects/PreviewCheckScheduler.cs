using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Workers;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Hosted service that once a night starts the preview check of every pipeline that
/// has it turned on: a build against Microsoft's next minor and one against the next
/// major preview, so a breaking change shows up before that version reaches a
/// customer. Mirrors <see cref="Bc.EnvironmentRefreshScheduler"/>: poll on a short
/// interval, enumerate active orgs (the organisations table carries no tenant filter,
/// so that read needs no bypass), and do the per-org work inside that org's
/// <see cref="AmbientOrganizationScope"/>.
///
/// <para>
/// Each build starts as the person who turned the check on, the way
/// <see cref="Bc.UpgradeActionWorker"/> fires an action as its requester: the access
/// check, the clone credential and the name on the build are theirs. When that person
/// is gone or can no longer manage the solution, the check is paused with a reason on
/// the pipeline instead of failing a build every night. Which targets are due is
/// <see cref="PreviewCheckService.ListDueAsync"/>'s call. Opt out with
/// <c>DISABLE_PREVIEW_CHECK_SCHEDULER=1</c>. See
/// <c>.design/object-explorer-project-builds.md</c>, "The nightly preview check".
/// </para>
/// </summary>
public sealed class PreviewCheckScheduler : PolledScheduler
{
    /// <summary>The UTC hour the nightly check starts in, ahead of the European working day.</summary>
    internal const int SweepHourUtc = 1;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<PreviewCheckScheduler> _logger;

    private DateOnly? _lastSweptUtcDate;

    public PreviewCheckScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<PreviewCheckScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
        // A sweep reads Microsoft's preview index once per country and queues builds
        // (the builds run on the ProjectBuildWorkers), so ten minutes is ample.
        : base(logger, heartbeats, nameof(PreviewCheckScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromMinutes(10),
            maxIdleSilence: TimeSpan.FromMinutes(30),
            disableEnvVar: "DISABLE_PREVIEW_CHECK_SCHEDULER")
    {
        _services = services;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Whether a poll at <paramref name="nowUtc"/> should sweep: inside the sweep hour and not yet done today.</summary>
    internal static bool IsDue(DateTime nowUtc, DateOnly? lastSweptUtcDate) =>
        nowUtc.Hour == SweepHourUtc && lastSweptUtcDate != DateOnly.FromDateTime(nowUtc);

    protected override async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        if (!IsDue(nowUtc, _lastSweptUtcDate)) return;

        await SweepAsync(ct).ConfigureAwait(false);
        _lastSweptUtcDate = DateOnly.FromDateTime(nowUtc);
    }

    /// <summary>
    /// One pass over every active org. Internal so a test can drive it directly
    /// against a seeded database. Returns how many builds were queued.
    /// </summary>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        var nowUtc = _clock.GetUtcNow().UtcDateTime;

        var orgs = await SweptOrganizations.ListAsync(_services, ct).ConfigureAwait(false);

        var queued = 0;
        foreach (var (orgId, isSystem) in orgs)
        {
            try
            {
                queued += await SweepOrganizationAsync(orgId, isSystem, nowUtc, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "PreviewCheckScheduler sweep failed for org {OrgId}.", orgId);
            }
        }

        if (queued > 0)
        {
            _logger.LogInformation("PreviewCheckScheduler queued {Count} preview build(s).", queued);
        }
        return queued;
    }

    private async Task<int> SweepOrganizationAsync(int orgId, bool isSystem, DateTime nowUtc, CancellationToken ct)
    {
        using var ambient = AmbientOrganizationScope.Enter(
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
        await using var scope = _services.CreateAsyncScope();
        var checks = scope.ServiceProvider.GetRequiredService<PreviewCheckService>();

        var queued = 0;
        foreach (var pipeline in (await checks.ListDueAsync(nowUtc, ct).ConfigureAwait(false)).GroupBy(d => d.PipelineId))
        {
            var blocked = pipeline.Select(d => d.Blocked).FirstOrDefault(b => b is not null);
            foreach (var due in pipeline.Where(d => d.Blocked is null && d.BcTarget is not null))
            {
                var (started, refused) = await StartAsync(orgId, isSystem, due, ct).ConfigureAwait(false);
                if (refused is not null)
                {
                    blocked = refused;
                    break;
                }
                if (started) queued++;
            }
            await checks.SetBlockedAsync(pipeline.Key, blocked, ct).ConfigureAwait(false);
        }
        return queued;
    }

    /// <summary>
    /// Starts one preview build as the person who turned the check on. Returns whether
    /// it started, and why the check is paused when the refusal is one the pipeline
    /// should show.
    /// </summary>
    private async Task<(bool Started, string? Blocked)> StartAsync(int orgId, bool isSystem, PreviewCheckDue due, CancellationToken ct)
    {
        var outcome = await AutomatedBuilds.StartAsAsync(
            _services, orgId, isSystem, due.UserId,
            importer => importer.StartPreviewCheckAsync(due.PipelineId, due.BcTarget!, ct),
            "the check could not start.").ConfigureAwait(false);
        if (outcome.Error is { } error)
        {
            // One pipeline's trouble is not the org's: log it and carry on with the
            // rest. Tomorrow night tries again.
            _logger.LogError(error, "PreviewCheckScheduler could not start a {BcTarget} check for pipeline {PipelineId}.",
                due.BcTarget, due.PipelineId);
            return (false, null);
        }
        if (outcome.Refusal is { } reason)
        {
            _logger.LogInformation("Paused the preview check of pipeline {PipelineId} as user {UserId}: {Reason}",
                due.PipelineId, due.UserId, reason);
        }
        return (outcome.Started, outcome.Refusal);
    }
}
