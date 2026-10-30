namespace ALDevToolbox.Services.Generation;

/// <summary>
/// Resolved per-extension data carried through the generation pipeline.
/// Built by <see cref="GenerationService"/> from the loaded
/// <see cref="ALDevToolbox.Domain.Entities.RuntimeTemplate"/> and modules,
/// then handed to <see cref="WorkspaceZipBuilder"/> for ZIP emission.
/// </summary>
internal sealed record EmittableExtension(
    string Path,
    string Name,
    Guid Id,
    int IdRangeFrom,
    int IdRangeTo,
    string Application,
    string Runtime,
    string Publisher,
    bool IsModuleClone,
    string? ModuleKey,
    string ModuleName,
    IReadOnlyList<FolderNode> FolderRoots,
    IReadOnlyList<EmittableDependency> Dependencies,
    // True for a template extension every workspace emits (Core, Hotfix);
    // false for a ticked optional one and for a module clone. Module clones
    // depend implicitly on the required ones only (generation-engine.md).
    bool Required,
    // For a module copied from a GitHub repository (#1415): where the copy came
    // from, recorded in workspace.aldt.toml. A record, not a link.
    string? SourceRepository = null,
    string? SourceCommit = null);

/// <summary>Folder + its files + its children (recursive). Built once at load time.</summary>
internal sealed record FolderNode(
    string Path,
    IReadOnlyList<FileLeaf> Files,
    IReadOnlyList<FolderNode> Folders);

internal sealed record FileLeaf(string Path, string Content, bool IsExample);

internal sealed record EmittableDependency(
    string? RefExtensionPath,
    string? RefModuleKey,
    string? LitId,
    string? LitName,
    string? LitPublisher,
    string? LitVersion);
