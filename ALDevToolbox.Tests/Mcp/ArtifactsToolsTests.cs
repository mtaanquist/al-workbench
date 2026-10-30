using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
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
            new ProjectService(ctx, _db.OrgContext, access, discovery, _db.NewOfflineGitHubAccessService(ctx), _db.NewRepositoryReadProbe(ctx), NullLogger<ProjectService>.Instance));
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

        (await tools.ListProjectsAsync()).Rows.Should().ContainSingle(p => p.Name == "CRONUS A/S");
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
        detail.FailedApps.Should().BeEmpty("a clean build reports no failed extensions");
        detail.ErrorCount.Should().Be(0);
        detail.Errors.Should().BeEmpty();
    }

    /// <summary>
    /// A build goes ready with some extensions missing, so the tool has to say which
    /// ones and why, or an agent reports a partial build as a clean one (#1330).
    /// </summary>
    [Fact]
    public async Task Get_project_build_reports_the_extensions_and_errors_of_a_partial_build()
    {
        int buildId;
        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS A/S");
            var releaseId = await SeedReleaseAsync(ctx);
            buildId = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow, bcVersion: "26.0", artifacts: 1, releaseId: releaseId);
            ctx.OeProjectBuildResults.AddRange(
                Result(releaseId, "CRONUS Core", ProjectBuildResultStatus.Ingested, null),
                Result(releaseId, "CRONUS Banking", ProjectBuildResultStatus.Failed, "Compilation failed (see the build report for CRONUS Banking)."));
            for (var i = 0; i < MaxErrorsPlusTwo; i++)
            {
                ctx.OeProjectBuildDiagnostics.Add(Diagnostic(buildId, ProjectBuildDiagnosticSeverity.Error, i));
            }
            ctx.OeProjectBuildDiagnostics.Add(Diagnostic(buildId, ProjectBuildDiagnosticSeverity.Warning, MaxErrorsPlusTwo));
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var detail = await NewTools(read).GetProjectBuildAsync(buildId);

        detail.Status.Should().Be(ProjectBuildStatus.Ready);
        var failed = detail.FailedApps.Should().ContainSingle().Subject;
        failed.AppName.Should().Be("CRONUS Banking");
        failed.Message.Should().Be("Compilation failed (see the build report for CRONUS Banking).");
        detail.ErrorCount.Should().Be(MaxErrorsPlusTwo);
        detail.WarningCount.Should().Be(1);
        detail.Errors.Should().HaveCount(ArtifactsTools.MaxBuildErrors, "the rest are counted, not listed");
        detail.Errors![0].Should().Be(new BuildErrorRow(null, "src/Banking0.al", 1, 5, "AL0118", "Error 0"));
        detail.Errors.Select(e => e.Message).Should().Equal(
            Enumerable.Range(0, ArtifactsTools.MaxBuildErrors).Select(i => $"Error {i}"), "in the order the compiler reported them");

        static OeProjectBuildResult Result(int releaseId, string app, string status, string? message) => new()
        {
            OrganizationId = TestDb.DefaultOrgId, ReleaseId = releaseId, AppName = app, AppId = Guid.NewGuid().ToString(),
            Status = status, Message = message, CreatedAt = DateTime.UtcNow,
        };

        static OeProjectBuildDiagnostic Diagnostic(int buildId, string severity, int i) => new()
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId, Path = $"src/Banking{i}.al",
            Line = i + 1, Column = 5, Severity = severity, Code = "AL0118",
            Message = severity == ProjectBuildDiagnosticSeverity.Error ? $"Error {i}" : "Unused variable", Ordering = i,
        };
    }

    private const int MaxErrorsPlusTwo = ArtifactsTools.MaxBuildErrors + 2;

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
        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("same solution");
    }

    [Fact]
    public async Task Compare_project_builds_rejects_a_preview_check_that_indexed_nothing()
    {
        int b1, b2;
        await using (var ctx = _db.NewContext())
        {
            var p = await SeedProjectAsync(ctx, "CRONUS A/S");
            b1 = await SeedBuildAsync(ctx, p, ProjectBuildStatus.Ready, DateTime.UtcNow, releaseId: await SeedReleaseAsync(ctx));
            b2 = await SeedBuildAsync(ctx, p, ProjectBuildStatus.Ready, DateTime.UtcNow, releaseId: await SeedReleaseAsync(ctx));
            (await ctx.OeProjectBuilds.SingleAsync(b => b.Id == b2)).BcTarget = ProjectBuildTarget.NextMajor;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var act = () => NewTools(read).CompareProjectBuildsAsync(b1, b2);
        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("preview check");
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

    [Fact]
    public async Task List_solutions_stops_at_the_limit_and_says_how_to_narrow_it()
    {
        await using (var ctx = _db.NewContext())
        {
            foreach (var name in new[] { "CRONUS A/S", "CRONUS Danmark", "CRONUS Sverige" })
            {
                await SeedProjectAsync(ctx, name);
            }
        }

        await using var read = _db.NewContext();
        var tools = NewTools(read);

        var cut = await tools.ListProjectsAsync(limit: 2);
        cut.Rows.Select(r => r.Name).Should().Equal("CRONUS A/S", "CRONUS Danmark");
        cut.Truncated.Should().BeTrue();
        cut.Note.Should().Contain("search");

        var all = await tools.ListProjectsAsync();
        all.Rows.Should().HaveCount(3);
        all.Truncated.Should().BeFalse();
        all.Note.Should().BeNull();
    }

    [Fact]
    public void A_long_changelog_keeps_the_first_commits_and_counts_the_rest()
    {
        var commits = Enumerable.Range(1, ArtifactsTools.MaxChangelogCommits + 5)
            .Select(i => new ChangelogRow($"abc{i:D4}", $"Commit {i}", "CRONUS dev", null))
            .ToList();
        var groups = new List<ChangelogGroup> { new("cronus-core", commits), new("cronus-small", commits.Take(2).ToList()) };

        var capped = ArtifactsTools.CapChangelog(groups);

        capped[0].Commits.Should().HaveCount(ArtifactsTools.MaxChangelogCommits + 1);
        capped[0].Commits[^1].ShortHash.Should().BeEmpty("the summary row carries no hash, like the service's own notes");
        capped[0].Commits[^1].Message.Should().StartWith("5 more commits");
        capped[1].Commits.Should().HaveCount(2, "a short changelog is left alone");
    }

    [Fact]
    public void A_changelog_the_service_already_cut_keeps_its_note_and_counts_only_commits()
    {
        var commits = Enumerable.Range(1, 100)
            .Select(i => new ChangelogRow($"abc{i:D4}", $"Commit {i}", "CRONUS dev", null))
            .Append(new ChangelogRow(string.Empty, "...and more commits not shown (the changelog is capped at 100)", string.Empty, null))
            .ToList();

        var capped = ArtifactsTools.CapChangelog([new ChangelogGroup("cronus-core", commits)]);

        var rows = capped[0].Commits;
        rows.Should().HaveCount(ArtifactsTools.MaxChangelogCommits + 2);
        rows[ArtifactsTools.MaxChangelogCommits].Message.Should().StartWith("At least 80 more commits");
        rows[^1].Message.Should().Contain("capped at 100", "the service's own note is kept");
    }

    [Fact]
    public void A_list_row_failure_message_is_cut_and_says_where_the_rest_is()
    {
        McpListLimit.ShortenFailure(null, "get_solution_build").Should().BeNull();
        McpListLimit.ShortenFailure("Short failure.", "get_solution_build").Should().Be("Short failure.");

        var cut = McpListLimit.ShortenFailure(new string('x', 1000), "get_solution_build");
        cut!.Should().StartWith(new string('x', McpListLimit.MaxListFailureMessage) + "...");
        cut.Should().Contain("get_solution_build");
        cut.Length.Should().BeLessThan(400);
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
            var projects = new ProjectService(ctx, _db.OrgContext, access, discovery, _db.NewOfflineGitHubAccessService(ctx), _db.NewRepositoryReadProbe(ctx), NullLogger<ProjectService>.Instance);
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
            var rows = (await NewTools(read).ListProjectsAsync()).Rows;
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
            (await NewTools(read).CompareProjectBuildsAsync(firstBuild, secondBuild)).Rows.Should().BeEmpty();
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
