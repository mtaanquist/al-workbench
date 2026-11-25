namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// One node in the live folder-tree preview shown on the New Workspace and
/// New Extension pages. Built from the current form state — see
/// <c>ui-design.md</c> for the visual spec.
/// </summary>
public record PreviewNode(
    string Name,
    PreviewNodeKind Kind,
    IReadOnlyList<PreviewNode> Children
)
{
    /// <summary>
    /// Marks a node as "newly added" relative to an existing workspace, so the
    /// preview renderer can label it. Used by the New Extension page when a
    /// workspace config has been imported and the user is scaffolding a sibling
    /// extension; the rest of the tree shows what's already there, this flag
    /// pins the addition.
    /// </summary>
    public bool IsNew { get; init; }

    /// <summary>
    /// Marks a node the generator will <em>not</em> write, so the preview can
    /// show it struck through rather than hiding it. Set on example AL files
    /// while the "Example files" toggle in the preview card head is off: the
    /// user sees exactly what the toggle costs them instead of the tree
    /// silently shrinking. See <c>customer-naming.md</c>.
    /// </summary>
    public bool IsExcluded { get; init; }

    /// <summary>
    /// Right-aligned per-row detail, rendered into the design system's
    /// <c>.tree__meta</c> slot. Carries the allocated AL object-id range on a
    /// generated extension folder ("ID 50100-50199") — which is what the
    /// handoff's generator screen puts there, and what the preview was missing
    /// entirely (#546). Empty on every other row: the slot is for the one fact
    /// a row can carry that its name cannot.
    /// </summary>
    public string? Meta { get; init; }

    public static PreviewNode Folder(string name, IEnumerable<PreviewNode>? children = null) =>
        new(name, PreviewNodeKind.Folder, children?.ToList() ?? new List<PreviewNode>());

    public static PreviewNode Extension(string name, IEnumerable<PreviewNode>? children = null) =>
        new(name, PreviewNodeKind.Extension, children?.ToList() ?? new List<PreviewNode>());

    public static PreviewNode File(string name) =>
        new(name, PreviewNodeKind.File, Array.Empty<PreviewNode>());
}

/// <summary>
/// Discriminator on <see cref="PreviewNode"/>. Drives icon and colour choice:
/// <see cref="Extension"/> nodes pick up the accent treatment described in
/// <c>ui-design.md</c>; everything else renders in the secondary text colour.
/// </summary>
public enum PreviewNodeKind
{
    /// <summary>The workspace root (e.g. <c>CRONUSCustomer/</c>).</summary>
    Workspace,

    /// <summary>A generated extension folder (Core or a module).</summary>
    Extension,

    /// <summary>A grouping or static folder (<c>.assets</c>, <c>Source</c>, <c>Translations</c>).</summary>
    Folder,

    /// <summary>A leaf file (<c>app.json</c>, <c>.gitkeep</c>, an example <c>.al</c>).</summary>
    File,
}

/// <summary>
/// Helpers for building a <see cref="PreviewNode"/> tree from a list of
/// slash-delimited template folder paths. Splitting each path independently
/// would emit one parent chain per leaf, so siblings that share a prefix
/// (e.g. <c>Source/Foundation</c> and <c>Source/Finance</c>) would each get
/// their own <c>Source/</c> wrapper. This helper merges them so a shared
/// prefix renders as a single parent.
/// </summary>
public static class PreviewTreeBuilder
{
    /// <summary>
    /// Finishes a workspace-root preview the same way every generator-backed
    /// page does: appends the two side-car files the generator always emits
    /// (<c>{rootName}.code-workspace</c> and the workspace config file), grafts
    /// each workspace-root-scoped organisation file into the tree, then wraps
    /// the lot in a <see cref="PreviewNodeKind.Workspace"/> root and sorts it
    /// for display. <paramref name="children"/> is the per-page set of
    /// extension/module/logo nodes already assembled by the caller; it is
    /// mutated in place (the side-cars and grafted files are appended to it).
    /// <paramref name="configFileName"/> is passed in rather than referenced
    /// directly so this value-object layer stays free of a Services dependency.
    /// <paramref name="codeWorkspaceFileName"/> names the workspace file when it
    /// is not <c>{rootName}.code-workspace</c>: an existing workspace keeps the
    /// name its settings recorded (#1615).
    /// </summary>
    public static PreviewNode BuildWorkspacePreview(
        string rootName,
        IList<PreviewNode> children,
        string configFileName,
        IEnumerable<string> workspaceRootPaths,
        IEnumerable<string>? emptyRootFolders = null,
        string? codeWorkspaceFileName = null)
    {
        children.Add(PreviewNode.File(codeWorkspaceFileName ?? $"{rootName}.code-workspace"));
        children.Add(PreviewNode.File(configFileName));
        foreach (var path in workspaceRootPaths)
        {
            GraftFile(children, path);
        }
        GraftEmptyFolders(children, emptyRootFolders, workspaceRootPaths);
        return SortForDisplay(new PreviewNode(
            rootName, PreviewNodeKind.Workspace, children as IReadOnlyList<PreviewNode> ?? children.ToList()));
    }

    /// <summary>
    /// Grafts the template's declared empty folders into the workspace root,
    /// each showing the <c>.gitkeep</c> the generator drops inside it — the
    /// same placeholder <c>ExtensionPreviewBuilder</c> shows for an empty leaf.
    /// A folder that a workspace-root file already lives inside is skipped,
    /// mirroring <c>WorkspaceZipBuilder.WriteRootFolders</c>: the folder is in
    /// the tree either way, with real content rather than a placeholder.
    /// </summary>
    public static void GraftEmptyFolders(
        IList<PreviewNode> siblings,
        IEnumerable<string>? folderPaths,
        IEnumerable<string> workspaceRootPaths)
    {
        if (folderPaths is null) return;
        var occupied = workspaceRootPaths as IReadOnlyList<string> ?? workspaceRootPaths.ToList();
        foreach (var raw in folderPaths)
        {
            var path = raw?.Trim().Trim('/') ?? string.Empty;
            if (path.Length == 0) continue;
            if (occupied.Any(f => f.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))) continue;
            GraftFile(siblings, $"{path}/.gitkeep");
        }
    }

    /// <summary>
    /// Returns a copy of <paramref name="node"/> with every level of children
    /// reordered to mimic Windows File Explorer: folders first (alphabetical),
    /// then files (alphabetical). Comparison is case-insensitive so casing
    /// inconsistencies don't surprise users.
    /// </summary>
    public static PreviewNode SortForDisplay(PreviewNode node)
    {
        if (node.Children.Count == 0) return node;
        var sorted = node.Children
            .Select(SortForDisplay)
            .OrderBy(c => c.Kind == PreviewNodeKind.File ? 1 : 0)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return node with { Children = sorted };
    }

    /// <summary>
    /// Walks a forward-slash-separated <paramref name="path"/> and grafts a
    /// leaf <see cref="PreviewNode.File"/> into the supplied
    /// <paramref name="siblings"/>, materialising intermediate folders as
    /// needed. Two grafts that share a prefix end up under one folder so the
    /// preview matches what an OS file explorer would render. Duplicate
    /// files at the same path are no-ops. The factory methods always
    /// allocate mutable <c>List&lt;PreviewNode&gt;</c> for children, so the
    /// downward walk casts each level back to <see cref="IList{T}"/>.
    /// </summary>
    public static void GraftFile(IList<PreviewNode> siblings, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return;
        GraftRecursive(siblings, segments, 0);
    }

    private static void GraftRecursive(IList<PreviewNode> siblings, string[] segments, int idx)
    {
        var name = segments[idx];
        if (idx == segments.Length - 1)
        {
            if (!siblings.Any(n => n.Kind == PreviewNodeKind.File && n.Name == name))
            {
                siblings.Add(PreviewNode.File(name));
            }
            return;
        }
        var existing = siblings.FirstOrDefault(n => n.Kind == PreviewNodeKind.Folder && n.Name == name);
        if (existing is null)
        {
            var children = new List<PreviewNode>();
            siblings.Add(new PreviewNode(name, PreviewNodeKind.Folder, children));
            GraftRecursive(children, segments, idx + 1);
        }
        else if (existing.Children is IList<PreviewNode> nestedList)
        {
            GraftRecursive(nestedList, segments, idx + 1);
        }
    }
}
