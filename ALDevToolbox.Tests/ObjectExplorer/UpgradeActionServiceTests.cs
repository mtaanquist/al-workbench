using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The Stage 4b request contract (issue #657): asking for an upgrade action now or at an
/// agreed slot, cancelling one that hasn't fired, and reading an environment's history.
///
/// <para>The split that matters most here is <em>immediate is a direct send</em> — the
/// request thread calls Business Central and the row lands finished — while a future slot
/// writes a pending row and calls nobody. The tests below pin both halves by counting the
/// writes that reached the fake admin client.</para>
/// </summary>
public sealed class UpgradeActionServiceTests : IDisposable
{
    private readonly UpgradeActionTestFixture _f = new();

    public void Dispose() => _f.Dispose();

    // ── Immediate: a direct send ────────────────────────────────────────

    [Fact]
    public async Task An_immediate_action_reaches_Business_Central_and_is_recorded_as_sent()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();

        UpgradeActionRow row;
        await using (var ctx = _f.Db.NewContext())
        {
            row = await _f.Svc(ctx).ScheduleUpgradeActionAsync(
                projectId, envId, UpgradeActionKind.RunNow, executeAt: null);
        }

        _f.Admin.Writes.Should().Be(1, "'immediately' is a direct send, not a fast-scheduled row");
        _f.Admin.SelectedIgnoreUpdateWindow.Should().BeTrue();

        var stored = await _f.ReadActionAsync(row.Id);
        stored.Status.Should().Be(UpgradeActionStatus.Sent);
        stored.SentAt.Should().NotBeNull();
        stored.Outcome.Should().NotBeNullOrWhiteSpace();
        stored.RequestedBy.Should().Contain("Anna Jensen", "the feed names the person after their account is gone");
        stored.ExecuteAfter.Should().Be(stored.RequestedAt, "an immediate action fires when it is asked for");
    }

    [Fact]
    public async Task A_refused_immediate_action_is_recorded_as_failed_and_still_rethrows()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        _f.Admin.OnUpdates = Array.Empty<BcEnvironmentUpdate>;

        await using (var ctx = _f.Db.NewContext())
        {
            var act = () => _f.Svc(ctx).ScheduleUpgradeActionAsync(
                projectId, envId, UpgradeActionKind.RunNow, executeAt: null);
            await act.Should().ThrowAsync<PlanValidationException>(
                "the fleet page still shows the per-row message it always did");
        }

        await using var verify = _f.Db.NewContext();
        var stored = await verify.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync();
        stored.Status.Should().Be(UpgradeActionStatus.Failed);
        stored.Outcome.Should().Contain("No update", "the feed keeps the attempts that came to nothing");
        _f.Admin.Writes.Should().Be(0);
    }

    // ── Scheduled: a pending row and nobody called ──────────────────────

    [Fact]
    public async Task A_future_slot_is_written_pending_and_calls_nobody()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var slot = _f.Clock.GetUtcNow().AddHours(12);

        UpgradeActionRow row;
        await using (var ctx = _f.Db.NewContext())
        {
            row = await _f.Svc(ctx).ScheduleUpgradeActionAsync(
                projectId, envId, UpgradeActionKind.RunNow, slot);
        }

        _f.Admin.Writes.Should().Be(0, "nothing leaves the building until the slot arrives");
        row.IsPending.Should().BeTrue();

        var stored = await _f.ReadActionAsync(row.Id);
        stored.Status.Should().Be(UpgradeActionStatus.Pending);
        stored.ExecuteAfter.Should().Be(slot.UtcDateTime);
        stored.SentAt.Should().BeNull();
    }

    [Fact]
    public async Task A_slot_that_has_already_passed_is_refused()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();

        await using (var ctx = _f.Db.NewContext())
        {
            var act = () => _f.Svc(ctx).ScheduleUpgradeActionAsync(
                projectId, envId, UpgradeActionKind.RunNow, _f.Clock.GetUtcNow().AddHours(-2));
            (await act.Should().ThrowAsync<PlanValidationException>())
                .Which.Errors.Should().ContainKey("ExecuteAt");
        }

        await using var verify = _f.Db.NewContext();
        (await verify.OeEnvironmentUpgradeActions.CountAsync()).Should().Be(0,
            "a refusal writes nothing: nothing was asked of Business Central");
    }

    [Fact]
    public async Task Someone_without_the_update_grant_cannot_ask_for_anything()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        _f.ActAs(UpgradeActionTestFixture.PlainTeamUserId);

        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Svc(ctx).ScheduleUpgradeActionAsync(
            projectId, envId, UpgradeActionKind.PushDateToLatest, executeAt: null);
        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
        _f.Admin.Writes.Should().Be(0);
    }

    // ── Cancelling ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_booked_action_cancels_and_records_who_cancelled_it()
    {
        var id = await BookAsync();

        await using (var ctx = _f.Db.NewContext())
            await _f.Svc(ctx).CancelUpgradeActionAsync(id);

        var stored = await _f.ReadActionAsync(id);
        stored.Status.Should().Be(UpgradeActionStatus.Cancelled);
        stored.CancelledBy.Should().Contain("Anna Jensen");
        stored.CancelledByUserId.Should().Be(UpgradeActionTestFixture.FlagUserId);
        stored.CancelledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task An_action_that_already_ran_cannot_be_cancelled()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        UpgradeActionRow row;
        await using (var ctx = _f.Db.NewContext())
        {
            row = await _f.Svc(ctx).ScheduleUpgradeActionAsync(
                projectId, envId, UpgradeActionKind.RunNow, executeAt: null);
        }

        await using var cancelCtx = _f.Db.NewContext();
        var act = () => _f.Svc(cancelCtx).CancelUpgradeActionAsync(row.Id);
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Values.Should().ContainMatch("*already run*");
    }

    [Fact]
    public async Task Cancelling_twice_refuses_the_second_time()
    {
        var id = await BookAsync();
        await using (var ctx = _f.Db.NewContext())
            await _f.Svc(ctx).CancelUpgradeActionAsync(id);

        await using var second = _f.Db.NewContext();
        var act = () => _f.Svc(second).CancelUpgradeActionAsync(id);
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Values.Should().ContainMatch("*already cancelled*");
    }

    [Fact]
    public async Task Someone_without_the_update_grant_cannot_cancel()
    {
        var id = await BookAsync();
        _f.ActAs(UpgradeActionTestFixture.PlainTeamUserId);

        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Svc(ctx).CancelUpgradeActionAsync(id);
        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    // ── The feed ────────────────────────────────────────────────────────

    [Fact]
    public async Task Reading_the_feed_needs_only_being_able_to_see_the_customer()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await BookAsync(projectId, envId);

        // A colleague on the team but without the flag reads the history fine.
        _f.ActAs(UpgradeActionTestFixture.PlainTeamUserId);
        await using (var ctx = _f.Db.NewContext())
        {
            var feed = await _f.Svc(ctx).ListEnvironmentActivityAsync(projectId, envId);
            feed.Should().ContainSingle("acting needs the grant; reading needs only visibility");
        }
    }

    [Fact]
    public async Task A_private_customers_feed_is_closed_to_someone_who_cannot_see_it()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await BookAsync(projectId, envId);
        await using (var ctx = _f.Db.NewContext())
        {
            var project = await ctx.OeProjects.SingleAsync(p => p.Id == projectId);
            project.Visibility = ProjectVisibility.Private;
            await ctx.SaveChangesAsync();
        }

        _f.ActAs(UpgradeActionTestFixture.OutsiderUserId);
        await using var read = _f.Db.NewContext();
        var act = () => _f.Svc(read).ListEnvironmentActivityAsync(projectId, envId);
        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    [Fact]
    public async Task The_feed_is_newest_first_and_capped()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();

        // More than the cap, each a minute apart so "newest" is unambiguous.
        for (var i = 0; i < UpgradeActionService.FeedLimit + 5; i++)
        {
            _f.Clock.Advance(TimeSpan.FromMinutes(1));
            await BookAsync(projectId, envId);
        }

        await using var ctx = _f.Db.NewContext();
        var feed = await _f.Svc(ctx).ListEnvironmentActivityAsync(projectId, envId);

        feed.Should().HaveCount(UpgradeActionService.FeedLimit);
        feed.Should().BeInDescendingOrder(a => a.RequestedAt);
    }

    [Fact]
    public async Task Pending_actions_are_listed_across_the_visible_fleet()
    {
        var (projectA, envA) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (projectB, envB) = await _f.SeedCustomerAsync("CRONUS Norway", "Europe/Oslo");
        await BookAsync(projectA, envA);
        await BookAsync(projectB, envB);
        var cancelled = await BookAsync(projectB, envB);
        await using (var ctx = _f.Db.NewContext())
            await _f.Svc(ctx).CancelUpgradeActionAsync(cancelled);

        await using var read = _f.Db.NewContext();
        var pending = await _f.Svc(read).ListPendingAsync();

        pending.Should().HaveCount(2, "a cancelled action is history, not something still waiting");
        pending.Select(p => p.EnvironmentId).Should().BeEquivalentTo(new[] { envA, envB });
    }

    // ── How the feed reads afterwards ───────────────────────────────────

    [Fact]
    public async Task A_booking_is_told_apart_from_an_action_run_there_and_then()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();

        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Svc(ctx).ScheduleUpgradeActionAsync(
                projectId, envId, UpgradeActionKind.RunNow, executeAt: null);
        }
        var bookedId = await BookAsync(projectId, envId);

        await using var read = _f.Db.NewContext();
        var feed = await _f.Svc(read).ListEnvironmentActivityAsync(projectId, envId);

        feed.Single(a => a.Id == bookedId).IsBooking.Should().BeTrue();
        feed.Single(a => a.Id != bookedId).IsBooking.Should().BeFalse(
            "an immediate action fires when it is asked for, so its headline must not claim it was booked");
    }

    [Fact]
    public async Task A_failed_action_is_written_down_in_the_past_tense()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        _f.Admin.OnUpdates = Array.Empty<BcEnvironmentUpdate>;

        await using (var ctx = _f.Db.NewContext())
        {
            var act = () => _f.Svc(ctx).ScheduleUpgradeActionAsync(
                projectId, envId, UpgradeActionKind.RunNow, executeAt: null);
            await act.Should().ThrowAsync<PlanValidationException>();
        }

        await using var verify = _f.Db.NewContext();
        var stored = await verify.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync();
        stored.Outcome.Should().StartWith("The update didn't start.",
            "the feed is read days later, so it says what did not happen rather than what is wrong now");
    }

    [Theory]
    // The refusals these two writes actually produce, and what the history makes of them.
    [InlineData("The environment is busy with another operation right now. Try again once it finishes.",
        "The update didn't start. Reason given at the time: The environment is busy with another operation right now.")]
    [InlineData("That environment no longer exists. Refresh the list and try again.",
        "The update didn't start. Reason given at the time: That environment no longer exists.")]
    [InlineData("No update is available to run.",
        "The update didn't start. Reason given at the time: No update is available to run.")]
    public void The_history_drops_advice_meant_for_somebody_standing_at_the_form(string reason, string expected)
    {
        UpgradeActionService.FailureOutcome(UpgradeActionKind.RunNow, reason).Should().Be(expected);
    }

    [Fact]
    public void A_failed_date_move_says_the_date_was_not_moved()
    {
        UpgradeActionService.FailureOutcome(
                UpgradeActionKind.PushDateToLatest, "This update's date is already the latest Microsoft allows.")
            .Should().StartWith("The update date wasn't moved.");
    }

    [Fact]
    public async Task The_feed_names_a_person_without_their_address_in_the_sentence()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await BookAsync(projectId, envId);

        await using var read = _f.Db.NewContext();
        var entry = (await _f.Svc(read).ListEnvironmentActivityAsync(projectId, envId)).Single();

        entry.RequestedByName.Should().Be("Anna Jensen");
        entry.RequestedByEmail.Should().Be("upgrade@example.com");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<int> BookAsync() => await BookAsync(null, null);

    /// <summary>Books an update for a slot half a day out, seeding a customer if needed.</summary>
    private async Task<int> BookAsync(int? projectId, int? environmentId)
    {
        if (projectId is null || environmentId is null)
        {
            (projectId, environmentId) = await _f.SeedCustomerAsync();
        }

        await using var ctx = _f.Db.NewContext();
        var row = await _f.Svc(ctx).ScheduleUpgradeActionAsync(
            projectId.Value, environmentId.Value, UpgradeActionKind.RunNow,
            _f.Clock.GetUtcNow().AddHours(12));
        return row.Id;
    }

    [Fact]
    public async Task A_booked_upload_is_not_a_pending_platform_update()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        _f.ActAs(UpgradeActionTestFixture.OwnerUserId);
        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Connections(ctx).InstallUploadedAppsAsync(projectId, envId,
                new[] { new UploadPackage(new byte[] { 1 }, "Partner.app") },
                ALDevToolbox.Domain.ValueObjects.ObjectExplorer.UploadAppTiming.AtTime,
                _f.Clock.GetUtcNow().AddHours(12));
        }
        _f.ActAs(UpgradeActionTestFixture.FlagUserId);

        await using var read = _f.Db.NewContext();
        var pending = await _f.Svc(read).ListPendingAsync();
        var uploads = await _f.Svc(read).ListPendingUploadsAsync(projectId, envId);

        pending.Should().BeEmpty("the Upgrades page must not report a vendor app waiting for tonight as a booked update");
        uploads.Should().ContainSingle().Which.PackageFileName.Should().Be("Partner.app");
    }
}
