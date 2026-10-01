using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// CRUD over <see cref="OeReleasePipeline"/> — the reusable "where + how" of a deploy
/// that draws a <see cref="OePipeline"/> (build) pipeline's artifacts and targets one
/// <see cref="OeProjectEnvironment"/>. A build pipeline can feed several release
/// pipelines (build-once-deploy-many). Management rights come from the parent
/// project's owner via <see cref="ProjectAccess"/>. Org-scoped via the EF query
/// filter; mutations run inside an authenticated request. Validation throws
/// <see cref="PlanValidationException"/> with field-keyed errors. Deploying a build
/// is <see cref="DeliveryService"/>'s job. See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class ReleasePipelineService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly ILogger<ReleasePipelineService> _logger;

    public ReleasePipelineService(AppDbContext db, IOrganizationContext orgContext, ProjectAccess access, ILogger<ReleasePipelineService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; release-pipeline mutation called outside an authenticated request.");

    /// <summary>
    /// True when the current user may manage <paramref name="releasePipelineId"/> — i.e.
    /// they may manage its parent project (owner or org Admin / SiteAdmin). False when
    /// the deployment pipeline no longer exists.
    /// </summary>
    public async Task<bool> CanManageAsync(int releasePipelineId, CancellationToken ct = default)
    {
        var owner = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.Id == releasePipelineId && r.DeletedAt == null)
            .Select(r => new { r.ProjectId, OwnerId = r.Project!.CreatedByUserId })
            .FirstOrDefaultAsync(ct);
        return owner is not null && await _access.CanManageAsync(owner.ProjectId, owner.OwnerId, ct);
    }

    /// <summary>
    /// Active deployment pipelines for the current org, optionally scoped to one project,
    /// each with its target environment and source build-pipeline name resolved for
    /// display. Ordered by name.
    /// </summary>
    public async Task<List<ReleasePipelineRow>> ListReleasePipelinesAsync(int? projectId = null, CancellationToken ct = default)
    {
        var query = _db.OeReleasePipelines.AsNoTracking().Where(r => r.DeletedAt == null);
        if (projectId is { } pid)
        {
            await _access.EnsureCanViewAsync(pid, ct);
            query = query.Where(r => r.ProjectId == pid);
        }
        else
        {
            // A deployment pipeline inherits its project's visibility.
            var visible = ProjectAccess.VisibleProjectPredicate(await _access.GetSnapshotAsync(ct));
            query = query.Where(r => _db.OeProjects.Where(visible).Any(v => v.Id == r.ProjectId));
        }

        return await query
            .OrderBy(r => r.Name)
            .Select(r => new ReleasePipelineRow(
                r.Id,
                r.ProjectId,
                r.Project!.Name,
                r.Name,
                r.BuildPipelineId,
                r.BuildPipeline != null ? r.BuildPipeline.Name : string.Empty,
                r.ProjectEnvironmentId,
                r.ProjectEnvironment!.Name,
                r.ProjectEnvironment.Type,
                r.ProjectEnvironment.MissingSince != null,
                r.DeploymentSchedule,
                r.SchemaSyncMode,
                r.ArtifactSource,
                r.GithubReleaseRepositoryId,
                r.GithubReleaseRepository != null ? r.GithubReleaseRepository.DisplayName : null,
                r.ProjectEnvironment.Status,
                r.PrepareReleaseOnNewBuild))
            .ToListAsync(ct);
    }

    /// <summary>
    /// <see cref="ListReleasePipelinesAsync"/> for the whole org, with each row's
    /// deliveries summed up for the Deployment pipelines list: the newest finished deployment, the
    /// one running now (with how far through its apps it is, and how long the last
    /// successful one here took), the next one waiting for its time, and Microsoft's
    /// next update for the target environment. A fixed handful of queries for the
    /// whole list, never one per row - the page re-reads this every two seconds
    /// while something is shipping. See <c>.design/saas-delivery.md</c>.
    /// </summary>
    public async Task<List<ReleasePipelineRow>> ListReleasePipelineOverviewAsync(CancellationToken ct = default)
    {
        var rows = await ListReleasePipelinesAsync(null, ct);
        if (rows.Count == 0) return rows;

        var pipelineIds = rows.Select(r => r.Id).ToList();
        var deliveries = _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => pipelineIds.Contains(d.ReleasePipelineId));

        // The newest deployment that has finished, one way or another. Ordered by when it
        // finished rather than by id: a deployment scheduled for tonight is created before
        // one deployed right now, and finishes after it.
        // A dismissed prepared deployment (#934) is left out on purpose: it never was a
        // deployment, and would read as the last one every time a newer build came along.
        var latest = await deliveries
            .Where(d => d.Status == ProjectDeliveryStatus.Deployed
                        || d.Status == ProjectDeliveryStatus.Failed
                        || d.Status == ProjectDeliveryStatus.Cancelled
                        || d.Status == ProjectDeliveryStatus.HandedOff)
            .GroupBy(d => d.ReleasePipelineId)
            .Select(g => g
                .OrderByDescending(d => d.FinishedAt ?? d.UpdatedAt)
                .ThenByDescending(d => d.Id)
                .Select(d => new
                {
                    d.ReleasePipelineId,
                    d.Id,
                    d.Status,
                    At = d.FinishedAt ?? d.UpdatedAt,
                    d.DeploymentSchedule,
                    FailedApp = d.Results
                        .Where(r => r.Status == ProjectDeliveryResultStatus.Failed)
                        .OrderBy(r => r.Ordering)
                        .Select(r => r.AppName)
                        .FirstOrDefault(),
                })
                .First())
            .ToListAsync(ct);

        // What is shipping right now. Few rows by nature, so the per-app states come
        // back whole and are counted here.
        var live = await deliveries
            .Where(d => d.Status == ProjectDeliveryStatus.Claimed
                        || d.Status == ProjectDeliveryStatus.Uploading
                        || d.Status == ProjectDeliveryStatus.Installing)
            .OrderBy(d => d.Id)
            .Select(d => new
            {
                d.ReleasePipelineId,
                d.Id,
                d.Status,
                StartedAt = d.StartedAt ?? d.ClaimedAt,
                Apps = d.Results.OrderBy(r => r.Ordering).Select(r => r.Status).ToList(),
            })
            .ToListAsync(ct);

        // How long the last successful deployment took, for the live band's "the last
        // deployment here took 6 minutes". Only asked for pipelines that are shipping.
        var livePipelineIds = live.Select(l => l.ReleasePipelineId).Distinct().ToList();
        var previous = new Dictionary<int, TimeSpan>();
        if (livePipelineIds.Count > 0)
        {
            var took = await deliveries
                .Where(d => livePipelineIds.Contains(d.ReleasePipelineId)
                            && d.Status == ProjectDeliveryStatus.Deployed
                            && d.StartedAt != null && d.FinishedAt != null)
                .GroupBy(d => d.ReleasePipelineId)
                .Select(g => g
                    .OrderByDescending(d => d.FinishedAt)
                    .Select(d => new { d.ReleasePipelineId, d.StartedAt, d.FinishedAt })
                    .First())
                .ToListAsync(ct);
            foreach (var t in took) previous[t.ReleasePipelineId] = t.FinishedAt!.Value - t.StartedAt!.Value;
        }

        // The next deployment waiting for its time.
        var next = await deliveries
            .Where(d => d.Status == ProjectDeliveryStatus.Scheduled)
            .GroupBy(d => d.ReleasePipelineId)
            .Select(g => g
                .OrderBy(d => d.ScheduledFor)
                .ThenBy(d => d.Id)
                .Select(d => new
                {
                    d.ReleasePipelineId,
                    d.Id,
                    d.ScheduledFor,
                    d.ScheduledOutsideWindow,
                    By = d.TriggeredByUser != null ? d.TriggeredByUser.DisplayName : null,
                })
                .First())
            .ToListAsync(ct);

        // The deployment waiting for approval (#934). A newer build replaces an older one,
        // so there is at most one per pipeline; the newest wins if two ever overlap.
        var proposed = await deliveries
            .Where(d => d.Status == ProjectDeliveryStatus.Proposed)
            .GroupBy(d => d.ReleasePipelineId)
            .Select(g => g
                .OrderByDescending(d => d.Id)
                .Select(d => new { d.ReleasePipelineId, d.Id, d.ProjectBuildId, d.CreatedAt })
                .First())
            .ToListAsync(ct);

        // Microsoft's next update for each target, as last mirrored: a deployment handed
        // to Business Central for "the next update" installs then.
        var environmentIds = rows.Select(r => r.ProjectEnvironmentId).Distinct().ToList();
        var updates = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => environmentIds.Contains(e.Id) && e.BcNextUpdateVersion != null)
            .Select(e => new { e.Id, e.BcNextUpdateDate, Version = e.BcNextUpdateVersion!, e.BcNextUpdateType })
            .ToListAsync(ct);

        var latestBy = latest.ToDictionary(l => l.ReleasePipelineId);
        var liveBy = live.GroupBy(l => l.ReleasePipelineId).ToDictionary(g => g.Key, g => g.First());
        var nextBy = next.ToDictionary(n => n.ReleasePipelineId);
        var proposedBy = proposed.ToDictionary(p => p.ReleasePipelineId);
        var updateBy = updates.ToDictionary(u => u.Id);

        return rows.Select(r => r with
        {
            LastDelivery = latestBy.TryGetValue(r.Id, out var l)
                ? new ReleasePipelineLastDelivery(l.Id, l.Status, l.At, l.FailedApp, l.DeploymentSchedule)
                : null,
            LiveDelivery = liveBy.TryGetValue(r.Id, out var v)
                ? ReleasePipelineLiveDelivery.From(v.Id, v.Status, v.StartedAt, v.Apps,
                    previous.TryGetValue(r.Id, out var took) ? took : null)
                : null,
            NextDelivery = nextBy.TryGetValue(r.Id, out var n)
                ? new ReleasePipelineNextDelivery(n.Id, n.ScheduledFor, n.ScheduledOutsideWindow, n.By)
                : null,
            ProposedDelivery = proposedBy.TryGetValue(r.Id, out var p)
                ? new ReleasePipelineProposedDelivery(p.Id, p.ProjectBuildId, p.CreatedAt)
                : null,
            EnvironmentNextUpdate = updateBy.TryGetValue(r.ProjectEnvironmentId, out var u)
                ? new EnvironmentNextUpdate(u.BcNextUpdateDate, u.Version, u.BcNextUpdateType)
                : null,
        }).ToList();
    }

    /// <summary>
    /// The deployments prepared from a new build and waiting for someone to approve them
    /// (#934), across one solution's active deployment pipelines, by pipeline name.
    /// For the solution page's "1 deployment waiting for approval". Gated on the solution's
    /// visibility like the rest of its reads.
    /// </summary>
    public async Task<List<ReleaseWaitingForApproval>> ListWaitingForApprovalAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        return await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.ProjectId == projectId
                        && d.Status == ProjectDeliveryStatus.Proposed
                        && d.ReleasePipeline!.DeletedAt == null)
            .OrderBy(d => d.ReleasePipeline!.Name).ThenBy(d => d.Id)
            .Select(d => new ReleaseWaitingForApproval(d.ReleasePipelineId, d.ReleasePipeline!.Name, d.Id, d.ProjectBuildId))
            .ToListAsync(ct);
    }

    /// <summary>A single active deployment pipeline, or null when not found in this org.</summary>
    public async Task<OeReleasePipeline?> GetReleasePipelineAsync(int id, CancellationToken ct = default)
    {
        await EnsureCanViewReleasePipelineAsync(id, ct);
        return await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.Id == id && r.DeletedAt == null)
            .Include(r => r.Project)
            .Include(r => r.BuildPipeline)
            .Include(r => r.GithubReleaseRepository)
            .Include(r => r.ProjectEnvironment)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Creates a deployment pipeline under a project. Returns the new id.</summary>
    public async Task<int> CreateReleasePipelineAsync(ReleasePipelineInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var v = await ValidateAsync(input, existingId: null, ct);

        var now = DateTime.UtcNow;
        var pipeline = new OeReleasePipeline
        {
            OrganizationId = orgId,
            ProjectId = input.ProjectId,
            CreatedByUserId = _orgContext.CurrentUserId,
            Name = v.Name,
            ArtifactSource = v.ArtifactSource,
            BuildPipelineId = v.BuildPipelineId,
            GithubReleaseRepositoryId = v.GithubReleaseRepositoryId,
            ProjectEnvironmentId = input.ProjectEnvironmentId,
            DeploymentSchedule = v.DeploymentSchedule,
            SchemaSyncMode = v.SchemaSyncMode,
            PrepareReleaseOnNewBuild = v.PrepareReleaseOnNewBuild,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.OeReleasePipelines.Add(pipeline);
        await SaveTranslatingNameClashAsync(ct);

        _logger.LogInformation("Created deployment pipeline {ReleasePipelineId} ({Name}) for project {ProjectId} → environment {EnvironmentId}.",
            pipeline.Id, v.Name, input.ProjectId, input.ProjectEnvironmentId);
        return pipeline.Id;
    }

    /// <summary>Updates a deployment pipeline's name, source, target, and modes.</summary>
    public async Task UpdateReleasePipelineAsync(int id, ReleasePipelineInput input, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var pipeline = await _db.OeReleasePipelines
            .FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, ct)
            ?? throw Validation("Name", "This deployment pipeline no longer exists.");

        // A deployment pipeline can't move between projects; validate against its own.
        var v = await ValidateAsync(input with { ProjectId = pipeline.ProjectId }, existingId: id, ct);

        pipeline.Name = v.Name;
        pipeline.ArtifactSource = v.ArtifactSource;
        pipeline.BuildPipelineId = v.BuildPipelineId;
        pipeline.GithubReleaseRepositoryId = v.GithubReleaseRepositoryId;
        pipeline.ProjectEnvironmentId = input.ProjectEnvironmentId;
        pipeline.DeploymentSchedule = v.DeploymentSchedule;
        pipeline.SchemaSyncMode = v.SchemaSyncMode;
        pipeline.PrepareReleaseOnNewBuild = v.PrepareReleaseOnNewBuild;
        pipeline.UpdatedAt = DateTime.UtcNow;
        await SaveTranslatingNameClashAsync(ct);
        _logger.LogInformation("Updated deployment pipeline {ReleasePipelineId} ({Name}).", pipeline.Id, v.Name);
    }

    /// <summary>Soft-deletes a deployment pipeline.</summary>
    public async Task SoftDeleteReleasePipelineAsync(int id, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var pipeline = await _db.OeReleasePipelines
            .FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, ct)
            ?? throw Validation("Name", "This deployment pipeline no longer exists.");

        var ownerId = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == pipeline.ProjectId)
            .Select(p => p.CreatedByUserId)
            .FirstOrDefaultAsync(ct);
        await _access.EnsureCanManageAsync(pipeline.ProjectId, ownerId, ct);

        pipeline.DeletedAt = DateTime.UtcNow;
        pipeline.UpdatedAt = pipeline.DeletedAt.Value;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Soft-deleted deployment pipeline {ReleasePipelineId}.", id);
    }

    /// <summary>
    /// Validates the input against its project (which must exist and be manageable),
    /// the per-project name uniqueness rule, the source build pipeline and target
    /// environment (both must belong to the same project, and the environment's status
    /// must not block installs), and the deployment schedule and schema-sync mode. Returns the normalised values. Throws
    /// <see cref="PlanValidationException"/> with field-keyed errors otherwise.
    /// </summary>
    private async Task<ValidatedReleasePipeline> ValidateAsync(
        ReleasePipelineInput input, int? existingId, CancellationToken ct)
    {
        // The parent project must exist in this org and be manageable by the user.
        var owner = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == input.ProjectId && p.DeletedAt == null)
            .Select(p => new { p.CreatedByUserId })
            .FirstOrDefaultAsync(ct);
        if (owner is null)
        {
            throw Validation("Project", "Choose a solution for this deployment pipeline.");
        }
        await _access.EnsureCanManageAsync(input.ProjectId, owner.CreatedByUserId, ct);

        var errors = new Dictionary<string, string>();

        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            errors["Name"] = "Give the deployment pipeline a name.";
        }
        else if (name.Length > 200)
        {
            errors["Name"] = "Keep the name under 200 characters.";
        }
        else
        {
            var clash = await _db.OeReleasePipelines.AsNoTracking()
                .AnyAsync(r => r.DeletedAt == null
                               && r.ProjectId == input.ProjectId
                               && r.Id != (existingId ?? 0)
                               && r.Name.ToLower() == name.ToLower(), ct);
            if (clash)
            {
                errors["Name"] = "Another deployment pipeline in this solution already uses this name.";
            }
        }

        // The artifact source: exactly one of the two, and the one named must belong
        // to this project. A pipeline that named both would leave "what does this
        // deployment install" with two answers.
        var artifactSource = string.IsNullOrWhiteSpace(input.ArtifactSource)
            ? ReleaseArtifactSource.Build
            : input.ArtifactSource;
        int? buildPipelineId = null;
        int? releaseRepositoryId = null;
        if (!ReleaseArtifactSource.IsValid(artifactSource))
        {
            errors["ArtifactSource"] = "Choose where this pipeline's apps come from.";
        }
        else if (artifactSource == ReleaseArtifactSource.Build)
        {
            // Source build pipeline: must be an active pipeline in the same project.
            var buildPipelineOk = input.BuildPipelineId != 0 && await _db.OePipelines.AsNoTracking()
                .AnyAsync(p => p.Id == input.BuildPipelineId
                               && p.DeletedAt == null
                               && p.ProjectId == input.ProjectId, ct);
            if (!buildPipelineOk)
            {
                errors["BuildPipelineId"] = "Choose a build pipeline to deploy from.";
            }
            else
            {
                buildPipelineId = input.BuildPipelineId;
            }
        }
        else
        {
            var repositoryOk = input.GithubReleaseRepositoryId is { } repoId && repoId != 0
                && await _db.OeProjectRepositories.AsNoTracking()
                    .AnyAsync(r => r.Id == repoId
                                   && r.ProjectId == input.ProjectId
                                   && r.Provider == RepositoryProvider.GitHub, ct);
            if (!repositoryOk)
            {
                errors["GithubReleaseRepositoryId"] = "Choose one of this solution's GitHub repositories to deploy from.";
            }
            else
            {
                releaseRepositoryId = input.GithubReleaseRepositoryId;
            }
        }

        // Target environment: must belong to the same project, must still be there, and
        // must be in a state that can take an install. The status is the cached one — the
        // live re-read happens when a delivery actually runs — so this is the same refusal
        // the user would hit later, just earlier and while they can still change it.
        var environment = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == input.ProjectEnvironmentId && e.ProjectId == input.ProjectId)
            .Select(e => new { e.Name, e.Status, Missing = e.MissingSince != null, e.UpdateWindowStart, e.UpdateWindowEnd })
            .FirstOrDefaultAsync(ct);
        if (environment is null)
        {
            errors["ProjectEnvironmentId"] = "Choose a target environment.";
        }
        else if (environment.Missing)
        {
            errors["ProjectEnvironmentId"] =
                $"'{environment.Name}' is no longer present in Business Central. Refresh the environments on the solution's Business Central page, then come back.";
        }
        else if (BcEnvironmentStatus.RefusalMessage(environment.Name, environment.Status) is { } statusRefusal)
        {
            errors["ProjectEnvironmentId"] = statusRefusal;
        }

        // Only the wire values Business Central still accepts pass. A pipeline saved
        // under the retired upload API stores wording this one rejects, so re-saving such
        // a pipeline means picking again rather than silently carrying the old value over.
        var deploymentSchedule = string.IsNullOrWhiteSpace(input.DeploymentSchedule)
            ? BcDeploymentSchedule.Immediate
            : input.DeploymentSchedule;
        if (!BcDeploymentSchedule.Pickable.Contains(deploymentSchedule))
        {
            errors["DeploymentSchedule"] = "Choose when installs should run.";
        }
        else if (BcDeploymentSchedule.IsOurDeliveryWindow(deploymentSchedule)
                 && environment is not null
                 && !UpdateWindow.IsConfigured(environment.UpdateWindowStart, environment.UpdateWindowEnd))
        {
            // The delivery window belongs to the environment, so the choice only means
            // something while that environment has one.
            errors["DeploymentSchedule"] =
                $"'{environment.Name}' has no delivery window yet. Set one on the environment's page, or choose another time for installs.";
        }

        var schemaSyncMode = string.IsNullOrWhiteSpace(input.SchemaSyncMode) ? BcSyncMode.Add : input.SchemaSyncMode;
        if (!BcSyncMode.IsValid(schemaSyncMode))
        {
            errors["SchemaSyncMode"] = "Choose a schema sync setting.";
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        // Preparing a deployment follows a build pipeline's builds; a pipeline that installs
        // GitHub releases has no build to follow, so the setting means nothing there.
        return new ValidatedReleasePipeline(
            name, deploymentSchedule, schemaSyncMode, artifactSource, buildPipelineId, releaseRepositoryId,
            input.PrepareReleaseOnNewBuild && artifactSource == ReleaseArtifactSource.Build);
    }

    /// <summary>The normalised values a validated deployment-pipeline input settles on.</summary>
    private sealed record ValidatedReleasePipeline(
        string Name,
        string DeploymentSchedule,
        string SchemaSyncMode,
        string ArtifactSource,
        int? BuildPipelineId,
        int? GithubReleaseRepositoryId,
        bool PrepareReleaseOnNewBuild);

    /// <summary>
    /// Gates a deployment-pipeline-keyed read on its project's visibility. One that
    /// doesn't exist passes; the read below returns nothing on its own.
    /// </summary>
    private async Task EnsureCanViewReleasePipelineAsync(int releasePipelineId, CancellationToken ct)
    {
        var projectId = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.Id == releasePipelineId)
            .Select(r => (int?)r.ProjectId)
            .FirstOrDefaultAsync(ct);
        if (projectId is { } id) await _access.EnsureCanViewAsync(id, ct);
    }

    /// <summary>
    /// Saves, turning the name-uniqueness backstop into the same field-keyed error
    /// the pre-check gives. The pre-check reads before this writes, so two
    /// concurrent saves can leave one to be caught by the case-insensitive unique
    /// index — and that has to read as an inline message, not a 500. See #702.
    /// </summary>
    private async Task SaveTranslatingNameClashAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw Validation("Name", "Another deployment pipeline in this solution already uses this name.");
        }
    }

    private static PlanValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });

    /// <summary>
    /// Throws a friendly <see cref="McpException"/> when the id isn't an active
    /// deployment pipeline the caller can see, instead of silently returning an
    /// empty history. Two fences: the org query filter, and the owning project's
    /// visibility. A pipeline under a Private project the caller has no grant on
    /// answers "not found", the same as an id in another org. Relocated here
    /// from the MCP tool class so the fence has one home.
    /// See <c>.design/teams-and-visibility.md</c>.
    /// </summary>
    public async Task EnsureReleasePipelineExistsAsync(int releasePipelineId, CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        var exists = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => _db.OeProjects.Where(visible).Any(p => p.Id == r.ProjectId))
            .AnyAsync(r => r.Id == releasePipelineId && r.DeletedAt == null, ct);
        if (!exists)
        {
            throw new McpException($"Deployment pipeline {releasePipelineId} was not found. Call list_deployment_pipelines to see available pipelines.");
        }
    }

}

/// <summary>Form-post shape for a deployment pipeline: project, name, source build pipeline, target environment, and modes.</summary>
public sealed record ReleasePipelineInput(
    int ProjectId,
    string Name,
    int BuildPipelineId,
    int ProjectEnvironmentId,
    string DeploymentSchedule,
    string SchemaSyncMode,
    /// <summary>
    /// Where the apps come from: <c>build</c> (a build pipeline's builds) or
    /// <c>github_release</c> (a repository's GitHub Releases). See
    /// <c>.design/github-integration-phase2.md</c> (#632).
    /// </summary>
    string ArtifactSource = ReleaseArtifactSource.Build,
    /// <summary>The solution repository whose Releases this pipeline draws from, when the source is <c>github_release</c>.</summary>
    int? GithubReleaseRepositoryId = null,
    /// <summary>
    /// Prepare a deployment, for a person to approve, whenever the build pipeline has a new
    /// successful build (#934). Ignored for a pipeline that installs GitHub releases.
    /// </summary>
    bool PrepareReleaseOnNewBuild = false);

/// <summary>List-row projection of a deployment pipeline with its source and target resolved for display.</summary>
public sealed record ReleasePipelineRow(
    int Id,
    int ProjectId,
    /// <summary>
    /// The owning project's name. Added when the Deployment pipelines browser moved onto the
    /// list archetype: its table needs a Project column, and until this existed
    /// the page could only link the literal word "Project".
    /// </summary>
    string ProjectName,
    string Name,
    int? BuildPipelineId,
    string BuildPipelineName,
    int ProjectEnvironmentId,
    string EnvironmentName,
    string EnvironmentType,
    bool EnvironmentMissing,
    string DeploymentSchedule,
    string SchemaSyncMode,
    /// <summary>Which of the two artifact sources this pipeline draws from (#632).</summary>
    string ArtifactSource = ReleaseArtifactSource.Build,
    /// <summary>The repository whose GitHub Releases it draws from, when that is the source.</summary>
    int? GithubReleaseRepositoryId = null,
    /// <summary>That repository's display name, for the list and the editor.</summary>
    string? GithubReleaseRepositoryName = null,
    /// <summary>The environment's status as Business Central last reported it, verbatim.</summary>
    string? EnvironmentStatus = null,
    /// <summary>
    /// True when a new successful build prepares a deployment through this pipeline for a
    /// person to approve (#934). Nothing is installed until someone does. Serialised under
    /// the product's word, because <c>list_deployment_pipelines</c> hands this row to agents.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonPropertyName("prepareDeploymentOnNewBuild")]
    bool PrepareReleaseOnNewBuild = false)
{
    // ── The delivery summary: filled by ListReleasePipelineOverviewAsync only ──
    //
    // Not serialised: list_deployment_pipelines hands this record to agents as it is,
    // and there these would always be null, which would read as "never deployed".

    /// <summary>The newest deployment through this pipeline that has finished, or null when none has.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ReleasePipelineLastDelivery? LastDelivery { get; init; }

    /// <summary>The deployment shipping through this pipeline right now, or null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ReleasePipelineLiveDelivery? LiveDelivery { get; init; }

    /// <summary>The next deployment waiting for its scheduled time, or null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ReleasePipelineNextDelivery? NextDelivery { get; init; }

    /// <summary>The deployment prepared from a new build and waiting for someone to approve it (#934), or null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ReleasePipelineProposedDelivery? ProposedDelivery { get; init; }

    /// <summary>Microsoft's next platform update for the target environment, as last mirrored; null when there is none.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public EnvironmentNextUpdate? EnvironmentNextUpdate { get; init; }

    /// <summary>
    /// Why nothing can be deployed through this pipeline at all, in a few words, or null
    /// when it can. Not a busy environment: that passes on its own. An environment that
    /// is gone, being removed, or failed does not, and a pipeline aimed at one is broken
    /// until somebody re-points it - so the list says so instead of looking healthy
    /// until the next deployment is refused.
    /// </summary>
    public string? EnvironmentProblem => DescribeEnvironmentProblem(EnvironmentMissing, EnvironmentStatus);

    /// <summary>
    /// <see cref="EnvironmentProblem"/> for a caller holding the two facts rather
    /// than a whole row - the command palette's deployment-pipeline source, which has
    /// to say what the Deployment pipelines page says, in the same words.
    /// </summary>
    public static string? DescribeEnvironmentProblem(bool environmentMissing, string? environmentStatus) =>
        environmentMissing
            ? "no longer present"
            : BcEnvironmentStatus.Classify(environmentStatus) switch
            {
                BcEnvironmentReadiness.Deleting => "being removed",
                BcEnvironmentReadiness.Failed => "failed in Business Central",
                _ => null,
            };
}

/// <summary>The newest finished deployment through a pipeline, as the Deployment pipelines list shows it.</summary>
/// <param name="At">When it finished (deployed, failed, cancelled or handed over), UTC.</param>
/// <param name="FailedAppName">The first app that failed, when the deployment failed on one.</param>
/// <param name="DeploymentSchedule">When the deployment told Business Central to install, snapshotted at deployment time.</param>
public sealed record ReleasePipelineLastDelivery(
    int DeliveryId, string Status, DateTime At, string? FailedAppName, string DeploymentSchedule);

/// <summary>
/// A deployment shipping right now: what the app in hand is doing, which app it is and
/// how many are done. The engine sends one app at a time, so "app 2 of 3" is the
/// first app still uploading or installing.
/// </summary>
/// <param name="Phase">The in-hand app's state (uploading / installing), or null before the first upload.</param>
/// <param name="CurrentApp">1-based position of the app in hand; 0 when the deployment lists no apps.</param>
/// <param name="StartedAt">When the run started (or was claimed, before the first upload), UTC.</param>
/// <param name="PreviousDuration">How long the last successful deployment through the same pipeline took, when there was one.</param>
public sealed record ReleasePipelineLiveDelivery(
    int DeliveryId, string Status, string? Phase, int CurrentApp, int AppsDone, int AppCount,
    DateTime? StartedAt, TimeSpan? PreviousDuration)
{
    /// <summary>Counts the per-app states of a running deployment, in publish order, into the live summary.</summary>
    public static ReleasePipelineLiveDelivery From(
        int deliveryId, string status, DateTime? startedAt, IReadOnlyList<string> apps, TimeSpan? previousDuration)
    {
        var done = apps.Count(a => a is ProjectDeliveryResultStatus.Completed or ProjectDeliveryResultStatus.Scheduled);
        var inHand = -1;
        for (var i = 0; i < apps.Count; i++)
        {
            if (apps[i] is ProjectDeliveryResultStatus.Uploading or ProjectDeliveryResultStatus.Installing)
            {
                inHand = i;
                break;
            }
        }
        var current = inHand >= 0 ? inHand + 1 : Math.Min(done + 1, apps.Count);
        return new ReleasePipelineLiveDelivery(
            deliveryId, status, inHand >= 0 ? apps[inHand] : null, current, done, apps.Count, startedAt, previousDuration);
    }
}

/// <summary>The next deployment through a pipeline waiting for its scheduled time.</summary>
/// <param name="ScheduledFor">When it is due, UTC.</param>
/// <param name="OutsideWindow">True when the person chose a time outside the environment's update window.</param>
/// <param name="ScheduledBy">Who scheduled it, while the account still exists.</param>
public sealed record ReleasePipelineNextDelivery(int DeliveryId, DateTime ScheduledFor, bool OutsideWindow, string? ScheduledBy);

/// <summary>One deployment waiting for approval, as the solution page names it (#934).</summary>
public sealed record ReleaseWaitingForApproval(int ReleasePipelineId, string ReleasePipelineName, int DeliveryId, int BuildId);

/// <summary>A deployment prepared from a new build, waiting for someone to approve it (#934).</summary>
/// <param name="BuildId">The build it would install.</param>
/// <param name="PreparedAt">When the build succeeded and the deployment was prepared, UTC.</param>
public sealed record ReleasePipelineProposedDelivery(int DeliveryId, int BuildId, DateTime PreparedAt);

/// <summary>Microsoft's next platform update for an environment, as last mirrored.</summary>
/// <param name="Date">When it is set to run, UTC; null when no date is chosen yet.</param>
/// <param name="Version">The version it moves to, e.g. <c>27.6</c>.</param>
/// <param name="Type">The API's target version type (major / minor), verbatim.</param>
public sealed record EnvironmentNextUpdate(DateTime? Date, string Version, string? Type);
