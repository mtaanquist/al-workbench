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

    // ── The line ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_start_whose_update_ended_below_the_target_is_no_longer_booked()
    {
        var (projectId, envId, _, sent) = await StartAsync(targetVersion: "28.3");
        _f.Admin.OnOperations = () => [Op("Succeeded", sent.AddMinutes(5), toVersion: "28.3.54977.56391")];
        await FollowAsync();

        await using var ctx = _f.Db.NewContext();
        var detail = await _f.Upgrades(ctx).GetAsync(_upgradeId);
        detail!.Lines.Single().State.Should().Be(UpgradeLineState.Planned,
            "the update went to 28.3, so the upgrade to 28.5 still has to happen");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

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
