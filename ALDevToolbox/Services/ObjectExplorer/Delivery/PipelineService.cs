using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
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
        var (name, nameIsCustom, selectionJson, releaseRepositoryId, branch) = await ValidateAsync(input, existingId: null, ct);

        var now = DateTime.UtcNow;
        var pipeline = new OePipeline
        {
            OrganizationId = orgId,
            ProjectId = input.ProjectId,
            CreatedByUserId = _orgContext.CurrentUserId,
            Name = name,
            NameIsCustom = nameIsCustom,
            RequestedAppIdsJson = selectionJson,
            GithubReleaseRepositoryId = releaseRepositoryId,
            Branch = branch,
            PreviewCheck = input.PreviewCheck,
            PreviewCheckByUserId = input.PreviewCheck ? _orgContext.CurrentUserId : null,
            AutoVersion = input.AutoVersion,
            ChangedAppsOnly = input.ChangedAppsOnly,
            BuildOnPush = input.BuildOnPush,
            BuildOnPushByUserId = input.BuildOnPush ? _orgContext.CurrentUserId : null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.OePipelines.Add(pipeline);
        await SaveTranslatingNameClashAsync(ct);

        _logger.LogInformation("Created pipeline {PipelineId} ({Name}) for project {ProjectId}.",
            pipeline.Id, name, input.ProjectId);
        return pipeline.Id;
    }

    /// <summary>
    /// Updates a pipeline's extension selection, publishing target, branch, nightly
    /// preview check and building on push, and the name that follows from them. Turning the check on makes
    /// the caller the person it runs as; so does saving while it is paused (its person
    /// gone or refused), which is how someone with access takes it over. A new name
    /// carries through to the deployment pipelines named after this one.
    /// </summary>
    public async Task UpdatePipelineAsync(int id, PipelineInput input, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var pipeline = await _db.OePipelines
            .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct)
            ?? throw Validation("Name", "This pipeline no longer exists.");

        // Validate against the pipeline's own project (input.ProjectId is ignored on
        // update — a pipeline can't move between projects).
        var (name, nameIsCustom, selectionJson, releaseRepositoryId, branch) = await ValidateAsync(input with { ProjectId = pipeline.ProjectId }, existingId: id, ct);

        var oldName = pipeline.Name;
        pipeline.Name = name;
        pipeline.NameIsCustom = nameIsCustom;
        pipeline.RequestedAppIdsJson = selectionJson;
        pipeline.GithubReleaseRepositoryId = releaseRepositoryId;
        pipeline.Branch = branch;
        if (!input.PreviewCheck)
        {
            pipeline.PreviewCheckByUserId = null;
            pipeline.PreviewCheckBlocked = null;
        }
        else if (!pipeline.PreviewCheck || pipeline.PreviewCheckByUserId is null || pipeline.PreviewCheckBlocked is not null)
        {
            pipeline.PreviewCheckByUserId = _orgContext.CurrentUserId;
            pipeline.PreviewCheckBlocked = null;
        }
        pipeline.PreviewCheck = input.PreviewCheck;
        // Building on push follows the same rule: turning it on, or saving while it is
        // paused, makes the caller the person its builds run as.
        if (!input.BuildOnPush)
        {
            pipeline.BuildOnPushByUserId = null;
            pipeline.BuildOnPushBlocked = null;
        }
        else if (!pipeline.BuildOnPush || pipeline.BuildOnPushByUserId is null || pipeline.BuildOnPushBlocked is not null)
        {
            pipeline.BuildOnPushByUserId = _orgContext.CurrentUserId;
            pipeline.BuildOnPushBlocked = null;
        }
        pipeline.BuildOnPush = input.BuildOnPush;
        pipeline.AutoVersion = input.AutoVersion;
        pipeline.ChangedAppsOnly = input.ChangedAppsOnly;
        pipeline.UpdatedAt = DateTime.UtcNow;
        if (!string.Equals(oldName, name, StringComparison.Ordinal))
        {
            await RenameDeploymentPipelinesAsync(pipeline, ct);
        }
        await SaveTranslatingNameClashAsync(ct);
        _logger.LogInformation("Updated pipeline {PipelineId} ({Name}).", pipeline.Id, name);
    }

    /// <summary>
    /// Resumes a paused nightly preview check by making the caller the person it runs
    /// as. Same rule as saving the pipeline while the check is paused.
    /// </summary>
    public async Task TakeOverPreviewCheckAsync(int id, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var pipeline = await _db.OePipelines
            .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct)
            ?? throw Validation("Name", "This pipeline no longer exists.");
        if (!pipeline.PreviewCheck) throw Validation("PreviewCheck", "This pipeline doesn't run the preview check.");

        var ownerId = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == pipeline.ProjectId)
            .Select(c => c.CreatedByUserId)
            .FirstOrDefaultAsync(ct);
        await _access.EnsureCanManageAsync(pipeline.ProjectId, ownerId, ct);

        pipeline.PreviewCheckByUserId = _orgContext.CurrentUserId;
        pipeline.PreviewCheckBlocked = null;
        pipeline.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Pipeline {PipelineId}'s preview check now runs as user {UserId}.", id, _orgContext.CurrentUserId);
    }

    /// <summary>
    /// Resumes paused building on push by making the caller the person its builds run
    /// as. Same rule as saving the pipeline while it is paused. The push that found it
    /// paused is not built; the next one is.
    /// </summary>
    public async Task TakeOverBuildOnPushAsync(int id, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var pipeline = await _db.OePipelines
            .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct)
            ?? throw Validation("Name", "This pipeline no longer exists.");
        if (!pipeline.BuildOnPush) throw Validation("BuildOnPush", "This pipeline doesn't build on new commits.");

        var ownerId = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == pipeline.ProjectId)
            .Select(c => c.CreatedByUserId)
            .FirstOrDefaultAsync(ct);
        await _access.EnsureCanManageAsync(pipeline.ProjectId, ownerId, ct);

        pipeline.BuildOnPushByUserId = _orgContext.CurrentUserId;
        pipeline.BuildOnPushBlocked = null;
        pipeline.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Pipeline {PipelineId}'s builds on push now run as user {UserId}.", id, _orgContext.CurrentUserId);
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
    /// and the per-project name uniqueness rule. Returns the name (generated from the
    /// branch and selection, unless the person typed one) and the selection serialised
    /// to JSON (null = build everything). Throws <see cref="PlanValidationException"/>
    /// with field-keyed errors otherwise.
    /// </summary>
    private async Task<(string Name, bool NameIsCustom, string? SelectionJson, int? GithubReleaseRepositoryId, string? Branch)> ValidateAsync(
        PipelineInput input, int? existingId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        // The parent project must exist in this org and be manageable by the user.
        var owner = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == input.ProjectId && c.DeletedAt == null)
            .Select(c => new { c.CreatedByUserId, c.DiscoveredExtensionsJson })
            .FirstOrDefaultAsync(ct);
        if (owner is null)
        {
            throw Validation("Project", "Choose a project for this pipeline.");
        }
        await _access.EnsureCanManageAsync(input.ProjectId, owner.CreatedByUserId, ct);

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

        // The name follows from the branch and the selection. A typed name is only
        // needed when that one is taken; typing the generated name is not a custom one.
        var generated = PipelineNames.ForBuildPipeline(
            branch, input.SelectedAppIds, ExtensionNames(owner.DiscoveredExtensionsJson));
        var custom = string.IsNullOrWhiteSpace(input.CustomName) ? null : input.CustomName.Trim();
        if (custom is not null && string.Equals(custom, generated, StringComparison.Ordinal)) custom = null;
        var name = custom ?? generated;
        if (name.Length > PipelineNames.MaxLength)
        {
            errors["Name"] = "Keep the name under 200 characters.";
        }
        else if (!errors.ContainsKey("Branch"))
        {
            // Per-project name uniqueness among active rows (the DB enforces it via a
            // case-insensitive lower(name) index too); pre-check for a friendly error.
            var clash = await _db.OePipelines.AsNoTracking()
                .AnyAsync(p => p.DeletedAt == null
                               && p.ProjectId == input.ProjectId
                               && p.Id != (existingId ?? 0)
                               && p.Name.ToLower() == name.ToLower(), ct);
            if (clash) errors["Name"] = NameTakenMessage(name);
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        // null/empty selection = build everything (the default), stored as a null column.
        var selectionJson = input.SelectedAppIds is { Count: > 0 }
            ? JsonSerializer.Serialize(input.SelectedAppIds)
            : null;
        return (name, custom is not null, selectionJson, releaseRepositoryId, branch);
    }

    /// <summary>
    /// The extension names a build pipeline's name can use, by normalised app id, from
    /// the solution's discovered-extensions cache. Empty when nothing is discovered yet,
    /// in which case a one-extension pipeline is called "1 extension".
    /// </summary>
    internal static Dictionary<string, string> ExtensionNames(string? discoveredExtensionsJson)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(discoveredExtensionsJson)) return names;
        try
        {
            foreach (var extension in JsonSerializer.Deserialize<List<DiscoveredExtension>>(discoveredExtensionsJson) ?? [])
            {
                names.TryAdd(ProjectBuildService.NormalizeAppId(extension.AppId), extension.Name);
            }
        }
        catch (JsonException)
        {
            // A cache we can't read only costs the extension's name in the pipeline's.
        }
        return names;
    }

    /// <summary>
    /// Gives the deployment pipelines named after this build pipeline its new name.
    /// One a person named themselves keeps its name, and so does one whose new name
    /// another deployment pipeline already has: it is left for the next save of it to
    /// sort out, rather than failing this one.
    /// </summary>
    private async Task RenameDeploymentPipelinesAsync(OePipeline buildPipeline, CancellationToken ct)
    {
        var siblings = await _db.OeReleasePipelines
            .Where(r => r.ProjectId == buildPipeline.ProjectId && r.DeletedAt == null)
            .Include(r => r.ProjectEnvironment)
            .ToListAsync(ct);
        var taken = siblings.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var deployment in siblings)
        {
            if (deployment.BuildPipelineId != buildPipeline.Id
                || deployment.ArtifactSource != ReleaseArtifactSource.Build
                || deployment.NameIsCustom
                || deployment.ProjectEnvironment is null)
            {
                continue;
            }
            var renamed = PipelineNames.ForDeploymentFromBuild(buildPipeline.Name, deployment.ProjectEnvironment.Name);
            if (string.Equals(renamed, deployment.Name, StringComparison.Ordinal)) continue;
            if (!string.Equals(renamed, deployment.Name, StringComparison.OrdinalIgnoreCase) && taken.Contains(renamed))
            {
                _logger.LogWarning(
                    "Deployment pipeline {ReleasePipelineId} keeps its name {Name}: {NewName} is taken in project {ProjectId}.",
                    deployment.Id, deployment.Name, renamed, deployment.ProjectId);
                continue;
            }
            // The old name stays in the set on purpose: freeing it for a sibling would make
            // this save depend on the order EF writes the rows against the unique index.
            taken.Add(renamed);
            deployment.Name = renamed;
            deployment.UpdatedAt = buildPipeline.UpdatedAt;
        }
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
            throw Validation("Name", "Another pipeline in this solution already has this name. Type a different name for this one.");
        }
    }

    /// <summary>The clash message; the dialog shows a name field when it sees it.</summary>
    internal static string NameTakenMessage(string name) =>
        $"Another pipeline in this solution is already called '{name}'. Type a different name for this one.";

    private static PlanValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}

/// <summary>Form-post shape for a pipeline: its project, the extensions it compiles (null/empty = all), and its settings.</summary>
public sealed record PipelineInput(
    int ProjectId,
    /// <summary>
    /// A name the person typed, for when the generated one is already taken in the
    /// solution. Null or blank means the generated name (<see cref="PipelineNames"/>).
    /// </summary>
    string? CustomName,
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
    /// Whether the pipeline also runs the nightly preview check against the next
    /// minor and next major versions. See
    /// <c>.design/object-explorer-project-builds.md</c>, "The nightly preview check".
    /// </summary>
    bool PreviewCheck = false,
    /// <summary>
    /// Whether builds add their build number to each app's version. On unless the
    /// person turns it off. See <c>.design/object-explorer-project-builds.md</c>,
    /// "Build numbers in app versions".
    /// </summary>
    bool AutoVersion = true,
    /// <summary>
    /// Whether a push to the pipeline's branch starts a build. Off unless the person
    /// turns it on. See <c>.design/github-integration-phase2.md</c>, "Building on push".
    /// </summary>
    bool BuildOnPush = false,
    /// <summary>
    /// Whether builds publish only the extensions that changed since this pipeline last
    /// produced them. On unless the person turns it off. See
    /// <c>.design/object-explorer-project-builds.md</c>, "Publishing only what changed".
    /// </summary>
    bool ChangedAppsOnly = true);

/// <summary>A project choice for the "New pipeline" dialog's project picker.</summary>
public sealed record PipelineProjectOption(int Id, string Name);
