using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Following a started platform update to its end (#1711). Business Central gives no link
/// between the start we send and the update operation it creates later, so what is pinned
/// here is the match (type, time, version), what each ending records, who hears about a
/// failure, and that a start nobody can read about is left alone rather than guessed at.
/// </summary>
public sealed class UpgradeActionFollowTests : IDisposable
{
    private readonly UpgradeActionTestFixture _f = new();

    public UpgradeActionFollowTests()
    {
        // The re-read after an update ends: the environment is running again.
        _f.Admin.OnEnvironment = name => new BcEnvironment(name, "Production") { Status = "Active", Version = "28.5.12345.0" };
    }

    public void Dispose() => _f.Dispose();

    private static BcEnvironmentOperation Op(
        string status, DateTimeOffset created, string? toVersion = "28.5.12345.0", string type = "Update", string error = "") =>
        new(Guid.NewGuid().ToString(), type, status, created, created, null, "SYSTEM", error,
            toVersion is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["destinationVersion"] = toVersion });

    // ── The match ───────────────────────────────────────────────────────

    [Fact]
    public void The_match_takes_the_first_update_after_the_start_to_the_started_version()
    {
        var sent = new DateTime(2026, 11, 4, 12, 51, 0, DateTimeKind.Utc);
        var ours = Op("Running", sent.AddMinutes(2));
        var operations = new[]
        {
            Op("Succeeded", sent.AddDays(-30)),                                       // last month's update
            Op("Succeeded", sent.AddMinutes(1), type: "Modify"),                      // our own schedule write
            Op("Scheduled", sent.AddMinutes(1), toVersion: "28.3.54977.56391"),       // another version
            ours,
            Op("Queued", sent.AddHours(5)),
        };

        UpgradeActionWorker.MatchUpdateOperation(operations, sent, "28.5").Should().BeSameAs(ours);
    }

    [Fact]
    public void An_update_that_names_no_version_or_a_start_that_recorded_none_is_matched_on_time()
    {
        var sent = new DateTime(2026, 11, 4, 12, 51, 0, DateTimeKind.Utc);
        var unnamed = Op("Running", sent.AddMinutes(3), toVersion: null);

        UpgradeActionWorker.MatchUpdateOperation([unnamed], sent, "28.5").Should().BeSameAs(unnamed);
        var other = Op("Running", sent.AddMinutes(3), toVersion: "28.3.1.0");
        UpgradeActionWorker.MatchUpdateOperation([other], sent, targetVersion: null).Should().BeSameAs(other);
    }

    [Fact]
    public void An_update_queued_before_the_start_and_run_after_it_is_matched()
    {
        // Business Central had the update scheduled already; the start pulled it forward.
        var sent = new DateTime(2026, 11, 4, 12, 51, 0, DateTimeKind.Utc);
        var queued = Op("Running", sent.AddDays(-3)) with { StartedOn = sent.AddMinutes(4) };
        var waiting = Op("Scheduled", sent.AddDays(-3)) with { StartedOn = null };

        UpgradeActionWorker.MatchUpdateOperation([queued], sent, "28.5").Should().BeSameAs(queued);
        UpgradeActionWorker.MatchUpdateOperation([waiting], sent, "28.5").Should().BeSameAs(waiting);
    }

    [Fact]
    public void An_update_that_began_after_the_next_start_belongs_to_that_one()
    {
        var first = new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
        var second = first.AddHours(3);
        var operations = new[] { Op("Failed", second.AddMinutes(5)) };

        UpgradeActionWorker.MatchUpdateOperation(operations, first, "28.5", nextStartUtc: second).Should().BeNull();
        UpgradeActionWorker.MatchUpdateOperation(operations, second, "28.5").Should().NotBeNull();
    }

    [Fact]
    public void A_status_not_known_to_be_final_settles_nothing()
    {
        var start = new UpgradeActionWorker.FollowedStart(1, 1, 1, 1, "28.5", DateTime.UtcNow);
        UpgradeActionWorker.Settle(Op("NotStarted", DateTimeOffset.UtcNow), read: true, start, DateTime.UtcNow).Should().BeNull();
        UpgradeActionWorker.Settle(Op("", DateTimeOffset.UtcNow), read: true, start, DateTime.UtcNow).Should().BeNull();
    }

    // ── What each ending records ────────────────────────────────────────

    [Fact]
    public async Task A_succeeded_update_is_recorded_and_announces_nothing()
    {
        var (projectId, envId, actionId, sent) = await StartAsync();
        _f.Admin.OnOperations = () => [Op("Succeeded", sent.AddMinutes(5))];

        await FollowAsync();

        var row = await _f.ReadActionAsync(actionId);
        row.UpdateResult.Should().Be(PlatformUpdateResult.Succeeded);
        row.Status.Should().Be(UpgradeActionStatus.Sent);
        row.Outcome.Should().Be("Business Central finished the update to 28.5.12345.0.");
        _f.Emails.Sent.Should().BeEmpty("the ready-to-check notice covers a success");
    }

    [Fact]
    public async Task A_failed_update_fails_the_row_with_the_reason_and_tells_the_starter_and_the_assignee()
    {
        var (projectId, envId, actionId, sent) = await StartAsync(assignee: UpgradeActionTestFixture.PlainTeamUserId);
        _f.Admin.OnOperations = () => [Op("Failed", sent.AddMinutes(5), error: "The extension CRONUS Coffee failed to upgrade.")];

        await FollowAsync();

        var row = await _f.ReadActionAsync(actionId);
        row.UpdateResult.Should().Be(PlatformUpdateResult.Failed);
        row.Status.Should().Be(UpgradeActionStatus.Failed, "so the upgrade's line reads Failed without anybody having watched it");
        row.Outcome.Should().Contain("Reason given at the time: The extension CRONUS Coffee failed to upgrade.");
        row.BcOperationId.Should().NotBeNull();

        _f.Emails.Sent.Select(e => e.Subject).Should().AllBe("Failed: Update to 28.5 on CRONUS Denmark / Production");
        _f.Emails.Sent.Should().HaveCount(2, "the person who started it and the person checking it both hear");
    }

    [Fact]
    public async Task An_update_still_running_is_asked_about_again_later_not_every_sweep()
    {
        var (_, _, actionId, sent) = await StartAsync();
        _f.Admin.OnOperations = () => [Op("Running", sent.AddMinutes(5))];

        await FollowAsync();
        await FollowAsync();
        _f.Admin.OperationReads.Should().Be(1, "a start is asked about every few minutes, not every 30 seconds");
        (await _f.ReadActionAsync(actionId)).UpdateResult.Should().BeNull();

        _f.Clock.Advance(UpgradeActionWorker.FollowEvery);
        _f.Admin.OnOperations = () => [Op("Succeeded", sent.AddMinutes(5))];
        await FollowAsync();

        _f.Admin.OperationReads.Should().Be(2);
        (await _f.ReadActionAsync(actionId)).UpdateResult.Should().Be(PlatformUpdateResult.Succeeded);
    }

    [Fact]
    public async Task No_update_a_day_after_the_start_is_reported_as_never_started()
    {
        var (_, _, actionId, _) = await StartAsync();
        _f.Admin.OnOperations = () => [];

        await FollowAsync();
        (await _f.ReadActionAsync(actionId)).UpdateResult.Should().BeNull("Microsoft's scheduler may simply not have got to it");

        _f.Clock.Advance(UpgradeActionWorker.NeverStartedAfter);
        await FollowAsync();

        var row = await _f.ReadActionAsync(actionId);
        row.UpdateResult.Should().Be(PlatformUpdateResult.NeverStarted);
        row.Status.Should().Be(UpgradeActionStatus.Failed);
        row.Outcome.Should().StartWith("Business Central accepted the start, but hadn't begun the update to 28.5 a day later.");
        _f.Emails.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task A_day_of_failed_reads_says_nothing_about_the_update()
    {
        var (_, _, actionId, _) = await StartAsync();
        // OnOperations unset: every read throws.

        _f.Clock.Advance(UpgradeActionWorker.NeverStartedAfter + TimeSpan.FromHours(1));
        await FollowAsync();

        var row = await _f.ReadActionAsync(actionId);
        row.UpdateResult.Should().BeNull();
        row.Status.Should().Be(UpgradeActionStatus.Sent);
        _f.Emails.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_start_older_than_the_follow_window_is_left_alone()
    {
        var (_, _, actionId, _) = await StartAsync();
        _f.Admin.OnOperations = () => [];
        _f.Clock.Advance(UpgradeActionWorker.FollowFor + TimeSpan.FromMinutes(1));

        await FollowAsync();

        _f.Admin.OperationReads.Should().Be(0);
        (await _f.ReadActionAsync(actionId)).UpdateResult.Should().BeNull();
    }

    [Fact]
    public async Task A_success_waits_for_the_environment_to_be_read_again()
    {
        var (_, _, actionId, sent) = await StartAsync();
        _f.Admin.OnOperations = () => [Op("Succeeded", sent.AddMinutes(5))];
        _f.Admin.OnEnvironment = null; // the re-read fails

        await FollowAsync();
        (await _f.ReadActionAsync(actionId)).UpdateResult.Should().BeNull(
            "recorded without a fresh mirror, the line would leave Booked and read as if nothing had happened");

        _f.Admin.OnEnvironment = name => new BcEnvironment(name, "Production") { Status = "Active", Version = "28.5.12345.0" };
        _f.Clock.Advance(UpgradeActionWorker.FollowEvery);
        await FollowAsync();
        (await _f.ReadActionAsync(actionId)).UpdateResult.Should().Be(PlatformUpdateResult.Succeeded);
    }

    [Fact]
    public async Task A_start_sent_again_is_superseded_and_only_the_later_one_reports()
    {
        var (projectId, envId, firstId, first) = await StartAsync();
        _f.Clock.Advance(TimeSpan.FromHours(3));
        var second = _f.Clock.GetUtcNow().UtcDateTime;
        int secondId;
        await using (var ctx = _f.Db.NewContext())
        {
            var again = new OeEnvironmentUpgradeAction
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, EnvironmentId = envId, UpgradeId = _upgradeId,
                Kind = UpgradeActionKind.RunNow, Status = UpgradeActionStatus.Sent, TargetVersion = "28.5",
                RequestedByUserId = UpgradeActionTestFixture.FlagUserId, RequestedBy = "Anna Jensen <upgrade@example.com>",
                RequestedAt = second, ExecuteAfter = second, SentAt = second,
            };
            ctx.OeEnvironmentUpgradeActions.Add(again);
            await ctx.SaveChangesAsync();
            secondId = again.Id;
        }
        _f.Admin.OnOperations = () => [Op("Failed", second.AddMinutes(5), error: "Out of disk.")];

        await FollowAsync();

        var older = await _f.ReadActionAsync(firstId);
        older.UpdateResult.Should().Be(PlatformUpdateResult.Superseded);
        older.Status.Should().Be(UpgradeActionStatus.Sent);
        (await _f.ReadActionAsync(secondId)).UpdateResult.Should().Be(PlatformUpdateResult.Failed);
        _f.Emails.Sent.Should().ContainSingle("one failed update is one notice, not one per start");
    }

    // ── The line ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_start_whose_update_ended_below_the_target_is_no_longer_booked()
    {
        var (projectId, envId, _, sent) = await StartAsync(targetVersion: "28.3");
        _f.Admin.OnOperations = () => [Op("Succeeded", sent.AddMinutes(5), toVersion: "28.3.54977.56391")];
        _f.Admin.OnEnvironment = name => new BcEnvironment(name, "Production") { Status = "Active", Version = "28.3.54977.56391" };
        await FollowAsync();

        await using var ctx = _f.Db.NewContext();
        var detail = await _f.Upgrades(ctx).GetAsync(_upgradeId);
        detail!.Lines.Single().State.Should().Be(UpgradeLineState.Planned,
            "the update went to 28.3, so the upgrade to 28.5 still has to happen");
    }

    // ── The date move after a finished update ───────────────────────────

    [Fact]
    public async Task A_finished_update_books_the_next_date_out_a_little_later_as_the_starter()
    {
        var (projectId, envId, actionId, sent) = await StartAsync();
        _f.Admin.OnOperations = () => [Op("Succeeded", sent.AddMinutes(5))];

        await FollowAsync();

        var move = (await AutomaticMovesAsync(envId)).Should().ContainSingle().Subject;
        move.Status.Should().Be(UpgradeActionStatus.Pending);
        move.UpgradeId.Should().Be(_upgradeId);
        move.ProjectId.Should().Be(projectId);
        move.RequestedByUserId.Should().Be(UpgradeActionTestFixture.FlagUserId);
        move.ExecuteAfter.Should().Be(_f.Clock.GetUtcNow().UtcDateTime + UpgradeActionWorker.DateMoveAfterUpdate,
            "Business Central sets the next update just after this one ends, so the move waits for that");
    }

    [Fact]
    public async Task The_booked_date_move_runs_when_due_and_announces_nothing()
    {
        var (_, envId, _, sent) = await StartAsync();
        _f.Admin.OnOperations = () => [Op("Succeeded", sent.AddMinutes(5))];
        await FollowAsync();

        // Business Central's next update after the one that finished, read back as moved once written.
        _f.Admin.OnUpdates = () => [UpgradeActionTestFixture.Update(
            _f.Admin.SelectedDateTime ?? UpgradeActionTestFixture.ScheduledDate, UpgradeActionTestFixture.LatestDate)];
        _f.Clock.Advance(UpgradeActionWorker.DateMoveAfterUpdate + TimeSpan.FromMinutes(1));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        _f.Admin.Writes.Should().Be(1);
        (await AutomaticMovesAsync(envId)).Single().Status.Should().Be(UpgradeActionStatus.Sent);
        _f.Emails.Sent.Should().BeEmpty("nobody asked for it, so only a failure would be news");
    }

    [Fact]
    public async Task A_booked_date_move_with_nothing_to_move_ends_quietly()
    {
        var (_, envId, _, sent) = await StartAsync();
        _f.Admin.OnOperations = () => [Op("Succeeded", sent.AddMinutes(5))];
        await FollowAsync();

        // Business Central never set a next update.
        _f.Admin.OnUpdates = Array.Empty<BcEnvironmentUpdate>;
        _f.Clock.Advance(UpgradeActionWorker.DateMoveAfterUpdate + TimeSpan.FromMinutes(1));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var move = (await AutomaticMovesAsync(envId)).Single();
        move.Status.Should().Be(UpgradeActionStatus.Cancelled, "finding nothing to move is an answer, not a failure");
        move.Outcome.Should().Be("Nothing to move: No update is available to reschedule.");
        _f.Admin.Writes.Should().Be(0);
        _f.Emails.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_date_move_a_person_booked_still_fails_when_there_is_nothing_to_move()
    {
        var (projectId, envId, _, _) = await StartAsync();
        int id;
        await using (var ctx = _f.Db.NewContext())
        {
            var now = _f.Clock.GetUtcNow().UtcDateTime;
            var booked = new OeEnvironmentUpgradeAction
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, EnvironmentId = envId,
                Kind = UpgradeActionKind.PushDateToLatest, Status = UpgradeActionStatus.Pending,
                RequestedByUserId = UpgradeActionTestFixture.FlagUserId, RequestedBy = "Anna Jensen <upgrade@example.com>",
                RequestedAt = now, ExecuteAfter = now,
            };
            ctx.OeEnvironmentUpgradeActions.Add(booked);
            await ctx.SaveChangesAsync();
            id = booked.Id;
        }
        _f.Admin.OnUpdates = Array.Empty<BcEnvironmentUpdate>;

        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        (await _f.ReadActionAsync(id)).Status.Should().Be(UpgradeActionStatus.Failed, "the person who booked it expected a move");
    }

    [Fact]
    public async Task No_date_move_is_booked_when_the_upgrade_has_the_option_off()
    {
        var (_, envId, _, sent) = await StartAsync();
        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).UpdateDetailsAsync(_upgradeId, "28.5 in November 2026", "28.5", null, null,
                moveDateAfterUpdate: false);
        }
        _f.Admin.OnOperations = () => [Op("Succeeded", sent.AddMinutes(5))];

        await FollowAsync();

        (await AutomaticMovesAsync(envId)).Should().BeEmpty();
    }

    [Fact]
    public async Task No_date_move_is_booked_after_a_failed_update()
    {
        var (_, envId, _, sent) = await StartAsync();
        _f.Admin.OnOperations = () => [Op("Failed", sent.AddMinutes(5), error: "No.")];

        await FollowAsync();

        (await AutomaticMovesAsync(envId)).Should().BeEmpty();
    }

    [Fact]
    public async Task No_date_move_is_booked_when_the_update_stopped_short_of_the_target()
    {
        var (_, envId, _, sent) = await StartAsync();
        _f.Admin.OnOperations = () => [Op("Succeeded", sent.AddMinutes(5), toVersion: "28.3.54977.56391")];
        _f.Admin.OnEnvironment = name => new BcEnvironment(name, "Production") { Status = "Active", Version = "28.3.54977.56391" };

        await FollowAsync();

        (await AutomaticMovesAsync(envId)).Should().BeEmpty("the next update is the target itself, which must not be pushed away");
    }

    [Fact]
    public async Task No_date_move_is_booked_over_something_already_waiting_on_the_environment()
    {
        var (projectId, envId, _, sent) = await StartAsync();
        await using (var ctx = _f.Db.NewContext())
        {
            var now = _f.Clock.GetUtcNow().UtcDateTime;
            ctx.OeEnvironmentUpgradeActions.Add(new OeEnvironmentUpgradeAction
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, EnvironmentId = envId,
                Kind = UpgradeActionKind.PushDateToLatest, Status = UpgradeActionStatus.Pending,
                RequestedByUserId = UpgradeActionTestFixture.FlagUserId, RequestedBy = "Anna Jensen <upgrade@example.com>",
                RequestedAt = now, ExecuteAfter = now.AddDays(1),
            });
            await ctx.SaveChangesAsync();
        }
        _f.Admin.OnOperations = () => [Op("Succeeded", sent.AddMinutes(5))];

        await FollowAsync();

        (await AutomaticMovesAsync(envId)).Should().BeEmpty();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<List<OeEnvironmentUpgradeAction>> AutomaticMovesAsync(int envId)
    {
        await using var ctx = _f.Db.NewContext();
        return await ctx.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.EnvironmentId == envId && a.Automatic)
            .ToListAsync();
    }

    private int _upgradeId;

    private async Task FollowAsync() =>
        await _f.Worker().FollowStartedUpdatesAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

    /// <summary>A customer on an upgrade to 28.5 with a start from it that Business Central accepted, sent now.</summary>
    private async Task<(int ProjectId, int EnvironmentId, int ActionId, DateTime SentAt)> StartAsync(
        int? assignee = null, string targetVersion = "28.5")
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await using (var ctx = _f.Db.NewContext())
        {
            _upgradeId = await _f.Upgrades(ctx).CreateAsync("28.5 in November 2026", "28.5", null, null);
        }
        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).AddLinesAsync(_upgradeId, [envId]);
        }
        var sent = _f.Clock.GetUtcNow().UtcDateTime;
        await using (var ctx = _f.Db.NewContext())
        {
            if (assignee is { } person)
            {
                var line = await ctx.OeEnvironmentUpgradeLines.SingleAsync(l => l.UpgradeId == _upgradeId);
                line.AssigneeUserId = person;
            }
            var action = new OeEnvironmentUpgradeAction
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, EnvironmentId = envId, UpgradeId = _upgradeId,
                Kind = UpgradeActionKind.RunNow, Status = UpgradeActionStatus.Sent, TargetVersion = targetVersion,
                RequestedByUserId = UpgradeActionTestFixture.FlagUserId, RequestedBy = "Anna Jensen <upgrade@example.com>",
                RequestedAt = sent, ExecuteAfter = sent, SentAt = sent,
                Outcome = "Business Central was told to start the update, ignoring the environment's update window.",
            };
            ctx.OeEnvironmentUpgradeActions.Add(action);
            await ctx.SaveChangesAsync();
            return (projectId, envId, action.Id, sent);
        }
    }
}
