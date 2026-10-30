using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The restart half of <see cref="DeliveryScheduler"/> (#1114, #1179): a deployment left
/// in progress by the previous process is failed once per organisation, judged against
/// the time this process started, and an organisation whose check threw is tried again
/// on the next sweep rather than left with deployments stuck in progress.
/// </summary>
public sealed class DeliverySchedulerTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(Started);
    private int _failNextResolvesForDefaultOrg;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_first_sweep_fails_a_deployment_claimed_before_the_process_started_and_leaves_one_claimed_since()
    {
        var seed = await DeliveryHost.SeedAsync(_db, Started.UtcDateTime);
        var orphan = await AddInProgressAsync(seed, claimedAt: Started.UtcDateTime.AddMinutes(-3));
        var running = await AddInProgressAsync(seed, claimedAt: Started.UtcDateTime.AddSeconds(10));
        await using var provider = BuildProvider();
        var scheduler = NewScheduler(provider);
        // The worker is already draining when the first sweep comes round (#1114).
        _clock.Advance(TimeSpan.FromSeconds(30));

        await scheduler.SweepAsync(CancellationToken.None);

        (await DeliveryHost.StatusAsync(_db, orphan)).Should().Be(ProjectDeliveryStatus.Failed);
        (await DeliveryHost.StatusAsync(_db, running)).Should().Be(ProjectDeliveryStatus.Installing);
    }

    [Fact]
    public async Task An_organisation_whose_check_threw_is_checked_again_on_the_next_sweep_and_only_until_it_succeeds()
    {
        var seed = await DeliveryHost.SeedAsync(_db, Started.UtcDateTime);
        var orphan = await AddInProgressAsync(seed, claimedAt: Started.UtcDateTime.AddMinutes(-3));
        await using var provider = BuildProvider();
        var scheduler = NewScheduler(provider);

        _failNextResolvesForDefaultOrg = 1;
        await scheduler.SweepAsync(CancellationToken.None);
        (await DeliveryHost.StatusAsync(_db, orphan)).Should().Be(ProjectDeliveryStatus.Installing, "the check for this organisation threw");

        await scheduler.SweepAsync(CancellationToken.None);
        (await DeliveryHost.StatusAsync(_db, orphan)).Should().Be(ProjectDeliveryStatus.Failed, "the next sweep tries again");

        // Done once it got through: the check is a restart's, not every sweep's.
        var later = await AddInProgressAsync(seed, claimedAt: Started.UtcDateTime.AddMinutes(-2));
        await scheduler.SweepAsync(CancellationToken.None);
        (await DeliveryHost.StatusAsync(_db, later)).Should().Be(ProjectDeliveryStatus.Installing);
    }

    /// <summary>
    /// Each organisation's sweep runs under that organisation alone: it fails its own
    /// interrupted deployments, queues its own due ones under its own identity (which
    /// decides whose Business Central the worker installs into), and an organisation
    /// still waiting for approval is not swept at all.
    /// </summary>
    [Fact]
    public async Task A_sweep_handles_each_organisation_under_its_own_identity_and_skips_a_pending_one()
    {
        var pendingOrgId = await AddPendingOrganizationAsync();
        var before = Started.UtcDateTime.AddMinutes(-3);
        var due = Started.UtcDateTime.AddMinutes(-1);
        var orphans = new Dictionary<int, int>();
        var scheduled = new Dictionary<int, int>();
        foreach (var orgId in new[] { TestDb.SystemOrgId, TestDb.DefaultOrgId, pendingOrgId })
        {
            var seed = await DeliveryHost.SeedAsync(_db, Started.UtcDateTime, orgId);
            orphans[orgId] = await DeliveryHost.AddDeliveryAsync(_db, seed, ProjectDeliveryStatus.Installing, before, before);
            scheduled[orgId] = await DeliveryHost.AddDeliveryAsync(_db, seed, ProjectDeliveryStatus.Scheduled, due);
        }
        var queue = new DeliveryQueue();
        await using var provider = BuildProvider(queue);
        var scheduler = NewScheduler(provider);

        await scheduler.SweepAsync(CancellationToken.None);

        (await DeliveryHost.StatusAsync(_db, orphans[TestDb.SystemOrgId])).Should().Be(ProjectDeliveryStatus.Failed);
        (await DeliveryHost.StatusAsync(_db, orphans[TestDb.DefaultOrgId])).Should().Be(ProjectDeliveryStatus.Failed);
        (await DeliveryHost.StatusAsync(_db, orphans[pendingOrgId])).Should().Be(ProjectDeliveryStatus.Installing,
            "a pending organisation is not swept");
        var queued = Drain(queue).ToDictionary(j => j.DeliveryId, j => j.Identity);
        queued.Keys.Should().BeEquivalentTo([scheduled[TestDb.SystemOrgId], scheduled[TestDb.DefaultOrgId]]);
        queued[scheduled[TestDb.SystemOrgId]].OrganizationId.Should().Be(TestDb.SystemOrgId);
        queued[scheduled[TestDb.SystemOrgId]].IsSystemOrganization.Should().BeTrue("the sweep carries the org row's own flag");
        queued[scheduled[TestDb.DefaultOrgId]].OrganizationId.Should().Be(TestDb.DefaultOrgId);
        queued[scheduled[TestDb.DefaultOrgId]].IsSystemOrganization.Should().BeFalse();
    }

    /// <summary>
    /// While a restore runs, or a failed one holds the site closed (#1321), a poll does
    /// nothing: the rows it would fail or queue may be half loaded. The first poll after
    /// the site reopens does the work.
    /// </summary>
    [Fact]
    public async Task A_poll_during_maintenance_neither_fails_nor_queues_anything()
    {
        var seed = await DeliveryHost.SeedAsync(_db, Started.UtcDateTime);
        var orphan = await AddInProgressAsync(seed, claimedAt: Started.UtcDateTime.AddMinutes(-3));
        var due = await DeliveryHost.AddDeliveryAsync(_db, seed, ProjectDeliveryStatus.Scheduled, Started.UtcDateTime.AddMinutes(-1));
        var queue = new DeliveryQueue();
        await using var provider = BuildProvider(queue);
        var maintenance = new MaintenanceModeState();
        maintenance.HoldAfterFailedRestore("Restoring backup x failed.");
        var scheduler = new DeliveryScheduler(
            provider, _clock, NullLogger<DeliveryScheduler>.Instance, new WorkerHeartbeatRegistry(), maintenance);

        await scheduler.TickOnceAsync(CancellationToken.None);

        (await DeliveryHost.StatusAsync(_db, orphan)).Should().Be(ProjectDeliveryStatus.Installing);
        queue.Reader.Count.Should().Be(0);

        maintenance.Exit();
        await scheduler.TickOnceAsync(CancellationToken.None);

        (await DeliveryHost.StatusAsync(_db, orphan)).Should().Be(ProjectDeliveryStatus.Failed);
        Drain(queue).Select(j => j.DeliveryId).Should().Equal(due);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private DeliveryScheduler NewScheduler(IServiceProvider provider) =>
        new(provider, _clock, NullLogger<DeliveryScheduler>.Instance, new WorkerHeartbeatRegistry());

    // Resolving the delivery service is how a test makes one organisation's check throw.
    // Keyed to the organisation the sweep is acting as, not to the order it visits them in:
    // the sweep reads the organisations unordered, and the system org is one of them.
    private ServiceProvider BuildProvider(DeliveryQueue? queue = null) => DeliveryHost.Build(_db, _clock, () =>
        AmbientOrganizationScope.Current?.OrganizationId == TestDb.DefaultOrgId && _failNextResolvesForDefaultOrg-- > 0
            ? throw new InvalidOperationException("The database went away for a moment.")
            : new NoTokens(), queue);

    private static List<DeliveryJob> Drain(DeliveryQueue queue)
    {
        var jobs = new List<DeliveryJob>();
        while (queue.Reader.TryRead(out var job)) jobs.Add(job);
        return jobs;
    }

    private async Task<int> AddPendingOrganizationAsync()
    {
        await using var ctx = _db.NewContext();
        var org = new Organization
        {
            Name = "CRONUS Pending", Slug = "cronus-pending", IsPending = true, CreatedAt = Started.UtcDateTime,
        };
        ctx.Organizations.Add(org);
        await ctx.SaveChangesAsync();
        return org.Id;
    }

    private Task<int> AddInProgressAsync(DeliveryHost.Seed seed, DateTime claimedAt) =>
        DeliveryHost.AddDeliveryAsync(_db, seed, ProjectDeliveryStatus.Installing, claimedAt, claimedAt);

    /// <summary>Nothing here deploys: a token is never asked for.</summary>
    private sealed class NoTokens : IDeliveryTokenSource
    {
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, bool forceRefresh, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
