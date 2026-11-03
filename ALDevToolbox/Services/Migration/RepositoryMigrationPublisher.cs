using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Migration;

/// <summary>What the Admin asked a migration to create.</summary>
/// <param name="ReplaceSolutionId">
/// The solution of the same name to delete first, when the Admin chose to replace
/// it. Only ever one that came from an earlier migration whose repository is gone.
/// </param>
/// <param name="PinnedCommits">
/// The commit of <c>main</c> the preview read in each repository, by name. A run
/// that finds <c>main</c> has moved since stops, so what is pushed is what was
/// previewed.
/// </param>
public sealed record MigrationCreateRequest(
    string RepositoryName,
    bool IsPrivate,
    bool AddPipelines,
    int? ReplaceSolutionId,
    IReadOnlyDictionary<string, string> PinnedCommits);

/// <summary>What a migration created, for the page's success state.</summary>
/// <param name="Warnings">What GitHub or the solution refused beside the success, in words the Admin can act on.</param>
public sealed record MigrationOutcome(
    string RepositoryFullName,
    string RepositoryUrl,
    int? SolutionId,
    string? SolutionName,
    int FileCount,
    IReadOnlyList<string> Branches,
    IReadOnlyList<string> PipelineNames,
    IReadOnlyList<string> Warnings);

/// <summary>The GitHub organisation a migration would create the repository in, or why it can't.</summary>
public sealed record MigrationGitHubReadiness(string? OrgLogin, string? Blocker);

/// <summary>A solution that already has the customer's name.</summary>
/// <param name="CanReplace">
/// True when it came from an earlier migration and that migration's repository is
/// gone from GitHub, which is what makes deleting it safe to offer (Mads, #1701).
/// </param>
public sealed record MigrationSolutionClash(int Id, string Name, bool CanReplace);

/// <summary>
/// The last step of a migration (issue #1701): creates the GitHub repository and
/// pushes the workspace <see cref="MigrationWorkspaceBuilder"/> built into it as
/// one commit, then lets <see cref="GitHubWorkspaceRepositoryService"/> do what
/// every new repository gets: branches, branch rules, the solution and its build
/// pipelines. Records each run in <c>repository_migrations</c>.
///
/// <para><strong>One git push, not the Contents API.</strong> A customer's source
/// is thousands of files; committing them one API call each would spend the
/// installation's hourly allowance that builds and webhooks share. The push rides
/// the installation token, like every other write to a repository the app has just
/// created.</para>
/// </summary>
public sealed class RepositoryMigrationPublisher
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(30);

    private readonly GitHubWorkspaceRepositoryService _repositories;
    private readonly GitHubConnectionService _connection;
    private readonly GitHubAppClient _github;
    private readonly ProjectService _projects;
    private readonly IProcessRunner _processes;
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _org;
    private readonly ILogger<RepositoryMigrationPublisher> _logger;

    public RepositoryMigrationPublisher(
        GitHubWorkspaceRepositoryService repositories,
        GitHubConnectionService connection,
        GitHubAppClient github,
        ProjectService projects,
        IProcessRunner processes,
        AppDbContext db,
        IOrganizationContext org,
        ILogger<RepositoryMigrationPublisher> logger)
    {
        _repositories = repositories;
        _connection = connection;
        _github = github;
        _projects = projects;
        _processes = processes;
        _db = db;
        _org = org;
        _logger = logger;
    }

    /// <summary>Where the push goes. Settable so a test can push to a local repository instead of GitHub.</summary>
    internal Func<GitHubRepositorySummary, string> PushUrl { get; set; } = repository => repository.CloneUrl;

    /// <summary>
    /// Everything that would refuse <see cref="PublishAsync"/> before anything is
    /// created: the repository name, GitHub, and a solution that already has the
    /// customer's name and may not be replaced.
    /// </summary>
    /// <exception cref="PlanValidationException">Keyed to the repository-name, repository or solution field.</exception>
    public async Task CheckAsync(string customerName, MigrationCreateRequest request, CancellationToken ct)
    {
        await _repositories.CheckCanCreateAsync(request.RepositoryName, solutionId: null, ct).ConfigureAwait(false);
        await CheckSolutionAsync(customerName, request.ReplaceSolutionId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Where a repository would go, and what stops one being created at all
    /// (GitHub not set up, the Admin not linked or not a member), asked when the
    /// page offers step 5 rather than after the Admin presses the button.
    /// </summary>
    public async Task<MigrationGitHubReadiness> CheckGitHubAsync(CancellationToken ct)
    {
        try
        {
            // Any legal name: only the refusals that do not depend on it can come back.
            var gate = await _repositories.CheckCanCreateAsync("migration-check", solutionId: null, ct).ConfigureAwait(false);
            return new MigrationGitHubReadiness(gate.OrgLogin, null);
        }
        catch (PlanValidationException ex)
        {
            var status = await _connection.GetStatusAsync(ct).ConfigureAwait(false);
            return new MigrationGitHubReadiness(status.OrgLogin, string.Join(" ", ex.Errors.Values));
        }
    }

    /// <summary>
    /// The solution that already has <paramref name="customerName"/>, if any, and
    /// whether it may be replaced. Null when the name is free.
    /// </summary>
    public async Task<MigrationSolutionClash?> FindSolutionClashAsync(string customerName, CancellationToken ct)
    {
        var name = (customerName ?? string.Empty).Trim();
        if (name.Length == 0) return null;
        var lower = name.ToLowerInvariant();
        var existing = await _db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Name.ToLower() == lower)
            .Select(p => new { p.Id, p.Name })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (existing is null) return null;

        var migrated = await _db.RepositoryMigrations.AsNoTracking()
            .Where(m => m.ProjectId == existing.Id && m.Succeeded && m.RepositoryFullName != null)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.RepositoryFullName)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return new MigrationSolutionClash(existing.Id, existing.Name,
            migrated is not null && await RepositoryIsGoneAsync(migrated, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Creates the repository, pushes the workspace in <paramref name="workspace"/>
    /// into it, registers the solution, and records the run.
    /// </summary>
    /// <param name="commits">The commit read in each source repository, by name, for the commit message and the history.</param>
    /// <exception cref="PlanValidationException">A refusal, before or after the repository was created; the message says which.</exception>
    public async Task<MigrationOutcome> PublishAsync(
        MigrationPreviewJob job, MigrationCreateRequest request, string workspace,
        IReadOnlyDictionary<string, string> commits, CancellationToken ct)
    {
        var plan = job.Target.Plan;
        var gate = await _repositories.CheckCanCreateAsync(request.RepositoryName, solutionId: null, ct).ConfigureAwait(false);
        var replace = await CheckSolutionAsync(job.Target.CustomerName, request.ReplaceSolutionId, ct).ConfigureAwait(false);
        if (replace is { } old)
        {
            await _projects.SoftDeleteProjectAsync(old.Id, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Migration {RunId} deleted solution {SolutionId} ({Name}), which came from an earlier migration whose repository is gone.",
                job.Id, old.Id, old.Name);
        }

        var sources = string.Join("\n", job.Repositories.Select(r => $"{r.Name}@{commits.GetValueOrDefault(r.Name) ?? "?"}"));
        GitHubWorkspaceRepository created;
        try
        {
            created = await _repositories.CreateAndFillAsync(
                gate, plan, request.IsPrivate, solutionId: null, request.AddPipelines,
                (token, repository, standards, author, fillCt) => PushAsync(job, workspace, commits, token, repository, standards, author, fillCt),
                ct).ConfigureAwait(false);
        }
        catch (PlanValidationException ex)
        {
            await RecordAsync(job, sources, null, null, null, false, string.Join(" ", ex.Errors.Values), ct).ConfigureAwait(false);
            throw;
        }

        var warnings = new[] { created.StandardsWarning, created.BranchesWarning, created.SolutionWarning, created.PipelinesWarning }
            .OfType<string>()
            .ToList();
        await RecordAsync(job, sources, created.Repository.FullName, created.Repository.HtmlUrl, created.SolutionId, true,
            warnings.Count == 0 ? null : string.Join(" ", warnings), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Migration {RunId} moved {RepositoryCount} repositories from {Project} into {RepoFullName} ({FileCount} files, solution {SolutionId}).",
            job.Id, job.Repositories.Count, job.Project.DisplayName, created.Repository.FullName, created.FileCount, created.SolutionId);
        return new MigrationOutcome(
            created.Repository.FullName, created.Repository.HtmlUrl, created.SolutionId, created.SolutionName,
            created.FileCount, created.Branches ?? [], created.PipelineNames ?? [], warnings);
    }

    /// <summary>
    /// The solution to delete before the new one is made, when the Admin chose to
    /// replace one; refuses a clash otherwise.
    /// </summary>
    private async Task<MigrationSolutionClash?> CheckSolutionAsync(string customerName, int? replaceSolutionId, CancellationToken ct)
    {
        var clash = await FindSolutionClashAsync(customerName, ct).ConfigureAwait(false);
        if (clash is null) return null;
        if (clash.CanReplace && replaceSolutionId == clash.Id) return clash;
        throw new PlanValidationException(new Dictionary<string, string>
        {
            [RepositoryMigrationService.SolutionField] = clash.CanReplace
                ? $"A solution called {clash.Name} already exists, from an earlier migration whose repository has since been deleted. "
                  + "Tick Replace the existing solution to delete it and its pipelines, or use another customer name."
                : $"A solution called {clash.Name} already exists. A migration creates a new solution, so use another customer name, "
                  + "or delete that solution first.",
        });
    }

    /// <summary>
    /// The fill: the organisation's standard files laid over the workspace, one
    /// commit naming every source repository and commit, and one push to the
    /// default branch on the installation token.
    /// </summary>
    private async Task<RepositoryFillResult> PushAsync(
        MigrationPreviewJob job, string workspace, IReadOnlyDictionary<string, string> commits,
        string token, GitHubRepositorySummary repository, IReadOnlyList<GitHubRepositoryStandardFile> standards,
        GitHubCommitAuthor? author, CancellationToken ct)
    {
        foreach (var standard in standards)
        {
            var path = Path.GetFullPath(Path.Combine(workspace, standard.Path));
            if (!path.StartsWith(Path.GetFullPath(workspace) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, standard.Content, ct).ConfigureAwait(false);
        }

        var git = RepositoryMigrationPreviewer.GitPath();
        var name = author?.Name ?? "AL Workbench";
        var email = author?.Email ?? "noreply@users.noreply.github.com";
        string[] identity = ["-c", $"user.name={name}", "-c", $"user.email={email}", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false"];

        await RunAsync(git, ["init", "--quiet", "--initial-branch", repository.DefaultBranch, workspace], workspace, null, token, repository, ct).ConfigureAwait(false);
        await RunAsync(git, [.. identity, "-C", workspace, "add", "--all"], workspace, null, token, repository, ct).ConfigureAwait(false);
        await RunAsync(git, [.. identity, "-C", workspace, "commit", "--quiet", "--no-verify", "-m", CommitMessage(job, commits)], workspace, null, token, repository, ct).ConfigureAwait(false);
        var listed = await RunAsync(git, ["-C", workspace, "ls-files", "-z"], workspace, null, token, repository, ct).ConfigureAwait(false);
        var fileCount = listed.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;
        await RunAsync(git, ["-C", workspace, "push", "--quiet", PushUrl(repository), $"HEAD:refs/heads/{repository.DefaultBranch}"],
            workspace, ProjectBuildService.GitAuthEnv(RepositoryProvider.GitHub, token), token, repository, ct).ConfigureAwait(false);
        var head = (await RunAsync(git, ["-C", workspace, "rev-parse", "HEAD"], workspace, null, token, repository, ct).ConfigureAwait(false)).Trim();

        _logger.LogInformation("Pushed {FileCount} files to {RepoFullName} at {Commit}.", fileCount, repository.FullName, head);
        return new RepositoryFillResult(fileCount, standards.Count, head);
    }

    private async Task<string> RunAsync(
        string git, string[] args, string workDir, IReadOnlyDictionary<string, string>? env, string token,
        GitHubRepositorySummary repository, CancellationToken ct)
    {
        var result = await _processes.RunAsync(new ProcessRunRequest(git, args, workDir, env, GitTimeout), ct).ConfigureAwait(false);
        if (result.Succeeded) return result.StdOut;

        var detail = ProjectBuildService.Sanitize((result.StdErr.Length > 0 ? result.StdErr : result.StdOut).Trim(), token);
        if (detail.Length > 500) detail = detail[..500] + "...";
        _logger.LogWarning("git {Step} for {RepoFullName} failed (exit {Exit}): {Detail}", args.FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('=') && a != workDir), repository.FullName, result.ExitCode, detail);
        throw new PlanValidationException(new Dictionary<string, string>
        {
            [GitHubWorkspaceRepositoryService.RepositoryField] =
                $"{repository.FullName} was created, but the files could not be pushed to it"
                + (detail.Length > 0 ? $" ({detail})" : string.Empty)
                + $". Delete {repository.FullName} on GitHub, then migrate again.",
        });
    }

    /// <summary>The commit names where everything came from, so the repository records it.</summary>
    private static string CommitMessage(MigrationPreviewJob job, IReadOnlyDictionary<string, string> commits)
    {
        var message = new StringBuilder();
        message.Append("Move ").Append(job.Target.CustomerName).AppendLine(" from Azure DevOps");
        message.AppendLine();
        message.Append("From ").Append(job.Project.WebUrl).AppendLine(", main of:");
        foreach (var repository in job.Repositories)
        {
            message.Append("- ").Append(repository.Name);
            if (commits.TryGetValue(repository.Name, out var commit)) message.Append(" at ").Append(commit);
            message.AppendLine();
        }
        message.AppendLine();
        message.Append("Laid out by the ").Append(job.Target.TemplateName).AppendLine(" template in AL Workbench.");
        return message.ToString();
    }

    private async Task<bool> RepositoryIsGoneAsync(string fullName, CancellationToken ct)
    {
        var parts = fullName.Split('/', 2);
        if (parts.Length != 2) return false;
        var connection = await _connection.GetStatusAsync(ct).ConfigureAwait(false);
        if (connection.InstallationId is not { } installationId) return false;
        try
        {
            var token = await _github.GetInstallationTokenAsync(installationId, ct).ConfigureAwait(false);
            // The app made this repository, so it can always see it while it exists.
            return await _github.GetRepositoryAsync(token, parts[0], parts[1], ct).ConfigureAwait(false) is null;
        }
        catch (GitHubApiException ex)
        {
            _logger.LogWarning(ex, "Could not ask GitHub whether {RepoFullName} still exists.", fullName);
            return false;
        }
    }

    private async Task RecordAsync(
        MigrationPreviewJob job, string sources, string? fullName, string? url, int? solutionId, bool succeeded,
        string? message, CancellationToken ct)
    {
        try
        {
            _db.ChangeTracker.Clear();
            _db.RepositoryMigrations.Add(new RepositoryMigration
            {
                OrganizationId = job.Identity.OrganizationId,
                CreatedByUserId = _org.CurrentUserId,
                CreatedAt = DateTime.UtcNow,
                SourceProject = job.Project.DisplayName,
                SourceProjectUrl = job.Project.WebUrl,
                Sources = sources,
                CustomerName = job.Target.CustomerName,
                TemplateKey = job.Target.TemplateKey,
                RepositoryFullName = fullName,
                RepositoryUrl = url,
                ProjectId = solutionId,
                Succeeded = succeeded,
                Message = message,
            });
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            // The repository exists either way; a lost history row is not worth failing the run over.
            _logger.LogWarning(ex, "Could not record migration {RunId} in the history.", job.Id);
        }
    }
}
