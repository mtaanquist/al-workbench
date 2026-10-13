using System.Collections.Concurrent;

namespace ALDevToolbox.Services.Workers;

/// <summary>
/// Tiny thread-safe liveness record for one <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>.
/// Each worker constructs one via <see cref="WorkerHeartbeatRegistry.Register"/>
/// at startup, then calls <see cref="Tick"/> on every loop iteration (periodic
/// schedulers) or brackets long-running work with <see cref="BeginActive"/> /
/// <see cref="EndActive"/> (queue-driven workers). <see cref="BackgroundWorkerHealthCheck"/>
/// reads these snapshots out-of-band so a stuck or wedged worker shows up on
/// <c>/healthz</c> instead of hanging silently.
///
/// <para><b>Policy:</b> a worker is unhealthy when either
/// <list type="bullet">
///   <item>it has an in-flight job and <see cref="ActiveSinceUtc"/> is older
///     than <see cref="MaxActiveDuration"/> (stuck / wedged on one job), or</item>
///   <item>it has no in-flight job, a finite <see cref="MaxIdleSilence"/>, and no
///     <see cref="Tick"/> has landed within that window (the loop crashed or
///     stalled outside a job). The idle bound does not apply while a job is in
///     flight; <see cref="MaxActiveDuration"/> covers that. Queue-driven workers that legitimately sit idle for hours pass
///     <c>null</c> for <see cref="MaxIdleSilence"/> to opt out of this check.</item>
/// </list>
/// Schedulers pick <see cref="MaxIdleSilence"/> as ~3× their poll interval; the
/// <see cref="MaxActiveDuration"/> ceiling is the longest legitimate single
/// iteration / job. Tighter than that and you'll alarm on a slow-but-fine
/// import; looser and a real wedge sits unnoticed for too long.</para>
/// </summary>
public sealed class WorkerHeartbeat
{
    private long _lastTickTicks;
    private long _activeSinceTicks;
    private readonly TimeProvider _clock;

    public string Name { get; }
    public TimeSpan MaxActiveDuration { get; }
    public TimeSpan? MaxIdleSilence { get; }

    internal WorkerHeartbeat(string name, TimeSpan maxActiveDuration, TimeSpan? maxIdleSilence, TimeProvider clock)
    {
        Name = name;
        MaxActiveDuration = maxActiveDuration;
        MaxIdleSilence = maxIdleSilence;
        _clock = clock;
        // Treat construction as the first tick so a worker created moments before
        // the health check fires doesn't get flagged purely on registration order.
        _lastTickTicks = _clock.GetUtcNow().UtcDateTime.Ticks;
    }

    public DateTime? LastTickUtc
    {
        get
        {
            var t = Interlocked.Read(ref _lastTickTicks);
            return t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
        }
    }

    public DateTime? ActiveSinceUtc
    {
        get
        {
            var t = Interlocked.Read(ref _activeSinceTicks);
            return t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
        }
    }

    /// <summary>Records progress: the worker's loop completed an iteration.</summary>
    public void Tick() => Interlocked.Exchange(ref _lastTickTicks, _clock.GetUtcNow().UtcDateTime.Ticks);

    /// <summary>Marks a long-running unit of work as started. Also ticks.</summary>
    public void BeginActive()
    {
        var now = _clock.GetUtcNow().UtcDateTime.Ticks;
        Interlocked.Exchange(ref _activeSinceTicks, now);
        Interlocked.Exchange(ref _lastTickTicks, now);
    }

    /// <summary>Marks the active unit of work as complete. Also ticks.</summary>
    public void EndActive()
    {
        Interlocked.Exchange(ref _activeSinceTicks, 0);
        Interlocked.Exchange(ref _lastTickTicks, _clock.GetUtcNow().UtcDateTime.Ticks);
    }

    /// <summary>
    /// Evaluates the heartbeat against the policy. <paramref name="reason"/> is
    /// only set when unhealthy.
    /// </summary>
    public bool IsHealthy(DateTime nowUtc, out string? reason)
    {
        var activeSince = ActiveSinceUtc;
        if (activeSince is not null && (nowUtc - activeSince.Value) > MaxActiveDuration)
        {
            reason = $"{Name}: active job has been running for {(nowUtc - activeSince.Value).TotalMinutes:N0} min (limit {MaxActiveDuration.TotalMinutes:N0} min).";
            return false;
        }
        // While a unit of work is in flight the active ceiling above is the
        // bound that applies. PolledScheduler only ticks before and after a
        // tick, so applying the idle bound as well would call a backup or a
        // vacuum that runs past it "stalled" in the middle of normal work (#1256).
        if (activeSince is null && MaxIdleSilence is { } idleCeiling)
        {
            var lastTick = LastTickUtc;
            if (lastTick is null || (nowUtc - lastTick.Value) > idleCeiling)
            {
                var age = lastTick is null ? "(never)" : $"{(nowUtc - lastTick.Value).TotalMinutes:N1} min ago";
                reason = $"{Name}: last loop iteration was {age} (limit {idleCeiling.TotalMinutes:N1} min).";
                return false;
            }
        }
        reason = null;
        return true;
    }
}

/// <summary>
/// Singleton registry of every worker's heartbeat. Workers register on construction;
/// the health check enumerates and reports.
/// </summary>
public sealed class WorkerHeartbeatRegistry
{
    private readonly ConcurrentDictionary<string, WorkerHeartbeat> _heartbeats = new();
    private readonly TimeProvider _clock;

    public WorkerHeartbeatRegistry() : this(TimeProvider.System) { }

    public WorkerHeartbeatRegistry(TimeProvider clock) => _clock = clock;

    /// <summary>
    /// Registers (or returns the already-registered) heartbeat for <paramref name="name"/>.
    /// Idempotent so a worker that's restarted mid-process (rare) doesn't blow up.
    /// </summary>
    public WorkerHeartbeat Register(string name, TimeSpan maxActiveDuration, TimeSpan? maxIdleSilence) =>
        _heartbeats.GetOrAdd(name, n => new WorkerHeartbeat(n, maxActiveDuration, maxIdleSilence, _clock));

    public IReadOnlyCollection<WorkerHeartbeat> All() => _heartbeats.Values.ToArray();
}
