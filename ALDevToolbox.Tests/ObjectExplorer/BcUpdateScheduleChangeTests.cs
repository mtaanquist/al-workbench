using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// What a refresh tells a solution's followers about an environment's next update
/// (issue #1049): a new one, a moved date, or the latest date coming close, each once.
/// </summary>
public sealed class BcUpdateScheduleChangeTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Date = Now.AddDays(20);
    private static readonly DateTime Latest = Now.AddDays(40);

    [Fact]
    public void The_first_read_announces_nothing() =>
        new BcUpdateScheduleSnapshot(null, null, null, null).CompareWith(Row("27.1", Date, Latest), Now).Should().BeNull();

    [Fact]
    public void A_new_version_is_scheduled() =>
        Snapshot("27.0", Date, Latest).CompareWith(Row("27.1", Date, Latest), Now)!.Kind.Should().Be(BcUpdateScheduleChangeKind.Scheduled);

    [Fact]
    public void A_date_where_there_was_none_is_scheduled() =>
        Snapshot("27.1", null, Latest).CompareWith(Row("27.1", Date, Latest), Now)!.Kind.Should().Be(BcUpdateScheduleChangeKind.Scheduled);

    [Fact]
    public void A_moved_date_carries_the_old_one()
    {
        var change = Snapshot("27.1", Date, Latest).CompareWith(Row("27.1", Date.AddDays(3), Latest), Now)!;

        change.Kind.Should().Be(BcUpdateScheduleChangeKind.Moved);
        change.PreviousDate.Should().Be(Date);
        change.Date.Should().Be(Date.AddDays(3));
    }

    [Fact]
    public void Nothing_changed_announces_nothing() =>
        Snapshot("27.1", Date, Latest).CompareWith(Row("27.1", Date, Latest), Now).Should().BeNull();

    [Fact]
    public void No_date_announces_nothing() =>
        Snapshot("27.0", Date, Latest).CompareWith(Row("27.1", null, Latest), Now).Should().BeNull();

    [Fact]
    public void The_latest_date_is_announced_once_as_it_comes_within_a_week()
    {
        var latest = Now.AddDays(6);
        var date = Now.AddDays(5);
        // The read before saw it eight days off.
        Snapshot("27.1", date, latest, fetchedAt: Now.AddDays(-2)).CompareWith(Row("27.1", date, latest), Now)!
            .Kind.Should().Be(BcUpdateScheduleChangeKind.LatestDateClose);
        // The read before already saw it within the week.
        Snapshot("27.1", date, latest, fetchedAt: Now.AddHours(-1)).CompareWith(Row("27.1", date, latest), Now)
            .Should().BeNull();
    }

    [Fact]
    public void A_date_is_shown_in_the_environments_time_zone_or_said_to_be_utc()
    {
        var late = new DateTime(2026, 10, 17, 23, 30, 0, DateTimeKind.Utc);

        EnvironmentUpdateNotifier.FormatDate(late, "Europe/Copenhagen").Should().Be("Sun 18 Oct 2026");
        EnvironmentUpdateNotifier.FormatDate(late, null).Should().Be("Sat 17 Oct 2026 (UTC)");
    }

    private static BcUpdateScheduleSnapshot Snapshot(string version, DateTime? date, DateTime? latest, DateTime? fetchedAt = null) =>
        new(version, date, latest, fetchedAt ?? Now.AddHours(-1));

    private static OeProjectEnvironment Row(string version, DateTime? date, DateTime? latest) => new()
    {
        Id = 4, ProjectId = 2, Name = "Production",
        BcNextUpdateVersion = version, BcNextUpdateDate = date, BcNextUpdateLatestDate = latest,
    };
}
