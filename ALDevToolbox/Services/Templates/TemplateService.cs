using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Services.Templates;

/// <summary>
/// Read- and write-side service for runtime templates under the
/// unified-extensions model. Drives the user-facing dropdowns and templates
/// browser; backs the <c>/admin/templates*</c> pages.
/// </summary>
/// <remarks>
/// Write side accepts <see cref="TemplateAuthoring"/> — produced by
/// <see cref="TemplateTomlMapper.FromToml(string, bool)"/> on the TOML editor
/// path, or by the structured admin form's <c>FormState.ToAuthoring()</c>.
/// Both paths converge on the same model.
/// </remarks>
public class TemplateService
{
    /// <summary>Accepts BC's runtime formats: bare major (<c>15</c>) or Major.Minor (<c>15.2</c>).</summary>
    private static readonly Regex RuntimeFormatRegex = new(@"^\d+(\.\d+)?$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = PersistenceJson.Options;

    /// <summary>
    /// Parses a Runtime string (e.g. <c>"15"</c> or <c>"15.2"</c>) into a sortable tuple.
    /// </summary>
    public static (int Major, int Minor) RuntimeSortKey(string? runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime)) return (-1, -1);
        var parts = runtime.Trim().Split('.', 2);
        var major = int.TryParse(parts[0], out var m) ? m : -1;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        return (major, minor);
    }

    private readonly AppDbContext _db;
    private readonly ILogger<TemplateService> _logger;
    private readonly IOrganizationContext _orgContext;
    private readonly FolderTreeHydrator _folderTree;

    public TemplateService(AppDbContext db, ILogger<TemplateService> logger, IOrganizationContext orgContext, FolderTreeHydrator folderTree)
    {
        _db = db;
        _logger = logger;
        _orgContext = orgContext;
        _folderTree = folderTree;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    // ===== Read side =====

    public async Task<List<RuntimeTemplate>> GetTemplatesAsync(bool includeDeprecated = true, CancellationToken ct = default)
    {
        var query = _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.DeletedAt == null);

        if (!includeDeprecated)
            query = query.Where(t => !t.Deprecated);

        var rows = await query
            .Include(t => t.WorkspaceExtensions.OrderBy(e => e.Ordering))
            .Include(t => t.DefaultModules.OrderBy(d => d.Ordering))
                .ThenInclude(d => d.Module!)
            .Include(t => t.RootFolders.OrderBy(f => f.Ordering))
            .Include(t => t.DefaultApplicationVersion)
            .ToListAsync(ct);
        await AttachIncludedFileHeadersAsync(rows, ct);

        return rows
            .OrderBy(t => RuntimeSortKey(t.Runtime))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<RuntimeTemplate>> GetAllForAdminAsync(bool includeDeleted, CancellationToken ct = default)
    {
        var query = _db.RuntimeTemplates.AsNoTracking();
        if (!includeDeleted)
        {
            query = query.Where(t => t.DeletedAt == null);
        }

        var rows = await query
            .Include(t => t.WorkspaceExtensions.OrderBy(e => e.Ordering))
            .Include(t => t.DefaultModules.OrderBy(d => d.Ordering))
                .ThenInclude(d => d.Module!)
            .Include(t => t.DefaultApplicationVersion)
            .ToListAsync(ct);

        return rows
            .OrderBy(t => t.DeletedAt == null ? 0 : 1)
            .ThenBy(t => RuntimeSortKey(t.Runtime))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<RuntimeTemplate?> GetDefaultAsync(CancellationToken ct = default) =>
        _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.IsDefault && t.DeletedAt == null && !t.Deprecated)
            .FirstOrDefaultAsync(ct);

    public async Task<RuntimeTemplate?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        var template = await _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.Key == key)
            .Include(t => t.WorkspaceExtensions.OrderBy(e => e.Ordering))
            .Include(t => t.DefaultModules.OrderBy(d => d.Ordering))
                .ThenInclude(d => d.Module!)
            // The template detail page previews the workspace this template
            // generates, which needs both of these: the root folders it emits,
            // and the included files, because a file landing inside a declared
            // folder is what tells the preview to show real content there
            // rather than a placeholder. IncludedFiles was missing here, so
            // that preview was also silently dropping app.json.
            .Include(t => t.RootFolders.OrderBy(f => f.Ordering))
            .Include(t => t.DefaultApplicationVersion)
            .FirstOrDefaultAsync(ct);
        if (template is not null) await AttachIncludedFileHeadersAsync([template], ct);
        return template;
    }

    /// <summary>
    /// Fills <see cref="RuntimeTemplate.IncludedFiles"/> with each joined
    /// organisation file's path and scope, leaving its body in the database.
    /// The read side only lists or previews these files; an Include would pull
    /// every ruleset and README body along with them. The generator reads the
    /// bodies through the organisation configuration instead.
    /// </summary>
    private async Task AttachIncludedFileHeadersAsync(IReadOnlyList<RuntimeTemplate> templates, CancellationToken ct)
    {
        if (templates.Count == 0) return;
        var ids = templates.Select(t => t.Id).ToList();
        var joins = await _db.RuntimeTemplateIncludedFiles
            .AsNoTracking()
            .Where(j => ids.Contains(j.RuntimeTemplateId))
            .OrderBy(j => j.Ordering)
            .Select(j => new RuntimeTemplateIncludedFile
            {
                Id = j.Id,
                OrganizationId = j.OrganizationId,
                RuntimeTemplateId = j.RuntimeTemplateId,
                OrganizationFileId = j.OrganizationFileId,
                Ordering = j.Ordering,
                OrganizationFile = new OrganizationFile
                {
                    Id = j.OrganizationFile!.Id,
                    OrganizationId = j.OrganizationFile.OrganizationId,
                    Path = j.OrganizationFile.Path,
                    Scope = j.OrganizationFile.Scope,
                    Ordering = j.OrganizationFile.Ordering,
                },
            })
            .ToListAsync(ct);

        var byTemplate = joins.ToLookup(j => j.RuntimeTemplateId);
        foreach (var template in templates)
        {
            template.IncludedFiles = byTemplate[template.Id].ToList();
        }
    }

    /// <summary>
    /// The keys of the modules a template pre-selects, in its order, leaving
    /// out soft-deleted modules - the same list <c>get_template</c> shows an
    /// agent. An agent that leaves <c>selectedModuleKeys</c> out gets these, as
    /// that parameter promises (#1243). Empty for a template that does not
    /// exist; generation refuses that key on its own.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetDefaultModuleKeysAsync(string templateKey, CancellationToken ct = default) =>
        await _db.RuntimeTemplateDefaultModules
            .AsNoTracking()
            .Where(d => d.Template!.Key == templateKey && d.Template.DeletedAt == null
                        && d.Module!.DeletedAt == null)
            .OrderBy(d => d.Ordering)
            .Select(d => d.Module!.Key)
            .ToListAsync(ct);

    /// <summary>
    /// Loads an existing template into its <see cref="TemplateAuthoring"/>
    /// representation — the same shape the TOML mapper produces — so the
    /// structured admin form binds to a single canonical model regardless of
    /// which surface (form or TOML) the admin opened. Hydrates the recursive
    /// folder/file tree under each extension via flat queries + client-side
    /// reassembly (EF's <c>ThenInclude</c> only recurses two levels).
    /// </summary>
    public async Task<TemplateAuthoring?> GetAuthoringByKeyAsync(string key, CancellationToken ct = default)
    {
        var template = await _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.Key == key)
            .Include(t => t.WorkspaceExtensions.OrderBy(e => e.Ordering))
                .ThenInclude(e => e.Dependencies.OrderBy(d => d.Ordering))
            .Include(t => t.DefaultModules.OrderBy(d => d.Ordering))
                .ThenInclude(d => d.Module!)
            .Include(t => t.IncludedFiles.OrderBy(j => j.Ordering))
                .ThenInclude(j => j.OrganizationFile!)
            .Include(t => t.RootFolders.OrderBy(f => f.Ordering))
            .Include(t => t.DefaultApplicationVersion)
            .FirstOrDefaultAsync(ct);

        if (template is null) return null;

        await _folderTree.HydrateExtensionFolderTreeAsync(new[] { template }, ct);

        var extensions = template.WorkspaceExtensions
            .OrderBy(e => e.Ordering)
            .Select(TemplateAuthoringMapper.BuildExtensionAuthoring)
            .ToList();

        return new TemplateAuthoring(
            Key: template.Key,
            Runtime: template.Runtime,
            Name: template.Name,
            Description: template.Description,
            DefaultsJson: JsonSerializer.Serialize(template.Defaults ?? new TemplateDefaults(), JsonOptions),
            AppSourceCopJson: JsonSerializer.Serialize(template.AppSourceCop ?? new AppSourceCopSettings(), JsonOptions),
            CoreIdRangeFrom: template.CoreIdRangeFrom,
            CoreIdRangeTo: template.CoreIdRangeTo,
            ModuleIdRangeStart: template.ModuleIdRangeStart,
            ModuleIdRangeSize: template.ModuleIdRangeSize,
            Deprecated: template.Deprecated,
            IsDefault: template.IsDefault,
            DefaultApplicationVersionKey: template.DefaultApplicationVersionLatest
                ? null
                : template.DefaultApplicationVersion?.Key,
            // Soft-deleted modules are left out: the editor draws a checkbox
            // only for live modules, so a deleted key here could never be
            // unticked and would fail every save with "Unknown default
            // module(s)" (#1326). The link itself survives the save - see
            // ReconcileDefaultModules - so restoring the module brings it back.
            DefaultModuleKeys: template.DefaultModules
                .OrderBy(d => d.Ordering)
                .Where(d => d.Module is not null && d.Module.DeletedAt == null)
                .Select(d => d.Module!.Key)
                .ToList(),
            Extensions: extensions,
            CodeWorkspaceJson: template.CodeWorkspaceJson,
            IncludedFilePaths: template.IncludedFiles
                .OrderBy(j => j.Ordering)
                .Where(j => j.OrganizationFile is not null)
                .Select(j => j.OrganizationFile!.Path)
                .ToList(),
            DefaultApplicationVersionLatest: template.DefaultApplicationVersionLatest,
            RootFolderPaths: template.RootFolders
                .OrderBy(f => f.Ordering)
                .Select(f => f.Path)
                .ToList());
    }

    public Task<List<Module>> GetModulesAsync(bool includeDeprecated = true, CancellationToken ct = default)
    {
        var query = _db.Modules
            .AsNoTracking()
            .Where(m => m.DeletedAt == null);

        if (!includeDeprecated)
            query = query.Where(m => !m.Deprecated);

        return query
            .OrderBy(m => m.Name)
            .Include(m => m.Dependencies.OrderBy(d => d.Ordering))
            .ToListAsync(ct);
    }

    public Task<List<WellKnownDependency>> GetCatalogAsync(CancellationToken ct = default) =>
        _db.WellKnownDependencies
            .AsNoTracking()
            .OrderBy(w => w.Category)
            .ThenBy(w => w.Ordering)
            .ThenBy(w => w.DepName)
            .ToListAsync(ct);

    // ===== Write side =====

    /// <summary>
    /// Creates a new runtime template from a TOML authoring payload.
    /// Validation errors are thrown as <see cref="PlanValidationException"/>
    /// with field-keyed messages.
    /// </summary>
    public async Task<RuntimeTemplate> CreateAsync(TemplateAuthoring input, CancellationToken ct = default)
    {
        var (defaults, appSourceCop, defaultModuleIds, appVersionId, includedFileIds) =
            await ValidateAsync(input, existingId: null, savedModuleKeys: NoModuleKeys, ct);

        // Auto-include the canonical app.json for every new template so the
        // admin doesn't have to remember to tick the box. Resolves by path
        // against the seeded org files; if the org somehow doesn't have one
        // (legacy DBs that pre-date the seeder + migration), skip silently —
        // the admin can opt in once the row exists.
        includedFileIds = await EnsureAppJsonFirstAsync(includedFileIds, ct);

        var now = DateTime.UtcNow;
        var orgId = RequireOrganizationId();
        var template = new RuntimeTemplate
        {
            OrganizationId = orgId,
            Key = input.Key.Trim(),
            Runtime = input.Runtime.Trim(),
            Name = input.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            Defaults = defaults,
            AppSourceCop = appSourceCop,
            CodeWorkspaceJson = TemplateAuthoringMapper.NormaliseCodeWorkspaceJson(input.CodeWorkspaceJson),
            CoreIdRangeFrom = input.CoreIdRangeFrom,
            CoreIdRangeTo = input.CoreIdRangeTo,
            ModuleIdRangeStart = input.ModuleIdRangeStart,
            ModuleIdRangeSize = input.ModuleIdRangeSize,
            Deprecated = input.Deprecated,
            DefaultApplicationVersionId = appVersionId,
            DefaultApplicationVersionLatest = input.DefaultApplicationVersionLatest,
            CreatedAt = now,
            UpdatedAt = now,
            WorkspaceExtensions = input.Extensions
                .Select((e, i) => TemplateAuthoringMapper.BuildExtension(e, orgId, ordering: i))
                .ToList(),
            DefaultModules = defaultModuleIds
                .Select((moduleId, i) => new RuntimeTemplateDefaultModule
                {
                    OrganizationId = orgId,
                    ModuleId = moduleId,
                    Ordering = i,
                })
                .ToList(),
            IncludedFiles = includedFileIds
                .Select((fileId, i) => new RuntimeTemplateIncludedFile
                {
                    OrganizationId = orgId,
                    OrganizationFileId = fileId,
                    Ordering = i,
                })
                .ToList(),
            RootFolders = NormaliseRootFolderPaths(input)
                .Select((path, i) => new RuntimeTemplateRootFolder
                {
                    OrganizationId = orgId,
                    Path = path,
                    Ordering = i,
                })
                .ToList(),
        };

        _db.RuntimeTemplates.Add(template);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created runtime template '{Key}' (id={Id}) with {Extensions} extension(s).",
            template.Key, template.Id, template.WorkspaceExtensions.Count);
        return template;
    }

    /// <summary>
    /// Updates an existing template. Extensions, their folder trees, files,
    /// and dependencies are rebuilt from the authoring payload: the existing
    /// child rows are cascade-deleted and fresh rows take their place. Stable
    /// rows on the parent template (key, deprecated, default-modules join)
    /// are reconciled in place so their primary keys survive — the audit log
    /// stays compact for unchanged metadata edits.
    /// </summary>
    public async Task UpdateAsync(int id, TemplateAuthoring input, CancellationToken ct = default)
    {
        var existing = await _db.RuntimeTemplates
            .Include(t => t.WorkspaceExtensions)
                .ThenInclude(e => e.Dependencies)
            .Include(t => t.DefaultModules)
            .Include(t => t.IncludedFiles)
            .Include(t => t.RootFolders)
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Template with id {id} was not found.",
            });

        // Key is immutable after creation — ignore whatever the input carries.
        var validatable = input with { Key = existing.Key };
        // Module keys the template already depends on: a module deleted since
        // they were saved must not stop the template being saved again (#1342).
        var savedModuleKeys = existing.WorkspaceExtensions
            .SelectMany(e => e.Dependencies)
            .Where(d => d.RefModuleKey is not null)
            .Select(d => d.RefModuleKey!)
            .ToHashSet(StringComparer.Ordinal);
        var (defaults, appSourceCop, defaultModuleIds, appVersionId, includedFileIds) =
            await ValidateAsync(validatable, existingId: id, savedModuleKeys, ct);

        existing.Runtime = input.Runtime.Trim();
        existing.Name = input.Name.Trim();
        existing.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        existing.Defaults = defaults;
        existing.AppSourceCop = appSourceCop;
        existing.CodeWorkspaceJson = TemplateAuthoringMapper.NormaliseCodeWorkspaceJson(input.CodeWorkspaceJson);
        existing.CoreIdRangeFrom = input.CoreIdRangeFrom;
        existing.CoreIdRangeTo = input.CoreIdRangeTo;
        existing.ModuleIdRangeStart = input.ModuleIdRangeStart;
        existing.ModuleIdRangeSize = input.ModuleIdRangeSize;
        existing.Deprecated = input.Deprecated;
        existing.DefaultApplicationVersionId = appVersionId;
        existing.DefaultApplicationVersion = null;
        existing.DefaultApplicationVersionLatest = input.DefaultApplicationVersionLatest;
        existing.UpdatedAt = DateTime.UtcNow;

        // Extensions: clear-and-rebuild. The cascade FK on workspace_extensions
        // drops all dependent folder / file / dep rows automatically. A
        // path-keyed reconciliation pass (preserving extension primary keys
        // for unchanged rows) is a possible refinement once the audit log
        // pattern around recursive trees firms up — for now, simplicity wins.
        existing.WorkspaceExtensions.Clear();
        var orgId = existing.OrganizationId;
        var fresh = input.Extensions
            .Select((e, i) => TemplateAuthoringMapper.BuildExtension(e, orgId, ordering: i))
            .ToList();
        foreach (var ext in fresh) existing.WorkspaceExtensions.Add(ext);

        var retiredModuleIds = await RetiredDefaultModuleIdsAsync(existing, ct);
        ReconcileDefaultModules(existing, defaultModuleIds, orgId, retiredModuleIds);
        ReconcileIncludedFiles(existing, includedFileIds, orgId);
        ReconcileRootFolders(existing, NormaliseRootFolderPaths(input), orgId);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Updated runtime template '{Key}' (id={Id}); now has {Extensions} extension(s).",
            existing.Key, existing.Id, existing.WorkspaceExtensions.Count);
    }

    /// <summary>
    /// Reconciles the join rows for the per-org default-modules list. Matches
    /// by <see cref="RuntimeTemplateDefaultModule.ModuleId"/> rather than by
    /// list position: each row's natural identity is the module it points
    /// at, so reordering the input list only rewrites <c>Ordering</c> and
    /// the join row primary keys survive. Crucially this avoids the swap
    /// cycle — mutating two rows' <c>module_id</c> in one batch would trip
    /// the <c>(runtime_template_id, module_id)</c> unique index, which EF's
    /// command-batcher detects and refuses to topologically sort.
    ///
    /// <para>Links to soft-deleted modules (<paramref name="retiredModuleIds"/>)
    /// are kept, ordered after the live ones: the authoring model leaves them
    /// out because the editor cannot show them (#1326), so their absence from
    /// the input does not mean the editor unticked them. Keeping the link means
    /// restoring the module makes it a default of this template again, as it
    /// was before it was deleted.</para>
    /// </summary>
    private static void ReconcileDefaultModules(
        RuntimeTemplate existing, IReadOnlyList<int> moduleIds, int orgId, IReadOnlySet<int> retiredModuleIds)
    {
        var existingByModuleId = existing.DefaultModules.ToDictionary(d => d.ModuleId);

        for (var i = 0; i < moduleIds.Count; i++)
        {
            var moduleId = moduleIds[i];
            if (existingByModuleId.TryGetValue(moduleId, out var row))
            {
                // Keep the PK; rewrite ordering only when it actually changed.
                if (row.Ordering != i) row.Ordering = i;
                row.Module = null;
            }
            else
            {
                existing.DefaultModules.Add(new RuntimeTemplateDefaultModule
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    ModuleId = moduleId,
                });
            }
        }

        // Retired links stay, after the live ones, in their previous order.
        var keep = new HashSet<int>(moduleIds);
        var ordering = moduleIds.Count;
        foreach (var row in existing.DefaultModules
                     .Where(d => !keep.Contains(d.ModuleId) && retiredModuleIds.Contains(d.ModuleId))
                     .OrderBy(d => d.Ordering)
                     .ToList())
        {
            if (row.Ordering != ordering) row.Ordering = ordering;
            ordering++;
            keep.Add(row.ModuleId);
        }

        // Remove rows whose ModuleId fell off the input list.
        var toRemove = existing.DefaultModules.Where(d => !keep.Contains(d.ModuleId)).ToList();
        foreach (var row in toRemove)
        {
            existing.DefaultModules.Remove(row);
        }
    }

    /// <summary>
    /// The ids of the soft-deleted modules this template still links as
    /// defaults. Read separately rather than included on the tracked template,
    /// so the reconcile pass never holds a tracked module it could orphan.
    /// </summary>
    private async Task<IReadOnlySet<int>> RetiredDefaultModuleIdsAsync(RuntimeTemplate existing, CancellationToken ct)
    {
        var linkedIds = existing.DefaultModules.Select(d => d.ModuleId).ToList();
        if (linkedIds.Count == 0) return new HashSet<int>();
        var retired = await _db.Modules
            .AsNoTracking()
            .Where(m => linkedIds.Contains(m.Id) && m.DeletedAt != null)
            .Select(m => m.Id)
            .ToListAsync(ct);
        return retired.ToHashSet();
    }

    /// <summary>
    /// Mirror of <see cref="ReconcileDefaultModules"/> for the per-template
    /// always-included file join. Matches by <c>OrganizationFileId</c> so the
    /// admin can reorder without churning primary keys; the unique index on
    /// <c>(runtime_template_id, organization_file_id)</c> prevents the swap-cycle
    /// case the same way.
    /// </summary>
    private static void ReconcileIncludedFiles(RuntimeTemplate existing, IReadOnlyList<int> fileIds, int orgId)
    {
        var existingByFileId = existing.IncludedFiles.ToDictionary(d => d.OrganizationFileId);

        for (var i = 0; i < fileIds.Count; i++)
        {
            var fileId = fileIds[i];
            if (existingByFileId.TryGetValue(fileId, out var row))
            {
                if (row.Ordering != i) row.Ordering = i;
                row.OrganizationFile = null;
            }
            else
            {
                existing.IncludedFiles.Add(new RuntimeTemplateIncludedFile
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    OrganizationFileId = fileId,
                });
            }
        }

        var keep = new HashSet<int>(fileIds);
        var toRemove = existing.IncludedFiles.Where(d => !keep.Contains(d.OrganizationFileId)).ToList();
        foreach (var row in toRemove)
        {
            existing.IncludedFiles.Remove(row);
        }
    }

    /// <summary>
    /// Mirror of <see cref="ReconcileIncludedFiles"/> for the template's empty
    /// root folders. Matches by <see cref="RuntimeTemplateRootFolder.Path"/>,
    /// which is the row's natural identity, so reordering the list rewrites
    /// only <c>Ordering</c> and leaves the primary keys alone.
    /// </summary>
    private static void ReconcileRootFolders(RuntimeTemplate existing, IReadOnlyList<string> paths, int orgId)
    {
        var existingByPath = existing.RootFolders
            .ToDictionary(f => f.Path, StringComparer.Ordinal);

        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            if (existingByPath.TryGetValue(path, out var row))
            {
                if (row.Ordering != i) row.Ordering = i;
            }
            else
            {
                existing.RootFolders.Add(new RuntimeTemplateRootFolder
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    Path = path,
                });
            }
        }

        var keep = new HashSet<string>(paths, StringComparer.Ordinal);
        var toRemove = existing.RootFolders.Where(f => !keep.Contains(f.Path)).ToList();
        foreach (var row in toRemove)
        {
            existing.RootFolders.Remove(row);
        }
    }

    /// <summary>
    /// Trims and drops blanks from the authoring payload's root-folder list.
    /// The validator has already rejected anything malformed by the time this
    /// runs; a blank row is what an admin leaves behind after clearing an
    /// input they no longer want, so it is dropped rather than refused.
    /// </summary>
    private static List<string> NormaliseRootFolderPaths(TemplateAuthoring input) =>
        (input.RootFolderPaths ?? Array.Empty<string>())
            .Select(p => p?.Trim() ?? string.Empty)
            .Where(p => p.Length > 0)
            .ToList();

    // ===== Default ops =====

    public async Task SetDefaultAsync(int id, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var target = await _db.RuntimeTemplates.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Template with id {id} was not found.",
            });

        if (target.DeletedAt is not null)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = "A soft-deleted template cannot be the default. Restore it first.",
            });
        }
        if (target.Deprecated)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = "A deprecated template cannot be the default. Un-deprecate it first.",
            });
        }

        if (target.IsDefault) return;

        // Two-phase update so the filtered unique index never sees two true rows.
        var previous = await _db.RuntimeTemplates
            .Where(t => t.OrganizationId == orgId && t.IsDefault)
            .ToListAsync(ct);
        if (previous.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var row in previous)
            {
                row.IsDefault = false;
                row.UpdatedAt = now;
            }
            await _db.SaveChangesAsync(ct);
        }

        target.IsDefault = true;
        target.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Set default runtime template to '{Key}' (id={Id}) for org {OrgId}.",
            target.Key, target.Id, orgId);
    }

    public async Task SoftDeleteAsync(int id, CancellationToken ct = default)
    {
        var existing = await _db.RuntimeTemplates.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Template with id {id} was not found.",
            });

        if (existing.DeletedAt is not null) return;

        existing.DeletedAt = DateTime.UtcNow;
        existing.UpdatedAt = existing.DeletedAt.Value;
        existing.IsDefault = false;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Soft-deleted runtime template '{Key}' (id={Id}).", existing.Key, existing.Id);
    }

    public async Task RestoreAsync(int id, CancellationToken ct = default)
    {
        var existing = await _db.RuntimeTemplates.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Template with id {id} was not found.",
            });

        if (existing.DeletedAt is null) return;

        existing.DeletedAt = null;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Restored runtime template '{Key}' (id={Id}).", existing.Key, existing.Id);
    }

    public Task<BulkActionResult> BulkSoftDeleteAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        => BulkMutateAsync(ids, t =>
        {
            if (t.DeletedAt is not null) return false;
            t.DeletedAt = DateTime.UtcNow;
            t.UpdatedAt = t.DeletedAt.Value;
            t.IsDefault = false;
            return true;
        }, "soft-delete", ct);

    public Task<BulkActionResult> BulkRestoreAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        => BulkMutateAsync(ids, t =>
        {
            if (t.DeletedAt is null) return false;
            t.DeletedAt = null;
            t.UpdatedAt = DateTime.UtcNow;
            return true;
        }, "restore", ct);

    public Task<BulkActionResult> BulkDeprecateAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        => BulkMutateAsync(ids, t =>
        {
            if (t.Deprecated) return false;
            t.Deprecated = true;
            t.UpdatedAt = DateTime.UtcNow;
            t.IsDefault = false;
            return true;
        }, "deprecate", ct);

    public Task<BulkActionResult> BulkUnDeprecateAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        => BulkMutateAsync(ids, t =>
        {
            if (!t.Deprecated) return false;
            t.Deprecated = false;
            t.UpdatedAt = DateTime.UtcNow;
            return true;
        }, "un-deprecate", ct);

    private async Task<BulkActionResult> BulkMutateAsync(
        IReadOnlyList<int> ids,
        Func<RuntimeTemplate, bool> mutate,
        string actionLabel,
        CancellationToken ct)
    {
        RequireOrganizationId();
        var succeeded = new List<int>();
        var failures = new List<BulkActionFailure>();
        var distinctIds = ids.Distinct().ToList();
        var rows = await _db.RuntimeTemplates
            .Where(t => distinctIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);
        foreach (var id in distinctIds)
        {
            if (!rows.TryGetValue(id, out var row))
            {
                failures.Add(new BulkActionFailure(id, $"#{id}", "Not found in this organisation."));
                continue;
            }
            try
            {
                if (!mutate(row))
                {
                    succeeded.Add(id);
                    continue;
                }
                await _db.SaveChangesAsync(ct);
                succeeded.Add(id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bulk {Action} failed for template id={Id}.", actionLabel, id);
                failures.Add(new BulkActionFailure(id, row.Name, ex.Message));
                _db.Entry(row).State = EntityState.Detached;
            }
        }
        _logger.LogInformation(
            "Bulk {Action} on templates: {Ok}/{Total} succeeded.",
            actionLabel, succeeded.Count, ids.Count);
        return new BulkActionResult(ids.Count, succeeded, failures);
    }

    // ===== Validation =====

    /// <summary>
    /// Validates the authoring payload. Returns the parsed value objects and
    /// resolved FK ids so the caller doesn't re-deserialise. Throws an
    /// aggregated <see cref="PlanValidationException"/> with one entry per
    /// problem so the editor can highlight everything at once.
    /// </summary>
    private async Task<(TemplateDefaults Defaults, AppSourceCopSettings AppSourceCop, IReadOnlyList<int> DefaultModuleIds, int? DefaultApplicationVersionId, IReadOnlyList<int> IncludedFileIds)>
        ValidateAsync(TemplateAuthoring input, int? existingId, IReadOnlySet<string> savedModuleKeys, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        await ValidateMetadataAsync(input, existingId, errors, ct);
        var (defaults, appSourceCop) = TemplateValidation.ParseJsonOverrides(input, errors);
        TemplateValidation.ValidateExtensions(input.Extensions, errors);
        await ValidateDependencyModuleKeysAsync(input, savedModuleKeys, errors, ct);
        var defaultModuleIds = await ResolveDefaultModuleIdsAsync(input, errors, ct);
        var defaultApplicationVersionId = await ResolveApplicationVersionIdAsync(input, errors, ct);
        var (includedFileIds, workspaceRootPaths) = await ResolveIncludedFileIdsAsync(input, errors, ct);
        // The root-folder rules need to know which included files land at the
        // workspace root, because a file there fills a folder the template
        // would otherwise be declaring as empty.
        TemplateValidation.ValidateRootFolders(
            input.RootFolderPaths ?? Array.Empty<string>(), input.Extensions, workspaceRootPaths, errors);

        if (errors.Count > 0) throw new PlanValidationException(errors);
        return (defaults, appSourceCop, defaultModuleIds, defaultApplicationVersionId, includedFileIds);
    }

    /// <summary>
    /// Resolves the per-template always-included file paths to organisation
    /// file ids. Unknown paths land in <paramref name="errors"/>; preserves
    /// the caller's order (deduplicated by path). Also returns the
    /// workspace-root-scoped subset of those paths, which the root-folder
    /// rules need — a per-extension-scoped row lands inside an extension
    /// folder and so can never fill a workspace-root folder.
    /// </summary>
    private async Task<(IReadOnlyList<int> Ids, IReadOnlyList<string> WorkspaceRootPaths)> ResolveIncludedFileIdsAsync(
        TemplateAuthoring input, Dictionary<string, string> errors, CancellationToken ct)
    {
        var empty = (Array.Empty<int>(), (IReadOnlyList<string>)Array.Empty<string>());
        var paths = input.IncludedFilePaths;
        if (paths is null || paths.Count == 0) return empty;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var orderedUnique = new List<string>();
        foreach (var p in paths)
        {
            var trimmed = p?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed)) orderedUnique.Add(trimmed);
        }
        if (orderedUnique.Count == 0) return empty;

        var matched = await _db.OrganizationFiles
            .AsNoTracking()
            .Where(f => orderedUnique.Contains(f.Path))
            .Select(f => new { f.Path, f.Id, f.Scope })
            .ToListAsync(ct);
        var idByPath = matched.ToDictionary(m => m.Path, m => m.Id, StringComparer.Ordinal);

        var missing = orderedUnique.Where(p => !idByPath.ContainsKey(p)).ToList();
        if (missing.Count > 0)
        {
            errors[nameof(input.IncludedFilePaths)] = $"Unknown included file(s): {string.Join(", ", missing)}.";
            return empty;
        }
        var workspaceRootPaths = matched
            .Where(m => m.Scope == OrganizationFileScope.WorkspaceRoot)
            .Select(m => m.Path)
            .ToList();
        return (orderedUnique.Select(p => idByPath[p]).ToList(), workspaceRootPaths);
    }

    /// <summary>
    /// Key/runtime/name/id-range validation, plus the per-org Key uniqueness
    /// guard. The Key clash check is the only DB hit here; the rest are pure.
    /// </summary>
    private async Task ValidateMetadataAsync(
        TemplateAuthoring input, int? existingId, Dictionary<string, string> errors, CancellationToken ct)
    {
        var key = input.Key?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(key))
        {
            errors[nameof(input.Key)] = "Key is required.";
        }
        else if (!ValidationPatterns.Key.IsMatch(key))
        {
            errors[nameof(input.Key)] = "Key must contain only lowercase letters, digits, and hyphens.";
        }
        else
        {
            var clash = await _db.RuntimeTemplates
                .AsNoTracking()
                .Where(t => t.Key == key)
                .Select(t => (int?)t.Id)
                .FirstOrDefaultAsync(ct);
            if (clash is not null && clash != existingId)
            {
                errors[nameof(input.Key)] = $"A template with key '{key}' already exists.";
            }
        }

        if (string.IsNullOrWhiteSpace(input.Runtime))
        {
            errors[nameof(input.Runtime)] = "Runtime is required.";
        }
        else if (!RuntimeFormatRegex.IsMatch(input.Runtime.Trim()))
        {
            errors[nameof(input.Runtime)] = "Runtime must be a number, optionally with one minor part (e.g. 15 or 15.2).";
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            errors[nameof(input.Name)] = "Name is required.";
        }

        if (input.CoreIdRangeFrom <= 0) errors[nameof(input.CoreIdRangeFrom)] = "Core ID range start must be greater than zero.";
        if (input.CoreIdRangeTo <= input.CoreIdRangeFrom) errors[nameof(input.CoreIdRangeTo)] = "Core ID range end must be greater than the start.";
        if (input.ModuleIdRangeStart <= 0) errors[nameof(input.ModuleIdRangeStart)] = "Module ID range start must be greater than zero.";
        if (input.ModuleIdRangeSize <= 0) errors[nameof(input.ModuleIdRangeSize)] = "Module ID range size must be greater than zero.";
    }

    private static readonly IReadOnlySet<string> NoModuleKeys = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Refuses a <c>module =</c> dependency whose key is new in this save and
    /// names no live module, so a typo is caught where it was typed rather than
    /// vanishing from every generated app.json (#1342). A key the template
    /// already had is left alone even when its module has since been deleted:
    /// generation drops it like an unticked module, and refusing it would stop
    /// the template being saved at all, which is the #1326 trap. Keys are
    /// compared as a set, not by row position, so reordering is not "new".
    /// Only dependencies that set the module shape alone are checked; the
    /// shape rules in <see cref="TemplateValidation"/> report the rest.
    /// </summary>
    private async Task ValidateDependencyModuleKeysAsync(
        TemplateAuthoring input, IReadOnlySet<string> savedModuleKeys,
        Dictionary<string, string> errors, CancellationToken ct)
    {
        var refs = input.Extensions
            .SelectMany((ext, i) => ext.Dependencies.Select((dep, j) => (Ext: i, Dep: j, dep)))
            .Where(r => r.dep.RefModuleKey is not null && r.dep.RefExtensionPath is null && r.dep.LitId is null)
            .Select(r => (r.Ext, r.Dep, Key: r.dep.RefModuleKey!.Trim()))
            .ToList();

        foreach (var (ext, dep, _) in refs.Where(r => r.Key.Length == 0))
        {
            errors[$"Extensions[{ext}].Dependencies[{dep}].Module"] =
                "Enter the key of the module this extension depends on, or remove the dependency.";
        }

        var added = refs.Where(r => r.Key.Length > 0 && !savedModuleKeys.Contains(r.Key)).ToList();
        if (added.Count == 0) return;

        var keys = added.Select(r => r.Key).Distinct(StringComparer.Ordinal).ToList();
        var live = await _db.Modules
            .AsNoTracking()
            .Where(m => keys.Contains(m.Key) && m.DeletedAt == null)
            .Select(m => m.Key)
            .ToListAsync(ct);
        var liveKeys = new HashSet<string>(live, StringComparer.Ordinal);

        foreach (var (ext, dep, key) in added.Where(r => !liveKeys.Contains(r.Key)))
        {
            errors[$"Extensions[{ext}].Dependencies[{dep}].Module"] =
                $"No module with the key '{key}' exists. Check the spelling of the key you added, or pick a module from the catalogue.";
        }
    }

    /// <summary>
    /// Resolves the requested default-module keys to ids. Unknown / soft-
    /// deleted keys land in <paramref name="errors"/>; on success the returned
    /// list preserves the caller's order (deduplicated).
    /// </summary>
    private async Task<IReadOnlyList<int>> ResolveDefaultModuleIdsAsync(
        TemplateAuthoring input, Dictionary<string, string> errors, CancellationToken ct)
    {
        if (input.DefaultModuleKeys.Count == 0) return Array.Empty<int>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var orderedUnique = new List<string>();
        foreach (var k in input.DefaultModuleKeys)
        {
            var trimmed = k?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed)) orderedUnique.Add(trimmed);
        }
        if (orderedUnique.Count == 0) return Array.Empty<int>();

        var matched = await _db.Modules
            .AsNoTracking()
            .Where(m => orderedUnique.Contains(m.Key) && m.DeletedAt == null)
            .Select(m => new { m.Key, m.Id })
            .ToListAsync(ct);
        var idByKey = matched.ToDictionary(m => m.Key, m => m.Id, StringComparer.Ordinal);

        var missing = orderedUnique.Where(k => !idByKey.ContainsKey(k)).ToList();
        if (missing.Count > 0)
        {
            errors[nameof(input.DefaultModuleKeys)] = $"Unknown default module(s): {string.Join(", ", missing)}.";
            return Array.Empty<int>();
        }
        return orderedUnique.Select(k => idByKey[k]).ToList();
    }

    /// <summary>
    /// Prepends the org's canonical <c>app.json</c> organisation file to the
    /// resolved include-file list when it isn't already present. Lets fresh
    /// templates emit an <c>app.json</c> by default without forcing admins
    /// to remember to tick the box — they can still untick it later if they
    /// don't want one. Idempotent on already-included rows.
    /// </summary>
    private async Task<IReadOnlyList<int>> EnsureAppJsonFirstAsync(
        IReadOnlyList<int> includedFileIds, CancellationToken ct)
    {
        var orgId = RequireOrganizationId();
        var appJsonId = await _db.OrganizationFiles
            .AsNoTracking()
            .Where(f => f.OrganizationId == orgId && f.Path == PlatformOrganizationFiles.AppJsonPath)
            .Select(f => (int?)f.Id)
            .FirstOrDefaultAsync(ct);
        if (appJsonId is null) return includedFileIds;
        if (includedFileIds.Contains(appJsonId.Value)) return includedFileIds;
        var combined = new List<int>(includedFileIds.Count + 1) { appJsonId.Value };
        combined.AddRange(includedFileIds);
        return combined;
    }

    /// <summary>
    /// Resolves the optional default application-version key to an id. Empty
    /// returns null; an unknown / soft-deleted key lands in <paramref name="errors"/>.
    /// </summary>
    private async Task<int?> ResolveApplicationVersionIdAsync(
        TemplateAuthoring input, Dictionary<string, string> errors, CancellationToken ct)
    {
        // "Latest" sentinel: the FK stays null and the bool flag on the
        // entity drives the pre-fill at form-load time. Accept either form
        // (explicit bool, or the sentinel string in the key slot) so the
        // structured form, TOML, and direct API callers can all express it.
        if (input.DefaultApplicationVersionLatest
            || string.Equals(input.DefaultApplicationVersionKey?.Trim(), ApplicationVersionService.LatestSentinel, StringComparison.Ordinal))
        {
            return null;
        }

        var versionKey = input.DefaultApplicationVersionKey?.Trim();
        if (string.IsNullOrEmpty(versionKey)) return null;

        var resolved = await _db.ApplicationVersions
            .AsNoTracking()
            .Where(a => a.Key == versionKey && a.DeletedAt == null)
            .Select(a => (int?)a.Id)
            .FirstOrDefaultAsync(ct);
        if (resolved is null)
        {
            errors[nameof(input.DefaultApplicationVersionKey)] = $"Unknown application version '{versionKey}'.";
            return null;
        }
        return resolved;
    }

}
