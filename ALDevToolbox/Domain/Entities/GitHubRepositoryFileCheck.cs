namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// What the last workspace files check found in one repository (issue #1719, part 2):
/// which of the files the workbench keeps in step - the repository standards, the
/// always-included files stored as-is, and the workspace settings - are missing or
/// differ from what the organisation has now, and the update pull request already open
/// for them, if any.
///
/// <para>One row per repository, replaced by each check. Recomputable from GitHub, but
/// restored with the organisation like the drift findings beside it. See
/// <c>.design/github-integration-phase2.md</c>, "Workspace files".</para>
/// </summary>
public class GitHubRepositoryFileCheck
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary><c>owner/name</c> in lower case, so one repository is one key however it was spelled.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>Repository-relative paths the organisation expects that the repository does not have.</summary>
    public List<string> MissingPaths { get; set; } = new();

    /// <summary>Repository-relative paths the repository has with different content.</summary>
    public List<string> ChangedPaths { get; set; } = new();

    /// <summary>The open workspace files pull request when the check ran, if any.</summary>
    public int? PullRequestNumber { get; set; }

    public string? PullRequestUrl { get; set; }

    /// <summary>When it was read (UTC).</summary>
    public DateTime CheckedAt { get; set; }
}
