using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.ObjectExplorer.Import;

namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One build of a <see cref="OeProject"/> — a first-class entity split off
/// <see cref="OeRelease"/>. A build is a <em>set</em> of <c>(repository, commit)</c>
/// pairs (<see cref="OeProjectBuildRepoCommit"/>) with captured logs
/// (<see cref="OeProjectBuildLog"/>), a per-repo changelog
/// (<see cref="OeProjectBuildCommit"/>), and the retained downloadable <c>.app</c>
/// deliverables (<see cref="OeProjectBuildArtifact"/>) — none of which a Release
/// models. It still produces exactly one <c>project</c>-kind Release for Object
/// Explorer object navigation, referenced by <see cref="ReleaseId"/> (the
/// importer hook). Org-scoped via the standard query filter. See
/// <c>.design/artifacts.md</c>.
/// </summary>
public class OeProjectBuild
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>The project this build belongs to. Builds ride along on the project's soft-delete.</summary>
    public int ProjectId { get; set; }
    public OeProject? Project { get; set; }

    /// <summary>
    /// The pipeline this build is a run of. Nullable (<c>ON DELETE SET NULL</c>) so
    /// deleting a pipeline doesn't destroy its build history — the build keeps its
    /// deliverables and stays attributable via <see cref="ProjectId"/>. Null only
    /// for migration-synthesised legacy builds before the Default pipeline backfill.
    /// </summary>
    public int? PipelineId { get; set; }
    public OePipeline? Pipeline { get; set; }

    /// <summary>
    /// User who triggered the build (the clone runs as them, using their per-user
    /// repository token). Nullable so a build outlives the account that started it
    /// (FK <c>ON DELETE SET NULL</c>) and so migration-synthesised legacy builds
    /// without a known starter are representable.
    /// </summary>
    public int? StartedByUserId { get; set; }
    public User? StartedByUser { get; set; }

    /// <summary>
    /// The produced <c>project</c>-kind <see cref="OeRelease"/> — the Object Explorer
    /// hook that keeps the build's objects navigable. Nullable: set once the
    /// release row exists, and cleared (<c>ON DELETE SET NULL</c>) if the release
    /// is later reaped, leaving the build's deliverables and logs intact.
    /// </summary>
    public int? ReleaseId { get; set; }
    public OeRelease? Release { get; set; }

    /// <summary>
    /// The branch built. A manual build snapshots its pipeline's branch here and
    /// clones it (null = each repository's default branch, #963); a pull-request
    /// build stamps the head ref as a provenance label and checks out the head
    /// commit instead.
    /// </summary>
    public string? Branch { get; set; }

    /// <summary>
    /// The branch the repositories were actually on when <see cref="Branch"/> is null,
    /// read from each clone, so a build of the default branch can still say which one
    /// that was. Several names, comma-separated, when repositories differ. Null for a
    /// pull-request build, a build that named its branch, and builds made before this
    /// was recorded. Display only: the deployment branch rule reads <see cref="Branch"/>.
    /// </summary>
    public string? DefaultBranch { get; set; }

    /// <summary>
    /// What asked for this build: <c>manual</c> (a person pressed Build) or
    /// <c>pull_request</c> (GitHub told us a pull request moved). See
    /// <see cref="ProjectBuildTrigger"/>. Existing rows are <c>manual</c>, which
    /// is what they were.
    /// </summary>
    public string Trigger { get; set; } = ProjectBuildTrigger.Manual;

    /// <summary>The pull request this build is about, for a <c>pull_request</c> build; null otherwise.</summary>
    public int? PullRequestNumber { get; set; }

    /// <summary>
    /// The exact commit built. Set for a pull-request build, where the head is
    /// the whole point and moves under the branch name, and for a build started by
    /// a push, which builds the commit that push left the branch on even when later
    /// pushes have moved it since; null for a manual build, whose per-repository
    /// commits are recorded as <see cref="RepoCommits"/>.
    /// </summary>
    public string? HeadSha { get; set; }

    /// <summary>
    /// For a build started by a push: the solution repository that was pushed to,
    /// the one checked out at <see cref="HeadSha"/>. The solution's other
    /// repositories are cloned at the pipeline's branch as usual. Null for every
    /// other build. Deliberately not a foreign key: it only steers the checkout, and a
    /// repository removed while the build waits simply leaves nothing to pin.
    /// </summary>
    public int? HeadRepositoryId { get; set; }

    /// <summary>
    /// The GitHub check run this build reports into, so the worker can complete
    /// the run it opened. Null for every build that is not reporting to GitHub.
    /// </summary>
    public long? CheckRunId { get; set; }

    /// <summary>One of <c>queued</c>, <c>building</c>, <c>ready</c>, <c>failed</c>. See <see cref="ProjectBuildStatus"/>.</summary>
    public string Status { get; set; } = ProjectBuildStatus.Queued;

    /// <summary>Resolved BC application version the build compiled against (e.g. <c>25.18</c>). Null until known.</summary>
    public string? BcVersion { get; set; }

    /// <summary>
    /// The exact Business Central build the symbols came from (e.g. <c>29.0.52914.0</c>),
    /// stamped as soon as it is resolved so a build that then fails still says what
    /// it was compiled against. <see cref="BcVersion"/> stays the Major.Minor, because
    /// earlier builds' apps are matched against it as symbols. Null until known, and
    /// for builds made before it was recorded.
    /// </summary>
    public string? BcArtifactVersion { get; set; }

    /// <summary>
    /// Which Business Central version this build compiled against, one of
    /// <see cref="ProjectBuildTarget"/>. Set when the build is started: <c>current</c>
    /// for every build a person or a pull request starts, and next minor or next major
    /// for the nightly preview check. Anything but <c>current</c> is a preview build,
    /// which is check-only: never published as a GitHub release and never deployable.
    /// See <c>.design/object-explorer-project-builds.md</c>, "The nightly preview check".
    /// </summary>
    public string BcTarget { get; set; } = ProjectBuildTarget.Current;

    /// <summary>Why a <c>failed</c> build failed (the whole-build reason); null otherwise.</summary>
    public string? FailureMessage { get; set; }

    /// <summary>
    /// The extensions the user chose to compile, as a JSON array of app-id GUID
    /// strings captured from the "New build" picker's live discovery. <c>null</c>
    /// means "build everything discovered" — today's behaviour, and what a
    /// restart-resumed or migration-synthesised build falls back to. The worker
    /// reads this off the build row and filters the discovered set before compiling.
    /// See <c>.design/artifacts.md</c>.
    /// </summary>
    public string? RequestedAppIdsJson { get; set; }

    /// <summary>
    /// The GitHub Release tag this build was published as (<c>v1.2.3.0</c>, or
    /// <c>build-&lt;number&gt;</c> when its apps have different versions), when the
    /// pipeline names a repository to publish to. It doubles as the marker of a
    /// <em>staged</em> build: a build with a tag and no pipeline was not compiled here
    /// at all but downloaded from a Release so it could be deployed. Null when nothing
    /// was published. See <c>.design/github-integration-phase2.md</c> (#632).
    /// </summary>
    public string? GithubReleaseTag { get; set; }

    /// <summary>The published Release's page on GitHub, for the link on the build card. Null when nothing was published.</summary>
    public string? GithubReleaseUrl { get; set; }

    /// <summary>
    /// Why the build was not published as a Release - GitHub's own refusal, for
    /// instance. A publish failure is never a build failure: the
    /// <c>.app</c> files exist and download regardless, so this is a note on a build
    /// that is still <c>ready</c>.
    /// </summary>
    public string? GithubReleaseError { get; set; }

    public DateTime StartedAt { get; set; }

    /// <summary>When the build reached a terminal state (<c>ready</c> / <c>failed</c>); null while in flight.</summary>
    public DateTime? FinishedAt { get; set; }

    public ICollection<OeProjectBuildRepoCommit> RepoCommits { get; set; } = new List<OeProjectBuildRepoCommit>();
    public ICollection<OeProjectBuildCommit> Changelog { get; set; } = new List<OeProjectBuildCommit>();
    public ICollection<OeProjectBuildArtifact> Artifacts { get; set; } = new List<OeProjectBuildArtifact>();
    public ICollection<OeProjectBuildLog> Logs { get; set; } = new List<OeProjectBuildLog>();
    public ICollection<OeProjectBuildDiagnostic> Diagnostics { get; set; } = new List<OeProjectBuildDiagnostic>();
}

/// <summary>What asked for a <see cref="OeProjectBuild"/>.</summary>
public static class ProjectBuildTrigger
{
    /// <summary>A person pressed Build on a pipeline. The clone uses their own repository token.</summary>
    public const string Manual = "manual";

    /// <summary>
    /// GitHub told us a pull request opened, reopened or gained a commit. There is
    /// no user behind it, so the clone and the check run both act as the app. See
    /// <c>.design/github-integration-phase2.md</c> (#627).
    /// </summary>
    public const string PullRequest = "pull_request";

    /// <summary>
    /// A push to the branch a pipeline watches queued it, because the pipeline builds
    /// on push (<see cref="OePipeline.BuildOnPush"/>). It runs as the person who turned
    /// that on, and checks the pushed repository out at the push's own commit
    /// (<see cref="OeProjectBuild.HeadSha"/>, <see cref="OeProjectBuild.HeadRepositoryId"/>).
    /// Otherwise an ordinary pipeline build. See
    /// <c>.design/github-integration-phase2.md</c>, "Building on push" (#1079).
    /// </summary>
    public const string Push = "push";

    /// <summary>
    /// The nightly preview check on a pipeline queued it, as the person who turned
    /// the check on (<see cref="OePipeline.PreviewCheckByUserId"/>). Always a preview
    /// build. See <c>.design/object-explorer-project-builds.md</c>, "The nightly preview check".
    /// </summary>
    public const string PreviewCheck = "preview_check";
}

/// <summary>
/// The stored names of <see cref="BcBuildTarget"/>, on <see cref="OeProjectBuild.BcTarget"/>. Stored as words rather than the enum's
/// ordinal so the column reads on its own.
/// </summary>
public static class ProjectBuildTarget
{
    /// <summary>The version the extensions' manifests ask for. The default, and what every build before the setting existed was.</summary>
    public const string Current = "current";

    /// <summary>The next minor version, from Microsoft's preview builds.</summary>
    public const string NextMinor = "next_minor";

    /// <summary>The next major version, from Microsoft's preview builds.</summary>
    public const string NextMajor = "next_major";

    /// <summary>The longest stored value, for the column width.</summary>
    public const int MaxLength = 20;

    /// <summary>The targets the nightly preview check builds, in the order they are shown.</summary>
    public static readonly IReadOnlyList<string> Previews = [NextMinor, NextMajor];

    /// <summary>True when a build of this target compiles against a preview, which makes it check-only.</summary>
    public static bool IsPreview(string? value) => value is NextMinor or NextMajor;

    /// <summary>The engine's target for a stored value. Anything unknown is <see cref="BcBuildTarget.Current"/>.</summary>
    public static BcBuildTarget ToBuildTarget(string? value) => value switch
    {
        NextMinor => BcBuildTarget.NextMinor,
        NextMajor => BcBuildTarget.NextMajor,
        _ => BcBuildTarget.Current,
    };

    /// <summary>What the person reads for a stored value: "Current", "Next minor", "Next major".</summary>
    public static string Label(string? value) => value switch
    {
        NextMinor => "Next minor",
        NextMajor => "Next major",
        _ => "Current",
    };
}

/// <summary>The lifecycle states a <see cref="OeProjectBuild"/> moves through.</summary>
public static class ProjectBuildStatus
{
    /// <summary>Created and enqueued; the worker hasn't started cloning yet.</summary>
    public const string Queued = "queued";

    /// <summary>The worker is cloning / compiling / ingesting.</summary>
    public const string Building = "building";

    /// <summary>At least one extension compiled and the release ingested. Deliverables are downloadable.</summary>
    public const string Ready = "ready";

    /// <summary>The build failed as a whole. <see cref="OeProjectBuild.FailureMessage"/> says why.</summary>
    public const string Failed = "failed";
}
