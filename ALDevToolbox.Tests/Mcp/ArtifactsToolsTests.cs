using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// The MCP Artifacts surface — the agent-facing parallel of the Projects/Artifacts
/// web tools. Pins project/build listing, build detail with download paths, and the
/// project-scoped guard on compare_solution_builds. See .design/artifacts.md.
/// </summary>
public sealed class ArtifactsToolsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ArtifactsTools NewTools(Data.AppDbContext ctx)
    {
        var access = new ProjectAccess(ctx, _db.OrgContext);
        var discovery = new ProjectDiscoveryService(ctx, _db.OrgContext, access, new ProjectDiscoveryQueue(),
            NullLogger<ProjectDiscoveryService>.Instance);
        return new ArtifactsTools(
            new ArtifactService(ctx, access),
            new ReleaseComparisonService(ctx, access, NullLogger<ReleaseComparisonService>.Instance),
            new ProjectService(ctx, _db.OrgContext, access, discovery, NullLogger<ProjectService>.Instance));
    }

    [Fact]
    public async Task List_projects_and_builds_round_trip_by_name_and_id()
    {
        int projectId;
        await using (var ctx = _db.NewContext())
        {
            projectId = await SeedProjectAsync(ctx, "CRONUS A/S");
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow, bcVersion: "26.0", artifacts: 2);
        }

        await using var read = _db.NewContext();
        var tools = NewTools(read);

        (await tools.ListProjectsAsync()).Should().ContainSingle(p => p.Name == "CRONUS A/S");
        (await tools.ListProjectBuildsAsync("CRONUS A/S")).Should().ContainSingle();
        (await tools.ListProjectBuildsAsync(projectId.ToString())).Should().ContainSingle();
    }

    [Fact]
    public async Task Get_project_build_returns_download_paths()
    {
        int buildId;
        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS A/S");
            buildId = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow, bcVersion: "26.0", artifacts: 1);
            ctx.OeProjectBuildLogs.Add(new OeProjectBuildLog
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId,
                Section = "Build", Content = "ok", Ordering = 0, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var detail = await NewTools(read).GetProjectBuildAsync(buildId);

        detail.Apps.Should().ContainSingle();
        detail.Apps[0].DownloadPath.Should().StartWith($"/artifacts/build/{buildId}/app/");
        detail.DownloadAllPath.Should().Be($"/artifacts/build/{buildId}/all");
        detail.RawLogPath.Should().Be($"/artifacts/build/{buildId}/log");
    }

    [Fact]
    public async Task Get_project_build_throws_for_a_missing_build()
    {
        await using var read = _db.NewContext();
        var act = () => NewTools(read).GetProjectBuildAsync(999999);
        await act.Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task Compare_project_builds_rejects_builds_from_different_projects()
    {
        int b1, b2;
        await using (var ctx = _db.NewContext())
        {
            var p1 = await SeedProjectAsync(ctx, "Project One");
            var p2 = await SeedProjectAsync(ctx, "Project Two");
            b1 = await SeedBuildAsync(ctx, p1, ProjectBuildStatus.Ready, DateTime.UtcNow, releaseId: await SeedReleaseAsync(ctx));
            b2 = await SeedBuildAsync(ctx, p2, ProjectBuildStatus.Ready, DateTime.UtcNow, releaseId: await SeedReleaseAsync(ctx));
        }

        await using var read = _db.NewContext();
        var act = () => NewTools(read).CompareProjectBuildsAsync(b1, b2);
        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("same project");
    }

    [Fact]
    public async Task Compare_project_builds_rejects_a_non_ready_build()
    {
        int b1, b2;
        await using (var ctx = _db.NewContext())
        {
            var p = await SeedProjectAsync(ctx, "CRONUS A/S");
            b1 = await SeedBuildAsync(ctx, p, ProjectBuildStatus.Ready, DateTime.UtcNow, releaseId: await SeedReleaseAsync(ctx));
            b2 = await SeedBuildAsync(ctx, p, ProjectBuildStatus.Failed, DateTime.UtcNow);
        }

        await using var read = _db.NewContext();
        var act = () => NewTools(read).CompareProjectBuildsAsync(b1, b2);
        await act.Should().ThrowAsync<McpException>();
    }

    // ── seeding ─────────────────────────────────────────────────────────

    // ── Project visibility (slice 3) ─────────────────────────────────────

    private const int OwnerUserId = 9500;
    private const int AdminUserId = 9501;
    private const int MemberUserId = 9502;
    private const int OutsiderUserId = 9503;

    private void ActAs(int? userId, bool siteAdmin = false)
    {
        _db.OrgContext.CurrentUserId = userId;
        _db.OrgContext.IsSiteAdmin = siteAdmin;
    }

    [Fact]
    public async Task List_builds_returns_the_newest_twenty_unless_asked_for_more()
    {
        int projectId;
        await using (var ctx = _db.NewContext())
        {
            projectId = await SeedProjectAsync(ctx, "CRONUS A/S");
            for (var i = 0; i < 25; i++)
            {
                await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow.AddHours(-i));
            }
        }

        await using var read = _db.NewContext();
        var tools = NewTools(read);

        var newest = await tools.ListProjectBuildsAsync("CRONUS A/S");
        newest.Should().HaveCount(20);
        newest.Select(b => b.StartedAt).Should().BeInDescendingOrder();
        (await tools.ListProjectBuildsAsync("CRONUS A/S", limit: 5)).Should().HaveCount(5);
        (await tools.ListProjectBuildsAsync("CRONUS A/S", limit: 1000)).Should().HaveCount(25, "the cap is far above what there is");
        (await tools.ListProjectBuildsAsync("CRONUS A/S", limit: 0)).Should().HaveCount(1, "a limit below one still returns the newest");
    }

    private async Task SeedUsersAsync()
    {
        await using var ctx = _db.NewContext();
        foreach (var (id, email, role) in new[]
        {
            (OwnerUserId, "owner@example.com", ALDevToolbox.Domain.Entities.UserRole.Editor),
            (AdminUserId, "admin@example.com", ALDevToolbox.Domain.Entities.UserRole.Admin),
            (MemberUserId, "mel@example.com", ALDevToolbox.Domain.Entities.UserRole.User),
            (OutsiderUserId, "nils@example.com", ALDevToolbox.Domain.Entities.UserRole.User),
        })
        {
            ctx.Users.Add(new ALDevToolbox.Domain.Entities.User
            {
                Id = id,
                OrganizationId = TestDb.DefaultOrgId,
                Email = email,
                PasswordHash = "x",
                DisplayName = email,
                Role = role,
                Status = ALDevToolbox.Domain.Entities.UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await ctx.SaveChangesAsync();
    }

    /// <summary>A Private project owned by the owner, with one team the member is on.</summary>
    private async Task<(int ProjectId, int BuildId)> SeedPrivateProjectAsync(
        string name, ProjectVisibility visibility)
    {
        int projectId, teamId, buildId;
        await using (var ctx = _db.NewContext())
        {
            var project = new OeProject
            {
                OrganizationId = TestDb.DefaultOrgId, Name = name, CreatedByUserId = OwnerUserId,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            ctx.OeProjects.Add(project);
            var team = new ALDevToolbox.Domain.Entities.Team
            {
                OrganizationId = TestDb.DefaultOrgId, Name = "Team " + name,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            ctx.Teams.Add(team);
            await ctx.SaveChangesAsync();
            ctx.TeamMembers.Add(new ALDevToolbox.Domain.Entities.TeamMember
            {
                OrganizationId = TestDb.DefaultOrgId, TeamId = team.Id, UserId = MemberUserId,
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
            projectId = project.Id;
            teamId = team.Id;
            var releaseId = await SeedReleaseAsync(ctx);
            buildId = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow,
                bcVersion: "26.0", artifacts: 1, releaseId: releaseId);
        }

        if (visibility != ProjectVisibility.Public)
        {
            ActAs(OwnerUserId);
            await using var ctx = _db.NewContext();
            var access = new ProjectAccess(ctx, _db.OrgContext);
            var discovery = new ProjectDiscoveryService(ctx, _db.OrgContext, access, new ProjectDiscoveryQueue(),
                NullLogger<ProjectDiscoveryService>.Instance);
            var projects = new ProjectService(ctx, _db.OrgContext, access, discovery, NullLogger<ProjectService>.Instance);
            await projects.SetAccessAsync(projectId, visibility, new[] { teamId });
        }

        return (projectId, buildId);
    }

    public static TheoryData<ProjectVisibility> AllLevels => new()
    {
        ProjectVisibility.Public,
        ProjectVisibility.ReadOnly,
        ProjectVisibility.Private,
    };

    /// <summary>
    /// The web list keeps a locked, name-only row so a human doesn't think the
    /// project vanished. An agent has no such confusion to spare, so
    /// <c>list_solutions</c> drops it entirely.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllLevels))]
    public async Task List_projects_omits_a_private_project_entirely_for_a_non_member(
        ProjectVisibility visibility)
    {
        await SeedUsersAsync();
        await SeedPrivateProjectAsync("CRONUS Denmark", visibility);
        var hidden = visibility == ProjectVisibility.Private;

        foreach (var (userId, siteAdmin, expected) in new (int?, bool, bool)[]
        {
            (OutsiderUserId, false, !hidden),
            (MemberUserId, false, true),
            (AdminUserId, false, true),
            (OutsiderUserId, true, true),
        })
        {
            ActAs(userId, siteAdmin);
            await using var read = _db.NewContext();
            var rows = await NewTools(read).ListProjectsAsync();
            rows.Any(r => r.Name == "CRONUS Denmark").Should().Be(
                expected, $"user {userId} (siteAdmin={siteAdmin}) on a {visibility} project");
            // Never a locked row: the agent surface has no use for a name it can't act on.
            rows.Should().NotContain(r => r.IsLocked);
        }
    }

    [Fact]
    public async Task Resolving_a_private_project_by_name_or_id_answers_does_not_exist()
    {
        await SeedUsersAsync();
        var (projectId, _) = await SeedPrivateProjectAsync(
            "CRONUS Denmark", ProjectVisibility.Private);

        ActAs(OutsiderUserId);
        await using (var read = _db.NewContext())
        {
            var tools = NewTools(read);
            await ((Func<Task>)(() => tools.ListProjectBuildsAsync("CRONUS Denmark")))
                .Should().ThrowAsync<McpException>();
            await ((Func<Task>)(() => tools.ListProjectBuildsAsync(projectId.ToString())))
                .Should().ThrowAsync<McpException>();
        }

        ActAs(MemberUserId);
        await using (var read = _db.NewContext())
        {
            (await NewTools(read).ListProjectBuildsAsync("CRONUS Denmark")).Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Get_project_build_hides_a_private_projects_build_from_a_non_member()
    {
        await SeedUsersAsync();
        var (_, buildId) = await SeedPrivateProjectAsync(
            "CRONUS Denmark", ProjectVisibility.Private);

        ActAs(OutsiderUserId);
        await using (var read = _db.NewContext())
        {
            // Not a distinct refusal: the same "was not found" an unknown id gets.
            var thrown = await ((Func<Task>)(() => NewTools(read).GetProjectBuildAsync(buildId)))
                .Should().ThrowAsync<McpException>();
            thrown.Which.Message.Should().Contain("was not found");
        }

        ActAs(MemberUserId);
        await using (var read = _db.NewContext())
        {
            (await NewTools(read).GetProjectBuildAsync(buildId)).BuildId.Should().Be(buildId);
        }
    }

    [Fact]
    public async Task Comparing_two_builds_of_a_private_project_is_refused_to_a_non_member()
    {
        await SeedUsersAsync();
        var (projectId, firstBuild) = await SeedPrivateProjectAsync(
            "CRONUS Denmark", ProjectVisibility.Private);

        int secondBuild;
        await using (var ctx = _db.NewContext())
        {
            var releaseId = await SeedReleaseAsync(ctx);
            secondBuild = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready,
                DateTime.UtcNow.AddMinutes(1), bcVersion: "26.1", releaseId: releaseId);
        }

        ActAs(OutsiderUserId);
        await using (var read = _db.NewContext())
        {
            await ((Func<Task>)(() => NewTools(read).CompareProjectBuildsAsync(firstBuild, secondBuild)))
                .Should().ThrowAsync<McpException>();
        }

        ActAs(MemberUserId);
        await using (var read = _db.NewContext())
        {
            // Both releases are empty, so the diff is empty - but it runs.
            (await NewTools(read).CompareProjectBuildsAsync(firstBuild, secondBuild)).Should().BeEmpty();
        }
    }

    private static async Task<int> SeedProjectAsync(Data.AppDbContext ctx, string name)
    {
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private static async Task<int> SeedReleaseAsync(Data.AppDbContext ctx)
    {
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId, Label = "build", Kind = "project", Status = "ready",
            ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    private static async Task<int> SeedBuildAsync(
        Data.AppDbContext ctx, int projectId, string status, DateTime startedAt,
        string? bcVersion = null, int artifacts = 0, int? releaseId = null)
    {
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Status = status, BcVersion = bcVersion,
            StartedAt = startedAt, ReleaseId = releaseId,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        for (var i = 0; i < artifacts; i++)
        {
            ctx.OeProjectBuildArtifacts.Add(new OeProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id,
                FileName = $"app{i}.app", AppName = $"App {i}", AppVersion = "1.0.0.0",
                SizeBytes = 1, Content = new byte[] { (byte)i }, CreatedAt = DateTime.UtcNow,
            });
        }
        if (artifacts > 0) await ctx.SaveChangesAsync();
        return build.Id;
    }
}
