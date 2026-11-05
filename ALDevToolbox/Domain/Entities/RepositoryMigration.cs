using ALDevToolbox.Domain.Entities.ObjectExplorer;

namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One run of Migrate repositories that created, or tried to create, a GitHub
/// repository (issue #1701). Kept so an Admin moving a hundred customers can see
/// which are done, and so a later run can tell that a solution came from an
/// earlier migration. See <c>.design/repository-migration.md</c>.
/// </summary>
public class RepositoryMigration
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Who ran it. Null once that user is deleted; the row stays.</summary>
    public int? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The Azure DevOps project, as "organisation/project".</summary>
    public string SourceProject { get; set; } = string.Empty;

    /// <summary>The project's address.</summary>
    public string SourceProjectUrl { get; set; } = string.Empty;

    /// <summary>
    /// The repositories moved and the commit of <c>main</c> each was read at, one
    /// "name@commit" per line. Only ever shown, never parsed for anything that acts.
    /// </summary>
    public string Sources { get; set; } = string.Empty;

    /// <summary>
    /// Azure DevOps's ids for the repositories that were chosen, one per line, so
    /// "Migrate again" can tick the same ones. Empty on rows from before #1706.
    /// </summary>
    public string SourceRepositoryIds { get; set; } = string.Empty;

    /// <summary>The customer's name, as the solution was named.</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>The short name the Admin gave, if any; refilled by "Migrate again".</summary>
    public string? ShortName { get; set; }

    public string TemplateKey { get; set; } = string.Empty;

    /// <summary>Whether the template's own extensions were asked for; refilled by "Migrate again".</summary>
    public bool AddTemplateExtensions { get; set; }

    /// <summary>"owner/name" of the GitHub repository, once it was created.</summary>
    public string? RepositoryFullName { get; set; }

    /// <summary>The repository's page on GitHub, once it was created.</summary>
    public string? RepositoryUrl { get; set; }

    /// <summary>
    /// GitHub's number for the repository, once it was created. It survives a
    /// rename, so "is that repository gone" is asked by it rather than by
    /// <see cref="RepositoryFullName"/>. Null on rows from before #1706.
    /// </summary>
    public long? RepositoryGitHubId { get; set; }

    /// <summary>The solution it was registered on. Null when registering failed, or once that solution is gone.</summary>
    public int? ProjectId { get; set; }
    public OeProject? Project { get; set; }

    /// <summary>True when the repository was created and holds the workspace.</summary>
    public bool Succeeded { get; set; }

    /// <summary>Why it failed, or the warnings beside a success, in the words the page showed.</summary>
    public string? Message { get; set; }
}
