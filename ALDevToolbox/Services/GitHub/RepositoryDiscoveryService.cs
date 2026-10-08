using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// One repository the sweep found and nobody tracks yet, as the panel on the
/// Solutions page renders it.
/// </summary>
/// <param name="CandidateId">The stored finding, so Track and Ignore name a row rather than a repository name.</param>
/// <param name="FullName"><c>owner/name</c>.</param>
/// <param name="HtmlUrl">The repository on GitHub.</param>
/// <param name="Name">The repository's own name, without the owner.</param>
/// <param name="AppName">What the manifest calls the extension - the solution name that is offered.</param>
/// <param name="DefaultBranch">The branch a build would clone.</param>
public sealed record UntrackedRepositoryRow(
    int CandidateId,
    string FullName,
    string HtmlUrl,
    string Name,
    string AppName,
    string DefaultBranch);

/// <summary>
/// What the panel needs in one read: the rows, the GitHub organisation they came
/// from, and the country code to pre-fill a new solution with.
/// </summary>
public sealed record UntrackedRepositoriesView(
    IReadOnlyList<UntrackedRepositoryRow> Rows,
    string? OrgLogin,
    string? SuggestedCountry);

/// <summary>
/// Finds the AL repositories in the connected GitHub organisation that no
/// solution tracks yet, so a consultant who has just connected their
/// organisation is told what the workbench does not know about instead of having
/// to remember it.
///
/// <para><strong>This is repository discovery, not the extension discovery of
/// <see cref="ProjectDiscoveryService"/>.</strong> That one looks inside one
/// solution's repositories for the extensions a pipeline can pick; this one
/// looks across an organisation for repositories no solution has.</para>
///
/// <para><strong>The two credentials do different jobs here, as everywhere
/// else.</strong> The sweep is an act of the organisation and runs on the
/// installation token - listing the organisation's repositories and probing each
/// one for an <c>app.json</c>. The panel is a page one person is looking at, so
/// what it lists is narrowed to the repositories that person can open on GitHub
/// themselves (<see cref="GitHubAccessService.FilterAccessibleAsync"/>).
/// "Already tracked", by contrast, is decided against <em>every</em> solution in
/// the organisation and not only the ones the viewer can see: a repository that a
/// Private solution already tracks must not be offered, because offering it would
/// say that solution exists.</para>
///
/// <para>See <c>.design/github-integration-phase2.md</c>, issue #629.</para>
/// </summary>
public sealed class RepositoryDiscoveryService
{
    /// <summary>
    /// How deep the probe looks for a manifest: the repository root, or exactly
    /// one folder down. An AL workspace keeps each extension in its own top-level
    /// folder, and anything deeper is a vendored copy or a sample rather than the
    /// thing this repository ships.
    /// </summary>
    private const int MaxManifestDepth = 2;

    private readonly AppDbContext _db;
    private readonly GitHubAppClient _github;
    private readonly GitHubAccessService _access;
    private readonly GitHubConnectionService _connection;
    private readonly OrganizationConfigService _orgConfig;
    private readonly ProjectService _projects;
    private readonly IOrganizationContext _orgContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<RepositoryDiscoveryService> _logger;

    public RepositoryDiscoveryService(
        AppDbContext db,
        GitHubAppClient github,
        GitHubAccessService access,
        GitHubConnectionService connection,
        OrganizationConfigService orgConfig,
        ProjectService projects,
        IOrganizationContext orgContext,
        TimeProvider clock,
        ILogger<RepositoryDiscoveryService> logger)
    {
        _db = db;
        _github = github;
        _access = access;
        _connection = connection;
        _orgConfig = orgConfig;
        _projects = projects;
        _orgContext = orgContext;
        _clock = clock;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; repository discovery called outside an authenticated request or an organisation scope.");

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; repository discovery called outside an authenticated request.");

    // ── The sweep ────────────────────────────────────────────────────────

    /// <summary>
    /// Probes every repository the installation can see for an <c>app.json</c>
    /// and reconciles the stored candidates with what it found. Returns how many
    /// AL repositories the organisation has; zero when the organisation has not
    /// connected GitHub, which is an ordinary answer rather than a failure.
    ///
    /// <para>Runs on the installation token, so it needs an organisation in scope
    /// and no user - the nightly scheduler enters an
    /// <c>AmbientOrganizationScope</c> per organisation and calls exactly this.
    /// A repository that cannot be probed is logged and skipped: one unreadable
    /// repository must not cost the organisation its whole sweep.</para>
    /// </summary>
    public async Task<int> SweepCurrentOrganisationAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var status = await _connection.GetStatusAsync(ct);
        if (status.InstallationId is not { } installationId)
        {
            _logger.LogInformation(
                "Organisation {OrgId} has no GitHub connection, so there is nothing to discover.", orgId);
            return 0;
        }

        var token = await _github.GetInstallationTokenAsync(installationId, ct);
        var listing = await _github.ListInstallationRepositoriesAsync(token, ct);
        var repositories = listing.Repositories;
        if (listing.Truncated)
        {
            // Not the same as "the App cannot read them": there are simply more
            // than one sweep reads, and saying so is better than a repository
            // quietly never being offered.
            _logger.LogWarning(
                "Organisation {OrgId} shares more than 1000 repositories with the app; some were not checked in this sweep.",
                orgId);
        }

        var found = new List<(GitHubRepositorySummary Repo, string Path, AppJsonManifest Manifest)>();
        foreach (var repo in repositories)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await ProbeAsync(token, repo, ct) is { } hit) found.Add((repo, hit.Path, hit.Manifest));
            }
            catch (Exception ex) when (ex is GitHubApiException or HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex,
                    "Could not probe {RepoFullName} for an app.json; it is left out of this sweep.", repo.FullName);
            }
        }

        await ReconcileAsync(orgId, found, ct);
        _logger.LogInformation(
            "Repository discovery for organisation {OrgId} found {AlCount} AL repositories of the installation's {RepositoryCount}.",
            orgId, found.Count, repositories.Count);
        return found.Count;
    }

    /// <summary>
    /// One repository's probe: a single recursive tree read at the default
    /// branch, then the one manifest that read pointed at. The root's
    /// <c>app.json</c> wins over a folder's, and folders the build would not
    /// compile - test folders, <c>.alpackages</c>, <c>.git</c> and friends - are
    /// not looked at.
    ///
    /// <para>A truncated tree is used as far as it goes: GitHub says when it
    /// cropped the listing, and a partial answer still finds the manifest in
    /// almost every repository. Warning rather than a refusal, because the
    /// alternative - a call per folder - is what the recursive read exists to
    /// avoid.</para>
    /// </summary>
    private async Task<(string Path, AppJsonManifest Manifest)?> ProbeAsync(
        string installationToken, GitHubRepositorySummary repo, CancellationToken ct)
    {
        var tree = await _github.ListTreeAsync(
            installationToken, repo.Owner, repo.Name, repo.DefaultBranch, ct);
        if (tree.Truncated)
        {
            _logger.LogWarning(
                "GitHub cropped the file list for {RepoFullName}; discovery used the {EntryCount} entries it did return.",
                repo.FullName, tree.Entries.Count);
        }

        foreach (var path in ManifestPaths(tree.Entries))
        {
            var file = await _github.GetFileAsync(
                installationToken, repo.Owner, repo.Name, path, repo.DefaultBranch, ct);
            if (file is null) continue;
            if (AppJsonManifestParser.Parse(file.Text) is { } manifest)
            {
                // A test app in a folder the folder rules miss would otherwise name the
                // repository after itself, or offer a repository of only tests (#1193).
                if (AppJsonManifestParser.IsTestApp(manifest)) continue;
                return (path, manifest);
            }

            _logger.LogWarning(
                "{RepoFullName} has a {Path} that is not readable as an app.json.", repo.FullName, path);
        }
        return null;
    }

    /// <summary>
    /// The manifest paths worth reading in one tree, root first and then folders
    /// in a stable order, so two sweeps over an unchanged repository settle on
    /// the same one.
    /// </summary>
    internal static IReadOnlyList<string> ManifestPaths(IReadOnlyList<GitHubTreeEntry> entries)
    {
        var paths = new List<string>();
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Type, "blob", StringComparison.Ordinal)) continue;
            var segments = entry.Path.Split('/');
            if (segments.Length > MaxManifestDepth) continue;
            if (!string.Equals(segments[^1], AppJsonManifestParser.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (segments.Length == MaxManifestDepth
                && (AppJsonManifestParser.IsExcludedSegment(segments[0]) || AppJsonManifestParser.IsTestSegment(segments[0])))
            {
                continue;
            }
            paths.Add(entry.Path);
        }

        return paths
            .OrderBy(p => p.Contains('/') ? 1 : 0)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Makes the stored candidates say what the sweep just saw: refresh what is
    /// still there, add what is new, and drop what no longer matches. Dropping is
    /// deliberate even for an ignored row whose repository stopped being an AL
    /// repository - the finding is gone, so the decision about it has nothing
    /// left to apply to. An ignored row whose repository is still found keeps its
    /// <c>ignored_at</c>.
    /// </summary>
    private async Task ReconcileAsync(
        int orgId,
        IReadOnlyList<(GitHubRepositorySummary Repo, string Path, AppJsonManifest Manifest)> found,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var existing = await _db.GitHubRepositoryCandidates.ToListAsync(ct);
        var byName = existing.ToDictionary(c => c.FullName, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (repo, path, manifest) in found)
        {
            seen.Add(repo.FullName);
            if (!byName.TryGetValue(repo.FullName, out var row))
            {
                row = new GitHubRepositoryCandidate
                {
                    OrganizationId = orgId,
                    FullName = repo.FullName,
                    DiscoveredAt = now,
                };
                _db.GitHubRepositoryCandidates.Add(row);
            }

            row.HtmlUrl = repo.HtmlUrl;
            row.CloneUrl = repo.CloneUrl;
            row.DefaultBranch = repo.DefaultBranch;
            row.AppName = Trim(manifest.Name, 250);
            row.AppId = Trim(manifest.Id, 100);
            row.AppJsonPath = Trim(path, 500);
            row.LastSeenAt = now;
        }

        var vanished = existing.Where(c => !seen.Contains(c.FullName)).ToList();
        if (vanished.Count > 0) _db.GitHubRepositoryCandidates.RemoveRange(vanished);

        await _db.SaveChangesAsync(ct);
        if (vanished.Count > 0)
        {
            _logger.LogInformation(
                "Dropped {Count} repository candidate(s) that no longer look like AL repositories for organisation {OrgId}.",
                vanished.Count, orgId);
        }
    }

    private static string Trim(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max];
    }

    // ── What the panel reads ─────────────────────────────────────────────

    /// <summary>
    /// The untracked repositories to offer the person looking, with the GitHub
    /// organisation they came from and the country code a new solution should be
    /// pre-filled with.
    ///
    /// <para>Two narrowings, in this order and for different reasons. Every
    /// solution in the organisation subtracts its repositories, whether or not
    /// the viewer may see that solution - otherwise a Private solution's
    /// repository would be offered here and its existence given away. What is
    /// left is then asked about on GitHub with the viewer's own token, so a
    /// repository they cannot open is neither listed nor counted.</para>
    /// </summary>
    public async Task<UntrackedRepositoriesView> ListUntrackedAsync(CancellationToken ct = default)
    {
        var userId = RequireUserId();
        var status = await _connection.GetStatusAsync(ct);
        var settings = (await _orgConfig.GetCurrentAsync(ct)).Settings;
        var country = OrganizationConfigService
            .ParseAutoImportCountries(settings.AutoImportCountry)
            .FirstOrDefault();

        var candidates = await _db.GitHubRepositoryCandidates.AsNoTracking()
            .Where(c => c.IgnoredAt == null)
            .OrderBy(c => c.FullName)
            .ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return new UntrackedRepositoriesView([], status.OrgLogin, country);
        }

        // Org-scoped by the EF query filter, and deliberately NOT narrowed to the
        // solutions this viewer may see - see the note on the class.
        var trackedUrls = await _db.OeProjectRepositories.AsNoTracking()
            .Where(r => r.Provider == RepositoryProvider.GitHub)
            .Where(r => _db.OeProjects.Any(p => p.Id == r.ProjectId && p.DeletedAt == null))
            .Select(r => r.Url)
            .ToListAsync(ct);
        var tracked = trackedUrls.Select(CanonicalUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var untracked = candidates
            .Where(c => !tracked.Contains(CanonicalUrl(c.CloneUrl)))
            .ToList();
        if (untracked.Count == 0)
        {
            return new UntrackedRepositoriesView([], status.OrgLogin, country);
        }

        var visible = (await _access.FilterAccessibleAsync(userId, untracked.Select(c => c.FullName), ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = untracked
            .Where(c => visible.Contains(c.FullName))
            .Select(c => new UntrackedRepositoryRow(
                c.Id,
                c.FullName,
                c.HtmlUrl,
                RepositoryName(c.FullName),
                string.IsNullOrWhiteSpace(c.AppName) ? RepositoryName(c.FullName) : c.AppName,
                c.DefaultBranch))
            .ToList();

        _logger.LogInformation(
            "Offering {OfferedCount} untracked AL repositories of {CandidateCount} to user {UserId}.",
            rows.Count, candidates.Count, userId);
        return new UntrackedRepositoriesView(rows, status.OrgLogin, country);
    }

    // ── What a person decides ────────────────────────────────────────────

    /// <summary>
    /// Creates a solution for one candidate and attaches the repository to it,
    /// then forgets the candidate: it is tracked now, so there is nothing left to
    /// offer. Returns the new solution's id.
    ///
    /// <para>Validation is <see cref="ProjectService.CreateProjectAsync"/>'s, so
    /// a name clash or a missing country comes back field-keyed and renders
    /// beside the field the person typed in.</para>
    /// </summary>
    /// <exception cref="PlanValidationException">The name or country was refused, or the candidate is gone.</exception>
    public async Task<int> TrackAsync(int candidateId, string name, string? country, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var userId = RequireUserId();
        var candidate = await _db.GitHubRepositoryCandidates
            .FirstOrDefaultAsync(c => c.Id == candidateId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Name"] = "This repository is no longer on the list. Check GitHub again to refresh it.",
            });

        // The list was filtered to what this person can see on GitHub when it was
        // rendered, but the id posted back is the client's, and access can have
        // gone since. Asking GitHub again is what keeps someone from turning a
        // repository they cannot read into a solution they own.
        if (!await _access.CanAccessRepoAsync(userId, candidate.FullName, ct))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Name"] = "You no longer have access to this repository on GitHub.",
            });
        }

        var projectId = await _projects.CreateProjectAsync(new ProjectInput(
            name,
            // Nothing here knows an abbreviation for the customer; the solution's
            // own settings page is where one gets typed.
            ShortName: null,
            country,
            [new ProjectRepositoryInput(RepositoryProvider.GitHub, candidate.CloneUrl, RepositoryName(candidate.FullName))]),
            access: null, ct);

        _db.GitHubRepositoryCandidates.Remove(candidate);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Tracked {RepoFullName} as solution {ProjectId}.", candidate.FullName, projectId);
        return projectId;
    }

    /// <summary>
    /// Turns a candidate down. The row stays - so the next sweep does not offer
    /// it again - and disappears from the panel. It comes back only if somebody
    /// tracks the repository, which deletes the row outright.
    /// </summary>
    public async Task IgnoreAsync(int candidateId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var userId = RequireUserId();
        var candidate = await _db.GitHubRepositoryCandidates
            .FirstOrDefaultAsync(c => c.Id == candidateId, ct);
        if (candidate is null || candidate.IgnoredAt is not null) return;

        candidate.IgnoredAt = _clock.GetUtcNow().UtcDateTime;
        candidate.IgnoredByUserId = userId;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("User {UserId} set aside the repository {RepoFullName}.", userId, candidate.FullName);
    }

    /// <summary>
    /// Puts a repository somebody has just hidden back on the list - the undo
    /// offered beside "hidden" while the page is still open. Named by the
    /// repository rather than by candidate id because that is what the panel
    /// still holds once the row has left the list, and it is the same thing a
    /// later sweep would re-find. Hiding something already offered is a no-op.
    /// </summary>
    public async Task UnignoreAsync(string fullName, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var userId = RequireUserId();
        var candidate = await _db.GitHubRepositoryCandidates
            .FirstOrDefaultAsync(c => c.FullName == fullName, ct);
        if (candidate is null || candidate.IgnoredAt is null) return;

        candidate.IgnoredAt = null;
        candidate.IgnoredByUserId = null;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "User {UserId} put the repository {RepoFullName} back on the list.", userId, candidate.FullName);
    }

    /// <summary>The repository's own name, without the owner.</summary>
    private static string RepositoryName(string fullName)
    {
        var slash = fullName.LastIndexOf('/');
        return slash >= 0 && slash < fullName.Length - 1 ? fullName[(slash + 1)..] : fullName;
    }

    /// <summary>
    /// The form two clone URLs have to share to be the same repository. GitHub's
    /// own URL ends in <c>.git</c> and a URL somebody pasted into a solution
    /// usually does not, so comparing them as stored would offer a repository
    /// that is already tracked.
    /// </summary>
    internal static string CanonicalUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        return trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }
}
