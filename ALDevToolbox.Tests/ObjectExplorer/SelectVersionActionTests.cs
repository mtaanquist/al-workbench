using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// "Change the next version" as an upgrade action (issue #960): the kind's version rule,
/// the immediate send through <c>SelectTargetVersionAsync</c> with its live re-read, the
/// booked row the worker fires with the version it carried, and the history wording.
/// </summary>
public sealed class SelectVersionActionTests : IDisposable
{
    private readonly UpgradeActionTestFixture _f = new();

    public SelectVersionActionTests()
    {
        // 27.6 is chosen and 27.7 is on offer; after a write, whichever version was sent
        // is the selected one, as Business Central would report it.
        _f.Admin.OnUpdates = () =>
        {
            var selected = _f.Admin.SelectedTargetVersion ?? "27.6";
            return new[]
            {
                Offer("27.6", selected == "27.6"),
                Offer("27.7", selected == "27.7"),
                new BcEnvironmentUpdate("28.0", false, false, "", "GA", null, null, false, "", 4, 2027),
            };
        };
    }

    public void Dispose() => _f.Dispose();

    private static BcEnvironmentUpdate Offer(string version, bool selected, string status = "scheduled") =>
        new(version, true, selected, status, "GA", selected ? UpgradeActionTestFixture.ScheduledDate : null,
            UpgradeActionTestFixture.LatestDate, false, "Active", null, null);

    // ── The version rule ─────────────────────────────────────────────────

    [Theory]
    [InlineData("  ", "Choose the version to update to.")]
    // 33 characters: one past the longest version the column holds.
    [InlineData("28.0.123456789012345678901234.567", "That isn't a Business Central version.")]
    public async Task A_version_change_without_a_usable_version_is_refused_and_writes_nothing(string version, string message)
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();

        await using (var ctx = _f.Db.NewContext())
        {
            var act = () => _f.Svc(ctx).ScheduleUpgradeActionAsync(
                projectId, envId, UpgradeActionKind.SelectVersion, executeAt: null, targetVersion: version);
            (await act.Should().ThrowAsync<PlanValidationException>())
                .Which.Errors["TargetVersion"].Should().Be(message);
        }

        _f.Admin.Writes.Should().Be(0);
        await using var verify = _f.Db.NewContext();
        (await verify.OeEnvironmentUpgradeActions.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(UpgradeActionKind.PushDateToLatest)]
    [InlineData(UpgradeActionKind.RunNow)]
    public async Task A_date_move_that_names_a_version_is_refused(UpgradeActionKind kind)
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();

        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Svc(ctx).ScheduleUpgradeActionAsync(
            projectId, envId, kind, executeAt: null, targetVersion: "27.7");
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("TargetVersion");
        _f.Admin.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_recorded_only_kind_cannot_be_requested_here()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();

        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Svc(ctx).ScheduleUpgradeActionAsync(
            projectId, envId, UpgradeActionKind.CopyEnvironment, executeAt: null);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ── Immediate ────────────────────────────────────────────────────────

    [Fact]
    public async Task An_immediate_version_change_sends_the_version_and_records_it()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();

        UpgradeActionRow row;
        await using (var ctx = _f.Db.NewContext())
        {
            row = await _f.Svc(ctx).ScheduleUpgradeActionAsync(
                projectId, envId, UpgradeActionKind.SelectVersion, executeAt: null, targetVersion: " 27.7 ");
        }

        _f.Admin.Writes.Should().Be(1);
        _f.Admin.SelectedTargetVersion.Should().Be("27.7");
        _f.Admin.SelectedDateTime.Should().BeNull("changing the version leaves the date to Business Central");
        _f.Admin.SelectedIgnoreUpdateWindow.Should().BeNull();
        row.TargetVersion.Should().Be("27.7");

        var stored = await _f.ReadActionAsync(row.Id);
        stored.Status.Should().Be(UpgradeActionStatus.Sent);
        stored.TargetVersion.Should().Be("27.7");
        stored.Outcome.Should().Be("Set the next version to 27.7 on Production.");
    }

    [Fact]
    public async Task The_row_is_re_mirrored_with_the_new_version_and_the_versions_on_offer()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();

        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Svc(ctx).ScheduleUpgradeActionAsync(
                projectId, envId, UpgradeActionKind.SelectVersion, executeAt: null, targetVersion: "27.7");
        }

        await using var verify = _f.Db.NewContext();
        var env = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId);
        env.BcNextUpdateVersion.Should().Be("27.7", "the fleet page shows the change without waiting for the sweep");
        env.BcOfferedVersions.Should().Equal(["27.7", "27.6"],
            "newest first, and an unreleased version is not something anyone can pick");
    }

    [Fact]
    public async Task A_version_business_central_does_not_offer_is_refused_live_and_recorded_as_failed()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();

        await using (var ctx = _f.Db.NewContext())
        {
            var act = () => _f.Svc(ctx).ScheduleUpgradeActionAsync(
                projectId, envId, UpgradeActionKind.SelectVersion, executeAt: null, targetVersion: "28.0");
            await act.Should().ThrowAsync<PlanValidationException>();
        }

        _f.Admin.Writes.Should().Be(0, "the live re-read decides, whatever the mirror said");
        await using var verify = _f.Db.NewContext();
        var stored = await verify.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync();
        stored.Status.Should().Be(UpgradeActionStatus.Failed);
        stored.TargetVersion.Should().Be("28.0");
        stored.Outcome.Should().StartWith("The next version wasn't changed to 28.0.");
        stored.Outcome.Should().NotContain("Reopen the panel", "advice for the person at the form is dropped from the history");
    }

    [Fact]
    public async Task A_running_update_is_left_alone()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        _f.Admin.OnUpdates = () => new[] { Offer("27.6", true, status: "Running"), Offer("27.7", false) };

        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Svc(ctx).ScheduleUpgradeActionAsync(
            projectId, envId, UpgradeActionKind.SelectVersion, executeAt: null, targetVersion: "27.7");
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["TargetVersion"].Should().Contain("already running");
        _f.Admin.Writes.Should().Be(0, "once an update has started, Microsoft owns it");
    }

    [Fact]
    public async Task A_change_business_central_did_not_keep_is_not_recorded_as_done()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        // The write is accepted, but the re-read still shows 27.6 chosen.
        _f.Admin.OnUpdates = () => new[] { Offer("27.6", true), Offer("27.7", false) };

        await using (var ctx = _f.Db.NewContext())
        {
            var act = () => _f.Svc(ctx).ScheduleUpgradeActionAsync(
                projectId, envId, UpgradeActionKind.SelectVersion, executeAt: null, targetVersion: "27.7");
            (await act.Should().ThrowAsync<PlanValidationException>())
                .Which.Errors["TargetVersion"].Should().Contain("still 27.6");
        }

        await using var verify = _f.Db.NewContext();
        (await verify.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync())
            .Status.Should().Be(UpgradeActionStatus.Failed);
    }

    // ── Booked ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_booked_version_change_fires_with_the_version_it_carried()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();

        int actionId;
        await using (var ctx = _f.Db.NewContext())
        {
            var row = await _f.Svc(ctx).ScheduleUpgradeActionAsync(
                projectId, envId, UpgradeActionKind.SelectVersion,
                _f.Clock.GetUtcNow().AddHours(4), targetVersion: "27.7");
            actionId = row.Id;
        }
        _f.Admin.Writes.Should().Be(0);

        _f.Clock.Advance(TimeSpan.FromHours(5));
        var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        ran.Should().Be(1);
        _f.Admin.SelectedTargetVersion.Should().Be("27.7");
        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Sent);
        stored.Outcome.Should().Be("Set the next version to 27.7 on Production.");
    }

    [Fact]
    public async Task A_booked_version_change_refused_at_fire_time_says_which_version_did_not_land()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();

        int actionId;
        await using (var ctx = _f.Db.NewContext())
        {
            var row = await _f.Svc(ctx).ScheduleUpgradeActionAsync(
                projectId, envId, UpgradeActionKind.SelectVersion,
                _f.Clock.GetUtcNow().AddHours(4), targetVersion: "27.7");
            actionId = row.Id;
        }

        // Withdrawn during the afternoon.
        _f.Admin.OnUpdates = () => new[] { Offer("27.6", true) };
        _f.Clock.Advance(TimeSpan.FromHours(5));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Failed);
        stored.Outcome.Should().StartWith("The next version wasn't changed to 27.7.");
        _f.Admin.Writes.Should().Be(0);
    }

    [Fact]
    public async Task The_feed_and_the_pending_list_carry_the_version()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Svc(ctx).ScheduleUpgradeActionAsync(
                projectId, envId, UpgradeActionKind.SelectVersion,
                _f.Clock.GetUtcNow().AddHours(4), targetVersion: "27.7");
        }

        await using var read = _f.Db.NewContext();
        (await _f.Svc(read).ListEnvironmentActivityAsync(projectId, envId)).Single().TargetVersion.Should().Be("27.7");
        (await _f.Svc(read).ListPendingAsync()).Single().TargetVersion.Should().Be("27.7");
    }

    // ── Wording ──────────────────────────────────────────────────────────

    [Fact]
    public void The_success_outcome_names_the_version_and_the_environment()
    {
        UpgradeActionService.SuccessOutcome(UpgradeActionKind.SelectVersion, "29.2", "Production")
            .Should().Be("Set the next version to 29.2 on Production.");
        UpgradeActionService.SuccessOutcome(UpgradeActionKind.SelectVersion, "29.2")
            .Should().Be("Set the next version to 29.2.");
    }

    [Fact]
    public void The_failure_outcome_leads_with_the_version_that_was_not_set()
    {
        UpgradeActionService.FailureOutcome(UpgradeActionKind.SelectVersion,
                "Business Central 29.2 isn't available for Production right now. Reopen the panel to see what is.", "29.2")
            .Should().Be("The next version wasn't changed to 29.2. Reason given at the time: Business Central 29.2 isn't available for Production right now.");
    }

    [Fact]
    public void The_date_moves_keep_their_wording()
    {
        UpgradeActionService.SuccessOutcome(UpgradeActionKind.PushDateToLatest)
            .Should().Be("The update date was moved out to the latest Business Central allows.");
        UpgradeActionService.SuccessOutcome(UpgradeActionKind.RunNow)
            .Should().StartWith("Business Central was told to start the update");
    }
}
