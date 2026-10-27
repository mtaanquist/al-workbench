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
    public void A_negative_time_other_than_infinite_is_refused()
    {
        var due = () => _clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(-1), Timeout.InfiniteTimeSpan);
        var period = () => _clock.CreateTimer(_ => { }, null, TimeSpan.Zero, TimeSpan.FromSeconds(-1));

        due.Should().Throw<ArgumentOutOfRangeException>();
        period.Should().Throw<ArgumentOutOfRangeException>();
    }
}
