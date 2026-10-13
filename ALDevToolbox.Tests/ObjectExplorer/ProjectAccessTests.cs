using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The two-axis authorisation contract from <c>.design/teams-and-visibility.md</c>:
/// <see cref="ProjectService.SetAccessAsync"/> holds the
/// <c>Visibility != Public</c> ⇔ <em>at least one team</em> invariant, deleting a
/// team is refused while it is the last one on a non-Public project, being on an
/// assigned team grants manage but never delete, and every project-id read surface
/// hides a Private project from someone with no grant on it.
/// </summary>
public sealed class ProjectAccessTests : IDisposable
{
    private readonly TestDb _db = new();

    private const int OwnerUserId = 9300;
    private const int AdminUserId = 9301;
    private const int TeamMemberUserId = 9302;
    private const int OtherTeamUserId = 9303;
    private const int PlainUserId = 9304;

    private readonly ProjectDiscoveryQueue _discoveryQueue = new();

    public ProjectAccessTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.AddRange(
            NewUser(OwnerUserId, "owner@example.com", "Olive Owner", UserRole.Editor),
            NewUser(AdminUserId, "admin@example.com", "Ada Admin", UserRole.Admin),
            NewUser(TeamMemberUserId, "mel@example.com", "Mel Member", UserRole.User),
            NewUser(OtherTeamUserId, "nils@example.com", "Nils Other", UserRole.User),
            NewUser(PlainUserId, "pat@example.com", "Pat Plain", UserRole.User));
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose() => _db.Dispose();

    private static User NewUser(int id, string email, string displayName, UserRole role) => new()
    {
        Id = id,
        OrganizationId = TestDb.DefaultOrgId,
        Email = email,
        PasswordHash = "x",
        DisplayName = displayName,
        Role = role,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };

    /// <summary>Acts as <paramref name="userId"/> for the rest of the test. Null = a background worker with no user.</summary>
    private void ActAs(int? userId, bool siteAdmin = false)
    {
        _db.OrgContext.CurrentUserId = userId;
        _db.OrgContext.IsSiteAdmin = siteAdmin;
    }

    private ProjectAccess Access(AppDbContext ctx) => new(ctx, _db.OrgContext);

    private ProjectService Svc(AppDbContext ctx)
    {
        var access = Access(ctx);
        var discovery = new ProjectDiscoveryService(
            ctx, _db.OrgContext, access, _discoveryQueue, NullLogger<ProjectDiscoveryService>.Instance);
        return new ProjectService(ctx, _db.OrgContext, access, discovery, _db.NewOfflineGitHubAccessService(ctx), NullLogger<ProjectService>.Instance);
    }

    private TeamService Teams(AppDbContext ctx) => new(ctx, _db.OrgContext, NullLogger<TeamService>.Instance);

    private ArtifactService Artifacts(AppDbContext ctx) => new(ctx, Access(ctx));

    // ── Seeding ─────────────────────────────────────────────────────────

    private async Task<int> SeedProjectAsync(string name = "CRONUS Denmark", int? ownerId = OwnerUserId)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            DefaultArtifactCountry = "dk",
            CreatedByUserId = ownerId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private async Task<int> SeedTeamAsync(string name, params int[] memberUserIds)
    {
        await using var ctx = _db.NewContext();
        var team = new Team
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Teams.Add(team);
        await ctx.SaveChangesAsync();

        foreach (var userId in memberUserIds)
        {
            ctx.TeamMembers.Add(new TeamMember
            {
                OrganizationId = TestDb.DefaultOrgId,
                TeamId = team.Id,
                UserId = userId,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await ctx.SaveChangesAsync();
        return team.Id;
    }

    /// <summary>Sets a project's access as the owner, then hands the acting user back to the caller.</summary>
    private async Task SetAccessAsOwnerAsync(int projectId, ProjectVisibility visibility, params int[] teamIds)
    {
        var previous = _db.OrgContext.CurrentUserId;
        var previousSiteAdmin = _db.OrgContext.IsSiteAdmin;
        ActAs(OwnerUserId);
        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx).SetAccessAsync(projectId, visibility, teamIds);
        }
        ActAs(previous, previousSiteAdmin);
    }

    // ── SetAccessAsync: the invariant ───────────────────────────────────

    [Theory]
    [InlineData(ProjectVisibility.ReadOnly)]
    [InlineData(ProjectVisibility.Private)]
    public async Task SetAccess_stores_the_level_and_its_teams(ProjectVisibility visibility)
    {
        var projectId = await SeedProjectAsync();
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);

        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx).SetAccessAsync(projectId, visibility, new[] { teamId });
        }

        await using (var ctx = _db.NewContext())
        {
            var access = await Svc(ctx).GetAccessAsync(projectId);
            access.Visibility.Should().Be(visibility);
            access.TeamIds.Should().Equal(teamId);
        }
    }

    [Fact]
    public async Task SetAccess_back_to_public_drops_the_teams()
    {
        var projectId = await SeedProjectAsync();
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.Private, teamId);

        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx).SetAccessAsync(projectId, ProjectVisibility.Public, Array.Empty<int>());
        }

        await using (var ctx = _db.NewContext())
        {
            var access = await Svc(ctx).GetAccessAsync(projectId);
            access.Visibility.Should().Be(ProjectVisibility.Public);
            access.TeamIds.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData(ProjectVisibility.ReadOnly)]
    [InlineData(ProjectVisibility.Private)]
    public async Task SetAccess_refuses_a_non_public_level_with_no_teams(ProjectVisibility visibility)
    {
        var projectId = await SeedProjectAsync();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx).SetAccessAsync(projectId, visibility, Array.Empty<int>());

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Teams");
    }

    [Fact]
    public async Task SetAccess_refuses_public_with_teams()
    {
        var projectId = await SeedProjectAsync();
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx).SetAccessAsync(projectId, ProjectVisibility.Public, new[] { teamId });

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Teams");
    }

    [Fact]
    public async Task SetAccess_refuses_a_team_from_another_organisation()
    {
        var projectId = await SeedProjectAsync();

        int foreignTeamId;
        await using (var ctx = _db.NewContext())
        {
            var team = new Team
            {
                OrganizationId = TestDb.OtherOrgId,
                Name = "Somebody else's team",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.Teams.Add(team);
            await ctx.SaveChangesAsync();
            foreignTeamId = team.Id;
        }

        await using (var ctx = _db.NewContext())
        {
            // The org query filter makes it simply not found, which is the right
            // answer and the right error.
            var act = () => Svc(ctx).SetAccessAsync(projectId, ProjectVisibility.Private, new[] { foreignTeamId });
            (await act.Should().ThrowAsync<PlanValidationException>())
                .Which.Errors.Should().ContainKey("Teams");
        }

        await using (var verify = _db.NewContext())
        {
            (await verify.OeProjects.AsNoTracking().FirstAsync(p => p.Id == projectId))
                .Visibility.Should().Be(ProjectVisibility.Public);
        }
    }

    [Fact]
    public async Task SetAccess_is_refused_to_a_plain_user()
    {
        var projectId = await SeedProjectAsync();
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        ActAs(PlainUserId);

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx).SetAccessAsync(projectId, ProjectVisibility.Private, new[] { teamId });
        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    // ── Team delete refusal ─────────────────────────────────────────────

    [Fact]
    public async Task Deleting_the_last_team_on_a_private_project_is_refused_and_names_it()
    {
        var projectId = await SeedProjectAsync("CRONUS Denmark");
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.Private, teamId);
        ActAs(AdminUserId);

        await using (var ctx = _db.NewContext())
        {
            var act = () => Teams(ctx).DeleteTeamAsync(teamId);
            var thrown = (await act.Should().ThrowAsync<PlanValidationException>()).Which;
            thrown.Errors.Should().ContainKey("Name");
            thrown.Errors["Name"].Should().Contain("CRONUS Denmark");
        }

        await using (var verify = _db.NewContext())
        {
            (await verify.Teams.AsNoTracking().AnyAsync(t => t.Id == teamId)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Deleting_a_team_is_allowed_when_the_project_has_a_second_one()
    {
        var projectId = await SeedProjectAsync();
        var first = await SeedTeamAsync("Nordics", TeamMemberUserId);
        var second = await SeedTeamAsync("Platform", OtherTeamUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.Private, first, second);
        ActAs(AdminUserId);

        await using (var ctx = _db.NewContext())
        {
            await Teams(ctx).DeleteTeamAsync(second);
        }

        await using (var verify = _db.NewContext())
        {
            (await verify.Teams.AsNoTracking().AnyAsync(t => t.Id == second)).Should().BeFalse();
            // The assignment row went with it; the project keeps its other team.
            (await verify.OeProjectTeams.AsNoTracking().CountAsync(t => t.ProjectId == projectId)).Should().Be(1);
        }
    }

    [Fact]
    public async Task Deleting_a_team_is_allowed_once_the_project_is_public_again()
    {
        var projectId = await SeedProjectAsync();
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.Private, teamId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.Public);
        ActAs(AdminUserId);

        await using (var ctx = _db.NewContext())
        {
            await Teams(ctx).DeleteTeamAsync(teamId);
        }

        await using (var verify = _db.NewContext())
        {
            (await verify.Teams.AsNoTracking().AnyAsync(t => t.Id == teamId)).Should().BeFalse();
        }
    }

    // ── Manage / delete matrix ──────────────────────────────────────────

    public static TheoryData<ProjectVisibility> AllLevels => new()
    {
        ProjectVisibility.Public, ProjectVisibility.ReadOnly, ProjectVisibility.Private,
    };

    [Theory]
    [MemberData(nameof(AllLevels))]
    public async Task Manage_matrix(ProjectVisibility visibility)
    {
        var projectId = await SeedProjectAsync();
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SeedTeamAsync("Elsewhere", OtherTeamUserId);
        if (visibility != ProjectVisibility.Public)
        {
            await SetAccessAsOwnerAsync(projectId, visibility, teamId);
        }

        (await CanManageAsAsync(projectId, OwnerUserId)).Should().BeTrue("the owner always manages");
        (await CanManageAsAsync(projectId, AdminUserId)).Should().BeTrue("an org Admin always manages");
        (await CanManageAsAsync(projectId, PlainUserId, siteAdmin: true)).Should().BeTrue("a SiteAdmin always manages");

        // Public is open both ways, so everyone manages it; the two narrower levels
        // reserve writing for the teams, and being on an unassigned team is the same
        // as being on none.
        var openToEveryone = visibility == ProjectVisibility.Public;
        (await CanManageAsAsync(projectId, PlainUserId)).Should().Be(openToEveryone);
        (await CanManageAsAsync(projectId, OtherTeamUserId)).Should().Be(openToEveryone);

        // An assigned team grants manage at the levels where manage is not already
        // everyone's - which is what a team is for.
        (await CanManageAsAsync(projectId, TeamMemberUserId)).Should().BeTrue();
    }

    /// <summary>
    /// The two carve-outs that keep "Public means everyone manages it" from being a
    /// blanket grant: ending the solution, and acting on the customer's own tenant.
    /// Neither follows from managing it, at any level.
    /// </summary>
    [Fact]
    public async Task A_colleague_manages_a_public_project_but_cannot_delete_it_or_schedule_its_updates()
    {
        var projectId = await SeedProjectAsync();
        ActAs(PlainUserId);

        await using (var ctx = _db.NewContext())
        {
            (await Svc(ctx).CanManageAsync(projectId)).Should().BeTrue("Public is open both ways");
            (await Access(ctx).CanManageEnvironmentUpdatesAsync(projectId))
                .Should().BeFalse("the update flag comes through an assigned team, never from manage");
        }

        await using (var ctx = _db.NewContext())
        {
            var act = () => Svc(ctx).SoftDeleteProjectAsync(projectId);
            await act.Should().ThrowAsync<ProjectAccessDeniedException>();
        }

        await using (var verify = _db.NewContext())
        {
            (await verify.OeProjects.AsNoTracking().FirstAsync(p => p.Id == projectId))
                .DeletedAt.Should().BeNull();
        }
    }

    /// <summary>
    /// The level that closes writing is Read-only, and it has to actually close it -
    /// otherwise the ladder has no middle rung.
    /// </summary>
    [Fact]
    public async Task A_colleague_does_not_manage_a_read_only_project()
    {
        var projectId = await SeedProjectAsync();
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.ReadOnly, teamId);

        ActAs(PlainUserId);
        await using var ctx = _db.NewContext();
        (await Svc(ctx).CanManageAsync(projectId)).Should().BeFalse();
        (await Access(ctx).CanViewAsync(projectId)).Should().BeTrue("Read-only still reads to everyone");
    }

    [Fact]
    public async Task Delete_is_denied_to_an_assigned_team_member_who_may_otherwise_manage()
    {
        var projectId = await SeedProjectAsync();
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.Private, teamId);
        ActAs(TeamMemberUserId);

        await using (var ctx = _db.NewContext())
        {
            // Manage yes...
            (await Svc(ctx).CanManageAsync(projectId)).Should().BeTrue();
        }

        await using (var ctx = _db.NewContext())
        {
            // ...delete no. A team grant is about doing the work, not ending it.
            var act = () => Svc(ctx).SoftDeleteProjectAsync(projectId);
            await act.Should().ThrowAsync<ProjectAccessDeniedException>();
        }

        await using (var verify = _db.NewContext())
        {
            (await verify.OeProjects.AsNoTracking().FirstAsync(p => p.Id == projectId))
                .DeletedAt.Should().BeNull();
        }
    }

    [Fact]
    public async Task Delete_is_allowed_to_the_owner_and_to_an_org_admin()
    {
        var ownerProject = await SeedProjectAsync("Owner's");
        var adminProject = await SeedProjectAsync("Admin's");

        ActAs(OwnerUserId);
        await using (var ctx = _db.NewContext()) await Svc(ctx).SoftDeleteProjectAsync(ownerProject);

        ActAs(AdminUserId);
        await using (var ctx = _db.NewContext()) await Svc(ctx).SoftDeleteProjectAsync(adminProject);

        await using (var verify = _db.NewContext())
        {
            (await verify.OeProjects.AsNoTracking().Where(p => p.DeletedAt == null).CountAsync()).Should().Be(0);
        }
    }

    private async Task<bool> CanManageAsAsync(int projectId, int userId, bool siteAdmin = false)
    {
        ActAs(userId, siteAdmin);
        await using var ctx = _db.NewContext();
        return await Svc(ctx).CanManageAsync(projectId);
    }

    // ── View matrix ─────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllLevels))]
    public async Task View_matrix(ProjectVisibility visibility)
    {
        var projectId = await SeedProjectAsync();
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SeedTeamAsync("Elsewhere", OtherTeamUserId);
        if (visibility != ProjectVisibility.Public)
        {
            await SetAccessAsOwnerAsync(projectId, visibility, teamId);
        }

        (await CanViewAsAsync(projectId, OwnerUserId)).Should().BeTrue();
        (await CanViewAsAsync(projectId, AdminUserId)).Should().BeTrue();
        (await CanViewAsAsync(projectId, PlainUserId, siteAdmin: true)).Should().BeTrue();
        (await CanViewAsAsync(projectId, TeamMemberUserId)).Should().BeTrue();

        // Read-only is about who may *change* it; everyone still reads it. Only
        // Private closes the door.
        var hiddenFromOutsiders = visibility == ProjectVisibility.Private;
        (await CanViewAsAsync(projectId, PlainUserId)).Should().Be(!hiddenFromOutsiders);
        (await CanViewAsAsync(projectId, OtherTeamUserId)).Should().Be(!hiddenFromOutsiders);
    }

    private async Task<bool> CanViewAsAsync(int projectId, int userId, bool siteAdmin = false)
    {
        ActAs(userId, siteAdmin);
        await using var ctx = _db.NewContext();
        return await Access(ctx).CanViewAsync(projectId);
    }

    [Fact]
    public async Task A_null_user_snapshot_grants_nothing_and_never_throws()
    {
        var projectId = await SeedProjectAsync();
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.Private, teamId);

        // A background worker running under an ambient org scope has no user.
        ActAs(null);
        await using var ctx = _db.NewContext();
        var access = Access(ctx);

        var snapshot = await access.GetSnapshotAsync();
        snapshot.UserId.Should().BeNull();
        snapshot.IsOrgAdmin.Should().BeFalse();
        snapshot.BypassesVisibility.Should().BeFalse();
        snapshot.TeamIds.Should().BeEmpty();

        (await access.CanViewAsync(projectId)).Should().BeFalse();
        (await access.CanManageAsync(projectId, OwnerUserId)).Should().BeFalse();
        (await access.CanDeleteAsync(OwnerUserId)).Should().BeFalse();
    }

    // ── Gated reads ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_private_project_is_hidden_from_every_project_keyed_read()
    {
        var projectId = await SeedProjectAsync("CRONUS Denmark");
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.Private, teamId);

        // A non-member gets refused everywhere.
        ActAs(PlainUserId);
        await using (var ctx = _db.NewContext())
        {
            var projects = Svc(ctx);
            var artifacts = Artifacts(ctx);

            await ((Func<Task>)(() => projects.GetProjectAsync(projectId)))
                .Should().ThrowAsync<ProjectAccessDeniedException>();
            await ((Func<Task>)(() => projects.ListProjectReleasesAsync(projectId)))
                .Should().ThrowAsync<ProjectAccessDeniedException>();
            await ((Func<Task>)(() => projects.ListSupplementalSymbolsAsync(projectId)))
                .Should().ThrowAsync<ProjectAccessDeniedException>();
            await ((Func<Task>)(() => artifacts.GetProjectHeaderAsync(projectId)))
                .Should().ThrowAsync<ProjectAccessDeniedException>();
            await ((Func<Task>)(() => artifacts.ListBuildsForProjectAsync(projectId)))
                .Should().ThrowAsync<ProjectAccessDeniedException>();

            // The picker list omits it rather than refusing — a name you can't act
            // on is only in the way of choosing one you can.
            (await projects.ListProjectsAsync()).Should().BeEmpty();
        }

        // A member of the assigned team gets everything.
        ActAs(TeamMemberUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Svc(ctx).GetProjectAsync(projectId)).Should().NotBeNull();
            (await Artifacts(ctx).GetProjectHeaderAsync(projectId))!.Name.Should().Be("CRONUS Denmark");
            (await Svc(ctx).ListProjectsAsync()).Should().ContainSingle();
        }

        // An org Admin bypasses visibility entirely.
        ActAs(AdminUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Svc(ctx).GetProjectAsync(projectId)).Should().NotBeNull();
            (await Svc(ctx).ListProjectsAsync()).Should().ContainSingle();
        }
    }

    private sealed record SeededBuild(int PipelineId, int BuildId, int ArtifactId);

    /// <summary>A pipeline on <paramref name="projectId"/> with one successful build and its one app.</summary>
    private async Task<SeededBuild> SeedPipelineBuildAsync(int projectId, string appName = "CRONUS Sales")
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId, Label = appName, BcVersion = "",
            DedupKey = Guid.NewGuid().ToString(), Kind = OeRelease.ProjectBuildKind, Status = "ready",
            ImportedAt = now, CreatedAt = now,
        };
        ctx.OeReleases.Add(release);
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = appName, CreatedAt = now, UpdatedAt = now,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipeline.Id, ReleaseId = release.Id,
            Status = ProjectBuildStatus.Ready, StartedAt = now, FinishedAt = now,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        var artifact = new OeProjectBuildArtifact
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id, FileName = $"{appName}_1.0.0.0.app",
            AppName = appName, AppVersion = "1.0.0.0", SizeBytes = 3, Content = new byte[] { 1, 2, 3 }, CreatedAt = now,
        };
        ctx.OeProjectBuildArtifacts.Add(artifact);
        await ctx.SaveChangesAsync();
        return new SeededBuild(pipeline.Id, build.Id, artifact.Id);
    }

    /// <summary>Every <see cref="ArtifactService"/> read keyed by a pipeline id, run once each.</summary>
    private static IEnumerable<(string Name, Func<Task> Read)> PipelineKeyedReads(ArtifactService artifacts, SeededBuild seed) =>
    [
        ("GetPipelineHeaderAsync", () => artifacts.GetPipelineHeaderAsync(seed.PipelineId)),
        ("ListBuildsAsync", () => artifacts.ListBuildsAsync(seed.PipelineId)),
        ("GetPipelineBuildRowAsync", () => artifacts.GetPipelineBuildRowAsync(seed.PipelineId, seed.BuildId)),
        ("CountBuildsAsync", () => artifacts.CountBuildsAsync(seed.PipelineId)),
        ("ListDeployableBuildsAsync", () => artifacts.ListDeployableBuildsAsync(seed.PipelineId)),
        ("ListBuildAppsAsync", () => artifacts.ListBuildAppsAsync(seed.PipelineId, [seed.BuildId])),
        ("HasBuildInFlightAsync", () => artifacts.HasBuildInFlightAsync(seed.PipelineId)),
        ("IsBuildRunningAsync", () => artifacts.IsBuildRunningAsync(seed.PipelineId)),
        ("ListComparableBuildsAsync", () => artifacts.ListComparableBuildsAsync(seed.PipelineId)),
    ];

    /// <summary>
    /// A pipeline inherits its solution's visibility (#1274): every read that takes a
    /// pipeline id refuses someone with no grant on a Private solution, answers a
    /// member of its team, and the pipelines list leaves it out for the first.
    /// </summary>
    [Fact]
    public async Task A_private_projects_pipelines_are_hidden_from_every_pipeline_keyed_read()
    {
        var projectId = await SeedProjectAsync("CRONUS Denmark");
        var openId = await SeedProjectAsync("CRONUS Norway");
        var seed = await SeedPipelineBuildAsync(projectId, "CRONUS Sales");
        await SeedPipelineBuildAsync(openId, "CRONUS Service");
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.Private, teamId);

        ActAs(PlainUserId);
        await using (var ctx = _db.NewContext())
        {
            var artifacts = Artifacts(ctx);
            foreach (var (name, read) in PipelineKeyedReads(artifacts, seed))
            {
                await read.Should().ThrowAsync<ProjectAccessDeniedException>(name);
            }
            (await artifacts.ListPipelinesAsync()).Select(p => p.Name).Should().Equal("CRONUS Service");
        }

        ActAs(TeamMemberUserId);
        await using (var ctx = _db.NewContext())
        {
            var artifacts = Artifacts(ctx);
            foreach (var (name, read) in PipelineKeyedReads(artifacts, seed))
            {
                await read.Should().NotThrowAsync(name);
            }
            (await artifacts.GetPipelineHeaderAsync(seed.PipelineId)).Should().NotBeNull();
            (await artifacts.ListBuildsAsync(seed.PipelineId)).Should().ContainSingle();
            (await artifacts.ListPipelinesAsync()).Select(p => p.Name).Should().Equal("CRONUS Sales", "CRONUS Service");
        }
    }

    /// <summary>
    /// The download checks visibility on the build id it is given, so the artifact has to
    /// belong to that build (#1274): a build the caller may see, paired with the id of a
    /// Private solution's app, must not hand that app over.
    /// </summary>
    [Fact]
    public async Task A_visible_build_id_does_not_unlock_a_private_builds_artifact()
    {
        var privateId = await SeedProjectAsync("CRONUS Denmark");
        var openId = await SeedProjectAsync("CRONUS Norway");
        var hidden = await SeedPipelineBuildAsync(privateId, "CRONUS Sales");
        var visible = await SeedPipelineBuildAsync(openId, "CRONUS Service");
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SetAccessAsOwnerAsync(privateId, ProjectVisibility.Private, teamId);

        ActAs(PlainUserId);
        await using var ctx = _db.NewContext();
        var artifacts = Artifacts(ctx);

        (await artifacts.GetArtifactBytesAsync(visible.BuildId, hidden.ArtifactId)).Should().BeNull();
        (await artifacts.ListBuildAppsAsync(visible.PipelineId, [hidden.BuildId])).Should().BeEmpty(
            "the apps are pinned to the pipeline asked about");
        (await artifacts.GetArtifactBytesAsync(visible.BuildId, visible.ArtifactId)).Should().NotBeNull();
    }

    /// <summary>
    /// Adding and removing a solution's dependency symbols is managing it (#1274):
    /// a colleague may read a Read-only solution but neither add nor delete them.
    /// </summary>
    [Fact]
    public async Task A_colleague_cannot_add_or_delete_supplemental_symbols_on_a_read_only_project()
    {
        var projectId = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx).AddSupplementalSymbolsAsync(projectId, [new SupplementalSymbolUpload("Dep.app", [1])]);
        }
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.ReadOnly, teamId);

        ActAs(PlainUserId);
        await using (var ctx = _db.NewContext())
        {
            var projects = Svc(ctx);
            var symbolId = (await projects.ListSupplementalSymbolsAsync(projectId)).Single().Id;

            await ((Func<Task>)(() => projects.AddSupplementalSymbolsAsync(projectId, [new SupplementalSymbolUpload("Other.app", [2])])))
                .Should().ThrowAsync<ProjectAccessDeniedException>();
            await ((Func<Task>)(() => projects.DeleteSupplementalSymbolAsync(projectId, symbolId)))
                .Should().ThrowAsync<ProjectAccessDeniedException>();
        }

        await using (var verify = _db.NewContext())
        {
            (await verify.OeProjectSymbols.AsNoTracking().Where(s => s.ProjectId == projectId).Select(s => s.FileName).ToListAsync())
                .Should().Equal("Dep.app");
        }
    }

    /// <summary>
    /// The delete is pinned to the solution it names (#1274): managing one solution does not
    /// reach a symbol stored on another by passing its id.
    /// </summary>
    [Fact]
    public async Task Deleting_another_projects_symbol_through_a_managed_project_leaves_it_in_place()
    {
        var mineId = await SeedProjectAsync("CRONUS Denmark");
        var theirsId = await SeedProjectAsync("CRONUS Norway", ownerId: OtherTeamUserId);
        await using (var ctx = _db.NewContext())
        {
            ActAs(OtherTeamUserId);
            await Svc(ctx).AddSupplementalSymbolsAsync(theirsId, [new SupplementalSymbolUpload("Dep.app", [1])]);
        }
        var teamId = await SeedTeamAsync("Norway", OtherTeamUserId);
        ActAs(OtherTeamUserId);
        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx).SetAccessAsync(theirsId, ProjectVisibility.ReadOnly, [teamId]);
        }
        int theirSymbolId;
        await using (var ctx = _db.NewContext())
        {
            theirSymbolId = (await ctx.OeProjectSymbols.AsNoTracking().SingleAsync(s => s.ProjectId == theirsId)).Id;
        }

        ActAs(OwnerUserId);
        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx).DeleteSupplementalSymbolAsync(mineId, theirSymbolId);
        }

        await using (var verify = _db.NewContext())
        {
            (await verify.OeProjectSymbols.AsNoTracking().AnyAsync(s => s.Id == theirSymbolId)).Should().BeTrue();
        }
    }

    /// <summary>
    /// The generator's Solution picker reads through the same gate: a customer
    /// this person cannot see is not offered as one to start a workspace for.
    /// </summary>
    [Fact]
    public async Task The_solution_picker_omits_a_private_solution_the_user_has_no_grant_on()
    {
        var openId = await SeedProjectAsync("CRONUS Norway");
        var privateId = await SeedProjectAsync("CRONUS Denmark");
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SetAccessAsOwnerAsync(privateId, ProjectVisibility.Private, teamId);

        ActAs(PlainUserId);
        await using (var ctx = _db.NewContext())
        {
            var options = await Svc(ctx).ListSolutionOptionsAsync();
            options.Select(o => o.Id).Should().Equal(openId);
        }

        ActAs(TeamMemberUserId);
        await using (var ctx = _db.NewContext())
        {
            var options = await Svc(ctx).ListSolutionOptionsAsync();
            options.Select(o => o.Name).Should().Equal("CRONUS Denmark", "CRONUS Norway");
        }
    }

    [Fact]
    public async Task The_projects_list_shows_a_private_project_as_a_name_only_locked_row()
    {
        var visibleId = await SeedProjectAsync("CRONUS Norway");
        var privateId = await SeedProjectAsync("CRONUS Denmark");
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SetAccessAsOwnerAsync(privateId, ProjectVisibility.Private, teamId);

        ActAs(PlainUserId);
        await using var ctx = _db.NewContext();
        var rows = await Artifacts(ctx).ListProjectsAsync();

        rows.Should().HaveCount(2);
        var locked = rows.Single(r => r.Id == privateId);
        locked.IsLocked.Should().BeTrue();
        locked.Name.Should().Be("CRONUS Denmark");
        locked.OwnerName.Should().BeNull("the owner reports who is working on the customer");
        locked.RepoCount.Should().Be(0);
        locked.RepoNames.Should().BeEmpty();
        locked.Latest.Should().BeNull("build status reports activity on the customer");
        locked.LatestSuccessfulBuildId.Should().BeNull();

        rows.Single(r => r.Id == visibleId).IsLocked.Should().BeFalse();
    }

    [Fact]
    public async Task A_member_and_an_admin_see_the_private_project_unlocked()
    {
        var projectId = await SeedProjectAsync("CRONUS Denmark");
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.Private, teamId);

        foreach (var userId in new[] { TeamMemberUserId, AdminUserId, OwnerUserId })
        {
            ActAs(userId);
            await using var ctx = _db.NewContext();
            var rows = await Artifacts(ctx).ListProjectsAsync();
            rows.Should().ContainSingle().Which.IsLocked.Should().BeFalse($"user {userId} has a grant on it");
        }
    }

    // ── Audit ───────────────────────────────────────────────────────────

    /// <summary>
    /// Who could see a customer's project, and when that changed, is the point of
    /// the feature — so both halves of the write land in the audit log: the
    /// visibility column through the <c>OeProject</c> gate, the assignment as its
    /// own <c>ProjectTeam</c> row.
    /// </summary>
    [Fact]
    public async Task Changing_a_projects_access_is_audited_on_both_halves()
    {
        var projectId = await SeedProjectAsync();
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);

        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            await Svc(ctx).SetAccessAsync(projectId, ProjectVisibility.Private, new[] { teamId });
        }
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            await Svc(ctx).SetAccessAsync(projectId, ProjectVisibility.Public, Array.Empty<int>());
        }

        await using var read = _db.NewContext();
        var assignments = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.ProjectTeam)
            .OrderBy(r => r.Id)
            .Select(r => r.Action)
            .ToListAsync();
        assignments.Should().Equal(AuditAction.Created, AuditAction.Deleted);

        var visibility = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.Project)
            .OrderBy(r => r.Id)
            .Select(r => r.Action)
            .ToListAsync();
        visibility.Should().Equal(AuditAction.Updated, AuditAction.Updated);
    }

    [Fact]
    public async Task A_read_only_project_stays_readable_by_everyone()
    {
        var projectId = await SeedProjectAsync("CRONUS Denmark");
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.ReadOnly, teamId);

        ActAs(PlainUserId);
        await using var ctx = _db.NewContext();
        (await Svc(ctx).GetProjectAsync(projectId)).Should().NotBeNull();
        (await Artifacts(ctx).ListProjectsAsync()).Should().ContainSingle().Which.IsLocked.Should().BeFalse();
    }

    // ── Environment-update axis (issue #657) ────────────────────────────

    /// <summary>Stamps <c>ManagesUpdates</c> on an existing membership row.</summary>
    private async Task GrantUpdateFlagAsync(int teamId, int userId)
    {
        await using var ctx = _db.NewContext();
        var member = await ctx.TeamMembers.SingleAsync(m => m.TeamId == teamId && m.UserId == userId);
        member.ManagesUpdates = true;
        await ctx.SaveChangesAsync();
    }

    /// <summary>Runs <see cref="ProjectAccess.UpdateOpsProjectPredicate"/> as a query, as a page would.</summary>
    private async Task<List<int>> OperableProjectIdsAsync(AppDbContext ctx)
    {
        var access = Access(ctx);
        var snapshot = await access.GetSnapshotAsync();
        return await ctx.OeProjects.AsNoTracking()
            .Where(ProjectAccess.UpdateOpsProjectPredicate(snapshot))
            .Select(p => p.Id)
            .ToListAsync();
    }

    [Fact]
    public async Task The_flag_in_an_assigned_team_grants_update_ops()
    {
        var projectId = await SeedProjectAsync();
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await GrantUpdateFlagAsync(teamId, TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.ReadOnly, teamId);

        ActAs(TeamMemberUserId);
        await using var ctx = _db.NewContext();
        (await Access(ctx).CanManageEnvironmentUpdatesAsync(projectId)).Should().BeTrue();
        (await OperableProjectIdsAsync(ctx)).Should().Equal(projectId);
    }

    [Fact]
    public async Task The_flag_in_a_team_the_project_does_not_have_grants_nothing()
    {
        var projectId = await SeedProjectAsync();
        var assigned = await SeedTeamAsync("Nordics", PlainUserId);
        var elsewhere = await SeedTeamAsync("Benelux", TeamMemberUserId);
        await GrantUpdateFlagAsync(elsewhere, TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.ReadOnly, assigned);

        ActAs(TeamMemberUserId);
        await using var ctx = _db.NewContext();
        (await Access(ctx).CanManageEnvironmentUpdatesAsync(projectId)).Should().BeFalse();
        (await OperableProjectIdsAsync(ctx)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_team_manager_without_the_flag_cannot_run_update_ops()
    {
        var projectId = await SeedProjectAsync();
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await using (var ctx = _db.NewContext())
        {
            var member = await ctx.TeamMembers.SingleAsync(m => m.TeamId == teamId);
            member.IsManager = true;
            await ctx.SaveChangesAsync();
        }
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.ReadOnly, teamId);

        ActAs(TeamMemberUserId);
        await using var read = _db.NewContext();
        // Managing the team is a different axis from operating its projects' updates.
        (await Access(read).CanManageEnvironmentUpdatesAsync(projectId)).Should().BeFalse();
        (await OperableProjectIdsAsync(read)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_plain_member_of_the_assigned_team_cannot_run_update_ops()
    {
        var projectId = await SeedProjectAsync();
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.ReadOnly, teamId);

        ActAs(TeamMemberUserId);
        await using var ctx = _db.NewContext();
        // Being on the team grants manage, but never the update flag.
        (await Access(ctx).CanManageAsync(projectId, OwnerUserId)).Should().BeTrue();
        (await Access(ctx).CanManageEnvironmentUpdatesAsync(projectId)).Should().BeFalse();
    }

    [Fact]
    public async Task The_owner_alone_cannot_run_update_ops()
    {
        var projectId = await SeedProjectAsync();

        ActAs(OwnerUserId);
        await using var ctx = _db.NewContext();
        // Owning a project is not permission to touch the customer's tenant.
        (await Access(ctx).CanManageAsync(projectId, OwnerUserId)).Should().BeTrue();
        (await Access(ctx).CanManageEnvironmentUpdatesAsync(projectId)).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admins_can_run_update_ops_on_a_team_less_project(bool siteAdmin)
    {
        var projectId = await SeedProjectAsync();

        ActAs(siteAdmin ? PlainUserId : AdminUserId, siteAdmin);
        await using var ctx = _db.NewContext();
        (await Access(ctx).CanManageEnvironmentUpdatesAsync(projectId)).Should().BeTrue();
        (await OperableProjectIdsAsync(ctx)).Should().Equal(projectId);
    }

    [Fact]
    public async Task EnsureCanManageEnvironmentUpdates_throws_for_somebody_without_the_flag()
    {
        var projectId = await SeedProjectAsync();

        ActAs(PlainUserId);
        await using var ctx = _db.NewContext();
        var act = () => Access(ctx).EnsureCanManageEnvironmentUpdatesAsync(projectId);

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    [Fact]
    public async Task On_a_private_project_the_flag_holder_on_its_team_both_sees_and_operates()
    {
        var projectId = await SeedProjectAsync();
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await GrantUpdateFlagAsync(teamId, TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.Private, teamId);

        ActAs(TeamMemberUserId);
        await using var ctx = _db.NewContext();
        (await Access(ctx).CanViewAsync(projectId)).Should().BeTrue();
        (await Access(ctx).CanManageEnvironmentUpdatesAsync(projectId)).Should().BeTrue();
    }

    [Fact]
    public async Task On_a_private_project_a_flag_holder_from_elsewhere_neither_sees_nor_operates()
    {
        var projectId = await SeedProjectAsync();
        var assigned = await SeedTeamAsync("Nordics", PlainUserId);
        var elsewhere = await SeedTeamAsync("Benelux", TeamMemberUserId);
        await GrantUpdateFlagAsync(elsewhere, TeamMemberUserId);
        await SetAccessAsOwnerAsync(projectId, ProjectVisibility.Private, assigned);

        ActAs(TeamMemberUserId);
        await using var ctx = _db.NewContext();
        (await Access(ctx).CanViewAsync(projectId)).Should().BeFalse();
        (await Access(ctx).CanManageEnvironmentUpdatesAsync(projectId)).Should().BeFalse();
    }

    [Fact]
    public async Task CanUseEnvironmentOps_is_true_for_a_flag_holder_and_for_admins()
    {
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);
        await GrantUpdateFlagAsync(teamId, TeamMemberUserId);

        ActAs(TeamMemberUserId);
        await using (var ctx = _db.NewContext())
        {
            var snapshot = await Access(ctx).GetSnapshotAsync();
            snapshot.UpdateOpsTeamIds.Should().Equal(teamId);
            snapshot.CanUseEnvironmentOps.Should().BeTrue();
        }

        ActAs(AdminUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Access(ctx).GetSnapshotAsync()).CanUseEnvironmentOps.Should().BeTrue();
        }

        ActAs(PlainUserId, siteAdmin: true);
        await using (var ctx = _db.NewContext())
        {
            (await Access(ctx).GetSnapshotAsync()).CanUseEnvironmentOps.Should().BeTrue();
        }
    }

    [Fact]
    public async Task CanUseEnvironmentOps_is_false_for_a_member_without_the_flag_and_for_nobody()
    {
        var teamId = await SeedTeamAsync("Nordics", TeamMemberUserId);

        ActAs(TeamMemberUserId);
        await using (var ctx = _db.NewContext())
        {
            var snapshot = await Access(ctx).GetSnapshotAsync();
            snapshot.TeamIds.Should().Equal(teamId);
            snapshot.UpdateOpsTeamIds.Should().BeEmpty();
            snapshot.CanUseEnvironmentOps.Should().BeFalse();
        }

        ActAs(null);
        await using (var ctx = _db.NewContext())
        {
            (await Access(ctx).GetSnapshotAsync()).CanUseEnvironmentOps.Should().BeFalse();
        }
    }
}
