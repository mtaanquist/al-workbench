using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Templates;

/// <summary>
/// Hydrates the recursive folder/file trees that hang off workspace extensions
/// and module extensions. Both walks issue two flat queries and reassemble the
/// parent/child links client-side, because EF's <c>ThenInclude</c> only
/// recurses two hops. Extracted from <see cref="TemplateService"/> so the
/// generation, import, and template-authoring paths share one home for the
/// reassembly rather than reaching through the CRUD service. Safe to call on
/// <c>AsNoTracking</c> reads.
/// </summary>
public sealed class FolderTreeHydrator
{
    private readonly AppDbContext _db;

    public FolderTreeHydrator(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Hydrates the recursive folder/file tree on every <see cref="WorkspaceExtension"/>
    /// of every supplied template. Pass <paramref name="ignoreOrgFilter"/> for
    /// cross-org reads (system-org import) that run without a tenant in scope.
    /// Pass <paramref name="pathsOnly"/> when the caller only draws the layout
    /// (the generator pages' live preview) — the file bodies are then left behind
    /// in the database instead of every example AL file being read to render a
    /// list of paths. The hydrated files carry an empty <c>Content</c>, so never
    /// pass it on a path that writes or generates.
    /// </summary>
    public async Task HydrateExtensionFolderTreeAsync(
        IEnumerable<RuntimeTemplate> templates,
        CancellationToken ct = default,
        bool ignoreOrgFilter = false,
        bool pathsOnly = false)
    {
        var allExtensions = templates.SelectMany(t => t.WorkspaceExtensions).ToList();
        if (allExtensions.Count == 0) return;

        var extensionIds = allExtensions.Select(e => e.Id).ToList();
        IQueryable<WorkspaceExtensionFolder> folderQuery = _db.WorkspaceExtensionFolders.AsNoTracking();
        IQueryable<WorkspaceExtensionFile> fileQuery = _db.WorkspaceExtensionFiles.AsNoTracking();
        if (ignoreOrgFilter)
        {
            // Fence category 5 (system-org fork read), and only when the caller opts in.
            // ignoreOrgFilter is passed true by exactly one caller — TemplateImportService
            // (both hydrate calls in ImportTemplateAsync) — whose source rows were already
            // read pinned to OrganizationId == systemOrgId, so the ids below belong to the
            // system org by construction. Every other caller (GenerationService, ModuleService,
            // ExportService, NewWorkspace/NewExtension previews) leaves it false and stays
            // inside the tenant filter.
            folderQuery = folderQuery.IgnoreQueryFilters();
            // Same category 5 read, under the same opt-in.
            fileQuery = fileQuery.IgnoreQueryFilters();
        }
        var folders = await folderQuery
            .Where(f => extensionIds.Contains(f.WorkspaceExtensionId))
            .OrderBy(f => f.Ordering)
            .ToListAsync(ct);
        var folderIds = folders.Select(f => f.Id).ToList();
        var fileRows = folderIds.Count == 0
            ? null
            : fileQuery
                .Where(f => folderIds.Contains(f.WorkspaceExtensionFolderId))
                .OrderBy(f => f.Ordering);
        var files = fileRows is null
            ? new List<WorkspaceExtensionFile>()
            : pathsOnly
                ? await fileRows.Select(f => new WorkspaceExtensionFile
                {
                    Id = f.Id,
                    OrganizationId = f.OrganizationId,
                    WorkspaceExtensionFolderId = f.WorkspaceExtensionFolderId,
                    Ordering = f.Ordering,
                    Path = f.Path,
                    IsExample = f.IsExample,
                }).ToListAsync(ct)
                : await fileRows.ToListAsync(ct);

        var foldersById = folders.ToDictionary(f => f.Id);
        var extensionsById = allExtensions.ToDictionary(e => e.Id);

        foreach (var ext in allExtensions) ext.Folders.Clear();
        foreach (var folder in folders)
        {
            folder.Folders.Clear();
            folder.Files.Clear();
        }
        foreach (var folder in folders)
        {
            if (folder.ParentFolderId is int parentId && foldersById.TryGetValue(parentId, out var parent))
            {
                parent.Folders.Add(folder);
            }
            else if (extensionsById.TryGetValue(folder.WorkspaceExtensionId, out var ext))
            {
                ext.Folders.Add(folder);
            }
        }
        foreach (var file in files)
        {
            if (foldersById.TryGetValue(file.WorkspaceExtensionFolderId, out var folder))
            {
                folder.Files.Add(file);
            }
        }
    }

    /// <summary>
    /// Hydrates the recursive <see cref="Module.ExtensionFolders"/> tree on
    /// every supplied module. Same flat-query + client-side reassembly pattern
    /// as <see cref="HydrateExtensionFolderTreeAsync"/>, including
    /// <paramref name="pathsOnly"/>.
    /// </summary>
    public async Task HydrateModuleExtensionFolderTreeAsync(
        IEnumerable<Module> modules,
        CancellationToken ct = default,
        bool ignoreOrgFilter = false,
        bool pathsOnly = false)
    {
        var moduleList = modules.ToList();
        if (moduleList.Count == 0) return;

        var moduleIds = moduleList.Select(m => m.Id).ToList();
        IQueryable<ModuleExtensionFolder> folderQuery = _db.ModuleExtensionFolders.AsNoTracking();
        IQueryable<ModuleExtensionFile> fileQuery = _db.ModuleExtensionFiles.AsNoTracking();
        if (ignoreOrgFilter)
        {
            // Fence category 5 (system-org fork read): same opt-in as above — only
            // TemplateImportService passes ignoreOrgFilter, with system-org module ids.
            folderQuery = folderQuery.IgnoreQueryFilters();
            // Same category 5 read, under the same opt-in.
            fileQuery = fileQuery.IgnoreQueryFilters();
        }
        var folders = await folderQuery
            .Where(f => moduleIds.Contains(f.ModuleId))
            .OrderBy(f => f.Ordering)
            .ToListAsync(ct);
        var folderIds = folders.Select(f => f.Id).ToList();
        var fileRows = folderIds.Count == 0
            ? null
            : fileQuery
                .Where(f => folderIds.Contains(f.ModuleExtensionFolderId))
                .OrderBy(f => f.Ordering);
        var files = fileRows is null
            ? new List<ModuleExtensionFile>()
            : pathsOnly
                ? await fileRows.Select(f => new ModuleExtensionFile
                {
                    Id = f.Id,
                    OrganizationId = f.OrganizationId,
                    ModuleExtensionFolderId = f.ModuleExtensionFolderId,
                    Ordering = f.Ordering,
                    Path = f.Path,
                    IsExample = f.IsExample,
                }).ToListAsync(ct)
                : await fileRows.ToListAsync(ct);

        var foldersById = folders.ToDictionary(f => f.Id);
        var modulesById = moduleList.ToDictionary(m => m.Id);

        foreach (var module in moduleList) module.ExtensionFolders.Clear();
        foreach (var folder in folders)
        {
            folder.Folders.Clear();
            folder.Files.Clear();
        }
        foreach (var folder in folders)
        {
            if (folder.ParentFolderId is int parentId && foldersById.TryGetValue(parentId, out var parent))
            {
                parent.Folders.Add(folder);
            }
            else if (modulesById.TryGetValue(folder.ModuleId, out var module))
            {
                module.ExtensionFolders.Add(folder);
            }
        }
        foreach (var file in files)
        {
            if (foldersById.TryGetValue(file.ModuleExtensionFolderId, out var folder))
            {
                folder.Files.Add(file);
            }
        }
    }
}
