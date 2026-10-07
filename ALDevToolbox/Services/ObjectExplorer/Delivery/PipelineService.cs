using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
    /// True when the current user may delete pipelines: an org Admin or a SiteAdmin.
    /// Everyone else who manages a solution disables its pipelines instead (#1131).
    /// </summary>
    public Task<bool> CanDeleteAsync(CancellationToken ct = default) => _access.CanDeletePipelinesAsync(ct);

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
        var (name, nameIsCustom, selectionJson, releaseRepositoryId, branch) = await ValidateAsync(input, existing: null, ct);

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
    /// preview check and building on push, and the name that follows from them. Saving with the check or
    /// building on push on makes the caller the person those builds run as. A new name
    /// carries through to the deployment pipelines named after this one. A typed name
    /// left as it was while the branch or extensions change goes back to the generated
    /// one (#1135).
    /// </summary>
    public async Task UpdatePipelineAsync(int id, PipelineInput input, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var pipeline = await _db.OePipelines
            .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct)
            ?? throw Validation("Pipeline", "This pipeline no longer exists.");

        // Validate against the pipeline's own project (input.ProjectId is ignored on
        // update — a pipeline can't move between projects).
        var (name, nameIsCustom, selectionJson, releaseRepositoryId, branch) = await ValidateAsync(input with { ProjectId = pipeline.ProjectId }, existing: pipeline, ct);

        var oldName = pipeline.Name;
        pipeline.Name = name;
        pipeline.NameIsCustom = nameIsCustom;
        pipeline.RequestedAppIdsJson = selectionJson;
        pipeline.GithubReleaseRepositoryId = releaseRepositoryId;
        pipeline.Branch = branch;
        // Whoever saves the pipeline with the preview check or building on push on becomes
        // the person those builds run as, the way a deployment pipeline's "deploy without
        // approval" follows whoever saved it. Otherwise one manager could repoint the
        // branch or the extensions and have the builds clone with another person's
        // repository access.
        pipeline.PreviewCheckByUserId = input.PreviewCheck ? _orgContext.CurrentUserId : null;
        pipeline.PreviewCheckBlocked = null;
        pipeline.PreviewCheck = input.PreviewCheck;
        pipeline.BuildOnPushByUserId = input.BuildOnPush ? _orgContext.CurrentUserId : null;
        pipeline.BuildOnPushBlocked = null;
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
    /// as, without editing the pipeline. Same rule as saving it.
    /// </summary>
    public Task TakeOverPreviewCheckAsync(int id, CancellationToken ct = default) =>
        TakeOverAsync(id, PipelineAutomation.PreviewCheck, ct);

    /// <summary>
    /// Resumes paused building on push by making the caller the person its builds run
    /// as, without editing the pipeline. Same rule as saving it. The push that found it
    /// paused is not built; the next one is.
    /// </summary>
    public Task TakeOverBuildOnPushAsync(int id, CancellationToken ct = default) =>
        TakeOverAsync(id, PipelineAutomation.BuildOnPush, ct);

    private async Task TakeOverAsync(int id, PipelineAutomation automation, CancellationToken ct)
    {
        RequireOrganizationId();
        var pipeline = await _db.OePipelines
            .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct)
            ?? throw Validation("Pipeline", "This pipeline no longer exists.");
        var previewCheck = automation == PipelineAutomation.PreviewCheck;
        if (previewCheck ? !pipeline.PreviewCheck : !pipeline.BuildOnPush)
        {
            throw previewCheck
                ? Validation("PreviewCheck", "This pipeline doesn't run the preview check.")
                : Validation("BuildOnPush", "This pipeline doesn't build on new commits.");
        }

        var ownerId = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == pipeline.ProjectId)
            .Select(c => c.CreatedByUserId)
            .FirstOrDefaultAsync(ct);
        await _access.EnsureCanManageAsync(pipeline.ProjectId, ownerId, ct);

        if (previewCheck)
        {
            pipeline.PreviewCheckByUserId = _orgContext.CurrentUserId;
            pipeline.PreviewCheckBlocked = null;
        }
        else
        {
            pipeline.BuildOnPushByUserId = _orgContext.CurrentUserId;
            pipeline.BuildOnPushBlocked = null;
        }
        pipeline.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Pipeline {PipelineId}'s {Automation} now runs as user {UserId}.", id, automation, _orgContext.CurrentUserId);
    }

    /// <summary>
    /// Disables or enables a pipeline. A disabled pipeline starts no build: Build is
    /// refused, pushes and the nightly preview check pass it by, and automatic builds
    /// already waiting are refused when their turn comes. Its settings, its builds and
    /// the deployment pipelines drawing from it stay as they are (#1131). Anyone who may
    /// manage the solution may do it. Doing it twice changes nothing.
    /// </summary>
    public async Task SetPipelineDisabledAsync(int id, bool disabled, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var pipeline = await _db.OePipelines
            .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct)
            ?? throw Validation("Pipeline", "This pipeline no longer exists.");

        var ownerId = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == pipeline.ProjectId)
            .Select(c => c.CreatedByUserId)
            .FirstOrDefaultAsync(ct);
        await _access.EnsureCanManageAsync(pipeline.ProjectId, ownerId, ct);

        if ((pipeline.DisabledAt is not null) == disabled) return;
        var now = DateTime.UtcNow;
        pipeline.DisabledAt = disabled ? now : null;
        pipeline.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("{Action} pipeline {PipelineId} as user {UserId}.",
            disabled ? "Disabled" : "Enabled", id, _orgContext.CurrentUserId);
    }

    /// <summary>
    /// Soft-deletes a pipeline. Admins only: anyone else who manages the solution can
    /// disable it instead (#1131). Refused while a deployment pipeline still draws from
    /// it: the delete is soft, so the foreign key's restrict never fires, and the
    /// deployment pipeline would go on naming a source that no longer builds (#1123).
    /// </summary>
    public async Task SoftDeletePipelineAsync(int id, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var pipeline = await _db.OePipelines
            .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct)
            ?? throw Validation("Pipeline", "This pipeline no longer exists.");

        await _access.EnsureCanDeletePipelinesAsync(ct);

        var dependents = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.BuildPipelineId == id && r.DeletedAt == null)
            .OrderBy(r => r.Name)
            .Select(r => r.Name)
            .ToListAsync(ct);
        if (dependents.Count > 0)
        {
            var names = string.Join(", ", dependents.Select(n => $"\"{n}\""));
            throw Validation("Pipeline", dependents.Count == 1
                ? $"The deployment pipeline {names} deploys this pipeline's builds. Delete it, or point it at another build pipeline, first."
                : $"The deployment pipelines {names} deploy this pipeline's builds. Delete them, or point them at another build pipeline, first.");
        }

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
    /// with field-keyed errors otherwise. <paramref name="existing"/> is the pipeline
    /// being updated (null on create), whose typed name is dropped when the branch or
    /// selection changes under it (#1135).
    /// </summary>
    private async Task<(string Name, bool NameIsCustom, string? SelectionJson, int? GithubReleaseRepositoryId, string? Branch)> ValidateAsync(
        PipelineInput input, OePipeline? existing, CancellationToken ct)
    {
        var existingId = existing?.Id;
        var errors = new Dictionary<string, string>();

        // The parent project must exist in this org and be manageable by the user.
        var owner = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == input.ProjectId && c.DeletedAt == null)
            .Select(c => new { c.CreatedByUserId, c.DiscoveredExtensionsJson })
            .FirstOrDefaultAsync(ct);
        if (owner is null)
        {
            throw Validation("Project", "Choose a solution for this pipeline.");
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
        // A typed name was given for the branch and extensions it was typed under. A
        // save that changes either and leaves the typed name exactly as it was goes back
        // to the generated name (#1135); a name typed afresh in the same save is kept.
        // When the generated name is taken the typed one stays, since asking for a name
        // here would only be answered by the same typed name and dropped again.
        if (custom is not null
            && existing is { NameIsCustom: true }
            && string.Equals(custom, existing.Name, StringComparison.Ordinal)
            && !errors.ContainsKey("Branch")
            && (!string.Equals(branch, existing.Branch, StringComparison.Ordinal)
                || !SameSelection(input.SelectedAppIds, existing.RequestedAppIdsJson))
            && generated.Length <= PipelineNames.MaxLength
            && !await NameTakenAsync(input.ProjectId, existing.Id, generated, ct))
        {
            custom = null;
        }
        var name = custom ?? generated;
        if (name.Length > PipelineNames.MaxLength)
        {
            errors["Name"] = "Keep the name under 200 characters.";
        }
        else if (!errors.ContainsKey("Branch"))
        {
            // Per-project name uniqueness among active rows (the DB enforces it via a
            // case-insensitive lower(name) index too); pre-check for a friendly error.
            if (await NameTakenAsync(input.ProjectId, existingId ?? 0, name, ct)) errors["Name"] = NameTakenMessage(name);
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        // null/empty selection = build everything (the default), stored as a null column.
        var selectionJson = input.SelectedAppIds is { Count: > 0 }
            ? JsonSerializer.Serialize(input.SelectedAppIds)
            : null;
        return (name, custom is not null, selectionJson, releaseRepositoryId, branch);
    }

    /// <summary>
    /// Whether another active pipeline in the project already has <paramref name="name"/>,
    /// compared the way the case-insensitive unique index compares it.
    /// </summary>
    private Task<bool> NameTakenAsync(int projectId, int exceptId, string name, CancellationToken ct) =>
        _db.OePipelines.AsNoTracking()
            .AnyAsync(p => p.DeletedAt == null
                           && p.ProjectId == projectId
                           && p.Id != exceptId
                           && p.Name.ToLower() == name.ToLower(), ct);

    /// <summary>
    /// Whether <paramref name="selected"/> picks the same extensions as the stored
    /// <paramref name="storedJson"/>, ignoring order and how the ids are written.
    /// Null or empty on either side means every extension.
    /// </summary>
    internal static bool SameSelection(IReadOnlyCollection<string>? selected, string? storedJson)
    {
        var before = Normalized(ReadSelection(storedJson));
        var after = Normalized(selected);
        return before.SetEquals(after);

        static HashSet<string> Normalized(IEnumerable<string>? ids) =>
            (ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(ProjectBuildService.NormalizeAppId)
                .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The stored extension selection; null (every extension) when unset or unreadable.</summary>
    private static List<string>? ReadSelection(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gives the solution's build pipelines whose names were generated the name their
    /// branch and extensions now call for, and carries a new name through to the
    /// deployment pipelines named after them. Run after discovery refreshes the
    /// discovered-extensions cache, since a one-extension pipeline is named after its
    /// extension and the extension may have been renamed (#1135). A typed name is left
    /// alone. A pipeline whose new name another pipeline already has keeps its old one
    /// and says so in the log: this runs in the background, where there is nobody to
    /// ask for a name. Has no access check: the caller (discovery) owns that.
    /// </summary>
    public async Task RefreshGeneratedNamesAsync(int projectId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        // A solution deleted since the job was queued, or one never discovered, has no
        // extension names to go by; renaming from nothing would only say "1 extension".
        var project = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Select(p => new { p.DiscoveredExtensionsJson })
            .FirstOrDefaultAsync(ct);
        if (project?.DiscoveredExtensionsJson is null) return;
        var extensionNames = ExtensionNames(project.DiscoveredExtensionsJson);

        var pipelines = await _db.OePipelines.AsNoTracking()
            .Where(p => p.ProjectId == projectId && p.DeletedAt == null && !p.NameIsCustom)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
        // Old names stay in the set, as in RenameDeploymentPipelinesAsync, so no two
        // pipelines swap names in one pass.
        var taken = await _db.OePipelines.AsNoTracking()
            .Where(p => p.ProjectId == projectId && p.DeletedAt == null)
            .Select(p => p.Name)
            .ToListAsync(ct);
        var takenSet = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pipeline in pipelines)
        {
            var selection = ReadSelection(pipeline.RequestedAppIdsJson);
            // Only a one-extension pipeline is named after discovery. When its extension is
            // missing - a repository that failed to clone this time - it keeps its name
            // rather than flipping to "1 extension" until the next discovery.
            if (selection is not { Count: 1 }
                || !extensionNames.ContainsKey(ProjectBuildService.NormalizeAppId(selection.First())))
            {
                continue;
            }
            var generated = PipelineNames.ForBuildPipeline(pipeline.Branch, selection, extensionNames);
            if (string.Equals(generated, pipeline.Name, StringComparison.Ordinal)) continue;
            if (!string.Equals(generated, pipeline.Name, StringComparison.OrdinalIgnoreCase) && takenSet.Contains(generated))
            {
                _logger.LogWarning(
                    "Pipeline {PipelineId} keeps its name {Name}: {NewName} is taken in project {ProjectId}.",
                    pipeline.Id, pipeline.Name, generated, projectId);
                continue;
            }

            // Only if nobody saved the pipeline since it was read: a save in between has
            // already named it from newer settings, or given it a name of its own.
            var now = DateTime.UtcNow;
            int changed;
            try
            {
                changed = await _db.OePipelines
                    .Where(p => p.Id == pipeline.Id && p.DeletedAt == null && !p.NameIsCustom
                                && p.Name == pipeline.Name && p.Branch == pipeline.Branch
                                && p.RequestedAppIdsJson == pipeline.RequestedAppIdsJson)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(p => p.Name, generated)
                        .SetProperty(p => p.UpdatedAt, now), ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // A save that raced this one took the name first. The names are only
                // cosmetic here, so the next discovery tries again.
                _logger.LogWarning(ex, "Pipeline {PipelineId} keeps its name {Name}: {NewName} was taken while renaming.",
                    pipeline.Id, pipeline.Name, generated);
                continue;
            }
            if (changed == 0) continue;

            takenSet.Add(generated);
            _logger.LogInformation("Renamed pipeline {PipelineId} from {Name} to {NewName} after its extensions changed.",
                pipeline.Id, pipeline.Name, generated);
            pipeline.Name = generated;
            pipeline.UpdatedAt = now;
            await RenameDeploymentPipelinesAsync(pipeline, ct);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
            {
                _db.ChangeTracker.Clear();
                _logger.LogWarning(ex, "Deployment pipelines named after pipeline {PipelineId} kept their names: a name was taken while renaming.", pipeline.Id);
            }
        }
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
