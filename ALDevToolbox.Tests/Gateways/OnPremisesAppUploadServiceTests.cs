using System.Text.Json;
using System.Text.Json.Nodes;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// Uploading apps by hand to an on-premises environment (#1786): dependency order, the
/// refusals made before anything is stored, installing now as one <c>publish-install</c>
/// command, booking for later and the worker's send, and how the history reads the
/// command back. See <c>.design/on-prem-gateway.md</c>, "Uploading apps by hand, as built (#1786)".
/// </summary>
public sealed class OnPremisesAppUploadServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid BaseId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid AddOnId = Guid.Parse("66666666-7777-8888-9999-000000000000");
    private static readonly Guid SystemId = Guid.Parse("63ca2fa4-4f03-4f2b-a480-172fef340d3f");

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(Start);
    private User _owner = null!;
    private int _gatewayId;
    private int _environmentId;

    public OnPremisesAppUploadServiceTests()
    {
        SetUpAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------- now

    [Fact]
    public async Task Apps_installed_now_go_in_dependency_order_as_one_command_and_read_back_from_it()
    {
        await SetPolicyAsync();

        var outcome = await InstallAsync(null, AddOn(), Base());

        outcome.Should().Be(new OnPremisesUploadOutcome(2, null));
        await using (var ctx = _db.NewContext())
        {
            var command = await ctx.GatewayCommands.AsNoTracking().SingleAsync();
            command.Verb.Should().Be(GatewayVerb.PublishInstall);
            command.RequestedByUserId.Should().Be(_owner.Id);
            var parameters = JsonNode.Parse(command.ParametersJson)!.AsObject();
            parameters["apps"]!.AsArray().Select(a => a!["file"]!.GetValue<string>())
                .Should().Equal(["CRONUS_Base.app", "CRONUS_AddOn.app"], "a dependency installs before the app that needs it");
            parameters["syncMode"]!.GetValue<string>().Should().Be(GatewaySyncModes.Add);

            var rows = await ctx.OeEnvironmentUpgradeActions.AsNoTracking().OrderBy(a => a.BatchOrder).ToListAsync();
            rows.Select(r => (r.Kind, r.Status, r.PackageFileName, r.GatewayCommandId, r.SentAt)).Should().Equal(
                (UpgradeActionKind.UploadApp, UpgradeActionStatus.Sent, "CRONUS_Base.app", (long?)command.Id, (DateTime?)Start.UtcDateTime),
                (UpgradeActionKind.UploadApp, UpgradeActionStatus.Sent, "CRONUS_AddOn.app", (long?)command.Id, (DateTime?)Start.UtcDateTime));
            rows.Should().AllSatisfy(r => r.PackageContent.Should().BeNull("the command holds its own copy"));
            rows.Select(r => r.BatchId).Distinct().Should().ContainSingle().Which.Should().NotBeNull();
        }

        var waiting = await HistoryAsync();
        waiting.Should().AllSatisfy(r => (r.Status, r.SentAt).Should().Be((UpgradeActionStatus.Pending, (DateTime?)Start.UtcDateTime)), "sent and not finished reads as installing");

        await FinishCommandAsync(GatewayCommandStatus.Failed, "CRONUS AddOn failed to install.", new GatewayPublishInstallResult
        {
            Apps =
            [
                new GatewayPublishInstallAppResult { File = "CRONUS_Base.app", Outcome = GatewayPublishInstallOutcomes.Installed },
                new GatewayPublishInstallAppResult { File = "CRONUS_AddOn.app", Outcome = GatewayPublishInstallOutcomes.Failed, Message = "The schema sync failed." },
            ],
        }.ToJson());
        var done = (await HistoryAsync()).OrderBy(r => r.BatchOrder).ToList();
        done.Select(r => (r.Status, r.Outcome)).Should().Equal(
            (UpgradeActionStatus.Sent, "Installed."),
            (UpgradeActionStatus.Failed, "The schema sync failed."));
    }

    [Fact]
    public async Task What_the_customers_settings_refuse_is_said_before_anything_is_stored()
    {
        await SetPolicyAsync(allow: ["inventory", "restart"]);
        (await Refusal(null, Base())).Should().StartWith(OnPremisesDeployFacts.NotAllowed).And.Contain("may not be published");

        await SetPolicyAsync(publishers: ["Microsoft"]);
        (await Refusal(null, Base())).Should().Contain("apps from CRONUS may not be installed");

        await SetPolicyAsync(allowUnsigned: false);
        (await Refusal(null, Base())).Should().Contain("isn't signed");
        await InstallAsync(null, Base(signed: true));

        await using var ctx = _db.NewContext();
        (await ctx.GatewayCommands.CountAsync()).Should().Be(1, "only the signed one went");
    }

    [Fact]
    public async Task A_missing_dependency_or_an_unreadable_file_is_refused()
    {
        await SetPolicyAsync();
        await InstalledAsync((SystemId, "System Application", "25.0.0.0"));

        (await Refusal(null, AddOn())).Should().Contain("needs CRONUS Base by CRONUS, which isn't installed");
        await InstalledAsync((BaseId, "CRONUS Base", "0.9.0.0"));
        (await Refusal(null, AddOn())).Should().Contain("needs CRONUS Base 1.0.0.0 or later");

        var act = () => InstallAsync(null, new OnPremisesUploadFile([1, 2, 3, 4], "Vendor.app"));
        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["App"].Should().Contain("can't be read as an app file");

        await using var ctx = _db.NewContext();
        (await ctx.GatewayCommands.CountAsync()).Should().Be(0);
        (await ctx.OeEnvironmentUpgradeActions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Installing_now_outside_the_customers_change_hours_is_refused_and_the_form_offers_their_opening()
    {
        await SetPolicyAsync(changeHours: "20:00-06:00", timeZone: "UTC");

        (await Refusal(null, Base())).Should().Contain("20:00 to 06:00");

        await using var ctx = _db.NewContext();
        var plan = (await Service(ctx).PlanAsync(_environmentId, []))!;
        (plan.InsideChangeHoursNow, plan.ChangeHours, plan.DefaultRunAt).Should().Be(
            (false, "20:00-06:00", new DateTime(2026, 10, 15, 20, 0, 0, DateTimeKind.Utc)));
    }

    // ---------------------------------------------------------------- booked

    [Fact]
    public async Task A_booked_upload_waits_with_its_files_and_the_worker_sends_the_batch_once()
    {
        await SetPolicyAsync(changeHours: "20:00-06:00", timeZone: "UTC");
        var runAt = Start.UtcDateTime.AddHours(12);

        var outcome = await InstallAsync(runAt, AddOn(), Base());

        outcome.Should().Be(new OnPremisesUploadOutcome(2, runAt));
        int first;
        await using (var ctx = _db.NewContext())
        {
            (await ctx.GatewayCommands.CountAsync()).Should().Be(0, "nothing reaches the gateway before its time");
            var rows = await ctx.OeEnvironmentUpgradeActions.AsNoTracking().OrderBy(a => a.BatchOrder).ToListAsync();
            rows.Should().AllSatisfy(r =>
            {
                (r.Status, r.ExecuteAfter, r.SentAt).Should().Be((UpgradeActionStatus.Pending, runAt, (DateTime?)null));
                r.PackageContent.Should().NotBeNull();
            });
            first = rows[0].Id;
            (await Service(ctx).DueBookingsAsync()).Should().BeEmpty();
        }

        _clock.Advance(runAt - _clock.GetUtcNow().UtcDateTime);
        await GatewayCheckedInAsync();
        await using (var ctx = _db.NewContext())
        {
            (await Service(ctx).DueBookingsAsync()).Should().Equal([(first, (int?)_owner.Id)], "one per upload, not one per app");
            (await Service(ctx).RunBookingAsync(first)).Should().Be(ServiceTierBookingRun.Sent);
        }
        await using (var ctx = _db.NewContext())
        {
            var command = await ctx.GatewayCommands.AsNoTracking().SingleAsync();
            JsonNode.Parse(command.ParametersJson)!["apps"]!.AsArray().Count.Should().Be(2);
            var rows = await ctx.OeEnvironmentUpgradeActions.AsNoTracking().ToListAsync();
            rows.Should().AllSatisfy(r =>
            {
                (r.Status, r.GatewayCommandId).Should().Be((UpgradeActionStatus.Sent, (long?)command.Id));
                r.PackageContent.Should().BeNull();
            });
            (await Service(ctx).RunBookingAsync(first)).Should().Be(ServiceTierBookingRun.Gone, "it runs once");
        }
    }

    [Fact]
    public async Task A_booking_outside_change_hours_is_refused_and_one_app_cancelled_from_a_batch_stays_behind()
    {
        await SetPolicyAsync(changeHours: "20:00-06:00", timeZone: "UTC");
        var noon = () => InstallAsync(Start.UtcDateTime.AddHours(3), Base());
        (await noon.Should().ThrowAsync<PlanValidationException>()).Which.Errors["runAt"].Should().Contain("20:00 to 06:00");

        var runAt = Start.UtcDateTime.AddHours(12);
        await InstallAsync(runAt, Base(), Other());
        await using (var ctx = _db.NewContext())
        {
            var other = await ctx.OeEnvironmentUpgradeActions.SingleAsync(a => a.PackageFileName == "CRONUS_Other.app");
            await new UpgradeActionService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), null!, _clock,
                NullLogger<UpgradeActionService>.Instance).CancelUpgradeActionAsync(other.Id);
        }

        _clock.Advance(runAt - _clock.GetUtcNow().UtcDateTime);
        await GatewayCheckedInAsync();
        await using (var ctx = _db.NewContext())
        {
            var due = await Service(ctx).DueBookingsAsync();
            (await Service(ctx).RunBookingAsync(due.Single().Id)).Should().Be(ServiceTierBookingRun.Sent);
        }
        await using var check = _db.NewContext();
        var parameters = JsonNode.Parse((await check.GatewayCommands.AsNoTracking().SingleAsync()).ParametersJson)!;
        parameters["apps"]!.AsArray().Select(a => a!["file"]!.GetValue<string>()).Should().Equal(["CRONUS_Base.app"]);
    }

    [Fact]
    public async Task A_booked_upload_the_gateway_failed_is_settled_once_and_one_too_late_is_never_sent()
    {
        await SetPolicyAsync();
        var runAt = Start.UtcDateTime.AddHours(1);
        await InstallAsync(runAt, Base());
        _clock.Advance(TimeSpan.FromHours(1));
        await GatewayCheckedInAsync();
        int id;
        await using (var ctx = _db.NewContext())
        {
            id = (await Service(ctx).DueBookingsAsync()).Single().Id;
            await Service(ctx).RunBookingAsync(id);
        }
        await FinishCommandAsync(GatewayCommandStatus.Expired, null, null);
        await using (var ctx = _db.NewContext())
        {
            (await Service(ctx).SettleFailedBookingsAsync()).Should().Equal(id);
            (await Service(ctx).SettleFailedBookingsAsync()).Should().BeEmpty("the person is told once");
            var row = await ctx.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync();
            (row.Status, row.Outcome).Should().Be((UpgradeActionStatus.Failed,
                "The gateway did not pick this up in time, so nothing was installed. Check that the gateway is online, then upload it again."));
        }

        await InstallAsync(Start.UtcDateTime.AddHours(2), Other());
        _clock.Advance(TimeSpan.FromHours(4));
        await GatewayCheckedInAsync();
        await using (var ctx = _db.NewContext())
        {
            var late = (await Service(ctx).DueBookingsAsync()).Single().Id;
            (await Service(ctx).RunBookingAsync(late)).Should().Be(ServiceTierBookingRun.Failed);
            (await ctx.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.Id == late)).Outcome.Should().Contain("two hours of the booked time");
            (await ctx.GatewayCommands.CountAsync()).Should().Be(1);
        }
    }

    // ---------------------------------------------------------------- building blocks

    private static OnPremisesUploadFile Base(bool signed = false) =>
        new(AppFiles.Build(BaseId, "CRONUS Base", "CRONUS", "1.0.0.0", signed), "CRONUS_Base.app");

    private static OnPremisesUploadFile Other() =>
        new(AppFiles.Build(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "CRONUS Other", "CRONUS", "1.0.0.0"), "CRONUS_Other.app");

    private static OnPremisesUploadFile AddOn() => new(AppFiles.Build(AddOnId, "CRONUS AddOn", "CRONUS", "1.0.0.0", manifest: $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
          <App Id="{AddOnId}" Name="CRONUS AddOn" Publisher="CRONUS" Brief="" Description="" Version="1.0.0.0" CompatibilityId="0.0.0.0" Platform="25.0.0.0" Application="25.0.0.0" Runtime="14.0" Target="OnPrem" ShowMyCode="False" />
          <Dependencies>
            <Dependency Id="{BaseId}" Name="CRONUS Base" Publisher="CRONUS" MinVersion="1.0.0.0" />
          </Dependencies>
        </Package>
        """), "CRONUS_AddOn.app");

    private OnPremisesAppUploadService Service(AppDbContext ctx) => new(
        ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
        new GatewayCommandService(ctx, _db.OrgContext, new GatewayCommandSignal(), _clock, NullLogger<GatewayCommandService>.Instance),
        _clock, NullLogger<OnPremisesAppUploadService>.Instance);

    private async Task<OnPremisesUploadOutcome> InstallAsync(DateTime? runAt, params OnPremisesUploadFile[] files)
    {
        await using var ctx = _db.NewContext();
        return await Service(ctx).InstallAsync(_environmentId, files, runAt);
    }

    private async Task<string> Refusal(DateTime? runAt, params OnPremisesUploadFile[] files)
    {
        var act = () => InstallAsync(runAt, files);
        return (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Values.Single();
    }

    private async Task<List<UpgradeActionRow>> HistoryAsync()
    {
        await using var ctx = _db.NewContext();
        var rows = (await ctx.OeEnvironmentUpgradeActions.AsNoTracking().ToListAsync())
            .Select(a => new UpgradeActionRow(a.Id, a.ProjectId, a.EnvironmentId, a.Kind, a.Status, a.RequestedBy, a.RequestedAt,
                a.ExecuteAfter, a.SentAt, a.Outcome, a.CancelledBy, a.CancelledAt,
                PackageFileName: a.PackageFileName, BatchId: a.BatchId, BatchOrder: a.BatchOrder, GatewayCommandId: a.GatewayCommandId))
            .ToList();
        return await OnPremisesUploadResult.ApplyAsync(ctx, rows, CancellationToken.None);
    }

    private async Task InstalledAsync(params (Guid Id, string Name, string Version)[] apps)
    {
        await using var ctx = _db.NewContext();
        var tenant = await ctx.GatewayInstanceTenants.SingleAsync();
        foreach (var (id, name, version) in apps)
        {
            ctx.GatewayInstanceApps.Add(new GatewayInstanceApp
            {
                OrganizationId = TestDb.DefaultOrgId, TenantRowId = tenant.Id, AppId = id.ToString(), Name = name, Publisher = "CRONUS",
                Version = version, Scope = "Tenant", IsInstalled = true, IsPublished = true,
            });
        }
        await ctx.SaveChangesAsync();
    }

    private async Task GatewayCheckedInAsync()
    {
        await using var ctx = _db.NewContext();
        var now = _clock.GetUtcNow().UtcDateTime;
        await ctx.Gateways.Where(g => g.Id == _gatewayId).ExecuteUpdateAsync(s => s.SetProperty(g => g.LastSeenAt, now));
    }

    private async Task FinishCommandAsync(GatewayCommandStatus status, string? failure, JsonObject? result)
    {
        await using var ctx = _db.NewContext();
        await ctx.GatewayCommands.ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Status, status)
            .SetProperty(c => c.FailureMessage, failure)
            .SetProperty(c => c.ResultJson, result == null ? null : result.ToJsonString())
            .SetProperty(c => c.FinishedAt, _clock.GetUtcNow().UtcDateTime));
    }

    private async Task SetUpAsync()
    {
        _owner = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Editor, "owner@cronus.example");
        _db.OrgContext.CurrentUserId = _owner.Id;
        _db.OrgContext.IsSiteAdmin = false;

        var now = Start.UtcDateTime;
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Coffee", Visibility = ProjectVisibility.Public,
            CreatedByUserId = _owner.Id, CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeProjects.Add(project);
        var gateway = new Gateway
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS server room", MachineName = "CRONUS-BC01",
            KeyFingerprint = Guid.NewGuid().ToString("N"),
            State = GatewayState.Approved, RegisteredAt = now, KeyRegisteredAt = now, LastSeenAt = now,
        };
        ctx.Gateways.Add(gateway);
        await ctx.SaveChangesAsync();
        var tier = new GatewayInstance
        {
            OrganizationId = TestDb.DefaultOrgId, GatewayId = gateway.Id, Machine = "CRONUS-BC01", Name = "BC250", State = "Running",
            Version = "25.4.29661.29927", DatabaseServer = "CRONUS-SQL01", DatabaseName = "CRONUS", Multitenant = false,
            FirstReportedAt = now, ReportedAt = now, ReadAt = now,
        };
        tier.Tenants.Add(new GatewayInstanceTenant { OrganizationId = TestDb.DefaultOrgId, TenantId = "default", ReadAt = now, AppsReadAt = now });
        ctx.GatewayInstances.Add(tier);
        await ctx.SaveChangesAsync();
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production",
            Kind = OeEnvironmentKind.OnPremises, GatewayInstanceId = tier.Id, TenantId = "default",
            Status = BcEnvironmentStatus.Active, FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        _gatewayId = gateway.Id;
        _environmentId = env.Id;
    }

    /// <summary>The customer's settings: one database published and restarted on BC01.</summary>
    private async Task SetPolicyAsync(string[]? allow = null, string[]? publishers = null, bool allowUnsigned = true,
        string? changeHours = null, string? timeZone = null)
    {
        var policy = new GatewayPolicyReport
        {
            State = GatewayPolicyStates.Valid,
            TimeZone = timeZone,
            Instances =
            [
                new GatewayPolicyInstance
                {
                    Machine = "CRONUS-BC01", Name = "BC250",
                    Allow = allow ?? ["inventory", "publish", "restart", "unpublish"], Tenants = ["default"],
                    ChangeHours = changeHours, Publishers = publishers, AllowUnsigned = allowUnsigned,
                },
            ],
            Databases =
            [
                new GatewayPolicyDatabase
                {
                    Name = "CRONUS-SQL01/CRONUS",
                    PublishOn = new GatewayPolicyTier { Machine = "CRONUS-BC01", Instance = "BC250" },
                    Restart = [new GatewayPolicyTier { Machine = "CRONUS-BC01", Instance = "BC250" }],
                },
            ],
        };
        await using var ctx = _db.NewContext();
        await ctx.Gateways.Where(g => g.Id == _gatewayId).ExecuteUpdateAsync(s => s
            .SetProperty(g => g.EffectivePolicyJson, JsonSerializer.Serialize(policy)));
    }
}
