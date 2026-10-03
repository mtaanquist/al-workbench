using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
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

    // ── Notifications (#1046) ───────────────────────────────────────────
    // Whoever booked a change hears how it went, once, when it has settled.

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
    public async Task A_line_already_checked_or_on_a_closed_upgrade_sends_nothing()
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
            await act.Should().ThrowAsync<Exception>();
            var now = () => _f.Svc(ctx).RunBookedInstallNowAsync(ids[1]);
            await now.Should().ThrowAsync<Exception>();
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
