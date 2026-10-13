using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services;
using AwesomeAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Tests.Services;

/// <summary>
/// Unit tests for <see cref="WorkerHeartbeat"/> + <see cref="BackgroundWorkerHealthCheck"/>.
/// All time-dependent assertions go through <see cref="FakeTimeProvider"/> so
/// CI doesn't have to wait real seconds.
/// </summary>
public sealed class WorkerHeartbeatTests
{
    [Fact]
    public void Tick_marks_the_heartbeat_healthy_within_the_idle_window()
    {
        var registry = new WorkerHeartbeatRegistry();
        var hb = registry.Register("loop-worker",
            maxActiveDuration: TimeSpan.FromMinutes(5),
            maxIdleSilence: TimeSpan.FromMinutes(1));
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        hb.Tick();
        hb.IsHealthy(now, out var reason).Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public void Idle_silence_beyond_the_window_is_unhealthy()
    {
        var hb = new WorkerHeartbeatRegistry().Register("loop-worker",
            maxActiveDuration: TimeSpan.FromMinutes(5),
            maxIdleSilence: TimeSpan.FromMinutes(1));

        hb.Tick(); // beat once at construction-ish time
        var future = (hb.LastTickUtc ?? DateTime.UtcNow).AddMinutes(2);

        hb.IsHealthy(future, out var reason).Should().BeFalse();
        reason.Should().Contain("loop-worker").And.Contain("last loop iteration");
    }

    [Fact]
    public void Queue_workers_with_null_idle_silence_stay_healthy_when_idle()
    {
        // Queue-driven workers (ReleaseImportWorker, OffsiteRestoreWorker) opt
        // out of idle-silence checking — sitting idle waiting for an admin to
        // enqueue work is normal and shouldn't trip /healthz.
        var hb = new WorkerHeartbeatRegistry().Register("queue-worker",
            maxActiveDuration: TimeSpan.FromMinutes(30),
            maxIdleSilence: null);

        var future = (hb.LastTickUtc ?? DateTime.UtcNow).AddDays(7);

        hb.IsHealthy(future, out _).Should().BeTrue();
    }

    [Fact]
    public void Active_job_running_longer_than_ceiling_is_unhealthy()
    {
        // The stuck-DVD-download case: BeginActive fires, then nothing. After
        // the active-duration ceiling elapses, the check trips even though
        // the worker's loop hasn't ticked (because it can't — it's blocked
        // inside the job).
        var hb = new WorkerHeartbeatRegistry().Register("queue-worker",
            maxActiveDuration: TimeSpan.FromMinutes(30),
            maxIdleSilence: null);

        hb.BeginActive();
        var activeSince = hb.ActiveSinceUtc!.Value;
        var future = activeSince.AddMinutes(45);

        hb.IsHealthy(future, out var reason).Should().BeFalse();
        reason.Should().Contain("queue-worker").And.Contain("active job");
    }

    [Fact]
    public void A_scheduler_tick_longer_than_the_idle_bound_is_healthy_until_the_active_bound()
    {
        // #1256: the backup scheduler's bounds. A 20-minute nightly backup is
        // one tick, and the loop does not beat inside it; the idle bound must
        // not call that a stall. The active ceiling still catches a real wedge.
        var hb = new WorkerHeartbeatRegistry().Register("BackupScheduler",
            maxActiveDuration: TimeSpan.FromHours(2),
            maxIdleSilence: TimeSpan.FromMinutes(5));

        hb.BeginActive();
        var activeSince = hb.ActiveSinceUtc!.Value;

        hb.IsHealthy(activeSince.AddMinutes(20), out var reason).Should().BeTrue(reason);
        hb.IsHealthy(activeSince.AddHours(3), out reason).Should().BeFalse();
        reason.Should().Contain("active job");
    }

    [Fact]
    public void EndActive_clears_the_active_window_so_completed_long_jobs_dont_trip_later()
    {
        var hb = new WorkerHeartbeatRegistry().Register("queue-worker",
            maxActiveDuration: TimeSpan.FromMinutes(30),
            maxIdleSilence: null);

        hb.BeginActive();
        hb.EndActive();
        var future = (hb.LastTickUtc ?? DateTime.UtcNow).AddHours(5);

        hb.IsHealthy(future, out _).Should().BeTrue();
        hb.ActiveSinceUtc.Should().BeNull();
    }

    [Fact]
    public async Task HealthCheck_returns_healthy_when_no_workers_registered()
    {
        // Boot path: the check runs before any BackgroundService has had time
        // to register. Don't fail health on that — StartupReadinessHealthCheck
        // owns the boot-window signal.
        var registry = new WorkerHeartbeatRegistry();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var check = new BackgroundWorkerHealthCheck(registry, clock);

        var result = await check.CheckHealthAsync(new HealthCheckContext());
        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task HealthCheck_returns_unhealthy_with_every_failing_worker_named()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(t0);
        var registry = new WorkerHeartbeatRegistry(clock);
        var stuck = registry.Register("StuckWorker",
            maxActiveDuration: TimeSpan.FromMinutes(10), maxIdleSilence: null);
        var silent = registry.Register("SilentScheduler",
            maxActiveDuration: TimeSpan.FromMinutes(60), maxIdleSilence: TimeSpan.FromMinutes(2));
        var happy = registry.Register("HappyScheduler",
            maxActiveDuration: TimeSpan.FromMinutes(60), maxIdleSilence: TimeSpan.FromMinutes(2));

        stuck.BeginActive();
        // Advance past stuck's active ceiling AND silent's idle window
        // (silent only ever beat at construction). HappyScheduler beats just
        // before the check, so its idle window stays open.
        clock.Advance(TimeSpan.FromMinutes(20));
        happy.Tick();

        var check = new BackgroundWorkerHealthCheck(registry, clock);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("StuckWorker").And.Contain("SilentScheduler");
        result.Description.Should().NotContain("HappyScheduler");
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) { _now = start; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
