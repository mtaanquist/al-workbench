namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A reusable module the user can select on the New Workspace form. Each module
/// declares its own extension folder/file tree; when the user picks it, the
/// generator clones that tree into the workspace as another extension. Modules
/// also carry their own AL dependencies (e.g. the runtime DLLs / system apps
/// they depend on) which are added to the cloned extension's <c>app.json</c>.
/// </summary>
public class Module
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>URL-safe unique key (e.g. <c>document-capture</c>). Used for
    /// admin URLs and the dependency reference target; not the ZIP folder name.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Display name (e.g. <c>Document Capture</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// PascalCase name used for both the ZIP folder of the cloned extension
    /// (e.g. <c>DocumentCapture/</c>) and the rendered AL extension name
    /// (after <c>{{extension_prefix}}</c> substitution: <c>"CRONUS DocumentCapture"</c>).
    /// Kept separate from <see cref="Key"/> so admins can pick a URL slug that
    /// differs from the folder layout AL developers actually see.
    /// </summary>
    public string ExtensionName { get; set; } = string.Empty;

    /// <summary>
    /// Optional override for how many object ids this module reserves. Falls
    /// back to <see cref="RuntimeTemplate.ModuleIdRangeSize"/> when null.
    /// </summary>
    public int? IdRangeSize { get; set; }

    /// <summary>
    /// The ID range size the copied repository's own <c>app.json</c> declares,
    /// refreshed with every copy the way its dependencies are, or null for a
    /// typed module or a repository whose <c>app.json</c> declares no range. Read
    /// only when <see cref="IdRangeSize"/> is blank, so a size the Editor typed
    /// always wins and a blank one follows the repository (#1631).
    /// </summary>
    public int? SourceIdRangeSize { get; set; }

    /// <summary>
    /// <c>owner/name</c> of the github.com repository this module's files are
    /// copied from, or null for a module whose files are typed in the editor.
    /// The copy lives in <see cref="ExtensionFolders"/> like any other module's,
    /// so generation never calls GitHub; it is refreshed on save, on "Update now",
    /// on a push to the branch and daily. See <c>.design/templates-and-seeding.md</c>,
    /// "Modules from a GitHub repository" (#1415).
    /// </summary>
    public string? SourceRepository { get; set; }

    /// <summary>Branch, tag or commit to copy; null means the repository's default branch.</summary>
    public string? SourceRef { get; set; }

    /// <summary>Folder inside the repository that holds the extension; null means the repository root.</summary>
    public string? SourcePath { get; set; }

    /// <summary>The commit the current copy came from.</summary>
    public string? SourceCommitSha { get; set; }

    /// <summary>When the copy was last replaced.</summary>
    public DateTime? SourceFetchedAt { get; set; }

    /// <summary>
    /// When the repository was last asked, whether or not anything changed.
    /// Written without the audit log, as is <see cref="SourceError"/>: a daily
    /// "nothing changed" is not an event worth a row.
    /// </summary>
    public DateTime? SourceCheckedAt { get; set; }

    /// <summary>
    /// Why the last refresh failed, cleared by the next good one. The copy is
    /// kept when a refresh fails.
    /// </summary>
    public string? SourceError { get; set; }

    /// <summary>
    /// JSON list of the files the last copy left out and why
    /// (<see cref="Services.Templates.ModuleSourceSkippedFile"/>), for the module page.
    /// </summary>
    public string? SourceSkippedJson { get; set; }

    /// <summary>
    /// JSON list of the dependencies the repository's own <c>app.json</c>
    /// declared at the last copy (<see cref="Services.Templates.ModuleDependencyInput"/>).
    /// Merged with <see cref="Dependencies"/> at generation, the typed entry
    /// winning when both name the same app.
    /// </summary>
    public string? SourceDependenciesJson { get; set; }

    public bool Deprecated { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>The dependencies this module appends to its cloned extension's <c>app.json</c>.</summary>
    public List<ModuleDependency> Dependencies { get; set; } = new();

    /// <summary>
    /// Top-level folders in the module's extension layout. Children live
    /// recursively in <see cref="ModuleExtensionFolder.Folders"/>; files attach
    /// at any depth via <see cref="ModuleExtensionFolder.Files"/>.
    /// </summary>
    public List<ModuleExtensionFolder> ExtensionFolders { get; set; } = new();
}
