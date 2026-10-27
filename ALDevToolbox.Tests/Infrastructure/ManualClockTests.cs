using AwesomeAssertions;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// The manual clock behaves like a real timer where the tests lean on it: a timer is
/// dead once disposed, and a negative time is refused rather than read as "now".
/// </summary>
public sealed class ManualClockTests
{
    private readonly ManualClock _clock = new(DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_timer_due_now_fires_on_the_next_advance_and_not_before()
    {
        var fired = 0;
        using var timer = _clock.CreateTimer(_ => fired++, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        fired.Should().Be(0);

        _clock.Advance(TimeSpan.Zero);

        fired.Should().Be(1);
        _clock.PendingTimers.Should().Be(0);
    }

    [Fact]
    public void A_disposed_timer_cannot_be_started_again()
    {
        var fired = 0;
        var timer = _clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        timer.Dispose();

        timer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan).Should().BeFalse();
        _clock.Advance(TimeSpan.FromSeconds(5));
        fired.Should().Be(0);
    }

    [Fact]
    public void A_periodic_timer_fires_once_per_period_passed_at_each_due_time()
    {
        var firedAt = new List<DateTimeOffset>();
        using var timer = _clock.CreateTimer(
            _ => firedAt.Add(_clock.GetUtcNow()), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        _clock.Advance(TimeSpan.FromSeconds(6));

        firedAt.Should().Equal(
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            DateTimeOffset.UnixEpoch.AddSeconds(3),
            DateTimeOffset.UnixEpoch.AddSeconds(5));
        _clock.GetUtcNow().Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(6));
        _clock.PendingTimers.Should().Be(1, "a periodic timer stays set until it is disposed");
    }

    [Fact]
    public void A_timer_changed_from_its_own_callback_fires_on_its_new_time()
    {
        var firedAt = new List<DateTimeOffset>();
        ITimer? timer = null;
        timer = _clock.CreateTimer(_ =>
        {
            firedAt.Add(_clock.GetUtcNow());
            // Re-arms once, as a poll that sets its next tick when one finishes.
            if (firedAt.Count == 1) timer!.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        _clock.Advance(TimeSpan.FromSeconds(10));

        firedAt.Should().Equal(DateTimeOffset.UnixEpoch.AddSeconds(1), DateTimeOffset.UnixEpoch.AddSeconds(4));
        _clock.PendingTimers.Should().Be(0);
        timer.Dispose();
    }

    [Fact]
    public void A_periodic_timer_stopped_from_its_own_callback_does_not_fire_again()
    {
        var fired = 0;
        ITimer? timer = null;
        timer = _clock.CreateTimer(_ =>
        {
            fired++;
            timer!.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        _clock.Advance(TimeSpan.FromSeconds(10));

        fired.Should().Be(1);
        _clock.PendingTimers.Should().Be(0);
        timer.Dispose();
    }

    [Fact]
    public void A_negative_time_other_than_infinite_is_refused()
    {
        var due = () => _clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(-1), Timeout.InfiniteTimeSpan);
        var period = () => _clock.CreateTimer(_ => { }, null, TimeSpan.Zero, TimeSpan.FromSeconds(-1));

        due.Should().Throw<ArgumentOutOfRangeException>();
        period.Should().Throw<ArgumentOutOfRangeException>();
    }
}
