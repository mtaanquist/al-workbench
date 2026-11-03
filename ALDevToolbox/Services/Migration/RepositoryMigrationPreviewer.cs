using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;

namespace ALDevToolbox.Services.Migration;

/// <summary>
/// Runs one Migrate Repositories preview (issues #1699, #1700): clones <c>main</c>
/// of each source repository with the Admin's own Azure DevOps token, reads what
/// the clones hold, builds the new workspace from them with
/// <see cref="MigrationWorkspaceBuilder"/>, and records the outcome on
/// <see cref="RepositoryMigrationQueue"/>.
/// A preview writes nothing anywhere but a temporary folder, which is removed at
/// the end. A migration run (#1701) does the same reading again, checks that
/// <c>main</c> has not moved since the preview, and then hands the workspace to
/// <see cref="RepositoryMigrationPublisher"/>.
///
/// <para><strong>The clone never downloads <c>.alpackages</c>.</strong> Some
/// source repositories are several gigabytes because <c>.alpackages</c> was
/// committed and changed over the years. A shallow, blobless clone fetches only
/// the newest commit's folder listing, and the sparse checkout then fetches the
/// files of that commit except those under any <c>.alpackages</c> folder. Old
/// versions of a file are never fetched at all.</para>
///
/// <para>Runs inside <see cref="AmbientOrganizationScope"/> for the Admin who
/// asked, so <see cref="UserRepositoryTokenService"/> finds their token.</para>
/// </summary>
public sealed class RepositoryMigrationPreviewer
{
    /// <summary>The only branch a migration takes (decided on #1698).</summary>
    public const string Branch = "main";

    /// <summary>Prefix of the temporary folder each preview clones into.</summary>
    public const string TempPrefix = "aldt-migrate-";

    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

    // Everything in the commit except .alpackages folders at any depth. Sparse
    // patterns match case-sensitively, so the spellings seen in AL repositories are
    // listed; the analysis skips the folder whatever its case.
    private static readonly string[] SparsePatterns = ["/*", "!.alpackages/", "!.ALPackages/", "!.AlPackages/", "!.Alpackages/"];

    private readonly IProcessRunner _processes;
    private readonly UserRepositoryTokenService _tokens;
    private readonly RepositoryMigrationQueue _queue;
    private readonly MigrationWorkspaceBuilder _workspace;
    private readonly RepositoryMigrationPublisher _publisher;
    private readonly ILogger<RepositoryMigrationPreviewer> _logger;

    public RepositoryMigrationPreviewer(
        IProcessRunner processes,
        UserRepositoryTokenService tokens,
        RepositoryMigrationQueue queue,
        MigrationWorkspaceBuilder workspace,
        RepositoryMigrationPublisher publisher,
        ILogger<RepositoryMigrationPreviewer> logger)
    {
        _processes = processes;
        _tokens = tokens;
        _queue = queue;
        _workspace = workspace;
        _publisher = publisher;
        _logger = logger;
    }

    /// <summary>Folder the clones of every preview go under. Settable so a test can keep them in its own.</summary>
    internal string WorkRoot { get; set; } = Path.GetTempPath();

    /// <summary>Called with each clone's folder before it is read. Test seam: the folder is gone once the run ends.</summary>
    internal Action<string>? Cloned { get; set; }

    /// <summary>Clones and reads every repository of <paramref name="job"/>, recording each step on the queue.</summary>
    public async Task RunAsync(MigrationPreviewJob job, CancellationToken ct)
    {
        _queue.MarkRunning(job.Id);
        var token = await _tokens.ResolveTokenAsync(RepositoryProvider.AzureDevOps, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
        {
            _queue.MarkFailed(job.Id,
                "Your Azure DevOps token is gone or can no longer be read. Add it again under Account → Repository access, then preview again.");
            return;
        }

        var gitPath = GitPath();
        var root = Path.Combine(WorkRoot, TempPrefix + job.Id.ToString("N"));
        Directory.CreateDirectory(root);
        var started = DateTime.UtcNow;
        try
        {
            var env = ProjectBuildService.GitAuthEnv(RepositoryProvider.AzureDevOps, token);
            var cloned = new List<ClonedSource>();
            var commits = new Dictionary<string, string>(StringComparer.Ordinal);
            var failed = new List<MigrationFinding>();
            var index = 0;
            foreach (var repository in job.Repositories)
            {
                ct.ThrowIfCancellationRequested();
                _queue.SetRepository(job.Id, repository.Name, MigrationRepositoryStatus.Cloning);
                var dest = Path.Combine(root, $"repo-{index++}");
                if (FreeBytes(root) is { } free && free < MinFreeBytes)
                {
                    const string full = "The server is running out of disk space, so this repository was not copied. Ask whoever runs AL Workbench for your company to free some up.";
                    _queue.SetRepository(job.Id, repository.Name, MigrationRepositoryStatus.Failed, message: full);
                    failed.Add(new MigrationFinding(MigrationFindingSeverity.Blocking, repository.Name, full));
                    _logger.LogWarning("Migration preview {PreviewId}: only {FreeBytes} bytes free under {Folder}; skipped {Repository}.", job.Id, free, root, repository.Name);
                    continue;
                }
                var (commit, failure) = await CloneAsync(gitPath, repository, dest, env, token, ct).ConfigureAwait(false);
                if (failure is not null)
                {
                    _queue.SetRepository(job.Id, repository.Name, MigrationRepositoryStatus.Failed, message: failure);
                    failed.Add(new MigrationFinding(MigrationFindingSeverity.Blocking, repository.Name, failure));
                    continue;
                }
                _queue.SetRepository(job.Id, repository.Name, MigrationRepositoryStatus.Cloned, commit);
                if (commit is not null) commits[repository.Name] = commit;
                Cloned?.Invoke(dest);
                cloned.Add(new ClonedSource(repository.Name, dest, repository.WebUrl));
            }

            if (job.Create is { } pinned)
            {
                var moved = job.Repositories
                    .Where(r => commits.TryGetValue(r.Name, out var now) && pinned.PinnedCommits.TryGetValue(r.Name, out var then) && now != then)
                    .Select(r => r.Name)
                    .ToList();
                if (moved.Count > 0)
                {
                    _queue.MarkFailed(job.Id,
                        $"main has changed in {string.Join(", ", moved)} since the preview. Nothing was created. Preview again to see what would move now.");
                    return;
                }
            }

            var analysis = MigrationSourceAnalyzer.Analyze(cloned);
            analysis = analysis with { Findings = failed.Concat(analysis.Findings).ToList() };

            // The workspace is only worth building once the sources are sound:
            // a folder clash or a duplicate id would make it wrong.
            MigrationWorkspace? workspace = null;
            if (analysis.CanMigrate)
            {
                if (job.Create is not null) _queue.SetStage(job.Id, "Laying out the new repository");
                try
                {
                    workspace = await _workspace.BuildAsync(job.Target, cloned, analysis, commits, Path.Combine(root, "workspace"), ct, job.Project)
                        .ConfigureAwait(false);
                }
                catch (PlanValidationException ex)
                {
                    _queue.MarkFailed(job.Id, "The template can't be used: " + string.Join(" ", ex.Errors.Values));
                    return;
                }
            }
            if (job.Create is { } create && workspace is { CanMigrate: true })
            {
                _queue.SetStage(job.Id, "Creating the repository on GitHub");
                MigrationOutcome outcome;
                try
                {
                    outcome = await _publisher.PublishAsync(job, create, Path.Combine(root, "workspace"), commits, ct).ConfigureAwait(false);
                }
                catch (PlanValidationException ex)
                {
                    _queue.MarkFailed(job.Id, string.Join(" ", ex.Errors.Values));
                    return;
                }
                _queue.MarkMigrated(job.Id, analysis, workspace, outcome);
                return;
            }

            _queue.MarkDone(job.Id, analysis, workspace);
            _logger.LogInformation(
                "Migration preview {PreviewId} of {Project} read {RepositoryCount} repositories ({FailedCount} failed) and found {ExtensionCount} extensions in {Duration}.",
                job.Id, job.Project.DisplayName, job.Repositories.Count, failed.Count, analysis.Extensions.Count, DateTime.UtcNow - started);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _queue.MarkFailed(job.Id, job.Create is null
                ? "The server stopped while this preview ran. Start it again."
                : "The server stopped while this migration ran. Check Earlier migrations and GitHub for a half-made repository before you start again.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Migration preview {PreviewId} of {Project} failed.", job.Id, job.Project.DisplayName);
            _queue.MarkFailed(job.Id, job.Create is null
                ? "The preview failed unexpectedly. Try again. If it fails again, ask whoever runs AL Workbench for your company to look into it."
                : "The migration failed unexpectedly. Check GitHub for a half-made repository and delete it before you try again. If it fails again, ask whoever runs AL Workbench for your company to look into it.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// Clones <c>main</c> of <paramref name="repository"/> into <paramref name="dest"/>
    /// without <c>.alpackages</c>. Returns the commit read, or why it could not be.
    /// </summary>
    private async Task<(string? Commit, string? Failure)> CloneAsync(
        string gitPath, AzureDevOpsRepository repository, string dest,
        IReadOnlyDictionary<string, string> env, string token, CancellationToken ct)
    {
        if (repository.IsDisabled)
        {
            return (null, "This repository is switched off in Azure DevOps, so it can't be read. Switch it on there, or leave it out.");
        }

        // Asking for main first turns "no such branch" into its own answer rather
        // than a clone failure in git's words.
        var probe = await _processes.RunAsync(new ProcessRunRequest(gitPath,
            ["ls-remote", "--heads", repository.CloneUrl, "refs/heads/" + Branch], Path.GetDirectoryName(dest), env, ProbeTimeout), ct).ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            _logger.LogWarning("Migration: could not reach {Repository} (exit {Exit}).", repository.CloneUrl, probe.ExitCode);
            return (null, "Couldn't reach this repository: " + GitMessage(probe, token));
        }
        if (string.IsNullOrWhiteSpace(probe.StdOut))
        {
            return (null, $"This repository has no {Branch} branch. Only {Branch} is moved, so merge the work into it first.");
        }

        var steps = new (string[] Args, string What)[]
        {
            (["clone", "--filter=blob:none", "--no-checkout", "--depth", "1", "--single-branch", "--branch", Branch,
              "--no-tags", "--quiet", repository.CloneUrl, dest], "clone"),
            (["-C", dest, "sparse-checkout", "set", "--no-cone", .. SparsePatterns], "read"),
            (["-C", dest, "checkout", "--quiet"], "read"),
        };
        foreach (var (args, what) in steps)
        {
            var result = await _processes.RunAsync(new ProcessRunRequest(gitPath, args, Path.GetDirectoryName(dest), env, GitTimeout), ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _logger.LogWarning("Migration: git {Step} of {Repository} failed (exit {Exit}).", what, repository.CloneUrl, result.ExitCode);
                return (null, $"Couldn't {what} this repository: " + GitMessage(result, token));
            }
        }

        var head = await _processes.RunAsync(new ProcessRunRequest(gitPath, ["-C", dest, "rev-parse", "HEAD"], dest, null, ProbeTimeout), ct).ConfigureAwait(false);
        return (head.Succeeded ? head.StdOut.Trim() : null, null);
    }

    /// <summary>The git executable: <c>GIT_PATH</c> when set, else whatever <c>git</c> the path finds.</summary>
    internal static string GitPath() =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GIT_PATH"))
            ? "git"
            : Environment.GetEnvironmentVariable("GIT_PATH")!;

    /// <summary>A clone is not started with less free space than this under the work folder.</summary>
    internal const long MinFreeBytes = 2L * 1024 * 1024 * 1024;

    private static long? FreeBytes(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    private static string GitMessage(ProcessRunResult result, string token)
    {
        var text = ProjectBuildService.Sanitize((result.StdErr.Length > 0 ? result.StdErr : result.StdOut).Trim(), token);
        if (text.Length == 0) return $"git stopped with exit code {result.ExitCode}.";
        return text.Length > 500 ? text[..500] + "..." : text;
    }

    private void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the migration preview folder {Folder}.", root);
        }
    }
}
