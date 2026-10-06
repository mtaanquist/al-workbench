namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One value in one repository's <c>app.json</c> that is behind what the
/// workbench now knows about - what the drift scan found when a new Business
/// Central release landed (issue #630).
///
/// <para>A row is a <em>finding</em>, not a task: the scan replaces the
/// organisation's rows every time it runs, so a value somebody has since
/// bumped by hand simply stops being listed. Nothing here is a decision a
/// person made - what they decide is whether to open the pull request, and
/// that lives on GitHub.</para>
///
/// <para>Per organisation, like everything else editable: the EF query filter
/// scopes reads to <see cref="OrganizationId"/>. See
/// <c>.design/github-integration-phase2.md</c>, issue #630.</para>
/// </summary>
public class GitHubRepositoryDrift
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary><c>owner/name</c>, the form every GitHub route and stored reference uses.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>Which manifest holds the value: <c>app.json</c> at the root, or <c>&lt;folder&gt;/app.json</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// What moved: <c>application</c>, <c>platform</c>, or
    /// <c>dependency:&lt;app id&gt;</c> for one entry of the manifest's
    /// dependency list. The dependency's id rides in the field rather than in
    /// its own column because it is what makes the finding unique inside one
    /// manifest, and the unique index is what lets a rescan replace a row
    /// instead of adding a second one beside it.
    /// </summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>The value the manifest states today, exactly as it is written there.</summary>
    public string Current { get; set; } = string.Empty;

    /// <summary>The value the pull request would put in its place.</summary>
    public string Proposed { get; set; } = string.Empty;

    /// <summary>The release whose import found this. Deleting the release takes its findings with it.</summary>
    public int ReleaseId { get; set; }
    public ObjectExplorer.OeRelease? Release { get; set; }

    /// <summary>
    /// The production environment whose Business Central version <see cref="Proposed"/>
    /// was measured against, when the solution tracking the repository has one with a
    /// known version (issue #1081). Null when the scan fell back to
    /// <see cref="Release"/>. Deleting the environment takes its findings with it, the
    /// same as deleting the release does.
    /// </summary>
    public int? EnvironmentId { get; set; }
    public ObjectExplorer.OeProjectEnvironment? Environment { get; set; }

    /// <summary>When the scan that recorded it ran (UTC).</summary>
    public DateTime DetectedAt { get; set; }
}
