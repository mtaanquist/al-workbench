namespace ALDevToolbox.Domain.Seed;

/// <summary>
/// In-memory representation of a single module TOML document. Used by the
/// export pipeline to serialise module rows for off-site storage.
/// </summary>
public class ModuleSeedFile
{
    public ModuleSeed Module { get; set; } = new();
}

/// <summary>The <c>[module]</c> table.</summary>
public class ModuleSeed
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// PascalCase ZIP folder name / rendered AL extension name. Distinct from
    /// <see cref="Key"/> (the admin/URL slug) — see
    /// <see cref="ALDevToolbox.Domain.Entities.Module.ExtensionName"/>.
    /// </summary>
    public string ExtensionName { get; set; } = string.Empty;
    public int? IdRangeSize { get; set; }

    /// <summary>
    /// Mirrors <see cref="ALDevToolbox.Domain.Entities.Module.Deprecated"/> so
    /// the export/import round-trip preserves the "hidden from end-user
    /// pickers but still available for regeneration" flag.
    /// </summary>
    public bool Deprecated { get; set; }

    public List<ModuleDependencySeed> Dependencies { get; set; } = new();

    /// <summary>
    /// The module's recursive folder/file tree, cloned into the workspace as a
    /// generated extension. Reuses <see cref="FolderSeed"/> so the on-disk shape
    /// matches a template extension's <c>[[extensions.folders]]</c> blocks.
    /// </summary>
    public List<FolderSeed> Folders { get; set; } = new();

    /// <summary>
    /// Where the files are copied from, for a module copied from a GitHub
    /// repository (#1415); null for a typed module. <see cref="Folders"/> still
    /// carries the copy itself.
    /// </summary>
    public ModuleSourceSeed? Source { get; set; }
}

/// <summary>The <c>[module.source]</c> table.</summary>
public class ModuleSourceSeed
{
    public string Repository { get; set; } = string.Empty;
    public string? Ref { get; set; }
    public string? Path { get; set; }
    public string? Commit { get; set; }
}

/// <summary>One <c>[[module.dependencies]]</c> entry.</summary>
public class ModuleDependencySeed
{
    public string DepId { get; set; } = string.Empty;
    public string DepName { get; set; } = string.Empty;
    public string DepPublisher { get; set; } = string.Empty;
    public string DepVersion { get; set; } = string.Empty;
}
