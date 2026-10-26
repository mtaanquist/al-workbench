using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.GitHub;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Templates;

/// <summary>How one refresh of a repository module ended.</summary>
public enum ModuleSourceRefreshOutcome
{
    /// <summary>The repository had moved on and the module now holds the new copy.</summary>
    Updated,

    /// <summary>The repository is still at the commit the module was copied from.</summary>
    Unchanged,

    /// <summary>The refresh failed; the module keeps its copy and shows why.</summary>
    Failed,

    /// <summary>The module is not copied from a repository (or no longer exists).</summary>
    NotARepositoryModule,
}

/// <summary>
/// Modules whose files are copied from a GitHub repository (#1415). The copy is
/// stored in the module's ordinary folder/file rows, so generation reads it from
/// the database like any other module and never calls GitHub. This service is
/// the only thing that talks to GitHub for them: it copies on save, and refreshes
/// the copy on "Update now", on a push to the module's branch
/// (<see cref="GitHubPullRequestBuildWorker"/>) and daily
/// (<see cref="ModuleSourceRefreshScheduler"/>). See
/// <c>.design/templates-and-seeding.md</c>, "Modules from a GitHub repository".
///
/// <para><strong>Which repositories.</strong> Public repositories anywhere, and
/// private ones in the GitHub organisation this organisation connected. They are
/// read with this organisation's own installation token when it has one (an
/// installation token reads public repositories too, at a far higher rate limit),
/// and anonymously when it has none. Saving a private one also needs the acting
/// person to see it with their own linked GitHub account, the rule #1294 set for
/// solution repositories, so the module cannot be used to read a repository its
/// author has no access to. Another organisation's installation is never used.</para>
/// </summary>
public class ModuleSourceService
{
    private readonly AppDbContext _db;
    private readonly ModuleService _modules;
    private readonly GitHubAppClient _github;
    private readonly GitHubConnectionService _connection;
    private readonly GitHubAccessService _access;
    private readonly IOrganizationContext _orgContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<ModuleSourceService> _logger;

    public ModuleSourceService(
        AppDbContext db,
        ModuleService modules,
        GitHubAppClient github,
        GitHubConnectionService connection,
        GitHubAccessService access,
        IOrganizationContext orgContext,
        TimeProvider clock,
        ILogger<ModuleSourceService> logger)
    {
        _db = db;
        _modules = modules;
        _github = github;
        _connection = connection;
        _access = access;
        _orgContext = orgContext;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Creates a module whose files are copied from <paramref name="source"/>. The
    /// copy is made first, so a repository that cannot be read refuses the save
    /// with the reason under the field and no module is created without files.
    /// A blank ID range size takes the size of the repository's own <c>app.json</c>
    /// ranges.
    /// </summary>
    /// <exception cref="PlanValidationException">The module or the source is invalid, or the copy failed.</exception>
    public async Task<Module> CreateAsync(ModuleInput input, ModuleSourceInput source, CancellationToken ct = default)
    {
        var (state, snapshot) = await CopyForSaveAsync(source, ct);
        var module = await _modules.CreateAsync(
            input with { Folders = snapshot.Folders, IdRangeSize = input.IdRangeSize ?? snapshot.IdRangeSize, Source = state }, ct);
        _logger.LogInformation(
            "Created module '{Key}' from {Repository} at {Commit} ({FileCount} files, {Skipped} left out).",
            module.Key, state.Repository, state.CommitSha, snapshot.FileCount, state.Skipped.Count);
        return module;
    }

    /// <summary>
    /// Saves module <paramref name="id"/> with its files copied afresh from
    /// <paramref name="source"/>, replacing whatever it held, typed or copied.
    /// </summary>
    /// <exception cref="PlanValidationException">The module or the source is invalid, or the copy failed.</exception>
    public async Task UpdateAsync(int id, ModuleInput input, ModuleSourceInput source, CancellationToken ct = default)
    {
        var (state, snapshot) = await CopyForSaveAsync(source, ct);
        await _modules.UpdateAsync(
            id, input with { Folders = snapshot.Folders, IdRangeSize = input.IdRangeSize ?? snapshot.IdRangeSize, Source = state }, ct);
        _logger.LogInformation(
            "Saved module {ModuleId} from {Repository} at {Commit} ({FileCount} files, {Skipped} left out).",
            id, state.Repository, state.CommitSha, snapshot.FileCount, state.Skipped.Count);
    }

    /// <summary>
    /// Asks the repository for the commit module <paramref name="moduleId"/>'s
    /// branch, tag or commit names now, and replaces the copy when it moved. A
    /// failure keeps the copy, records why on the module, and never throws for a
    /// repository problem: the callers are a button, a webhook and a nightly pass,
    /// and each reports the outcome its own way.
    /// </summary>
    public async Task<ModuleSourceRefreshOutcome> RefreshAsync(int moduleId, CancellationToken ct = default)
    {
        var module = await _db.Modules.AsNoTracking()
            .Where(m => m.Id == moduleId && m.DeletedAt == null)
            .FirstOrDefaultAsync(ct);
        if (module?.SourceRepository is not { } repository || module.SourceCommitSha is not { } copiedCommit)
        {
            return ModuleSourceRefreshOutcome.NotARepositoryModule;
        }

        try
        {
            // The repository is read again each time, not just its branch: a
            // renamed repository answers with a redirect the commit and archive
            // calls do not follow, and one made private outside the connected
            // organisation may no longer be copied at all.
            var token = await InstallationTokenAsync(ct);
            var (owner, repo) = Split(repository);
            var summary = await _github.GetRepositorySummaryAsync(token, owner, repo, ct);
            var status = await _connection.GetStatusAsync(ct);
            if (summary is null || (summary.IsPrivate && !IsInConnectedOrganisation(summary.FullName, status)))
            {
                return await RecordFailureAsync(module.Id,
                    $"The repository {repository} could not be read. It may have been deleted or made private, or the GitHub App may no longer have access to it.", ct);
            }
            var current = string.IsNullOrEmpty(summary.FullName) ? repository : summary.FullName;
            (owner, repo) = Split(current);

            var commit = await _github.ResolveCommitAsync(token, owner, repo, module.SourceRef, ct);
            if (commit is null)
            {
                return await RecordFailureAsync(module.Id,
                    $"There is no branch, tag or commit called '{module.SourceRef ?? summary.DefaultBranch}' in {current} any more.", ct);
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            if (string.Equals(commit, copiedCommit, StringComparison.OrdinalIgnoreCase))
            {
                await _db.Modules.Where(m => m.Id == module.Id && m.SourceCommitSha == copiedCommit)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.SourceRepository, current)
                        .SetProperty(m => m.SourceCheckedAt, now)
                        .SetProperty(m => m.SourceError, (string?)null), ct);
                return ModuleSourceRefreshOutcome.Unchanged;
            }

            var snapshot = await DownloadAsync(token, owner, repo, commit, module.SourcePath, ct);
            var expected = new ModuleSourceState(repository, module.SourceRef, module.SourcePath, copiedCommit, now, [], []);
            var fresh = new ModuleSourceState(current, module.SourceRef, module.SourcePath, commit, now, snapshot.Skipped, snapshot.Dependencies);
            if (!await _modules.ReplaceSourceCopyAsync(module.Id, expected, snapshot.Folders, fresh, ct))
            {
                _logger.LogInformation(
                    "Dropped a refresh of module '{Key}' from {Repository}: the module changed while the copy was downloading.",
                    module.Key, current);
                return ModuleSourceRefreshOutcome.Unchanged;
            }

            _logger.LogInformation(
                "Updated module '{Key}' from {Repository}: {OldCommit} to {NewCommit} ({FileCount} files).",
                module.Key, current, ShortSha(copiedCommit), ShortSha(commit), snapshot.FileCount);
            return ModuleSourceRefreshOutcome.Updated;
        }
        catch (ModuleSourceException ex)
        {
            return await RecordFailureAsync(module.Id, ex.Message, ct);
        }
        catch (GitHubApiException ex)
        {
            return await RecordFailureAsync(module.Id, $"GitHub did not answer: {ex.Message}", ct);
        }
        catch (Exception ex) when (IsUnreachable(ex, ct))
        {
            _logger.LogWarning(ex, "Could not refresh module '{Key}' from {Repository}: GitHub could not be reached.",
                module.Key, repository);
            return await RecordFailureAsync(module.Id, "GitHub could not be reached. The copy will be tried again later.", ct);
        }
        catch (PlanValidationException ex)
        {
            return await RecordFailureAsync(module.Id,
                $"The new files could not be saved: {string.Join(" ", ex.Errors.Values.Distinct())}", ct);
        }
    }

    /// <summary>
    /// Refreshes every module in the current organisation that copies
    /// <paramref name="repositoryFullName"/> at <paramref name="branch"/> - by name,
    /// or by leaving the branch blank when <paramref name="branch"/> is the
    /// repository's default. Called for each push the GitHub App is told about.
    /// Returns how many modules took a new copy.
    /// </summary>
    public async Task<int> RefreshForPushAsync(
        string repositoryFullName, string branch, string defaultBranch, CancellationToken ct = default)
    {
        var candidates = await _db.Modules.AsNoTracking()
            .Where(m => m.DeletedAt == null && m.SourceRepository != null)
            .Select(m => new { m.Id, m.SourceRepository, m.SourceRef })
            .ToListAsync(ct);
        var due = candidates
            .Where(m => string.Equals(m.SourceRepository, repositoryFullName, StringComparison.OrdinalIgnoreCase))
            .Where(m => m.SourceRef is null
                ? string.Equals(branch, defaultBranch, StringComparison.Ordinal)
                : string.Equals(m.SourceRef, branch, StringComparison.Ordinal))
            .Select(m => m.Id)
            .ToList();

        var updated = 0;
        foreach (var id in due)
        {
            if (await RefreshIsolatedAsync(id, ct) == ModuleSourceRefreshOutcome.Updated) updated++;
        }
        return updated;
    }

    /// <summary>
    /// The daily pass over the current organisation: refreshes every repository
    /// module except those pinned to a full commit SHA, which cannot change. A tag
    /// costs one small call to confirm it still names the same commit. Returns how
    /// many modules took a new copy.
    /// </summary>
    public async Task<int> RefreshAllAsync(CancellationToken ct = default)
    {
        var modules = await _db.Modules.AsNoTracking()
            .Where(m => m.DeletedAt == null && m.SourceRepository != null)
            .OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.SourceRef })
            .ToListAsync(ct);

        var updated = 0;
        foreach (var module in modules.Where(m => !GitHubAppClient.IsCommitSha(m.SourceRef)))
        {
            if (await RefreshIsolatedAsync(module.Id, ct) == ModuleSourceRefreshOutcome.Updated) updated++;
        }
        return updated;
    }

    /// <summary>
    /// <see cref="RefreshAsync"/> for one module of a pass over many: an unexpected
    /// failure (a database error, say) is logged and the pass moves on, rather than
    /// leaving the rest of the organisation's modules unrefreshed.
    /// </summary>
    private async Task<ModuleSourceRefreshOutcome> RefreshIsolatedAsync(int moduleId, CancellationToken ct)
    {
        try
        {
            return await RefreshAsync(moduleId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _db.ChangeTracker.Clear();
            _logger.LogError(ex, "Could not refresh module {ModuleId} from its repository.", moduleId);
            return ModuleSourceRefreshOutcome.Failed;
        }
    }

    // ===== Copying for a save =====

    private async Task<(ModuleSourceState State, ModuleSourceSnapshot Snapshot)> CopyForSaveAsync(
        ModuleSourceInput source, CancellationToken ct)
    {
        if (!ModuleSourceAddress.TryParse(source.Address, out var fullName, out var refFromLink, out var pathFromLink, out var addressError))
        {
            throw Refuse("Source.Address", addressError);
        }
        if (!ModuleSourceAddress.TryNormaliseRef(string.IsNullOrWhiteSpace(source.Ref) ? refFromLink : source.Ref,
                out var gitRef, out var refError))
        {
            throw Refuse("Source.Ref", refError);
        }
        if (!ModuleSourceAddress.TryNormalisePath(string.IsNullOrWhiteSpace(source.Path) ? pathFromLink : source.Path,
                out var path, out var pathError))
        {
            throw Refuse("Source.Path", pathError);
        }

        var (owner, repo) = Split(fullName);
        var status = await _connection.GetStatusAsync(ct);
        var inConnectedOrg = status.OrgLogin is { } login && string.Equals(owner, login, StringComparison.OrdinalIgnoreCase);

        try
        {
            var token = await InstallationTokenAsync(ct);
            var summary = await _github.GetRepositorySummaryAsync(token, owner, repo, ct);
            if (summary is null || (summary.IsPrivate && !inConnectedOrg))
            {
                throw Refuse("Source.Address", inConnectedOrg
                    ? "AL Workbench can't see this repository. Check the address, and that the GitHub App has been given access to it."
                    : status.OrgLogin is { } connected
                        ? $"No public repository with that address was found on GitHub. A private repository can only be used when it is in {connected}, the GitHub organisation connected under Administration."
                        : "No public repository with that address was found on GitHub. To use a private repository, connect your GitHub organisation under Administration first.");
            }
            if (!string.IsNullOrEmpty(summary.FullName)) fullName = summary.FullName;

            if (summary.IsPrivate)
            {
                var userId = _orgContext.CurrentUserId
                    ?? throw new InvalidOperationException("No user in scope; a module source can only be saved by a signed-in person.");
                var canSee = await _access.TryCanAccessRepoAsync(userId, fullName, ct);
                if (canSee is null)
                {
                    throw Refuse("Source.Address", "GitHub didn't say whether you can see this repository. Try again in a moment.");
                }
                if (canSee is false)
                {
                    throw Refuse("Source.Address",
                        "Your GitHub account can't see this repository. Link your GitHub account under Account, or ask for access to the repository on GitHub.");
                }
            }

            (owner, repo) = Split(fullName);
            var commit = await _github.ResolveCommitAsync(token, owner, repo, gitRef, ct)
                ?? throw Refuse("Source.Ref", $"There is no branch, tag or commit called '{gitRef}' in {fullName}.");

            var snapshot = await DownloadAsync(token, owner, repo, commit, path, ct);
            var now = _clock.GetUtcNow().UtcDateTime;
            return (new ModuleSourceState(fullName, gitRef, path, commit, now, snapshot.Skipped, snapshot.Dependencies), snapshot);
        }
        catch (ModuleSourceException ex)
        {
            throw Refuse(ex.IsAboutFolder ? "Source.Path" : "Source.Address", ex.Message);
        }
        catch (GitHubApiException ex)
        {
            _logger.LogWarning(ex, "Could not copy a module from {Repository}: GitHub did not answer.", fullName);
            throw Refuse("Source.Address", "GitHub didn't answer, so the files could not be copied. Try again in a moment.");
        }
        catch (Exception ex) when (IsUnreachable(ex, ct))
        {
            _logger.LogWarning(ex, "Could not copy a module from {Repository}: GitHub could not be reached.", fullName);
            throw Refuse("Source.Address", "GitHub could not be reached, so the files could not be copied. Try again in a moment.");
        }
    }

    // ===== GitHub =====

    /// <summary>
    /// A network failure or a timeout talking to GitHub, as opposed to the caller
    /// cancelling: the first is reported to the Editor, the second propagates.
    /// </summary>
    private static bool IsUnreachable(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or IOException
        || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private async Task<ModuleSourceSnapshot> DownloadAsync(
        string? token, string owner, string repo, string commit, string? path, CancellationToken ct)
    {
        try
        {
            return await _github.ReadTarballAsync(
                token, owner, repo, commit, ModuleSourceLimits.MaxDownloadBytes,
                (stream, readCt) => ModuleSourceArchiveReader.ReadAsync(stream, path, readCt), ct);
        }
        catch (ArchiveTooLargeException)
        {
            throw new ModuleSourceException(
                $"The repository is too large to copy (more than {ModuleSourceLimits.MaxDownloadBytes / (1024 * 1024)} MB to download).");
        }
    }

    /// <summary>
    /// This organisation's installation token, or null when it has no connection
    /// (or the deployment has no App), in which case public repositories are read
    /// anonymously. A connected installation GitHub refuses a token for is a
    /// <see cref="GitHubApiException"/>, not a silent fall-back: reading anonymously
    /// would turn "the installation is broken" into a misleading "not found".
    /// </summary>
    private async Task<string?> InstallationTokenAsync(CancellationToken ct)
    {
        var status = await _connection.GetStatusAsync(ct);
        if (!status.DeploymentConfigured || status.InstallationId is not long installationId) return null;
        try
        {
            return await _github.GetInstallationTokenAsync(installationId, ct);
        }
        catch (GitHubAppNotConfiguredException)
        {
            return null;
        }
    }

    private async Task<ModuleSourceRefreshOutcome> RecordFailureAsync(int moduleId, string reason, CancellationToken ct)
    {
        // A failed UpdateAsync may have left tracked changes behind; none of them
        // may reach the database with the error.
        _db.ChangeTracker.Clear();
        var now = _clock.GetUtcNow().UtcDateTime;
        await _db.Modules.Where(m => m.Id == moduleId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.SourceCheckedAt, now)
                .SetProperty(m => m.SourceError, reason), ct);
        _logger.LogWarning("Could not refresh module {ModuleId} from its repository: {Reason}", moduleId, reason);
        return ModuleSourceRefreshOutcome.Failed;
    }

    private static bool IsInConnectedOrganisation(string fullName, GitHubConnectionStatus status) =>
        status.OrgLogin is { } login
        && fullName.StartsWith(login + "/", StringComparison.OrdinalIgnoreCase);

    private static (string Owner, string Repo) Split(string fullName)
    {
        var slash = fullName.IndexOf('/');
        return (fullName[..slash], fullName[(slash + 1)..]);
    }

    private static string ShortSha(string? sha) => sha is { Length: >= 7 } ? sha[..7] : sha ?? "none";

    private static PlanValidationException Refuse(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}
