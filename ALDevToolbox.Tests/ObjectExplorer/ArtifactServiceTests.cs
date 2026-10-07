using System.Text;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The Artifacts read surface: the project directory with its latest build, build
/// history + detail (commit set, changelog grouped by repo, deliverables, logs),
/// the project-scoped compare picker, and the download byte fetches. All reads are
/// org-scoped by the EF query filter. See .design/artifacts.md.
/// </summary>
public sealed class ArtifactServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ArtifactService Svc(Data.AppDbContext ctx) => new(ctx, new ProjectAccess(ctx, _db.OrgContext));

    [Fact]
    public async Task ListProjectsAsync_summarises_the_latest_build_and_latest_successful()
    {
        int projectId;
        await using (var ctx = _db.NewContext())
        {
            projectId = await SeedProjectAsync(ctx, "CRONUS A/S", shortName: "CRO");
            var pipelineId = await SeedPipelineAsync(ctx, projectId);
            // An older successful build, then a newer failed one.
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), bcVersion: "26.0", artifactCount: 2, pipelineId: pipelineId);
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Failed, new DateTime(2026, 6, 2, 9, 0, 0, DateTimeKind.Utc), pipelineId: pipelineId);
        }

        await using var read = _db.NewContext();
        var rows = await Svc(read).ListProjectsAsync();

        var row = rows.Should().ContainSingle().Subject;
        row.Name.Should().Be("CRONUS A/S");
        // The abbreviation rides along so an agent reading list_solutions sees
        // the same two names a person does. See .design/customer-naming.md.
        row.ShortName.Should().Be("CRO");
        row.Latest!.Status.Should().Be(ProjectBuildStatus.Failed, "the newest build wins the summary");
        row.LatestSuccessfulBuildId.Should().NotBeNull("the older ready build is the Download-all target");
    }

    [Fact]
    public async Task ListProjectsAsync_shows_no_build_status_for_a_solution_without_a_pipeline()
    {
        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS A/S");
            // What a repository attached to a solution with no pipeline collects:
            // pull-request builds the GitHub App started, which can fail for
            // reasons that have nothing to do with the solution (superseded by a
            // newer push, nothing to compile yet). None of them is a pipeline
            // build, so none of them is the row's status.
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Failed, DateTime.UtcNow.AddMinutes(-2));
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow.AddMinutes(-1), artifactCount: 1);
        }

        await using var read = _db.NewContext();
        var row = (await Svc(read).ListProjectsAsync()).Should().ContainSingle().Subject;

        row.Latest.Should().BeNull("a solution without a pipeline has no build status");
        row.LatestSuccessfulBuildId.Should().BeNull("nor anything to download as its build");
    }

    [Fact]
    public async Task ListProjectsAsync_ignores_pull_request_builds_beside_a_pipelines_own()
    {
        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS A/S");
            var pipelineId = await SeedPipelineAsync(ctx, projectId);
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow.AddMinutes(-2), bcVersion: "26.0", pipelineId: pipelineId);
            // Newer, but not the pipeline's: a failed pull-request build must not
            // turn the row red.
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Failed, DateTime.UtcNow.AddMinutes(-1));
        }

        await using var read = _db.NewContext();
        var row = (await Svc(read).ListProjectsAsync()).Should().ContainSingle().Subject;

        row.Latest!.Status.Should().Be(ProjectBuildStatus.Ready);
        row.Latest.BcVersion.Should().Be("26.0");
    }

    [Fact]
    public async Task ListProjectsAsync_includes_the_latest_build_branch_and_representative_commit()
    {
        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS A/S", repoNames: new[] { "core", "trade" });
            var pipelineId = await SeedPipelineAsync(ctx, projectId);
            var buildId = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow, bcVersion: "26.0", branch: "main", pipelineId: pipelineId);
            // Two repos: the cell shows the first by display name ("core"), shortened to 7 chars.
            ctx.OeProjectBuildRepoCommits.AddRange(
                new OeProjectBuildRepoCommit { OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId, RepoUrl = "u", RepoDisplayName = "trade", CommitHash = "9999999bbb" },
                new OeProjectBuildRepoCommit { OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId, RepoUrl = "u", RepoDisplayName = "core", CommitHash = "abc1234def" });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = (await Svc(read).ListProjectsAsync()).Should().ContainSingle().Subject;
        row.Latest!.Branch.Should().Be("main");
        row.Latest.CommitShort.Should().Be("abc1234", "the first repo by display name wins and the hash is shortened to 7 chars");
    }

    [Fact]
    public async Task ListProjectsAsync_filters_by_name_owner_or_repo()
    {
        await using (var ctx = _db.NewContext())
        {
            await SeedProjectAsync(ctx, "CRONUS A/S", repoNames: new[] { "core" });
            await SeedProjectAsync(ctx, "Northwind", repoNames: new[] { "widgets" }, shortName: "NWT");
        }

        await using var read = _db.NewContext();
        // The list shows the short name beside the name, so it is something people type.
        (await Svc(read).ListProjectsAsync("nwt")).Should().ContainSingle(r => r.Name == "Northwind");
        (await Svc(read).ListProjectsAsync("widgets")).Should().ContainSingle(r => r.Name == "Northwind");
        (await Svc(read).ListProjectsAsync("CRONUS")).Should().ContainSingle(r => r.Name == "CRONUS A/S");
    }

    [Fact]
    public async Task GetBuildDetailAsync_groups_changelog_by_repo_and_lists_deliverables()
    {
        int buildId;
        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS A/S", repoNames: new[] { "core" });
            buildId = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow, bcVersion: "26.0", artifactCount: 1);
            var repoId = ctx.OeProjectRepositories.First(r => r.ProjectId == projectId).Id;

            ctx.OeProjectBuildRepoCommits.Add(new OeProjectBuildRepoCommit
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId, ProjectRepositoryId = repoId,
                RepoUrl = "https://github.com/cronus/core", RepoDisplayName = "core", CommitHash = "abc1234",
            });
            ctx.OeProjectBuildCommits.Add(new OeProjectBuildCommit
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId, ProjectRepositoryId = repoId,
                ShortHash = "abc1234", Message = "Fix posting", Author = "Ada", Ordering = 0,
            });
            ctx.OeProjectBuildLogs.Add(new OeProjectBuildLog
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId,
                Section = "core", Content = "Cloned.", Ordering = 0, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var detail = await Svc(read).GetBuildDetailAsync(buildId);

        detail.Should().NotBeNull();
        detail!.Artifacts.Should().ContainSingle();
        detail.RepoCommits.Should().ContainSingle().Which.CommitHash.Should().Be("abc1234");
        detail.Changelog.Should().ContainSingle().Which.RepoName.Should().Be("core");
        detail.Changelog[0].Commits.Should().ContainSingle().Which.Message.Should().Be("Fix posting");
        detail.Logs.Should().ContainSingle();
    }

    /// <summary>
    /// A partial build goes ready, so the extensions that failed have to be on the
    /// build itself or the pipeline page shows a clean build. The rows whose reason
    /// is a dependency the solution can supply are marked so the page can offer the
    /// way to the Symbols tab (#901).
    /// </summary>
    [Fact]
    public async Task GetBuildDetailAsync_lists_the_extensions_that_did_not_build()
    {
        int buildId;
        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS A/S");
            var releaseId = await SeedReleaseAsync(ctx);
            buildId = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow, releaseId: releaseId);
            ctx.OeProjectBuildResults.AddRange(
                Result(releaseId, "CRONUS Core", ProjectBuildResultStatus.Ingested, null),
                Result(releaseId, "CRONUS Continia", ProjectBuildResultStatus.Failed,
                    "Missing dependency: Continia Core by Continia Software, version 12.1.0.0 or later. Looked in ..."),
                Result(releaseId, "CRONUS Banking", ProjectBuildResultStatus.Failed,
                    "Compilation failed (see the build report for CRONUS Banking)."));
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var detail = await Svc(read).GetBuildDetailAsync(buildId);

        detail!.FailedApps.Should().Equal(
            new FailedAppRow("CRONUS Banking", "Compilation failed (see the build report for CRONUS Banking).", NeedsSymbols: false),
            new FailedAppRow("CRONUS Continia",
                "Missing dependency: Continia Core by Continia Software, version 12.1.0.0 or later. Looked in ...", NeedsSymbols: true));

        static OeProjectBuildResult Result(int releaseId, string app, string status, string? message) => new()
        {
            OrganizationId = TestDb.DefaultOrgId, ReleaseId = releaseId, AppName = app, AppId = Guid.NewGuid().ToString(),
            Status = status, Message = message, CreatedAt = DateTime.UtcNow,
        };
    }

    [Fact]
    public async Task ListBuildsAsync_surfaces_each_build_head_commit_and_count()
    {
        int projectId, pipelineId, withCommits, summaryNote;
        await using (var ctx = _db.NewContext())
        {
            projectId = await SeedProjectAsync(ctx, "CRONUS A/S", repoNames: new[] { "core" });
            pipelineId = await SeedPipelineAsync(ctx, projectId);
            var repoId = ctx.OeProjectRepositories.First(r => r.ProjectId == projectId).Id;

            // A build with two changelog commits — the head (Ordering 0) names the row,
            // winning over the pinned commit so hash + message come from the same commit.
            withCommits = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc), pipelineId: pipelineId);
            ctx.OeProjectBuildRepoCommits.Add(new OeProjectBuildRepoCommit
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = withCommits, ProjectRepositoryId = repoId,
                RepoUrl = "u", RepoDisplayName = "core", CommitHash = "pinned00aaa",
            });
            ctx.OeProjectBuildCommits.AddRange(
                new OeProjectBuildCommit { OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = withCommits, ProjectRepositoryId = repoId, ShortHash = "head123", Message = "Add posting-date validation", Author = "Ada", Ordering = 0 },
                new OeProjectBuildCommit { OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = withCommits, ProjectRepositoryId = repoId, ShortHash = "old456", Message = "Earlier change", Author = "Ada", Ordering = 1 });

            // An earlier build with no new commits: only a summary note in the changelog,
            // but it still has a pinned commit (what it was built at) to show as the hash.
            summaryNote = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), pipelineId: pipelineId);
            ctx.OeProjectBuildRepoCommits.Add(new OeProjectBuildRepoCommit
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = summaryNote, ProjectRepositoryId = repoId,
                RepoUrl = "u", RepoDisplayName = "core", CommitHash = "abc1234def",
            });
            ctx.OeProjectBuildCommits.Add(new OeProjectBuildCommit
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = summaryNote, ShortHash = "", Message = "First build", Author = "", Ordering = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var builds = await Svc(read).ListBuildsAsync(pipelineId);

        var head = builds.Single(b => b.Id == withCommits);
        head.HeadCommitShort.Should().Be("head123", "the changelog head commit names the row when there are new commits");
        head.HeadCommitMessage.Should().Be("Add posting-date validation");
        head.CommitCount.Should().Be(2, "so the row can hint at the remaining commits");

        var note = builds.Single(b => b.Id == summaryNote);
        note.HeadCommitShort.Should().Be("abc1234", "with no new commits, the build's pinned commit (shortened) still shows");
        note.HeadCommitMessage.Should().Be("First build", "and the summary note describes the build");
        note.CommitCount.Should().Be(0, "a summary note isn't a real commit");
    }

    [Fact]
    public async Task ListComparableBuildsAsync_only_returns_ready_builds_with_a_release()
    {
        int pipelineId;
        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS A/S");
            pipelineId = await SeedPipelineAsync(ctx, projectId);
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), releaseId: await SeedReleaseAsync(ctx), pipelineId: pipelineId);
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc), releaseId: await SeedReleaseAsync(ctx), pipelineId: pipelineId);
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Failed, new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc), pipelineId: pipelineId);
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc), pipelineId: pipelineId); // ready but no release
        }

        await using var read = _db.NewContext();
        var comparable = await Svc(read).ListComparableBuildsAsync(pipelineId);

        comparable.Should().HaveCount(2, "only ready builds that produced a navigable release can be compared");
        comparable.Should().BeInDescendingOrder(c => c.StartedAt);
    }

    [Fact]
    public async Task Download_fetches_return_bytes_and_a_concatenated_log()
    {
        int buildId, artifactId;
        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS A/S");
            buildId = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow);
            var art = new OeProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId,
                FileName = "CRONUS_Core_1.0.0.0.app", AppName = "Core", AppVersion = "1.0.0.0",
                SizeBytes = 3, Content = new byte[] { 1, 2, 3 }, CreatedAt = DateTime.UtcNow,
            };
            ctx.OeProjectBuildArtifacts.Add(art);
            ctx.OeProjectBuildLogs.Add(new OeProjectBuildLog
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId,
                Section = "Build", Content = "alc ok", Ordering = 0, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
            artifactId = art.Id;
        }

        await using var read = _db.NewContext();
        var svc = Svc(read);

        (await svc.GetArtifactBytesAsync(buildId, artifactId))!.Content.Should().Equal(1, 2, 3);
        (await svc.GetAllArtifactBytesAsync(buildId)).Should().ContainSingle();
        var raw = await svc.GetRawLogAsync(buildId);
        raw.Should().NotBeNull();
        raw!.Content.Should().Contain("alc ok");
    }

    [Fact]
    public async Task Reads_are_scoped_to_the_acting_org()
    {
        int otherBuildId;
        await using (var ctx = _db.NewContext())
        {
            var otherProject = await SeedProjectAsync(ctx, "Other Co", orgId: TestDb.OtherOrgId);
            otherBuildId = await SeedBuildAsync(ctx, otherProject, ProjectBuildStatus.Ready, DateTime.UtcNow, orgId: TestDb.OtherOrgId);
        }

        await using var read = _db.NewContext(); // scoped to DefaultOrg
        (await Svc(read).ListProjectsAsync()).Should().BeEmpty("the other org's project is filtered out");
        (await Svc(read).GetBuildDetailAsync(otherBuildId)).Should().BeNull("the other org's build is filtered out");
    }

    // ── Last shipped to production (.design/solution-customer-info.md) ──

    [Fact]
    public async Task ListProjectsAsync_reads_the_newest_production_delivery_and_both_terminal_successes_count()
    {
        int deployedId, handedOffId, neverId, pipelineForDeployed;
        await using (var ctx = _db.NewContext())
        {
            deployedId = await SeedProjectAsync(ctx, "CRONUS Denmark");
            var build = await SeedBuildAsync(ctx, deployedId, ProjectBuildStatus.Ready, DateTime.UtcNow, pipelineId: await SeedPipelineAsync(ctx, deployedId));
            pipelineForDeployed = await SeedReleasePipelineAsync(ctx, deployedId, "Production");
            await SeedDeliveryAsync(ctx, deployedId, pipelineForDeployed, build, ProjectDeliveryStatus.Deployed, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));
            var newest = await SeedDeliveryAsync(ctx, deployedId, pipelineForDeployed, build, ProjectDeliveryStatus.Deployed, new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc));
            // Newer still, but it failed - a failure never reached the customer.
            await SeedDeliveryAsync(ctx, deployedId, pipelineForDeployed, build, ProjectDeliveryStatus.Failed, new DateTime(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc));

            handedOffId = await SeedProjectAsync(ctx, "CRONUS Sweden");
            var build2 = await SeedBuildAsync(ctx, handedOffId, ProjectBuildStatus.Ready, DateTime.UtcNow, pipelineId: await SeedPipelineAsync(ctx, handedOffId));
            // The environment's type is compared the way Business Central's own spelling varies.
            var rp2 = await SeedReleasePipelineAsync(ctx, handedOffId, " production ");
            await SeedDeliveryAsync(ctx, handedOffId, rp2, build2, ProjectDeliveryStatus.HandedOff, new DateTime(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc));

            neverId = await SeedProjectAsync(ctx, "CRONUS Norway");
        }

        await using var read = _db.NewContext();
        var rows = (await Svc(read).ListProjectsAsync()).ToDictionary(r => r.Id);

        var deployed = rows[deployedId].LastProductionDelivery!;
        deployed.Status.Should().Be(ProjectDeliveryStatus.Deployed);
        deployed.FinishedAt.Should().Be(new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc), "the newest success wins, and a newer failure is not one");
        deployed.ReleasePipelineId.Should().Be(pipelineForDeployed);
        deployed.ReleasePipelineRemoved.Should().BeFalse();

        rows[handedOffId].LastProductionDelivery!.Status.Should().Be(ProjectDeliveryStatus.HandedOff,
            "Business Central accepting the apps for a later window counts as shipped");
        rows[neverId].LastProductionDelivery.Should().BeNull("a solution with no release pipeline has never shipped");
    }

    [Fact]
    public async Task ListProjectsAsync_ignores_a_delivery_to_a_sandbox()
    {
        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS Denmark");
            var build = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow, pipelineId: await SeedPipelineAsync(ctx, projectId));
            var sandbox = await SeedReleasePipelineAsync(ctx, projectId, "Sandbox");
            await SeedDeliveryAsync(ctx, projectId, sandbox, build, ProjectDeliveryStatus.Deployed, DateTime.UtcNow);
        }

        await using var read = _db.NewContext();
        var row = (await Svc(read).ListProjectsAsync()).Should().ContainSingle().Subject;
        row.LastProductionDelivery.Should().BeNull("a sandbox is where it was tried, not where the customer got it");
    }

    [Fact]
    public async Task ListProjectsAsync_still_counts_a_delivery_whose_release_pipeline_was_deleted()
    {
        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS Denmark");
            var build = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow, pipelineId: await SeedPipelineAsync(ctx, projectId));
            var rp = await SeedReleasePipelineAsync(ctx, projectId, "Production");
            await SeedDeliveryAsync(ctx, projectId, rp, build, ProjectDeliveryStatus.Deployed, DateTime.UtcNow);
            var pipeline = await ctx.OeReleasePipelines.SingleAsync(r => r.Id == rp);
            pipeline.DeletedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var shipped = (await Svc(read).ListProjectsAsync()).Should().ContainSingle().Subject.LastProductionDelivery;
        shipped.Should().NotBeNull("the customer still got it");
        shipped!.ReleasePipelineRemoved.Should().BeTrue("so the list does not link to a page that is gone");
    }

    [Fact]
    public async Task ListProjectsAsync_carries_each_solutions_visibility_and_locks_a_private_one_as_private()
    {
        await using (var ctx = _db.NewContext())
        {
            var readOnly = await SeedProjectAsync(ctx, "CRONUS Denmark");
            (await ctx.OeProjects.SingleAsync(p => p.Id == readOnly)).Visibility = ProjectVisibility.ReadOnly;
            await SeedProjectAsync(ctx, "CRONUS Norway");
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var rows = await Svc(read).ListProjectsAsync();
        rows.Single(r => r.Name == "CRONUS Denmark").Visibility.Should().Be(ProjectVisibility.ReadOnly);
        rows.Single(r => r.Name == "CRONUS Norway").Visibility.Should().Be(ProjectVisibility.Public);
        // list_solutions hands the row to an assistant as JSON: a word, not an enum number.
        System.Text.Json.JsonSerializer.Serialize(rows.Single(r => r.Name == "CRONUS Denmark"))
            .Should().Contain("\"Visibility\":\"ReadOnly\"");
    }

    [Fact]
    public async Task ListProjectsAsync_reads_the_list_in_the_same_number_of_commands_whatever_the_number_of_solutions()
    {
        async Task<int> CountFor()
        {
            var counter = new CommandCounter();
            await using var read = _db.NewContext(counter);
            await Svc(read).ListProjectsAsync();
            return counter.Count;
        }

        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS Denmark");
            var build = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow, pipelineId: await SeedPipelineAsync(ctx, projectId));
            await SeedDeliveryAsync(ctx, projectId, await SeedReleasePipelineAsync(ctx, projectId, "Production"), build, ProjectDeliveryStatus.Deployed, DateTime.UtcNow);
        }
        var one = await CountFor();

        await using (var ctx = _db.NewContext())
        {
            foreach (var name in new[] { "CRONUS Norway", "CRONUS Sweden", "CRONUS Finland" })
            {
                var projectId = await SeedProjectAsync(ctx, name);
                var build = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow, pipelineId: await SeedPipelineAsync(ctx, projectId));
                await SeedDeliveryAsync(ctx, projectId, await SeedReleasePipelineAsync(ctx, projectId, "Production"), build, ProjectDeliveryStatus.Deployed, DateTime.UtcNow);
            }
        }
        var four = await CountFor();

        four.Should().Be(one, "the last delivery is one query for every row, not one per row");
    }

    private sealed class CommandCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    // --- Bounded reads (#1138) ---------------------------------------------

    /// <summary>Five builds an hour apart, oldest first: ready, preview, failed, ready, ready.</summary>
    private async Task<(int PipelineId, int OtherPipelineId, int[] Builds)> SeedHistoryAsync()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx, "CRONUS A/S");
        var pipelineId = await SeedPipelineAsync(ctx, projectId);
        var otherPipelineId = await SeedPipelineAsync(ctx, projectId, "Other");
        var start = DateTime.UtcNow.AddDays(-1);
        var ids = new List<int>();
        string[] statuses = [ProjectBuildStatus.Ready, ProjectBuildStatus.Ready, ProjectBuildStatus.Failed, ProjectBuildStatus.Ready, ProjectBuildStatus.Ready];
        for (var i = 0; i < statuses.Length; i++)
        {
            ids.Add(await SeedBuildAsync(ctx, projectId, statuses[i], start.AddHours(i), artifactCount: 1, pipelineId: pipelineId));
        }
        await ctx.OeProjectBuilds.Where(b => b.Id == ids[1])
            .ExecuteUpdateAsync(u => u.SetProperty(b => b.BcTarget, ProjectBuildTarget.NextMajor));
        return (pipelineId, otherPipelineId, ids.ToArray());
    }

    [Fact]
    public async Task ListBuildsAsync_takes_the_newest_and_can_leave_preview_builds_out()
    {
        var (pipelineId, _, ids) = await SeedHistoryAsync();
        await using var read = _db.NewContext();
        var svc = Svc(read);

        (await svc.ListBuildsAsync(pipelineId, limit: 2)).Select(b => b.Id).Should().Equal(ids[4], ids[3]);
        (await svc.ListBuildsAsync(pipelineId, includePreview: false)).Select(b => b.Id).Should().Equal(ids[4], ids[3], ids[2], ids[0]);
        (await svc.ListBuildsAsync(pipelineId)).Should().HaveCount(5);
        (await svc.CountBuildsAsync(pipelineId)).Should().Be(new BuildCounts(5, 1));
    }

    [Fact]
    public async Task ListDeployableBuildsAsync_offers_the_newest_successful_builds_only()
    {
        var (pipelineId, _, ids) = await SeedHistoryAsync();
        await using var read = _db.NewContext();
        var svc = Svc(read);

        (await svc.ListDeployableBuildsAsync(pipelineId)).Select(b => b.Id).Should().Equal(ids[4], ids[3], ids[0]);
        (await svc.ListDeployableBuildsAsync(pipelineId, limit: 1)).Select(b => b.Id).Should().Equal(ids[4]);
        var apps = await svc.ListBuildAppsAsync(pipelineId, [ids[4]]);
        apps.Keys.Should().Equal(ids[4]);
    }

    [Fact]
    public async Task GetPipelineBuildRowAsync_reads_one_build_of_that_pipeline_only()
    {
        var (pipelineId, otherPipelineId, ids) = await SeedHistoryAsync();
        await using var read = _db.NewContext();
        var svc = Svc(read);

        (await svc.GetPipelineBuildRowAsync(pipelineId, ids[0]))!.Id.Should().Be(ids[0]);
        (await svc.GetPipelineBuildRowAsync(otherPipelineId, ids[0])).Should().BeNull();
    }

    [Fact]
    public async Task ListPipelinesAsync_picks_the_newest_build_and_the_newest_successful_one()
    {
        var (pipelineId, otherPipelineId, ids) = await SeedHistoryAsync();
        await using (var ctx = _db.NewContext())
        {
            // A newer failed build, and a still newer preview build that must not count.
            var projectId = await ctx.OePipelines.Where(p => p.Id == pipelineId).Select(p => p.ProjectId).SingleAsync();
            var failed = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Failed, DateTime.UtcNow.AddMinutes(-30), pipelineId: pipelineId);
            var preview = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow.AddMinutes(-10), pipelineId: pipelineId);
            await ctx.OeProjectBuilds.Where(b => b.Id == preview)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.BcTarget, ProjectBuildTarget.NextMinor));
            ids = [.. ids, failed];
        }

        await using var read = _db.NewContext();
        var rows = await Svc(read).ListPipelinesAsync();

        var row = rows.Single(r => r.Id == pipelineId);
        row.Latest!.BuildId.Should().Be(ids[5]);
        row.LatestSuccessfulBuildId.Should().Be(ids[4]);
        var other = rows.Single(r => r.Id == otherPipelineId);
        other.Latest.Should().BeNull();
        other.LatestSuccessfulBuildId.Should().BeNull();
        ArtifactService.MatchesSearch(row, "cronus").Should().BeTrue();
        ArtifactService.MatchesSearch(row, "Fabrikam").Should().BeFalse();
    }

    private static async Task<int> SeedReleasePipelineAsync(Data.AppDbContext ctx, int projectId, string environmentType)
    {
        var environment = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = environmentType.Trim(), Type = environmentType,
            FetchedAt = DateTime.UtcNow,
        };
        ctx.OeProjectEnvironments.Add(environment);
        await ctx.SaveChangesAsync();
        var pipeline = new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = $"To {environment.Name}",
            ProjectEnvironmentId = environment.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleasePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return pipeline.Id;
    }

    private static async Task<int> SeedDeliveryAsync(Data.AppDbContext ctx, int projectId, int releasePipelineId, int buildId, string status, DateTime finishedAt)
    {
        var delivery = new OeProjectDelivery
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, ReleasePipelineId = releasePipelineId, ProjectBuildId = buildId,
            EnvironmentName = "Production", Status = status, ScheduledFor = finishedAt, StartedAt = finishedAt, FinishedAt = finishedAt,
            CreatedAt = finishedAt, UpdatedAt = finishedAt,
        };
        ctx.OeProjectDeliveries.Add(delivery);
        await ctx.SaveChangesAsync();
        return delivery.Id;
    }

    // ── seeding helpers ─────────────────────────────────────────────────

    private static async Task<int> SeedProjectAsync(Data.AppDbContext ctx, string name, string[]? repoNames = null, int orgId = TestDb.DefaultOrgId, string? shortName = null)
    {
        var project = new OeProject
        {
            OrganizationId = orgId, Name = name, ShortName = shortName, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Repositories = (repoNames ?? new[] { "repo" }).Select(n => new OeProjectRepository
            {
                OrganizationId = orgId, Provider = ALDevToolbox.Domain.ValueObjects.RepositoryProvider.GitHub,
                Url = $"https://github.com/x/{n}", DisplayName = n,
            }).ToList(),
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private static async Task<int> SeedReleaseAsync(Data.AppDbContext ctx, int orgId = TestDb.DefaultOrgId)
    {
        var release = new OeRelease
        {
            OrganizationId = orgId, Label = "build", Kind = "project", Status = "ready",
            ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    private static async Task<int> SeedPipelineAsync(Data.AppDbContext ctx, int projectId, string name = "Default", int orgId = TestDb.DefaultOrgId)
    {
        var pipeline = new OePipeline
        {
            OrganizationId = orgId, ProjectId = projectId, Name = name,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return pipeline.Id;
    }

    private static async Task<int> SeedBuildAsync(
        Data.AppDbContext ctx, int projectId, string status, DateTime startedAt,
        string? bcVersion = null, int artifactCount = 0, int? releaseId = null, string? branch = null,
        int? pipelineId = null, int orgId = TestDb.DefaultOrgId)
    {
        var build = new OeProjectBuild
        {
            OrganizationId = orgId, ProjectId = projectId, PipelineId = pipelineId, Status = status, BcVersion = bcVersion, Branch = branch,
            StartedAt = startedAt, FinishedAt = status is ProjectBuildStatus.Ready or ProjectBuildStatus.Failed ? startedAt : null,
            ReleaseId = releaseId,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();

        for (var i = 0; i < artifactCount; i++)
        {
            ctx.OeProjectBuildArtifacts.Add(new OeProjectBuildArtifact
            {
                OrganizationId = orgId, ProjectBuildId = build.Id,
                FileName = $"app{i}.app", AppName = $"App {i}", AppVersion = "1.0.0.0",
                SizeBytes = 1, Content = new byte[] { (byte)i }, CreatedAt = DateTime.UtcNow,
            });
        }
        if (artifactCount > 0) await ctx.SaveChangesAsync();
        return build.Id;
    }
}
