using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Seed;
using Microsoft.EntityFrameworkCore;
using Tomlyn;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Services.Organizations;

/// <summary>
/// Backup / snapshot service: walks the active rows in the database and emits
/// a TOML ZIP for off-site storage or migration to a fresh deployment.
/// See <c>.design/templates-and-seeding.md</c>.
/// </summary>
public class ExportService
{
    /// <summary>
    /// Mirrors <see cref="TemplateTomlMapper"/>: a global <c>snake_case</c>
    /// policy with per-property TOML name overrides on the camelCase outliers
    /// (defaults / appSourceCop) handled inside the seed POCOs themselves.
    /// Keeping the options identical across read and write paths is what
    /// makes round-tripping reliable.
    /// </summary>
    private static readonly TomlSerializerOptions TomlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly FolderTreeHydrator _folderTree;
    private readonly ILogger<ExportService> _logger;

    public ExportService(AppDbContext db, IOrganizationContext orgContext, FolderTreeHydrator folderTree, ILogger<ExportService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _folderTree = folderTree;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; export called outside an authenticated request.");

    /// <summary>
    /// Produces a ZIP containing the current state of the database serialised
    /// as TOML for off-site storage. Soft-deleted rows are excluded so the
    /// export reflects the live, useful state. The returned stream is a
    /// rewound <see cref="MemoryStream"/> ready to be copied to the HTTP body.
    /// </summary>
    public async Task<GeneratedArchive> ExportAllAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var templateRows = await _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .Include(t => t.WorkspaceExtensions.OrderBy(e => e.Ordering))
            .Include(t => t.DefaultModules.OrderBy(d => d.Ordering))
                .ThenInclude(d => d.Module!)
            .Include(t => t.DefaultApplicationVersion)
            .ToListAsync(ct);

        // Sort client-side because Runtime is a text column and lexicographic
        // order would put "10" before "9". TemplateService exposes the
        // version-aware tuple comparer.
        var templates = templateRows
            .OrderBy(t => TemplateService.RuntimeSortKey(t.Runtime))
            .ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var modules = await _db.Modules
            .AsNoTracking()
            .Where(m => m.DeletedAt == null)
            .Include(m => m.Dependencies.OrderBy(d => d.Ordering))
            .OrderBy(m => m.Key)
            .ToListAsync(ct);
        // Folder/file trees need the flat-query hydrator (Include only recurses
        // two hops), otherwise the export would silently drop module layouts.
        await _folderTree.HydrateModuleExtensionFolderTreeAsync(modules, ct);

        var catalog = await _db.WellKnownDependencies
            .AsNoTracking()
            .OrderBy(w => w.Category)
            .ThenBy(w => w.Ordering)
            .ThenBy(w => w.DepName)
            .ToListAsync(ct);

        var applicationVersions = await _db.ApplicationVersions
            .AsNoTracking()
            .Where(a => a.DeletedAt == null)
            .OrderBy(a => a.Ordering)
            .ThenBy(a => a.Key)
            .ToListAsync(ct);

        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var template in templates)
            {
                // Re-use the same mapper the admin TOML editor saves through, so
                // the round-trip is byte-identical to "edit in TOML mode, save".
                var entryPath = $"{template.Key}/template.toml";
                await WriteTextEntryAsync(archive, entryPath, TemplateTomlMapper.ToToml(template), ct);
            }

            foreach (var module in modules)
            {
                var seed = new ModuleSeedFile
                {
                    Module = new ModuleSeed
                    {
                        Key = module.Key,
                        Name = module.Name,
                        ExtensionName = module.ExtensionName,
                        IdRangeSize = module.IdRangeSize,
                        Deprecated = module.Deprecated,
                        Dependencies = module.Dependencies
                            .OrderBy(d => d.Ordering)
                            .Select(d => new ModuleDependencySeed
                            {
                                DepId = d.DepId,
                                DepName = d.DepName,
                                DepPublisher = d.DepPublisher,
                                DepVersion = d.DepVersion,
                            })
                            .ToList(),
                        Folders = module.ExtensionFolders
                            .OrderBy(f => f.Ordering)
                            .Select(ToFolderSeed)
                            .ToList(),
                    },
                };
                await WriteTextEntryAsync(
                    archive,
                    $"modules/{module.Key}.toml",
                    TomlSerializer.Serialize(seed, TomlOptions),
                    ct);
            }

            foreach (var version in applicationVersions)
            {
                var seed = new ApplicationVersionSeedFile
                {
                    Version = new ApplicationVersionSeed
                    {
                        Key = version.Key,
                        Name = version.Name,
                        Application = version.Application,
                        Runtime = version.Runtime,
                        Ordering = version.Ordering,
                        Deprecated = version.Deprecated,
                    },
                };
                await WriteTextEntryAsync(
                    archive,
                    $"application-versions/{version.Key}.toml",
                    TomlSerializer.Serialize(seed, TomlOptions),
                    ct);
            }

            var catalogSeed = new CatalogSeedFile
            {
                Dependency = catalog
                    .Select(c => new WellKnownDependencySeed
                    {
                        DepId = c.DepId,
                        DepName = c.DepName,
                        DepPublisher = c.DepPublisher,
                        DepVersionDefault = c.DepVersionDefault,
                        Category = c.Category,
                        Ordering = c.Ordering,
                    })
                    .ToList(),
            };
            await WriteTextEntryAsync(
                archive,
                "catalog/well-known-deps.toml",
                TomlSerializer.Serialize(catalogSeed, TomlOptions),
                ct);

            // Per-organisation configuration block (Milestone P3.14): defaults,
            // always-included files and the logo. The logo's bytes go in as
            // base64 because TOML has no binary literal — capped at 256 KB
            // upstream so the resulting text stays small.
            await WriteOrgConfigAsync(archive, ct);
        }

        stream.Position = 0;
        var fileName = $"aldt-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";

        _logger.LogInformation(
            "Exported {Templates} template(s), {Modules} module(s), {AppVersions} application version(s), {Catalog} catalogue entry(ies) to {FileName} in {Elapsed}ms.",
            templates.Count, modules.Count, applicationVersions.Count, catalog.Count, fileName, stopwatch.ElapsedMilliseconds);

        return new GeneratedArchive(stream, fileName);
    }

    /// <summary>
    /// Walks the acting org's <c>organization_settings</c>, <c>organization_files</c>
    /// and <c>organization_assets</c> rows and writes a single
    /// <c>organization-config.toml</c> entry. The block is omitted if the org
    /// has no settings row at all — fresh orgs start without one and the
    /// admin fills it in via <c>/admin/configuration</c>.
    /// </summary>
    private async Task WriteOrgConfigAsync(ZipArchive archive, CancellationToken ct)
    {
        var orgId = RequireOrganizationId();

        var settings = await _db.OrganizationSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (settings is null) return;

        var logo = await _db.OrganizationAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.OrganizationId == orgId
                                      && a.Kind == ALDevToolbox.Domain.Entities.OrganizationAssetKind.Logo, ct);

        var files = await _db.OrganizationFiles
            .AsNoTracking()
            .Where(f => f.OrganizationId == orgId)
            .OrderBy(f => f.Ordering)
            .ToListAsync(ct);

        var seed = new OrganizationConfigSeedFile
        {
            Settings = new OrganizationSettingsSeed
            {
                DefaultPublisher = settings.DefaultPublisher,
                DefaultIdRangeFrom = settings.DefaultIdRangeFrom,
                DefaultIdRangeTo = settings.DefaultIdRangeTo,
                DefaultBrief = settings.DefaultBrief,
                DefaultCoreDescription = settings.DefaultCoreDescription,
                CodeWorkspaceJson = settings.CodeWorkspaceJson,
                DefaultUrl = settings.DefaultUrl ?? string.Empty,
                DefaultLogo = settings.DefaultLogo ?? string.Empty,
            },
            Logo = logo is null ? null : new OrganizationLogoSeed
            {
                ContentType = logo.ContentType,
                ContentBase64 = Convert.ToBase64String(logo.Content),
            },
            File = files
                .Select(f => new OrganizationFileSeed
                {
                    Path = f.Path,
                    Content = f.Content,
                    MustacheEnabled = f.MustacheEnabled,
                    Scope = f.Scope.ToString(),
                })
                .ToList(),
        };

        await WriteTextEntryAsync(
            archive,
            "organization-config.toml",
            TomlSerializer.Serialize(seed, TomlOptions),
            ct);
    }

    private static async Task WriteTextEntryAsync(ZipArchive archive, string path, string content, CancellationToken ct)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        // UTF-8 without BOM keeps the export diff-friendly with the seed files
        // that ship in the repo.
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content.AsMemory(), ct);
    }

    /// <summary>
    /// Recursively projects a hydrated <see cref="Domain.Entities.ModuleExtensionFolder"/>
    /// onto the serialisable <see cref="FolderSeed"/> shape, ordered the same way
    /// the generator emits it.
    /// </summary>
    private static FolderSeed ToFolderSeed(Domain.Entities.ModuleExtensionFolder folder) => new()
    {
        Path = folder.Path,
        Files = folder.Files
            .OrderBy(f => f.Ordering)
            .Select(f => new FolderFileSeed
            {
                Path = f.Path,
                Content = f.Content,
                IsExample = f.IsExample,
            })
            .ToList(),
        Folders = folder.Folders
            .OrderBy(f => f.Ordering)
            .Select(ToFolderSeed)
            .ToList(),
    };
}
