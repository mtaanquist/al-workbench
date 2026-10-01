using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// CRUD over <see cref="OePipeline"/> — the named build configurations that belong to
/// a <see cref="OeProject"/>. A pipeline owns the extension selection a build runs
/// (<see cref="OePipeline.RequestedAppIdsJson"/>); a project has many. Management
/// rights come from the parent project's owner via <see cref="ProjectAccess"/>.
/// Org-scoped via the EF query filter; mutations run inside an authenticated request.
/// Validation throws <see cref="PlanValidationException"/> with field-keyed errors.
/// See <c>.design/artifacts.md</c>.
/// </summary>
public sealed class PipelineService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly ILogger<PipelineService> _logger;

    public PipelineService(AppDbContext db, IOrganizationContext orgContext, ProjectAccess access, ILogger<PipelineService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; pipeline mutation called outside an authenticated request.");

    /// <summary>
    /// True when the current user may manage <paramref name="pipelineId"/> — i.e. they
    /// may manage its parent project (owner or org Admin / SiteAdmin). False when the
    /// pipeline no longer exists.
    /// </summary>
    public async Task<bool> CanManageAsync(int pipelineId, CancellationToken ct = default)
    {
        var owner = await _db.OePipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId && p.DeletedAt == null)
            .Select(p => new { p.ProjectId, OwnerId = p.Project!.CreatedByUserId })
            .FirstOrDefaultAsync(ct);
        return owner is not null && await _access.CanManageAsync(owner.ProjectId, owner.OwnerId, ct);
    }

    /// <summary>
    /// Active pipelines the current user may see, optionally scoped to one project,
    /// ordered by name. A pipeline inherits its project's visibility.
    /// </summary>
    public async Task<List<OePipeline>> ListPipelinesAsync(int? projectId = null, CancellationToken ct = default)
    {
        var query = _db.OePipelines.AsNoTracking().Where(p => p.DeletedAt == null);
        if (projectId is { } pid)
        {
            await _access.EnsureCanViewAsync(pid, ct);
            query = query.Where(p => p.ProjectId == pid);
        }
        else
        {
            var visible = ProjectAccess.VisibleProjectPredicate(await _access.GetSnapshotAsync(ct));
            query = query.Where(p => _db.OeProjects.Where(visible).Any(v => v.Id == p.ProjectId));
        }
        return await query.OrderBy(p => p.Name).ToListAsync(ct);
    }

    /// <summary>
    /// A single active pipeline with its project, or null when not found in this
    /// org. Throws <see cref="ProjectAccessDeniedException"/> when its project is
    /// Private and the caller has no grant on it.
    /// </summary>
    public async Task<OePipeline?> GetPipelineAsync(int id, CancellationToken ct = default)
    {
        await EnsureCanViewPipelineAsync(id, ct);
        return await _db.OePipelines.AsNoTracking()
            .Where(p => p.Id == id && p.DeletedAt == null)
            .Include(p => p.Project)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Creates a pipeline under a project. Returns the new id.</summary>
    public async Task<int> CreatePipelineAsync(PipelineInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var (name, selectionJson, releaseRepositoryId, branch, bcTarget) = await ValidateAsync(input, existingId: null, ct);

        var now = DateTime.UtcNow;
        var pipeline = new OePipeline
        {
            OrganizationId = orgId,
            ProjectId = input.ProjectId,
            CreatedByUserId = _orgContext.CurrentUserId,
            Name = name,
            RequestedAppIdsJson = selectionJson,
            GithubReleaseRepositoryId = releaseRepositoryId,
            Branch = branch,
            BcTarget = bcTarget,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.OePipelines.Add(pipeline);
        await SaveTranslatingNameClashAsync(ct);

        _logger.LogInformation("Created pipeline {PipelineId} ({Name}) for project {ProjectId}.",
            pipeline.Id, name, input.ProjectId);
        return pipeline.Id;
    }

    /// <summary>Updates a pipeline's name, extension selection, publishing target, branch and the Business Central version it builds against.</summary>
    public async Task UpdatePipelineAsync(int id, PipelineInput input, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var pipeline = await _db.OePipelines
            .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct)
            ?? throw Validation("Name", "This pipeline no longer exists.");

        // Validate against the pipeline's own project (input.ProjectId is ignored on
        // update — a pipeline can't move between projects).
        var (name, selectionJson, releaseRepositoryId, branch, bcTarget) = await ValidateAsync(input with { ProjectId = pipeline.ProjectId }, existingId: id, ct);

        pipeline.Name = name;
        pipeline.RequestedAppIdsJson = selectionJson;
        pipeline.GithubReleaseRepositoryId = releaseRepositoryId;
        pipeline.Branch = branch;
        pipeline.BcTarget = bcTarget;
        pipeline.UpdatedAt = DateTime.UtcNow;
        await SaveTranslatingNameClashAsync(ct);
        _logger.LogInformation("Updated pipeline {PipelineId} ({Name}).", pipeline.Id, name);
    }

    /// <summary>Soft-deletes a pipeline. Its past builds stay reachable (their pipeline_id is nulled by the FK).</summary>
    public async Task SoftDeletePipelineAsync(int id, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var pipeline = await _db.OePipelines
            .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct)
            ?? throw Validation("Name", "This pipeline no longer exists.");

        var ownerId = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == pipeline.ProjectId)
            .Select(c => c.CreatedByUserId)
            .FirstOrDefaultAsync(ct);
        await _access.EnsureCanManageAsync(pipeline.ProjectId, ownerId, ct);

        pipeline.DeletedAt = DateTime.UtcNow;
        pipeline.UpdatedAt = pipeline.DeletedAt.Value;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Soft-deleted pipeline {PipelineId}.", id);
    }

    /// <summary>
    /// Validates the input against its project (which must exist and be manageable)
    /// and the per-project name uniqueness rule. Returns the normalised name and the
    /// selection serialised to JSON (null = build everything). Throws
    /// <see cref="PlanValidationException"/> with field-keyed errors otherwise.
    /// </summary>
    private async Task<(string Name, string? SelectionJson, int? GithubReleaseRepositoryId, string? Branch, string BcTarget)> ValidateAsync(
        PipelineInput input, int? existingId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        // The parent project must exist in this org and be manageable by the user.
        var owner = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == input.ProjectId && c.DeletedAt == null)
            .Select(c => new { c.CreatedByUserId })
            .FirstOrDefaultAsync(ct);
        if (owner is null)
        {
            throw Validation("Project", "Choose a project for this pipeline.");
        }
        await _access.EnsureCanManageAsync(input.ProjectId, owner.CreatedByUserId, ct);

        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            errors["Name"] = "Give the pipeline a name.";
        }
        else if (name.Length > 200)
        {
            errors["Name"] = "Keep the name under 200 characters.";
        }
        else
        {
            // Per-project name uniqueness among active rows (the DB enforces it via a
            // case-insensitive lower(name) index too); pre-check for a friendly error.
            var clash = await _db.OePipelines.AsNoTracking()
                .AnyAsync(p => p.DeletedAt == null
                               && p.ProjectId == input.ProjectId
                               && p.Id != (existingId ?? 0)
                               && p.Name.ToLower() == name.ToLower(), ct);
            if (clash)
            {
                errors["Name"] = "Another pipeline in this project already uses this name.";
            }
        }

        // Publishing target: a repository of this very project, so a pipeline can
        // never be pointed at another customer's repository by editing a form value.
        int? releaseRepositoryId = null;
        if (input.GithubReleaseRepositoryId is { } repoId && repoId != 0)
        {
            var belongs = await _db.OeProjectRepositories.AsNoTracking()
                .AnyAsync(r => r.Id == repoId
                               && r.ProjectId == input.ProjectId
                               && r.Provider == RepositoryProvider.GitHub, ct);
            if (!belongs)
            {
                errors["GithubReleaseRepositoryId"] = "Choose one of this solution's GitHub repositories, or don't publish releases.";
            }
            else
            {
                releaseRepositoryId = repoId;
            }
        }

        // The branch every repository is checked out at. Blank means each
        // repository's default branch. The rule is git's own, over the alphabet a
        // webhook's branch is held to, so a name typed here and one GitHub reports
        // on a push are judged the same way (#963).
        var branch = string.IsNullOrWhiteSpace(input.Branch) ? null : input.Branch.Trim();
        if (branch is not null && !GitBranchName.IsValid(branch))
        {
            errors["Branch"] = "That isn't a valid branch name. Type it exactly as GitHub shows it, e.g. main or release/25.0 - no spaces or '..'.";
        }

        // Blank means Current, so a caller that predates the setting keeps building
        // what the manifests ask for.
        var bcTarget = string.IsNullOrWhiteSpace(input.BcTarget) ? ProjectBuildTarget.Current : input.BcTarget.Trim();
        if (!ProjectBuildTarget.IsValid(bcTarget))
        {
            errors["BcTarget"] = "Choose Current, Next minor or Next major.";
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        // null/empty selection = build everything (the default), stored as a null column.
        var selectionJson = input.SelectedAppIds is { Count: > 0 }
            ? JsonSerializer.Serialize(input.SelectedAppIds)
            : null;
        return (name, selectionJson, releaseRepositoryId, branch, bcTarget);
    }

    /// <summary>
    /// Gates a pipeline-keyed read on its project's visibility — a pipeline has no
    /// visibility of its own. A pipeline that doesn't exist passes; the read below
    /// returns nothing on its own.
    /// </summary>
    private async Task EnsureCanViewPipelineAsync(int pipelineId, CancellationToken ct)
    {
        var projectId = await _db.OePipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId)
            .Select(p => (int?)p.ProjectId)
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
            throw Validation("Name", "Another pipeline in this project already uses this name.");
        }
    }

    private static PlanValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}

/// <summary>Form-post shape for a pipeline: its project, name, and the extensions it compiles (null/empty = all).</summary>
public sealed record PipelineInput(
    int ProjectId,
    string Name,
    IReadOnlyList<string>? SelectedAppIds,
    /// <summary>
    /// The solution repository each successful build is published to as a GitHub
    /// Release. Null (the default) means builds are not published anywhere. See
    /// <c>.design/github-integration-phase2.md</c> (#632).
    /// </summary>
    int? GithubReleaseRepositoryId = null,
    /// <summary>
    /// The branch the pipeline builds and watches. Null or blank means each
    /// repository's default branch. See <c>.design/github-integration-phase2.md</c>,
    /// "Branch watching" (#963).
    /// </summary>
    string? Branch = null,
    /// <summary>
    /// Which Business Central version the pipeline builds against, one of
    /// <see cref="ProjectBuildTarget"/>. Null or blank means <c>current</c>. See
    /// <c>.design/object-explorer-project-builds.md</c>, "Building against the next version".
    /// </summary>
    string? BcTarget = null);

/// <summary>A project choice for the "New pipeline" dialog's project picker.</summary>
public sealed record PipelineProjectOption(int Id, string Name);
