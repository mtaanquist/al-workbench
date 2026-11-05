namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// What the last drift scan read from one tracked repository, kept whether or not it
/// found anything behind (issue #1719). The findings in <see cref="GitHubRepositoryDrift"/>
/// say what is behind; this row says what the repository is on and when it was read,
/// so the Repositories list can show an up-to-date repository's version as well.
///
/// <para>Replaced wholesale by an organisation-wide scan, and per repository by its
/// row's "Check again". See <c>.design/github-integration-phase2.md</c>,
/// "The Repositories list".</para>
/// </summary>
public class GitHubRepositoryCheck
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary><c>owner/name</c> in lower case, so one repository is one key however it was spelled.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>
    /// The oldest Business Central application version its app.json files ask for, as
    /// <c>major.minor</c>. Null when none of them states one (no app.json, or only test apps).
    /// </summary>
    public string? Application { get; set; }

    /// <summary>When it was read (UTC).</summary>
    public DateTime CheckedAt { get; set; }
}
