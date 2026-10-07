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

    /// <summary>
    /// Display name, unique per project among active rows. Generated from the
    /// pipeline's setup by <c>PipelineNames</c> (e.g. <c>main (3 extensions)</c>) unless
    /// <see cref="NameIsCustom"/>.
    /// </summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// True when a person typed the name because the generated one was already taken
    /// in the solution. Such a name is kept on later saves until they clear it.
    /// </summary>
    public bool NameIsCustom { get; set; }

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

    /// <summary>
    /// Whether a push to the branch this pipeline watches starts a build of it: one
    /// build per push, at the commit the push left the branch on, queued behind any
    /// build of the pipeline already waiting so every change gets its own build.
    /// GitHub repositories only, since pushes arrive by GitHub webhook. Off by
    /// default. See <c>.design/github-integration-phase2.md</c>, "Building on push" (#1079).
    /// </summary>
    public bool BuildOnPush { get; set; }

    /// <summary>
    /// Who turned building on push on. Those builds run as this person, the same way
    /// the nightly preview check runs as <see cref="PreviewCheckByUserId"/>: the clone
    /// uses their repository access and the build is theirs on the page. Null once
    /// that user is deleted, which pauses building on push.
    /// </summary>
    public int? BuildOnPushByUserId { get; set; }
    public User? BuildOnPushByUser { get; set; }

    /// <summary>
    /// Why the last push could not start a build (the person who turned it on lost
    /// access or has nothing to clone with), shown on the pipeline. Null when the last
    /// push queued its build.
    /// </summary>
    public string? BuildOnPushBlocked { get; set; }

    /// <summary>
    /// Whether this pipeline's builds number their apps: the build's number is added to
    /// the third part (Build) of each <c>app.json</c> version in the build's own copy of
    /// the repository, so <c>28.2.0.0</c> built as build #4812 compiles as
    /// <c>28.2.4812.0</c>. Nothing is written back to the repository. On by default; the
    /// pull-request check and the nightly preview check never number their apps. See
    /// <c>.design/object-explorer-project-builds.md</c>, "Build numbers in app versions".
    /// </summary>
    public bool AutoVersion { get; set; } = true;

    /// <summary>
    /// Whether this pipeline's builds publish only the extensions that changed. An
    /// extension with no change under its folder since the last build of this pipeline
    /// that produced it still compiles, but the build keeps that earlier <c>.app</c> and
    /// version instead of a new one, and leaves it out of the GitHub release. On by
    /// default; off, every build publishes everything. See
    /// <c>.design/object-explorer-project-builds.md</c>, "Publishing only what changed" (#1094).
    /// </summary>
    public bool ChangedAppsOnly { get; set; } = true;

    /// <summary>
    /// When the pipeline was disabled; null while it is enabled. A disabled pipeline
    /// keeps its settings and builds but starts no new build: Build is refused, and
    /// pushes and the nightly preview check pass it by. Anyone who may manage the
    /// solution can disable or enable it; deleting is for admins only (#1131).
    /// </summary>
    public DateTime? DisabledAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. Hidden from lists unless restored; past builds stay reachable.</summary>
    public DateTime? DeletedAt { get; set; }

    public ICollection<OeProjectBuild> Builds { get; set; } = new List<OeProjectBuild>();
}
