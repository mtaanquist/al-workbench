using ALDevToolbox.Components.Pages.Upgrades;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The rules the Upgrades list sheet's spec sets (UpgradesListBody.dc.html), apart from the
/// page: which tone each status wears, what the breakdown counts, how a slot is said,
/// which upgrade comes first, and what the notices say.
/// </summary>
public sealed class UpgradesListWordingTests
{
    private static EnvironmentUpgradeSummary Upgrade(
        string name, DateTime? plannedAt = null, Dictionary<UpgradeLineState, int>? counts = null,
        UpgradeStatus status = UpgradeStatus.Planned, DateTime? createdAt = null)
    {
        counts ??= new();
        return new EnvironmentUpgradeSummary(
            1, name, "28.5", plannedAt, null, "Anna Jensen <anna@example.com>", createdAt ?? new DateTime(2026, 9, 1),
            null, null, DateTime.UtcNow, status, counts, counts.Values.Sum());
    }

    [Theory]
    [InlineData(UpgradeStatus.Planned, "Planned", "queued")]
    [InlineData(UpgradeStatus.InProgress, "In progress", "running")]
    [InlineData(UpgradeStatus.Updated, "Updated", "warn")]
    public void Each_open_status_has_the_sheets_word_and_tone(UpgradeStatus status, string word, string tone)
    {
        UpgradesListWording.StatusWord(status).Should().Be(word);
        UpgradesListWording.StatusTone(status).Should().Be(tone);
    }

    [Fact]
    public void The_breakdown_counts_checked_as_updated_and_tones_only_running_and_failed()
    {
        var u = Upgrade("x", counts: new()
        {
            [UpgradeLineState.Updated] = 4,
            [UpgradeLineState.Checked] = 2,
            [UpgradeLineState.Running] = 1,
            [UpgradeLineState.Failed] = 1,
            [UpgradeLineState.Planned] = 2,
        });

        UpgradesListWording.Breakdown(u).Should().Equal(
            new UpgradesListWording.Part("6 updated", null),
            new UpgradesListWording.Part("1 running", "running"),
            new UpgradesListWording.Part("1 failed", "failed"),
            new UpgradesListWording.Part("2 planned", null));
    }

    [Fact]
    public void A_state_with_no_lines_is_left_out_of_the_breakdown()
    {
        UpgradesListWording.Breakdown(Upgrade("x", counts: new() { [UpgradeLineState.Booked] = 11 }))
            .Should().Equal(new UpgradesListWording.Part("11 booked", null));
    }

    [Theory]
    [InlineData(0, 20, "Tonight")]
    [InlineData(0, 9, "Today")]
    [InlineData(-1, 20, "Last night")]
    [InlineData(-1, 9, "Yesterday")]
    [InlineData(1, 20, "Tomorrow")]
    [InlineData(4, 20, null)]
    public void A_near_slot_is_said_as_the_team_says_it(int days, int hour, string? word)
    {
        var now = new DateTime(2026, 9, 26, 8, 0, 0);
        var slot = now.Date.AddDays(days).AddHours(hour);

        UpgradesListWording.DayWord(UpgradesListWording.DayOf(slot, now), slot).Should().Be(word);
    }

    [Theory]
    [InlineData(3, "in 3 days")]
    [InlineData(-4, "4 days ago")]
    [InlineData(47, "in 7 weeks")]
    [InlineData(-120, "4 months ago")]
    public void A_far_slot_says_how_far_away_it_is(int days, string expected)
    {
        var now = new DateTime(2026, 9, 26, 8, 0, 0);
        UpgradesListWording.Distance(now.AddDays(days), now).Should().Be(expected);
    }

    [Fact]
    public void The_open_order_is_today_then_what_ran_most_recent_first_then_what_is_to_come_then_no_slot()
    {
        var now = new DateTime(2026, 9, 26, 8, 0, 0, DateTimeKind.Utc);
        var list = new[]
        {
            Upgrade("12 Nov", now.AddDays(47)),
            Upgrade("22 Sep", now.AddDays(-4)),
            Upgrade("no slot"),
            Upgrade("tonight", now.Date.AddHours(20)),
            Upgrade("29 Sep", now.AddDays(3)),
            Upgrade("last night", now.Date.AddDays(-1).AddHours(20)),
        };

        UpgradesListWording.OpenOrder(list, t => t, now).Select(u => u.Name).Should().Equal(
            "tonight", "last night", "22 Sep", "29 Sep", "12 Nov", "no slot");
    }

    [Fact]
    public void The_subtitle_counts_the_open_upgrades_and_what_is_booked_for_later_today()
    {
        var now = new DateTime(2026, 9, 26, 8, 0, 0, DateTimeKind.Utc);
        var open = new[]
        {
            Upgrade("tonight", now.Date.AddHours(20), new() { [UpgradeLineState.Booked] = 11 }),
            // Ran this morning: nothing of it is booked for tonight any more.
            Upgrade("this morning", now.Date.AddHours(6), new() { [UpgradeLineState.Booked] = 2 }),
            Upgrade("next week", now.AddDays(7), new() { [UpgradeLineState.Booked] = 3 }),
        };

        UpgradesListWording.Subtitle(open, t => t, now).Should().Be("3 open, 11 environments booked for tonight.");
        UpgradesListWording.Subtitle([], t => t, now).Should().Be("Nothing planned.");
        UpgradesListWording.Subtitle([open[2]], t => t, now).Should().Be("1 open, nothing booked for tonight.");
    }

    [Fact]
    public void Mark_done_says_how_many_are_unchecked_or_is_one_sentence_when_none_are()
    {
        UpgradesPage.MarkDoneWording("28.5 in November 2026", 3, 8).Should().Be((
            "3 of 8 are not checked yet. Mark done anyway?",
            "\"28.5 in November 2026\" moves to the Archive and becomes read-only. The unchecked lines stay unchecked in the record."));
        UpgradesPage.MarkDoneWording("28.5 in November 2026", 0, 8).Should().Be((
            "Mark \"28.5 in November 2026\" done?",
            "\"28.5 in November 2026\" moves to the Archive and becomes read-only."));
    }

    [Fact]
    public void The_added_notice_says_what_went_on_and_why_the_rest_did_not()
    {
        var result = new AddUpgradeLinesResult([1, 2, 3, 4, 5, 6], [], new Dictionary<string, string>());
        var notice = UpgradesPage.AddedNotice(new AddToUpgradeOutcome(7, "28.5 in October", result, 2, Created: false));

        notice.Text.Should().Be("Added 6 environments to \"28.5 in October\". 2 left out: already on another open upgrade.");
        notice.Tone.Should().Be(AlertTone.Warn);
        notice.LinkHref.Should().Be("/upgrades/7");

        var clean = UpgradesPage.AddedNotice(new AddToUpgradeOutcome(7, "28.5 in October",
            new AddUpgradeLinesResult([1], [], new Dictionary<string, string>()), 0, Created: true));
        clean.Text.Should().Be("Created \"28.5 in October\" and added 1 environment to it.");
        clean.Tone.Should().Be(AlertTone.Success);
    }
}
