using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Organizations;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.GitHub;

/// <summary>A file the organisation expects in a repository, and what it should hold.</summary>
/// <param name="IsWorkspaceSettings">
/// True for the <c>.code-workspace</c> file, whose <c>settings</c> are compared and merged
/// rather than the whole file: its folder list belongs to the repository.
/// </param>
internal sealed record ExpectedRepositoryFile(string Path, string Content, bool IsWorkspaceSettings = false);

/// <summary>What creating or updating a workspace files pull request did.</summary>
/// <param name="Refusal">Why nothing was opened, in words for the person who asked. Null when one was.</param>
/// <param name="IsUpToDate">True when there was nothing to change.</param>
public sealed record WorkspaceFilesPullRequest(
    GitHubPullRequest? PullRequest, bool IsNewPullRequest, int FileCount, string? Refusal, bool IsUpToDate = false);

/// <summary>
/// Keeps the files the workbench manages in step across an organisation's repositories
/// (issue #1719, part 2): the repository standards, the always-included files a
/// repository's template opts into that are stored as-is, and the settings in its
/// <c>.code-workspace</c>. A check reads them from GitHub with the installation and
/// records what is missing or different; an update pull request writes what the
/// organisation has now, on <see cref="Branch"/>, with the person's own account.
///
/// <para>Files with placeholders (the README stub, each extension's app.json) are
/// starting points the team is expected to edit, so they are never compared. What the
/// generator would write is read out of the archive it builds, so this cannot come to
/// disagree with a new workspace. See <c>.design/github-integration-phase2.md</c>,
/// "Workspace files".</para>
/// </summary>
public sealed class RepositoryFilesService
{
    /// <summary>The branch the update pull request comes from. Reused while its pull request is open.</summary>
    public const string Branch = "aldt/workspace-files";

    private const string PullRequestTitle = "Update workspace files";

    private static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    private readonly AppDbContext _db;
    private readonly GitHubAppClient _github;
    private readonly GitHubConnectionService _connection;
    private readonly DependencyDriftService _drift;
    private readonly WorkspaceConfigService _configs;
    private readonly GenerationService _generation;
    private readonly IOrganizationContext _orgContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<RepositoryFilesService> _logger;

    public RepositoryFilesService(
        AppDbContext db,
        GitHubAppClient github,
        GitHubConnectionService connection,
        DependencyDriftService drift,
        WorkspaceConfigService configs,
        GenerationService generation,
        IOrganizationContext orgContext,
        TimeProvider clock,
        ILogger<RepositoryFilesService> logger)
    {
        _db = db;
        _github = github;
        _connection = connection;
        _drift = drift;
        _configs = configs;
        _generation = generation;
        _orgContext = orgContext;
        _clock = clock;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; the workspace files check ran outside an authenticated request.");

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; a workspace files pull request was asked for outside an authenticated request.");

    /// <summary>
    /// Reads one repository's workspace files again and replaces what is stored about
    /// it. Only a repository on a solution this person may see.
    /// </summary>
    public async Task<RepositoryCheckOutcome> CheckRepositoryAsync(string repository, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var fullName = DependencyDriftService.ToFullName($"https://github.com/{repository}") ?? repository;
        if (!(await _drift.VisibleRepositoryNamesAsync(ct)).Contains(fullName)) return RepositoryCheckOutcome.NotTracked;

        // One reader per organisation at a time, shared with the drift scan, so two
        // people checking at once wait for each other rather than collide on the row.
        using var gate = await DependencyDriftService.ScanGate.EnterAsync(orgId, ct);
        var status = await _connection.GetStatusAsync(ct);
        if (status.InstallationId is not { } installationId || string.IsNullOrWhiteSpace(status.OrgLogin))
        {
            return RepositoryCheckOutcome.NotConnected;
        }
        var tracked = await _drift.TrackedRepositoryNamesAsync(status.OrgLogin!, ct);
        if (!tracked.Contains(fullName, StringComparer.OrdinalIgnoreCase)) return RepositoryCheckOutcome.NotTracked;

        var token = await _github.GetInstallationTokenAsync(installationId, ct);
        var parts = fullName.Split('/');
        if (await _github.GetRepositoryAsync(token, parts[0], parts[1], ct) is not { } repo)
        {
            return RepositoryCheckOutcome.NotReadable;
        }

        var check = await ReadAsync(token, repo, ct);
        await ReplaceAsync([check], fullName, ct);
        return RepositoryCheckOutcome.Checked;
    }

    /// <summary>
    /// Reads every tracked repository the installation can see and replaces what is
    /// stored for the whole organisation. A repository GitHub cannot answer about keeps
    /// nothing, so it reads as not checked rather than as up to date. Returns how many
    /// were read.
    /// </summary>
    public async Task<int> CheckAllAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        using var gate = await DependencyDriftService.ScanGate.EnterAsync(orgId, ct);
        var status = await _connection.GetStatusAsync(ct);
        if (status.InstallationId is not { } installationId || string.IsNullOrWhiteSpace(status.OrgLogin)) return 0;

        var tracked = await _drift.TrackedRepositoryNamesAsync(status.OrgLogin!, ct);
        var token = await _github.GetInstallationTokenAsync(installationId, ct);
        var listing = await _github.ListInstallationRepositoriesAsync(token, ct);
        var installed = listing.Repositories.ToDictionary(r => r.FullName, StringComparer.OrdinalIgnoreCase);

        var checks = new List<GitHubRepositoryFileCheck>();
        foreach (var fullName in tracked)
        {
            ct.ThrowIfCancellationRequested();
            if (!installed.TryGetValue(fullName, out var repo)) continue;
            try
            {
                checks.Add(await ReadAsync(token, repo, ct));
            }
            catch (Exception ex) when (ex is GitHubApiException or HttpRequestException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                _logger.LogWarning(ex,
                    "Could not read the workspace files of {RepoFullName}; it is left out of this check.", repo.FullName);
            }
        }

        await ReplaceAsync(checks, repository: null, ct);
        _logger.LogInformation(
            "Checked the workspace files of {CheckedCount} of {TrackedCount} repositories for organisation {OrgId}: {BehindCount} have files to update.",
            checks.Count, tracked.Count, orgId, checks.Count(c => c.MissingPaths.Count + c.ChangedPaths.Count > 0));
        return checks.Count;
    }

    /// <summary>
    /// Opens a pull request that brings <paramref name="repository"/>'s workspace files
    /// in step, or adds to the one already open, with the person's own GitHub account.
    /// Refusals come back as an answer rather than an exception, as the row shows them.
    /// </summary>
    public async Task<WorkspaceFilesPullRequest> OpenPullRequestAsync(string repository, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var userId = RequireUserId();
        var fullName = DependencyDriftService.ToFullName($"https://github.com/{repository}") ?? repository;
        if (!(await _drift.VisibleRepositoryNamesAsync(ct)).Contains(fullName))
        {
            return new WorkspaceFilesPullRequest(null, false, 0, "This repository is not on a solution you can see.");
        }

        var (token, readiness) = await _drift.ResolveWriteTokenAsync(userId, ct);
        if (token is null)
        {
            return new WorkspaceFilesPullRequest(null, false, 0, DependencyDriftService.ReadinessRefusal(readiness));
        }
        // The same repositories a check reads: confirmed, and in the connected GitHub
        // organisation. Anything else is not the organisation's to write its standards into.
        var status = await _connection.GetStatusAsync(ct);
        var tracked = string.IsNullOrWhiteSpace(status.OrgLogin)
            ? []
            : await _drift.TrackedRepositoryNamesAsync(status.OrgLogin!, ct);
        if (!tracked.Contains(fullName, StringComparer.OrdinalIgnoreCase))
        {
            return new WorkspaceFilesPullRequest(null, false, 0,
                "Nobody has confirmed AL Workbench may use this repository, or it is outside your organisation's GitHub. Open the solution's Repositories tab to confirm it.");
        }

        try
        {
            var parts = fullName.Split('/');
            if (await _github.GetRepositoryAsync(token, parts[0], parts[1], ct) is not { } repo)
            {
                return new WorkspaceFilesPullRequest(null, false, 0,
                    "Your GitHub account cannot open this repository. Ask its owner to give you write access.");
            }
            var result = await OpenAsync(token, repo, ct);

            // What the row shows comes from the check, so it is read again from where the
            // pull request now leaves it.
            if (result.PullRequest is not null)
            {
                await RecordPullRequestAsync(fullName, result.PullRequest, ct);
            }
            _logger.LogInformation(
                "User {UserId} asked for the workspace files of {RepoFullName} to be updated: {FileCount} files, pull request {PullRequestNumber}.",
                userId, fullName, result.FileCount, result.PullRequest?.Number);
            return result;
        }
        catch (GitHubApiException ex) when (ex.IsRateLimited)
        {
            _logger.LogWarning(ex, "GitHub rate limited the workspace files pull request on {RepoFullName}.", fullName);
            return new WorkspaceFilesPullRequest(null, false, 0,
                "GitHub is limiting how fast your account can make changes just now. Wait a few minutes, then try again.");
        }
        catch (PlanValidationException ex)
        {
            return new WorkspaceFilesPullRequest(null, false, 0,
                ex.Errors.Values.FirstOrDefault() ?? "The pull request was refused.");
        }
    }

    // ===== Reading =====

    private async Task<GitHubRepositoryFileCheck> ReadAsync(string token, GitHubRepositorySummary repo, CancellationToken ct)
    {
        var expected = await ExpectedFilesAsync(token, repo, ct);
        var missing = new List<string>();
        var changed = new List<string>();
        foreach (var file in expected)
        {
            ct.ThrowIfCancellationRequested();
            var actual = await _github.GetFileAsync(token, repo.Owner, repo.Name, file.Path, repo.DefaultBranch, ct);
            if (actual is null)
            {
                missing.Add(file.Path);
            }
            else if (Updated(file, actual.Text) is not null)
            {
                changed.Add(file.Path);
            }
        }

        var open = await FindOpenAsync(token, repo, ct);
        return new GitHubRepositoryFileCheck
        {
            OrganizationId = RequireOrganizationId(),
            Repository = repo.FullName.ToLowerInvariant(),
            MissingPaths = missing,
            ChangedPaths = changed,
            PullRequestNumber = open?.Number,
            PullRequestUrl = open?.HtmlUrl,
            CheckedAt = _clock.GetUtcNow().UtcDateTime,
        };
    }

    /// <summary>
    /// What <paramref name="current"/> should become to match <paramref name="file"/>,
    /// or null when it already does. Line endings and trailing blank lines are not a
    /// difference: Git and editors change both without anybody meaning anything by it.
    /// </summary>
    internal static string? Updated(ExpectedRepositoryFile file, string current)
    {
        if (!file.IsWorkspaceSettings)
        {
            return Normalise(current) == Normalise(file.Content) ? null : file.Content;
        }

        JsonObject? actual;
        try
        {
            actual = JsonNode.Parse(current, documentOptions: JsoncOptions) as JsonObject;
        }
        catch (JsonException)
        {
            // A settings file somebody broke by hand is theirs to fix; overwriting it
            // would take whatever else they had in it with it.
            return null;
        }
        if (actual is null) return null;
        if (JsonNode.Parse(file.Content)?["settings"] is not JsonObject wanted) return null;

        var settings = actual["settings"] as JsonObject;
        if (settings is not null && Contains(settings, wanted)) return null;

        if (settings is null)
        {
            settings = new JsonObject();
            actual["settings"] = settings;
        }
        Merge(settings, wanted);
        return actual.ToJsonString(IndentedOptions) + "\n";
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n").TrimEnd();

    /// <summary>Whether every value in <paramref name="wanted"/> is in <paramref name="actual"/>; extra keys are the team's own.</summary>
    private static bool Contains(JsonObject actual, JsonObject wanted)
    {
        foreach (var (key, value) in wanted)
        {
            if (!actual.TryGetPropertyValue(key, out var have)) return false;
            if (value is JsonObject wantedChild)
            {
                if (have is not JsonObject haveChild || !Contains(haveChild, wantedChild)) return false;
            }
            else if (!JsonNode.DeepEquals(have, value))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Writes every value in <paramref name="wanted"/> into <paramref name="target"/>, keeping keys it does not name.</summary>
    private static void Merge(JsonObject target, JsonObject wanted)
    {
        foreach (var (key, value) in wanted)
        {
            if (value is JsonObject wantedChild && target[key] is JsonObject targetChild)
            {
                Merge(targetChild, wantedChild);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>
    /// The files the organisation expects in a repository: its standards, and - when the
    /// repository carries the workbench's saved settings - the always-included files its
    /// template opts into that have no placeholders, plus its workspace settings. A
    /// standard at the same path as an always-included file wins, as it does when the
    /// workbench creates a repository.
    /// </summary>
    internal async Task<List<ExpectedRepositoryFile>> ExpectedFilesAsync(
        string token, GitHubRepositorySummary repo, CancellationToken ct)
    {
        var byPath = new Dictionary<string, ExpectedRepositoryFile>(StringComparer.Ordinal);
        foreach (var file in await WorkspaceFilesAsync(token, repo, ct)) byPath[file.Path] = file;

        var standards = await _db.GitHubRepositoryStandardFiles.AsNoTracking()
            .OrderBy(f => f.Ordering).ThenBy(f => f.Id)
            .Select(f => new { f.Path, f.Content })
            .ToListAsync(ct);
        foreach (var standard in standards) byPath[standard.Path] = new ExpectedRepositoryFile(standard.Path, standard.Content);

        return byPath.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    private async Task<List<ExpectedRepositoryFile>> WorkspaceFilesAsync(
        string token, GitHubRepositorySummary repo, CancellationToken ct)
    {
        var saved = await _github.GetFileAsync(
            token, repo.Owner, repo.Name, WorkspaceConfigService.FileName, repo.DefaultBranch, ct);
        if (saved is null) return [];

        WorkspaceConfigImport import;
        try
        {
            import = await _configs.ParseAsync(saved.Text, ct);
        }
        catch (PlanValidationException ex)
        {
            _logger.LogInformation(
                "The saved settings in {RepoFullName} could not be read, so only its standards are compared: {Errors}",
                repo.FullName, string.Join("; ", ex.Errors.Select(e => $"{e.Key}: {e.Value}")));
            return [];
        }
        if (import.Workspace is not { } plan) return [];

        var template = await _db.RuntimeTemplates.AsNoTracking()
            .Where(t => t.Key == plan.TemplateKey && t.DeletedAt == null)
            .Select(t => new { t.Id })
            .FirstOrDefaultAsync(ct);
        if (template is null) return [];

        var included = await _db.RuntimeTemplateIncludedFiles.AsNoTracking()
            .Where(j => j.RuntimeTemplateId == template.Id)
            .Select(j => j.OrganizationFile!)
            .Where(f => !f.MustacheEnabled && f.Scope == OrganizationFileScope.WorkspaceRoot)
            .Select(f => f.Path)
            .ToListAsync(ct);

        // Only what is the same for every workspace off this template: no optional
        // extensions, no catalogue modules, no examples. The files compared do not
        // depend on them, and the archive is built in memory.
        var bare = plan with { SelectedExtensionPaths = [], SelectedModuleKeys = [], IncludeExamples = false };
        Dictionary<string, string> generated;
        try
        {
            generated = await GeneratedFilesAsync(bare, ct);
        }
        catch (PlanValidationException ex)
        {
            _logger.LogInformation(
                "The workspace in {RepoFullName} could not be generated again, so only its standards are compared: {Errors}",
                repo.FullName, string.Join("; ", ex.Errors.Select(e => $"{e.Key}: {e.Value}")));
            return [];
        }

        var files = included
            .Where(path => !string.Equals(path, PlatformOrganizationFiles.AppJsonPath, StringComparison.OrdinalIgnoreCase))
            .Where(generated.ContainsKey)
            .Select(path => new ExpectedRepositoryFile(path, generated[path]))
            .ToList();

        var codeWorkspace = generated.Keys.FirstOrDefault(p => !p.Contains('/') && p.EndsWith(".code-workspace", StringComparison.Ordinal));
        if (codeWorkspace is not null)
        {
            // The name it was written under when the repository was made, which the
            // organisation's folder style may no longer produce (#1615).
            // Only a workspace file at the root: the saved settings are the repository's
            // own text, and must not point the merge at some other JSON file.
            var savedName = import.CodeWorkspaceFileName;
            var path = !string.IsNullOrWhiteSpace(savedName) && !savedName.Contains('/') && !savedName.Contains('\\')
                && savedName.EndsWith(".code-workspace", StringComparison.Ordinal)
                ? savedName
                : codeWorkspace;
            var current = await _github.GetFileAsync(token, repo.Owner, repo.Name, path, repo.DefaultBranch, ct);
            // Only compared when the repository has one: a workspace saved under a name
            // nobody can work out is not one to plant a second copy beside.
            if (current is not null) files.Add(new ExpectedRepositoryFile(path, generated[codeWorkspace], IsWorkspaceSettings: true));
        }
        return files;
    }

    /// <summary>The generated workspace's files by repository-relative path.</summary>
    private async Task<Dictionary<string, string>> GeneratedFilesAsync(ProjectPlan plan, CancellationToken ct)
    {
        var archive = await _generation.GenerateWorkspaceAsync(plan, ct);
        await using var stream = archive.Stream;
        stream.Position = 0;
        var root = Path.GetFileNameWithoutExtension(archive.FileName) + "/";
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var path = entry.FullName.StartsWith(root, StringComparison.Ordinal) ? entry.FullName[root.Length..] : entry.FullName;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            files[path] = await reader.ReadToEndAsync(ct);
        }
        return files;
    }

    // ===== Writing =====

    private async Task<WorkspaceFilesPullRequest> OpenAsync(string token, GitHubRepositorySummary repo, CancellationToken ct)
    {
        var (branch, open) = await ChooseBranchAsync(token, repo, ct);
        var parent = open is not null
            ? await _github.GetBranchHeadShaAsync(token, repo.Owner, repo.Name, branch, ct)
                ?? throw Refuse($"Pull request #{open.Number} says it comes from {branch}, but that branch is gone. Close the pull request on GitHub, then try again.")
            : await _github.GetBranchHeadShaAsync(token, repo.Owner, repo.Name, repo.DefaultBranch, ct)
                ?? throw Refuse($"'{repo.FullName}' has no commits on {repo.DefaultBranch} yet, so there is nothing to update.");

        var blobs = new List<(string Path, string BlobSha)>();
        var written = new List<string>();
        foreach (var file in await ExpectedFilesAsync(token, repo, ct))
        {
            ct.ThrowIfCancellationRequested();
            var current = await _github.GetFileAsync(token, repo.Owner, repo.Name, file.Path, parent, ct);
            var content = current is null
                ? file.IsWorkspaceSettings ? null : file.Content
                : Updated(file, current.Text);
            if (content is null) continue;
            blobs.Add((file.Path, await _github.CreateBlobAsync(token, repo.Owner, repo.Name, Encoding.UTF8.GetBytes(content), ct)));
            written.Add(file.Path);
        }

        if (blobs.Count == 0)
        {
            return new WorkspaceFilesPullRequest(open, false, 0,
                open is null ? "Every workspace file here already matches. Check again to refresh what the list shows." : null,
                IsUpToDate: true);
        }

        var baseTree = await _github.GetCommitTreeShaAsync(token, repo.Owner, repo.Name, parent, ct);
        var tree = await _github.CreateTreeAsync(token, repo.Owner, repo.Name, baseTree, blobs, ct);
        var commit = await _github.CreateCommitAsync(token, repo.Owner, repo.Name, PullRequestTitle, tree, parent, ct: ct);

        if (open is null)
        {
            if (!await _github.CreateBranchAsync(token, repo.Owner, repo.Name, branch, commit, ct))
            {
                throw Refuse($"Branch {branch} appeared on '{repo.FullName}' while this was being prepared. Try again and the workbench will pick the next name.");
            }
            var created = await _github.CreatePullRequestAsync(
                token, repo.Owner, repo.Name,
                title: PullRequestTitle,
                head: branch,
                baseBranch: repo.DefaultBranch,
                body: Body(written),
                ct);
            return new WorkspaceFilesPullRequest(created, true, written.Count, null);
        }

        if (!await _github.UpdateBranchAsync(token, repo.Owner, repo.Name, branch, commit, ct))
        {
            throw Refuse($"Branch {branch} on '{repo.FullName}' moved on while this was being prepared, so nothing was committed. Try again.");
        }
        return new WorkspaceFilesPullRequest(open, false, written.Count, null);
    }

    private const int MaxBranchAttempts = 5;

    /// <summary>
    /// The branch the update belongs on: <see cref="Branch"/>, then <c>-2</c>, <c>-3</c>...
    /// until one has a pull request open (join it) or does not exist (start it). A branch
    /// with no open pull request is somebody's - a closed review kept for more work, say -
    /// so it is stepped past, never reset.
    /// </summary>
    private async Task<(string Branch, GitHubPullRequest? Open)> ChooseBranchAsync(
        string token, GitHubRepositorySummary repo, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxBranchAttempts; attempt++)
        {
            var branch = attempt == 1 ? Branch : $"{Branch}-{attempt}";
            if (await _github.FindOpenPullRequestAsync(token, repo.Owner, repo.Name, branch, ct) is { } open)
            {
                return (branch, open);
            }
            if (await _github.GetBranchHeadShaAsync(token, repo.Owner, repo.Name, branch, ct) is null)
            {
                return (branch, null);
            }
        }
        throw Refuse(
            $"'{repo.FullName}' already has branches named {Branch} through {Branch}-{MaxBranchAttempts}, and none of them has a pull request open. Tidy those up on GitHub, then try again.");
    }

    /// <summary>The open workspace files pull request, on whichever branch name it ended up.</summary>
    private async Task<GitHubPullRequest?> FindOpenAsync(string token, GitHubRepositorySummary repo, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxBranchAttempts; attempt++)
        {
            var branch = attempt == 1 ? Branch : $"{Branch}-{attempt}";
            if (await _github.FindOpenPullRequestAsync(token, repo.Owner, repo.Name, branch, ct) is { } open) return open;
            if (await _github.GetBranchHeadShaAsync(token, repo.Owner, repo.Name, branch, ct) is null) return null;
        }
        return null;
    }

    private static string Body(IReadOnlyList<string> paths)
    {
        var body = new StringBuilder();
        body.AppendLine("Brings the files AL Workbench manages in line with what your organisation has set up now:");
        body.AppendLine();
        foreach (var path in paths) body.AppendLine($"- `{path}`");
        body.AppendLine();
        body.AppendLine("Workspace settings are merged: settings this repository added itself are kept, and comments in the file are not.");
        body.AppendLine();
        body.Append("Opened by AL Workbench.");
        return body.ToString();
    }

    private async Task RecordPullRequestAsync(string fullName, GitHubPullRequest pullRequest, CancellationToken ct)
    {
        var key = fullName.ToLowerInvariant();
        var row = await _db.GitHubRepositoryFileChecks.FirstOrDefaultAsync(c => c.Repository == key, ct);
        if (row is null) return;
        row.PullRequestNumber = pullRequest.Number;
        row.PullRequestUrl = pullRequest.HtmlUrl;
        await _db.SaveChangesAsync(ct);
    }

    private async Task ReplaceAsync(IReadOnlyList<GitHubRepositoryFileCheck> checks, string? repository, CancellationToken ct)
    {
        var key = repository?.ToLowerInvariant();
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var existing = await _db.GitHubRepositoryFileChecks
            .Where(c => key == null || c.Repository == key)
            .ToListAsync(ct);
        _db.GitHubRepositoryFileChecks.RemoveRange(existing);
        await _db.SaveChangesAsync(ct);
        _db.GitHubRepositoryFileChecks.AddRange(checks
            .GroupBy(c => c.Repository, StringComparer.Ordinal)
            .Select(g => g.First()));
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static PlanValidationException Refuse(string message) =>
        new(new Dictionary<string, string> { ["GitHubRepository"] = message });
}
