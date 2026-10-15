using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using ALDevToolbox.Tests.ObjectExplorer;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// A build's objects against the licence an on-premises service tier reports (#1370): the
/// warning a build records for its Solution's linked Production tiers, the refusal a
/// deployment gets before anything is sent, "not checked" when the licence cannot be
/// trusted, nothing at all online, and the same answers through the MCP tools. See
/// <c>.design/on-prem-gateway.md</c>, "Licensed object ranges, as built".
/// </summary>
public sealed class OnPremisesLicenceRangeTests : IDisposable
{
    private const string CoreId = "c0ffee00-0000-0000-0000-000000000001";

    private readonly TestDb _db = new();
    private readonly DeliveryQueue _queue = new();

    public OnPremisesLicenceRangeTests()
    {
        _db.OrgContext.IsSiteAdmin = true; // manage rights via the project owner
    }

    public void Dispose() => _db.Dispose();

    private static readonly (string Kind, int Id, string Name)[] InsideObjects =
    [
        ("table", 50000, "CRONUS Rating"),
        ("page", 50001, "CRONUS Rating Card"),
        ("codeunit", 50002, "CRONUS Rating Mgt."),
        ("tableextension", 70000, "CRONUS Customer Ext"),
        ("enum", 70100, "CRONUS Rating Level"),
        ("interface", 0, "CRONUS IRating"),
    ];

    // ── The build ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_build_with_an_object_outside_the_licence_records_a_warning_and_stays_ready()
    {
        var seed = await SeedAsync(objects: [.. InsideObjects, ("table", 50120, "CRONUS Rating Entry")]);

        await RecordAsync(seed);

        var check = (await StoredAsync(seed)).Should().ContainSingle().Subject;
        check.Outcome.Should().Be(LicenceCheckOutcome.OutsideLicence);
        check.EnvironmentName.Should().Be("Production");
        check.Findings.Select(f => f.Message).Should().Equal("Table 50120 \"CRONUS Rating Entry\" is outside the licence (50000-50099)");
        check.CheckedObjectCount.Should().Be(4, "the table, page, codeunit and the new table; not the extension, enum or interface");
        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.SingleAsync(b => b.Id == seed.BuildId)).Status.Should().Be(ProjectBuildStatus.Ready,
            "a licence warning never fails a build");
    }

    [Fact]
    public async Task A_build_inside_the_licence_records_a_pass()
    {
        var seed = await SeedAsync();

        await RecordAsync(seed);

        var check = (await StoredAsync(seed)).Single();
        check.IsPassed.Should().BeTrue();
        check.Findings.Should().BeEmpty();
        check.Summary.Should().Be("Licence checked against Production: every object in this build is inside the licence.");
    }

    [Fact]
    public async Task On_bc14_an_enum_outside_the_table_ranges_is_a_warning()
    {
        var seed = await SeedAsync(tierVersion: "14.0.29530.0", objects: [.. InsideObjects, ("enum", 50010, "CRONUS Inside"), ("enum", 50130, "CRONUS Rating Source")]);

        await RecordAsync(seed);

        var check = (await StoredAsync(seed)).Single();
        check.Findings.Select(f => f.Message).Should().BeEquivalentTo(
            "Enum 50130 \"CRONUS Rating Source\" needs table 50130 in the licence (BC 14)",
            "Enum 70100 \"CRONUS Rating Level\" needs table 70100 in the licence (BC 14)");
    }

    [Fact]
    public async Task A_licence_the_gateway_has_not_read_yet_is_not_checked_rather_than_passed()
    {
        var seed = await SeedAsync(licence: false, objects: [.. InsideObjects, ("table", 50120, "CRONUS Rating Entry")]);

        await RecordAsync(seed);

        var check = (await StoredAsync(seed)).Single();
        check.IsNotChecked.Should().BeTrue();
        check.Reason.Should().Be("the gateway has not read the licence on CRONUS-BC01 / BC250 yet.");
        check.Summary.Should().StartWith("Licence not checked for Production: ");
    }

    [Fact]
    public async Task A_licence_behind_an_offline_gateway_or_read_long_ago_is_not_checked()
    {
        var offline = await SeedAsync(gatewayLastSeen: DateTime.UtcNow.AddHours(-3));
        await RecordAsync(offline);
        (await StoredAsync(offline)).Single().Reason.Should().Be(
            "the gateway for CRONUS-BC01 / BC250 is offline, so the licence it last read may be out of date.");

        var stale = await SeedAsync(licenceReadAt: DateTime.UtcNow.AddHours(-2));
        await RecordAsync(stale);
        (await StoredAsync(stale)).Single().Reason.Should().Be(
            "the licence on CRONUS-BC01 / BC250 has not been read in the last hour, so it may be out of date.");
    }

    [Fact]
    public async Task An_online_solution_is_never_checked()
    {
        var seed = await SeedOnlineAsync();

        await RecordAsync(seed);

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.SingleAsync(b => b.Id == seed.BuildId)).LicenceCheckJson.Should().BeNull();
    }

    [Fact]
    public async Task Only_linked_production_tiers_are_checked_on_the_build_and_preview_builds_never()
    {
        var sandbox = await SeedAsync(environmentType: "Sandbox", objects: [("table", 60000, "Outside")]);
        await RecordAsync(sandbox);
        (await StoredAsync(sandbox)).Should().BeEmpty();

        var preview = await SeedAsync(objects: [("table", 60000, "Outside")]);
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjectBuilds.Where(b => b.Id == preview.BuildId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.BcTarget, ProjectBuildTarget.NextMajor));
        }
        await RecordAsync(preview);
        (await StoredAsync(preview)).Should().BeEmpty();
    }

    // ── Deploying ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_deployment_of_a_build_outside_the_licence_is_refused_before_anything_is_made_or_sent()
    {
        var seed = await SeedAsync(environmentType: "Sandbox",
            objects: [.. InsideObjects, ("table", 50120, "CRONUS Rating Entry"), ("report", 50060, "CRONUS Rating List")]);

        await using var ctx = _db.NewContext();
        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        var refusal = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Build"];
        refusal.Should().Be(
            $"Build #{seed.BuildId} can't be deployed to Production: 2 objects are outside the customer's licence there, so nothing was sent. "
            + "Table 50120 \"CRONUS Rating Entry\" is outside the licence (50000-50099); "
            + "Report 50060 \"CRONUS Rating List\" is outside the licence (50000-50049). "
            + "Ask the customer to add these objects to their licence, or give them ids the licence covers, then deploy again.");
        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.CountAsync()).Should().Be(0);
        (await read.GatewayCommands.CountAsync()).Should().Be(0, "nothing reaches the gateway");
    }

    [Fact]
    public async Task A_licence_that_shrinks_after_the_deployment_was_made_refuses_the_run_and_sends_nothing()
    {
        var seed = await SeedAsync(environmentType: "Sandbox", objects: [.. InsideObjects, ("table", 50090, "CRONUS Rating Entry")]);
        int id;
        await using (var ctx = _db.NewContext())
        {
            id = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
            while (_queue.Reader.TryRead(out var job)) _queue.Complete(job.DeliveryId);
        }
        // The customer's new licence covers fewer tables.
        await using (var ctx = _db.NewContext())
        {
            await ctx.GatewayInstanceLicenceRanges.Where(r => r.ObjectType == "Table" && r.Licence!.InstanceId == seed.InstanceId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ToId, 50049));
        }

        await using (var ctx = _db.NewContext())
        {
            (await NewService(ctx).RunDeliveryAsync(id)).Should().BeTrue();
        }

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.Include(d => d.Results).SingleAsync(d => d.Id == id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().StartWith($"Build #{seed.BuildId} can't be deployed to Production: 1 object is outside")
            .And.Contain("Table 50090 \"CRONUS Rating Entry\" is outside the licence (50000-50049)");
        delivery.Results.Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Skipped, "no app was sent");
        (await read.GatewayCommands.CountAsync()).Should().Be(0, "nothing reaches the gateway");
    }

    [Fact]
    public async Task A_deployment_whose_licence_cannot_be_read_goes_ahead_and_says_it_was_not_checked()
    {
        var seed = await SeedAsync(environmentType: "Sandbox", licence: false);
        int id;
        await using (var ctx = _db.NewContext())
        {
            id = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
            while (_queue.Reader.TryRead(out var job)) _queue.Complete(job.DeliveryId);
        }
        await using (var ctx = _db.NewContext())
        {
            (await NewService(ctx).RunDeliveryAsync(id)).Should().BeTrue();
        }

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.SingleAsync(d => d.Id == id);
        delivery.DiagnosticsLog.Should().Contain("Licence not checked for Production: the gateway has not read the licence on CRONUS-BC01 / BC250 yet.");
        (await read.GatewayCommands.CountAsync()).Should().Be(1, "a licence nobody could read is no reason to stop");
    }

    [Fact]
    public async Task A_deployment_inside_the_licence_says_so_in_its_log()
    {
        var seed = await SeedAsync(environmentType: "Sandbox");
        int id;
        await using (var ctx = _db.NewContext())
        {
            id = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
            while (_queue.Reader.TryRead(out var job)) _queue.Complete(job.DeliveryId);
        }
        await using (var ctx = _db.NewContext())
        {
            (await NewService(ctx).RunDeliveryAsync(id)).Should().BeTrue();
        }

        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == id)).DiagnosticsLog
            .Should().Contain("Licence checked against Production: every object in this build is inside the licence.");
        (await read.GatewayCommands.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_deploy_dialog_reads_the_check_for_the_picked_build_and_nothing_online()
    {
        var seed = await SeedAsync(objects: [.. InsideObjects, ("table", 50120, "CRONUS Rating Entry")]);
        await using (var ctx = _db.NewContext())
        {
            var check = await NewService(ctx).CheckLicenceAsync(seed.ReleasePipelineId, seed.BuildId);
            check!.IsOutside.Should().BeTrue();
            check.Findings.Should().ContainSingle();
        }

        var online = await SeedOnlineAsync();
        await using (var ctx = _db.NewContext())
        {
            (await NewService(ctx).CheckLicenceAsync(online.ReleasePipelineId, online.BuildId)).Should().BeNull();
            (await NewService(ctx).CheckLicenceAsync(seed.ReleasePipelineId, online.BuildId)).Should().BeNull(
                "a build of another solution is not this pipeline's to check");
        }
    }

    // ── MCP ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_build_tools_carry_the_licence_warning()
    {
        var seed = await SeedAsync(objects: [.. InsideObjects, ("table", 50120, "CRONUS Rating Entry")]);
        await RecordAsync(seed);

        await using var ctx = _db.NewContext();
        var tools = NewArtifactsTools(ctx);

        var listed = (await tools.ListPipelineBuildsAsync(seed.PipelineId)).Single();
        listed.LicenceChecks!.Single().Outcome.Should().Be(LicenceCheckOutcome.OutsideLicence);
        (await tools.ListProjectBuildsAsync(seed.ProjectId.ToString())).Single().LicenceChecks.Should().ContainSingle();

        var detail = await tools.GetProjectBuildAsync(seed.BuildId);
        detail.LicenceChecks!.Single().Findings.Single().Message
            .Should().Be("Table 50120 \"CRONUS Rating Entry\" is outside the licence (50000-50099)");
    }

    [Fact]
    public async Task The_build_tools_say_not_checked_and_leave_it_out_online()
    {
        var notRead = await SeedAsync(licence: false);
        await RecordAsync(notRead);
        var online = await SeedOnlineAsync();
        await RecordAsync(online);

        await using var ctx = _db.NewContext();
        var tools = NewArtifactsTools(ctx);
        (await tools.GetProjectBuildAsync(notRead.BuildId)).LicenceChecks!.Single().Outcome.Should().Be(LicenceCheckOutcome.NotChecked);
        (await tools.GetProjectBuildAsync(online.BuildId)).LicenceChecks.Should().BeNull();
    }

    [Fact]
    public async Task Deploy_build_returns_the_refusal_and_sends_nothing()
    {
        var seed = await SeedAsync(environmentType: "Sandbox", objects: [.. InsideObjects, ("table", 50120, "CRONUS Rating Entry")]);

        await using var ctx = _db.NewContext();
        var act = () => NewDeliveryTools(ctx).PublishBuildAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should()
            .StartWith($"Couldn't deploy that build: Build #{seed.BuildId} can't be deployed to Production: 1 object is outside the customer's licence there")
            .And.Contain("Table 50120 \"CRONUS Rating Entry\" is outside the licence (50000-50099)");
        await using var read = _db.NewContext();
        (await read.GatewayCommands.CountAsync()).Should().Be(0);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private sealed record Seed(int ProjectId, int PipelineId, int InstanceId, int ReleasePipelineId, int BuildId, int ReleaseId);

    /// <summary>
    /// An on-premises Solution with one tier linked as "Production" (its type set by
    /// <paramref name="environmentType"/>, so a test tier can be deployed to), a licence
    /// covering 50000-50099 for tables and codeunits, 50000-50149 for pages and 50000-50049
    /// for reports, and one ready build whose indexed objects are <paramref name="objects"/>.
    /// </summary>
    private async Task<Seed> SeedAsync(
        (string Kind, int Id, string Name)[]? objects = null,
        string environmentType = "Production",
        string tierVersion = "25.0.12345.0",
        bool licence = true,
        DateTime? licenceReadAt = null,
        DateTime? gatewayLastSeen = null)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;

        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS " + Guid.NewGuid().ToString("N"),
            HostingType = ProjectHostingType.CustomerHardware, CreatedAt = now, UpdatedAt = now,
        };
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
                    Allow = ["inventory", "publish"],
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

        var instance = new GatewayInstance
        {
            OrganizationId = TestDb.DefaultOrgId, GatewayId = gateway.Id, Machine = "CRONUS-BC01", Name = "BC250", State = "Running",
            Version = tierVersion, DatabaseServer = "CRONUS-SQL01", DatabaseName = "CRONUS", Multitenant = false, SessionCount = 0,
            FirstReportedAt = now, ReportedAt = now, ReadAt = now,
        };
        instance.Tenants.Add(new GatewayInstanceTenant
        {
            OrganizationId = TestDb.DefaultOrgId, TenantId = "default", SessionCount = 0, ReadAt = now, AppsReadAt = now,
        });
        if (licence)
        {
            instance.Licence = new GatewayInstanceLicence
            {
                OrganizationId = TestDb.DefaultOrgId, Licensee = "CRONUS A/S", Text = "licence", TextSha256 = new string('0', 64),
                ReadAt = licenceReadAt ?? now,
                Ranges =
                [
                    Range("Table", 50000, 50099, "Direct"),
                    Range("TableData", 50000, 50199, "Direct Direct Direct Direct"),
                    Range("Page", 50000, 50149, "Direct"),
                    Range("Report", 50000, 50049, "Direct"),
                    Range("Codeunit", 50000, 50099, "Direct"),
                ],
            };
        }
        ctx.GatewayInstances.Add(instance);

        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, Project = project, Name = "Production", Type = environmentType,
            Kind = OeEnvironmentKind.OnPremises, GatewayInstance = instance, TenantId = "default",
            Status = BcEnvironmentStatus.Active, FetchedAt = now, Version = tierVersion,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();

        return await AddBuildAsync(ctx, project.Id, pipeline.Id, env.Id, instance.Id, objects ?? InsideObjects);
    }

    /// <summary>A Business Central online Solution with an online Production environment and the same build.</summary>
    private async Task<Seed> SeedOnlineAsync()
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Online " + Guid.NewGuid().ToString("N"),
            HostingType = ProjectHostingType.MicrosoftCloud, CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeProjects.Add(project);
        var pipeline = new OePipeline { OrganizationId = TestDb.DefaultOrgId, Project = project, Name = "Build", CreatedAt = now, UpdatedAt = now };
        ctx.OePipelines.Add(pipeline);
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, Project = project, Name = "Production", Type = "Production",
            Kind = OeEnvironmentKind.Online, Status = BcEnvironmentStatus.Active, FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        return await AddBuildAsync(ctx, project.Id, pipeline.Id, env.Id, 0, [("table", 60000, "Anything")]);
    }

    private static GatewayInstanceLicenceRange Range(string type, int from, int to, string? permissions) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, ObjectType = type, FromId = from, ToId = to, Permissions = permissions,
    };

    /// <summary>A ready build with one app, its objects indexed as the build's release, and a deployment pipeline to the environment.</summary>
    private static async Task<Seed> AddBuildAsync(AppDbContext ctx, int projectId, int pipelineId, int environmentId, int instanceId,
        (string Kind, int Id, string Name)[] objects)
    {
        var now = DateTime.UtcNow;
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId, Label = "build", Kind = OeRelease.ProjectBuildKind, Status = "ready",
            ImportedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        var module = new OeModule
        {
            OrganizationId = TestDb.DefaultOrgId, ReleaseId = release.Id, AppId = Guid.Parse(CoreId),
            Name = "CRONUS Core", Publisher = "CRONUS", Version = "1.0.0.0", CreatedAt = now,
        };
        ctx.OeModules.Add(module);
        await ctx.SaveChangesAsync();
        foreach (var (kind, id, name) in objects)
        {
            ctx.OeModuleObjects.Add(new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId, ModuleId = module.Id, Kind = kind,
                ObjectId = kind == "interface" ? null : id, Name = name, LineNumber = 1,
            });
        }

        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipelineId, ReleaseId = release.Id,
            Status = ProjectBuildStatus.Ready, StartedAt = now, FinishedAt = now, BcVersion = "25.0",
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        var bytes = SyntheticApp.Build(CoreId, "CRONUS Core", "CRONUS", "1.0.0.0");
        ctx.OeProjectBuildArtifacts.Add(new OeProjectBuildArtifact
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id, FileName = "CRONUS_CRONUSCore_1.0.0.0.app",
            AppId = CoreId, AppName = "CRONUS Core", AppVersion = "1.0.0.0", SizeBytes = bytes.Length, Content = bytes, CreatedAt = now,
        });
        var releasePipeline = new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Build → Production",
            BuildPipelineId = pipelineId, ProjectEnvironmentId = environmentId,
            DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleasePipelines.Add(releasePipeline);
        await ctx.SaveChangesAsync();
        return new Seed(projectId, pipelineId, instanceId, releasePipeline.Id, build.Id, release.Id);
    }

    /// <summary>What the worker does once a build is ready.</summary>
    private async Task RecordAsync(Seed seed)
    {
        await using var ctx = _db.NewContext();
        var service = new ProjectBuildService(
            ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
            null!, null!, null!, null!, null!, null!, null!, TimeProvider.System, NullLogger<ProjectBuildService>.Instance);
        await service.RecordLicenceCheckAsync(seed.ReleaseId);
    }

    private async Task<IReadOnlyList<LicenceCheck>> StoredAsync(Seed seed)
    {
        await using var ctx = _db.NewContext();
        var json = await ctx.OeProjectBuilds.AsNoTracking().Where(b => b.Id == seed.BuildId).Select(b => b.LicenceCheckJson).SingleAsync();
        return LicenceRangeCheck.Parse(json);
    }

    private DeliveryService NewService(AppDbContext ctx)
    {
        var commands = new GatewayCommandService(ctx, _db.OrgContext, new GatewayCommandSignal(), TimeProvider.System,
            NullLogger<GatewayCommandService>.Instance);
        var gateway = new GatewayTargetServices(ctx, commands, TimeProvider.System, NullLogger<GatewayDeliveryTarget>.Instance);
        return new DeliveryService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
            new DeliveryTargetFactory(new NoTokens(), OnPremisesDeliveryTests.Unreachable.Of<IBcAppManagementClient>(),
                OnPremisesDeliveryTests.Unreachable.Of<IBcAdminClient>(), gateway), _queue,
            new BcPanelCache(TimeProvider.System),
            _db.NewToolEnablement(ctx),
            TimeProvider.System,
            NullLogger<DeliveryService>.Instance);
    }

    private DeliveryTools NewDeliveryTools(AppDbContext ctx)
    {
        var github = _db.NewOfflineGitHubAccessService(ctx);
        return new DeliveryTools(NewService(ctx),
            new ReleasePipelineService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
                _db.NewToolEnablement(ctx), NullLogger<ReleasePipelineService>.Instance),
            _db.NewGitHubReleaseService(ctx, _db.NewGitHubAppClient(ctx, new UnreachableGitHub()), github),
            new ArtifactService(ctx, new ProjectAccess(ctx, _db.OrgContext)));
    }

    private ArtifactsTools NewArtifactsTools(AppDbContext ctx)
    {
        var access = new ProjectAccess(ctx, _db.OrgContext);
        var discovery = new ProjectDiscoveryService(ctx, _db.OrgContext, access, new ProjectDiscoveryQueue(),
            NullLogger<ProjectDiscoveryService>.Instance);
        return new ArtifactsTools(
            new ArtifactService(ctx, access),
            new ReleaseComparisonService(ctx, access, NullLogger<ReleaseComparisonService>.Instance),
            new ProjectService(ctx, _db.OrgContext, access, discovery, _db.NewOfflineGitHubAccessService(ctx), _db.NewRepositoryReadProbe(ctx), NullLogger<ProjectService>.Instance));
    }

    private sealed class NoTokens : IDeliveryTokenSource
    {
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, bool forceRefresh, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnreachableGitHub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("GitHub is not reachable from this test.");
    }
}
