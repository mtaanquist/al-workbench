using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
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
    private int _failNextResolves;

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

        _failNextResolves = 1;
        await scheduler.SweepAsync(CancellationToken.None);
        (await DeliveryHost.StatusAsync(_db, orphan)).Should().Be(ProjectDeliveryStatus.Installing, "the check for this organisation threw");

        await scheduler.SweepAsync(CancellationToken.None);
        (await DeliveryHost.StatusAsync(_db, orphan)).Should().Be(ProjectDeliveryStatus.Failed, "the next sweep tries again");

        // Done once it got through: the check is a restart's, not every sweep's.
        var later = await AddInProgressAsync(seed, claimedAt: Started.UtcDateTime.AddMinutes(-2));
        await scheduler.SweepAsync(CancellationToken.None);
        (await DeliveryHost.StatusAsync(_db, later)).Should().Be(ProjectDeliveryStatus.Installing);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private DeliveryScheduler NewScheduler(IServiceProvider provider) =>
        new(provider, _clock, NullLogger<DeliveryScheduler>.Instance, new WorkerHeartbeatRegistry());

    // Resolving the delivery service is how a test makes one organisation's check throw.
    private ServiceProvider BuildProvider() => DeliveryHost.Build(_db, _clock, () => _failNextResolves-- > 0
        ? throw new InvalidOperationException("The database went away for a moment.")
        : new NoTokens());

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
