using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// In-process hand-off from the import endpoint to <see cref="ReleaseImportWorker"/>
/// for the DVD-scale paths (folder-ZIP upload, URL download). A bounded
/// <see cref="System.Threading.Channels.Channel{T}"/> — not an external queue — keeps the "no external
/// services" fence intact while giving the worker a clean back-pressure point.
///
/// <para>
/// The channel itself is in-memory, but every enqueue is mirrored to the
/// <c>oe_import_jobs</c> table via <see cref="PersistedImportJobs"/>. On
/// startup the reconciler re-enqueues every <c>queued</c> / <c>running</c>
/// URL-source row so an interrupted download resumes; staged-zip rows can't
/// be resumed (their temp file lives in container-local <c>/tmp</c> and is
/// gone after a restart), so the reconciler marks those <c>failed</c> with a
/// "restart lost the upload" message instead of stranding them.
/// </para>
/// </summary>
public sealed class ReleaseImportQueue : JobQueue<ReleaseImportJob>
{
    // Small bound: each job can hold a multi-GB temp file and the worker
    // processes one at a time, so we never want a deep backlog. Writers wait
    // briefly rather than the queue growing unbounded. No dedupe gate, unlike
    // the sibling queues: the durable oe_import_jobs row is this queue's guard
    // against a duplicate run, and a re-import of the same release is a
    // legitimate request rather than a double-click to coalesce.
    public ReleaseImportQueue() : base(capacity: 16) { }
}

/// <summary>
/// A queued release import. The release row already exists in <c>ingesting</c>
/// state (created synchronously in the request so it shows in the list);
/// the worker materialises the uploads from <see cref="Source"/> and runs the
/// ingest under the captured <see cref="Identity"/>.
///
/// <para><see cref="JobRowId"/> points at the durable <c>oe_import_jobs</c>
/// row managed by <see cref="PersistedImportJobs"/>. The worker updates that
/// row's status (running → completed / failed) as it progresses so the admin
/// page reflects current state and the startup reconciler can re-enqueue
/// URL-source rows that survived a restart.</para>
/// </summary>
public sealed record ReleaseImportJob(
    int ReleaseId,
    AmbientOrganizationScope.OrganizationIdentity Identity,
    ReleaseImportSource Source,
    bool StoreSymbolReference = false,
    long JobRowId = 0);

/// <summary>Where the worker gets the bytes from.</summary>
public abstract record ReleaseImportSource
{
    /// <summary>Download the full DVD ZIP from a (already allow-list-validated) URL, then keep only the DVD subset.</summary>
    public sealed record Url(string DownloadUrl) : ReleaseImportSource;

    /// <summary>
    /// Resolve and download a Microsoft Business Central OnPrem artifact set —
    /// the application artifact at <paramref name="ApplicationUrl"/> plus the
    /// platform artifact it references — then walk the merged loose <c>.app</c>
    /// files as a DVD subset. The URL is resolved from Microsoft's index before
    /// enqueue (so it's fixed/validated), which keeps the worker resumable like
    /// <see cref="Url"/>. See <c>BcArtifactService</c>.
    /// </summary>
    public sealed record BcArtifact(string ApplicationUrl) : ReleaseImportSource;

    /// <summary>
    /// Compile a project's solution from source: clone its repos, resolve the
    /// matching Microsoft symbols, compile each extension with <c>alc</c>, and
    /// ingest the resulting <c>.app</c>s into the (already-created) project
    /// Release. Resumable like <see cref="BcArtifact"/> — the project id is
    /// enough to re-clone and rebuild idempotently after a restart, since nothing
    /// on disk survives; a build that already cloned once checks out the commits it
    /// recorded then (#1110). See <c>ProjectBuildService</c>.
    /// </summary>
    public sealed record ProjectBuild(int ProjectId) : ReleaseImportSource;

    /// <summary>
    /// The same compile as <see cref="OeProjectBuild"/>, asked for by GitHub rather
    /// than by a person: one repository of the project is checked out at
    /// <paramref name="HeadSha"/> instead of its default branch, every clone
    /// authenticates as the app's installation rather than as a user (there is no
    /// user), and the result is reported back as a check run on the pull request.
    ///
    /// <para>It rides the same queue and the same worker branch as a manual build
    /// on purpose: symbol resolution, the parent-release import and the Object
    /// Explorer ingest are exactly what a reviewer wants a pull request measured
    /// against, and a second code path would drift from the first. See
    /// <c>.design/github-integration-phase2.md</c> (#627).</para>
    /// </summary>
    /// <param name="ForkAuthor">
    /// The member whose own fork the head lives in, or null when the pull request
    /// is on a branch of the repository itself. It is carried so the check run can
    /// say where the code came from - a reviewer reading "from someone's fork"
    /// knows to look harder than at a branch a colleague pushed. Not persisted:
    /// the job is in memory for the life of one build, like the rest of this
    /// record.
    /// </param>
    public sealed record PullRequestBuild(
        int ProjectId,
        int RepositoryId,
        string HeadSha,
        long InstallationId,
        string RepositoryFullName,
        int PullRequestNumber,
        string? ForkAuthor = null) : ReleaseImportSource;

    /// <summary>Open a ZIP already staged to a temp file. <paramref name="IsDvd"/> selects the DVD-subset walk vs the whole-archive walk.</summary>
    public sealed record StagedZip(string TempPath, bool IsDvd) : ReleaseImportSource;

    /// <summary>
    /// A legacy C/AL TXT export staged to a temp file, decoded with the named
    /// codepage ("850" or "1252"). Like <see cref="StagedZip"/> it lives in
    /// container-local <c>/tmp</c> and so is never resumed after a restart.
    /// </summary>
    public sealed record CalTxt(string TempPath, string EncodingName) : ReleaseImportSource;

    /// <summary>
    /// Maintenance re-extraction over already-stored source: repopulate
    /// <c>oe_module_system_references</c> for an existing release without
    /// re-uploading the package (see #291). Carries no payload — the release id
    /// on the job is enough — so unlike the upload sources it survives a restart
    /// only as a failed row to re-trigger, never a lost temp file. The worker
    /// routes it to the AL or C/AL backfill path by inspecting the release.
    /// </summary>
    public sealed record Backfill() : ReleaseImportSource;
}
