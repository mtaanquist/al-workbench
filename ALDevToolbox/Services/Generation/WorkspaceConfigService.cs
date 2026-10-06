using System.Text;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Tomlyn;
using Tomlyn.Serialization;

namespace ALDevToolbox.Services.Generation;

/// <summary>
/// Serialises a generated project's form-post shape to <c>workspace.aldt.toml</c>
/// and parses such a file back into a <see cref="ProjectPlan"/> or
/// <see cref="StandaloneExtensionPlan"/>. Lets users save a workspace's settings
/// alongside the generated ZIP and re-import them later — see Milestone P2.3 in
/// <c>.design/milestones.md</c>.
/// </summary>
/// <remarks>
/// The DB remains the source of truth: parsing validates the referenced template
/// and module keys against live rows, refusing imports that point at deleted or
/// missing entities. Publisher is derived from the picked template's defaults
/// rather than persisted in the config file, so a reused workspace config always
/// reflects the current template setup.
/// </remarks>
public class WorkspaceConfigService
{
    /// <summary>Schema version baked into emitted files. Bump when the on-disk shape changes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The kind value emitted for a workspace config.</summary>
    public const string WorkspaceKind = "workspace";

    /// <summary>The kind value emitted for a standalone-extension config.</summary>
    public const string ExtensionKind = "extension";

    /// <summary>Filename written into the generated ZIP and accepted by the import action.</summary>
    public const string FileName = "workspace.aldt.toml";

    private static readonly TomlSerializerOptions TomlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    private readonly AppDbContext _db;

    public WorkspaceConfigService(AppDbContext db)
    {
        _db = db;
    }

    // ===== Build (no DB access) =====

    /// <summary>
    /// Serialises a workspace plan plus the identities (GUIDs, names, folders,
    /// id-ranges) of every generated extension. The identity block is what lets
    /// a later sibling-extension import declare real dependencies and avoid
    /// id-range collisions; pass an empty list when no identities are available
    /// (e.g. legacy callers).
    /// </summary>
    public string BuildWorkspace(ProjectPlan plan, IReadOnlyList<WorkspaceExtensionIdentity> extensions)
    {
        var doc = new WorkspaceDoc
        {
            SchemaVersion = CurrentSchemaVersion,
            Kind = WorkspaceKind,
            Workspace = new WorkspaceSection
            {
                Template = plan.TemplateKey,
                Name = plan.WorkspaceName,
                ShortName = plan.ShortName ?? string.Empty,
                Brief = plan.Brief,
                Description = plan.Description,
                ApplicationVersion = plan.ApplicationVersion,
                RuntimeVersion = plan.RuntimeVersion,
                CoreIdRangeFrom = plan.CoreIdRangeFrom,
                CoreIdRangeTo = plan.CoreIdRangeTo,
                IncludeExamples = plan.IncludeExamples,
                ExtensionPrefix = plan.ExtensionPrefix,
                SelectedExtensions = plan.SelectedExtensionPaths.ToList(),
                Modules = plan.SelectedModuleKeys.ToList(),
                TenantId = plan.TenantId,
                Extensions = extensions
                    .Select(e => new ExtensionIdentitySection
                    {
                        Kind = e.Kind,
                        Key = e.Key ?? string.Empty,
                        Id = e.Id.ToString(),
                        Name = e.Name,
                        Folder = e.Folder,
                        Publisher = e.Publisher,
                        IdRangeFrom = e.IdRangeFrom,
                        IdRangeTo = e.IdRangeTo,
                    })
                    .ToList(),
            },
        };
        return PrependHeader(TomlSerializer.Serialize(doc, TomlOptions));
    }

    /// <summary>
    /// Serialises a standalone-extension plan to its <c>workspace.aldt.toml</c>
    /// form. Mirrors <see cref="BuildWorkspace"/> but uses the extension shape.
    /// </summary>
    public string BuildExtension(StandaloneExtensionPlan plan)
    {
        var doc = new ExtensionDoc
        {
            SchemaVersion = CurrentSchemaVersion,
            Kind = ExtensionKind,
            Extension = new ExtensionSection
            {
                Template = plan.TemplateKey,
                Name = plan.ExtensionName,
                Brief = plan.Brief,
                Description = plan.Description,
                Publisher = plan.Publisher,
                ApplicationVersion = plan.ApplicationVersion,
                RuntimeVersion = plan.RuntimeVersion,
                IdRangeFrom = plan.IdRangeFrom,
                IdRangeTo = plan.IdRangeTo,
                IncludeExamples = plan.IncludeExamples,
                Dependencies = plan.Dependencies
                    .Select(d => new DependencyConfig
                    {
                        Id = d.DepId,
                        Name = d.DepName,
                        Publisher = d.DepPublisher,
                        Version = d.DepVersion,
                    })
                    .ToList(),
            },
        };
        return PrependHeader(TomlSerializer.Serialize(doc, TomlOptions));
    }

    private static string PrependHeader(string body) =>
        new StringBuilder()
            .AppendLine("# AL Workbench project config.")
            .AppendLine("# Re-import this file from /templates/workspace or /templates/extension to recreate the")
            .AppendLine("# project with the same settings, or scaffold a sibling extension against the")
            .AppendLine("# same workspace shape.")
            .Append(body)
            .ToString();

    // ===== Parse (validates against the DB) =====

    /// <summary>
    /// Parses the supplied TOML and validates it against the live database.
    /// Returns whichever shape the file declared via its <c>kind</c> key. Throws
    /// <see cref="PlanValidationException"/> when the file is malformed, points
    /// at a missing/deleted template, or references unknown module keys; the
    /// errors dictionary is keyed for inline display by the caller.
    /// </summary>
    public async Task<WorkspaceConfigImport> ParseAsync(string toml, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toml))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["File"] = "The file is empty.",
            });
        }

        UnionDoc doc;
        try
        {
            doc = TomlSerializer.Deserialize<UnionDoc>(toml, TomlOptions) ?? new UnionDoc();
        }
        catch (Tomlyn.TomlException tex)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["File"] = $"Failed to parse TOML: {tex.Message}",
            });
        }
        catch (Exception ex)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["File"] = $"Failed to parse TOML: {ex.Message}",
            });
        }

        if (doc.SchemaVersion != CurrentSchemaVersion)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["SchemaVersion"] = $"Unsupported schema_version '{doc.SchemaVersion}'. Expected {CurrentSchemaVersion}.",
            });
        }

        return doc.Kind switch
        {
            WorkspaceKind => new WorkspaceConfigImport(
                WorkspaceKind,
                await BuildWorkspaceImportAsync(doc, ct),
                null,
                ParseExtensions(doc.Workspace?.Extensions)),
            ExtensionKind => new WorkspaceConfigImport(
                ExtensionKind,
                null,
                await BuildExtensionImportAsync(doc, ct),
                Array.Empty<WorkspaceExtensionIdentity>()),
            _ => throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Kind"] = $"Unknown kind '{doc.Kind}'. Expected '{WorkspaceKind}' or '{ExtensionKind}'.",
            }),
        };
    }

    /// <summary>
    /// Maps the persisted <c>[[workspace.extensions]]</c> rows into the public
    /// <see cref="WorkspaceExtensionIdentity"/> shape. Rows with malformed GUIDs
    /// are dropped silently — older configs without the section yield an empty
    /// list, and the sibling-extension import falls back to template defaults.
    /// </summary>
    private static IReadOnlyList<WorkspaceExtensionIdentity> ParseExtensions(IReadOnlyList<ExtensionIdentitySection>? rows)
    {
        if (rows is null || rows.Count == 0) return Array.Empty<WorkspaceExtensionIdentity>();

        var result = new List<WorkspaceExtensionIdentity>(rows.Count);
        foreach (var row in rows)
        {
            if (!Guid.TryParse(row.Id, out var id)) continue;
            result.Add(new WorkspaceExtensionIdentity(
                Kind: row.Kind,
                Key: string.IsNullOrEmpty(row.Key) ? null : row.Key,
                Id: id,
                Name: row.Name,
                Folder: row.Folder,
                Publisher: row.Publisher,
                IdRangeFrom: row.IdRangeFrom,
                IdRangeTo: row.IdRangeTo));
        }
        return result;
    }

    private async Task<ProjectPlan> BuildWorkspaceImportAsync(UnionDoc doc, CancellationToken ct)
    {
        var section = doc.Workspace ?? throw new PlanValidationException(new Dictionary<string, string>
        {
            ["Workspace"] = "Missing [workspace] section.",
        });

        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(section.Template)) errors["Template"] = "Required.";

        // Validate template exists and is active.
        if (!string.IsNullOrWhiteSpace(section.Template))
        {
            var templateExists = await _db.RuntimeTemplates
                .AsNoTracking()
                .AnyAsync(t => t.DeletedAt == null && t.Key == section.Template, ct);
            if (!templateExists)
            {
                errors["Template"] = $"Template '{section.Template}' was not found or has been deleted.";
            }
        }

        // Validate every referenced module key against the live, non-deleted set.
        var unknownModules = new List<string>();
        if (section.Modules.Count > 0)
        {
            var liveKeys = await _db.Modules
                .AsNoTracking()
                .Where(m => m.DeletedAt == null && section.Modules.Contains(m.Key))
                .Select(m => m.Key)
                .ToListAsync(ct);
            var liveSet = liveKeys.ToHashSet(StringComparer.Ordinal);
            unknownModules = section.Modules.Where(k => !liveSet.Contains(k)).ToList();
            if (unknownModules.Count > 0)
            {
                errors["Modules"] = $"Unknown or deleted module key(s): {string.Join(", ", unknownModules)}.";
            }
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        return new ProjectPlan(
            TemplateKey: section.Template,
            WorkspaceName: section.Name,
            ShortName: section.ShortName,
            ExtensionPrefix: section.ExtensionPrefix,
            Brief: section.Brief,
            Description: section.Description,
            ApplicationVersion: section.ApplicationVersion,
            RuntimeVersion: section.RuntimeVersion,
            CoreIdRangeFrom: section.CoreIdRangeFrom,
            CoreIdRangeTo: section.CoreIdRangeTo,
            IncludeExamples: section.IncludeExamples,
            SelectedExtensionPaths: section.SelectedExtensions.ToList(),
            SelectedModuleKeys: section.Modules.ToList(),
            TenantId: section.TenantId);
    }

    private async Task<StandaloneExtensionPlan> BuildExtensionImportAsync(UnionDoc doc, CancellationToken ct)
    {
        var section = doc.Extension ?? throw new PlanValidationException(new Dictionary<string, string>
        {
            ["Extension"] = "Missing [extension] section.",
        });

        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(section.Template)) errors["Template"] = "Required.";

        if (!string.IsNullOrWhiteSpace(section.Template))
        {
            var templateExists = await _db.RuntimeTemplates
                .AsNoTracking()
                .AnyAsync(t => t.DeletedAt == null && t.Key == section.Template, ct);
            if (!templateExists)
            {
                errors["Template"] = $"Template '{section.Template}' was not found or has been deleted.";
            }
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        var deps = section.Dependencies
            .Select(d => new DependencyEntry(
                DepId: d.Id,
                DepName: d.Name,
                DepPublisher: d.Publisher,
                DepVersion: d.Version))
            .ToList();

        return new StandaloneExtensionPlan(
            TemplateKey: section.Template,
            ExtensionName: section.Name,
            Brief: section.Brief,
            Description: section.Description,
            ApplicationVersion: section.ApplicationVersion,
            RuntimeVersion: section.RuntimeVersion,
            IdRangeFrom: section.IdRangeFrom,
            IdRangeTo: section.IdRangeTo,
            IncludeExamples: section.IncludeExamples,
            Publisher: section.Publisher,
            Dependencies: deps);
    }

    // ===== TOML POCOs =====

    /// <summary>Emitted shape for kind = "workspace".</summary>
    private class WorkspaceDoc
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string Kind { get; set; } = WorkspaceKind;
        public WorkspaceSection Workspace { get; set; } = new();
    }

    /// <summary>Emitted shape for kind = "extension".</summary>
    private class ExtensionDoc
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string Kind { get; set; } = ExtensionKind;
        public ExtensionSection Extension { get; set; } = new();
    }

    /// <summary>
    /// Permissive shape used when reading: both sub-tables are nullable so the
    /// deserializer doesn't need a kind-aware preflight pass. The kind field
    /// dispatches to the correct branch.
    /// </summary>
    private class UnionDoc
    {
        public int SchemaVersion { get; set; }
        public string Kind { get; set; } = string.Empty;
        public WorkspaceSection? Workspace { get; set; }
        public ExtensionSection? Extension { get; set; }
    }

    private class WorkspaceSection
    {
        public string Template { get; set; } = string.Empty;
        /// <summary>The customer's name, as typed on the form.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>
        /// The customer's abbreviated name, used in the extension names.
        /// Empty on configs written before short names existed and on
        /// workspaces whose customer name needed no abbreviating; either way
        /// the reader falls back to <see cref="Name"/>.
        /// </summary>
        public string ShortName { get; set; } = string.Empty;
        public string Brief { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ApplicationVersion { get; set; } = string.Empty;
        public string RuntimeVersion { get; set; } = string.Empty;
        public int CoreIdRangeFrom { get; set; }
        public int CoreIdRangeTo { get; set; }
        public bool IncludeExamples { get; set; } = true;
        public string ExtensionPrefix { get; set; } = string.Empty;
        /// <summary>
        /// Paths of optional template-declared extensions the user ticked on
        /// the form (<c>WorkspaceExtension</c> entries with
        /// <c>required = false</c>). Always-required extensions are emitted
        /// regardless of this list.
        /// </summary>
        public List<string> SelectedExtensions { get; set; } = new();
        // Per-extension identity for Core + every module, captured at workspace
        // generation time. Populated when the workspace flow emits a config so
        // a sibling-extension import can see the actual GUIDs / id-ranges /
        // folder names rather than guessing. Optional on read — older configs
        // (and hand-written ones) lack the section and the import gracefully
        // falls back to template defaults.
        public List<ExtensionIdentitySection> Extensions { get; set; } = new();
        public List<string> Modules { get; set; } = new();
        /// <summary>
        /// Tenant GUID captured on the New Workspace form. Persisted so
        /// re-importing the file regenerates the workspace with the same
        /// <c>{{tenant_id}}</c> substitution. Empty on older configs;
        /// the reader falls back to the empty string.
        /// </summary>
        public string TenantId { get; set; } = string.Empty;
    }

    private class ExtensionSection
    {
        public string Template { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Brief { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string ApplicationVersion { get; set; } = string.Empty;
        public string RuntimeVersion { get; set; } = string.Empty;
        public int IdRangeFrom { get; set; }
        public int IdRangeTo { get; set; }
        public bool IncludeExamples { get; set; } = true;
        public List<DependencyConfig> Dependencies { get; set; } = new();
    }

    private class DependencyConfig
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
    }

    /// <summary>
    /// Per-extension identity row inside a workspace config. <c>Kind</c> is
    /// <c>"core"</c> or <c>"module"</c>; <c>Key</c> is the module key
    /// (empty for Core); <c>Id</c> is the GUID stamped into the corresponding
    /// <c>app.json</c> at generation time.
    /// </summary>
    private class ExtensionIdentitySection
    {
        public string Kind { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Folder { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public int IdRangeFrom { get; set; }
        public int IdRangeTo { get; set; }
    }
}

/// <summary>
/// Identity of one extension inside a workspace, persisted in
/// <c>workspace.aldt.toml</c> and read back when the New Extension page
/// imports a workspace config. The GUID and id-range are stamped at the
/// original generation time so a sibling extension can declare a real
/// dependency on the existing extension and pick a non-colliding id range.
/// </summary>
public record WorkspaceExtensionIdentity(
    string Kind,
    string? Key,
    Guid Id,
    string Name,
    string Folder,
    string Publisher,
    int IdRangeFrom,
    int IdRangeTo)
{
    public const string CoreKind = "core";
    public const string ModuleKind = "module";

    /// <summary>An extension added to the workspace after it was generated, from New Extension.</summary>
    public const string AddedKind = "added";
}

/// <summary>
/// Result of <see cref="WorkspaceConfigService.ParseAsync"/>. Exactly one of
/// <see cref="Workspace"/> / <see cref="Extension"/> is non-null, matching the
/// declared <see cref="Kind"/>. <see cref="Extensions"/> carries the per-
/// extension identity rows from a workspace config (GUIDs / names / id-ranges
/// for Core + every module) and is empty for extension-kind imports or for
/// older workspace configs that lack the section.
/// </summary>
public record WorkspaceConfigImport(
    string Kind,
    ProjectPlan? Workspace,
    StandaloneExtensionPlan? Extension,
    IReadOnlyList<WorkspaceExtensionIdentity> Extensions);
