using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The preview of "change the next version" and the picker's version list (issue #960),
/// both answered from the mirrored columns alone. One test per group in the issue's table,
/// plus the orderings that a string compare would get wrong.
/// </summary>
public sealed class SelectVersionPreviewTests
{
    private static int _nextId = 1;

    private static UpgradeFleetRow Row(
        string? next = "28.5",
        string? current = "28.4.1234.0",
        List<string>? offered = null,
        bool offeredUnknown = false,
        bool canAct = true,
        string? status = "Active",
        string? nextStatus = "scheduled",
        DateTime? softDeletedOn = null)
    {
        var id = _nextId++;
        return new UpgradeFleetRow(
            ProjectId: 1, ProjectName: "CRONUS Denmark", TimeZone: null,
            EnvironmentId: id, EnvironmentName: $"Env{id}", EnvironmentType: "Production",
            Status: status, Version: current,
            NextUpdateVersion: next, NextUpdateType: "GA", NextUpdateStatus: next is null ? null : nextStatus,
            NextUpdateDate: null, NextUpdateLatestDate: null, NextUpdateIgnoresWindow: false,
            FetchedAt: DateTime.UtcNow, CanAct: canAct,
            SoftDeletedOn: softDeletedOn,
            OfferedVersions: offeredUnknown ? null : offered ?? ["30.0", "29.2", "28.5"]);
    }

    private static SelectVersionPreviewRow One(UpgradeFleetRow row, string target = "29.2",
        IEnumerable<UpgradeActionRow>? pending = null) =>
        UpgradeFleetService.PreviewSelectVersion([row], target, pending ?? []).Single();

    [Fact]
    public void A_row_on_an_earlier_next_version_changes_forward()
    {
        var preview = One(Row(next: "28.5"));

        preview.Group.Should().Be(SelectVersionGroup.ChangeForward);
        preview.WillChange.Should().BeTrue();
        preview.FromVersion.Should().Be("28.5");
        preview.Detail.Should().Be("28.5 to 29.2");
        preview.OfferUnknown.Should().BeFalse();
    }

    [Fact]
    public void A_row_set_to_a_later_version_changes_back()
    {
        var preview = One(Row(next: "30.0"));

        preview.Group.Should().Be(SelectVersionGroup.ChangeBack);
        preview.WillChange.Should().BeTrue();
        preview.Detail.Should().Be("30.0 back to 29.2");
    }

    [Fact]
    public void A_row_with_nothing_chosen_yet_changes_forward()
    {
        var preview = One(Row(next: null));

        preview.Group.Should().Be(SelectVersionGroup.ChangeForward);
        preview.FromVersion.Should().BeNull();
    }

    [Theory]
    [InlineData("29.2.1234.0", "Already on 29.2")]
    [InlineData("29.3.1.0", "Already on 29.3, past 29.2")]
    [InlineData("30.0.5.0", "Already on 30.0, past 29.2")]
    public void A_row_already_on_the_version_or_later_is_skipped(string current, string detail)
    {
        var preview = One(Row(next: "30.1", current: current));

        preview.Group.Should().Be(SelectVersionGroup.AlreadyOnIt, "a row above the target is already on it, not an error");
        preview.WillChange.Should().BeFalse();
        preview.Detail.Should().Be(detail);
    }

    [Fact]
    public void Already_on_it_compares_numerically_not_as_text()
    {
        // As text "10.1" sorts before "9.2"; as a version it is later.
        One(Row(next: null, current: "10.1.0.0", offered: ["9.2"]), target: "9.2")
            .Group.Should().Be(SelectVersionGroup.AlreadyOnIt);
        One(Row(next: null, current: "9.1.0.0", offered: ["10.1"]), target: "10.1")
            .Group.Should().Be(SelectVersionGroup.ChangeForward);
    }

    [Fact]
    public void A_row_whose_next_update_is_the_target_is_already_chosen()
    {
        var preview = One(Row(next: "29.2"));

        preview.Group.Should().Be(SelectVersionGroup.AlreadyChosen);
        preview.Detail.Should().Be("Already set to 29.2");
    }

    [Fact]
    public void A_row_not_offered_the_version_is_skipped_with_the_reason()
    {
        var preview = One(Row(next: "28.5", offered: ["28.5"]));

        preview.Group.Should().Be(SelectVersionGroup.NotOffered);
        preview.Detail.Should().Be("Business Central does not offer 29.2 to this environment yet");
    }

    [Fact]
    public void A_row_whose_offer_list_was_never_read_changes_and_says_the_live_read_decides()
    {
        var preview = One(Row(next: "28.5", offeredUnknown: true));

        preview.Group.Should().Be(SelectVersionGroup.ChangeForward);
        preview.OfferUnknown.Should().BeTrue("the run re-reads the offer list live and refuses a version not on it");
    }

    [Fact]
    public void A_running_update_is_left_alone()
    {
        One(Row(nextStatus: "Running")).Group.Should().Be(SelectVersionGroup.UpdateUnderWay);
        One(Row(status: "Upgrading")).Group.Should().Be(SelectVersionGroup.UpdateUnderWay);
    }

    [Fact]
    public void A_row_with_one_of_our_actions_waiting_is_left_alone()
    {
        var row = Row();
        var pending = new UpgradeActionRow(
            7, row.ProjectId, row.EnvironmentId, UpgradeActionKind.RunNow, UpgradeActionStatus.Pending,
            "Anna Jensen <upgrade@example.com>", DateTime.UtcNow, DateTime.UtcNow.AddHours(4), null, null, null, null);

        var preview = One(row, pending: [pending]);

        preview.Group.Should().Be(SelectVersionGroup.UpdateUnderWay);
        preview.Detail.Should().Contain("already booked");
    }

    [Fact]
    public void A_row_the_person_cannot_act_on_is_no_access()
    {
        One(Row(canAct: false)).Group.Should().Be(SelectVersionGroup.NoAccess);
    }

    [Fact]
    public void A_deleted_environment_is_missing()
    {
        One(Row(softDeletedOn: DateTime.UtcNow)).Group.Should().Be(SelectVersionGroup.Missing);
        One(Row(status: "Removing")).Group.Should().Be(SelectVersionGroup.Missing);
    }

    [Fact]
    public void Missing_wins_over_no_access_and_no_access_over_everything_else()
    {
        One(Row(canAct: false, softDeletedOn: DateTime.UtcNow)).Group.Should().Be(SelectVersionGroup.Missing);
        One(Row(canAct: false, nextStatus: "Running")).Group.Should().Be(SelectVersionGroup.NoAccess);
    }

    [Fact]
    public void Every_row_comes_back_in_the_order_given()
    {
        var rows = new[] { Row(next: "28.5"), Row(next: "29.2"), Row(canAct: false) };

        var preview = UpgradeFleetService.PreviewSelectVersion(rows, "29.2", []);

        preview.Select(p => p.Row.EnvironmentId).Should().Equal(rows.Select(r => r.EnvironmentId));
        preview.Select(p => p.Group).Should().Equal(
            SelectVersionGroup.ChangeForward, SelectVersionGroup.AlreadyChosen, SelectVersionGroup.NoAccess);
    }

    // ── The picker ───────────────────────────────────────────────────────

    [Fact]
    public void The_picker_lists_every_offered_version_newest_first_with_a_count()
    {
        var rows = new[]
        {
            Row(offered: ["30.0", "29.2", "28.5"]),
            Row(offered: ["29.2", "28.5"]),
            Row(offered: ["9.2", "10.1"]),
        };

        var options = UpgradeFleetService.OfferedVersions(rows);

        options.Should().Equal(
            new OfferedVersionOption("30.0", 1),
            new OfferedVersionOption("29.2", 2),
            new OfferedVersionOption("28.5", 2),
            new OfferedVersionOption("10.1", 1),
            new OfferedVersionOption("9.2", 1));
    }

    [Fact]
    public void The_picker_falls_back_to_the_next_version_when_the_offer_list_was_never_read()
    {
        var rows = new[] { Row(next: "29.2", offeredUnknown: true), Row(next: null, offeredUnknown: true) };

        UpgradeFleetService.OfferedVersions(rows).Should().Equal(new OfferedVersionOption("29.2", 1));
    }

    [Fact]
    public void The_picker_speaks_in_major_minor()
    {
        var rows = new[] { Row(offered: ["29.2.0.0"]), Row(offered: ["29.2"]) };

        UpgradeFleetService.OfferedVersions(rows).Should().Equal(new OfferedVersionOption("29.2", 2));
    }

    // ── Start update from an upgrade ────────────────────────────────────

    [Fact]
    public void An_upgrade_start_takes_a_row_offered_its_release_even_when_another_is_next() =>
        UpgradeFleetService.WhyNoUpgradeStart(Row(next: "28.3", offered: ["28.3", "28.5"]), "28.5").Should().BeNull();

    [Fact]
    public void An_upgrade_start_takes_a_row_whose_next_update_is_its_release() =>
        UpgradeFleetService.WhyNoUpgradeStart(Row(next: "28.5.12345.0", offeredUnknown: true), "28.5").Should().BeNull();

    [Fact]
    public void An_upgrade_start_passes_over_a_row_not_offered_its_release() =>
        UpgradeFleetService.WhyNoUpgradeStart(Row(next: "28.3", offered: ["28.3"]), "28.5")
            .Should().Be("Business Central does not offer 28.5 to this environment yet");

    [Fact]
    public void An_upgrade_start_passes_over_a_row_already_on_its_release() =>
        UpgradeFleetService.WhyNoUpgradeStart(Row(next: null, current: "28.5.2000.0", offered: []), "28.5")
            .Should().Be("already on 28.5");
}
