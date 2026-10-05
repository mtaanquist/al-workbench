using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Import;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Coordinates starting a project build: create the <c>ingesting</c> project
/// Release row synchronously (so it shows in the list immediately) and enqueue a
/// <see cref="ReleaseImportSource.ProjectBuild"/> job for the worker to clone /
/// compile / ingest off-thread. Mirrors <see cref="ArtifactReleaseImporter"/>; the
/// heavy lifting lives in <see cref="ProjectBuildService"/>, run by
/// <see cref="ReleaseImportWorker"/>.
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
    private readonly ReleaseImportService _importer;
    private readonly ReleaseImportQueue _queue;
    private readonly PersistedImportJobs _persistedJobs;
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly CloneCredentialResolver _credentials;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectBuildImporter> _logger;

    public ProjectBuildImporter(
        ReleaseImportService importer,
        ReleaseImportQueue queue,
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
    /// when the pipeline/project is gone (or the project has no repositories) so the
    /// trigger UI can show the reason inline.
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

    private async Task<int> StartPipelineBuildAsync(int pipelineId, string bcTarget, string trigger, CancellationToken ct)
    {
        var pipeline = await _db.OePipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId && p.DeletedAt == null)
            .Select(p => new
            {
                p.ProjectId,
                p.RequestedAppIdsJson,
                p.Branch,
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
        // up. The preview check is left to fail its build: it runs unattended, and a
        // refusal here would pause the check rather than report one bad night.
        if (trigger == ProjectBuildTrigger.Manual)
        {
            foreach (var provider in pipeline.Providers)
            {
                if ((await _credentials.ResolveAsync(provider, ct).ConfigureAwait(false)).Count == 0)
                {
                    throw new PlanValidationException(new Dictionary<string, string>
                    {
                        ["Pipeline"] = CloneCredentialResolver.NothingToCloneWith(provider),
                    });
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
            StartedAt = now,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var identity = AmbientOrganizationScope.OrganizationIdentity.FromContext(_orgContext, "queuing a project build");
        var source = new ReleaseImportSource.ProjectBuild(pipeline.ProjectId);
        var jobRowId = await _persistedJobs.CreateAsync(releaseId, identity, source, storeSymbolReference: false, ct).ConfigureAwait(false);
        await _queue.EnqueueAsync(
            new ReleaseImportJob(releaseId, identity, source, StoreSymbolReference: false, jobRowId), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Queued project build for {Project} against {BcTarget} (pipeline {PipelineId}, project {ProjectId}, release {ReleaseId}).",
            pipeline.ProjectName, bcTarget, pipelineId, pipeline.ProjectId, releaseId);
        return releaseId;
    }

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
    /// about a commit nobody is looking at any more. The next push - or GitHub's
    /// own redelivery - is the recovery.</para>
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
            CheckRunId = checkRunId,
            StartedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.OeProjectBuilds.Add(build);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var identity = AmbientOrganizationScope.OrganizationIdentity.FromContext(_orgContext, "queuing a pull-request build");
        var source = new ReleaseImportSource.PullRequestBuild(
            projectId, repositoryId, headSha, installationId, repositoryFullName, pullRequestNumber, forkAuthor);
        await _queue.EnqueueAsync(
            new ReleaseImportJob(releaseId, identity, source, StoreSymbolReference: false, JobRowId: 0), ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Queued pull-request build for {Project} ({Repository}#{Number} at {HeadSha}, release {ReleaseId}, build {BuildId}).",
            project.Name, repositoryFullName, pullRequestNumber, headSha, releaseId, build.Id);
        return (releaseId, build.Id);
    }
}
