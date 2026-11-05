using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.Templates;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.GitHub;

/// <summary>The last build that included a repository.</summary>
/// <param name="Status">One of <see cref="ProjectBuildStatus"/>.</param>
/// <param name="At">When it finished, or started if it has not finished.</param>
/// <param name="Href">The build's page.</param>
public sealed record RepositoryListBuild(int Id, string Status, DateTime At, string Href);

/// <summary>An update pull request already open for a repository.</summary>
public sealed record RepositoryListPullRequest(int Number, string HtmlUrl);

/// <summary>What the last workspace files check found in a repository.</summary>
/// <param name="Missing">Files the organisation expects that the repository does not have.</param>
/// <param name="Changed">Files the repository has with different content.</param>
/// <param name="PullRequest">The workspace files pull request open when it was checked, if any.</param>
public sealed record RepositoryListFiles(
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Changed,
    RepositoryListPullRequest? PullRequest,
    DateTime CheckedAt)
{
    /// <summary>How many files an update pull request would write.</summary>
    public int Count => Missing.Count + Changed.Count;
}

/// <summary>One repository on the Repositories list (issue #1719).</summary>
/// <param name="Repository">
/// <c>owner/name</c> for a GitHub repository, else the display name it was added with.
/// </param>
/// <param name="FullName">The GitHub <c>owner/name</c>, or null for a repository elsewhere.</param>
/// <param name="Environment">What the customer runs: the solution's production environment, else its sandbox. Null when it has none.</param>
/// <param name="AppJsonApplication">
/// The oldest Business Central application its app.json files ask for, as
/// <c>major.minor</c>, from the last check. Null when it has not been checked.
/// </param>
/// <param name="Changes">What an update pull request would move. Empty when it is up to date.</param>
/// <param name="PullRequest">The update pull request already open for the version it would move to, if any.</param>
/// <param name="Files">What the last workspace files check found. Null when it has not been checked.</param>
public sealed record RepositoryListRow(
    int RepositoryId,
    string Repository,
    string HtmlUrl,
    string? FullName,
    int SolutionId,
    string SolutionName,
    string? SolutionShortName,
    string SolutionHref,
    RepositoryListBuild? LastBuild,
    YardstickEnvironment? Environment,
    string? AppJsonApplication,
    DateTime? CheckedAt,
    IReadOnlyList<DependencyDriftChange> Changes,
    RepositoryListPullRequest? PullRequest,
    RepositoryListFiles? Files = null)
{
    /// <summary>The customer's Business Central as <c>major.minor</c>, the way the Solutions list shows it.</summary>
    public string? BcVersion => Environment is null ? null : BcArtifactIndex.ToMajorMinor(Environment.Version);

    /// <summary>True when an update pull request would change something.</summary>
    public bool HasAppJsonUpdate => Changes.Count > 0;
}

/// <summary>
/// The rows behind the Repositories list (issue #1719): every repository on a solution
/// the viewer may see, with its last build, what the customer runs, and what its
/// app.json asks for. Reads the database only - what GitHub said comes from the last
/// drift scan - so a slow GitHub cannot hold the page up.
///
/// <para>See <c>.design/github-integration-phase2.md</c>, "The Repositories list".</para>
/// </summary>
public sealed class RepositoryListService
{
    private readonly AppDbContext _db;
    private readonly ProjectAccess _projectAccess;
    private readonly DependencyDriftService _drift;
    private readonly CatalogService _catalog;
    private readonly IOrganizationContext _orgContext;

    public RepositoryListService(
        AppDbContext db,
        ProjectAccess projectAccess,
        DependencyDriftService drift,
        CatalogService catalog,
        IOrganizationContext orgContext)
    {
        _db = db;
        _projectAccess = projectAccess;
        _drift = drift;
        _catalog = catalog;
        _orgContext = orgContext;
    }

    /// <summary>
    /// Every repository on a live solution the viewer may see, ordered by solution and
    /// then repository. A Private solution the viewer is not on contributes nothing:
    /// its repositories are a fact about its customer.
    /// </summary>
    public async Task<List<RepositoryListRow>> GetAsync(CancellationToken ct = default)
    {
        _ = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope; the repository list was read outside an authenticated request.");

        var snapshot = await _projectAccess.GetSnapshotAsync(ct);
        var repositories = await _db.OeProjectRepositories.AsNoTracking()
            .Where(r => _db.OeProjects
                .Where(ProjectAccess.VisibleProjectPredicate(snapshot))
                .Any(p => p.Id == r.ProjectId && p.DeletedAt == null))
            .Select(r => new
            {
                r.Id,
                r.ProjectId,
                r.Provider,
                r.Url,
                r.DisplayName,
                ProjectName = r.Project!.Name,
                r.Project.ShortName,
                r.Project.Slug,
            })
            .ToListAsync(ct);
        if (repositories.Count == 0) return [];

        var repositoryIds = repositories.Select(r => r.Id).ToList();
        var projectIds = repositories.Select(r => r.ProjectId).Distinct().ToList();

        // The newest build per repository, from the commits each build recorded. Pull
        // request and preview checks are left out: the column answers "does what is on
        // the branch build", not "did somebody's pull request".
        var builds = await _db.OeProjectBuildRepoCommits.AsNoTracking()
            .Where(c => c.ProjectRepositoryId != null && repositoryIds.Contains(c.ProjectRepositoryId.Value))
            .Where(c => c.ProjectBuild!.Trigger != ProjectBuildTrigger.PullRequest
                && c.ProjectBuild.Trigger != ProjectBuildTrigger.PreviewCheck)
            .GroupBy(c => c.ProjectRepositoryId!.Value)
            .Select(g => g.OrderByDescending(c => c.ProjectBuild!.StartedAt).ThenByDescending(c => c.ProjectBuildId)
                .Select(c => new
                {
                    RepositoryId = g.Key,
                    c.ProjectBuildId,
                    c.ProjectBuild!.Status,
                    c.ProjectBuild.StartedAt,
                    c.ProjectBuild.FinishedAt,
                    c.ProjectBuild.PipelineId,
                    c.ProjectBuild.ProjectId,
                })
                .First())
            .ToListAsync(ct);
        var buildByRepository = builds.ToDictionary(b => b.RepositoryId);

        var environments = await _drift.YardstickEnvironmentsAsync(projectIds, ct);

        var checks = (await _db.GitHubRepositoryChecks.AsNoTracking().ToListAsync(ct))
            .ToDictionary(c => c.Repository, StringComparer.OrdinalIgnoreCase);
        var findings = (await _db.GitHubRepositoryDrift.AsNoTracking()
                .OrderBy(d => d.Path).ThenBy(d => d.Field)
                .ToListAsync(ct))
            .GroupBy(d => d.Repository, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var pullRequests = (await _db.GitHubUpdatePullRequests.AsNoTracking()
                .Where(p => p.SupersededAt == null)
                .ToListAsync(ct))
            .GroupBy(p => (p.Repository, p.Version))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.OpenedAt).First());
        var fileChecks = (await _db.GitHubRepositoryFileChecks.AsNoTracking().ToListAsync(ct))
            .ToDictionary(c => c.Repository, StringComparer.OrdinalIgnoreCase);
        var dependencyNames = (await _catalog.GetAllAsync(ct))
            .GroupBy(w => AppJsonValueEditor.NormaliseId(w.DepId))
            .ToDictionary(g => g.Key, g => g.First().DepName);

        var rows = new List<RepositoryListRow>(repositories.Count);
        foreach (var r in repositories)
        {
            var fullName = r.Provider == RepositoryProvider.GitHub ? DependencyDriftService.ToFullName(r.Url) : null;
            var htmlUrl = fullName is not null
                ? $"https://github.com/{fullName}"
                : RepositoryDiscoveryService.CanonicalUrl(r.Url) is { Length: > 0 } canonical ? canonical : r.Url;

            RepositoryListBuild? lastBuild = null;
            if (buildByRepository.TryGetValue(r.Id, out var b))
            {
                lastBuild = new RepositoryListBuild(
                    b.ProjectBuildId, b.Status, b.FinishedAt ?? b.StartedAt,
                    b.PipelineId is { } pipelineId
                        ? $"/pipelines/{pipelineId}?build={b.ProjectBuildId}"
                        : SolutionLinks.Solution(r.Slug, r.ProjectId));
            }

            var check = fullName is not null && checks.TryGetValue(fullName, out var c) ? c : null;
            var changes = fullName is not null && findings.TryGetValue(fullName, out var found)
                ? found.Select(d => new DependencyDriftChange(
                        d.Field, DependencyDriftService.FieldLabel(d.Field, dependencyNames), d.Current, d.Proposed))
                    .ToList()
                : [];

            RepositoryListPullRequest? pullRequest = null;
            if (changes.Count > 0 && fullName is not null)
            {
                var proposed = changes
                    .Where(x => x.Field == DependencyDriftService.ApplicationField)
                    .Select(x => BcArtifactIndex.ToMajorMinor(x.Proposed))
                    .FirstOrDefault();
                if (proposed is not null
                    && pullRequests.TryGetValue((fullName.ToLowerInvariant(), proposed), out var open))
                {
                    pullRequest = new RepositoryListPullRequest(open.PullRequestNumber, open.HtmlUrl);
                }
            }

            rows.Add(new RepositoryListRow(
                r.Id,
                fullName ?? (string.IsNullOrWhiteSpace(r.DisplayName) ? htmlUrl : r.DisplayName),
                htmlUrl,
                fullName,
                r.ProjectId,
                r.ProjectName,
                string.IsNullOrWhiteSpace(r.ShortName) ? null : r.ShortName,
                SolutionLinks.Solution(r.Slug, r.ProjectId),
                lastBuild,
                environments.TryGetValue(r.ProjectId, out var environment) ? environment : null,
                check?.Application,
                check?.CheckedAt,
                changes,
                pullRequest,
                fullName is not null && fileChecks.TryGetValue(fullName, out var files)
                    ? new RepositoryListFiles(
                        files.MissingPaths,
                        files.ChangedPaths,
                        files.PullRequestNumber is { } number && files.PullRequestUrl is { } url
                            ? new RepositoryListPullRequest(number, url)
                            : null,
                        files.CheckedAt)
                    : null));
        }

        return rows
            .OrderBy(r => r.SolutionName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Repository, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
