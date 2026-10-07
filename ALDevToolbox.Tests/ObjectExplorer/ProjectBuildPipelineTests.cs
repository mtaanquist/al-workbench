using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Integration coverage for the project-build pipeline's durable + report
/// surfaces — the parts reachable without a real <c>git</c>/<c>alc</c>/CDN:
/// the external-process seam contract (<see cref="ProcessRunner"/>), durable
/// resume of a <c>project_build</c> job across a restart
/// (<see cref="PersistedImportJobs"/>), and the per-app build report the manage
/// page renders (<see cref="ObjectExplorerService.GetProjectBuildResultsAsync"/>),
/// including the partial-failure shape (one extension ingested, one failed).
///
/// <para>
/// The clone → resolve-symbols → compile path is driven end to end, with git,
/// <c>alc</c>, the artifact CDN and the symbol feeds faked, by
/// <see cref="ProjectBuildSymbolFeedTests"/>; the real toolchain and ingest are
/// exercised by the staging smoke. The pure build logic is covered by
/// <see cref="ProjectBuildServiceTests"/>.
/// </para>
/// </summary>
public sealed class ProjectBuildPipelineTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    // ── External-process seam (ProcessRunner) ───────────────────────────

    [Fact]
    public async Task ProcessRunner_captures_stdout_and_success_on_exit_zero()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(new ProcessRunRequest("/bin/sh", new[] { "-c", "echo hello" }));

        result.Succeeded.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.StdOut.Trim().Should().Be("hello");
    }

    [Fact]
    public async Task ProcessRunner_captures_exit_code_and_stderr_on_failure()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(new ProcessRunRequest("/bin/sh", new[] { "-c", "echo oops 1>&2; exit 4" }));

        result.Succeeded.Should().BeFalse();
        result.ExitCode.Should().Be(4);
        result.StdErr.Trim().Should().Be("oops");
    }

    [Fact]
    public async Task ProcessRunner_times_out_and_kills_a_stalled_process()
    {
        var runner = new ProcessRunner();

        // A process that would sleep far longer than the timeout — must be killed
        // and reported as a non-zero result, not awaited forever (the discovery /
        // build clone hang fix).
        var result = await runner.RunAsync(new ProcessRunRequest(
            "/bin/sh", new[] { "-c", "sleep 30" }, Timeout: TimeSpan.FromMilliseconds(300)));

        result.Succeeded.Should().BeFalse();
        result.ExitCode.Should().Be(-1);
        result.StdErr.Should().Contain("Timed out");
    }

    [Fact]
    public async Task ProcessRunner_honours_working_directory_and_env()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(new ProcessRunRequest(
            "/bin/sh", new[] { "-c", "echo $OE_TEST_VAR in $(pwd)" },
            WorkingDirectory: "/tmp",
            Environment: new Dictionary<string, string> { ["OE_TEST_VAR"] = "marker" }));

        result.StdOut.Should().Contain("marker in /tmp");
    }

    // ── Durable resume of a project_build job ──────────────────────────

    [Fact]
    public async Task ProjectBuild_job_persists_kind_and_project_id()
    {
        var releaseId = await SeedProjectReleaseAsync(status: "ingesting");

        await using var ctx = _db.NewContext();
        var jobs = new PersistedImportJobs(ctx, TimeProvider.System);

        var rowId = await jobs.CreateAsync(releaseId, Identity(), new ReleaseImportSource.ProjectBuild(77), storeSymbolReference: false);

        await using var verify = _db.NewContext();
        var row = await verify.OeImportJobs.FindAsync(rowId);
        row!.Kind.Should().Be("project_build");
        row.ProjectId.Should().Be(77);
        row.Status.Should().Be("queued");
    }

    [Fact]
    public async Task ReconcileOnStartup_reenqueues_project_build_as_resumable_job()
    {
        var releaseId = await SeedProjectReleaseAsync(status: "ingesting");
        await using (var ctx = _db.NewContext())
        {
            var jobs = new PersistedImportJobs(ctx, TimeProvider.System);
            await jobs.CreateAsync(releaseId, Identity(), new ReleaseImportSource.ProjectBuild(77), storeSymbolReference: false);
        }

        // A fresh context = a fresh process: the reconciler picks up the survivor.
        await using var reconcileCtx = _db.NewContext();
        var reconciler = new PersistedImportJobs(reconcileCtx, TimeProvider.System);
        var resumed = await reconciler.ReconcileOnStartupAsync();

        resumed.Should().ContainSingle();
        var job = resumed[0];
        job.ReleaseId.Should().Be(releaseId);
        job.Source.Should().BeOfType<ReleaseImportSource.ProjectBuild>()
            .Which.ProjectId.Should().Be(77);
    }

    // A resumed build goes back in its place in the build queue: a preview check
    // behind the builds people wait on (#1137).
    [Fact]
    public async Task ReconcileOnStartup_resumes_a_build_in_its_place_in_line()
    {
        var releaseId = await SeedProjectReleaseAsync(status: "ingesting");
        await using (var ctx = _db.NewContext())
        {
            var project = new OeProject
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = "CRONUS " + Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.OeProjects.Add(project);
            await ctx.SaveChangesAsync();
            ctx.OeProjectBuilds.Add(new OeProjectBuild
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = project.Id,
                ReleaseId = releaseId,
                Trigger = ProjectBuildTrigger.PreviewCheck,
                BcTarget = ProjectBuildTarget.NextMinor,
                StartedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
            await new PersistedImportJobs(ctx, TimeProvider.System)
                .CreateAsync(releaseId, Identity(), new ReleaseImportSource.ProjectBuild(project.Id), storeSymbolReference: false);
        }

        await using var reconcileCtx = _db.NewContext();
        var resumed = await new PersistedImportJobs(reconcileCtx, TimeProvider.System).ReconcileOnStartupAsync();

        resumed.Should().ContainSingle().Which.BuildOrder.Should().Be(new ProjectBuildOrder(2, null));
    }

    // ── Build report (manage page surface) ──────────────────────────────

    [Fact]
    public async Task Build_report_lists_failures_first_and_reflects_a_partial_build()
    {
        // The Core+ContiniaExts shape: one extension ingested, one failed —
        // a partial build (release ready, one failed row).
        var commit = new DateTime(2026, 6, 20, 9, 30, 0, DateTimeKind.Utc);
        var releaseId = await SeedProjectReleaseAsync(status: "ready");
        await using (var seed = _db.NewContext())
        {
            seed.OeProjectBuildResults.AddRange(
                Result(releaseId, "Core", ProjectBuildResultStatus.Ingested, null,
                    repoUrl: "https://github.com/acme/core", commitSha: "abc1234def5678", commitDate: commit),
                Result(releaseId, "ContiniaExts", ProjectBuildResultStatus.Failed, "Missing dependency symbols."));
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var svc = new ObjectExplorerService(ctx, new ReferenceQueryService(ctx, new ProjectAccess(ctx, _db.OrgContext), _db.OrgContext, NullLogger<ReferenceQueryService>.Instance),
            new ProjectAccess(ctx, _db.OrgContext),
            NullLogger<ObjectExplorerService>.Instance);

        var report = await svc.GetProjectBuildResultsAsync(releaseId);

        report.Should().HaveCount(2);
        report[0].Status.Should().Be(ProjectBuildResultStatus.Failed, "failures sort first so the admin sees what to fix");
        report[0].AppName.Should().Be("ContiniaExts");
        report[0].Message.Should().Be("Missing dependency symbols.");

        var core = report.Single(r => r.AppName == "Core");
        core.Status.Should().Be(ProjectBuildResultStatus.Ingested);
        core.RepoUrl.Should().Be("https://github.com/acme/core", "build provenance round-trips for the future Artifacts surface");
        core.CommitSha.Should().Be("abc1234def5678");
        core.CommitDate.Should().Be(commit);
    }

    [Fact]
    public async Task Build_report_is_org_scoped()
    {
        // A build report on another org's release must not leak through the
        // query filter.
        int otherReleaseId;
        await using (var seed = _db.NewContext())
        {
            var release = new OeRelease
            {
                OrganizationId = TestDb.OtherOrgId,
                Label = "Other on BC 26.0",
                Kind = "project",
                Status = "ready",
                ImportedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            seed.OeReleases.Add(release);
            await seed.SaveChangesAsync();
            otherReleaseId = release.Id;
            seed.OeProjectBuildResults.Add(Result(otherReleaseId, "Hidden", ProjectBuildResultStatus.Failed, "secret", TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var svc = new ObjectExplorerService(ctx, new ReferenceQueryService(ctx, new ProjectAccess(ctx, _db.OrgContext), _db.OrgContext, NullLogger<ReferenceQueryService>.Instance),
            new ProjectAccess(ctx, _db.OrgContext),
            NullLogger<ObjectExplorerService>.Instance);

        (await svc.GetProjectBuildResultsAsync(otherReleaseId)).Should().BeEmpty("the query filter scopes to the acting org");
    }

    // ── Relaxed label uniqueness (#4) ───────────────────────────────────

    [Fact]
    public async Task Dedup_key_is_unique_per_org_but_labels_may_repeat()
    {
        // The label is display-only now — any kind may repeat it, because a row
        // with no dedup key never collides.
        await using (var ctx = _db.NewContext())
        {
            ctx.OeReleases.AddRange(
                Rel("Business Central 26.0 (DK)", "first_party"),
                Rel("Business Central 26.0 (DK)", "first_party"),
                Rel("Acme on BC 26.0", "project"),
                Rel("Acme on BC 26.0", "project"));
            var act = () => ctx.SaveChangesAsync();
            await act.Should().NotThrowAsync("keyless rows are disambiguated by the release id, not the label");
        }

        // Two active rows sharing a dedup key collide — the daily artifact sweep's
        // race backstop. The labels differ to prove it's the key, not the label.
        await using (var ctx = _db.NewContext())
        {
            ctx.OeReleases.AddRange(
                Rel("Business Central 28.0 (DK)", "first_party", "bc-onprem:28.0:dk"),
                Rel("BC 28.0 Denmark (renamed)", "first_party", "bc-onprem:28.0:dk"));
            var act = () => ctx.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>("the unique index guards the dedup key");
        }
    }

    private static OeRelease Rel(string label, string kind, string? dedupKey = null) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        Label = label,
        Kind = kind,
        DedupKey = dedupKey,
        Status = "ready",
        ImportedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ── Nightly preview check (#994) ────────────────────────────────────

    [Fact]
    public async Task A_pipeline_and_its_build_default_to_no_check_and_the_current_version()
    {
        int pipelineId, buildId;
        await using (var ctx = _db.NewContext())
        {
            var (pipeline, build) = SeedPipelineAndBuild(ctx);
            await ctx.SaveChangesAsync();
            pipelineId = pipeline.Id;
            buildId = build.Id;
        }

        await using var read = _db.NewContext();
        var storedPipeline = await read.OePipelines.SingleAsync(p => p.Id == pipelineId);
        storedPipeline.PreviewCheck.Should().BeFalse();
        storedPipeline.PreviewCheckByUserId.Should().BeNull();
        storedPipeline.PreviewCheckBlocked.Should().BeNull();
        var stored = await read.OeProjectBuilds.SingleAsync(b => b.Id == buildId);
        stored.BcTarget.Should().Be(ProjectBuildTarget.Current);
        stored.Trigger.Should().Be(ProjectBuildTrigger.Manual);
        stored.BcArtifactVersion.Should().BeNull();
    }

    [Fact]
    public async Task Rows_written_before_the_columns_existed_read_as_no_check_and_current()
    {
        int pipelineId, buildId;
        await using (var ctx = _db.NewContext())
        {
            var (pipeline, build) = SeedPipelineAndBuild(ctx);
            pipeline.PreviewCheck = true;
            build.BcTarget = ProjectBuildTarget.NextMajor;
            await ctx.SaveChangesAsync();
            pipelineId = pipeline.Id;
            buildId = build.Id;
        }

        // The migration's column default is what an existing row gets; DEFAULT puts
        // it back the way the ALTER TABLE did.
        await using (var raw = _db.NewContext())
        {
            await raw.Database.ExecuteSqlRawAsync(
                "UPDATE oe_pipelines SET preview_check = DEFAULT WHERE id = {0}", pipelineId);
            await raw.Database.ExecuteSqlRawAsync(
                "UPDATE oe_project_builds SET bc_target = DEFAULT WHERE id = {0}", buildId);
        }

        await using var read = _db.NewContext();
        (await read.OePipelines.SingleAsync(p => p.Id == pipelineId)).PreviewCheck.Should().BeFalse();
        (await read.OeProjectBuilds.SingleAsync(b => b.Id == buildId)).BcTarget.Should().Be(ProjectBuildTarget.Current);
    }

    [Theory]
    [InlineData(ProjectBuildTarget.NextMinor)]
    [InlineData(ProjectBuildTarget.NextMajor)]
    public async Task The_check_settings_and_a_check_build_round_trip(string target)
    {
        int pipelineId, buildId;
        await using (var ctx = _db.NewContext())
        {
            var (pipeline, build) = SeedPipelineAndBuild(ctx);
            pipeline.PreviewCheck = true;
            pipeline.PreviewCheckBlocked = "This solution doesn't have a country code yet.";
            build.BcTarget = target;
            build.Trigger = ProjectBuildTrigger.PreviewCheck;
            build.BcArtifactVersion = "29.0.52914.0";
            await ctx.SaveChangesAsync();
            pipelineId = pipeline.Id;
            buildId = build.Id;
        }

        await using var read = _db.NewContext();
        var storedPipeline = await read.OePipelines.SingleAsync(p => p.Id == pipelineId);
        storedPipeline.PreviewCheck.Should().BeTrue();
        storedPipeline.PreviewCheckBlocked.Should().Be("This solution doesn't have a country code yet.");
        var stored = await read.OeProjectBuilds.SingleAsync(b => b.Id == buildId);
        stored.BcTarget.Should().Be(target);
        stored.Trigger.Should().Be(ProjectBuildTrigger.PreviewCheck);
        stored.BcArtifactVersion.Should().Be("29.0.52914.0");
        ProjectBuildTarget.IsPreview(stored.BcTarget).Should().BeTrue();
    }

    private static (OePipeline Pipeline, OeProjectBuild Build) SeedPipelineAndBuild(Data.AppDbContext ctx)
    {
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS " + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            Project = project,
            Name = "Production",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId,
            Project = project,
            Pipeline = pipeline,
            Status = ProjectBuildStatus.Queued,
            StartedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        ctx.OePipelines.Add(pipeline);
        ctx.OeProjectBuilds.Add(build);
        return (pipeline, build);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static AmbientOrganizationScope.OrganizationIdentity Identity() =>
        new(TestDb.DefaultOrgId, UserId: null, IsSiteAdmin: false, IsSystemOrganization: false);

    private async Task<int> SeedProjectReleaseAsync(string status)
    {
        await using var ctx = _db.NewContext();
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "Acme (building…) " + Guid.NewGuid().ToString("N"),
            Kind = "project",
            Status = status,
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    private static OeProjectBuildResult Result(
        int releaseId, string app, string status, string? message, int orgId = TestDb.DefaultOrgId,
        string? repoUrl = null, string? commitSha = null, DateTime? commitDate = null) =>
        new()
        {
            OrganizationId = orgId,
            ReleaseId = releaseId,
            AppName = app,
            AppId = Guid.NewGuid().ToString(),
            Status = status,
            Message = message,
            RepoUrl = repoUrl,
            CommitSha = commitSha,
            CommitDate = commitDate,
            CreatedAt = DateTime.UtcNow,
        };
}
