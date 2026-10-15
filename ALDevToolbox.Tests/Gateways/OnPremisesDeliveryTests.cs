using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Tests.Infrastructure;
using ALDevToolbox.Tests.ObjectExplorer;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// Deploying to a linked on-premises service tier (#1367): the shared run in
/// <see cref="DeliveryService"/> against <see cref="GatewayDeliveryTarget"/>, with a fake
/// gateway on the other side of the real command queue - it claims the command, posts
/// progress and reports per-app outcomes the way the publish-install verb does. Also the
/// rules on-premises adds: no deferred schedules, Force sync only where the customer's
/// gateway settings allow it, no AI deployments to production, open sessions counted
/// across every tier on the database, and an offline gateway failing the run at once.
/// See <c>.design/on-prem-gateway.md</c>, "Deploying to on-premises".
/// </summary>
public sealed class OnPremisesDeliveryTests : IDisposable
{
    private const string CoreId = "c0ffee00-0000-0000-0000-000000000001";
    private const string SalesId = "c0ffee00-0000-0000-0000-000000000002";
    private const string ReportsId = "c0ffee00-0000-0000-0000-000000000003";

    private readonly TestDb _db = new();
    private readonly DeliveryQueue _queue = new();
    private readonly GatewayCommandSignal _signal = new();

    public OnPremisesDeliveryTests()
    {
        _db.OrgContext.IsSiteAdmin = true; // manage rights via the project owner
    }

    public void Dispose() => _db.Dispose();

    // ── The run, end to end against a fake gateway ─────────────────────────────

    [Fact]
    public async Task A_deployment_goes_to_the_gateway_as_one_command_and_each_app_gets_its_own_outcome()
    {
        var seed = await SeedAsync(installed: [(SalesId, "CRONUS Sales", "1.0.1.0")], previous: [(CoreId, "CRONUS Core", "0.9.0.0")]);
        var id = await DeployNowAsync(seed);

        GatewayCommandMessage? seen = null;
        byte[]? downloaded = null;
        await RunWithGatewayAsync(id, seed.GatewayId, async (gateway, message) =>
        {
            seen = message;
            downloaded = await gateway.DownloadAsync(message, 0);
            return (
                ["Publishing CRONUS Core 1.0.0.0 on BC250.", "Installing CRONUS Reports 1.0.2.0."],
                Succeeded(
                    App(CoreId, GatewayPublishInstallOutcomes.Upgraded, fromVersion: "0.9.0.0", signer: "CN=CRONUS A/S"),
                    App(ReportsId, GatewayPublishInstallOutcomes.Installed)));
        });

        // What the gateway was asked to do.
        var parameters = seen!.Parameters;
        parameters["machine"]!.GetValue<string>().Should().Be("CRONUS-BC01");
        parameters["instance"]!.GetValue<string>().Should().Be("BC250");
        parameters["syncMode"]!.GetValue<string>().Should().Be(GatewaySyncModes.Add);
        parameters["unpublishPrevious"]!.GetValue<bool>().Should().BeTrue("the pipeline's default");
        parameters.ContainsKey("tenant").Should().BeFalse("a single-tenant server is left to its default tenant");
        var apps = parameters["apps"]!.AsArray();
        apps.Select(a => a!["id"]!.GetValue<string>()).Should().Equal([CoreId, ReportsId],
            "in dependency order, without the app the tier already has at this version");
        apps.Select(a => a!["version"]!.GetValue<string>()).Should().Equal("1.0.0.0", "1.0.2.0");
        seen.Files.Should().HaveCount(2);
        downloaded.Should().Equal(seed.Packages[0], "the file is read from the build artifact it references");

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.Include(d => d.Results).SingleAsync(d => d.Id == id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Deployed);
        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        results.Select(r => r.Status).Should().Equal(
            ProjectDeliveryResultStatus.Completed, ProjectDeliveryResultStatus.Skipped, ProjectDeliveryResultStatus.Completed);
        results[0].PreviousVersion.Should().Be("0.9.0.0");
        results[0].Message.Should().Contain("Signed by CN=CRONUS A/S.");
        results[0].OperationId.Should().BeNull("the gateway has no operation id of Business Central's own");
        results[1].Message.Should().Be("Already on 1.0.1.0.");
        delivery.TiersRestarted.Should().BeFalse();
        delivery.DiagnosticsLog.Should().Contain("through the gateway")
            .And.Contain("Queued for the gateway as command")
            .And.Contain("Gateway: Publishing CRONUS Core 1.0.0.0 on BC250.")
            .And.Contain("Gateway: Installing CRONUS Reports 1.0.2.0.")
            .And.Contain(DeliveryService.TiersNotRestartedLine);
    }

    [Fact]
    public async Task An_app_the_gateway_finds_already_installed_is_skipped_and_the_run_goes_on()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);

        await RunWithGatewayAsync(id, seed.GatewayId, (_, _) => Task.FromResult<(string[], GatewayCommandResultRequest)>((
            [],
            Succeeded(
                App(CoreId, GatewayPublishInstallOutcomes.AlreadyInstalled, message: "Already installed at 1.0.0.0."),
                App(SalesId, GatewayPublishInstallOutcomes.Installed),
                App(ReportsId, GatewayPublishInstallOutcomes.Installed)))));

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Deployed);
        delivery.Results.OrderBy(r => r.Ordering).Select(r => r.Status).Should().Equal(
            ProjectDeliveryResultStatus.Skipped, ProjectDeliveryResultStatus.Completed, ProjectDeliveryResultStatus.Completed);
        delivery.Results.Single(r => r.Ordering == 0).Message.Should().Be("Already installed at 1.0.0.0.");
    }

    [Fact]
    public async Task A_failed_app_fails_the_run_and_the_apps_after_it_are_skipped()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);

        await RunWithGatewayAsync(id, seed.GatewayId, (_, _) => Task.FromResult<(string[], GatewayCommandResultRequest)>((
            ["Publishing CRONUS Sales 1.0.1.0 on BC250."],
            Failed("CRONUS Sales 1.0.1.0 failed to sync.",
                App(CoreId, GatewayPublishInstallOutcomes.Installed),
                App(SalesId, GatewayPublishInstallOutcomes.Failed, message: "The schema sync found a destructive change in table 50100."),
                App(ReportsId, GatewayPublishInstallOutcomes.Skipped)))));

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.Results.OrderBy(r => r.Ordering).Select(r => r.Status).Should().Equal(
            ProjectDeliveryResultStatus.Completed, ProjectDeliveryResultStatus.Failed, ProjectDeliveryResultStatus.Skipped);
        delivery.FailureMessage.Should().Be("CRONUS Sales 1.0.1.0 failed: The schema sync found a destructive change in table 50100.");
        delivery.DiagnosticsLog.Should().NotContain(DeliveryService.TiersNotRestartedLine, "only a deployment that went through says so");
    }

    [Fact]
    public async Task A_refused_app_is_recorded_as_refused_not_failed()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);

        await RunWithGatewayAsync(id, seed.GatewayId, (_, _) => Task.FromResult<(string[], GatewayCommandResultRequest)>((
            [],
            Failed("The gateway refused CRONUS Core.",
                App(CoreId, GatewayPublishInstallOutcomes.Refused, message: "Not allowed by the customer's gateway settings: unsigned apps may not be published on CRONUS-BC01 / BC250."),
                App(SalesId, GatewayPublishInstallOutcomes.Skipped),
                App(ReportsId, GatewayPublishInstallOutcomes.Skipped)))));

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.Results.OrderBy(r => r.Ordering).Select(r => r.Status).Should().Equal(
            ProjectDeliveryResultStatus.Refused, ProjectDeliveryResultStatus.Skipped, ProjectDeliveryResultStatus.Skipped);
        delivery.FailureMessage.Should().StartWith("The gateway refused CRONUS Core 1.0.0.0: Not allowed by the customer's gateway settings");
        delivery.DiagnosticsLog.Should().Contain("REFUSED CRONUS Core 1.0.0.0");
    }

    [Fact]
    public async Task A_command_that_expires_uncollected_fails_the_first_app_with_the_queues_reason()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);

        // Nobody collects it: the queue's own sweep ends it as expired.
        var run = Task.Run(async () =>
        {
            await using var ctx = _db.NewContext();
            await NewService(ctx).RunDeliveryAsync(id);
        });
        var commandId = await WaitForCommandAsync(seed.GatewayId);
        await using (var ctx = _db.NewContext())
        {
            await ctx.GatewayCommands.Where(c => c.Id == commandId).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, GatewayCommandStatus.Expired)
                .SetProperty(c => c.FailureMessage, GatewayCommandService.OfflineMessage));
        }
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        results[0].Status.Should().Be(ProjectDeliveryResultStatus.Failed);
        results[0].Message.Should().Be(GatewayCommandService.OfflineMessage);
        results.Skip(1).Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Skipped);
    }

    [Fact]
    public async Task A_due_deployment_whose_gateway_is_offline_fails_at_once_and_queues_nothing()
    {
        var seed = await SeedAsync(gatewayLastSeen: DateTime.UtcNow.AddHours(-2));
        var id = await DeployNowAsync(seed);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RunDeliveryAsync(id);
        }

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Be(
            "The gateway was offline, so nothing was sent. Check that the gateway service is running on CRONUS-GW01, then deploy again.");
        await using var read = _db.NewContext();
        (await read.GatewayCommands.CountAsync()).Should().Be(0);
    }

    // ── What on-premises refuses ───────────────────────────────────────────────

    [Theory]
    [InlineData(BcDeploymentSchedule.NextMinorUpdate)]
    [InlineData(BcDeploymentSchedule.NextMajorUpdate)]
    public async Task A_deferred_schedule_is_refused_when_the_deployment_is_made(string schedule)
    {
        var seed = await SeedAsync();
        await using var ctx = _db.NewContext();
        await ctx.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DeploymentSchedule, schedule));

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Values
            .Should().Contain(DeliveryService.WaitsForUpdateRefusal("CRONUS Test"));
        _queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_deferred_schedule_on_a_deployment_already_made_fails_when_it_runs()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjectDeliveries.Where(d => d.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeploymentSchedule, BcDeploymentSchedule.NextMinorUpdate));
            await NewService(ctx).RunDeliveryAsync(id);
        }

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Be(DeliveryService.WaitsForUpdateRefusal("CRONUS Test"));
    }

    [Fact]
    public async Task Force_sync_is_refused_where_the_customers_gateway_settings_do_not_allow_it()
    {
        var seed = await SeedAsync(allowForceSync: false);
        await using var ctx = _db.NewContext();
        await ctx.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.SchemaSyncMode, BcSyncMode.ForceSync));

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["SchemaSyncMode"]
            .Should().StartWith(OnPremisesDeployFacts.NotAllowed).And.Contain("Force sync");
    }

    [Fact]
    public async Task Force_sync_goes_to_the_gateway_where_the_customers_gateway_settings_allow_it()
    {
        var seed = await SeedAsync(allowForceSync: true);
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.SchemaSyncMode, BcSyncMode.ForceSync));
        }
        var id = await DeployNowAsync(seed);

        string? syncMode = null;
        await RunWithGatewayAsync(id, seed.GatewayId, (_, message) =>
        {
            syncMode = message.Parameters["syncMode"]!.GetValue<string>();
            return Task.FromResult<(string[], GatewayCommandResultRequest)>(([],
                Succeeded(App(CoreId, "installed"), App(SalesId, "installed"), App(ReportsId, "installed"))));
        });

        syncMode.Should().Be(GatewaySyncModes.ForceSync);
    }

    [Fact]
    public async Task A_one_time_force_sync_retry_is_refused_where_the_settings_do_not_allow_it()
    {
        var seed = await SeedAsync(allowForceSync: false);
        var id = await DeployNowAsync(seed);
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjectDeliveries.Where(d => d.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, ProjectDeliveryStatus.Failed));
        }

        var act = () => NewService(_db.NewContext()).ReleaseAgainAsync(id, forceSyncOnce: true);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["SchemaSyncMode"]
            .Should().Be(OnPremisesDeployFacts.ForceSyncRefusal("CRONUS Test"));
    }

    [Fact]
    public async Task An_ai_assistant_cannot_deploy_to_on_premises_production_even_where_the_organisation_lets_it_deploy_to_production()
    {
        var seed = await SeedAsync(environmentType: "Production");
        await AllowAgentsInProductionAsync();

        var act = () => NewService(_db.NewContext()).DeployNowForAgentAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["ProjectEnvironment"]
            .Should().Be(DeliveryService.OnPremisesProductionRefusedForAgents("CRONUS Test"));
    }

    [Fact]
    public async Task An_ai_assistant_can_deploy_to_an_on_premises_test_tier()
    {
        var seed = await SeedAsync(environmentType: "Sandbox");

        var id = await NewService(_db.NewContext()).DeployNowForAgentAsync(seed.ReleasePipelineId, seed.BuildId);

        (await LoadAsync(id)).StartedByAgent.Should().BeTrue();
    }

    // ── Open sessions across the tiers on one database ─────────────────────────

    [Fact]
    public async Task Open_sessions_are_counted_on_every_tier_that_shares_the_database()
    {
        var seed = await SeedAsync(sessions: 1);
        await using (var ctx = _db.NewContext())
        {
            // A second tier on the same database, serving the web client, publishes nothing
            // but its sessions see the new version all the same.
            ctx.GatewayInstances.Add(Instance(seed.GatewayId, "CRONUS-BC02", "BC250", sessions: 4));
            // A tier on another database does not count.
            ctx.GatewayInstances.Add(Instance(seed.GatewayId, "CRONUS-BC03", "BC250", sessions: 9, database: "CRONUS Test 2"));
            await ctx.SaveChangesAsync();
        }

        var check = await NewService(_db.NewContext()).CheckOpenSessionsAsync(seed.ReleasePipelineId);

        check.Sessions.Should().Be(5);
        check.Tiers.Should().Be(2);
        check.NeedsConfirmation.Should().BeTrue();
    }

    [Fact]
    public async Task No_open_sessions_needs_no_confirmation()
    {
        var seed = await SeedAsync(sessions: 0);

        var check = await NewService(_db.NewContext()).CheckOpenSessionsAsync(seed.ReleasePipelineId);

        check.Sessions.Should().Be(0);
        check.NeedsConfirmation.Should().BeFalse();
    }

    // ── The result mapping ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(GatewayPublishInstallOutcomes.Installed, true, DeliveryAppOutcome.Installed)]
    [InlineData(GatewayPublishInstallOutcomes.Upgraded, true, DeliveryAppOutcome.Installed)]
    [InlineData(GatewayPublishInstallOutcomes.AlreadyInstalled, false, DeliveryAppOutcome.AlreadyInstalled)]
    [InlineData(GatewayPublishInstallOutcomes.Failed, false, DeliveryAppOutcome.Failed)]
    [InlineData(GatewayPublishInstallOutcomes.Refused, false, DeliveryAppOutcome.Refused)]
    [InlineData(GatewayPublishInstallOutcomes.Skipped, false, DeliveryAppOutcome.Skipped)]
    public void Each_gateway_outcome_maps_onto_the_runs_outcome(string outcome, bool completed, DeliveryAppOutcome expected)
    {
        var final = new GatewayCommandProgress(GatewayCommandStatus.Succeeded, null,
            Succeeded(App(CoreId, outcome, fromVersion: "0.9.0.0")).Result, string.Empty, 0);

        var mapped = GatewayDeliveryTarget.OutcomeFor(final, 0, Package());

        mapped.Completed.Should().Be(completed);
        mapped.Outcome.Should().Be(completed ? DeliveryAppOutcome.Installed : expected);
        mapped.TiersRestarted.Should().BeFalse();
        if (outcome == GatewayPublishInstallOutcomes.Upgraded) mapped.FromVersion.Should().Be("0.9.0.0");
    }

    [Fact]
    public void A_command_that_failed_without_a_result_gives_its_own_reason_to_the_app()
    {
        var final = new GatewayCommandProgress(GatewayCommandStatus.Failed, GatewayCommandService.InterruptedMessage, null, string.Empty, 0);

        var mapped = GatewayDeliveryTarget.OutcomeFor(final, 0, Package());

        mapped.Completed.Should().BeFalse();
        mapped.Outcome.Should().Be(DeliveryAppOutcome.Failed);
        mapped.Message.Should().Be(GatewayCommandService.InterruptedMessage);
        mapped.TiersRestarted.Should().BeNull("nothing was said about restarts");
    }

    [Fact]
    public void Gateway_lines_lose_the_time_stamp_the_deployment_log_puts_back()
    {
        GatewayDeliveryTarget.GatewayLines("2026-10-15 09:12:01Z Publishing CRONUS Core.\n\n2026-10-15 09:12:04Z Synced.\n")
            .Should().Equal("Publishing CRONUS Core.", "Synced.");
    }

    [Fact]
    public void A_signature_is_read_from_the_package_length_in_the_header()
    {
        var unsigned = SyntheticApp.Build(CoreId, "CRONUS Core", "CRONUS", "1.0.0.0");
        GatewayDeliveryTarget.CarriesSignature(unsigned).Should().BeFalse("the synthetic header says nothing follows the package");
        GatewayDeliveryTarget.CarriesSignature([1, 2, 3]).Should().BeFalse();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private sealed record Seed(int ProjectId, int GatewayId, int InstanceId, int EnvironmentId, int ReleasePipelineId, int BuildId, List<byte[]> Packages);

    private static DeliveryPackage Package() =>
        new(1, "CRONUS Core_1.0.0.0.app", Guid.Parse(CoreId), "CRONUS Core", "1.0.0.0", 3, _ => Task.FromResult<byte[]>([]));

    private static GatewayPublishInstallAppResult App(
        string id, string outcome, string? fromVersion = null, string? message = null, string? signer = null) => new()
    {
        Id = id,
        Outcome = outcome,
        FromVersion = fromVersion,
        Message = message,
        SignerSubject = signer,
    };

    private static GatewayCommandResultRequest Succeeded(params GatewayPublishInstallAppResult[] apps) => new()
    {
        Succeeded = true,
        Result = new GatewayPublishInstallResult { Apps = apps, Restarted = false }.ToJson(),
    };

    private static GatewayCommandResultRequest Failed(string why, params GatewayPublishInstallAppResult[] apps) => new()
    {
        Succeeded = false,
        FailureMessage = why,
        Result = new GatewayPublishInstallResult { Apps = apps, Restarted = false }.ToJson(),
    };

    private async Task<Seed> SeedAsync(
        IReadOnlyList<(string Id, string Name, string Version)>? installed = null,
        IReadOnlyList<(string Id, string Name, string Version)>? previous = null,
        bool allowForceSync = false,
        string environmentType = "Sandbox",
        int? sessions = 0,
        DateTime? gatewayLastSeen = null)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;

        var project = new OeProject { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS " + Guid.NewGuid().ToString("N"), CreatedAt = now, UpdatedAt = now };
        ctx.OeProjects.Add(project);
        var pipeline = new OePipeline { OrganizationId = TestDb.DefaultOrgId, Project = project, Name = "Build", CreatedAt = now, UpdatedAt = now };
        ctx.OePipelines.Add(pipeline);

        var policy = new GatewayPolicyReport
        {
            State = GatewayPolicyStates.Valid,
            Instances =
            [
                new GatewayPolicyInstance
                {
                    Machine = "CRONUS-BC01", Name = "BC250", Environment = environmentType == "Production" ? "production" : "test",
                    Allow = ["inventory", "publish"], AllowForceSync = allowForceSync,
                },
            ],
        };
        var gateway = new Gateway
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS hosting", MachineName = "CRONUS-GW01",
            KeyFingerprint = Guid.NewGuid().ToString("N"),
            State = GatewayState.Approved, RegisteredAt = now, KeyRegisteredAt = now,
            LastSeenAt = gatewayLastSeen ?? now,
            EffectivePolicyJson = System.Text.Json.JsonSerializer.Serialize(policy),
        };
        ctx.Gateways.Add(gateway);
        await ctx.SaveChangesAsync();

        var instance = Instance(gateway.Id, "CRONUS-BC01", "BC250", sessions);
        var tenant = new GatewayInstanceTenant
        {
            OrganizationId = TestDb.DefaultOrgId, TenantId = "default", SessionCount = sessions, ReadAt = now, AppsReadAt = now,
        };
        foreach (var (appId, name, version) in (installed ?? []).Concat(previous ?? []))
        {
            tenant.Apps.Add(new GatewayInstanceApp
            {
                OrganizationId = TestDb.DefaultOrgId, AppId = appId, Name = name, Publisher = "CRONUS", Version = version,
                Scope = "Tenant", IsInstalled = true, IsPublished = true,
            });
        }
        instance.Tenants.Add(tenant);
        ctx.GatewayInstances.Add(instance);

        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, Project = project, Name = "CRONUS Test", Type = environmentType,
            Kind = OeEnvironmentKind.OnPremises, GatewayInstance = instance, TenantId = "default",
            Status = BcEnvironmentStatus.Active, FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();

        var releasePipeline = new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Build → CRONUS Test",
            BuildPipelineId = pipeline.Id, ProjectEnvironmentId = env.Id,
            DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleasePipelines.Add(releasePipeline);

        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Pipeline = pipeline,
            Status = ProjectBuildStatus.Ready, StartedAt = now, FinishedAt = now,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();

        var packages = new List<byte[]>();
        string[] ids = [CoreId, SalesId, ReportsId];
        string[] names = ["CRONUS Core", "CRONUS Sales", "CRONUS Reports"];
        for (var i = 0; i < ids.Length; i++)
        {
            var version = $"1.0.{i}.0";
            var bytes = SyntheticApp.Build(ids[i], names[i], "CRONUS", version);
            packages.Add(bytes);
            // Saved one at a time: the id order is the build's dependency order.
            ctx.OeProjectBuildArtifacts.Add(new OeProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id, FileName = $"CRONUS_{names[i].Replace(" ", "")}_{version}.app",
                AppId = ids[i], AppName = names[i], AppVersion = version, SizeBytes = bytes.Length, Content = bytes, CreatedAt = now,
            });
            await ctx.SaveChangesAsync();
        }

        return new Seed(project.Id, gateway.Id, instance.Id, env.Id, releasePipeline.Id, build.Id, packages);
    }

    private static GatewayInstance Instance(int gatewayId, string machine, string name, int? sessions, string database = "CRONUS Test") => new()
    {
        OrganizationId = TestDb.DefaultOrgId, GatewayId = gatewayId, Machine = machine, Name = name, State = "Running",
        DatabaseServer = "CRONUS-SQL01", DatabaseName = database, Multitenant = false, SessionCount = sessions,
        FirstReportedAt = DateTime.UtcNow, ReportedAt = DateTime.UtcNow, ReadAt = DateTime.UtcNow,
    };

    private async Task AllowAgentsInProductionAsync()
    {
        await using var ctx = _db.NewContext();
        var settings = await ctx.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
        if (settings is null)
        {
            settings = new OrganizationSettings { OrganizationId = TestDb.DefaultOrgId };
            ctx.OrganizationSettings.Add(settings);
        }
        settings.AgentsMayDeployToProduction = true;
        await ctx.SaveChangesAsync();
    }

    private async Task<int> DeployNowAsync(Seed seed)
    {
        await using var ctx = _db.NewContext();
        var id = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        while (_queue.Reader.TryRead(out var job)) _queue.Complete(job.DeliveryId);
        return id;
    }

    private async Task<OeProjectDelivery> LoadAsync(int id)
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeProjectDeliveries.AsNoTracking().Include(d => d.Results).SingleAsync(d => d.Id == id);
    }

    /// <summary>Waits for the run to queue its command, and answers its id.</summary>
    private async Task<long> WaitForCommandAsync(int gatewayId)
    {
        var giveUp = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < giveUp)
        {
            await using var ctx = _db.NewContext();
            if (await ctx.GatewayCommands.AsNoTracking().Where(c => c.GatewayId == gatewayId).Select(c => (long?)c.Id).FirstOrDefaultAsync() is { } id)
            {
                return id;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("The run never queued its command.");
    }

    /// <summary>
    /// Runs the deployment while a fake gateway collects its command, posts the lines
    /// <paramref name="answer"/> gives and reports the result it gives.
    /// </summary>
    private async Task RunWithGatewayAsync(
        int deliveryId, int gatewayId,
        Func<FakeGateway, GatewayCommandMessage, Task<(string[] Lines, GatewayCommandResultRequest Result)>> answer)
    {
        var run = Task.Run(async () =>
        {
            await using var ctx = _db.NewContext();
            await NewService(ctx).RunDeliveryAsync(deliveryId);
        });
        var gateway = new FakeGateway(this, gatewayId);
        var giveUp = DateTime.UtcNow.AddSeconds(30);
        GatewayCommandMessage? message = null;
        while (message is null && DateTime.UtcNow < giveUp && !run.IsCompleted)
        {
            message = await gateway.ClaimAsync();
            if (message is null) await Task.Delay(20);
        }
        if (message is not null)
        {
            var (lines, result) = await answer(gateway, message);
            if (lines.Length > 0) await gateway.ProgressAsync(message.Id, lines);
            await gateway.CompleteAsync(message.Id, result);
        }
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        message.Should().NotBeNull("the run queues a command for the gateway");
    }

    /// <summary>The gateway's side of the command queue, as the endpoints call it.</summary>
    private sealed class FakeGateway(OnPremisesDeliveryTests owner, int gatewayId)
    {
        private GatewayCommandService Service(AppDbContext ctx) => owner.Commands(ctx);

        public async Task<GatewayCommandMessage?> ClaimAsync()
        {
            await using var ctx = owner._db.NewContext();
            var claim = await Service(ctx).ClaimNextAsync(gatewayId);
            return claim.Outcome == GatewayClaimOutcome.Claimed ? claim.Command : null;
        }

        public async Task<byte[]> DownloadAsync(GatewayCommandMessage message, int index)
        {
            await using var ctx = owner._db.NewContext();
            var (outcome, file) = await Service(ctx).OpenFileAsync(gatewayId, message.Id, message.Files[index].Id);
            outcome.Should().Be(GatewayFileOutcome.Found);
            using var stream = new MemoryStream();
            (await Service(ctx).CopyFileContentAsync(file!.Id, stream)).Should().BeTrue();
            var bytes = stream.ToArray();
            file.Size.Should().Be(bytes.Length);
            file.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(bytes)), "the hash the database worked out is the content's");
            return bytes;
        }

        public async Task ProgressAsync(long commandId, IReadOnlyList<string> lines)
        {
            await using var ctx = owner._db.NewContext();
            (await Service(ctx).AppendProgressAsync(gatewayId, commandId, lines)).Should().BeTrue();
        }

        public async Task CompleteAsync(long commandId, GatewayCommandResultRequest result)
        {
            await using var ctx = owner._db.NewContext();
            (await Service(ctx).CompleteAsync(gatewayId, commandId, result)).Should().BeTrue();
        }
    }

    private GatewayCommandService Commands(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _signal, TimeProvider.System, NullLogger<GatewayCommandService>.Instance);

    private DeliveryService NewService(AppDbContext ctx)
    {
        var gateway = new GatewayTargetServices(ctx, Commands(ctx), TimeProvider.System, NullLogger<GatewayDeliveryTarget>.Instance);
        return new DeliveryService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
            new DeliveryTargetFactory(new NoTokens(), Unreachable.Of<IBcAppManagementClient>(), Unreachable.Of<IBcAdminClient>(), gateway), _queue,
            new BcPanelCache(TimeProvider.System),
            _db.NewToolEnablement(ctx),
            TimeProvider.System,
            NullLogger<DeliveryService>.Instance)
        {
            PollDelay = TimeSpan.Zero,
            PollTimeoutPerApp = TimeSpan.FromSeconds(5),
        };
    }

    // Business Central online is never reached from these tests.
    private sealed class NoTokens : IDeliveryTokenSource
    {
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, bool forceRefresh, CancellationToken ct = default) => throw new NotSupportedException();
    }

    public class Unreachable : System.Reflection.DispatchProxy
    {
        public static T Of<T>() where T : class => Create<T, Unreachable>();

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"{targetMethod?.Name} reaches Business Central online.");
    }
}
