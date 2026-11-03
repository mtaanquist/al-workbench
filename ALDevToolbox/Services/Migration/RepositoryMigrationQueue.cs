using System.Collections.Concurrent;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.Migration;

/// <summary>Where a preview has got to.</summary>
public enum MigrationPreviewStatus
{
    /// <summary>Queued behind another preview.</summary>
    Waiting,

    /// <summary>Cloning and reading the repositories.</summary>
    Running,

    /// <summary>Finished; <see cref="MigrationPreview.Analysis"/> holds what was found.</summary>
    Done,

    /// <summary>Stopped by something that was not about one repository.</summary>
    Failed,
}

/// <summary>Where one source repository has got to within a preview.</summary>
public enum MigrationRepositoryStatus
{
    Waiting,
    Cloning,
    Cloned,
    Failed,
}

/// <summary>One source repository's progress, as the page lists it.</summary>
/// <param name="Commit">The commit of <c>main</c> that was read, once cloned.</param>
/// <param name="Message">Why it failed, in words the Admin can act on.</param>
/// <param name="Id">Azure DevOps's id for the repository, so the same set can be previewed again.</param>
public sealed record MigrationRepositoryProgress(
    string Id,
    string Name,
    string WebUrl,
    MigrationRepositoryStatus Status,
    string? Commit = null,
    string? Message = null);

/// <summary>
/// A snapshot of one preview. The queue swaps the whole record on every change
/// rather than mutating it, so a page reading it never sees half an update.
/// </summary>
public sealed record MigrationPreview(
    Guid Id,
    int OrganizationId,
    int UserId,
    string ProjectName,
    string ProjectUrl,
    MigrationPreviewStatus Status,
    IReadOnlyList<MigrationRepositoryProgress> Repositories,
    DateTime StartedAt,
    DateTime UpdatedAt,
    MigrationSourceAnalysis? Analysis = null,
    string? Error = null,
    MigrationWorkspace? Workspace = null,
    string TemplateKey = "",
    string TemplateName = "",
    string CustomerName = "",
    string? ShortName = null,
    bool AddTemplateExtensions = false,
    MigrationCreateRequest? Create = null,
    MigrationOutcome? Outcome = null,
    string? Stage = null,
    Guid? MigratedBy = null)
{
    /// <summary>
    /// True for a migration run rather than a preview: it reads the sources again
    /// and then creates the repository (#1701).
    /// </summary>
    public bool IsMigration => Create is not null;

    /// <summary>True when the repository can be created from this preview: it is ready and no run has used it yet.</summary>
    public bool CanCreate => !IsMigration && Status == MigrationPreviewStatus.Done && CanMigrate && MigratedBy is null;

    /// <summary>True when the sources and the workspace built from them are both fit to move.</summary>
    public bool CanMigrate => Analysis is { CanMigrate: true } && Workspace is { CanMigrate: true };

    /// <summary>True while the worker may still change it.</summary>
    public bool IsActive => Status is MigrationPreviewStatus.Waiting or MigrationPreviewStatus.Running;
}

/// <summary>
/// What the worker needs to run a preview: the snapshot's id, who asked (so the
/// clone uses their Azure DevOps token, resolved under their identity off the
/// request), the repositories Azure DevOps listed for the project, and the
/// template and customer they move into. <paramref name="Create"/> is set when
/// the job is a migration run rather than a preview.
/// </summary>
public sealed record MigrationPreviewJob(
    Guid Id,
    AmbientOrganizationScope.OrganizationIdentity Identity,
    AzureDevOpsProject Project,
    IReadOnlyList<AzureDevOpsRepository> Repositories,
    MigrationTarget Target,
    MigrationCreateRequest? Create = null);

/// <summary>
/// The hand-off from the Migrate Repositories page to
/// <see cref="RepositoryMigrationWorker"/>, and the in-memory record of every
/// preview the page polls (issue #1699).
///
/// <para>Preview state is kept in memory only, like the off-site restore jobs: a
/// preview is a throwaway reading of the sources, so a restart simply loses it
/// and the Admin presses Preview again. Finished previews stay for
/// <see cref="RetainFinishedFor"/> so a reload still shows the result.</para>
///
/// <para>One preview per person at a time, and at most <see cref="Capacity"/>
/// waiting across the server: each one clones whole repositories to disk.</para>
/// </summary>
public sealed class RepositoryMigrationQueue : JobQueue<MigrationPreviewJob>
{
    /// <summary>How many previews may be waiting or running at once.</summary>
    public const int Capacity = 8;

    /// <summary>How long a finished preview stays readable.</summary>
    public static readonly TimeSpan RetainFinishedFor = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<Guid, MigrationPreview> _previews = new();
    private readonly TimeProvider _clock;
    private readonly object _admit = new();

    public RepositoryMigrationQueue(TimeProvider clock, MaintenanceModeState? maintenance = null)
        : base(Capacity, maintenance: maintenance)
    {
        _clock = clock;
    }

    /// <summary>
    /// Records a new preview and queues it. Null when it was not queued: the
    /// person already has one running (<paramref name="refusal"/> says so), or the
    /// server is busy with <see cref="Capacity"/> others.
    /// </summary>
    public MigrationPreview? TryStart(MigrationPreviewJob job, string projectName, string projectUrl, out string? refusal)
    {
        var identity = job.Identity;
        if (identity.UserId is not { } userId)
        {
            throw new InvalidOperationException("A migration preview needs the person who asked for it.");
        }

        lock (_admit)
        {
            Evict();
            if (_previews.Values.Any(p => p.IsActive && p.UserId == userId && p.OrganizationId == identity.OrganizationId))
            {
                refusal = "Your last preview or migration is still running. Wait for it to finish, then try again.";
                return null;
            }
            if (_previews.Values.Count(p => p.IsActive) >= Capacity)
            {
                refusal = "The server is busy with other previews. Try again in a few minutes.";
                return null;
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            var preview = new MigrationPreview(
                job.Id, identity.OrganizationId, userId, projectName, projectUrl, MigrationPreviewStatus.Waiting,
                job.Repositories.Select(r => new MigrationRepositoryProgress(r.Id, r.Name, r.WebUrl, MigrationRepositoryStatus.Waiting)).ToList(),
                now, now,
                TemplateKey: job.Target.TemplateKey,
                TemplateName: job.Target.TemplateName,
                CustomerName: job.Target.CustomerName,
                ShortName: job.Target.Plan.ShortName,
                AddTemplateExtensions: job.Target.AddTemplateExtensions,
                Create: job.Create);
            _previews[job.Id] = preview;
            if (!TryWrite(job))
            {
                _previews.TryRemove(job.Id, out _);
                refusal = "The server is busy with other previews. Try again in a few minutes.";
                return null;
            }

            refusal = null;
            return preview;
        }
    }

    /// <summary>
    /// Marks <paramref name="previewId"/> as used by the run <paramref name="runId"/>,
    /// so one preview creates at most one repository. False when it is not
    /// <see cref="MigrationPreview.CanCreate"/> any more.
    /// </summary>
    internal bool TryClaim(Guid previewId, Guid runId)
    {
        lock (_admit)
        {
            if (!_previews.TryGetValue(previewId, out var preview) || !preview.CanCreate) return false;
            return _previews.TryUpdate(previewId, preview with { MigratedBy = runId }, preview);
        }
    }

    /// <summary>Gives a claimed preview back when its run could not be queued.</summary>
    internal void Release(Guid previewId, Guid runId) =>
        Update(previewId, p => p.MigratedBy == runId ? p with { MigratedBy = null } : p);

    /// <summary>The preview, but only for the person in the organisation who started it.</summary>
    public MigrationPreview? Get(Guid id, int organizationId, int userId) =>
        _previews.TryGetValue(id, out var preview) && preview.OrganizationId == organizationId && preview.UserId == userId
            ? preview
            : null;

    internal void MarkRunning(Guid id) =>
        Update(id, p => p with { Status = MigrationPreviewStatus.Running });

    internal void SetRepository(Guid id, string name, MigrationRepositoryStatus status, string? commit = null, string? message = null) =>
        Update(id, p => p with
        {
            Repositories = p.Repositories
                .Select(r => r.Name == name ? r with { Status = status, Commit = commit ?? r.Commit, Message = message } : r)
                .ToList(),
        });

    internal void MarkDone(Guid id, MigrationSourceAnalysis analysis, MigrationWorkspace? workspace = null) =>
        Update(id, p => p with { Status = MigrationPreviewStatus.Done, Analysis = analysis, Workspace = workspace, Stage = null });

    /// <summary>What a migration run is doing now, for the page's status line.</summary>
    internal void SetStage(Guid id, string stage) =>
        Update(id, p => p with { Stage = stage });

    /// <summary>A migration run that created the repository.</summary>
    internal void MarkMigrated(Guid id, MigrationSourceAnalysis analysis, MigrationWorkspace workspace, MigrationOutcome outcome) =>
        Update(id, p => p with { Status = MigrationPreviewStatus.Done, Analysis = analysis, Workspace = workspace, Outcome = outcome, Stage = null });

    internal void MarkFailed(Guid id, string error) =>
        Update(id, p => p with { Status = MigrationPreviewStatus.Failed, Error = error, Stage = null });

    /// <summary>Marks the preview failed unless it already finished.</summary>
    internal void FailIfActive(Guid id, string error) =>
        Update(id, p => p.IsActive ? p with { Status = MigrationPreviewStatus.Failed, Error = error } : p);

    /// <summary>A preview dropped because a restore replaced the database it was asked under.</summary>
    protected override void OnDiscarded(MigrationPreviewJob job, long epoch) =>
        MarkFailed(job.Id, RestoredWhileWaiting(job));

    /// <summary>Why a job that was waiting when the database was restored will not run.</summary>
    internal static string RestoredWhileWaiting(MigrationPreviewJob job) => job.Create is null
        ? "The server was restored from a backup while this preview waited. Start it again."
        : "The server was restored from a backup while this migration waited. Nothing was created. Preview again.";

    /// <summary>Why a job that stopped without an outcome shows as failed.</summary>
    internal static string StoppedUnexpectedly(MigrationPreviewJob job) => job.Create is null
        ? "The preview stopped unexpectedly. Try again. If it fails again, ask whoever runs AL Workbench for your company to look into it."
        : "The migration stopped unexpectedly. Check Earlier migrations and GitHub for a half-made repository before you try again.";

    private void Update(Guid id, Func<MigrationPreview, MigrationPreview> change)
    {
        while (_previews.TryGetValue(id, out var current))
        {
            var next = change(current) with { UpdatedAt = _clock.GetUtcNow().UtcDateTime };
            if (_previews.TryUpdate(id, next, current)) return;
        }
    }

    private void Evict()
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime - RetainFinishedFor;
        foreach (var (id, preview) in _previews)
        {
            if (!preview.IsActive && preview.UpdatedAt < cutoff) _previews.TryRemove(id, out _);
        }
    }
}
