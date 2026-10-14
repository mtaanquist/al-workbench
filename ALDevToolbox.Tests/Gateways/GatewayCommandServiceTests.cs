using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// <see cref="GatewayCommandService"/>: queueing, the exactly-once claim, progress
/// and results, heartbeats, the file check and the sweep's own pass. See
/// <c>.design/on-prem-gateway.md</c>, "Commands".
/// </summary>
public sealed class GatewayCommandServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly GatewayCommandSignal _signal = new();
    private User _admin = null!;

    public void Dispose() => _db.Dispose();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private GatewayCommandService Service(AppDbContext ctx, IOrganizationContext? org = null) =>
        new(ctx, org ?? _db.OrgContext, _signal, _clock, NullLogger<GatewayCommandService>.Instance);

    private async Task<int> GatewayAsync(bool approve = true, int organizationId = TestDb.DefaultOrgId, string machine = "CRONUS-BC01")
    {
        var admin = organizationId == TestDb.DefaultOrgId && _admin is not null
            ? _admin
            : await GatewayTestKit.SeedUserAsync(_db, organizationId, UserRole.Admin, $"admin{Guid.NewGuid():N}@cronus.example");
        if (organizationId == TestDb.DefaultOrgId) _admin ??= admin;
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, organizationId, admin.Id, key, approve, machine);
        return registered.GatewayId;
    }

    private async Task<long> EnqueueAsync(int gatewayId, GatewayVerb verb = GatewayVerb.RefreshInventory,
        DateTime? due = null, DateTime? windowEnd = null, IReadOnlyList<GatewayCommandFileInput>? files = null, JsonObject? parameters = null)
    {
        await using var ctx = _db.NewContext();
        return await Service(ctx).EnqueueAsync(new GatewayCommandRequest(gatewayId, verb, parameters, due, windowEnd,
            RequestedByUserId: _admin?.Id, Files: files));
    }

    private async Task<GatewayClaimResult> ClaimAsync(int gatewayId)
    {
        await using var ctx = _db.NewContext();
        return await Service(ctx).ClaimNextAsync(gatewayId);
    }

    private async Task<GatewayCommand> LoadAsync(long id)
    {
        await using var ctx = _db.NewContext();
        return await ctx.GatewayCommands.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    // ---------------------------------------------------------------- enqueue

    [Fact]
    public async Task A_command_is_queued_with_a_claim_deadline_fifteen_minutes_after_it_is_due()
    {
        var gateway = await GatewayAsync();
        var parameters = new JsonObject { ["instance"] = "BC140" };

        var id = await EnqueueAsync(gateway, GatewayVerb.RestartInstance, parameters: parameters);

        var row = await LoadAsync(id);
        row.Status.Should().Be(GatewayCommandStatus.Queued);
        row.Verb.Should().Be(GatewayVerb.RestartInstance);
        row.DueAt.Should().Be(Now);
        row.ExpiresAt.Should().Be(Now + TimeSpan.FromMinutes(15));
        row.RequestedByUserId.Should().Be(_admin.Id);
        JsonNode.Parse(row.ParametersJson)!["instance"]!.GetValue<string>().Should().Be("BC140");
    }

    [Fact]
    public void The_claim_deadline_is_the_window_end_when_that_comes_first()
    {
        var due = Start.UtcDateTime;
        GatewayCommandService.ClaimDeadline(due, due.AddMinutes(5)).Should().Be(due.AddMinutes(5));
        GatewayCommandService.ClaimDeadline(due, due.AddHours(2)).Should().Be(due.AddMinutes(15));
        GatewayCommandService.ClaimDeadline(due, null).Should().Be(due.AddMinutes(15));
    }

    [Fact]
    public async Task A_command_whose_window_has_ended_is_refused()
    {
        var gateway = await GatewayAsync();

        var act = () => EnqueueAsync(gateway, due: Now.AddHours(-1), windowEnd: Now.AddMinutes(-1));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("dueAt");
    }

    [Fact]
    public async Task A_pending_gateway_cannot_be_given_a_command()
    {
        var gateway = await GatewayAsync(approve: false);

        var act = () => EnqueueAsync(gateway);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["gateway"].Should().Contain("approve");
    }

    [Fact]
    public async Task A_revoked_gateway_cannot_be_given_a_command()
    {
        var gateway = await GatewayAsync();
        _db.OrgContext.CurrentUserId = _admin.Id;
        await using (var ctx = _db.NewContext())
        {
            await GatewayTestKit.NewService(ctx, _db.OrgContext, _clock).RevokeAsync(gateway);
        }

        var act = () => EnqueueAsync(gateway);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["gateway"].Should().Contain("revoked");
    }

    [Fact]
    public async Task Revoking_a_gateway_fails_its_unfinished_commands_at_once_and_leaves_finished_ones()
    {
        var gateway = await GatewayAsync();
        var other = await GatewayAsync(machine: "CRONUS-BC02");
        var done = await EnqueueAsync(gateway);
        var running = await EnqueueAsync(gateway);
        var claimed = await EnqueueAsync(gateway);
        var queued = await EnqueueAsync(gateway);
        var later = await EnqueueAsync(gateway, due: Now.AddDays(3));
        var otherGateways = await EnqueueAsync(other);
        await using (var ctx = _db.NewContext())
        {
            var service = Service(ctx);
            (await service.ClaimNextAsync(gateway)).Command!.Id.Should().Be(done);
            await service.CompleteAsync(gateway, done, new GatewayCommandResultRequest { Succeeded = true });
            (await service.ClaimNextAsync(gateway)).Command!.Id.Should().Be(running);
            await service.AppendProgressAsync(gateway, running, ["Publishing"]);
            (await service.ClaimNextAsync(gateway)).Command!.Id.Should().Be(claimed);
        }

        _db.OrgContext.CurrentUserId = _admin.Id;
        await using (var ctx = _db.NewContext())
        {
            await GatewayTestKit.NewService(ctx, _db.OrgContext, _clock).RevokeAsync(gateway);
        }

        foreach (var id in new[] { running, claimed, queued, later })
        {
            var row = await LoadAsync(id);
            row.Status.Should().Be(GatewayCommandStatus.Failed, $"command {id}");
            row.FailureMessage.Should().Be("The gateway was revoked.");
            row.FinishedAt.Should().Be(Now);
        }
        (await LoadAsync(done)).Status.Should().Be(GatewayCommandStatus.Succeeded);
        (await LoadAsync(otherGateways)).Status.Should().Be(GatewayCommandStatus.Queued);
    }

    [Fact]
    public async Task Another_organisations_gateway_cannot_be_given_a_command()
    {
        var foreign = await GatewayAsync(organizationId: TestDb.OtherOrgId);

        var act = () => EnqueueAsync(foreign);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["gateway"].Should().Contain("no longer exists");
    }

    // ---------------------------------------------------------------- claim

    [Fact]
    public async Task A_claim_hands_out_the_command_once_with_its_files_and_the_seconds_it_had_left()
    {
        var gateway = await GatewayAsync();
        var content = "app bytes"u8.ToArray();
        var id = await EnqueueAsync(gateway, GatewayVerb.PublishInstall,
            files: [new GatewayCommandFileInput("CRONUS_Coffee_1.4.0.0.app", content)]);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var first = await ClaimAsync(gateway);
        var second = await ClaimAsync(gateway);

        first.Outcome.Should().Be(GatewayClaimOutcome.Claimed);
        first.Command!.Id.Should().Be(id);
        first.Command.Verb.Should().Be(GatewayVerb.PublishInstall);
        first.Command.SecondsLeft.Should().Be(14 * 60);
        first.Command.Files.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Name = "CRONUS_Coffee_1.4.0.0.app",
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            Size = (long)content.Length,
        });
        second.Outcome.Should().Be(GatewayClaimOutcome.Nothing);
        var row = await LoadAsync(id);
        row.Status.Should().Be(GatewayCommandStatus.Claimed);
        row.ClaimedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Concurrent_claims_never_hand_out_the_same_command_twice()
    {
        var gateway = await GatewayAsync();
        var queued = new List<long>();
        for (var i = 0; i < 12; i++) queued.Add(await EnqueueAsync(gateway));

        var claimed = new System.Collections.Concurrent.ConcurrentBag<long>();
        async Task ClaimUntilEmptyAsync()
        {
            var misses = 0;
            while (misses < 3)
            {
                var result = await ClaimAsync(gateway);
                if (result.Outcome == GatewayClaimOutcome.Claimed) claimed.Add(result.Command!.Id);
                else misses++;
            }
        }
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(ClaimUntilEmptyAsync)));

        claimed.Should().OnlyHaveUniqueItems();
        claimed.Should().BeEquivalentTo(queued);
    }

    [Fact]
    public async Task Two_polls_racing_for_one_command_give_it_to_exactly_one()
    {
        var gateway = await GatewayAsync();
        for (var round = 0; round < 10; round++)
        {
            await EnqueueAsync(gateway);
            var results = await Task.WhenAll(Task.Run(() => ClaimAsync(gateway)), Task.Run(() => ClaimAsync(gateway)));
            results.Count(r => r.Outcome == GatewayClaimOutcome.Claimed).Should().Be(1, $"round {round}");
        }
    }

    [Fact]
    public async Task A_command_is_not_handed_out_before_it_is_due_or_after_its_deadline()
    {
        var gateway = await GatewayAsync();
        await EnqueueAsync(gateway, due: Now.AddMinutes(10));

        (await ClaimAsync(gateway)).Outcome.Should().Be(GatewayClaimOutcome.Nothing);
        _clock.Advance(TimeSpan.FromMinutes(26));
        (await ClaimAsync(gateway)).Outcome.Should().Be(GatewayClaimOutcome.Nothing, "its claim deadline has passed");
        _clock.Advance(TimeSpan.FromMinutes(-15));
        (await ClaimAsync(gateway)).Outcome.Should().Be(GatewayClaimOutcome.Claimed);
    }

    [Fact]
    public async Task A_pending_gateway_gets_no_command()
    {
        var gateway = await GatewayAsync();
        await EnqueueAsync(gateway);
        // A new key sends it back to pending; set it directly rather than rotate.
        await using (var ctx = _db.NewContext())
        {
            await ctx.Gateways.Where(g => g.Id == gateway).ExecuteUpdateAsync(s => s.SetProperty(g => g.State, GatewayState.Pending));
        }

        (await ClaimAsync(gateway)).Outcome.Should().Be(GatewayClaimOutcome.NotApproved);
    }

    [Fact]
    public async Task A_gateway_never_claims_another_gateways_command()
    {
        var mine = await GatewayAsync();
        var other = await GatewayAsync(machine: "CRONUS-BC02");
        await EnqueueAsync(other);

        (await ClaimAsync(mine)).Outcome.Should().Be(GatewayClaimOutcome.Nothing);
    }

    // ---------------------------------------------------------------- long-poll

    [Fact]
    public async Task The_long_poll_returns_as_soon_as_a_command_is_queued()
    {
        var gateway = await GatewayAsync();
        _signal.LongPollTimeout = TimeSpan.FromSeconds(20);
        await using var ctx = _db.NewContext();
        var watch = Stopwatch.StartNew();

        var waiting = Service(ctx).WaitForNextAsync(gateway);
        await Task.Delay(300);
        waiting.IsCompleted.Should().BeFalse("nothing is queued yet");
        var id = await EnqueueAsync(gateway);
        var result = await waiting;

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        result.Outcome.Should().Be(GatewayClaimOutcome.Claimed);
        result.Command!.Id.Should().Be(id);
    }

    [Fact]
    public async Task The_long_poll_gives_up_with_nothing_after_its_timeout()
    {
        var gateway = await GatewayAsync();
        _signal.LongPollTimeout = TimeSpan.FromMilliseconds(300);
        await using var ctx = _db.NewContext();

        var result = await Service(ctx).WaitForNextAsync(gateway);

        result.Outcome.Should().Be(GatewayClaimOutcome.Nothing);
    }

    // ---------------------------------------------------------------- progress and result

    [Fact]
    public async Task Progress_marks_the_command_running_and_is_logged_with_the_workbench_clock()
    {
        var gateway = await GatewayAsync();
        var id = await EnqueueAsync(gateway);
        await ClaimAsync(gateway);
        await using var ctx = _db.NewContext();

        (await Service(ctx).AppendProgressAsync(gateway, id, ["Publishing CRONUS Coffee", "two\nlines"])).Should().BeTrue();

        var row = await LoadAsync(id);
        row.Status.Should().Be(GatewayCommandStatus.Running);
        row.StartedAt.Should().Be(Now);
        row.Log.Should().Be("2026-10-14 12:00:00Z Publishing CRONUS Coffee\n2026-10-14 12:00:00Z two lines\n");
    }

    [Fact]
    public async Task A_result_is_recorded_once_and_later_reports_are_refused()
    {
        var gateway = await GatewayAsync();
        var id = await EnqueueAsync(gateway);
        await ClaimAsync(gateway);
        await using var ctx = _db.NewContext();
        var service = Service(ctx);

        (await service.CompleteAsync(gateway, id, new GatewayCommandResultRequest
        {
            Succeeded = true,
            Result = new JsonObject { ["installed"] = 2 },
        })).Should().BeTrue();
        (await service.CompleteAsync(gateway, id, new GatewayCommandResultRequest { Succeeded = false, FailureMessage = "late" }))
            .Should().BeFalse();
        (await service.AppendProgressAsync(gateway, id, ["late"])).Should().BeFalse();

        var row = await LoadAsync(id);
        row.Status.Should().Be(GatewayCommandStatus.Succeeded);
        row.FinishedAt.Should().Be(Now);
        JsonNode.Parse(row.ResultJson!)!["installed"]!.GetValue<int>().Should().Be(2);
        row.FailureMessage.Should().BeNull();
    }

    [Fact]
    public async Task A_failure_must_say_why()
    {
        var gateway = await GatewayAsync();
        var id = await EnqueueAsync(gateway);
        await ClaimAsync(gateway);
        await using var ctx = _db.NewContext();

        var act = () => Service(ctx).CompleteAsync(gateway, id, new GatewayCommandResultRequest { Succeeded = false });

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("failureMessage");
    }

    [Fact]
    public async Task Another_gateway_cannot_report_on_a_command()
    {
        var mine = await GatewayAsync();
        var other = await GatewayAsync(machine: "CRONUS-BC02");
        var id = await EnqueueAsync(mine);
        await ClaimAsync(mine);
        await using var ctx = _db.NewContext();

        (await Service(ctx).AppendProgressAsync(other, id, ["hello"])).Should().BeFalse();
        (await Service(ctx).CompleteAsync(other, id, new GatewayCommandResultRequest { Succeeded = true })).Should().BeFalse();
        (await LoadAsync(id)).Status.Should().Be(GatewayCommandStatus.Claimed);
    }

    // ---------------------------------------------------------------- heartbeat

    [Fact]
    public async Task An_interrupted_command_fails_and_is_never_handed_out_again()
    {
        var gateway = await GatewayAsync();
        var id = await EnqueueAsync(gateway);
        await ClaimAsync(gateway);
        await using var ctx = _db.NewContext();

        (await Service(ctx).ApplyHeartbeatAsync(gateway, running: [], interrupted: [id])).Should().Be(1);

        var row = await LoadAsync(id);
        row.Status.Should().Be(GatewayCommandStatus.Failed);
        row.FailureMessage.Should().Be(GatewayCommandService.InterruptedMessage);
        (await ClaimAsync(gateway)).Outcome.Should().Be(GatewayClaimOutcome.Nothing);
    }

    [Fact]
    public async Task A_heartbeat_cannot_fail_another_gateways_command()
    {
        var mine = await GatewayAsync();
        var other = await GatewayAsync(machine: "CRONUS-BC02");
        var id = await EnqueueAsync(mine);
        await ClaimAsync(mine);
        await using var ctx = _db.NewContext();

        (await Service(ctx).ApplyHeartbeatAsync(other, running: [], interrupted: [id])).Should().Be(0);

        (await LoadAsync(id)).Status.Should().Be(GatewayCommandStatus.Claimed);
    }

    [Fact]
    public async Task A_heartbeat_with_too_many_ids_is_refused()
    {
        var gateway = await GatewayAsync();
        await using var ctx = _db.NewContext();

        var act = () => Service(ctx).ApplyHeartbeatAsync(gateway, Enumerable.Range(1, 101).Select(i => (long)i).ToList(), []);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("runningCommandIds");
    }

    // ---------------------------------------------------------------- the sweep's pass

    [Fact]
    public async Task The_sweep_expires_a_command_nobody_collected()
    {
        var gateway = await GatewayAsync();
        var id = await EnqueueAsync(gateway);
        _clock.Advance(TimeSpan.FromMinutes(15));
        await using var ctx = _db.NewContext();

        var result = await Service(ctx).SweepAsync();

        result.Expired.Should().Be(1);
        var row = await LoadAsync(id);
        row.Status.Should().Be(GatewayCommandStatus.Expired);
        row.FailureMessage.Should().Be("The gateway was offline.");
    }

    [Fact]
    public async Task The_sweep_fails_a_running_command_after_ten_minutes_of_silence_and_keeps_one_the_heartbeat_lists()
    {
        var gateway = await GatewayAsync();
        var quiet = await EnqueueAsync(gateway);
        var listed = await EnqueueAsync(gateway);
        await ClaimAsync(gateway);
        await ClaimAsync(gateway);

        _clock.Advance(TimeSpan.FromMinutes(9));
        await using (var ctx = _db.NewContext())
        {
            await Service(ctx).ApplyHeartbeatAsync(gateway, running: [listed], interrupted: []);
        }
        _clock.Advance(TimeSpan.FromMinutes(2));
        await using (var ctx = _db.NewContext())
        {
            (await Service(ctx).SweepAsync()).FailedSilent.Should().Be(1);
        }

        var failed = await LoadAsync(quiet);
        failed.Status.Should().Be(GatewayCommandStatus.Failed);
        // The gateway was never heard from in this test, so the message blames it.
        failed.FailureMessage.Should().Be(GatewayCommandService.GatewaySilentMessage);
        (await LoadAsync(listed)).Status.Should().Be(GatewayCommandStatus.Claimed);
    }

    [Fact]
    public async Task A_command_a_live_gateway_stopped_listing_is_failed_with_its_own_message()
    {
        var gateway = await GatewayAsync();
        var id = await EnqueueAsync(gateway);
        await ClaimAsync(gateway);
        _clock.Advance(TimeSpan.FromMinutes(11));
        await using (var ctx = _db.NewContext())
        {
            await ctx.Gateways.Where(g => g.Id == gateway).ExecuteUpdateAsync(s => s.SetProperty(g => g.LastSeenAt, Now));
        }

        await using (var ctx = _db.NewContext())
        {
            await Service(ctx).SweepAsync();
        }

        (await LoadAsync(id)).FailureMessage.Should().Be(GatewayCommandService.CommandSilentMessage);
    }

    [Fact]
    public async Task The_sweep_clears_spent_request_tokens_and_keeps_live_ones()
    {
        var gateway = await GatewayAsync();
        await using (var ctx = _db.NewContext())
        {
            ctx.GatewaySeenTokens.AddRange(
                new GatewaySeenToken { OrganizationId = TestDb.DefaultOrgId, GatewayId = gateway, TokenId = "spent", ExpiresAt = Now.AddSeconds(-1) },
                new GatewaySeenToken { OrganizationId = TestDb.DefaultOrgId, GatewayId = gateway, TokenId = "live", ExpiresAt = Now.AddSeconds(60) });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            (await Service(ctx).SweepAsync()).TokensRemoved.Should().Be(1);
        }

        await using var check = _db.NewContext();
        (await check.GatewaySeenTokens.Select(t => t.TokenId).ToListAsync()).Should().Equal("live");
    }

    [Fact]
    public async Task The_sweep_leaves_another_organisations_commands_alone()
    {
        var foreign = await GatewayAsync(organizationId: TestDb.OtherOrgId);
        var other = new AmbientOrganizationContext { CurrentOrganizationId = TestDb.OtherOrgId };
        long id;
        await using (var ctx = _db.NewContext(other))
        {
            id = await Service(ctx, other).EnqueueAsync(new GatewayCommandRequest(foreign, GatewayVerb.RefreshInventory));
        }
        _clock.Advance(TimeSpan.FromHours(1));

        await using (var ctx = _db.NewContext())
        {
            (await Service(ctx).SweepAsync()).Expired.Should().Be(0);
        }

        await using var check = _db.NewContext(other);
        (await check.GatewayCommands.SingleAsync(c => c.Id == id)).Status.Should().Be(GatewayCommandStatus.Queued);
    }

    // ---------------------------------------------------------------- files

    [Fact]
    public async Task A_file_is_served_only_to_its_commands_gateway_while_the_command_runs()
    {
        var mine = await GatewayAsync();
        var other = await GatewayAsync(machine: "CRONUS-BC02");
        var content = RandomNumberGenerator.GetBytes(64);
        var id = await EnqueueAsync(mine, GatewayVerb.ImportLicence, files: [new GatewayCommandFileInput("CRONUS.bclicense", content)]);
        var secondId = await EnqueueAsync(mine, GatewayVerb.ImportLicence, files: [new GatewayCommandFileInput("Other.bclicense", content)]);
        var claim = await ClaimAsync(mine);
        var fileId = claim.Command!.Files.Single().Id;
        long secondFileId;
        await using (var ctx = _db.NewContext())
        {
            secondFileId = await ctx.GatewayCommandFiles.Where(f => f.CommandId == secondId).Select(f => f.Id).SingleAsync();
        }
        await using var work = _db.NewContext();
        var service = Service(work);

        var served = await service.OpenFileAsync(mine, id, fileId);
        served.Outcome.Should().Be(GatewayFileOutcome.Found);
        served.File!.Content.Should().Equal(content);
        served.File.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(content)));

        (await service.OpenFileAsync(other, id, fileId)).Outcome.Should().Be(GatewayFileOutcome.NotFound, "another gateway's command");
        (await service.OpenFileAsync(mine, id, secondFileId)).Outcome.Should().Be(GatewayFileOutcome.NotFound, "a file of another command");
        (await service.OpenFileAsync(mine, secondId, secondFileId)).Outcome.Should().Be(GatewayFileOutcome.NotRunning, "that command was never claimed");

        await service.CompleteAsync(mine, id, new GatewayCommandResultRequest { Succeeded = true });
        (await service.OpenFileAsync(mine, id, fileId)).Outcome.Should().Be(GatewayFileOutcome.NotRunning, "the command has finished");
    }

    [Fact]
    public async Task An_expired_commands_file_is_not_served()
    {
        var gateway = await GatewayAsync();
        var id = await EnqueueAsync(gateway, GatewayVerb.ImportLicence, files: [new GatewayCommandFileInput("CRONUS.bclicense", [1, 2, 3])]);
        long fileId;
        await using (var ctx = _db.NewContext())
        {
            fileId = await ctx.GatewayCommandFiles.Select(f => f.Id).SingleAsync();
        }
        _clock.Advance(TimeSpan.FromMinutes(20));
        await using var work = _db.NewContext();
        await Service(work).SweepAsync();

        (await Service(work).OpenFileAsync(gateway, id, fileId)).Outcome.Should().Be(GatewayFileOutcome.NotRunning);
    }

    [Fact]
    public async Task Another_organisations_command_file_is_not_served_even_to_a_gateway_with_the_same_id_space()
    {
        var foreign = await GatewayAsync(organizationId: TestDb.OtherOrgId);
        var other = new AmbientOrganizationContext { CurrentOrganizationId = TestDb.OtherOrgId };
        long id;
        GatewayClaimResult claim;
        await using (var ctx = _db.NewContext(other))
        {
            id = await Service(ctx, other).EnqueueAsync(new GatewayCommandRequest(foreign, GatewayVerb.ImportLicence,
                Files: [new GatewayCommandFileInput("CRONUS.bclicense", [1, 2, 3])]));
            claim = await Service(ctx, other).ClaimNextAsync(foreign);
        }

        // The same gateway id, asked under the default organisation: the filter hides it.
        await using var work = _db.NewContext();
        (await Service(work).OpenFileAsync(foreign, id, claim.Command!.Files.Single().Id)).Outcome
            .Should().Be(GatewayFileOutcome.NotFound);
    }

    [Theory]
    [InlineData("../evil.app")]
    [InlineData("C:\\evil.app")]
    [InlineData("")]
    public async Task A_file_name_that_is_a_path_is_refused(string name)
    {
        var gateway = await GatewayAsync();

        var act = () => EnqueueAsync(gateway, files: [new GatewayCommandFileInput(name, [1])]);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("files");
    }
}
