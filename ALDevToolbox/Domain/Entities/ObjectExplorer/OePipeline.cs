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
    /// Whether this pipeline also runs the nightly preview check: a build against
    /// Microsoft's next minor and next major preview versions, each recorded as an
    /// <see cref="OeProjectBuild"/> whose <see cref="OeProjectBuild.BcTarget"/> says
    /// which. The pipeline's own builds stay on the current version. See
    /// <c>.design/object-explorer-project-builds.md</c>, "The nightly preview check".
    /// </summary>
    public bool PreviewCheck { get; set; }

    /// <summary>
    /// Who turned the preview check on. The nightly builds run as this person, the
    /// way a scheduled upgrade action runs as its requester: the clone uses their
    /// repository access and the build is theirs on the page. Null once that user is
    /// deleted, which pauses the check until someone turns it on again.
    /// </summary>
    public int? PreviewCheckByUserId { get; set; }
    public User? PreviewCheckByUser { get; set; }

    /// <summary>
    /// Why the last night's preview check could not start (the person who turned it
    /// on lost access, the solution has no country), shown on the pipeline. Null when
    /// the last attempt queued its builds.
    /// </summary>
    public string? PreviewCheckBlocked { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. Hidden from lists unless restored; past builds stay reachable.</summary>
    public DateTime? DeletedAt { get; set; }

    public ICollection<OeProjectBuild> Builds { get; set; } = new List<OeProjectBuild>();
}
