using System.Collections.Concurrent;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// In-process hand-off from the import endpoint to <see cref="ReleaseImportWorker"/>
/// for the DVD-scale paths (folder-ZIP upload, URL download). Builds have their own
/// line, <see cref="ProjectBuildQueue"/> (#1137). A bounded
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
    //
    // Given the maintenance flag (production DI always does), an import queued
    // before a restore replaced the database is dropped rather than run against
    // the restored one (#1538).
    public ReleaseImportQueue(Operations.MaintenanceModeState? maintenance = null)
        : base(capacity: 16, maintenance: maintenance) { }

    /// <summary>
    /// A job dropped across a restore frees its paced slot like one the worker took,
    /// and its staged upload, which only the job's run would otherwise delete.
    /// </summary>
    protected override void OnDiscarded(ReleaseImportJob job, long epoch)
    {
        MarkPickedUp(job);
        DeleteStagedUpload(job);
    }

    /// <summary>
    /// Deletes the temp file an uploaded ZIP or C/AL export was staged to, for a job
    /// that is dropped without running. Without this the file stays in the
    /// container's <c>/tmp</c> until the next start sweeps it, and a few dropped
    /// multi-GB uploads can fill the scratch space new uploads are staged to (#1599).
    /// </summary>
    internal static void DeleteStagedUpload(ReleaseImportJob job)
    {
        var path = job.Source switch
        {
            ReleaseImportSource.StagedZip zip => zip.TempPath,
            ReleaseImportSource.CalTxt cal => cal.TempPath,
            _ => null,
        };
        if (path is null) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// How many paced jobs may wait in the channel at once. One keeps the worker
    /// busy (the next backfill is always ready) while an import someone submits
    /// meanwhile waits behind at most one of them, plus the one running (#1542).
    /// </summary>
    public const int PacedJobLimit = 1;

    private readonly SemaphoreSlim _pacedSlots = new(PacedJobLimit, PacedJobLimit);
    private readonly ConcurrentDictionary<long, byte> _pacedRowIds = new();

    /// <summary>
    /// Queues a job of a bulk run (the system-reference backfill of every release)
    /// without letting the run fill the channel: waits until fewer than
    /// <see cref="PacedJobLimit"/> paced jobs are waiting, then writes it. The worker
    /// frees the slot through <see cref="MarkPickedUp"/>. Without this the feed kept
    /// all sixteen slots full for hours, so a user's import, a Retry or the nightly
    /// auto-import sat behind sixteen backfills.
    /// </summary>
    /// <param name="epoch">
    /// The restore epoch the job was built under (see <see cref="JobQueue{TJob}.Epoch"/>).
    /// Defaults to the epoch at the call, taken before the wait for a slot, so a
    /// restore during that wait still drops the job (#1538).
    /// </param>
    public async Task EnqueuePacedAsync(ReleaseImportJob job, CancellationToken ct = default, long? epoch = null)
    {
        var builtUnder = epoch ?? Epoch;
        await _pacedSlots.WaitAsync(ct).ConfigureAwait(false);
        _pacedRowIds[job.JobRowId] = 0;
        try
        {
            await EnqueueAsync(job, ct, builtUnder).ConfigureAwait(false);
        }
        catch
        {
            if (_pacedRowIds.TryRemove(job.JobRowId, out _)) _pacedSlots.Release();
            throw;
        }
    }

    /// <summary>
    /// The worker calls this as it takes a job off the channel. Frees a paced slot
    /// when the job was queued by <see cref="EnqueuePacedAsync"/>; any other job is
    /// ignored.
    /// </summary>
    public void MarkPickedUp(ReleaseImportJob job)
    {
        if (_pacedRowIds.TryRemove(job.JobRowId, out _)) _pacedSlots.Release();
    }
}

/// <summary>
/// A queued release import. The release row already exists in <c>ingesting</c>
/// state (created synchronously in the request so it shows in the list);
/// the worker materialises the uploads from <see cref="Source"/> and runs the
/// ingest under the captured <see cref="Identity"/>.
///
/// <para><see cref="BuildOrder"/> is set on project and pull request builds only:
/// where the build stands in <see cref="ProjectBuildQueue"/>.</para>
///
/// <para><see cref="JobRowId"/> points at the durable <c>oe_import_jobs</c>
/// row managed by <see cref="PersistedImportJobs"/>. The worker updates that
/// row's status (running → completed / failed) as it progresses so the admin
/// page reflects current state and the startup reconciler can re-enqueue
/// URL-source rows that survived a restart.</para>
///
/// <para><see cref="Resumed"/> marks a job the startup reconciler put back after a
/// restart. The interrupted run may have left part of a module and some of the
/// release's references behind, so the worker wipes the release's ingested rows
/// before it runs the job again (#1209).</para>
/// </summary>
public sealed record ReleaseImportJob(
    int ReleaseId,
    AmbientOrganizationScope.OrganizationIdentity Identity,
    ReleaseImportSource Source,
    bool StoreSymbolReference = false,
    long JobRowId = 0,
    ProjectBuildOrder? BuildOrder = null,
    bool Resumed = false);

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
    /// <para>It rides the same build queue and the same worker branch as a manual build
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
