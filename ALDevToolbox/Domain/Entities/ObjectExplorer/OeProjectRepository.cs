using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One Git repository belonging to a <see cref="OeProject"/>. A child table rather
/// than a JSON column because repos are listed, validated (per-provider URL
/// shape), and managed individually. <see cref="OrganizationId"/> is denormalised
/// from the parent so the row carries its own tenant key — consistent with the
/// "every editable entity carries organization_id" fence — and the build uses the
/// org PAT matching <see cref="Provider"/> to clone. See
/// <c>.design/object-explorer-project-builds.md</c>.
/// </summary>
public class OeProjectRepository
{
    public int Id { get; set; }

    /// <summary>Owning organisation (denormalised from the parent project). EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectId { get; set; }
    public OeProject? Project { get; set; }

    /// <summary>The Git host this repo lives on; selects which org PAT clones it. Persisted as a string discriminator.</summary>
    public RepositoryProvider Provider { get; set; }

    /// <summary>The clone URL (HTTPS). Validated per-provider before save.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Short admin-facing label for the repo (e.g. the repo name).</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Who added this repository to the solution, or changed its address. Null for a
    /// row added before this was recorded, or whose person has since been deleted.
    /// </summary>
    public int? AddedByUserId { get; set; }
    public User? AddedByUser { get; set; }

    /// <summary>
    /// When someone confirmed, with their own access, that they can read this
    /// repository. Null means nobody has, and no build clones it until someone does:
    /// a build clones with the access of whoever started it, or of the GitHub App on a
    /// pull request, and a repository nobody vouched for must not reach anyone's
    /// Object Explorer through that. See <c>.design/object-explorer-project-builds.md</c>.
    /// </summary>
    public DateTime? AccessConfirmedAt { get; set; }

    /// <summary>Who confirmed <see cref="AccessConfirmedAt"/>: the person who added it, or someone who pressed Confirm access later.</summary>
    public int? AccessConfirmedByUserId { get; set; }
    public User? AccessConfirmedByUser { get; set; }

    /// <summary>Whether a build may clone this repository. See <see cref="AccessConfirmedAt"/>.</summary>
    public bool IsAccessConfirmed => AccessConfirmedAt is not null;
}
