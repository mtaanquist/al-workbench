using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The worker that fires the slots the upgrade team booked (issue #657 Stage 4b). What is
/// pinned here: a due row actually reaches Business Central and is attributed to the
/// person who asked for it, a row that isn't due yet is left alone, a failure lands as a
/// failed row with a readable reason rather than taking the sweep down, and the
/// claim-versus-cancel race resolves the same way from both sides.
/// </summary>
public sealed class UpgradeActionWorkerTests : IDisposable
{
    private readonly UpgradeActionTestFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task A_due_action_is_sent_and_attributed_to_the_person_who_booked_it()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);

        _f.Clock.Advance(TimeSpan.FromHours(13));
        var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        ran.Should().Be(1);
        _f.Admin.Writes.Should().Be(1);
        _f.Admin.SelectedIgnoreUpdateWindow.Should().BeTrue("a booked slot does exactly what pressing the button would have done");

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Sent);
        stored.SentAt.Should().Be(_f.Clock.GetUtcNow().UtcDateTime);
        stored.Outcome.Should().NotBeNullOrWhiteSpace();

        // The audit log has to name the person, not the machine: the worker runs under
        // the requester's identity precisely so this row reads like the immediate one.
        await using var verify = _f.Db.NewContext();
        var entry = await verify.AuditLog.AsNoTracking()
            .Where(a => a.EntityType == AuditEntityType.ProjectEnvironment && a.EntityId == envId)
            .OrderByDescending(a => a.Id)
            .FirstAsync();
        entry.ChangedByUserId.Should().Be(UpgradeActionTestFixture.FlagUserId);
        entry.ChangedBy.Should().Contain("Anna Jensen");
    }

    [Fact]
    public async Task An_action_whose_slot_has_not_arrived_is_left_alone()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);

        _f.Clock.Advance(TimeSpan.FromHours(1));
        var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        ran.Should().Be(0);
        _f.Admin.Writes.Should().Be(0);
        (await _f.ReadActionAsync(actionId)).Status.Should().Be(UpgradeActionStatus.Pending);
    }

    [Fact]
    public async Task A_live_refusal_at_fire_time_lands_as_a_failed_row_with_the_reason()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);

        // Between booking and firing, the update was applied or withdrawn.
        _f.Admin.OnUpdates = Array.Empty<BcEnvironmentUpdate>;
        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Failed);
        stored.Outcome.Should().Contain("No update", "the feed says why in the words the service refused with");
        stored.SentAt.Should().NotBeNull("we tried, and when we tried is part of the history");
    }

    [Fact]
    public async Task A_booked_start_starts_the_update_it_was_booked_for()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await SetNextUpdateAsync(envId, "27.6");
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);
        (await _f.ReadActionAsync(actionId)).TargetVersion.Should().Be("27.6", "the booking remembers what it was shown");

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        _f.Admin.Writes.Should().Be(1);
        (await _f.ReadActionAsync(actionId)).Status.Should().Be(UpgradeActionStatus.Sent);
    }

    [Fact]
    public async Task A_booked_start_does_not_start_another_update_that_became_next_in_the_meantime()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await SetNextUpdateAsync(envId, "27.6");
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);

        // In the afternoon Microsoft makes 28.0 the next update (#1234).
        _f.Admin.OnUpdates = () => new[]
        {
            UpgradeActionTestFixture.Update(UpgradeActionTestFixture.ScheduledDate, UpgradeActionTestFixture.LatestDate) with { TargetVersion = "28.0" },
        };
        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        _f.Admin.Writes.Should().Be(0, "the customer agreed to 27.6, not to whatever is next at the slot");
        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Failed);
        stored.Outcome.Should().Contain("28.0").And.Contain("27.6");
    }

    private async Task SetNextUpdateAsync(int envId, string version)
    {
        await using var ctx = _f.Db.NewContext();
        await ctx.OeProjectEnvironments.Where(e => e.Id == envId)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.BcNextUpdateVersion, version));
    }

    [Fact]
    public async Task A_booking_made_by_someone_disabled_since_does_not_run()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);

        // The person who booked it leaves and their account is disabled (#1235).
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.Users.Where(u => u.Id == UpgradeActionTestFixture.FlagUserId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, ALDevToolbox.Domain.Entities.UserStatus.Disabled));
        }
        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        _f.Admin.Writes.Should().Be(0, "a disabled account runs nothing");
        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Failed);
        stored.Outcome.Should().Contain("no longer had permission");
    }

    [Fact]
    public async Task One_customers_failure_does_not_stop_the_others()
    {
        var (projectA, envA) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (projectB, envB) = await _f.SeedCustomerAsync("CRONUS Norway", "Europe/Oslo");
        var failing = await BookAsync(projectA, envA, hoursAhead: 12);
        // An hour later, so the sweep's order is not in doubt.
        var fine = await BookAsync(projectB, envB, hoursAhead: 13);

        // The first customer's tenant refuses; every later read answers normally.
        var reads = 0;
        _f.Admin.OnUpdates = () => ++reads == 1
            ? throw new BcApiException(System.Net.HttpStatusCode.Forbidden, "The credentials were rejected.")
            : new[] { UpgradeActionTestFixture.Update(UpgradeActionTestFixture.ScheduledDate, UpgradeActionTestFixture.LatestDate) };

        _f.Clock.Advance(TimeSpan.FromHours(14));
        var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        ran.Should().Be(2, "one unreachable customer must not cost the other eighty-nine");
        (await _f.ReadActionAsync(failing)).Status.Should().Be(UpgradeActionStatus.Failed);
        (await _f.ReadActionAsync(fine)).Status.Should().Be(UpgradeActionStatus.Sent);
    }

    // ── The race, from both sides ───────────────────────────────────────

    [Fact]
    public async Task A_cancel_that_arrives_after_the_send_loses_cleanly()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        // A second context, exactly as a person's circuit would be: the cancel is refused
        // in words, not silently ignored.
        await using var cancelCtx = _f.Db.NewContext();
        var act = () => _f.Svc(cancelCtx).CancelUpgradeActionAsync(actionId);
        (await act.Should().ThrowAsync<ALDevToolbox.Domain.ValueObjects.PlanValidationException>())
            .Which.Errors.Values.Should().ContainMatch("*already run*");

        (await _f.ReadActionAsync(actionId)).Status.Should().Be(UpgradeActionStatus.Sent);
    }

    [Fact]
    public async Task A_send_that_arrives_after_the_cancel_never_reaches_Business_Central()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);

        await using (var cancelCtx = _f.Db.NewContext())
            await _f.Svc(cancelCtx).CancelUpgradeActionAsync(actionId);

        _f.Clock.Advance(TimeSpan.FromHours(13));
        var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        ran.Should().Be(0);
        _f.Admin.Writes.Should().Be(0, "the customer's tenant must never be touched by an action somebody took back");
        (await _f.ReadActionAsync(actionId)).Status.Should().Be(UpgradeActionStatus.Cancelled);
    }

    [Fact]
    public async Task An_action_a_restart_interrupted_mid_send_is_failed_rather_than_repeated()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);

        // The shape a crash leaves behind: claimed (sent_at stamped) but still pending.
        await using (var ctx = _f.Db.NewContext())
        {
            var row = await ctx.OeEnvironmentUpgradeActions.SingleAsync(a => a.Id == actionId);
            row.SentAt = _f.Clock.GetUtcNow().UtcDateTime;
            await ctx.SaveChangesAsync();
        }

        await _f.Worker().FailInterruptedAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Failed);
        stored.Outcome.Should().Contain("restarted");
        _f.Admin.Writes.Should().Be(0, "repeating 'start the update now' on a guess is not a safe default");
    }

    /// <summary>
    /// A row whose settling write was lost stays claimed and pending, which nobody can
    /// cancel. The regular sweep settles it once it is older than an install can take,
    /// and leaves a recent claim alone (#1265).
    /// </summary>
    [Fact]
    public async Task A_sweep_settles_a_row_whose_outcome_was_never_written_and_leaves_a_fresh_claim_alone()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var lost = await BookAsync(projectId, envId, hoursAhead: 12);
        var fresh = await BookAsync(projectId, envId, hoursAhead: 13);

        await using (var ctx = _f.Db.NewContext())
        {
            var now = _f.Clock.GetUtcNow().UtcDateTime;
            (await ctx.OeEnvironmentUpgradeActions.SingleAsync(a => a.Id == lost)).SentAt = now.AddHours(-1);
            (await ctx.OeEnvironmentUpgradeActions.SingleAsync(a => a.Id == fresh)).SentAt = now.AddMinutes(-1);
            await ctx.SaveChangesAsync();
        }

        await _f.Worker().SettleStaleClaimsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        (await _f.ReadActionAsync(lost)).Status.Should().Be(UpgradeActionStatus.Failed);
        (await _f.ReadActionAsync(fresh)).Status.Should().Be(UpgradeActionStatus.Pending,
            "a claim younger than an install can take may still be mid-send");
        _f.Admin.Writes.Should().Be(0);
    }

    // ── Notifications (#1046) ───────────────────────────────────────────
    // Whoever booked a change hears how it went, once, when it has settled.

    [Fact]
    public async Task Someone_taken_off_a_private_solutions_team_is_not_told()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await BookAsync(projectId, envId, hoursAhead: 12);
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeProjects.Where(p => p.Id == projectId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Visibility, ProjectVisibility.Private));
            await ctx.TeamMembers.Where(m => m.UserId == UpgradeActionTestFixture.FlagUserId).ExecuteDeleteAsync();
        }

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        _f.Emails.Sent.Should().BeEmpty();
        await using var verify = _f.Db.NewContext();
        (await verify.UserNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task The_person_who_booked_a_change_is_told_it_ran()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await BookAsync(projectId, envId, hoursAhead: 12);

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var sent = _f.Emails.Sent.Should().ContainSingle().Subject;
        sent.To.Should().Be("upgrade@example.com");
        sent.Subject.Should().Be("Done: Start the update on CRONUS Denmark / Production");
        sent.Purpose.Should().Be(EmailPurpose.UpgradeNotification);
        sent.Html.Should().Contain($"/environments/{envId}/history");
        await using var ctx = _f.Db.NewContext();
        var listed = await ctx.UserNotifications.SingleAsync();
        listed.UserId.Should().Be(UpgradeActionTestFixture.FlagUserId);
        listed.Category.Should().Be(NotificationCategory.Upgrades);
    }

    [Fact]
    public async Task The_person_who_booked_a_change_is_told_why_it_failed()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await BookAsync(projectId, envId, hoursAhead: 12);

        _f.Admin.OnUpdates = Array.Empty<BcEnvironmentUpdate>;
        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var sent = _f.Emails.Sent.Should().ContainSingle().Subject;
        sent.Subject.Should().Be("Failed: Start the update on CRONUS Denmark / Production");
        sent.Html.Should().Contain("No update");
    }

    [Fact]
    public async Task An_install_nobody_saw_finish_is_announced_once_after_the_second_look()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app");
        _f.Apps.PollErrorsBeforeAnswer = BcAppOperationPoller.MaxConsecutivePollErrors;

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);
        _f.Emails.Sent.Should().BeEmpty("the answer is still to come");

        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);
        _f.Emails.Sent.Should().ContainSingle().Which.Subject.Should().Be("Done: Install Core.app on CRONUS Denmark / Production");
    }

    [Fact]
    public async Task A_long_failure_reason_is_cut_to_one_line_in_the_notification_and_kept_whole_in_the_email()
    {
        // #1547: the bell flyout, /notifications and the digest show the detail as one
        // line, like every other notifier; Business Central's reason can run long.
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app");
        var reason = "The extension could not be deployed because it is already deployed on another tenant. "
            + new string('x', 400) + "\nCorrelation id: CRONUS-TAIL-MARKER";
        _f.Apps.InstallThrows = new BcApiException(System.Net.HttpStatusCode.BadRequest, reason);

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var sent = _f.Emails.Sent.Should().ContainSingle().Subject;
        sent.Subject.Should().StartWith("Failed:");
        sent.Html.Should().Contain("CRONUS-TAIL-MARKER", "the email keeps the whole outcome");
        await using var ctx = _f.Db.NewContext();
        var detail = (await ctx.UserNotifications.AsNoTracking().SingleAsync()).Detail;
        detail.Should().NotBeNull();
        detail!.Length.Should().BeLessThanOrEqualTo(NotificationText.MaxLineLength + 3);
        detail.Should().NotContain("\n").And.NotContain("CRONUS-TAIL-MARKER").And.EndWith("...");
    }

    [Fact]
    public async Task An_install_still_unconfirmed_after_the_second_look_asks_the_person_to_check()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app");
        _f.Apps.PollErrorsBeforeAnswer = 2 * BcAppOperationPoller.MaxConsecutivePollErrors;

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var sent = _f.Emails.Sent.Should().ContainSingle().Subject;
        sent.Subject.Should().Be("Not confirmed: Install Core.app on CRONUS Denmark / Production");
        sent.Html.Should().Contain("could not confirm");
    }

    [Fact]
    public async Task An_install_a_restart_interrupted_is_announced_once_after_business_central_is_asked()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var ids = await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app");
        _f.Clock.Advance(TimeSpan.FromHours(13));
        await InterruptMidInstallAsync(ids[0], _f.Apps.Accepted("Core.app"));

        await _f.Worker().FailInterruptedAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);
        _f.Emails.Sent.Should().BeEmpty("Business Central is still to be asked how it ended");

        await SweepUntilQuietAsync();
        _f.Emails.Sent.Should().ContainSingle().Which.Subject.Should().Be("Done: Install Core.app on CRONUS Denmark / Production");
    }

    [Fact]
    public async Task A_cancelled_booking_sends_nothing()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeEnvironmentUpgradeActions.Where(a => a.Id == actionId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, UpgradeActionStatus.Cancelled));
        }

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        _f.Emails.Sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData(UpgradeActionKind.PushDateToLatest, null, null, "Move the update to the latest date")]
    [InlineData(UpgradeActionKind.SelectVersion, "27.1", null, "Set the next version to 27.1")]
    [InlineData(UpgradeActionKind.UpdateApp, "2.0.0.0", "CRONUS Coffee", "Update CRONUS Coffee to 2.0.0.0")]
    [InlineData(UpgradeActionKind.UpdateApp, null, "CRONUS Coffee", "Update CRONUS Coffee")]
    public void Each_kind_of_change_is_named_in_plain_words(UpgradeActionKind kind, string? version, string? app, string expected) =>
        ALDevToolbox.Services.Notifications.UpgradeActionNotifier.Describe(kind, version, app).Should().Be(expected);

    [Fact]
    public async Task A_change_a_restart_interrupted_is_announced_as_failed()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);
        await using (var ctx = _f.Db.NewContext())
        {
            var row = await ctx.OeEnvironmentUpgradeActions.SingleAsync(a => a.Id == actionId);
            row.SentAt = _f.Clock.GetUtcNow().UtcDateTime;
            await ctx.SaveChangesAsync();
        }

        await _f.Worker().FailInterruptedAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        _f.Emails.Sent.Should().ContainSingle().Which.Subject.Should().Be("Failed: Start the update on CRONUS Denmark / Production");
    }

    [Fact]
    public async Task The_apps_after_a_failed_one_in_a_batch_are_each_announced()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app", "Reports.app");
        _f.Apps.OnOperationStatus = file => file == "Core.app" ? BcAppOperationStatus.Failed : BcAppOperationStatus.Succeeded;

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await SweepUntilQuietAsync();

        _f.Emails.Sent.Select(s => s.Subject).Should().Equal(
            "Failed: Install Core.app on CRONUS Denmark / Production", "Failed: Install Reports.app on CRONUS Denmark / Production");
    }

    // ── Ready to check (#1047) ──────────────────────────────────────────

    [Fact]
    public async Task The_assigned_checker_hears_once_when_the_environment_reaches_the_target()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var upgradeId = await PlanUpgradeAsync(projectId, envId, "27.6", assignee: UpgradeActionTestFixture.FlagUserId);

        (await _f.Worker().NotifyReadyToCheckAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(0,
            "the environment is still on 27.5");
        await SetVersionAsync(envId, "27.6.40000.0");
        await _f.Worker().NotifyReadyToCheckAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);
        await _f.Worker().NotifyReadyToCheckAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var sent = _f.Emails.Sent.Should().ContainSingle().Subject;
        sent.To.Should().Be("upgrade@example.com");
        sent.Subject.Should().Be("Ready to check: CRONUS Denmark / Production is on 27.6.40000.0");
        sent.Html.Should().Contain($"/upgrades/{upgradeId}");
    }

    [Fact]
    public async Task With_nobody_assigned_the_person_who_planned_the_upgrade_hears()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await PlanUpgradeAsync(projectId, envId, "27.6", assignee: null);
        await SetVersionAsync(envId, "27.6.40000.0");

        await _f.Worker().NotifyReadyToCheckAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var sent = _f.Emails.Sent.Should().ContainSingle().Subject;
        sent.To.Should().Be("owner@example.com");
        sent.Html.Should().Contain("Nobody is assigned");
    }

    [Fact]
    public async Task A_checker_who_cannot_see_a_private_solution_is_not_told_it_is_ready()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await PlanUpgradeAsync(projectId, envId, "27.6", assignee: UpgradeActionTestFixture.FlagUserId);
        await SetVersionAsync(envId, "27.6.40000.0");
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeProjects.Where(p => p.Id == projectId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Visibility, ProjectVisibility.Private));
            await ctx.TeamMembers.Where(m => m.UserId == UpgradeActionTestFixture.FlagUserId).ExecuteDeleteAsync();
        }

        await NotifyReadyAsync();

        _f.Emails.Sent.Should().BeEmpty();
        await using var verify = _f.Db.NewContext();
        (await verify.UserNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_line_on_a_closed_upgrade_sends_nothing()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var upgradeId = await PlanUpgradeAsync(projectId, envId, "27.6", assignee: UpgradeActionTestFixture.FlagUserId);
        await SetVersionAsync(envId, "27.6.40000.0");
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeEnvironmentUpgradeLines.Where(l => l.UpgradeId == upgradeId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsOpen, false));
        }

        await _f.Worker().NotifyReadyToCheckAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        _f.Emails.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Checking_the_environment_marks_its_ready_to_check_notice_read()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var upgradeId = await PlanUpgradeAsync(projectId, envId, "27.6", assignee: UpgradeActionTestFixture.FlagUserId);
        await SetVersionAsync(envId, "27.6.40000.0");
        await NotifyReadyAsync();
        var lineId = await ReadyNoticeLineAsync(upgradeId);

        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).SetCheckedAsync(lineId, true);
        }

        await using var verify = _f.Db.NewContext();
        (await verify.UserNotifications.AsNoTracking().SingleAsync()).ReadAt.Should().NotBeNull(
            "the check it asked for is done, whoever did it and from wherever");
    }

    [Fact]
    public async Task Marking_the_upgrade_done_marks_its_ready_to_check_notices_read()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var upgradeId = await PlanUpgradeAsync(projectId, envId, "27.6", assignee: UpgradeActionTestFixture.FlagUserId);
        await SetVersionAsync(envId, "27.6.40000.0");
        await NotifyReadyAsync();
        await ReadyNoticeLineAsync(upgradeId);

        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).CloseAsync(upgradeId);
        }

        await using var verify = _f.Db.NewContext();
        (await verify.UserNotifications.AsNoTracking().SingleAsync()).ReadAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData("reassign")]
    [InlineData("take off")]
    [InlineData("delete")]
    public async Task A_line_nobody_is_asked_to_check_any_more_marks_its_notice_read(string how)
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var upgradeId = await PlanUpgradeAsync(projectId, envId, "27.6", assignee: UpgradeActionTestFixture.FlagUserId);
        await SetVersionAsync(envId, "27.6.40000.0");
        await NotifyReadyAsync();
        var lineId = await ReadyNoticeLineAsync(upgradeId);

        await using (var ctx = _f.Db.NewContext())
        {
            var upgrades = _f.Upgrades(ctx);
            await (how switch
            {
                "reassign" => upgrades.AssignAsync(lineId, UpgradeActionTestFixture.PlainTeamUserId),
                "take off" => upgrades.RemoveLineAsync(lineId),
                _ => upgrades.DeleteAsync(upgradeId),
            });
        }

        await using var verify = _f.Db.NewContext();
        (await verify.UserNotifications.AsNoTracking().SingleAsync()).ReadAt.Should().NotBeNull();
    }

    /// <summary>The one line's id, after checking its unread notice names it.</summary>
    private async Task<int> ReadyNoticeLineAsync(int upgradeId)
    {
        await using var ctx = _f.Db.NewContext();
        var lineId = await ctx.OeEnvironmentUpgradeLines.Where(l => l.UpgradeId == upgradeId).Select(l => l.Id).SingleAsync();
        var notice = await ctx.UserNotifications.AsNoTracking().SingleAsync();
        notice.Subject.Should().Be(NotificationSubject.UpgradeLine(lineId));
        notice.ReadAt.Should().BeNull();
        return lineId;
    }

    [Fact]
    public async Task A_line_already_checked_sends_nothing()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var upgradeId = await PlanUpgradeAsync(projectId, envId, "27.6", assignee: UpgradeActionTestFixture.FlagUserId);
        await SetVersionAsync(envId, "27.6.40000.0");
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeEnvironmentUpgradeLines.Where(l => l.UpgradeId == upgradeId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.CheckedAt, DateTime.UtcNow));
        }

        await NotifyReadyAsync();

        _f.Emails.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_binned_solution_or_a_deleted_environment_sends_nothing()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await PlanUpgradeAsync(projectId, envId, "27.6", assignee: UpgradeActionTestFixture.FlagUserId);
        await SetVersionAsync(envId, "27.6.40000.0");
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeProjectEnvironments.Where(e => e.Id == envId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.SoftDeletedOn, DateTime.UtcNow));
        }
        await NotifyReadyAsync();
        _f.Emails.Sent.Should().BeEmpty("Business Central deleted the environment");

        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeProjectEnvironments.Where(e => e.Id == envId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.SoftDeletedOn, (DateTime?)null));
            await ctx.OeProjects.Where(p => p.Id == projectId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedAt, DateTime.UtcNow));
        }
        await NotifyReadyAsync();
        _f.Emails.Sent.Should().BeEmpty("the solution is in the bin");
    }

    [Fact]
    public async Task An_environment_still_updating_waits_until_it_is_done()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await PlanUpgradeAsync(projectId, envId, "27.6", assignee: UpgradeActionTestFixture.FlagUserId);
        await SetVersionAsync(envId, "27.6.40000.0");
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeProjectEnvironments.Where(e => e.Id == envId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.BcNextUpdateStatus, "Running"));
        }

        await NotifyReadyAsync();
        _f.Emails.Sent.Should().BeEmpty("the page still shows it as Running");

        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeProjectEnvironments.Where(e => e.Id == envId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.BcNextUpdateStatus, (string?)null));
        }
        await NotifyReadyAsync();
        _f.Emails.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Someone_assigned_after_the_notice_hears_too()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var upgradeId = await PlanUpgradeAsync(projectId, envId, "27.6", assignee: null);
        await SetVersionAsync(envId, "27.6.40000.0");
        await NotifyReadyAsync();

        int lineId;
        await using (var ctx = _f.Db.NewContext())
        {
            lineId = await ctx.OeEnvironmentUpgradeLines.Where(l => l.UpgradeId == upgradeId).Select(l => l.Id).SingleAsync();
        }
        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).AssignAsync(lineId, UpgradeActionTestFixture.FlagUserId);
        }
        await NotifyReadyAsync();

        _f.Emails.Sent.Select(s => s.To).Should().Equal("owner@example.com", "upgrade@example.com");
    }

    [Fact]
    public async Task Changing_the_target_announces_the_new_one()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var upgradeId = await PlanUpgradeAsync(projectId, envId, "27.0", assignee: UpgradeActionTestFixture.FlagUserId);
        await NotifyReadyAsync();
        _f.Emails.Sent.Should().ContainSingle("27.5 is already past the mistyped target");

        await AsOwnerAsync(ctx => _f.Upgrades(ctx).UpdateDetailsAsync(upgradeId, "Spring release", "27.6", null, null));
        await NotifyReadyAsync();
        _f.Emails.Sent.Should().ContainSingle("the environment has not reached 27.6 yet");

        await SetVersionAsync(envId, "27.6.40000.0");
        await NotifyReadyAsync();
        _f.Emails.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_line_that_reached_the_target_while_the_upgrade_was_done_is_not_announced_on_reopen()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var upgradeId = await PlanUpgradeAsync(projectId, envId, "27.6", assignee: UpgradeActionTestFixture.FlagUserId);
        await AsUpgradeManagerAsync(ctx => _f.Upgrades(ctx).CloseAsync(upgradeId));
        await SetVersionAsync(envId, "27.6.40000.0");

        await AsUpgradeManagerAsync(ctx => _f.Upgrades(ctx).ReopenAsync(upgradeId));

        await using (var ctx = _f.Db.NewContext())
        {
            var line = await ctx.OeEnvironmentUpgradeLines.AsNoTracking().SingleAsync(l => l.UpgradeId == upgradeId);
            line.IsOpen.Should().BeTrue();
            line.UpdatedNotifiedAt.Should().NotBeNull("reaching the target while closed is old news");
        }
        (await NotifyReadyAsync()).Should().Be(0);
        _f.Emails.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_line_still_short_of_the_target_on_reopen_is_announced_when_it_gets_there()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var upgradeId = await PlanUpgradeAsync(projectId, envId, "27.6", assignee: UpgradeActionTestFixture.FlagUserId);
        await AsUpgradeManagerAsync(ctx => _f.Upgrades(ctx).CloseAsync(upgradeId));
        await AsUpgradeManagerAsync(ctx => _f.Upgrades(ctx).ReopenAsync(upgradeId));

        await using (var ctx = _f.Db.NewContext())
        {
            (await ctx.OeEnvironmentUpgradeLines.AsNoTracking().SingleAsync(l => l.UpgradeId == upgradeId))
                .UpdatedNotifiedAt.Should().BeNull("the environment is still on 27.5");
        }
        await SetVersionAsync(envId, "27.6.40000.0");
        await NotifyReadyAsync();
        _f.Emails.Sent.Should().ContainSingle().Which.To.Should().Be("upgrade@example.com");
    }

    /// <summary>The fixture's default user, who holds the environment-updates grant on the team.</summary>
    private async Task AsUpgradeManagerAsync(Func<AppDbContext, Task> act)
    {
        await using var ctx = _f.Db.NewContext();
        await act(ctx);
    }

    private Task<int> NotifyReadyAsync() =>
        _f.Worker().NotifyReadyToCheckAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

    private async Task AsOwnerAsync(Func<AppDbContext, Task> act)
    {
        var before = _f.Db.OrgContext.CurrentUserId;
        _f.Db.OrgContext.CurrentUserId = UpgradeActionTestFixture.OwnerUserId;
        try
        {
            await using var ctx = _f.Db.NewContext();
            await act(ctx);
        }
        finally
        {
            _f.Db.OrgContext.CurrentUserId = before;
        }
    }

    private async Task<int> PlanUpgradeAsync(int projectId, int envId, string target, int? assignee)
    {
        await using var ctx = _f.Db.NewContext();
        var upgrade = new OeEnvironmentUpgrade
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "Spring release", TargetVersion = target,
            CreatedByUserId = UpgradeActionTestFixture.OwnerUserId, CreatedBy = "owner <owner@example.com>", CreatedAt = DateTime.UtcNow,
        };
        ctx.OeEnvironmentUpgrades.Add(upgrade);
        await ctx.SaveChangesAsync();
        ctx.OeEnvironmentUpgradeLines.Add(new OeEnvironmentUpgradeLine
        {
            OrganizationId = TestDb.DefaultOrgId, UpgradeId = upgrade.Id, EnvironmentId = envId, ProjectId = projectId,
            AssigneeUserId = assignee, AddedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
        return upgrade.Id;
    }

    private async Task SetVersionAsync(int envId, string version)
    {
        await using var ctx = _f.Db.NewContext();
        await ctx.OeProjectEnvironments.Where(e => e.Id == envId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Version, version));
    }

    // ── Booked uploads ──────────────────────────────────────────────────
    // Apps somebody was handed, booked for a picked time or a window: the package waits
    // in the row, the sweep sends it and waits for Business Central to finish, and
    // whichever way the row settles - sent, failed, or cancelled - the package goes.

    [Fact]
    public async Task A_due_upload_is_sent_from_its_stored_package_installed_to_the_end_and_the_package_is_dropped()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = (await BookUploadAsync(projectId, envId, hoursAhead: 12, "Partner.app"))[0];

        _f.Clock.Advance(TimeSpan.FromHours(13));
        var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        ran.Should().Be(1);
        _f.Apps.Installed.Should().NotBeNull();
        _f.Apps.Installed!.Value.FileName.Should().Be("Partner.app");
        _f.Apps.Installed.Value.Bytes.Should().Equal(new byte[] { 7, 8, 9 });
        _f.Apps.Installed.Value.Schedule.Should().Be(ALDevToolbox.Domain.ValueObjects.ObjectExplorer.BcDeploymentSchedule.Immediate,
            "the slot decided the time, and Business Central installs on arrival");

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Sent);
        stored.Outcome.Should().Contain("Partner.app was installed");
        stored.PackageFileName.Should().Be("Partner.app", "the history still names the file");
        stored.PackageContent.Should().BeNull("a sent package has no reason to stay in the database");
        stored.BcOperationId.Should().NotBeNull("the operation is stamped as soon as Business Central accepts the package, for a restart to find");
        stored.BcAppId.Should().NotBeNull();
    }

    [Fact]
    public async Task An_upload_business_central_fails_to_install_is_a_failed_row_in_its_words_without_its_package()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = (await BookUploadAsync(projectId, envId, hoursAhead: 12, "Partner.app"))[0];
        _f.Apps.OnOperationStatus = _ => BcAppOperationStatus.Failed;

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Failed);
        stored.Outcome.Should().Contain("Partner.app wasn't installed").And.Contain("Continia Core");
        stored.PackageContent.Should().BeNull("a failed booking is not retried, so the package would only sit there");
    }

    [Fact]
    public async Task A_batch_installs_one_app_at_a_time_in_its_order_each_after_the_one_before_finished()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var ids = await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app", "Connector.app", "Reports.app");

        // A poll answers "still running" until the app before it has been asked about
        // twice, so an install that started before its predecessor finished would show.
        var polls = new Dictionary<string, int>();
        _f.Apps.OnOperationStatus = file =>
        {
            polls[file] = polls.GetValueOrDefault(file) + 1;
            return polls[file] >= 2 ? BcAppOperationStatus.Succeeded : BcAppOperationStatus.Running;
        };
        _f.Clock.Advance(TimeSpan.FromHours(13));
        var first = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);
        first.Should().Be(1, "a sweep sends one upload per organisation and waits for it; the batch resumes next sweep");
        var ran = first + await SweepUntilQuietAsync();

        ran.Should().Be(3);
        _f.Apps.InstalledFiles.Should().Equal("Core.app", "Connector.app", "Reports.app");
        foreach (var id in ids)
        {
            var row = await _f.ReadActionAsync(id);
            row.Status.Should().Be(UpgradeActionStatus.Sent);
            row.PackageContent.Should().BeNull();
        }
    }

    [Fact]
    public async Task When_an_app_in_a_batch_fails_the_ones_after_it_are_not_tried()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var ids = await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app", "Connector.app", "Reports.app");
        _f.Apps.OnOperationStatus = file => file == "Core.app" ? BcAppOperationStatus.Failed : BcAppOperationStatus.Succeeded;

        _f.Clock.Advance(TimeSpan.FromHours(13));
        var sent = await SweepUntilQuietAsync();

        sent.Should().Be(1, "only the first app was sent; the rest were settled on the way, in the same sweep");
        _f.Apps.InstalledFiles.Should().Equal(new[] { "Core.app" }, "the apps after the failed one never reach Business Central");
        (await _f.ReadActionAsync(ids[0])).Status.Should().Be(UpgradeActionStatus.Failed);
        var second = await _f.ReadActionAsync(ids[1]);
        second.Status.Should().Be(UpgradeActionStatus.Failed);
        second.Outcome.Should().Contain("Core.app before it");
        second.PackageContent.Should().BeNull();
        (await _f.ReadActionAsync(ids[2])).Outcome.Should().Contain("Core.app before it");
    }

    [Fact]
    public async Task Cancelling_one_app_of_a_batch_lets_the_rest_go_ahead()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var ids = await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app", "Reports.app");

        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using (var cancelCtx = _f.Db.NewContext())
            await _f.Svc(cancelCtx).CancelUpgradeActionAsync(ids[0]);

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await SweepUntilQuietAsync();

        _f.Apps.InstalledFiles.Should().Equal(new[] { "Reports.app" }, "a cancel is the person's choice, not a failure that blocks the rest");
        (await _f.ReadActionAsync(ids[0])).PackageContent.Should().BeNull();
    }

    [Fact]
    public async Task Install_now_moves_a_whole_batch_to_the_next_sweep_keeping_its_order()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var ids = await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app", "Reports.app");

        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using (var ctx = _f.Db.NewContext())
            await _f.Svc(ctx).RunBookedInstallNowAsync(ids[1]);

        _f.Apps.InstalledFiles.Should().BeEmpty("nothing is sent from the page request");
        var ran = await SweepUntilQuietAsync();
        ran.Should().Be(2);
        _f.Apps.InstalledFiles.Should().Equal(new[] { "Core.app", "Reports.app" }, "the second app cannot jump ahead of the one it was booked to follow");
    }

    [Fact]
    public async Task Install_now_refuses_a_booking_that_is_no_longer_waiting()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = (await BookUploadAsync(projectId, envId, hoursAhead: 12, "Partner.app"))[0];
        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using (var cancelCtx = _f.Db.NewContext())
            await _f.Svc(cancelCtx).CancelUpgradeActionAsync(actionId);

        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Svc(ctx).RunBookedInstallNowAsync(actionId);

        await act.Should().ThrowAsync<ALDevToolbox.Domain.ValueObjects.PlanValidationException>();
    }

    [Fact]
    public async Task The_first_sweep_after_a_restart_drops_any_package_left_on_a_settled_row()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = (await BookUploadAsync(projectId, envId, hoursAhead: 12, "Partner.app"))[0];
        // A row settled by a path that forgot the package: not one of ours today, but
        // the sweep is the guarantee that no such row keeps 50 MB for good.
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeEnvironmentUpgradeActions.Where(a => a.Id == actionId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, UpgradeActionStatus.Sent));
        }

        await _f.Worker().FailInterruptedAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        (await _f.ReadActionAsync(actionId)).PackageContent.Should().BeNull();
    }

    [Fact]
    public async Task A_platform_update_due_in_the_same_sweep_goes_before_any_upload()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await BookUploadAsync(projectId, envId, hoursAhead: 12, "Partner.app");
        // Booked later, due at the same slot: the date move must not queue behind a
        // ten-minute install.
        var update = await BookAsync(projectId, envId, hoursAhead: 12);

        _f.Clock.Advance(TimeSpan.FromHours(13));
        var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        ran.Should().Be(2, "the update and then one upload, in that order, in one sweep");
        _f.Admin.Writes.Should().Be(1);
        (await _f.ReadActionAsync(update)).Status.Should().Be(UpgradeActionStatus.Sent);
        _f.Apps.InstalledFiles.Should().Equal("Partner.app");
    }

    [Fact]
    public async Task A_few_failed_polls_in_a_row_do_not_fail_an_install_business_central_is_still_running()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = (await BookUploadAsync(projectId, envId, hoursAhead: 12, "Partner.app"))[0];
        _f.Apps.PollErrorsBeforeAnswer = BcAppOperationPoller.MaxConsecutivePollErrors - 1;

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Sent, "a throttled read says nothing about the install");
        _f.Apps.Polls.Should().Be(BcAppOperationPoller.MaxConsecutivePollErrors);
    }

    [Fact]
    public async Task A_run_of_failed_polls_is_asked_about_again_on_the_next_sweep_before_the_batch_goes_on()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var ids = await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app", "Reports.app");
        _f.Apps.PollErrorsBeforeAnswer = BcAppOperationPoller.MaxConsecutivePollErrors;

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var core = await _f.ReadActionAsync(ids[0]);
        core.Status.Should().Be(UpgradeActionStatus.Sent, "Business Central has the app; only the answer is missing");
        core.Outcome.Should().Contain("Core.app was uploaded").And.Contain("wasn't confirmed here").And.NotContain("wasn't installed");
        core.PackageContent.Should().BeNull();
        core.ConfirmationDue.Should().BeTrue();

        // The next sweep asks again, and that takes its install turn.
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);
        core = await _f.ReadActionAsync(ids[0]);
        core.Outcome.Should().Contain("Core.app was installed");
        core.ConfirmationDue.Should().BeFalse();
        _f.Apps.InstalledFiles.Should().Equal(new[] { "Core.app" }, "the app is never sent twice, and Reports.app waits for Core.app's answer");

        await SweepUntilQuietAsync();
        _f.Apps.InstalledFiles.Should().Equal("Core.app", "Reports.app");
    }

    [Fact]
    public async Task An_install_is_asked_about_again_only_once_and_then_the_batch_goes_on()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var ids = await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app", "Reports.app");
        // Enough errors to leave both the live poll and the second look without an answer.
        _f.Apps.PollErrorsBeforeAnswer = 2 * BcAppOperationPoller.MaxConsecutivePollErrors;

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var core = await _f.ReadActionAsync(ids[0]);
        core.Status.Should().Be(UpgradeActionStatus.Sent);
        core.Outcome.Should().Contain("Core.app was uploaded").And.Contain("wasn't confirmed here");
        core.ConfirmationDue.Should().BeFalse("a row is asked about once, so it never keeps the worker busy for ever");
        _f.Apps.Polls.Should().Be(2 * BcAppOperationPoller.MaxConsecutivePollErrors);

        await SweepUntilQuietAsync();
        _f.Apps.InstalledFiles.Should().Equal("Core.app", "Reports.app");
        (await _f.ReadActionAsync(ids[0])).Outcome.Should().Contain("wasn't confirmed here", "the second look was the last");
    }

    [Fact]
    public async Task A_wait_that_runs_out_is_unconfirmed_too_not_a_failure()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = (await BookUploadAsync(projectId, envId, hoursAhead: 12, "Partner.app"))[0];
        _f.Apps.OnOperationStatus = _ => BcAppOperationStatus.Running;
        _f.UploadPollTimeout = TimeSpan.Zero;

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Sent);
        stored.Outcome.Should().Contain("still installing").And.Contain("wasn't confirmed here");
    }

    [Fact]
    public async Task The_error_count_starts_over_after_a_poll_that_answers()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = (await BookUploadAsync(projectId, envId, hoursAhead: 12, "Partner.app"))[0];
        // Three errors, an answer of "running", three more errors, then done: never four in a row.
        var limit = BcAppOperationPoller.MaxConsecutivePollErrors - 1;
        var answered = 0;
        _f.Apps.PollErrorsBeforeAnswer = limit;
        _f.Apps.OnOperationStatus = _ =>
        {
            answered++;
            if (answered == 1) _f.Apps.PollErrorsBeforeAnswer = limit;
            return answered == 1 ? BcAppOperationStatus.Running : BcAppOperationStatus.Succeeded;
        };

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Sent);
        stored.Outcome.Should().Contain("was installed");
        _f.Apps.Polls.Should().Be(2 * limit + 2);
    }

    [Fact]
    public async Task A_long_install_keeps_the_workers_heartbeat_alive()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await BookUploadAsync(projectId, envId, hoursAhead: 12, "Partner.app");
        // Every poll of the install is a tick, so a ten-minute install never reads as a
        // loop that has stopped.
        var polls = 0;
        _f.Apps.OnOperationStatus = _ =>
        {
            _f.Clock.Advance(TimeSpan.FromMinutes(2));
            return ++polls < 4 ? BcAppOperationStatus.Running : BcAppOperationStatus.Succeeded;
        };
        var heartbeats = new WorkerHeartbeatRegistry(_f.Clock);

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker(heartbeats).RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var heartbeat = heartbeats.All().Single();
        heartbeat.LastTickUtc.Should().BeCloseTo(_f.Clock.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(2),
            "the last poll ticked it, minutes after the sweep began");
    }

    [Fact]
    public async Task A_restart_while_an_app_is_installing_asks_business_central_how_it_ended_and_the_rest_of_its_batch_goes()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var ids = await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app", "Reports.app");
        _f.Clock.Advance(TimeSpan.FromHours(13));
        await InterruptMidInstallAsync(ids[0], _f.Apps.Accepted("Core.app"));

        await _f.Worker().FailInterruptedAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);
        var core = await _f.ReadActionAsync(ids[0]);
        core.Status.Should().Be(UpgradeActionStatus.Sent, "Business Central had the app and went on installing it");
        core.PackageContent.Should().BeNull();
        core.ConfirmationDue.Should().BeTrue();

        await SweepUntilQuietAsync();
        core = await _f.ReadActionAsync(ids[0]);
        core.Status.Should().Be(UpgradeActionStatus.Sent);
        core.Outcome.Should().Be("Core.app was installed on Production.");
        core.ConfirmationDue.Should().BeFalse();
        _f.Apps.InstalledFiles.Should().Equal(new[] { "Reports.app" }, "Core.app was asked about, never sent again");
    }

    [Fact]
    public async Task A_restart_while_an_app_is_installing_finds_a_failed_install_and_stops_the_rest_of_its_batch()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var ids = await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app", "Reports.app");
        _f.Apps.OnOperationStatus = name => name == "Core.app" ? BcAppOperationStatus.Failed : BcAppOperationStatus.Succeeded;
        _f.Clock.Advance(TimeSpan.FromHours(13));
        await InterruptMidInstallAsync(ids[0], _f.Apps.Accepted("Core.app"));

        await _f.Worker().FailInterruptedAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);
        await SweepUntilQuietAsync();

        var core = await _f.ReadActionAsync(ids[0]);
        core.Status.Should().Be(UpgradeActionStatus.Failed, "the history says what Business Central said, not 'check it yourself'");
        core.Outcome.Should().Contain("Core.app wasn't installed").And.Contain("Continia Core 28.0.0.0");
        _f.Apps.InstalledFiles.Should().BeEmpty();
        var reports = await _f.ReadActionAsync(ids[1]);
        reports.Status.Should().Be(UpgradeActionStatus.Failed);
        reports.Outcome.Should().Contain("Core.app before it");
    }

    [Fact]
    public async Task A_restart_whose_install_gets_no_answer_stays_unconfirmed_and_the_rest_of_its_batch_goes()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var ids = await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app", "Reports.app");
        _f.Apps.PollErrorsBeforeAnswer = BcAppOperationPoller.MaxConsecutivePollErrors;
        _f.Clock.Advance(TimeSpan.FromHours(13));
        await InterruptMidInstallAsync(ids[0], _f.Apps.Accepted("Core.app"));

        await _f.Worker().FailInterruptedAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);
        await SweepUntilQuietAsync();

        var core = await _f.ReadActionAsync(ids[0]);
        core.Status.Should().Be(UpgradeActionStatus.Sent);
        core.Outcome.Should().Contain("Core.app was uploaded").And.Contain("wasn't confirmed here");
        core.ConfirmationDue.Should().BeFalse();
        _f.Apps.InstalledFiles.Should().Equal(new[] { "Reports.app" }, "an unconfirmed predecessor does not stop the batch; Business Central refuses a dependent if it is missing");
    }

    [Fact]
    public async Task A_restart_whose_solution_is_gone_records_the_install_as_unconfirmed_not_failed()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var ids = await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app");
        _f.Clock.Advance(TimeSpan.FromHours(13));
        await InterruptMidInstallAsync(ids[0], _f.Apps.Accepted("Core.app"));
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeProjects.Where(p => p.Id == projectId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedAt, DateTime.UtcNow));
        }

        await _f.Worker().FailInterruptedAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);
        await SweepUntilQuietAsync();

        var core = await _f.ReadActionAsync(ids[0]);
        core.Status.Should().Be(UpgradeActionStatus.Sent, "losing the connection says nothing about an install Business Central ran regardless");
        core.Outcome.Should().Contain("Core.app was uploaded").And.Contain("couldn't reach the environment");
        core.ConfirmationDue.Should().BeFalse();
        _f.Apps.Polls.Should().Be(0);
    }

    [Fact]
    public async Task Asking_about_an_install_again_keeps_the_workers_heartbeat_alive()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var ids = await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app");
        _f.Clock.Advance(TimeSpan.FromHours(13));
        await InterruptMidInstallAsync(ids[0], _f.Apps.Accepted("Core.app"));
        var polls = 0;
        _f.Apps.OnOperationStatus = _ =>
        {
            _f.Clock.Advance(TimeSpan.FromMinutes(2));
            return ++polls < 4 ? BcAppOperationStatus.Running : BcAppOperationStatus.Succeeded;
        };
        var heartbeats = new WorkerHeartbeatRegistry(_f.Clock);
        var worker = _f.Worker(heartbeats);

        await worker.FailInterruptedAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);
        await worker.RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        (await _f.ReadActionAsync(ids[0])).Outcome.Should().Contain("Core.app was installed");
        heartbeats.All().Single().LastTickUtc.Should().BeCloseTo(_f.Clock.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(2),
            "the last poll ticked it, minutes after the sweep began");
    }

    [Fact]
    public async Task A_restart_before_the_upload_was_accepted_fails_the_row_and_the_rest_of_its_batch_is_not_tried()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var ids = await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app", "Reports.app");
        _f.Clock.Advance(TimeSpan.FromHours(13));
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeEnvironmentUpgradeActions.Where(a => a.Id == ids[0])
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.SentAt, _f.Clock.GetUtcNow().UtcDateTime));
        }

        await _f.Worker().FailInterruptedAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);
        (await _f.ReadActionAsync(ids[0])).Status.Should().Be(UpgradeActionStatus.Failed);

        await SweepUntilQuietAsync();
        _f.Apps.InstalledFiles.Should().BeEmpty();
        (await _f.ReadActionAsync(ids[1])).Outcome.Should().Contain("Core.app before it");
    }

    [Fact]
    public async Task A_predecessor_claimed_longer_ago_than_an_install_can_take_does_not_block_its_batch_for_ever()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var ids = await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app", "Reports.app");
        _f.Clock.Advance(TimeSpan.FromHours(13));
        // Claimed, and then its settling write was lost: pending with sent_at set, for good.
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeEnvironmentUpgradeActions.Where(a => a.Id == ids[0])
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.SentAt, _f.Clock.GetUtcNow().UtcDateTime));
        }

        (await SweepUntilQuietAsync()).Should().Be(0, "a predecessor that may still be installing is waited on");

        _f.Clock.Advance(ProjectConnectionService.DefaultUploadPollTimeout + TimeSpan.FromMinutes(6));
        await SweepUntilQuietAsync();
        _f.Apps.InstalledFiles.Should().Equal("Reports.app");
    }

    [Fact]
    public async Task Calling_off_or_starting_a_booked_upload_is_a_managers_write_not_an_outsiders()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var ids = await BookUploadAsync(projectId, envId, hoursAhead: 12, "Core.app", "Reports.app");

        // A Public solution is everyone's to manage; a Private one is its owner's, its
        // admins' and its teams'. Someone outside all of those: refused on both, with
        // nothing changed.
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeProjects.Where(p => p.Id == projectId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Visibility, ProjectVisibility.Private));
        }
        _f.ActAs(UpgradeActionTestFixture.OutsiderUserId);
        await using (var ctx = _f.Db.NewContext())
        {
            var act = () => _f.Svc(ctx).CancelUpgradeActionAsync(ids[0]);
            await act.Should().ThrowAsync<ProjectAccessDeniedException>();
            var now = () => _f.Svc(ctx).RunBookedInstallNowAsync(ids[1]);
            await now.Should().ThrowAsync<ProjectAccessDeniedException>();
        }
        (await _f.ReadActionAsync(ids[0])).Status.Should().Be(UpgradeActionStatus.Pending);
        (await _f.ReadActionAsync(ids[1])).ExecuteAfter.Should().BeAfter(_f.Clock.GetUtcNow().UtcDateTime);

        // The solution's owner manages it without holding the update team's grant: the
        // same check that made the booking lets them call it off.
        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using (var ctx = _f.Db.NewContext())
            await _f.Svc(ctx).CancelUpgradeActionAsync(ids[0]);
        (await _f.ReadActionAsync(ids[0])).Status.Should().Be(UpgradeActionStatus.Cancelled);
    }

    // ── Booked AppSource updates (#1001) ────────────────────────────────

    private static readonly Guid CoreAppId = Guid.NewGuid();
    private static readonly Guid SystemAppId = Guid.NewGuid();

    /// <summary>Continia Core 28.5.0.1 waiting, and waiting for Continia System Application unless told otherwise.</summary>
    private void Waiting(string version = "28.5.0.1", params BcAppUpdateRequirement[] requirements) =>
        _f.Apps.OnAvailable = () => new[] { new BcAvailableAppUpdate(CoreAppId, "Continia Core", "Continia Software", version, requirements) };

    private static BcAppUpdateRequirement SystemApp() =>
        new(SystemAppId, "Continia System Application", "Continia Software", "28.5.0.0", "update");

    [Fact]
    public async Task A_booked_app_update_is_sent_to_run_now_when_due_and_waited_for()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        Waiting(requirements: SystemApp());
        var actionId = await BookUpdateAsync(projectId, envId, hoursAhead: 12, SystemAppId);

        _f.Clock.Advance(TimeSpan.FromHours(1));
        (await SweepUntilQuietAsync()).Should().Be(0, "its slot has not come");

        _f.Clock.Advance(TimeSpan.FromHours(12));
        var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        ran.Should().Be(1);
        _f.Apps.Updated.Should().Equal((CoreAppId, "28.5.0.1", false, true));
        _f.Apps.Polls.Should().BeGreaterThan(0, "the worker waits for the update like an install");
        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Sent);
        stored.Outcome.Should().Contain("Continia Core was updated to 28.5.0.1");
        stored.BcAppId.Should().Be(CoreAppId);
        stored.BcOperationId.Should().NotBeNull("the operation is stamped before the poll, as for an upload");
    }

    [Fact]
    public async Task A_booked_update_takes_its_turn_behind_an_upload_due_in_the_same_sweep()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        Waiting();
        await BookUploadAsync(projectId, envId, hoursAhead: 12, "Partner.app");
        await BookUpdateAsync(projectId, envId, hoursAhead: 12);

        _f.Clock.Advance(TimeSpan.FromHours(13));
        var first = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        first.Should().Be(1, "a sweep runs one install at a time: two at once can deadlock in Business Central");
        _f.Apps.InstalledFiles.Should().Equal("Partner.app");
        await SweepUntilQuietAsync();
        _f.Apps.InstalledFiles.Should().Equal("Partner.app", $"update {CoreAppId}");
    }

    [Fact]
    public async Task Two_booked_updates_due_together_go_one_per_sweep()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var otherAppId = Guid.NewGuid();
        _f.Apps.OnAvailable = () => new[]
        {
            new BcAvailableAppUpdate(CoreAppId, "Continia Core", "Continia Software", "28.5.0.1", Array.Empty<BcAppUpdateRequirement>()),
            new BcAvailableAppUpdate(otherAppId, "Continia Banking", "Continia Software", "28.5.0.2", Array.Empty<BcAppUpdateRequirement>()),
        };
        await BookUpdateAsync(projectId, envId, hoursAhead: 12);
        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using (var ctx = _f.Db.NewContext())
            await _f.Connections(ctx).BookAppUpdateAsync(projectId, envId, otherAppId, "28.5.0.2",
                ALDevToolbox.Domain.ValueObjects.ObjectExplorer.UploadAppTiming.AtTime, _f.Clock.GetUtcNow().AddHours(12));

        _f.Clock.Advance(TimeSpan.FromHours(13));
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);
        _f.Apps.Updated.Should().ContainSingle();
        await SweepUntilQuietAsync();
        _f.Apps.Updated.Select(u => u.AppId).Should().Equal(CoreAppId, otherAppId);
    }

    // Several updates booked together (the operations team's "these tonight"): one
    // batch, the app another waits for first, and a failure stops only its dependents.

    private static readonly Guid BankingAppId = Guid.NewGuid();

    /// <summary>Continia Core waiting for Continia System Application, and an unrelated Continia Banking.</summary>
    private void WaitingThree() => _f.Apps.OnAvailable = () => new[]
    {
        new BcAvailableAppUpdate(CoreAppId, "Continia Core", "Continia Software", "28.5.0.1", new[] { SystemApp() }),
        new BcAvailableAppUpdate(BankingAppId, "Continia Banking", "Continia Software", "28.5.0.2", Array.Empty<BcAppUpdateRequirement>()),
        new BcAvailableAppUpdate(SystemAppId, "Continia System Application", "Continia Software", "28.5.0.0", Array.Empty<BcAppUpdateRequirement>()),
    };

    /// <summary>Books Core, Banking and System Application together, in that (wrong) order, as the owner.</summary>
    private async Task BookThreeAsync(int projectId, int environmentId)
    {
        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using var ctx = _f.Db.NewContext();
        await _f.Connections(ctx).BookAppUpdatesAsync(projectId, environmentId, new[]
            {
                new AppUpdatePick(CoreAppId, "28.5.0.1", new[] { SystemAppId }),
                new AppUpdatePick(BankingAppId, "28.5.0.2", Array.Empty<Guid>()),
                new AppUpdatePick(SystemAppId, "28.5.0.0", Array.Empty<Guid>()),
            },
            ALDevToolbox.Domain.ValueObjects.ObjectExplorer.UploadAppTiming.AtTime, _f.Clock.GetUtcNow().AddHours(12));
    }

    [Fact]
    public async Task Updates_booked_together_go_one_at_a_time_with_the_app_another_waits_for_first()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        WaitingThree();
        await BookThreeAsync(projectId, envId);

        _f.Clock.Advance(TimeSpan.FromHours(13));
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None))
            .Should().Be(1, "one install per sweep, whether booked alone or together");
        await SweepUntilQuietAsync();

        _f.Apps.Updated.Select(u => u.AppId).Should().Equal(new[] { SystemAppId, CoreAppId, BankingAppId },
            "Core waits for System Application, so that goes first; Banking keeps its place after Core");
    }

    [Fact]
    public async Task A_failed_update_in_a_batch_stops_only_the_apps_that_wait_for_it()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        WaitingThree();
        await BookThreeAsync(projectId, envId);
        _f.Apps.OnOperationStatus = name => name == $"update {SystemAppId}" ? BcAppOperationStatus.Failed : BcAppOperationStatus.Succeeded;

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await SweepUntilQuietAsync();

        _f.Apps.Updated.Select(u => u.AppId).Should().Equal(new[] { SystemAppId, BankingAppId },
            "Core waits for the app that failed; Banking does not, so it still goes");
        await using var read = _f.Db.NewContext();
        var core = await read.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.BcAppId == CoreAppId);
        core.Status.Should().Be(UpgradeActionStatus.Failed);
        core.Outcome.Should().Be("Continia Core wasn't updated, because Continia System Application, which it waits for, wasn't updated.");
        (await read.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.BcAppId == BankingAppId))
            .Status.Should().Be(UpgradeActionStatus.Sent);
    }

    [Fact]
    public async Task Two_updates_sharing_an_unticked_prerequisite_stop_after_the_first_fails()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        _f.Apps.OnAvailable = () => new[]
        {
            new BcAvailableAppUpdate(CoreAppId, "Continia Core", "Continia Software", "28.5.0.1", new[] { SystemApp() }),
            new BcAvailableAppUpdate(BankingAppId, "Continia Banking", "Continia Software", "28.5.0.2", new[] { SystemApp() }),
        };
        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using (var ctx = _f.Db.NewContext())
            await _f.Connections(ctx).BookAppUpdatesAsync(projectId, envId, new[]
                {
                    new AppUpdatePick(CoreAppId, "28.5.0.1", new[] { SystemAppId }),
                    new AppUpdatePick(BankingAppId, "28.5.0.2", new[] { SystemAppId }),
                },
                ALDevToolbox.Domain.ValueObjects.ObjectExplorer.UploadAppTiming.AtTime, _f.Clock.GetUtcNow().AddHours(12));
        _f.Apps.OnOperationStatus = _ => BcAppOperationStatus.Failed;

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await SweepUntilQuietAsync();

        _f.Apps.Updated.Select(u => u.AppId).Should().Equal(new[] { CoreAppId },
            "Banking would ask for the same prerequisite that just failed");
        await using var read = _f.Db.NewContext();
        (await read.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.BcAppId == BankingAppId))
            .Outcome.Should().Contain("both of them wait for");
    }

    [Fact]
    public async Task An_update_the_workbench_could_not_see_finish_is_recorded_as_sent_unconfirmed()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        Waiting();
        var actionId = await BookUpdateAsync(projectId, envId, hoursAhead: 12);
        _f.Apps.OnOperationStatus = _ => BcAppOperationStatus.Running;
        _f.UploadPollTimeout = TimeSpan.Zero;

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Sent);
        stored.Outcome.Should().Contain("Continia Core was sent for update to 28.5.0.1").And.Contain("wasn't confirmed here");
        stored.ConfirmationDue.Should().BeTrue("an update is asked about again like an upload");
    }

    [Fact]
    public async Task A_booked_update_is_refused_in_plain_words_when_the_waiting_version_changed()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        Waiting();
        var actionId = await BookUpdateAsync(projectId, envId, hoursAhead: 12);
        Waiting("28.6.0.0");

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        _f.Apps.Updated.Should().BeEmpty();
        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Failed);
        stored.Outcome.Should().Contain("Continia Core wasn't updated to 28.5.0.1").And.Contain("28.6.0.0");
    }

    [Fact]
    public async Task A_booked_update_is_refused_when_business_central_now_wants_an_app_nobody_agreed_to()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        Waiting();
        var actionId = await BookUpdateAsync(projectId, envId, hoursAhead: 12);
        Waiting(requirements: SystemApp());

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        _f.Apps.Updated.Should().BeEmpty();
        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Failed);
        stored.Outcome.Should().Contain("Continia System Application");
    }

    [Fact]
    public async Task Install_now_and_cancel_work_on_a_booked_update_for_whoever_manages_the_solution()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        Waiting();
        var actionId = await BookUpdateAsync(projectId, envId, hoursAhead: 12);

        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using (var ctx = _f.Db.NewContext())
            await _f.Svc(ctx).RunBookedInstallNowAsync(actionId);
        (await _f.ReadActionAsync(actionId)).ExecuteAfter.Should().Be(_f.Clock.GetUtcNow().UtcDateTime);

        await using (var ctx = _f.Db.NewContext())
            await _f.Svc(ctx).CancelUpgradeActionAsync(actionId);
        (await _f.ReadActionAsync(actionId)).Status.Should().Be(UpgradeActionStatus.Cancelled);
        (await SweepUntilQuietAsync()).Should().Be(0);
        _f.Apps.Updated.Should().BeEmpty();
    }

    [Fact]
    public async Task A_booked_update_is_listed_with_the_scheduled_installs_and_not_as_a_platform_update_booking()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        Waiting();
        var actionId = await BookUpdateAsync(projectId, envId, hoursAhead: 12);

        await using var ctx = _f.Db.NewContext();
        var installs = await _f.Svc(ctx).ListBookedInstallsAsync(projectId, envId);
        installs.Should().ContainSingle(r => r.Id == actionId && r.AppName == "Continia Core" && r.AppId == CoreAppId);
        (await _f.Svc(ctx).ListPendingAsync()).Should().NotContain(r => r.Id == actionId,
            "the Upgrades page must not read an app update as a booked platform move");
    }

    [Fact]
    public async Task A_restart_while_an_update_runs_asks_business_central_how_it_ended()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        Waiting();
        var actionId = await BookUpdateAsync(projectId, envId, hoursAhead: 12);
        _f.Clock.Advance(TimeSpan.FromHours(13));
        var (_, operationId) = _f.Apps.Accepted("update Continia Core");
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeEnvironmentUpgradeActions.Where(a => a.Id == actionId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.SentAt, _f.Clock.GetUtcNow().UtcDateTime)
                    .SetProperty(a => a.BcOperationId, operationId));
        }

        await _f.Worker().FailInterruptedAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Sent);
        stored.Outcome.Should().Contain("Continia Core was sent for update").And.Contain("restarted before it could confirm");
        stored.ConfirmationDue.Should().BeTrue("the booked app id and the stamped operation are all a second look needs");

        await SweepUntilQuietAsync();
        stored = await _f.ReadActionAsync(actionId);
        stored.Outcome.Should().Be("Continia Core was updated to 28.5.0.1 on Production.");
        _f.Apps.Updated.Should().BeEmpty("the update was asked about, never sent again");
    }

    [Fact]
    public async Task Install_now_is_only_for_app_installs()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var update = await BookAsync(projectId, envId, hoursAhead: 12);

        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Svc(ctx).RunBookedInstallNowAsync(update);

        (await act.Should().ThrowAsync<ALDevToolbox.Domain.ValueObjects.PlanValidationException>())
            .Which.Errors.Values.Should().ContainMatch("*booked for a later install*");
        (await _f.ReadActionAsync(update)).ExecuteAfter.Should().BeAfter(_f.Clock.GetUtcNow().UtcDateTime);
    }

    // ── A window chose the time (#1539) ─────────────────────────────────
    // The deliveries rule (#1124) for booked installs: a time a window chose runs inside
    // that window or waits for its next opening. The fixture's clock starts on Friday
    // 2026-09-11 at 08:00 UTC, 10:00 in Copenhagen.

    [Fact]
    public async Task A_window_booked_batch_that_outlasts_its_window_moves_the_rest_to_the_next_opening()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        // 22:00-00:00 in Copenhagen (summer time) is 20:00-22:00 UTC.
        await SetWindowsAsync(envId, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        var ids = await BookInWindowAsync(projectId, envId, UploadAppTiming.DeliveryWindow, "Core.app", "Reports.app");
        var opening = new DateTime(2026, 9, 11, 20, 0, 0, DateTimeKind.Utc);
        foreach (var id in ids)
        {
            var row = await _f.ReadActionAsync(id);
            row.ScheduledByWindow.Should().Be(UploadAppTiming.DeliveryWindow, "the row remembers that a window chose its time");
            row.ExecuteAfter.Should().Be(opening);
        }

        // Inside the window: the first app goes.
        _f.Clock.Advance(TimeSpan.FromHours(12));
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);

        // The window has closed before the second app's turn came.
        _f.Clock.Advance(TimeSpan.FromMinutes(150));
        var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        ran.Should().Be(0);
        _f.Apps.InstalledFiles.Should().Equal(new[] { "Core.app" }, "nothing is installed into the customer's working day");
        var second = await _f.ReadActionAsync(ids[1]);
        second.Status.Should().Be(UpgradeActionStatus.Pending);
        second.SentAt.Should().BeNull();
        second.ExecuteAfter.Should().Be(opening.AddDays(1), "it waits for the window's next opening");
        second.PackageContent.Should().NotBeNull("the package is still needed for the next opening");

        // And goes at that opening.
        _f.Clock.Advance(opening.AddDays(1) - _f.Clock.GetUtcNow().UtcDateTime);
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);
        _f.Apps.InstalledFiles.Should().Equal("Core.app", "Reports.app");
    }

    [Fact]
    public async Task An_update_booked_for_microsofts_window_that_comes_up_after_it_closed_waits_for_the_next_opening()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        // 01:00-05:00 in Copenhagen (summer time) is 23:00-03:00 UTC.
        await SetWindowsAsync(envId, bcStart: new TimeOnly(1, 0), bcEnd: new TimeOnly(5, 0), bcZone: "Europe/Copenhagen");
        Waiting();
        await AsOwnerAsync(ctx => _f.Connections(ctx).BookAppUpdateAsync(projectId, envId, CoreAppId, "28.5.0.1", UploadAppTiming.BcUpdateWindow));
        await using (var read = _f.Db.NewContext())
        {
            var booked = await read.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.EnvironmentId == envId);
            booked.ScheduledByWindow.Should().Be(UploadAppTiming.BcUpdateWindow);
            booked.ExecuteAfter.Should().Be(new DateTime(2026, 9, 11, 23, 0, 0, DateTimeKind.Utc));
        }

        // 06:00 in Copenhagen the next morning: the window closed an hour ago.
        _f.Clock.Advance(new DateTime(2026, 9, 12, 4, 0, 0, DateTimeKind.Utc) - _f.Clock.GetUtcNow().UtcDateTime);
        var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        ran.Should().Be(0);
        _f.Apps.Updated.Should().BeEmpty();
        await using var after = _f.Db.NewContext();
        var row = await after.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.EnvironmentId == envId);
        row.Status.Should().Be(UpgradeActionStatus.Pending);
        row.ExecuteAfter.Should().Be(new DateTime(2026, 9, 12, 23, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task A_picked_time_outside_the_window_still_fires()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await SetWindowsAsync(envId, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        // 11:00 in Copenhagen, well outside the window: the person chose it.
        var actionId = (await BookUploadAsync(projectId, envId, hoursAhead: 1, "Partner.app"))[0];
        (await _f.ReadActionAsync(actionId)).ScheduledByWindow.Should().BeNull();

        _f.Clock.Advance(TimeSpan.FromHours(3));
        var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        ran.Should().Be(1, "a picked time runs when the person said, even late");
        _f.Apps.InstalledFiles.Should().Equal(new[] { "Partner.app" });
    }

    [Fact]
    public async Task Install_now_on_a_window_booking_is_not_held_back_by_the_window()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await SetWindowsAsync(envId, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        var ids = await BookInWindowAsync(projectId, envId, UploadAppTiming.DeliveryWindow, "Core.app", "Reports.app");

        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using (var ctx = _f.Db.NewContext())
            await _f.Svc(ctx).RunBookedInstallNowAsync(ids[0]);

        foreach (var id in ids)
        {
            (await _f.ReadActionAsync(id)).ScheduledByWindow.Should().BeNull("the person has picked the time now");
        }
        (await SweepUntilQuietAsync()).Should().Be(2);
        _f.Apps.InstalledFiles.Should().Equal("Core.app", "Reports.app");
    }

    [Fact]
    public async Task A_single_booking_keeps_its_place_ahead_of_later_batches_when_its_window_moves_it_to_the_next_opening()
    {
        // One organisation: a single urgent app booked first, then a three-app batch, both
        // for tonight's delivery window, 22:00-00:00 in Copenhagen (20:00-22:00 UTC).
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await SetWindowsAsync(envId, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        var single = (await BookInWindowAsync(projectId, envId, UploadAppTiming.DeliveryWindow, "Urgent.app")).Single();
        _f.Clock.Advance(TimeSpan.FromMinutes(5));
        await BookInWindowAsync(projectId, envId, UploadAppTiming.DeliveryWindow, "X1.app", "X2.app", "X3.app");
        (await _f.ReadActionAsync(single)).BatchOrder.Should().BeNull("a single booking has no batch");

        // Nothing got a turn before the window closed (00:30 in Copenhagen): everything moves.
        var opening = new DateTime(2026, 9, 11, 20, 0, 0, DateTimeKind.Utc);
        _f.Clock.Advance(opening.AddMinutes(150) - _f.Clock.GetUtcNow().UtcDateTime);
        (await SweepUntilQuietAsync()).Should().Be(0);
        _f.Apps.InstalledFiles.Should().BeEmpty();
        (await _f.ReadActionAsync(single)).ExecuteAfter.Should().Be(opening.AddDays(1));

        // Another batch is booked for the new night after the move.
        await BookInWindowAsync(projectId, envId, UploadAppTiming.DeliveryWindow, "Y1.app");

        // At the next opening the single booking goes first, then the batch booked before
        // the new one, then the new one.
        _f.Clock.Advance(opening.AddDays(1) - _f.Clock.GetUtcNow().UtcDateTime);
        for (var i = 0; i < 5; i++)
        {
            (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);
        }
        _f.Apps.InstalledFiles.Should().Equal(new[] { "Urgent.app", "X1.app", "X2.app", "X3.app", "Y1.app" },
            "the move to the next opening keeps each booking's place in the queue");
    }

    [Fact]
    public async Task A_single_booking_due_with_a_batch_booked_after_it_goes_first()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await SetWindowsAsync(envId, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        await BookInWindowAsync(projectId, envId, UploadAppTiming.DeliveryWindow, "Urgent.app");
        _f.Clock.Advance(TimeSpan.FromMinutes(5));
        await BookInWindowAsync(projectId, envId, UploadAppTiming.DeliveryWindow, "X1.app", "X2.app");

        // Both are due at the same opening, 22:00 in Copenhagen (20:00 UTC).
        _f.Clock.Advance(new DateTime(2026, 9, 11, 20, 10, 0, DateTimeKind.Utc) - _f.Clock.GetUtcNow().UtcDateTime);
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);

        _f.Apps.InstalledFiles.Should().Equal(new[] { "Urgent.app" },
            "a single booking is not sorted behind every batch due at the same time");
    }

    [Fact]
    public async Task Install_now_goes_on_the_next_sweep_ahead_of_a_long_batch_already_running_for_another_customer()
    {
        // One organisation, as in single-tenant hosting: a batch for one customer came due
        // first and is running, one app per sweep.
        var (xProject, xEnv) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (wProject, wEnv) = await _f.SeedCustomerAsync("CRONUS Sweden");
        await BookUploadAsync(xProject, xEnv, hoursAhead: 1, "X1.app", "X2.app", "X3.app", "X4.app");
        var urgent = (await BookUploadAsync(wProject, wEnv, hoursAhead: 12, "Hotfix.app")).Single();
        _f.Clock.Advance(TimeSpan.FromHours(2));
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);

        // Twenty minutes in, someone presses Install now on another customer's booking.
        _f.Clock.Advance(TimeSpan.FromMinutes(20));
        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using (var ctx = _f.Db.NewContext())
            await _f.Svc(ctx).RunBookedInstallNowAsync(urgent);

        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);
        _f.Apps.InstalledFiles.Should().Equal(new[] { "X1.app", "Hotfix.app" },
            "Install now waits for at most the one install already going, not for the rest of another customer's batch");

        await SweepUntilQuietAsync();
        _f.Apps.InstalledFiles.Should().Equal(new[] { "X1.app", "Hotfix.app", "X2.app", "X3.app", "X4.app" },
            "the batch then carries on in its own order");
    }

    [Fact]
    public async Task An_upload_for_now_goes_on_the_next_sweep_ahead_of_a_long_batch_already_running_for_another_customer()
    {
        var (xProject, xEnv) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (wProject, wEnv) = await _f.SeedCustomerAsync("CRONUS Sweden");
        await BookUploadAsync(xProject, xEnv, hoursAhead: 1, "X1.app", "X2.app", "X3.app");
        _f.Clock.Advance(TimeSpan.FromHours(2));
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);

        await AsOwnerAsync(ctx => _f.Connections(ctx).InstallUploadedAppsAsync(
            wProject, wEnv, new[] { new UploadPackage(new byte[] { 7, 8, 9 }, "Hotfix.app") }, UploadAppTiming.Now));
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);

        _f.Apps.InstalledFiles.Should().Equal(new[] { "X1.app", "Hotfix.app" });
    }

    [Fact]
    public async Task Two_batches_for_one_environment_do_not_interleave_when_another_environment_is_due_too()
    {
        // The turn rotates between environments, never between bookings on one: a later
        // upload to the same environment may need an app the earlier one brings.
        var (xProject, xEnv) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (wProject, wEnv) = await _f.SeedCustomerAsync("CRONUS Sweden");
        await BookUploadAsync(xProject, xEnv, hoursAhead: 1, "A1.app", "A2.app");
        _f.Clock.Advance(TimeSpan.FromMinutes(1));
        await BookUploadAsync(xProject, xEnv, hoursAhead: 1, "B1.app");
        await BookUploadAsync(wProject, wEnv, hoursAhead: 1, "W1.app");
        _f.Clock.Advance(TimeSpan.FromHours(2));

        await SweepUntilQuietAsync();

        _f.Apps.InstalledFiles.Should().Equal("A1.app", "W1.app", "A2.app", "B1.app");
    }

    [Fact]
    public async Task Two_solutions_on_one_tenant_share_its_environment_so_an_add_on_does_not_install_before_its_base()
    {
        // A base solution and an add-on solution that needs it, both connected to the one
        // customer tenant: two environment rows, one Business Central "Production" (#1608).
        var (baseProject, baseEnv) = await _f.SeedCustomerAsync("CRONUS Base");
        var (addOnProject, addOnEnv) = await _f.SeedCustomerAsync("CRONUS Add-on");
        var (wProject, wEnv) = await _f.SeedCustomerAsync("CRONUS Sweden");
        await ShareTenantAsync(baseProject, addOnProject, addOnEnv, "PRODUCTION");

        // The base solution's row had the latest install there; the add-on's row never had one.
        await BookUploadAsync(baseProject, baseEnv, hoursAhead: 1, "Base0.app");
        _f.Clock.Advance(TimeSpan.FromHours(2));
        await SweepUntilQuietAsync();

        // Base v2 is booked first, then the add-on that needs it, then another customer's app.
        await BookUploadAsync(baseProject, baseEnv, hoursAhead: 1, "Base1.app", "Base2.app");
        _f.Clock.Advance(TimeSpan.FromMinutes(1));
        await BookUploadAsync(addOnProject, addOnEnv, hoursAhead: 1, "AddOn.app");
        await BookUploadAsync(wProject, wEnv, hoursAhead: 1, "W1.app");
        _f.Clock.Advance(TimeSpan.FromHours(2));

        await SweepUntilQuietAsync();

        _f.Apps.InstalledFiles.Should().Equal(new[] { "Base0.app", "W1.app", "Base1.app", "Base2.app", "AddOn.app" },
            "the turn is the environment's in Business Central, so bookings for it keep their order whichever solution made them");
    }

    [Fact]
    public async Task A_window_batch_that_has_started_keeps_the_turn_so_a_closing_window_leaves_one_customer_part_installed()
    {
        var (dProject, dEnv) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (sProject, sEnv) = await _f.SeedCustomerAsync("CRONUS Sweden");
        // 22:00-00:00 in Copenhagen (summer time) is 20:00-22:00 UTC.
        await SetWindowsAsync(dEnv, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        await SetWindowsAsync(sEnv, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        await BookInWindowAsync(dProject, dEnv, UploadAppTiming.DeliveryWindow, "D1.app", "D2.app", "D3.app");
        _f.Clock.Advance(TimeSpan.FromMinutes(1));
        var sIds = await BookInWindowAsync(sProject, sEnv, UploadAppTiming.DeliveryWindow, "S1.app", "S2.app", "S3.app");
        var opening = new DateTime(2026, 9, 11, 20, 0, 0, DateTimeKind.Utc);

        _f.Clock.Advance(opening - _f.Clock.GetUtcNow().UtcDateTime);
        for (var i = 0; i < 3; i++)
        {
            (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);
        }
        _f.Apps.InstalledFiles.Should().Equal(new[] { "D1.app", "D2.app", "D3.app" },
            "a batch a window chose the time for runs to its end once it has started");

        // The window closes before the other customer's turn: its batch moves whole.
        _f.Clock.Advance(TimeSpan.FromMinutes(150));
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(0);
        _f.Apps.InstalledFiles.Should().HaveCount(3);
        foreach (var id in sIds)
        {
            var row = await _f.ReadActionAsync(id);
            row.Status.Should().Be(UpgradeActionStatus.Pending);
            row.ExecuteAfter.Should().Be(opening.AddDays(1), "no app of an unstarted batch went in, so all of it waits for the next opening");
        }
    }

    [Fact]
    public async Task Install_now_still_goes_ahead_of_a_window_batch_that_has_started_for_another_customer()
    {
        var (dProject, dEnv) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (wProject, wEnv) = await _f.SeedCustomerAsync("CRONUS Sweden");
        await SetWindowsAsync(dEnv, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        await BookInWindowAsync(dProject, dEnv, UploadAppTiming.DeliveryWindow, "D1.app", "D2.app", "D3.app");
        var urgent = (await BookUploadAsync(wProject, wEnv, hoursAhead: 24, "Hotfix.app")).Single();
        _f.Clock.Advance(new DateTime(2026, 9, 11, 20, 0, 0, DateTimeKind.Utc) - _f.Clock.GetUtcNow().UtcDateTime);
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);

        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using (var ctx = _f.Db.NewContext())
            await _f.Svc(ctx).RunBookedInstallNowAsync(urgent);
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);
        _f.Apps.InstalledFiles.Should().Equal(new[] { "D1.app", "Hotfix.app" },
            "a started batch keeps the turn from other window bookings, not from an Install now");

        await SweepUntilQuietAsync();
        _f.Apps.InstalledFiles.Should().Equal("D1.app", "Hotfix.app", "D2.app", "D3.app");
    }

    [Fact]
    public async Task Install_now_behind_an_earlier_window_booking_on_its_environment_still_takes_the_turn_from_a_started_batch()
    {
        var (dProject, dEnv) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (wProject, wEnv) = await _f.SeedCustomerAsync("CRONUS Sweden");
        await SetWindowsAsync(dEnv, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        await SetWindowsAsync(wEnv, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        await BookInWindowAsync(dProject, dEnv, UploadAppTiming.DeliveryWindow, "D1.app", "D2.app", "D3.app");
        _f.Clock.Advance(new DateTime(2026, 9, 11, 20, 0, 0, DateTimeKind.Utc) - _f.Clock.GetUtcNow().UtcDateTime);
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);

        // Booked once the batch has started, so on their own they would wait for it (#1625).
        await BookInWindowAsync(wProject, wEnv, UploadAppTiming.DeliveryWindow, "WWindow.app");
        var urgent = (await BookUploadAsync(wProject, wEnv, hoursAhead: 24, "Hotfix.app"))
            .Last();

        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using (var ctx = _f.Db.NewContext())
            await _f.Svc(ctx).RunBookedInstallNowAsync(urgent);
        await SweepUntilQuietAsync();

        _f.Apps.InstalledFiles.Should().Equal(new[] { "D1.app", "WWindow.app", "Hotfix.app", "D2.app", "D3.app" },
            "the Install now gives its environment the turn, and the booking ahead of it there still goes first");
    }

    [Fact]
    public async Task A_started_window_batch_keeps_the_turn_when_another_booking_on_its_environment_sorts_ahead_of_it()
    {
        var (dProject, dEnv) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (sProject, sEnv) = await _f.SeedCustomerAsync("CRONUS Sweden");
        await SetWindowsAsync(dEnv, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        await SetWindowsAsync(sEnv, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        await BookInWindowAsync(dProject, dEnv, UploadAppTiming.DeliveryWindow, "D1.app", "D2.app", "D3.app");
        _f.Clock.Advance(TimeSpan.FromMinutes(1));
        await BookInWindowAsync(sProject, sEnv, UploadAppTiming.DeliveryWindow, "S1.app", "S2.app");
        _f.Clock.Advance(new DateTime(2026, 9, 11, 20, 0, 0, DateTimeKind.Utc) - _f.Clock.GetUtcNow().UtcDateTime);
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);

        // A single booking for the same window on the running customer, booked before
        // either batch (as one moved past last night's window would be), now sorts first there.
        var ids = await BookInWindowAsync(dProject, dEnv, UploadAppTiming.DeliveryWindow, "DLate.app");
        await using (var ctx = _f.Db.NewContext())
        {
            var late = await ctx.OeEnvironmentUpgradeActions.Where(a => a.PackageFileName == "DLate.app").Select(a => a.Id).SingleAsync();
            await ctx.OeEnvironmentUpgradeActions.Where(a => a.Id == late)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.RequestedAt, a => a.RequestedAt.AddDays(-1)));
        }
        for (var i = 0; i < 3; i++)
        {
            (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);
        }

        _f.Apps.InstalledFiles.Should().Equal(new[] { "D1.app", "DLate.app", "D2.app", "D3.app" },
            "the environment with a started batch keeps the turn, whichever of its rows comes first");
    }

    // ── A single window booking is not held behind another customer's batch (#1625) ──
    // One install at a time; the fixture's clock starts on Friday 2026-09-11 at 08:00 UTC.
    // 22:00 in Copenhagen (summer time) is 20:00 UTC, 23:00 is 21:00 UTC.

    [Fact]
    public async Task A_single_window_booking_goes_ahead_of_a_batch_booked_after_it_for_a_customer_served_less_recently()
    {
        var (xProject, xEnv) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (wProject, wEnv) = await _f.SeedCustomerAsync("CRONUS Sweden");
        await SetWindowsAsync(xEnv, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        await SetWindowsAsync(wEnv, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        // The single booking's customer had an install this morning; the batch's customer none.
        await BookUploadAsync(wProject, wEnv, hoursAhead: 1, "W0.app");
        _f.Clock.Advance(TimeSpan.FromHours(2));
        (await SweepUntilQuietAsync()).Should().Be(1);

        await BookInWindowAsync(wProject, wEnv, UploadAppTiming.DeliveryWindow, "Urgent.app");
        _f.Clock.Advance(TimeSpan.FromMinutes(5));
        await BookInWindowAsync(xProject, xEnv, UploadAppTiming.DeliveryWindow, "X1.app", "X2.app", "X3.app");

        _f.Clock.Advance(new DateTime(2026, 9, 11, 20, 0, 0, DateTimeKind.Utc) - _f.Clock.GetUtcNow().UtcDateTime);
        for (var i = 0; i < 2; i++)
        {
            (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);
        }

        _f.Apps.InstalledFiles.Should().Equal(new[] { "W0.app", "Urgent.app", "X1.app" },
            "a single window booking ranks with started batches, ahead of a batch not yet started, whatever the rotation says");
    }

    [Fact]
    public async Task A_single_window_booking_moved_to_the_next_night_does_not_wait_again_behind_another_customers_started_batch()
    {
        var (xProject, xEnv) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (wProject, wEnv) = await _f.SeedCustomerAsync("CRONUS Sweden");
        await SetWindowsAsync(xEnv, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        await SetWindowsAsync(wEnv, deliveryStart: new TimeOnly(23, 0), deliveryEnd: new TimeOnly(0, 0));
        // The urgent app is booked first; the other customer's batch five minutes later.
        var urgent = (await BookInWindowAsync(wProject, wEnv, UploadAppTiming.DeliveryWindow, "Urgent.app")).Single();
        _f.Clock.Advance(TimeSpan.FromMinutes(5));
        await BookInWindowAsync(xProject, xEnv, UploadAppTiming.DeliveryWindow, "X1.app", "X2.app", "X3.app", "X4.app");
        var friday = new DateTime(2026, 9, 11, 20, 0, 0, DateTimeKind.Utc);

        // Friday 22:00: the batch starts, the single booking's window has not opened yet.
        _f.Clock.Advance(friday - _f.Clock.GetUtcNow().UtcDateTime);
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);

        // Nothing more runs before both windows close: everything left moves to Saturday.
        _f.Clock.Advance(friday.AddMinutes(150) - _f.Clock.GetUtcNow().UtcDateTime);
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(0);
        (await _f.ReadActionAsync(urgent)).ExecuteAfter.Should().Be(friday.AddDays(1).AddHours(1));

        // Saturday 22:00: the batch carries on. 23:00: the single booking is due and takes
        // the next turn, though the batch started the night before.
        _f.Clock.Advance(friday.AddDays(1) - _f.Clock.GetUtcNow().UtcDateTime);
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);
        _f.Clock.Advance(TimeSpan.FromHours(1));
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);
        _f.Apps.InstalledFiles.Should().Equal(new[] { "X1.app", "X2.app", "Urgent.app" },
            "a single booking cannot be left part-installed, so it does not wait for the rest of a batch booked after it");

        await SweepUntilQuietAsync();
        _f.Apps.InstalledFiles.Should().Equal("X1.app", "X2.app", "Urgent.app", "X3.app", "X4.app");
    }

    [Fact]
    public async Task A_single_window_booking_behind_an_unstarted_batch_on_its_environment_does_not_start_that_batch_ahead_of_a_started_one()
    {
        var (dProject, dEnv) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (sProject, sEnv) = await _f.SeedCustomerAsync("CRONUS Sweden");
        await SetWindowsAsync(dEnv, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        await SetWindowsAsync(sEnv, deliveryStart: new TimeOnly(23, 0), deliveryEnd: new TimeOnly(0, 0));
        await BookInWindowAsync(sProject, sEnv, UploadAppTiming.DeliveryWindow, "S1.app", "S2.app");
        _f.Clock.Advance(TimeSpan.FromMinutes(1));
        await BookInWindowAsync(sProject, sEnv, UploadAppTiming.DeliveryWindow, "SSingle.app");
        _f.Clock.Advance(TimeSpan.FromMinutes(1));
        await BookInWindowAsync(dProject, dEnv, UploadAppTiming.DeliveryWindow, "D1.app", "D2.app", "D3.app");
        var opening = new DateTime(2026, 9, 11, 20, 0, 0, DateTimeKind.Utc);

        _f.Clock.Advance(opening - _f.Clock.GetUtcNow().UtcDateTime);
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);
        _f.Clock.Advance(TimeSpan.FromHours(1));
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);

        _f.Apps.InstalledFiles.Should().Equal(new[] { "D1.app", "D2.app" },
            "the single booking's turn would send the first app of the batch ahead of it, leaving a second customer part-installed");

        await SweepUntilQuietAsync();
        _f.Apps.InstalledFiles.Should().Equal("D1.app", "D2.app", "D3.app", "S1.app", "S2.app", "SSingle.app");
    }

    [Fact]
    public async Task An_older_single_queued_behind_a_started_batch_does_not_lend_the_batch_its_booking_time()
    {
        var (xProject, xEnv) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (wProject, wEnv) = await _f.SeedCustomerAsync("CRONUS Sweden");
        await SetWindowsAsync(xEnv, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        await SetWindowsAsync(wEnv, deliveryStart: new TimeOnly(23, 0), deliveryEnd: new TimeOnly(0, 0));
        await BookInWindowAsync(wProject, wEnv, UploadAppTiming.DeliveryWindow, "Urgent.app");
        // A single on the batch's environment, booked a day earlier but due later tonight.
        await BookInWindowAsync(xProject, xEnv, UploadAppTiming.DeliveryWindow, "XOld.app");
        var opening = new DateTime(2026, 9, 11, 20, 0, 0, DateTimeKind.Utc);
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeEnvironmentUpgradeActions.Where(a => a.PackageFileName == "XOld.app")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.RequestedAt, a => a.RequestedAt.AddDays(-1))
                    .SetProperty(a => a.ExecuteAfter, opening.AddMinutes(30)));
        }
        _f.Clock.Advance(TimeSpan.FromMinutes(5));
        await BookInWindowAsync(xProject, xEnv, UploadAppTiming.DeliveryWindow, "X1.app", "X2.app", "X3.app", "X4.app");

        _f.Clock.Advance(opening - _f.Clock.GetUtcNow().UtcDateTime);
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);
        _f.Clock.Advance(TimeSpan.FromHours(1));
        for (var i = 0; i < 3; i++)
        {
            (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);
        }

        _f.Apps.InstalledFiles.Should().Equal(new[] { "X1.app", "Urgent.app", "X2.app", "X3.app" },
            "the batch's place is the booking time of the row its turn would send, not of an older row queued behind it");
        await SweepUntilQuietAsync();
        _f.Apps.InstalledFiles.Should().Equal("X1.app", "Urgent.app", "X2.app", "X3.app", "X4.app", "XOld.app");
    }

    [Fact]
    public async Task A_skipped_single_does_not_hand_its_turn_to_an_unstarted_batch_behind_it_while_another_batch_is_part_installed()
    {
        var (dProject, dEnv) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (sProject, sEnv) = await _f.SeedCustomerAsync("CRONUS Sweden");
        await SetWindowsAsync(dEnv, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0));
        // Sweden: the delivery window runs to midnight, Microsoft's window closes at 22:30.
        await SetWindowsAsync(sEnv, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(0, 0),
            bcStart: new TimeOnly(22, 0), bcEnd: new TimeOnly(22, 30), bcZone: "Europe/Copenhagen");
        await BookInWindowAsync(dProject, dEnv, UploadAppTiming.DeliveryWindow, "D1.app", "D2.app", "D3.app");
        var opening = new DateTime(2026, 9, 11, 20, 0, 0, DateTimeKind.Utc);
        _f.Clock.Advance(opening - _f.Clock.GetUtcNow().UtcDateTime);
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);

        // A single for Microsoft's window, booked before the batch (as one moved from last
        // night would be), then a batch for the delivery window queued behind it.
        await BookInWindowAsync(sProject, sEnv, UploadAppTiming.BcUpdateWindow, "SSingle.app");
        await using (var ctx = _f.Db.NewContext())
        {
            await ctx.OeEnvironmentUpgradeActions.Where(a => a.PackageFileName == "SSingle.app")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.RequestedAt, a => a.RequestedAt.AddDays(-1))
                    .SetProperty(a => a.ExecuteAfter, opening));
        }
        await BookInWindowAsync(sProject, sEnv, UploadAppTiming.DeliveryWindow, "S1.app", "S2.app");

        // 22:45: the single's window closed before its turn, so it moves to tomorrow.
        _f.Clock.Advance(TimeSpan.FromMinutes(45));
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(1);

        _f.Apps.InstalledFiles.Should().Equal(new[] { "D1.app", "D2.app" },
            "the batch behind the skipped single would leave a second customer part-installed");
        await using var read = _f.Db.NewContext();
        (await read.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.PackageFileName == "SSingle.app"))
            .ExecuteAfter.Should().BeAfter(_f.Clock.GetUtcNow().UtcDateTime, "the single waits for its window's next opening");
    }

    [Fact]
    public void Solutions_with_no_tenant_yet_each_count_as_their_own_environment()
    {
        // A solution with no tenant cannot install yet, so this is the key on its own: two
        // such solutions never share a turn, and one tenant's environment ignores case.
        UpgradeActionWorker.EnvironmentKey(null, 5, "Production")
            .Should().NotBe(UpgradeActionWorker.EnvironmentKey(null, 6, "Production"));
        UpgradeActionWorker.EnvironmentKey(null, 5, "Production")
            .Should().Be(("solution:5", "PRODUCTION"));
        var tenant = Guid.NewGuid();
        UpgradeActionWorker.EnvironmentKey(tenant, 5, "Production")
            .Should().Be(UpgradeActionWorker.EnvironmentKey(tenant, 6, "PRODUCTION"));
    }

    [Fact]
    public async Task The_install_turn_rotates_between_organisations_so_a_long_batch_does_not_starve_another()
    {
        // This organisation booked two ten-app batches for 09:00...
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var batch = Enumerable.Range(1, 10).Select(i => $"A{i:00}.app").ToArray();
        await BookUploadAsync(projectId, envId, hoursAhead: 1, batch);
        await BookUploadAsync(projectId, envId, hoursAhead: 1, batch.Select(f => "B" + f).ToArray());
        // ...another booked one app for 10:00, as an active owner who may install there.
        var (otherProjectId, otherEnvId) = await _f.SeedOtherOrgCustomerAsync();
        await _f.InOtherOrgAsync(ctx => _f.Connections(ctx).InstallUploadedAppsAsync(
            otherProjectId, otherEnvId, new[] { new UploadPackage(new byte[] { 1, 2, 3 }, "Sweden.app") },
            UploadAppTiming.AtTime, _f.Clock.GetUtcNow().AddHours(2)));

        _f.Clock.Advance(TimeSpan.FromHours(3));
        for (var i = 0; i < 3; i++)
        {
            (await _f.Worker().RunOneUploadAsync(CancellationToken.None)).Should().BeTrue();
        }

        _f.Apps.InstalledFiles.Take(2).Should().Equal(new[] { "A01.app", "Sweden.app" },
            "the longest-waiting install goes first, then the organisation not yet served, not the rest of the twenty");
        _f.Apps.InstalledFiles.Should().HaveCount(3);
        _f.Apps.InstalledFiles[2].Should().NotBe("Sweden.app", "the turn comes back to the first organisation");
        await using var read = _f.Db.NewContext();
        var other = await read.OeEnvironmentUpgradeActions.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(a => a.EnvironmentId == otherEnvId);
        other.Status.Should().Be(UpgradeActionStatus.Sent);
        other.RequestedByUserId.Should().Be(UpgradeActionTestFixture.OtherOrgOwnerUserId);
    }

    /// <summary>Connects a second solution to the first one's tenant, its environment row naming the same environment in other letter case.</summary>
    private async Task ShareTenantAsync(int firstProjectId, int secondProjectId, int secondEnvId, string secondEnvName)
    {
        await using var ctx = _f.Db.NewContext();
        var tenant = await ctx.OeProjects.Where(p => p.Id == firstProjectId).Select(p => p.BcTenantId).SingleAsync();
        await ctx.OeProjects.Where(p => p.Id == secondProjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.BcTenantId, tenant));
        await ctx.OeProjectEnvironments.Where(e => e.Id == secondEnvId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Name, secondEnvName));
    }

    /// <summary>Sets an environment's delivery window and Microsoft's update window, as the environment page and a refresh would.</summary>
    private async Task SetWindowsAsync(int envId,
        TimeOnly? deliveryStart = null, TimeOnly? deliveryEnd = null,
        TimeOnly? bcStart = null, TimeOnly? bcEnd = null, string? bcZone = null)
    {
        await using var ctx = _f.Db.NewContext();
        var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == envId);
        env.UpdateWindowStart = deliveryStart;
        env.UpdateWindowEnd = deliveryEnd;
        env.BcUpdateWindowStart = bcStart;
        env.BcUpdateWindowEnd = bcEnd;
        env.BcUpdateWindowTimeZoneIana = bcZone;
        if (bcStart is not null) env.BcUpdateWindowFetchedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
    }

    /// <summary>Books uploads for a window as the solution's owner. Returns the row ids in batch order.</summary>
    private async Task<List<int>> BookInWindowAsync(int projectId, int environmentId, UploadAppTiming timing, params string[] files)
    {
        await AsOwnerAsync(ctx => _f.Connections(ctx).InstallUploadedAppsAsync(
            projectId, environmentId, files.Select(f => new UploadPackage(new byte[] { 7, 8, 9 }, f)).ToList(), timing));
        await using var read = _f.Db.NewContext();
        return await read.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.EnvironmentId == environmentId && a.Kind == UpgradeActionKind.UploadApp)
            .OrderBy(a => a.BatchOrder).ThenBy(a => a.Id)
            .Select(a => a.Id).ToListAsync();
    }

    /// <summary>The shape a restart mid-install leaves: claimed, still pending, with the operation Business Central answered with.</summary>
    private async Task InterruptMidInstallAsync(int actionId, (Guid AppId, Guid OperationId) accepted)
    {
        await using var ctx = _f.Db.NewContext();
        await ctx.OeEnvironmentUpgradeActions.Where(a => a.Id == actionId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.SentAt, _f.Clock.GetUtcNow().UtcDateTime)
                .SetProperty(a => a.BcAppId, accepted.AppId)
                .SetProperty(a => a.BcOperationId, accepted.OperationId));
    }

    /// <summary>Sweeps until a sweep sends nothing, as the worker does every thirty seconds. Returns how many rows ran in all.</summary>
    private async Task<int> SweepUntilQuietAsync()
    {
        var total = 0;
        for (var i = 0; i < 10; i++)
        {
            var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);
            if (ran == 0) break;
            total += ran;
        }
        return total;
    }

    /// <summary>Books uploads as the solution's owner, who may manage it; the flag holder may not upload. Returns the row ids in batch order.</summary>
    private async Task<List<int>> BookUploadAsync(int projectId, int environmentId, int hoursAhead, params string[] files)
    {
        var acting = _f.Db.OrgContext.CurrentUserId;
        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        try
        {
            await using var ctx = _f.Db.NewContext();
            var packages = files.Select(f => new UploadPackage(new byte[] { 7, 8, 9 }, f)).ToList();
            await _f.Connections(ctx).InstallUploadedAppsAsync(
                projectId, environmentId, packages,
                ALDevToolbox.Domain.ValueObjects.ObjectExplorer.UploadAppTiming.AtTime,
                _f.Clock.GetUtcNow().AddHours(hoursAhead));
            await using var read = _f.Db.NewContext();
            return await read.OeEnvironmentUpgradeActions.AsNoTracking()
                .Where(a => a.EnvironmentId == environmentId && a.Kind == UpgradeActionKind.UploadApp)
                .OrderBy(a => a.BatchOrder).ThenBy(a => a.Id)
                .Select(a => a.Id).ToListAsync();
        }
        finally
        {
            _f.ActAs(acting);
        }
    }

    /// <summary>Books Continia Core's waiting update as the solution's owner, agreeing to <paramref name="prerequisites"/>. Returns the row id.</summary>
    private async Task<int> BookUpdateAsync(int projectId, int environmentId, int hoursAhead, params Guid[] prerequisites)
    {
        var acting = _f.Db.OrgContext.CurrentUserId;
        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        try
        {
            await using var ctx = _f.Db.NewContext();
            await _f.Connections(ctx).BookAppUpdateAsync(
                projectId, environmentId, CoreAppId, "28.5.0.1",
                ALDevToolbox.Domain.ValueObjects.ObjectExplorer.UploadAppTiming.AtTime,
                _f.Clock.GetUtcNow().AddHours(hoursAhead), prerequisites);
            await using var read = _f.Db.NewContext();
            return await read.OeEnvironmentUpgradeActions.AsNoTracking()
                .Where(a => a.EnvironmentId == environmentId && a.Kind == UpgradeActionKind.UpdateApp)
                .OrderByDescending(a => a.Id)
                .Select(a => a.Id).FirstAsync();
        }
        finally
        {
            _f.ActAs(acting);
        }
    }

    private async Task<int> BookAsync(int projectId, int environmentId, int hoursAhead)
    {
        await using var ctx = _f.Db.NewContext();
        var row = await _f.Svc(ctx).ScheduleUpgradeActionAsync(
            projectId, environmentId, UpgradeActionKind.RunNow,
            _f.Clock.GetUtcNow().AddHours(hoursAhead));
        return row.Id;
    }
}
