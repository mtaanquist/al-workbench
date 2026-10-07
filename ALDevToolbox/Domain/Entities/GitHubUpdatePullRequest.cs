namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One Business Central update pull request the workbench opened on a repository, by
/// version (issue #1104). Written for every update pull request it opens, by hand or
/// on its own, and kept after it is merged or closed.
///
/// <para>This is what keeps the automatic run from opening the same pull request
/// again after someone closed it: the branch is usually deleted with it, so GitHub
/// alone cannot say that a version was already offered and turned down. It is also
/// how the run finds its own earlier pull request to close when the environment has
/// moved on to a newer version. See <c>.design/github-integration-phase2.md</c>.</para>
/// </summary>
public class GitHubUpdatePullRequest
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary><c>owner/name</c> in lower case, so one repository is one key however it was spelled.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>The Business Central version it moves the repository to, as <c>major.minor</c>.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The pull request's number on GitHub.</summary>
    public int PullRequestNumber { get; set; }

    /// <summary>Where it is on GitHub.</summary>
    public string HtmlUrl { get; set; } = string.Empty;

    /// <summary>True when the nightly run opened it rather than a person pressing the button.</summary>
    public bool IsAutomatic { get; set; }

    /// <summary>When it was opened (UTC).</summary>
    public DateTime OpenedAt { get; set; }

    /// <summary>
    /// When the workbench closed it because a newer version's pull request replaced it
    /// (UTC). Null while the workbench has not closed it, whatever happened to it on GitHub.
    /// </summary>
    public DateTime? SupersededAt { get; set; }
}
