using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
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

    // ── Booked uploads ──────────────────────────────────────────────────
    // An app somebody was handed, booked for a picked time or the delivery window: the
    // package waits in the row, and whichever way the row settles - sent by the sweep,
    // sent now by hand, or cancelled - the package goes with it.

    [Fact]
    public async Task A_due_upload_is_sent_from_its_stored_package_and_the_package_is_dropped()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookUploadAsync(projectId, envId, hoursAhead: 12);

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
        stored.Outcome.Should().Contain("Partner.app");
        stored.PackageFileName.Should().Be("Partner.app", "the history still names the file");
        stored.PackageContent.Should().BeNull("a sent package has no reason to stay in the database");
    }

    [Fact]
    public async Task An_upload_business_central_refuses_at_its_slot_is_a_failed_row_without_its_package()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookUploadAsync(projectId, envId, hoursAhead: 12);
        _f.Apps.InstallThrows = new BcApiException(null, "It needs Continia Core 28.0.0.0, which isn't installed.");

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Failed);
        stored.Outcome.Should().Contain("Partner.app wasn't installed").And.Contain("Continia Core");
        stored.PackageContent.Should().BeNull("a failed booking is not retried, so the package would only sit there");
    }

    [Fact]
    public async Task Cancelling_a_booked_upload_drops_its_package_and_the_sweep_leaves_it_alone()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookUploadAsync(projectId, envId, hoursAhead: 12);

        await using (var cancelCtx = _f.Db.NewContext())
            await _f.Svc(cancelCtx).CancelUpgradeActionAsync(actionId);

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Cancelled);
        stored.PackageContent.Should().BeNull();

        _f.Clock.Advance(TimeSpan.FromHours(13));
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(0);
        _f.Apps.Installed.Should().BeNull();
    }

    [Fact]
    public async Task Install_now_sends_a_booked_upload_ahead_of_its_slot_and_settles_the_row()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookUploadAsync(projectId, envId, hoursAhead: 12);

        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using (var ctx = _f.Db.NewContext())
            await _f.Svc(ctx).RunUploadNowAsync(actionId);

        _f.Apps.Installed!.Value.FileName.Should().Be("Partner.app");
        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Sent);
        stored.PackageContent.Should().BeNull();

        // The slot arrives later; the row is settled, so the sweep must not send it twice.
        _f.Clock.Advance(TimeSpan.FromHours(13));
        (await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Install_now_refuses_a_booking_that_is_no_longer_waiting()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookUploadAsync(projectId, envId, hoursAhead: 12);
        await using (var cancelCtx = _f.Db.NewContext())
            await _f.Svc(cancelCtx).CancelUpgradeActionAsync(actionId);

        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Svc(ctx).RunUploadNowAsync(actionId);

        await act.Should().ThrowAsync<ALDevToolbox.Domain.ValueObjects.PlanValidationException>();
        _f.Apps.Installed.Should().BeNull();
    }

    [Fact]
    public async Task The_first_sweep_after_a_restart_drops_any_package_left_on_a_settled_row()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookUploadAsync(projectId, envId, hoursAhead: 12);
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

    /// <summary>Books an upload as the solution's owner, who may manage it; the flag holder may not upload.</summary>
    private async Task<int> BookUploadAsync(int projectId, int environmentId, int hoursAhead)
    {
        var acting = _f.Db.OrgContext.CurrentUserId;
        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        try
        {
            await using var ctx = _f.Db.NewContext();
            var outcome = await _f.Connections(ctx).InstallUploadedAppAsync(
                projectId, environmentId, new byte[] { 7, 8, 9 }, "Partner.app",
                ALDevToolbox.Domain.ValueObjects.ObjectExplorer.UploadAppTiming.AtTime,
                _f.Clock.GetUtcNow().AddHours(hoursAhead));
            outcome.IsBooked.Should().BeTrue();
            await using var read = _f.Db.NewContext();
            return await read.OeEnvironmentUpgradeActions.AsNoTracking()
                .Where(a => a.EnvironmentId == environmentId && a.Kind == UpgradeActionKind.UploadApp)
                .Select(a => a.Id).SingleAsync();
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
