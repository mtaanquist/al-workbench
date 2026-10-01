namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// A named build configuration that belongs to a <see cref="OeProject"/>. A project
/// has <em>multiple</em> pipelines on purpose: different customer environments get
/// different subsets of extensions (and, in future, different delivery targets), so
/// a pipeline is "a flow of its own" rather than a single build. Running a pipeline
/// produces a <see cref="OeProjectBuild"/>; the pipeline owns the extension selection
/// (<see cref="RequestedAppIdsJson"/>), and each build snapshots it at run time.
/// Org-scoped via the standard query filter; soft-deleted. See
/// <c>.design/artifacts.md</c>.
/// </summary>
public class OePipeline
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>The project (customer) this pipeline belongs to. Pipelines ride along on the project's lifecycle.</summary>
    public int ProjectId { get; set; }
    public OeProject? Project { get; set; }

    /// <summary>
    /// The user who created the pipeline — its owner of record. Nullable
    /// (<c>ON DELETE SET NULL</c>) so a pipeline outlives the account that created
    /// it; management rights come from the parent project's owner via
    /// <c>ProjectAccess</c>, not this column.
    /// </summary>
    public int? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    /// <summary>Display name, unique per project among active rows.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The extensions this pipeline compiles, as a JSON array of app-id GUID
    /// strings. <c>null</c> means "build every discovered extension" — the default,
    /// and what the backfilled <c>Default</c> pipeline carries. Copied onto each
    /// <see cref="OeProjectBuild.RequestedAppIdsJson"/> at run time so the build is a
    /// faithful snapshot even after the pipeline's selection is later edited.
    /// </summary>
    public string? RequestedAppIdsJson { get; set; }

    /// <summary>
    /// The solution repository each successful build is published to as a GitHub
    /// Release. Null (the default) means builds are not published anywhere; non-null
    /// means "publish every successful build there", tagged <c>v&lt;version&gt;</c>.
    /// Nullable FK with <c>ON DELETE SET NULL</c>, so removing a repository from the
    /// solution turns publishing off rather than deleting the pipeline. See
    /// <c>.design/github-integration-phase2.md</c> (#632).
    /// </summary>
    public int? GithubReleaseRepositoryId { get; set; }
    public OeProjectRepository? GithubReleaseRepository { get; set; }

    /// <summary>
    /// The branch this pipeline builds and watches, checked out in every repository
    /// of the solution. Null means each repository's default branch, which is what
    /// a build took before pipelines had a branch. Held to
    /// <c>GitBranchName.IsValid</c>. A pull-request build ignores it and keeps its
    /// own head. See <c>.design/github-integration-phase2.md</c>, "Branch watching"
    /// (#963).
    /// </summary>
    public string? Branch { get; set; }

    /// <summary>
    /// Which Business Central version this pipeline's builds compile against: one of
    /// <see cref="ProjectBuildTarget"/>, <c>current</c> by default. Next minor and
    /// next major compile against Microsoft's preview builds to show breaking changes
    /// early, and their builds are check-only. Copied onto each
    /// <see cref="OeProjectBuild.BcTarget"/> when the build starts. Not the old
    /// <c>version_mode</c>, which became the deployment pipeline's
    /// <c>deployment_schedule</c> and means a time, not a version. See
    /// <c>.design/object-explorer-project-builds.md</c>, "Building against the next version".
    /// </summary>
    public string BcTarget { get; set; } = ProjectBuildTarget.Current;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. Hidden from lists unless restored; past builds stay reachable.</summary>
    public DateTime? DeletedAt { get; set; }

    public ICollection<OeProjectBuild> Builds { get; set; } = new List<OeProjectBuild>();
}
