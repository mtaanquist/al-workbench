using System.Linq.Expressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Coordinates starting a project build: create the <c>ingesting</c> project
/// Release row synchronously (so it shows in the list immediately) and enqueue a
/// <see cref="ReleaseImportSource.ProjectBuild"/> job for the worker to clone /
/// compile / ingest off-thread. Mirrors <see cref="ArtifactReleaseImporter"/>; the
/// heavy lifting lives in <see cref="ProjectBuildService"/>, run by
/// a <see cref="ProjectBuildWorker"/>.
///
/// <para>
/// The Release starts with a provisional label — <c>"{Project} (building…)"</c> —
/// because the real BC version isn't known until the build reads the repos'
/// <c>app.json</c>. The build service finalises the label once it resolves the
/// target version. Always <c>project</c> kind.
/// </para>
/// </summary>
public sealed class ProjectBuildImporter
{
    /// <summary>Why a build of a disabled pipeline is refused (#1131).</summary>
    public const string DisabledRefusal = "This pipeline is disabled. Enable it to build.";

    private readonly ReleaseImportService _importer;
    private readonly ProjectBuildQueue _queue;
    private readonly PersistedImportJobs _persistedJobs;
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly CloneCredentialResolver _credentials;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectBuildImporter> _logger;

    public ProjectBuildImporter(
        ReleaseImportService importer,
        ProjectBuildQueue queue,
        PersistedImportJobs persistedJobs,
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        CloneCredentialResolver credentials,
        TimeProvider clock,
        ILogger<ProjectBuildImporter> logger)
    {
        _importer = importer;
        _queue = queue;
        _persistedJobs = persistedJobs;
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _credentials = credentials;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Creates an ingesting project Release for the pipeline <paramref name="pipelineId"/>
    /// and queues its build. The build compiles the pipeline's saved extension
    /// selection — copied onto the <see cref="OeProjectBuild"/> row as a run-time
    /// snapshot, so the worker (and a restart-resumed job) compile the same subset
    /// even if the pipeline is later edited. Throws <see cref="PlanValidationException"/>
    /// when the pipeline/project is gone, the project has no repositories, or the
    /// person has nothing to clone one of its repositories with, or a build of the
    /// pipeline is already queued or building, so the trigger UI
    /// can show the reason inline before any build exists.
    /// </summary>
    public Task<int> StartBuildAsync(int pipelineId, CancellationToken ct = default) =>
        StartPipelineBuildAsync(pipelineId, ProjectBuildTarget.Current, ProjectBuildTrigger.Manual, ct);

    /// <summary>
    /// Starts one build of the nightly preview check: the same pipeline build as
    /// <see cref="StartBuildAsync"/>, against <paramref name="bcTarget"/> (next minor
    /// or next major) and marked <see cref="ProjectBuildTrigger.PreviewCheck"/>. The
    /// caller runs it as the person who turned the check on, so the access check and
    /// the clone credential are theirs. See <see cref="PreviewCheckScheduler"/>.
    /// </summary>
    public Task<int> StartPreviewCheckAsync(int pipelineId, string bcTarget, CancellationToken ct = default)
    {
        if (!ProjectBuildTarget.IsPreview(bcTarget))
        {
            throw new ArgumentOutOfRangeException(nameof(bcTarget), bcTarget, "A preview check builds against next minor or next major.");
        }
        return StartPipelineBuildAsync(pipelineId, bcTarget, ProjectBuildTrigger.PreviewCheck, ct);
    }

    /// <summary>
    /// How many builds started by a push may wait for the worker per pipeline. A push
    /// past that does not queue another build: it moves the newest waiting one onto
    /// its own commit instead, so a rebase pushed as a run of pushes, or a burst of
    /// small ones, cannot fill the build queue while the branch's latest state still
    /// gets built.
    /// </summary>
    internal const int MaxWaitingPushBuilds = 5;

    /// <summary>
    /// Queues a build of the pipeline <paramref name="pipelineId"/> because
    /// <paramref name="headSha"/> was pushed to its branch in the solution repository
    /// <paramref name="repositoryId"/>. The caller runs it as the person who turned
    /// building on push on (<see cref="OePipeline.BuildOnPushByUserId"/>), so the
    /// access check and the clone credential are theirs, as for the nightly preview
    /// check.
    ///
    /// <para>Unlike <see cref="StartBuildAsync"/> it is not refused while another build
    /// of the pipeline runs: it waits behind it. The import worker runs one build at a
    /// time in the order they were queued, so pushes are built in the order they
    /// arrived, each at its own commit. Past <see cref="MaxWaitingPushBuilds"/> waiting
    /// builds the newest one is moved onto this commit instead. Returns the release id
    /// of the build that will build this commit.</para>
    /// </summary>
    public Task<int> StartPushBuildAsync(int pipelineId, int repositoryId, string headSha, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        return StartPipelineBuildAsync(pipelineId, ProjectBuildTarget.Current, ProjectBuildTrigger.Push, ct, (repositoryId, headSha));
    }

    /// <summary>
    /// A build that keeps the pipeline's next manual build from starting: queued or
    /// building, against the current version, and its release still ingesting.
    /// <para>The nightly preview check is left out both ways: it does not hold up a
    /// manual build, and it is not held up by one (it starts its two targets back to
    /// back and skips a target whose last check is still running). The release has to
    /// be live because nothing resets a build row whose job was lost (a crash before
    /// it was queued, a release deleted mid-build), while the startup sweep already
    /// fails such a release; trusting the row alone would lock Build for good.</para>
    /// </summary>
    internal static readonly Expression<Func<OeProjectBuild, bool>> BlocksManualBuild = b =>
        (b.Status == ProjectBuildStatus.Queued || b.Status == ProjectBuildStatus.Building)
        && b.BcTarget == ProjectBuildTarget.Current
        && b.Release != null && b.Release.Status == "ingesting";

    /// <summary>
    /// Refuses to run an existing build again (an admin Retry, the symbol-recovery
    /// rebuild) when the person could not have started it with Build: they must be able
    /// to manage its solution, have something to clone each of its repositories with,
    /// and no other build of its pipeline may be running (#1110). The rebuild clones
    /// with the person's own credentials and first wipes what the build holds, so the
    /// refusal has to come before anything is touched.
    /// <para>
    /// Returns an open transaction holding the same pipeline lock a manual build takes,
    /// with the build already marked queued in it (#1119). The caller reopens the release
    /// inside it and commits, so a Build click at the same moment either waits and sees
    /// this build, or got in first and this call refuses. Disposing without committing
    /// undoes the mark.
    /// </para>
    /// </summary>
    /// <param name="projectId">The solution the release was built from, for a release with no build row.</param>
    /// <param name="errorKey">The form field a refusal is shown against.</param>
    public async Task<IDbContextTransaction> BeginRebuildAsync(int releaseId, int projectId, string errorKey, CancellationToken ct = default)
    {
        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ReleaseId == releaseId)
            .Select(b => new { b.Id, b.PipelineId, b.ProjectId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var solutionId = build?.ProjectId ?? projectId;
        var solution = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == solutionId && p.DeletedAt == null)
            .Select(p => new
            {
                p.CreatedByUserId,
                Providers = p.Repositories.Select(r => r.Provider).Distinct().ToList(),
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        await _access.EnsureCanManageAsync(solutionId, solution?.CreatedByUserId, ct).ConfigureAwait(false);

        if (solution is null || solution.Providers.Count == 0)
        {
            throw Refuse(solution is null
                ? "This solution no longer exists."
                : "Add at least one repository to this solution before building.");
        }

        var missing = new List<string>();
        foreach (var provider in solution.Providers.OrderBy(p => p))
        {
            if ((await _credentials.ResolveAsync(provider, ct).ConfigureAwait(false)).Count == 0)
            {
                missing.Add(CloneCredentialResolver.NothingToCloneWith(provider));
            }
        }
        if (missing.Count > 0) throw Refuse(string.Join(" ", missing));

        // A rebuild is a build by hand, so a disabled pipeline refuses it like Build does (#1131).
        if (build is { PipelineId: { } disabledCheckId }
            && await _db.OePipelines.AsNoTracking()
                .AnyAsync(p => p.Id == disabledCheckId && p.DisabledAt != null, ct).ConfigureAwait(false))
        {
            throw Refuse(DisabledRefusal);
        }

        var tx = build is { PipelineId: { } lockedPipelineId }
            ? await LockPipelineForManualBuildAsync(lockedPipelineId, ct).ConfigureAwait(false)
            : await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            if (build is { PipelineId: { } pipelineId }
                && await _db.OeProjectBuilds.AsNoTracking()
                    .Where(b => b.PipelineId == pipelineId && b.Id != build.Id)
                    .AnyAsync(BlocksManualBuild, ct)
                    .ConfigureAwait(false))
            {
                throw Refuse("Another build of this pipeline is running. Wait for it to finish, then try again.");
            }

            // Whatever the pipeline, one job at a time per release: a second Retry, or a
            // maintenance job on the same release, would otherwise run beside this one now
            // that builds and imports have workers of their own (#1137).
            await _db.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock({RebuildLockClass}, {releaseId})", ct).ConfigureAwait(false);
            if (await _db.OeImportJobs.AsNoTracking()
                    .AnyAsync(j => j.ReleaseId == releaseId && (j.Status == "queued" || j.Status == "running"), ct)
                    .ConfigureAwait(false))
            {
                throw Refuse("This build is already being worked on. Wait for that to finish, then try again.");
            }

            if (build is not null)
            {
                await _db.OeProjectBuilds
                    .Where(b => b.Id == build.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, ProjectBuildStatus.Queued), ct)
                    .ConfigureAwait(false);
            }
            return tx;
        }
        catch
        {
            await tx.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        PlanValidationException Refuse(string message) =>
            new(new Dictionary<string, string> { [errorKey] = message });
    }

    /// <summary>The advisory-lock namespace for manual builds, keyed per pipeline id ("PBLD").</summary>
    private const int ManualBuildLockClass = 0x50_42_4C_44;

    // "PBRL": one rebuild or maintenance job per release at a time.
    private const int RebuildLockClass = 0x50_42_52_4C;

    /// <summary>
    /// Opens a transaction holding a lock on <paramref name="pipelineId"/> that a second
    /// manual build of the same pipeline waits for, so its running check sees the first
    /// one's build. Released when the transaction commits or is disposed.
    /// </summary>
    private async Task<IDbContextTransaction> LockPipelineForManualBuildAsync(
        int pipelineId, CancellationToken ct)
    {
        var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await _db.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock({ManualBuildLockClass}, {pipelineId})", ct).ConfigureAwait(false);
            return tx;
        }
        catch
        {
            await tx.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<int> StartPipelineBuildAsync(
        int pipelineId, string bcTarget, string trigger, CancellationToken ct, (int RepositoryId, string Sha)? head = null)
    {
        var pipeline = await _db.OePipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId && p.DeletedAt == null)
            .Select(p => new
            {
                p.ProjectId,
                p.RequestedAppIdsJson,
                p.Branch,
                p.DisabledAt,
                ProjectName = p.Project!.Name,
                OwnerId = p.Project.CreatedByUserId,
                RepoCount = p.Project.Repositories.Count,
                Providers = p.Project.Repositories.Select(r => r.Provider).Distinct().ToList(),
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Pipeline"] = "This pipeline no longer exists.",
            });

        // Only the owner or an org Admin may trigger a build. See .design/artifacts.md.
        await _access.EnsureCanManageAsync(pipeline.ProjectId, pipeline.OwnerId, ct).ConfigureAwait(false);

        // A disabled pipeline builds nothing, whoever or whatever asks (#1131).
        if (pipeline.DisabledAt is not null)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Pipeline"] = DisabledRefusal,
            });
        }

        if (pipeline.RepoCount == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Pipeline"] = "Add at least one repository to this project before building.",
            });
        }

        // A manual build clones as the person who pressed Build. Without a credential
        // for one of the repositories it would be skipped and the build would fail
        // with nothing to compile, so refuse before a build exists and say what to set
        // up. A build on push is refused the same way, and the refusal pauses building
        // on push with the reason on the pipeline. The preview check is left to fail
        // its build: it runs unattended, and a refusal here would pause the check
        // rather than report one bad night.
        if (trigger is ProjectBuildTrigger.Manual or ProjectBuildTrigger.Push)
        {
            // Every missing host at once, so fixing one doesn't reveal the next.
            var missing = new List<string>();
            foreach (var provider in pipeline.Providers.OrderBy(p => p))
            {
                if ((await _credentials.ResolveAsync(provider, ct).ConfigureAwait(false)).Count == 0)
                {
                    missing.Add(trigger == ProjectBuildTrigger.Push
                        ? NothingToCloneWithOnPush(provider)
                        : CloneCredentialResolver.NothingToCloneWith(provider));
                }
            }
            if (missing.Count > 0)
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["Pipeline"] = string.Join(" ", missing),
                });
            }
        }

        // One manual build at a time per pipeline. The page disables Build while one
        // is running, but its state can be stale (another tab, another person, the
        // list page), so the refusal lives here too. Two clicks at once would both pass
        // a plain read, so a manual build checks and inserts under a lock on its
        // pipeline, held until the build row is committed (#1119).
        await using var manualBuildLock = trigger == ProjectBuildTrigger.Manual
            ? await LockPipelineForManualBuildAsync(pipelineId, ct).ConfigureAwait(false)
            : null;
        if (trigger == ProjectBuildTrigger.Manual
            && await _db.OeProjectBuilds.AsNoTracking()
                .Where(b => b.PipelineId == pipelineId)
                .AnyAsync(BlocksManualBuild, ct)
                .ConfigureAwait(false))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Pipeline"] = "A build of this pipeline is already running. Wait for it to finish before starting another.",
            });
        }

        // A push past the waiting limit rides on the newest waiting build rather than
        // adding one. Only a build still queued can move: one the worker has picked up
        // has already cloned. The update is conditional on that, so a build that starts
        // between the read and the write is left alone and this push queues its own.
        // Only builds queued as the same person count: one queued as someone who has
        // since handed the automatic builds over is refused when it starts (#1112), and
        // must not take this push down with it.
        if (trigger == ProjectBuildTrigger.Push && head is { } pushed)
        {
            var actingUserId = _orgContext.CurrentUserId;
            var waiting = _db.OeProjectBuilds
                .Where(b => b.PipelineId == pipelineId
                    && b.StartedByUserId == actingUserId
                    && b.Trigger == ProjectBuildTrigger.Push
                    && b.Status == ProjectBuildStatus.Queued
                    && b.Release != null && b.Release.Status == "ingesting");
            if (await waiting.CountAsync(ct).ConfigureAwait(false) >= MaxWaitingPushBuilds)
            {
                var newest = await waiting.AsNoTracking()
                    .OrderByDescending(b => b.Id)
                    .Select(b => new { b.Id, b.ReleaseId })
                    .FirstAsync(ct).ConfigureAwait(false);
                var moved = await _db.OeProjectBuilds
                    .Where(b => b.Id == newest.Id && b.Status == ProjectBuildStatus.Queued)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(b => b.HeadSha, pushed.Sha)
                        .SetProperty(b => b.HeadRepositoryId, pushed.RepositoryId), ct)
                    .ConfigureAwait(false);
                if (moved == 1)
                {
                    _logger.LogInformation(
                        "Pipeline {PipelineId} already has {Count} builds waiting; moved build {BuildId} onto pushed commit {HeadSha} instead of queuing another.",
                        pipelineId, MaxWaitingPushBuilds, newest.Id, pushed.Sha);
                    return newest.ReleaseId!.Value;
                }
            }
        }

        // Clean provisional label — just the project name. The build state shows
        // in the release's Status column ("Building…"), not the label, and
        // ProjectBuildService rewrites this to "{Project} on BC {Major}.{Minor}"
        // once the target version is known. Project-kind labels aren't unique
        // (the release id is their identity), so a concurrent rebuild doesn't
        // collide. See .design/object-explorer-project-builds.md.
        var metadata = new ReleaseImportMetadata(
            Label: pipeline.ProjectName,
            Kind: "project",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            ProjectName: pipeline.ProjectName);
        var releaseId = await _importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);

        // The first-class build row, linked to its pipeline and the release it
        // produces (the Object Explorer hook). The worker flips its status
        // building -> ready/failed and fills the commit set, changelog, logs, and
        // deliverables. The selection is snapshotted from the pipeline so editing
        // the pipeline later doesn't rewrite this build's history. See
        // .design/artifacts.md.
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope when queuing a project build.");
        var now = DateTime.UtcNow;
        _db.OeProjectBuilds.Add(new OeProjectBuild
        {
            OrganizationId = orgId,
            ProjectId = pipeline.ProjectId,
            PipelineId = pipelineId,
            StartedByUserId = _orgContext.CurrentUserId,
            ReleaseId = releaseId,
            Status = ProjectBuildStatus.Queued,
            RequestedAppIdsJson = pipeline.RequestedAppIdsJson,
            // The branch is snapshotted the same way: a restart-resumed job, or a
            // pipeline edited while this build waits, still checks out what was asked.
            Branch = pipeline.Branch,
            // The Business Central version it builds against, which also decides
            // whether the build is a check-only preview build.
            BcTarget = bcTarget,
            Trigger = trigger,
            // A build on push builds the commit its push named, not wherever the
            // branch is by the time the worker reaches it.
            HeadSha = head?.Sha,
            HeadRepositoryId = head?.RepositoryId,
            StartedAt = now,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var identity = AmbientOrganizationScope.OrganizationIdentity.FromContext(_orgContext, "queuing a project build");
        var source = new ReleaseImportSource.ProjectBuild(pipeline.ProjectId);
        var jobRowId = await _persistedJobs.CreateAsync(releaseId, identity, source, storeSymbolReference: false, ct).ConfigureAwait(false);
        if (manualBuildLock is not null) await manualBuildLock.CommitAsync(ct).ConfigureAwait(false);
        _queue.Enqueue(new ReleaseImportJob(
            releaseId, identity, source, StoreSymbolReference: false, jobRowId,
            ProjectBuildOrder.For(pipelineId, bcTarget, trigger)));

        _logger.LogInformation(
            "Queued project build for {Project} against {BcTarget} (pipeline {PipelineId}, project {ProjectId}, release {ReleaseId}).",
            pipeline.ProjectName, bcTarget, pipelineId, pipeline.ProjectId, releaseId);
        return releaseId;
    }

    /// <summary>
    /// Queues an existing build to run again in place (Retry, Recover symbols), after
    /// the caller has reopened its release. It goes in line as a build somebody is
    /// waiting on, behind any build of the same pipeline and target still running.
    /// </summary>
    public async Task QueueRebuildAsync(int releaseId, int projectId, CancellationToken ct = default)
    {
        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ReleaseId == releaseId)
            .Select(b => new { b.PipelineId, b.BcTarget })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var identity = AmbientOrganizationScope.OrganizationIdentity.FromContext(_orgContext, "queuing a rebuild");
        var source = new ReleaseImportSource.ProjectBuild(projectId);
        var jobRowId = await _persistedJobs.CreateAsync(releaseId, identity, source, storeSymbolReference: false, ct).ConfigureAwait(false);
        _queue.Enqueue(new ReleaseImportJob(
            releaseId, identity, source, StoreSymbolReference: false, jobRowId,
            ProjectBuildOrder.For(build?.PipelineId, build?.BcTarget, ProjectBuildTrigger.Manual)));
        _logger.LogInformation("Queued a rebuild of release {ReleaseId} (project {ProjectId}, pipeline {PipelineId}).",
            releaseId, projectId, build?.PipelineId);
    }

    /// <summary>
    /// Why a build on push could not clone, in words for the pipeline page, where
    /// people other than the one it runs as read it.
    /// </summary>
    internal static string NothingToCloneWithOnPush(RepositoryProvider provider) => provider == RepositoryProvider.GitHub
        ? "the person it runs as has no GitHub account connected and no GitHub token under Account → Repository access."
        : $"the person it runs as has no {provider.DisplayName()} token under Account → Repository access.";

    /// <summary>
    /// Creates an ingesting project Release for a pull-request build and queues
    /// it. Returns the release id and the build row's id, the latter so the caller
    /// can tie the GitHub check run it opened to the build that will complete it.
    ///
    /// <para>The differences from <see cref="StartBuildAsync"/> are all
    /// consequences of there being <em>no user and no pipeline</em>: no access
    /// check (GitHub's signed delivery plus the organisation having tracked this
    /// repository is the authority - see
    /// <c>.design/github-integration-phase2.md</c>), no
    /// <see cref="OeProjectBuild.StartedByUserId"/>, no
    /// <see cref="OeProjectBuild.PipelineId"/>, and no selection, so every extension
    /// the repositories hold is compiled. The clone credential is the
    /// installation token, which the worker resolves.</para>
    ///
    /// <para>Nor is a durable <c>oe_import_jobs</c> row written. A pull-request
    /// build is deliberately not resumed across a restart: by the time the process
    /// is back the head may have moved, and re-running would complete a check run
    /// about a commit nobody is looking at any more. After a restart
    /// <see cref="GitHub.GitHubWebhookRecoveryScheduler"/> fails the build and
    /// closes its check run, and the next push to the pull request is the recovery
    /// (#1121).</para>
    /// </summary>
    public async Task<(int ReleaseId, int BuildId)> StartPullRequestBuildAsync(
        int projectId,
        int repositoryId,
        string repositoryFullName,
        long installationId,
        string headSha,
        string headRef,
        int pullRequestNumber,
        long? checkRunId,
        string? forkAuthor = null,
        CancellationToken ct = default)
    {
        var project = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Select(p => new { p.Id, p.Name })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Project"] = "This solution no longer exists.",
            });

        var metadata = new ReleaseImportMetadata(
            Label: project.Name,
            Kind: "project",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            ProjectName: project.Name);
        var releaseId = await _importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);

        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope when queuing a pull-request build.");
        var build = new OeProjectBuild
        {
            OrganizationId = orgId,
            ProjectId = projectId,
            PipelineId = null,
            StartedByUserId = null,
            ReleaseId = releaseId,
            Status = ProjectBuildStatus.Queued,
            RequestedAppIdsJson = null,
            Trigger = ProjectBuildTrigger.PullRequest,
            Branch = headRef,
            PullRequestNumber = pullRequestNumber,
            HeadSha = headSha,
            // Which of the solution's repositories the pull request is on, so a
            // check run this build leaves open over a restart can be closed (#1121).
            HeadRepositoryId = repositoryId,
            CheckRunId = checkRunId,
            StartedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.OeProjectBuilds.Add(build);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var identity = AmbientOrganizationScope.OrganizationIdentity.FromContext(_orgContext, "queuing a pull-request build");
        var source = new ReleaseImportSource.PullRequestBuild(
            projectId, repositoryId, headSha, installationId, repositoryFullName, pullRequestNumber, forkAuthor);
        _queue.Enqueue(new ReleaseImportJob(
            releaseId, identity, source, StoreSymbolReference: false, JobRowId: 0,
            ProjectBuildOrder.For(pipelineId: null, ProjectBuildTarget.Current, ProjectBuildTrigger.PullRequest)));

        _logger.LogInformation(
            "Queued pull-request build for {Project} ({Repository}#{Number} at {HeadSha}, release {ReleaseId}, build {BuildId}).",
            project.Name, repositoryFullName, pullRequestNumber, headSha, releaseId, build.Id);
        return (releaseId, build.Id);
    }
}
