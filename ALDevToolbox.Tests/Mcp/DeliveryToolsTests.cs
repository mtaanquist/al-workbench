using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// The MCP delivery surface — the agent-facing parallel of the Releases web tool.
/// Pins release-pipeline listing, the publish_build → delivery-id contract (delegating
/// to DeliveryService), the unknown-pipeline guard, and the access-denied → McpException
/// translation. The publish here only enqueues (no worker runs), so the BC seams are
/// never exercised. See .design/saas-delivery.md ("MCP parity").
/// </summary>
public sealed class DeliveryToolsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DeliveryQueue _queue = new();

    public DeliveryToolsTests()
    {
        _db.OrgContext.IsSiteAdmin = true; // manage rights via the project owner
    }

    public void Dispose() => _db.Dispose();

    private DeliveryTools NewTools(AppDbContext ctx, HttpMessageHandler? api = null) =>
        new(new DeliveryService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
                new ThrowingTokenSource(), new ThrowingAppManagementClient(), new ThrowingAdminClient(), _queue,
                new ALDevToolbox.Services.ObjectExplorer.Bc.BcPanelCache(TimeProvider.System),
                _db.NewToolEnablement(ctx),
                TimeProvider.System,
                NullLogger<DeliveryService>.Instance),
            new ReleasePipelineService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
                _db.NewToolEnablement(ctx), NullLogger<ReleasePipelineService>.Instance),
            _db.NewGitHubReleaseService(ctx, _db.NewGitHubAppClient(ctx, api ?? new UnreachableGitHub()),
                _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, api ?? new UnreachableGitHub()))),
            new ArtifactService(ctx, new ProjectAccess(ctx, _db.OrgContext)));

    /// <summary>Stands in for a GitHub nothing in these tests is meant to reach.</summary>
    private sealed class UnreachableGitHub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("GitHub is not reachable from this test.");
    }

    [Fact]
    public async Task List_release_pipelines_returns_the_orgs_pipelines()
    {
        await using (var ctx = _db.NewContext())
        {
            await SeedAsync(ctx, new[] { "CRONUS Core" });
        }

        await using var read = _db.NewContext();
        var rows = await NewTools(read).ListReleasePipelinesAsync();

        rows.Should().ContainSingle(r => r.Name == "CRONUS App → Production" && r.EnvironmentName == "Production");
    }

    [Fact]
    public async Task Publish_build_queues_a_delivery_and_returns_its_id()
    {
        Seed seed;
        await using (var ctx = _db.NewContext())
        {
            seed = await SeedAsync(ctx, new[] { "CRONUS Core", "CRONUS Sales" });
        }

        await using var read = _db.NewContext();
        var result = await NewTools(read).PublishBuildAsync(seed.ReleasePipelineId, seed.BuildId);

        result.DeploymentId.Should().BeGreaterThan(0);

        await using var verify = _db.NewContext();
        var delivery = await verify.OeProjectDeliveries.AsNoTracking()
            .SingleAsync(d => d.Id == result.DeploymentId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Scheduled);
        delivery.ReleasePipelineId.Should().Be(seed.ReleasePipelineId);
    }

    [Fact]
    public async Task Publish_build_releases_now_even_when_the_pipeline_installs_in_the_delivery_window()
    {
        Seed seed;
        await using (var ctx = _db.NewContext())
        {
            seed = await SeedAsync(ctx, new[] { "CRONUS Core" });
            var envId = await ctx.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId)
                .Select(r => r.ProjectEnvironmentId).SingleAsync();
            // A window that opens an hour from now: the web dialog would default to it.
            var opens = TimeOnly.FromDateTime(DateTime.UtcNow.AddHours(1));
            await ctx.OeProjectEnvironments.Where(e => e.Id == envId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(e => e.UpdateWindowStart, opens)
                    .SetProperty(e => e.UpdateWindowEnd, opens.AddHours(2)));
            await ctx.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.DeploymentSchedule, BcDeploymentSchedule.OurDeliveryWindow));
        }

        await using var read = _db.NewContext();
        var result = await NewTools(read).PublishBuildAsync(seed.ReleasePipelineId, seed.BuildId);

        await using var verify = _db.NewContext();
        var delivery = await verify.OeProjectDeliveries.AsNoTracking()
            .SingleAsync(d => d.Id == result.DeploymentId);
        delivery.ScheduledFor.Should().BeBefore(DateTime.UtcNow.AddMinutes(1), "the tool releases now, not at the window");
        delivery.DeploymentSchedule.Should().Be(BcDeploymentSchedule.Immediate);
        delivery.ScheduledByDeliveryWindow.Should().BeTrue();
        delivery.ScheduledOutsideWindow.Should().BeTrue();
    }

    [Fact]
    public async Task Publish_build_surfaces_validation_as_mcp_exception()
    {
        Seed seed;
        await using (var ctx = _db.NewContext())
        {
            // A failed build can't be released — DeliveryService throws PlanValidationException.
            seed = await SeedAsync(ctx, new[] { "CRONUS Core" }, buildStatus: ProjectBuildStatus.Failed);
        }

        await using var read = _db.NewContext();
        var act = () => NewTools(read).PublishBuildAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("Couldn't deploy");
    }

    [Fact]
    public async Task Publish_build_surfaces_access_denied_as_mcp_exception()
    {
        Seed seed;
        await using (var ctx = _db.NewContext())
        {
            seed = await SeedAsync(ctx, new[] { "CRONUS Core" });
        }

        // A non-owner, non-admin user can't release this ownerless project's builds -
        // once the project is narrowed. A Public solution is managed by everyone in the
        // organisation, so there is nobody it would refuse.
        await using (var narrow = _db.NewContext())
        {
            var project = await narrow.OeProjects.SingleAsync(p => p.Id == seed.ProjectId);
            project.Visibility = ProjectVisibility.ReadOnly;
            await narrow.SaveChangesAsync();
        }

        _db.OrgContext.IsSiteAdmin = false;
        _db.OrgContext.CurrentUserId = 999;

        await using var read = _db.NewContext();
        var act = () => NewTools(read).PublishBuildAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("permission");
    }

    [Fact]
    public async Task List_deliveries_throws_for_an_unknown_pipeline()
    {
        await using var read = _db.NewContext();
        var act = () => NewTools(read).ListDeliveriesAsync(999999);

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task List_deliveries_returns_a_published_deliverys_history()
    {
        Seed seed;
        int deliveryId;
        await using (var ctx = _db.NewContext())
        {
            seed = await SeedAsync(ctx, new[] { "CRONUS Core" });
            deliveryId = (await NewTools(ctx).PublishBuildAsync(seed.ReleasePipelineId, seed.BuildId)).DeploymentId;
        }

        await using var read = _db.NewContext();
        var rows = await NewTools(read).ListDeliveriesAsync(seed.ReleasePipelineId);

        rows.Should().ContainSingle(d => d.Id == deliveryId && d.Status == ProjectDeliveryStatus.Scheduled);
        rows[0].Apps.Should().ContainSingle(a => a.AppName == "CRONUS Core");
    }

    // ── GitHub releases as an artifact source (#632) ──────────────────────

    [Fact]
    public async Task List_github_releases_is_refused_for_a_pipeline_that_releases_builds()
    {
        Seed seed;
        await using (var ctx = _db.NewContext())
        {
            seed = await SeedAsync(ctx, new[] { "CRONUS Core" });
        }

        await using var read = _db.NewContext();
        var act = () => NewTools(read).ListGitHubReleasesAsync(seed.ReleasePipelineId);

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("build pipeline");
    }

    [Fact]
    public async Task Stage_github_release_hands_back_a_build_publish_build_can_take()
    {
        Seed seed;
        await using (var ctx = _db.NewContext())
        {
            seed = await SeedAsync(ctx, new[] { "CRONUS Core" });
            await MakeReleaseSourcedAsync(ctx, seed.ReleasePipelineId);
        }
        await ConnectOrganisationAsync();

        BuildRow staged;
        await using (var read = _db.NewContext())
        {
            staged = await NewTools(read, GitHubWithOneRelease()).StageGitHubReleaseAsync(seed.ReleasePipelineId, "v1.0.0.0");
        }

        staged.Status.Should().Be(ProjectBuildStatus.Ready);
        staged.GitHubReleaseTag.Should().Be("v1.0.0.0");
        staged.ArtifactCount.Should().Be(1);

        // The whole point of staging: the ordinary publish takes it from here.
        await using var publish = _db.NewContext();
        var result = await NewTools(publish, GitHubWithOneRelease()).PublishBuildAsync(seed.ReleasePipelineId, staged.Id);
        result.DeploymentId.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Stage_github_release_says_so_when_the_tag_is_gone()
    {
        Seed seed;
        await using (var ctx = _db.NewContext())
        {
            seed = await SeedAsync(ctx, new[] { "CRONUS Core" });
            await MakeReleaseSourcedAsync(ctx, seed.ReleasePipelineId);
        }
        await ConnectOrganisationAsync();

        await using var read = _db.NewContext();
        var act = () => NewTools(read, GitHubWithOneRelease()).StageGitHubReleaseAsync(seed.ReleasePipelineId, "v9.9.9.9");

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("no release tagged");
    }

    [Fact]
    public async Task Both_release_tools_say_so_plainly_when_the_deployment_has_no_GitHub_app()
    {
        // Nothing is configured, so there is no App to act as. An agent gets the
        // sentence a person would see rather than an unhandled exception.
        Seed seed;
        await using (var ctx = _db.NewContext())
        {
            seed = await SeedAsync(ctx, new[] { "CRONUS Core" });
            await MakeReleaseSourcedAsync(ctx, seed.ReleasePipelineId);
            ctx.OrganizationSettings.Add(new ALDevToolbox.Domain.Entities.OrganizationSettings
            {
                OrganizationId = TestDb.DefaultOrgId,
                GitHubInstallationId = InstallationId,
                GitHubOrgLogin = OrgLogin,
                GitHubConnectedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var list = () => NewTools(read).ListGitHubReleasesAsync(seed.ReleasePipelineId);
        var stage = () => NewTools(read).StageGitHubReleaseAsync(seed.ReleasePipelineId, "v1.0.0.0");

        await list.Should().ThrowAsync<McpException>();
        await stage.Should().ThrowAsync<McpException>();
    }

    // ── Project visibility (slice 3) ─────────────────────────────────────

    /// <summary>
    /// A release pipeline inherits its project's visibility. Both the unfiltered
    /// list and the by-id resolve answer a non-member the same way an id in
    /// another org would — absent, then "not found" — never a distinct refusal.
    /// </summary>
    [Fact]
    public async Task A_private_projects_release_pipeline_is_invisible_to_a_non_member()
    {
        Seed seed;
        await using (var ctx = _db.NewContext())
        {
            seed = await SeedAsync(ctx, new[] { "CRONUS Core" });
        }

        const int ownerId = 9700;
        const int memberId = 9701;
        const int outsiderId = 9702;
        int teamId;
        await using (var ctx = _db.NewContext())
        {
            foreach (var (id, email) in new[] { (ownerId, "owner@example.com"), (memberId, "mel@example.com"), (outsiderId, "nils@example.com") })
            {
                ctx.Users.Add(new ALDevToolbox.Domain.Entities.User
                {
                    Id = id, OrganizationId = TestDb.DefaultOrgId, Email = email, PasswordHash = "x",
                    DisplayName = email, Role = ALDevToolbox.Domain.Entities.UserRole.User,
                    Status = ALDevToolbox.Domain.Entities.UserStatus.Active, CreatedAt = DateTime.UtcNow,
                });
            }
            var team = new ALDevToolbox.Domain.Entities.Team
            {
                OrganizationId = TestDb.DefaultOrgId, Name = "NDA team",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            ctx.Teams.Add(team);
            await ctx.SaveChangesAsync();
            teamId = team.Id;
            ctx.TeamMembers.Add(new ALDevToolbox.Domain.Entities.TeamMember
            {
                OrganizationId = TestDb.DefaultOrgId, TeamId = teamId, UserId = memberId, CreatedAt = DateTime.UtcNow,
            });
            var project = await ctx.OeProjects.FirstAsync(p => p.Id == seed.ProjectId);
            project.CreatedByUserId = ownerId;
            await ctx.SaveChangesAsync();
        }

        // Set it Private as a SiteAdmin (the fixture's default identity).
        await using (var ctx = _db.NewContext())
        {
            var access = new ProjectAccess(ctx, _db.OrgContext);
            var discovery = new ProjectDiscoveryService(ctx, _db.OrgContext, access, new ProjectDiscoveryQueue(),
                NullLogger<ProjectDiscoveryService>.Instance);
            await new ProjectService(ctx, _db.OrgContext, access, discovery, NullLogger<ProjectService>.Instance)
                .SetAccessAsync(seed.ProjectId, ProjectVisibility.Private, new[] { teamId });
        }

        _db.OrgContext.IsSiteAdmin = false;
        _db.OrgContext.CurrentUserId = outsiderId;
        await using (var read = _db.NewContext())
        {
            var tools = NewTools(read);
            (await tools.ListReleasePipelinesAsync()).Should().NotContain(r => r.Id == seed.ReleasePipelineId);
            (await ((Func<Task>)(() => tools.ListDeliveriesAsync(seed.ReleasePipelineId)))
                .Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("not found");
            await ((Func<Task>)(() => tools.ListReleasePipelinesAsync(seed.ProjectId)))
                .Should().ThrowAsync<McpException>();
        }

        _db.OrgContext.CurrentUserId = memberId;
        await using (var read = _db.NewContext())
        {
            var tools = NewTools(read);
            (await tools.ListReleasePipelinesAsync()).Should().ContainSingle(r => r.Id == seed.ReleasePipelineId);
            (await tools.ListDeliveriesAsync(seed.ReleasePipelineId)).Should().BeEmpty();
        }
    }

    // ── Seeding (a project → build pipeline → environment → release pipeline → build) ──

    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string GitHubRepo = OrgLogin + "/cronus-customer";

    /// <summary>A GitHub with one installable release on the solution's repository.</summary>
    private static FakeGitHubApi GitHubWithOneRelease() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                System.Net.HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson("ghs_installation"))
            .On(HttpMethod.Get, $"/repos/{GitHubRepo}/releases/tags/v1.0.0.0", System.Net.HttpStatusCode.OK,
                FakeGitHubApi.ReleaseJson(GitHubRepo, "v1.0.0.0", 900, assets: (5501, "CRONUS Core_1.0.0.0.app")))
            .On(HttpMethod.Get, $"/repos/{GitHubRepo}/releases/tags/", System.Net.HttpStatusCode.NotFound)
            .OnRedirect(HttpMethod.Get, $"/repos/{GitHubRepo}/releases/assets/5501",
                "https://objects.githubusercontent.com/app-bytes")
            .OnBytes(HttpMethod.Get, "/app-bytes", new byte[] { 1, 2, 3 });

    /// <summary>Points the seeded release pipeline at the solution's GitHub repository.</summary>
    private static async Task MakeReleaseSourcedAsync(AppDbContext ctx, int releasePipelineId)
    {
        var rp = await ctx.OeReleasePipelines.SingleAsync(r => r.Id == releasePipelineId);
        var repository = new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = rp.ProjectId,
            Provider = RepositoryProvider.GitHub, Url = $"https://github.com/{GitHubRepo}.git",
            DisplayName = "cronus-customer",
        };
        ctx.OeProjectRepositories.Add(repository);
        await ctx.SaveChangesAsync();

        rp.ArtifactSource = ReleaseArtifactSource.GithubRelease;
        rp.BuildPipelineId = null;
        rp.GithubReleaseRepositoryId = repository.Id;
        await ctx.SaveChangesAsync();
    }

    private async Task ConnectOrganisationAsync()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(
            new ALDevToolbox.Services.Operations.GitHubAppInput(
                AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
                ClientSecret: "s3cr3t", ClearClientSecret: false,
                PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));

        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new ALDevToolbox.Domain.Entities.OrganizationSettings
        {
            OrganizationId = TestDb.DefaultOrgId,
            GitHubInstallationId = InstallationId,
            GitHubOrgLogin = OrgLogin,
            GitHubConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private sealed record Seed(int ProjectId, int ReleasePipelineId, int BuildId);

    private static async Task<Seed> SeedAsync(AppDbContext ctx, string[] appNames, string buildStatus = ProjectBuildStatus.Ready)
    {
        var now = DateTime.UtcNow;
        var project = new OeProject { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS " + Guid.NewGuid().ToString("N"), CreatedAt = now, UpdatedAt = now };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        var pipeline = new OePipeline { OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Build", CreatedAt = now, UpdatedAt = now };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();

        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production",
            FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();

        var releasePipeline = new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "CRONUS App → Production",
            BuildPipelineId = pipeline.Id, ProjectEnvironmentId = env.Id,
            DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleasePipelines.Add(releasePipeline);
        await ctx.SaveChangesAsync();

        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, PipelineId = pipeline.Id,
            Status = buildStatus, StartedAt = now, FinishedAt = now,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();

        for (var i = 0; i < appNames.Length; i++)
        {
            ctx.OeProjectBuildArtifacts.Add(new OeProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id,
                FileName = appNames[i] + ".app", AppName = appNames[i], AppVersion = "1.0.0.0",
                Content = new byte[] { 1, 2, 3 }, CreatedAt = now,
            });
        }
        await ctx.SaveChangesAsync();

        return new Seed(project.Id, releasePipeline.Id, build.Id);
    }

    // The publish here only enqueues; these seams are never reached in these tests.
    private sealed class ThrowingTokenSource : IDeliveryTokenSource
    {
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised: publish_build tests only enqueue.");
    }

    private sealed class ThrowingAdminClient : IBcAdminClient
    {
        public Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcEnvironment?> GetEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcUpdateSettings?> GetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcEnvironmentOperation>> ListEnvironmentOperationsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcTenantStorage> GetTenantStorageAsync(string accessToken, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcTimeZone>> ListTimezonesAsync(string accessToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetAppUpdateCadenceAsync(string accessToken, string? applicationFamily, string environmentName, string cadence, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool?> GetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SelectTargetVersionAsync(string accessToken, string? applicationFamily, string environmentName, string targetVersion, string? targetVersionType, DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, TimeOnly start, TimeOnly end, string windowsTimeZoneId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecoverEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcSession>> ListSessionsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CancelSessionAsync(string accessToken, string? applicationFamily, string environmentName, int sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcEnvironmentCopy> CopyEnvironmentAsync(string accessToken, string? applicationFamily, string sourceEnvironmentName, string newEnvironmentName, string newEnvironmentType, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>These tools read history and enqueue; none of them talks to BC.</summary>
    private sealed class ThrowingAppManagementClient : IBcAppManagementClient
    {
        public Task<BcAppOperation> InstallPteAsync(string accessToken, string applicationFamily, string environmentName, byte[] appBytes, string fileName, string deploymentSchedule, string syncMode, string languageId, bool installOrUpdateNeededDependencies, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcAppOperation?> GetAppOperationAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, Guid operationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcScheduledPteOperation>> ListScheduledPteOperationsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcAvailableAppUpdate>> ListAvailableUpdatesAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcAppOperation> RemoveScheduledPteVersionAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, string scheduleKind, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcAppOperation> UpdateAppAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, bool useEnvironmentUpdateWindow, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
