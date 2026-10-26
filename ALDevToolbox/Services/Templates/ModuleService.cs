using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Templates;

/// <summary>
/// Read- and write-side service for modules. Mirrors <see cref="TemplateService"/>
/// in shape: validation throws <see cref="PlanValidationException"/> with
/// field-keyed messages, soft-delete via <see cref="Module.DeletedAt"/>, and
/// per-row reconciliation of the <see cref="ModuleDependency"/> child collection
/// so the audit log only captures real changes.
/// </summary>
public class ModuleService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ModuleService> _logger;
    private readonly IOrganizationContext _orgContext;
    private readonly FolderTreeHydrator _folderTree;

    public ModuleService(AppDbContext db, ILogger<ModuleService> logger, IOrganizationContext orgContext, FolderTreeHydrator folderTree)
    {
        _db = db;
        _logger = logger;
        _orgContext = orgContext;
        _folderTree = folderTree;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    /// <summary>
    /// Returns every module, including deprecated and soft-deleted ones, ordered
    /// with active rows first. Drives the admin list view.
    /// </summary>
    public Task<List<Module>> GetAllForAdminAsync(bool includeDeleted, CancellationToken ct = default)
    {
        var query = _db.Modules.AsNoTracking();
        if (!includeDeleted)
        {
            query = query.Where(m => m.DeletedAt == null);
        }

        return query
            .OrderBy(m => m.DeletedAt == null ? 0 : 1)
            .ThenBy(m => m.Name)
            .Include(m => m.Dependencies.OrderBy(d => d.Ordering))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns a single module by its <see cref="Module.Key"/>, including
    /// soft-deleted rows so admin pages can render and restore them. The
    /// recursive <see cref="Module.ExtensionFolders"/> tree is hydrated so the
    /// editor can render and round-trip the module's folder/file layout.
    /// </summary>
    public async Task<Module?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        var module = await _db.Modules
            .AsNoTracking()
            .Where(m => m.Key == key)
            .Include(m => m.Dependencies.OrderBy(d => d.Ordering))
            .FirstOrDefaultAsync(ct);

        if (module is not null)
        {
            await _folderTree.HydrateModuleExtensionFolderTreeAsync(new[] { module }, ct);
        }

        return module;
    }

    /// <summary>
    /// Creates a new module plus its dependencies.
    /// </summary>
    public async Task<Module> CreateAsync(ModuleInput input, CancellationToken ct = default)
    {
        await ValidateAsync(input, existingId: null, ct);

        var now = DateTime.UtcNow;
        var orgId = RequireOrganizationId();
        var module = new Module
        {
            OrganizationId = orgId,
            Key = input.Key.Trim(),
            Name = input.Name.Trim(),
            ExtensionName = input.ExtensionName.Trim(),
            IdRangeSize = input.IdRangeSize,
            Deprecated = input.Deprecated,
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = null,
            Dependencies = input.Dependencies
                .Select((d, i) => new ModuleDependency
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    DepId = d.DepId.Trim(),
                    DepName = d.DepName.Trim(),
                    DepPublisher = d.DepPublisher.Trim(),
                    DepVersion = d.DepVersion.Trim(),
                })
                .ToList(),
            ExtensionFolders = input.Folders
                .Select((f, i) => ModuleAuthoringMapper.BuildFolder(f, orgId, ordering: i))
                .ToList(),
        };
        ApplySource(module, input.Source);

        _db.Modules.Add(module);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created module '{Key}' (id={Id}) with {DepCount} dependency(ies) and {FolderCount} top-level folder(s).",
            module.Key, module.Id, module.Dependencies.Count, module.ExtensionFolders.Count);
        return module;
    }

    /// <summary>
    /// Updates an existing module's fields and reconciles its dependency list.
    /// The <see cref="Module.Key"/> is immutable after creation; the
    /// caller-supplied key on <paramref name="input"/> is ignored.
    /// </summary>
    public async Task UpdateAsync(int id, ModuleInput input, CancellationToken ct = default)
    {
        var existing = await _db.Modules
            .Include(m => m.Dependencies)
            .Include(m => m.ExtensionFolders)
            .FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Module with id {id} was not found.",
            });

        var validatableInput = input with { Key = existing.Key };
        await ValidateAsync(validatableInput, existingId: id, ct);

        existing.Name = input.Name.Trim();
        existing.ExtensionName = input.ExtensionName.Trim();
        existing.IdRangeSize = input.IdRangeSize;
        existing.Deprecated = input.Deprecated;
        existing.UpdatedAt = DateTime.UtcNow;
        ApplySource(existing, input.Source);

        ReconcileDependencies(existing, input.Dependencies, existing.OrganizationId);

        // Folder tree: clear-and-rebuild, in two saves inside one transaction.
        // The Module -> folders relationship is keyed on module_id and covers
        // every row in the tree (flat), so clearing the collection deletes the
        // whole tree, audited row by row. EF does not order those deletes ahead
        // of the inserts under the filtered unique index on root folder names, so
        // a single save of a tree that keeps a root folder's name (every
        // repository refresh, #1415) collided with itself. The denormalised
        // module_id on nested rows is re-stamped at save time by AppDbContext.
        await using var tx = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;
        existing.ExtensionFolders.Clear();
        await _db.SaveChangesAsync(ct);

        var freshFolders = input.Folders
            .Select((f, i) => ModuleAuthoringMapper.BuildFolder(f, existing.OrganizationId, ordering: i))
            .ToList();
        foreach (var folder in freshFolders) existing.ExtensionFolders.Add(folder);
        await _db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Updated module '{Key}' (id={Id}); now has {DepCount} dependency(ies) and {FolderCount} top-level folder(s).",
            existing.Key, existing.Id, existing.Dependencies.Count, existing.ExtensionFolders.Count);
    }

    /// <summary>
    /// Soft-deletes a module by setting <see cref="Module.DeletedAt"/>.
    /// </summary>
    public async Task SoftDeleteAsync(int id, CancellationToken ct = default)
    {
        var existing = await _db.Modules.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Module with id {id} was not found.",
            });

        if (existing.DeletedAt is not null)
        {
            return;
        }

        existing.DeletedAt = DateTime.UtcNow;
        existing.UpdatedAt = existing.DeletedAt.Value;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Soft-deleted module '{Key}' (id={Id}).", existing.Key, existing.Id);
    }

    /// <summary>
    /// Clears <see cref="Module.DeletedAt"/> on a previously soft-deleted module.
    /// </summary>
    public async Task RestoreAsync(int id, CancellationToken ct = default)
    {
        var existing = await _db.Modules.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Module with id {id} was not found.",
            });

        if (existing.DeletedAt is null)
        {
            return;
        }

        existing.DeletedAt = null;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Restored module '{Key}' (id={Id}).", existing.Key, existing.Id);
    }

    /// <summary>Bulk variant of <see cref="SoftDeleteAsync"/>.</summary>
    public Task<BulkActionResult> BulkSoftDeleteAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        => BulkMutateAsync(ids, m =>
        {
            if (m.DeletedAt is not null) return false;
            m.DeletedAt = DateTime.UtcNow;
            m.UpdatedAt = m.DeletedAt.Value;
            return true;
        }, "soft-delete", ct);

    /// <summary>Bulk variant of <see cref="RestoreAsync"/>.</summary>
    public Task<BulkActionResult> BulkRestoreAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        => BulkMutateAsync(ids, m =>
        {
            if (m.DeletedAt is null) return false;
            m.DeletedAt = null;
            m.UpdatedAt = DateTime.UtcNow;
            return true;
        }, "restore", ct);

    private async Task<BulkActionResult> BulkMutateAsync(
        IReadOnlyList<int> ids,
        Func<Module, bool> mutate,
        string actionLabel,
        CancellationToken ct)
    {
        RequireOrganizationId();
        var succeeded = new List<int>();
        var failures = new List<BulkActionFailure>();
        var distinctIds = ids.Distinct().ToList();
        var rows = await _db.Modules
            .Where(m => distinctIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, ct);
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
                _logger.LogWarning(ex, "Bulk {Action} failed for module id={Id}.", actionLabel, id);
                failures.Add(new BulkActionFailure(id, row.Name, ex.Message));
                _db.Entry(row).State = EntityState.Detached;
            }
        }
        _logger.LogInformation(
            "Bulk {Action} on modules: {Ok}/{Total} succeeded.",
            actionLabel, succeeded.Count, ids.Count);
        return new BulkActionResult(ids.Count, succeeded, failures);
    }

    /// <summary>
    /// Stamps (or, for null, clears) the repository source columns. A copy that
    /// left nothing out and found no dependencies stores null rather than an empty
    /// list, so a typed module and a repository module read the same when empty.
    /// </summary>
    private static void ApplySource(Module module, ModuleSourceState? source)
    {
        module.SourceRepository = source?.Repository;
        module.SourceRef = source?.Ref;
        module.SourcePath = source?.Path;
        module.SourceCommitSha = source?.CommitSha;
        module.SourceFetchedAt = source?.FetchedAt;
        module.SourceCheckedAt = source?.FetchedAt;
        module.SourceError = null;
        module.SourceSkippedJson = source is { Skipped.Count: > 0 }
            ? JsonSerializer.Serialize(source.Skipped, PersistenceJson.Options)
            : null;
        module.SourceDependenciesJson = source is { Dependencies.Count: > 0 }
            ? JsonSerializer.Serialize(source.Dependencies, PersistenceJson.Options)
            : null;
    }

    /// <summary>
    /// Same incremental-update pattern used by <see cref="TemplateService"/>:
    /// keep stable primary keys for unchanged rows so the audit log only
    /// captures real changes.
    /// </summary>
    private static void ReconcileDependencies(Module existing, IReadOnlyList<ModuleDependencyInput> inputs, int orgId)
    {
        var existingDeps = existing.Dependencies.OrderBy(d => d.Ordering).ToList();

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var depId = input.DepId.Trim();
            var depName = input.DepName.Trim();
            var depPublisher = input.DepPublisher.Trim();
            var depVersion = input.DepVersion.Trim();

            if (i < existingDeps.Count)
            {
                var dep = existingDeps[i];
                dep.Ordering = i;
                dep.DepId = depId;
                dep.DepName = depName;
                dep.DepPublisher = depPublisher;
                dep.DepVersion = depVersion;
            }
            else
            {
                existing.Dependencies.Add(new ModuleDependency
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    DepId = depId,
                    DepName = depName,
                    DepPublisher = depPublisher,
                    DepVersion = depVersion,
                });
            }
        }

        for (var i = inputs.Count; i < existingDeps.Count; i++)
        {
            existing.Dependencies.Remove(existingDeps[i]);
        }
    }

    private async Task ValidateAsync(ModuleInput input, int? existingId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

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
            var keyOwner = await _db.Modules
                .AsNoTracking()
                .Where(m => m.Key == key)
                .Select(m => (int?)m.Id)
                .FirstOrDefaultAsync(ct);
            if (keyOwner is not null && keyOwner != existingId)
            {
                errors[nameof(input.Key)] = $"A module with key '{key}' already exists.";
            }
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            errors[nameof(input.Name)] = "Name is required.";
        }

        var extensionName = input.ExtensionName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(extensionName))
        {
            errors[nameof(input.ExtensionName)] = "Extension name is required.";
        }
        else if (!ValidationPatterns.PascalCase.IsMatch(extensionName))
        {
            errors[nameof(input.ExtensionName)] = "Extension name must be PascalCase (start with an uppercase letter; letters and digits only).";
        }

        if (input.IdRangeSize is int size && size <= 0)
        {
            errors[nameof(input.IdRangeSize)] = "ID range size must be greater than zero (or empty to inherit from the template).";
        }

        var seenDepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < input.Dependencies.Count; i++)
        {
            var dep = input.Dependencies[i];
            var depId = dep.DepId?.Trim() ?? string.Empty;
            var depName = dep.DepName?.Trim() ?? string.Empty;
            var depPublisher = dep.DepPublisher?.Trim() ?? string.Empty;
            var depVersion = dep.DepVersion?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(depId))
            {
                errors[$"Dependencies[{i}].DepId"] = "Dependency id is required.";
            }
            else if (!Guid.TryParse(depId, out _))
            {
                errors[$"Dependencies[{i}].DepId"] = "Dependency id must be a GUID.";
            }
            else if (!seenDepIds.Add(depId))
            {
                errors[$"Dependencies[{i}].DepId"] = $"Duplicate dependency id '{depId}'.";
            }

            if (string.IsNullOrEmpty(depName))
            {
                errors[$"Dependencies[{i}].DepName"] = "Dependency name is required.";
            }
            if (string.IsNullOrEmpty(depPublisher))
            {
                errors[$"Dependencies[{i}].DepPublisher"] = "Dependency publisher is required.";
            }
            if (string.IsNullOrEmpty(depVersion))
            {
                errors[$"Dependencies[{i}].DepVersion"] = "Dependency version is required.";
            }
        }

        // Folder/file tree: same single-segment-path, sibling-uniqueness, and
        // one-file-per-name rules the template authoring path enforces. Field
        // keys mirror RecursiveFolderEditor so the editor renders them inline.
        TemplateValidation.ValidateFolderTree(input.Folders, "Folders", errors);

        if (errors.Count > 0)
        {
            throw new PlanValidationException(errors);
        }
    }
}

/// <summary>
/// Form-shaped admin input for module create/update.
/// </summary>
public record ModuleInput(
    string Key,
    string Name,
    string ExtensionName,
    int? IdRangeSize,
    bool Deprecated,
    IReadOnlyList<ModuleDependencyInput> Dependencies,
    IReadOnlyList<FolderAuthoring> Folders,
    // Set when Folders is a copy of a GitHub repository (#1415). Only
    // ModuleSourceService fills it, after a successful copy; null saves a typed
    // module and, on update, turns a repository module back into a typed one.
    ModuleSourceState? Source = null);

/// <summary>One dependency row submitted by the admin module editor.</summary>
public record ModuleDependencyInput(
    string DepId,
    string DepName,
    string DepPublisher,
    string DepVersion);
