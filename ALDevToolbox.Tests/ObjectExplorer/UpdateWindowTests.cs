using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The pure window math behind delivery scheduling: same-day and midnight-wrapping
/// windows, "no window = any time", and the next-opening prefill, in UTC and in an
/// offset timezone. See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class UpdateWindowTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    // A fixed +02:00 zone (no DST) so conversions are deterministic across hosts.
    private static readonly TimeZoneInfo Plus2 =
        TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

    private static DateTime Utc0(int h, int m = 0) => new(2026, 7, 1, h, m, 0, DateTimeKind.Utc);

    [Fact]
    public void IsConfigured_requires_both_bounds()
    {
        UpdateWindow.IsConfigured(null, null).Should().BeFalse();
        UpdateWindow.IsConfigured(new TimeOnly(9, 0), null).Should().BeFalse();
        UpdateWindow.IsConfigured(new TimeOnly(9, 0), new TimeOnly(17, 0)).Should().BeTrue();
    }

    [Fact]
    public void IsWithin_is_true_for_any_time_when_no_window()
    {
        UpdateWindow.IsWithin(null, null, Utc, Utc0(3)).Should().BeTrue();
    }

    [Theory]
    [InlineData(12, true)]   // inside
    [InlineData(9, true)]    // start is inclusive
    [InlineData(17, false)]  // end is exclusive
    [InlineData(8, false)]   // before
    [InlineData(20, false)]  // after
    public void IsWithin_same_day_window(int hour, bool expected)
    {
        UpdateWindow.IsWithin(new TimeOnly(9, 0), new TimeOnly(17, 0), Utc, Utc0(hour)).Should().Be(expected);
    }

    [Theory]
    [InlineData(23, true)]   // after start, before midnight
    [InlineData(2, true)]    // after midnight, before end
    [InlineData(22, true)]   // start inclusive
    [InlineData(6, false)]   // end exclusive
    [InlineData(12, false)]  // middle of the day, outside
    public void IsWithin_wraps_past_midnight(int hour, bool expected)
    {
        UpdateWindow.IsWithin(new TimeOnly(22, 0), new TimeOnly(6, 0), Utc, Utc0(hour)).Should().Be(expected);
    }

    [Fact]
    public void NextOpeningUtc_returns_from_when_no_window()
    {
        var from = Utc0(3);
        UpdateWindow.NextOpeningUtc(null, null, Utc, from).Should().Be(from);
    }

    [Fact]
    public void NextOpeningUtc_returns_from_when_already_inside()
    {
        var from = Utc0(12);
        UpdateWindow.NextOpeningUtc(new TimeOnly(9, 0), new TimeOnly(17, 0), Utc, from).Should().Be(from);
    }

    [Fact]
    public void NextOpeningUtc_returns_today_start_when_before_it()
    {
        // 07:00, window 22:00–06:00 → next opening is 22:00 today.
        var next = UpdateWindow.NextOpeningUtc(new TimeOnly(22, 0), new TimeOnly(6, 0), Utc, Utc0(7));
        next.Should().Be(Utc0(22));
    }

    [Fact]
    public void NextOpeningUtc_rolls_to_tomorrow_when_past_todays_start()
    {
        // 18:00, window 09:00–17:00 → today's window has closed, next opening 09:00 tomorrow.
        var next = UpdateWindow.NextOpeningUtc(new TimeOnly(9, 0), new TimeOnly(17, 0), Utc, Utc0(18));
        next.Should().Be(new DateTime(2026, 7, 2, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void IsWithin_respects_the_timezone()
    {
        // 21:00 UTC = 23:00 in +02:00; window 22:00–06:00 local → inside.
        UpdateWindow.IsWithin(new TimeOnly(22, 0), new TimeOnly(6, 0), Plus2, Utc0(21)).Should().BeTrue();
        // 19:00 UTC = 21:00 local → before the 22:00 start → outside.
        UpdateWindow.IsWithin(new TimeOnly(22, 0), new TimeOnly(6, 0), Plus2, Utc0(19)).Should().BeFalse();
    }

    [Fact]
    public void NextOpeningUtc_converts_local_start_back_to_utc()
    {
        // +02:00 zone, window opens 22:00 local = 20:00 UTC. From 19:00 UTC (21:00 local,
        // before the local start) → next opening 20:00 UTC.
        var next = UpdateWindow.NextOpeningUtc(new TimeOnly(22, 0), new TimeOnly(6, 0), Plus2, Utc0(19));
        next.Should().Be(Utc0(20));
    }

    // ── Days of the week ──────────────────────────────────────────────────
    // Utc0 is Wednesday 1 July 2026.

    private static readonly TimeOnly Noon = new(12, 0);
    private static readonly TimeOnly One = new(13, 0);

    private static DateTime On(int day, int h, int m = 0) => new(2026, 7, day, h, m, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(1, 12, true)]   // Wednesday lunch
    [InlineData(1, 13, false)]  // Wednesday, end is exclusive
    [InlineData(2, 12, false)]  // Thursday lunch
    [InlineData(7, 12, false)]  // Tuesday lunch
    [InlineData(8, 12, true)]   // the next Wednesday
    public void IsWithin_only_on_the_picked_days(int day, int hour, bool expected)
    {
        UpdateWindow.IsWithin(Noon, One, Utc, On(day, hour), DeliveryDays.Wednesday).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, 23, true)]   // Wednesday night, after it opened
    [InlineData(2, 2, true)]    // Thursday small hours: still Wednesday's window
    [InlineData(1, 2, false)]   // Wednesday small hours belong to Tuesday's window
    [InlineData(2, 23, false)]  // Thursday night: Thursday isn't picked
    public void IsWithin_counts_an_overnight_window_on_the_day_it_opens(int day, int hour, bool expected)
    {
        UpdateWindow.IsWithin(new TimeOnly(22, 0), new TimeOnly(6, 0), Utc, On(day, hour), DeliveryDays.Wednesday)
            .Should().Be(expected);
    }

    [Fact]
    public void IsWithin_reads_the_day_in_the_customer_zone()
    {
        // 23:30 UTC on Tuesday is 01:30 on Wednesday in +02:00.
        UpdateWindow.IsWithin(new TimeOnly(1, 0), new TimeOnly(2, 0), Plus2, On(7, 23, 30), DeliveryDays.Wednesday)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(1, 13, true)]   // Wednesday afternoon, after it opened
    [InlineData(2, 3, true)]    // Thursday morning: still Wednesday's 24 hours
    [InlineData(2, 13, false)]  // Thursday afternoon
    [InlineData(1, 3, false)]   // Wednesday morning belongs to Tuesday
    public void IsWithin_equal_bounds_are_24_hours_from_the_start_on_a_picked_day(int day, int hour, bool expected)
    {
        UpdateWindow.IsWithin(Noon, Noon, Utc, On(day, hour), DeliveryDays.Wednesday).Should().Be(expected);
    }

    [Fact]
    public void NextOpeningUtc_skips_to_the_next_picked_day()
    {
        // Thursday morning: the next Wednesday lunch is six days later.
        UpdateWindow.NextOpeningUtc(Noon, One, Utc, On(2, 9), DeliveryDays.Wednesday).Should().Be(On(8, 12));
        // Wednesday after lunch: a week later.
        UpdateWindow.NextOpeningUtc(Noon, One, Utc, On(1, 14), DeliveryDays.Wednesday).Should().Be(On(8, 12));
        // Wednesday morning: today.
        UpdateWindow.NextOpeningUtc(Noon, One, Utc, On(1, 9), DeliveryDays.Wednesday).Should().Be(On(1, 12));
    }

    [Fact]
    public void NextOpeningUtc_picks_the_nearest_of_several_days()
    {
        // Thursday 14:00; Monday and Friday picked → Friday.
        UpdateWindow.NextOpeningUtc(Noon, One, Utc, On(2, 14), DeliveryDays.Monday | DeliveryDays.Friday)
            .Should().Be(On(3, 12));
    }

    [Fact]
    public void NextOpeningUtc_equal_bounds_open_at_the_start_on_the_next_picked_day()
    {
        UpdateWindow.NextOpeningUtc(Noon, Noon, Utc, On(2, 13), DeliveryDays.Wednesday).Should().Be(On(8, 12));
    }

    [Fact]
    public void NextOpeningUtc_on_a_picked_day_steps_over_the_spring_forward_gap()
    {
        // Copenhagen skips 02:00-03:00 on Sunday 29 March 2026; a Sunday 02:30 window
        // opens at the first representable time, 03:30 local (01:30 UTC).
        var copenhagen = UpdateWindow.ResolveTimeZone("Europe/Copenhagen");
        var from = new DateTime(2026, 3, 28, 12, 0, 0, DateTimeKind.Utc); // Saturday
        UpdateWindow.NextOpeningUtc(new TimeOnly(2, 30), new TimeOnly(3, 30), copenhagen, from, DeliveryDays.Sunday)
            .Should().Be(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void No_days_reads_as_every_day_so_a_window_never_stays_shut()
    {
        UpdateWindow.IsWithin(Noon, One, Utc, On(2, 12), DeliveryDays.None).Should().BeTrue();
        UpdateWindow.NextOpeningUtc(Noon, One, Utc, On(2, 14), DeliveryDays.None).Should().Be(On(3, 12));
    }

    [Theory]
    [InlineData(DeliveryDays.Every, "")]
    [InlineData(DeliveryDays.Wednesday, " on Wednesdays")]
    [InlineData(DeliveryDays.Weekdays, " on weekdays")]
    [InlineData(DeliveryDays.Weekend, " on weekends")]
    [InlineData(DeliveryDays.Monday | DeliveryDays.Wednesday | DeliveryDays.Sunday, " on Mon, Wed, Sun")]
    public void DaysSuffix_names_the_days_only_when_not_daily(DeliveryDays days, string expected)
    {
        UpdateWindow.DaysSuffix(days).Should().Be(expected);
    }
}
