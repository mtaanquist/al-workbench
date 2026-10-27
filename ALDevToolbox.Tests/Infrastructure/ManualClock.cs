namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// A <see cref="TimeProvider"/> whose time only moves when a test calls
/// <see cref="Advance"/>, timers included: a timer or a <c>Task.Delay</c> on it fires
/// inside <see cref="Advance"/>, on the test's thread, once its time has come. For code
/// that waits on the clock rather than reads it, where a real wait is either slow or
/// races the machine's load (#1439). The other hand-rolled clocks in the suite only read.
/// </summary>
public sealed class ManualClock : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;
    private long _created;

    public ManualClock(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _now;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer;
        lock (_lock) timer = new ManualTimer(this, callback, state, _created++);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Moves time on by <paramref name="by"/>, firing every timer that falls due on the
    /// way, in the order they fall due, with the clock reading each one's due time.
    /// </summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        DateTimeOffset target;
        lock (_lock) target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_lock)
            {
                next = _timers
                    .Where(t => t.DueAt <= target)
                    .OrderBy(t => t.DueAt).ThenBy(t => t.Order)
                    .FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }
                if (next.DueAt > _now) _now = next.DueAt;
                if (next.Period > TimeSpan.Zero) next.DueAt += next.Period;
                else _timers.Remove(next);
            }
            next.Callback(next.State);
        }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state, long order) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public long Order { get; } = order;
        public DateTimeOffset DueAt { get; set; }
        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._lock)
            {
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                DueAt = clock._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                clock._timers.Add(this);
            }
            return true;
        }

        public void Dispose()
        {
            lock (clock._lock) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
