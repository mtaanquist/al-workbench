using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Import;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Services.GitHub;

/// <summary>One value in one manifest that is behind, as a page renders it.</summary>
/// <param name="Field">The stored field key - <c>application</c>, <c>platform</c> or <c>dependency:&lt;id&gt;</c>.</param>
/// <param name="Label">What that field is called in front of a person.</param>
/// <param name="Current">What the manifest says now.</param>
/// <param name="Proposed">What the pull request would put there.</param>
public sealed record DependencyDriftChange(string Field, string Label, string Current, string Proposed);

/// <summary>One <c>app.json</c> and everything that moves in it.</summary>
public sealed record DependencyDriftFile(string Path, IReadOnlyList<DependencyDriftChange> Changes);

/// <summary>One repository, its drifted manifests, and where to open it.</summary>
/// <param name="CurrentApplication">
/// The Business Central version its manifests still ask for, as
/// <c>major.minor</c>. Empty when nothing but a dependency moved.
/// </param>
/// <param name="TargetApplication">
/// The Business Central version it was measured against, as <c>major.minor</c>.
/// Empty when nothing but a dependency moved.
/// </param>
/// <param name="EnvironmentType">
/// What the target came from (issue #1081): <c>Production</c> for the solution's
/// production environment, <c>Sandbox</c> when the solution has no production
/// environment with a known version and its sandbox stood in. Null only for a finding
/// recorded before environments were the yardstick, which was measured against the
/// newest imported release and is replaced by the next scan.
/// </param>
/// <param name="Environment">
/// The environment's name, and <paramref name="Solution"/> the name of the solution it
/// belongs to. Both null when that solution is not one the viewer may see.
/// </param>
public sealed record DependencyDriftRepository(
    string Repository,
    string HtmlUrl,
    string CurrentApplication,
    IReadOnlyList<DependencyDriftFile> Files,
    string TargetApplication = "",
    string? EnvironmentType = null,
    string? Environment = null,
    string? Solution = null);

/// <summary>How many repositories are still on one Business Central version.</summary>
public sealed record DependencyDriftGroup(string CurrentApplication, int RepositoryCount);

/// <summary>What the Solutions panel needs in one read.</summary>
/// <param name="TargetVersion">
/// The release version, as <c>major.minor</c>, of findings recorded before environments
/// were the yardstick. Null when there are none - the normal case after a scan.
/// </param>
public sealed record DependencyDriftSummary(
    IReadOnlyList<DependencyDriftRepository> Repositories,
    IReadOnlyList<DependencyDriftGroup> Groups,
    string? TargetVersion)
{
    /// <summary>True when there is nothing to show, which is the panel's silent state.</summary>
    public bool IsEmpty => Repositories.Count == 0;
}

/// <summary>What "Open update pull requests" did to one repository.</summary>
/// <param name="Refusal">Why nothing was opened, in words the person can act on. Null when one was.</param>
/// <param name="IsUpToDate">
/// True when the refusal only means there was nothing left to change - the repository
/// has caught up since the scan - rather than something somebody has to fix.
/// </param>
/// <param name="IsRateLimited">
/// True when GitHub refused because the person's account is making changes too fast.
/// Every further write on that account is refused too until it cools down.
/// </param>
public sealed record DependencyDriftPullRequest(
    string Repository,
    GitHubPullRequest? PullRequest,
    bool IsNewPullRequest,
    int FileCount,
    string? Refusal,
    bool IsUpToDate = false,
    bool IsRateLimited = false);

/// <summary>What one solution's automatic run did (issue #1104).</summary>
/// <param name="Opened">How many pull requests it opened.</param>
/// <param name="Blocked">
/// What stopped it, worded to follow "Pull requests have stopped:" on the solution. Null
/// when nothing did.
/// </param>
/// <param name="RateLimited">
/// True when GitHub said the person's account was making changes too fast, so the run
/// stopped where it was. That is not something the solution has to fix: the next night
/// carries on, and nothing else is opened as that person in the meantime.
/// </param>
public sealed record AutomaticUpdatePullRequests(int Opened, string? Blocked, bool RateLimited = false);

/// <summary>
/// Which tracked repositories still target last year's Business Central, and
/// the pull requests that move them on (issue #630).
///
/// <para><strong>The scan is the organisation's, the pull request is the
/// person's.</strong> Reading every tracked repository's <c>app.json</c> happens
/// when a release is imported, with nobody watching, so it runs on the
/// installation token - the same read repository discovery does. Writing runs on
/// the acting user's own token through
/// <see cref="GitHubRepositoryService.ResolveAsync"/>, so GitHub enforces their
/// permissions and the pull request is genuinely theirs.</para>
///
/// <para><strong>Only what actually moved is edited.</strong> A manifest is a
/// file people maintain; a pull request that reflows it is one nobody can
/// review. <see cref="AppJsonValueEditor"/> replaces the bytes of the values
/// that changed and leaves the rest of the file alone.</para>
///
/// <para><strong>The customer's environment is the yardstick.</strong> A repository
/// is measured against the Business Central version of the production environment of
/// the solution that tracks it - the first by name when there are several - because
/// that is what the customer runs (issue #1081). A solution with no production
/// environment with a known version is measured against its first sandbox instead,
/// and one with neither is not checked at all: an imported release says nothing
/// about what a customer runs.</para>
///
/// <para><strong>Behind, never ahead.</strong> A value is only proposed when
/// what the manifest asks for is <em>lower</em> than what the workbench now knows
/// about, compared numerically by <see cref="BcVersionComparer"/>. A repository
/// already on the new version is not offered a pull request that changes
/// nothing, and one deliberately pinned ahead is left alone.</para>
///
/// <para>See <c>.design/github-integration-phase2.md</c>, issue #630.</para>
/// </summary>
public sealed class DependencyDriftService
{
    /// <summary>Branch names are <c>aldt/bump-bc-&lt;major.minor&gt;</c>, per the design doc.</summary>
    public const string BranchPrefix = "aldt/bump-bc-";

    /// <summary>The field key for the manifest's <c>application</c>.</summary>
    public const string ApplicationField = "application";

    /// <summary>The field key for the manifest's <c>platform</c>.</summary>
    public const string PlatformField = "platform";

    /// <summary>What a dependency's field key starts with; the app id follows it.</summary>
    public const string DependencyFieldPrefix = "dependency:";

    /// <summary>
    /// How many branch names are tried before giving up - the same handful
    /// <see cref="GitHubRecipeDeliveryService"/> allows, and for the same
    /// reason: a merged branch's history is somebody's work and is stepped past
    /// rather than rewound.
    /// </summary>
    private const int MaxBranchAttempts = 10;

    /// <summary>
    /// The gap the automatic run leaves between two writes to GitHub. GitHub asks
    /// integrations to wait at least a second between requests that create content, and
    /// enforces about 80 a minute per account with its secondary rate limit; one
    /// repository's pull request is five or more such writes, so the morning a Business
    /// Central wave lands and many customers move at once, a run that does not pace
    /// itself reaches that limit within a minute.
    /// </summary>
    internal static readonly TimeSpan WriteSpacing = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How the automatic run waits out <see cref="WriteSpacing"/>. A delay on the
    /// service's clock; tests swap it for one that records the wait and returns at once.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> PauseAsync { get; set; }

    /// <summary>Whether writes are paced - only the automatic run, which nobody is waiting on.</summary>
    private bool _pacing;

    /// <summary>When the last paced write started, on the service's clock.</summary>
    private DateTimeOffset? _lastWriteAt;

    private readonly AppDbContext _db;
    private readonly GitHubAppClient _github;
    private readonly GitHubAccessService _access;
    private readonly GitHubConnectionService _connection;
    private readonly GitHubRepositoryService _repositories;
    private readonly CatalogService _catalog;
    private readonly ProjectAccess _projectAccess;
    private readonly ObjectExplorerLinks _links;
    private readonly PublicOrigin _publicOrigin;
    private readonly IOrganizationContext _orgContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<DependencyDriftService> _logger;

    public DependencyDriftService(
        AppDbContext db,
        GitHubAppClient github,
        GitHubAccessService access,
        GitHubConnectionService connection,
        GitHubRepositoryService repositories,
        CatalogService catalog,
        ProjectAccess projectAccess,
        ObjectExplorerLinks links,
        PublicOrigin publicOrigin,
        IOrganizationContext orgContext,
        TimeProvider clock,
        ILogger<DependencyDriftService> logger)
    {
        _db = db;
        _github = github;
        _access = access;
        _connection = connection;
        _repositories = repositories;
        _catalog = catalog;
        _projectAccess = projectAccess;
        _links = links;
        _publicOrigin = publicOrigin;
        _orgContext = orgContext;
        _clock = clock;
        _logger = logger;
        PauseAsync = (delay, ct) => Task.Delay(delay, _clock, ct);
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; the dependency drift scan ran outside an authenticated request or an organisation scope.");

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; dependency drift called outside an authenticated request.");

    // ── The scan ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads every tracked GitHub repository's manifests and records what is
    /// behind <paramref name="releaseId"/>. Returns how many findings it wrote;
    /// zero is an ordinary answer - nothing drifted, GitHub is not connected, or
    /// the release is not one to compare against.
    ///
    /// <para>Runs on the installation token and needs an organisation in scope
    /// but no user, so the release import can call it for the organisation whose
    /// release it just finished. The findings of the previous scan are replaced
    /// wholesale: a value somebody has bumped by hand since simply stops being
    /// listed, which is what makes the panel disappear on its own.</para>
    /// </summary>
    public async Task<int> ScanForReleaseAsync(int releaseId, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        // Two imports can finish at once now that builds run side by side (#1137), and
        // each replaces the organisation's whole set of findings.
        using var scanning = await ScanGate.EnterAsync(orgId, ct);
        var release = await _db.OeReleases.AsNoTracking().FirstOrDefaultAsync(r => r.Id == releaseId, ct);
        if (release is null || release.Kind != "first_party" || release.IsPrerelease)
        {
            // A preview build is not a target either: nobody's app.json should be
            // moved to a version customers can't run yet.
            _logger.LogInformation(
                "Release {ReleaseId} is not a shipped first-party Business Central release, so nothing is compared against it.",
                releaseId);
            return 0;
        }
        if (string.IsNullOrWhiteSpace(release.BcVersion))
        {
            _logger.LogInformation(
                "Release {ReleaseId} has no application version, so there is nothing for repositories to be behind.",
                releaseId);
            return 0;
        }

        var status = await _connection.GetStatusAsync(ct);
        if (status.InstallationId is not { } installationId || string.IsNullOrWhiteSpace(status.OrgLogin))
        {
            _logger.LogInformation(
                "Organisation {OrgId} has no GitHub connection, so no repository can be checked for drift.", orgId);
            return 0;
        }

        var catalogue = (await _catalog.GetAllAsync(ct))
            .Where(w => !string.IsNullOrWhiteSpace(w.DepId) && !string.IsNullOrWhiteSpace(w.DepVersionDefault))
            .GroupBy(w => AppJsonValueEditor.NormaliseId(w.DepId))
            .ToDictionary(g => g.Key, g => g.First());

        var tracked = await TrackedRepositoryNamesAsync(status.OrgLogin!, ct);
        if (tracked.Count == 0)
        {
            await ReplaceFindingsAsync([], releaseId, ct);
            return 0;
        }
        var targets = await EnvironmentTargetsAsync(ct);

        var token = await _github.GetInstallationTokenAsync(installationId, ct);
        var listing = await _github.ListInstallationRepositoriesAsync(token, ct);
        var installed = listing.Repositories.ToDictionary(r => r.FullName, StringComparer.OrdinalIgnoreCase);

        var findings = new List<GitHubRepositoryDrift>();
        foreach (var fullName in tracked)
        {
            ct.ThrowIfCancellationRequested();
            if (!targets.TryGetValue(fullName, out var environment))
            {
                _logger.LogInformation(
                    "No solution tracking {RepoFullName} has an environment with a known version, so it is not checked for drift.",
                    fullName);
                continue;
            }
            if (!installed.TryGetValue(fullName, out var repo))
            {
                // Two different reasons, and they read differently to whoever is
                // wondering why a repository is missing from the panel.
                if (listing.Truncated)
                {
                    _logger.LogWarning(
                        "The installation shares more than 1000 repositories, so {RepoFullName} was not among the ones read; it is left out of the drift scan.",
                        fullName);
                }
                else
                {
                    _logger.LogInformation(
                        "{RepoFullName} is tracked by a solution but is not one the GitHub App can read, so it is left out of the drift scan.",
                        fullName);
                }
                continue;
            }

            try
            {
                var target = new DriftTarget(environment.Version, PlatformFor(environment.Version), environment.Id);
                findings.AddRange(
                    await ScanRepositoryAsync(orgId, token, repo, release, target, catalogue, ct));
            }
            catch (Exception ex) when (ex is GitHubApiException or HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex,
                    "Could not read {RepoFullName} while checking for dependency drift; it is left out of this scan.",
                    repo.FullName);
            }
        }

        var stored = await ReplaceFindingsAsync(findings, releaseId, ct);
        _logger.LogInformation(
            "Dependency drift for organisation {OrgId} after release {ReleaseId} ({BcVersion}): {CheckedCount} of {TrackedCount} repositories had an environment to measure against; {FindingCount} findings across {RepositoryCount} repositories.",
            orgId, releaseId, release.BcVersion, tracked.Count(targets.ContainsKey), tracked.Count, stored,
            findings.Select(f => f.Repository).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        return stored;
    }

    private static readonly ALDevToolbox.Services.Workers.KeyedGate<int> ScanGate = new();

    /// <summary>
    /// The newest first-party release that has finished importing, or null when
    /// the organisation has none - what "Check again" scans against.
    /// </summary>
    public async Task<int?> NewestFirstPartyReleaseIdAsync(CancellationToken ct = default)
    {
        RequireOrganizationId();
        var releases = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Kind == "first_party" && !r.IsPrerelease && r.Status == "ready" && r.BcVersion != null)
            .Select(r => new { r.Id, r.BcVersion })
            .ToListAsync(ct);
        return releases
            .OrderByDescending(r => r.BcVersion, BcVersionComparer.Instance)
            .ThenByDescending(r => r.Id)
            .Select(r => (int?)r.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// What one repository is measured against: the application and platform versions,
    /// and the environment they came from.
    /// </summary>
    private sealed record DriftTarget(string Application, string? Platform, int EnvironmentId);

    /// <summary>An environment a repository is measured against.</summary>
    private sealed record EnvironmentTarget(int Id, string Version);

    /// <summary>
    /// The environment each tracked GitHub repository is measured against, keyed by
    /// <c>owner/name</c>. Per solution the first production environment by name that is
    /// not deleted and has a version, else the first such sandbox; a repository tracked by
    /// more than one solution takes the oldest solution that has one, so the answer does
    /// not change from one scan to the next. A repository missing from the result is not
    /// checked.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, EnvironmentTarget>> EnvironmentTargetsAsync(CancellationToken ct)
    {
        var tracking = await _db.OeProjectRepositories.AsNoTracking()
            .Where(r => r.Provider == RepositoryProvider.GitHub)
            .Where(r => _db.OeProjects.Any(p => p.Id == r.ProjectId && p.DeletedAt == null))
            .Select(r => new { r.ProjectId, r.Url })
            .ToListAsync(ct);
        if (tracking.Count == 0) return new Dictionary<string, EnvironmentTarget>();

        var projectIds = tracking.Select(t => t.ProjectId).Distinct().ToList();
        var environments = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(EnvironmentQueries.NotSoftDeleted)
            .Where(e => projectIds.Contains(e.ProjectId) && e.MissingSince == null && e.Version != null)
            .Select(e => new { e.Id, e.ProjectId, e.Name, e.Type, e.Version })
            .ToListAsync(ct);

        var perProject = environments
            .Where(e => IsKnownVersion(e.Version) && BcEnvironmentTypes.Normalize(e.Type) is not null)
            .GroupBy(e => e.ProjectId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(e => BcEnvironmentTypes.IsProduction(e.Type))
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id)
                    .Select(e => new EnvironmentTarget(e.Id, e.Version!.Trim()))
                    .First());

        var result = new Dictionary<string, EnvironmentTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in tracking.OrderBy(t => t.ProjectId))
        {
            if (ToFullName(row.Url) is not { } name || result.ContainsKey(name)) continue;
            if (perProject.TryGetValue(row.ProjectId, out var target)) result[name] = target;
        }
        return result;
    }

    /// <summary>A version with at least a numeric major and minor - anything less says nothing a manifest can be behind.</summary>
    private static bool IsKnownVersion(string? version)
    {
        var segments = (version ?? string.Empty).Trim().Split('.');
        return segments.Length >= 2 && int.TryParse(segments[0], out _) && int.TryParse(segments[1], out _);
    }

    /// <summary>
    /// The platform an environment's version implies. Business Central's platform keeps
    /// the major and a zero minor through a whole wave (application 28.2 runs on platform
    /// 28.0), and an environment reports only its application version.
    /// </summary>
    private static string PlatformFor(string applicationVersion) =>
        applicationVersion.Trim().Split('.')[0] + ".0";

    /// <summary>One repository's manifests, and what each of them is behind on.</summary>
    private async Task<List<GitHubRepositoryDrift>> ScanRepositoryAsync(
        int orgId,
        string installationToken,
        GitHubRepositorySummary repo,
        OeRelease release,
        DriftTarget target,
        IReadOnlyDictionary<string, WellKnownDependency> catalogue,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var findings = new List<GitHubRepositoryDrift>();

        var tree = await _github.ListTreeAsync(
            installationToken, repo.Owner, repo.Name, repo.DefaultBranch, ct);
        if (tree.Truncated)
        {
            _logger.LogWarning(
                "GitHub cropped the file list for {RepoFullName}; the drift scan used the {EntryCount} entries it did return.",
                repo.FullName, tree.Entries.Count);
        }

        // The same manifests repository discovery looks at - the repository root
        // or one folder down, test folders left out - so the two features cannot
        // come to disagree about what a repository ships.
        foreach (var path in RepositoryDiscoveryService.ManifestPaths(tree.Entries))
        {
            ct.ThrowIfCancellationRequested();
            var file = await _github.GetFileAsync(
                installationToken, repo.Owner, repo.Name, path, repo.DefaultBranch, ct);
            if (file is null) continue;
            if (AppJsonManifestParser.Parse(file.Text) is not { } manifest)
            {
                _logger.LogWarning(
                    "{RepoFullName} has a {Path} that is not readable as an app.json, so it is not checked for drift.",
                    repo.FullName, path);
                continue;
            }

            // A manifest with no application version states no Business Central
            // it targets, so there is nothing to say it is behind.
            if (string.IsNullOrWhiteSpace(manifest.Application)) continue;

            foreach (var change in Changes(manifest, target.Application, target.Platform, catalogue))
            {
                findings.Add(new GitHubRepositoryDrift
                {
                    OrganizationId = orgId,
                    Repository = repo.FullName,
                    Path = path,
                    Field = change.Field,
                    Current = Trim(change.Current, 100),
                    Proposed = Trim(change.Proposed, 100),
                    ReleaseId = release.Id,
                    EnvironmentId = target.EnvironmentId,
                    DetectedAt = now,
                });
            }
        }

        return findings;
    }

    /// <summary>
    /// What one manifest is behind on: its Business Central application and
    /// platform, and any dependency the catalogue has a newer default for.
    /// </summary>
    private static IEnumerable<(string Field, string Current, string Proposed)> Changes(
        AppJsonManifest manifest,
        string applicationTarget,
        string? platformTarget,
        IReadOnlyDictionary<string, WellKnownDependency> catalogue)
    {
        if (manifest.Application is { } application && IsBehindByRelease(application, applicationTarget))
        {
            yield return (ApplicationField, application, ProposedVersion(applicationTarget, application));
        }

        if (platformTarget is not null
            && manifest.Platform is { } platform
            && IsBehindByRelease(platform, platformTarget))
        {
            yield return (PlatformField, platform, ProposedVersion(platformTarget, platform));
        }

        foreach (var dependency in manifest.Dependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency.Version)) continue;
            if (!catalogue.TryGetValue(AppJsonValueEditor.NormaliseId(dependency.Id), out var known)) continue;
            if (BcVersionComparer.Instance.Compare(dependency.Version, known.DepVersionDefault) >= 0) continue;

            yield return (
                DependencyFieldPrefix + AppJsonValueEditor.NormaliseId(dependency.Id),
                dependency.Version!,
                known.DepVersionDefault);
        }
    }

    /// <summary>
    /// Whether a manifest asks for an older Business Central than its target. Compared at <c>major.minor</c>, because that is the wave a
    /// repository is on - a build number nobody typed is not drift.
    /// </summary>
    private static bool IsBehindByRelease(string current, string target) =>
        BcVersionComparer.Instance.Compare(
            BcArtifactIndex.ToMajorMinor(current), BcArtifactIndex.ToMajorMinor(target)) < 0;

    /// <summary>
    /// The new value, written the way the old one was: the target's
    /// <c>major.minor</c> followed by as many zeroes as the manifest's own value
    /// had segments. An <c>application</c> is a minimum, so <c>28.2.0.0</c> is
    /// what a person would have typed - not a four-part build number.
    /// </summary>
    private static string ProposedVersion(string target, string current)
    {
        var head = BcArtifactIndex.ToMajorMinor(target).Split('.');
        var segments = Math.Max(current.Split('.').Length, head.Length);
        var parts = new List<string>(head);
        while (parts.Count < segments) parts.Add("0");
        return string.Join('.', parts);
    }

    /// <summary>
    /// Makes the stored findings say what this scan saw. The whole
    /// organisation's rows go, not only this release's: a finding against a
    /// release that has since been superseded is not something anyone should be
    /// offered a pull request for.
    /// </summary>
    private async Task<int> ReplaceFindingsAsync(
        IReadOnlyList<GitHubRepositoryDrift> findings, int releaseId, CancellationToken ct)
    {
        // One row per (repository, file, field) - the unique index says so, and a
        // manifest can name the same dependency twice: "id" and "appId" are both
        // read, and an app.json carrying both for one extension yields the same
        // finding twice. Without this the whole organisation's scan would fail on
        // one repository's spelling.
        var deduped = findings
            .GroupBy(f => (f.Repository.ToLowerInvariant(), f.Path, f.Field), TupleComparer)
            .Select(g => g.First())
            .ToList();
        if (deduped.Count != findings.Count)
        {
            _logger.LogInformation(
                "Dropped {DuplicateCount} duplicate drift finding(s) before saving.", findings.Count - deduped.Count);
        }

        var existing = await _db.GitHubRepositoryDrift.ToListAsync(ct);
        if (existing.Count > 0) _db.GitHubRepositoryDrift.RemoveRange(existing);
        if (deduped.Count > 0) _db.GitHubRepositoryDrift.AddRange(deduped);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // One repository's rows must not cost the organisation the whole
            // scan. Start again with the deletions alone, then add each
            // repository's findings on its own, so the ones that are fine land.
            _logger.LogWarning(ex, "Saving the drift findings failed; retrying one repository at a time.");
            return await SaveFindingsPerRepositoryAsync(deduped, ct);
        }

        _logger.LogInformation(
            "Replaced {OldCount} drift findings with {NewCount} from release {ReleaseId}.",
            existing.Count, deduped.Count, releaseId);
        return deduped.Count;
    }

    /// <summary>Compares the (repository, path, field) triples a finding is identified by.</summary>
    private static readonly IEqualityComparer<(string Repository, string Path, string Field)> TupleComparer =
        EqualityComparer<(string Repository, string Path, string Field)>.Default;

    /// <summary>
    /// The fallback for a batch save that failed: clear the tracked graph, delete
    /// what is there, then add one repository's findings per save. A repository
    /// the database refuses is logged and skipped; the rest are stored.
    /// </summary>
    private async Task<int> SaveFindingsPerRepositoryAsync(
        IReadOnlyList<GitHubRepositoryDrift> findings, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var existing = await _db.GitHubRepositoryDrift.ToListAsync(ct);
        if (existing.Count > 0)
        {
            _db.GitHubRepositoryDrift.RemoveRange(existing);
            await _db.SaveChangesAsync(ct);
        }

        var stored = 0;
        foreach (var group in findings.GroupBy(f => f.Repository, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _db.GitHubRepositoryDrift.AddRange(group);
                await _db.SaveChangesAsync(ct);
                stored += group.Count();
            }
            catch (DbUpdateException ex)
            {
                _db.ChangeTracker.Clear();
                _logger.LogWarning(ex,
                    "Could not store the drift findings for {RepoFullName}; the other repositories are unaffected.",
                    group.Key);
            }
        }

        _logger.LogInformation("Stored {StoredCount} of {FindingCount} drift findings one repository at a time.",
            stored, findings.Count);
        return stored;
    }

    /// <summary>
    /// The <c>owner/name</c> of every GitHub repository a live solution tracks,
    /// narrowed to the connected organisation - the workbench has no business
    /// reading a repository somewhere else, and could not open a pull request on
    /// one either.
    /// </summary>
    private async Task<IReadOnlyList<string>> TrackedRepositoryNamesAsync(string orgLogin, CancellationToken ct)
    {
        var urls = await _db.OeProjectRepositories.AsNoTracking()
            .Where(r => r.Provider == RepositoryProvider.GitHub)
            .Where(r => _db.OeProjects.Any(p => p.Id == r.ProjectId && p.DeletedAt == null))
            .Select(r => r.Url)
            .ToListAsync(ct);

        return urls
            .Select(ToFullName)
            .Where(n => n is not null)
            .Select(n => n!)
            .Where(n => string.Equals(n.Split('/')[0], orgLogin, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// <c>owner/name</c> out of a clone URL, however it was pasted - with or
    /// without the <c>.git</c>, with or without a trailing slash. Null when the
    /// URL is not a GitHub repository at all.
    /// </summary>
    internal static string? ToFullName(string? cloneUrl)
    {
        var canonical = RepositoryDiscoveryService.CanonicalUrl(cloneUrl);
        if (canonical.Length == 0) return null;
        var segments = canonical.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;
        var name = segments[^1];
        var owner = segments[^2];
        if (owner.Contains(':') || owner.EndsWith("github.com", StringComparison.OrdinalIgnoreCase)) return null;
        return $"{owner}/{name}";
    }

    // ── What the panel reads ─────────────────────────────────────────────

    /// <summary>
    /// Everything the Solutions panel shows: which repositories are behind,
    /// what moves in each of their manifests, and how many sit on each Business
    /// Central version.
    ///
    /// <para>Narrowed to the solutions this person may see. Drift is a fact
    /// about somebody's customer repository, and a Private solution's
    /// repository must not be named to a viewer who is not on it - the same
    /// rule repository discovery follows, decided here against the solution
    /// rather than against GitHub, because a solution the person cannot see is
    /// not theirs to bump.</para>
    /// </summary>
    public async Task<DependencyDriftSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        RequireOrganizationId();
        var rows = await _db.GitHubRepositoryDrift.AsNoTracking()
            .OrderBy(d => d.Repository).ThenBy(d => d.Path).ThenBy(d => d.Field)
            .ToListAsync(ct);
        if (rows.Count == 0) return new DependencyDriftSummary([], [], null);

        var visible = await VisibleRepositoryNamesAsync(ct);
        rows = rows.Where(r => visible.Contains(r.Repository)).ToList();
        if (rows.Count == 0) return new DependencyDriftSummary([], [], null);

        var names = (await _catalog.GetAllAsync(ct))
            .GroupBy(w => AppJsonValueEditor.NormaliseId(w.DepId))
            .ToDictionary(g => g.Key, g => g.First().DepName);

        var target = rows
            .Where(r => r.Field == ApplicationField && r.EnvironmentId == null)
            .Select(r => BcArtifactIndex.ToMajorMinor(r.Proposed))
            .OrderByDescending(v => v, BcVersionComparer.Instance)
            .FirstOrDefault();

        // Which environment each finding was measured against: its type always, its name
        // and solution only when that solution is one this viewer may see.
        var environmentIds = rows.Where(r => r.EnvironmentId != null).Select(r => r.EnvironmentId!.Value).Distinct().ToList();
        var environments = new Dictionary<int, (string Type, string? Name, string? Solution)>();
        if (environmentIds.Count > 0)
        {
            var snapshot = await _projectAccess.GetSnapshotAsync(ct);
            var visibleProjects = await _db.OeProjects.AsNoTracking()
                .Where(ProjectAccess.VisibleProjectPredicate(snapshot))
                .Where(p => p.DeletedAt == null)
                .Select(p => p.Id)
                .ToListAsync(ct);
            environments = (await _db.OeProjectEnvironments.AsNoTracking()
                    .Where(e => environmentIds.Contains(e.Id))
                    .Select(e => new { e.Id, e.Name, e.Type, e.ProjectId, Solution = e.Project!.Name })
                    .ToListAsync(ct))
                .ToDictionary(
                    e => e.Id,
                    e => visibleProjects.Contains(e.ProjectId)
                        ? (EnvironmentTypeLabel(e.Type), (string?)e.Name, (string?)e.Solution)
                        : (EnvironmentTypeLabel(e.Type), null, null));
        }

        var repositories = rows
            .GroupBy(r => r.Repository, StringComparer.OrdinalIgnoreCase)
            .Select(byRepo =>
            {
                var application = byRepo.Where(r => r.Field == ApplicationField).ToList();
                var environmentId = byRepo.Select(r => r.EnvironmentId).FirstOrDefault(id => id != null);
                (string Type, string? Name, string? Solution)? environment =
                    environmentId is { } id && environments.TryGetValue(id, out var named) ? named : null;
                return new DependencyDriftRepository(
                    byRepo.Key,
                    $"https://github.com/{byRepo.Key}",
                    application
                        .Select(r => BcArtifactIndex.ToMajorMinor(r.Current))
                        .OrderBy(v => v, BcVersionComparer.Instance)
                        .FirstOrDefault() ?? string.Empty,
                    byRepo
                        .GroupBy(r => r.Path, StringComparer.Ordinal)
                        .Select(byPath => new DependencyDriftFile(
                            byPath.Key,
                            byPath.Select(r => new DependencyDriftChange(
                                r.Field, FieldLabel(r.Field, names), r.Current, r.Proposed)).ToList()))
                        .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    application
                        .Select(r => BcArtifactIndex.ToMajorMinor(r.Proposed))
                        .OrderByDescending(v => v, BcVersionComparer.Instance)
                        .FirstOrDefault() ?? string.Empty,
                    environment?.Type,
                    environment?.Name,
                    environment?.Solution);
            })
            .OrderBy(r => r.Repository, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = repositories
            .Where(r => r.CurrentApplication.Length > 0)
            .GroupBy(r => r.CurrentApplication, StringComparer.Ordinal)
            .Select(g => new DependencyDriftGroup(g.Key, g.Count()))
            .OrderBy(g => g.CurrentApplication, BcVersionComparer.Instance)
            .ToList();

        return new DependencyDriftSummary(repositories, groups, string.IsNullOrEmpty(target) ? null : target);
    }

    /// <summary><c>Production</c> or <c>Sandbox</c>, spelled one way whatever casing Business Central reported.</summary>
    private static string EnvironmentTypeLabel(string type) =>
        BcEnvironmentTypes.IsProduction(type) ? BcEnvironmentTypes.Production : BcEnvironmentTypes.Sandbox;

    /// <summary>The repositories of the solutions this viewer may see, as <c>owner/name</c>.</summary>
    private async Task<HashSet<string>> VisibleRepositoryNamesAsync(CancellationToken ct)
    {
        var snapshot = await _projectAccess.GetSnapshotAsync(ct);
        var urls = await _db.OeProjects.AsNoTracking()
            .Where(ProjectAccess.VisibleProjectPredicate(snapshot))
            .Where(p => p.DeletedAt == null)
            .SelectMany(p => p.Repositories.Where(r => r.Provider == RepositoryProvider.GitHub).Select(r => r.Url))
            .ToListAsync(ct);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls)
        {
            if (ToFullName(url) is { } name) names.Add(name);
        }
        return names;
    }

    /// <summary>What a field is called in front of a person.</summary>
    private static string FieldLabel(string field, IReadOnlyDictionary<string, string> dependencyNames)
    {
        if (field == ApplicationField) return "Business Central application";
        if (field == PlatformField) return "Business Central platform";
        if (!field.StartsWith(DependencyFieldPrefix, StringComparison.Ordinal)) return field;

        var id = field[DependencyFieldPrefix.Length..];
        return dependencyNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : "Dependency " + id;
    }

    // ── The pull requests ────────────────────────────────────────────────

    /// <summary>
    /// Opens (or adds to) one pull request per repository, bumping only the
    /// values the scan found. One repository being refused does not stop the
    /// rest: every repository asked for comes back with either a pull request
    /// or a reason.
    /// </summary>
    public async Task<IReadOnlyList<DependencyDriftPullRequest>> OpenUpdatePullRequestsAsync(
        IReadOnlyList<string> repositories, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var userId = RequireUserId();
        var wanted = repositories
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count == 0) return [];

        var (token, readiness) = await ResolveWriteTokenAsync(userId, ct);
        if (token is null)
        {
            var reason = ReadinessRefusal(readiness);
            return wanted.Select(r => new DependencyDriftPullRequest(r, null, false, 0, reason)).ToList();
        }

        var visible = await VisibleRepositoryNamesAsync(ct);
        var results = new List<DependencyDriftPullRequest>(wanted.Count);
        var rateLimited = false;
        foreach (var name in wanted)
        {
            ct.ThrowIfCancellationRequested();
            if (rateLimited)
            {
                // Every write on this account is refused until GitHub lets up, so the
                // rest are not tried; asking again in a few minutes picks them up.
                results.Add(new DependencyDriftPullRequest(name, null, false, 0, RateLimitedRefusal, IsRateLimited: true));
                continue;
            }
            var result = await TryOpenOneAsync(token, name, visible, automatic: false, ct);
            if (result.PullRequest is { } pullRequest)
            {
                // A version opened by hand replaces an older automatic one just the same.
                rateLimited = await SupersedeOlderAsync(token, result.Repository, pullRequest, ct);
            }
            rateLimited |= result.IsRateLimited;
            results.Add(result);
        }

        _logger.LogInformation(
            "User {UserId} asked for update pull requests on {Asked} repositories; {Opened} came back with one.",
            userId, wanted.Count, results.Count(r => r.PullRequest is not null));
        return results;
    }

    // ── Opening them on their own (issue #1104) ──────────────────────────

    /// <summary>
    /// Opens the update pull request on each of <paramref name="projectId"/>'s GitHub
    /// repositories that the last scan found behind, as the person in scope - the one
    /// who turned automatic update pull requests on. Run by
    /// <see cref="DependencyDriftScheduler"/> after the nightly scan.
    ///
    /// <para>Each Business Central version is offered once per repository: while its
    /// pull request is open, new findings are added to it; once it has been merged or
    /// closed, the version is left alone, even when GitHub deleted the branch with it.
    /// Once a newer version's pull request is open, the older ones the automatic run
    /// opened are closed with a note pointing at it.</para>
    ///
    /// <para>Only findings measured against one of this solution's own environments are
    /// acted on. A repository two solutions share is measured once, against one of their
    /// environments; the other solution's customer may be on a different Business
    /// Central, and a pull request moving them onto it is not this solution's to open.</para>
    ///
    /// <para>Writes are paced (<see cref="WriteSpacing"/>), and the run stops at the first
    /// sign that GitHub is rate limiting the person's account.</para>
    /// </summary>
    public async Task<AutomaticUpdatePullRequests> OpenAutomaticPullRequestsAsync(
        int projectId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var userId = RequireUserId();
        var project = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Select(p => new
            {
                p.Id,
                p.CreatedByUserId,
                p.AutoUpdatePullRequests,
                Urls = p.Repositories.Where(r => r.Provider == RepositoryProvider.GitHub).Select(r => r.Url).ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (project is null || !project.AutoUpdatePullRequests) return new AutomaticUpdatePullRequests(0, null);
        if (!await _projectAccess.CanManageAsync(project.Id, project.CreatedByUserId, ct))
        {
            return new AutomaticUpdatePullRequests(0, AutomaticNoAccessMessage);
        }

        // Keyed without case but carrying the scan's own spelling, which is GitHub's: a
        // solution's URL is however somebody pasted it, and the findings are looked up
        // by the stored name. Only the findings measured against this solution's own
        // environments count (see above).
        var behind = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stored in await _db.GitHubRepositoryDrift.AsNoTracking()
                     .Where(d => d.EnvironmentId != null
                                 && _db.OeProjectEnvironments.Any(e => e.Id == d.EnvironmentId && e.ProjectId == project.Id))
                     .Select(d => d.Repository).Distinct().ToListAsync(ct))
        {
            behind.TryAdd(stored, stored);
        }
        var due = project.Urls
            .Select(ToFullName)
            .Where(n => n is not null && behind.ContainsKey(n))
            .Select(n => behind[n!])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (due.Count == 0) return new AutomaticUpdatePullRequests(0, null);

        var (token, readiness) = await ResolveWriteTokenAsync(userId, ct);
        if (token is null) return new AutomaticUpdatePullRequests(0, AutomaticReadinessRefusal(readiness));

        var visible = await VisibleRepositoryNamesAsync(ct);
        var opened = 0;
        string? blocked = null;
        var rateLimited = false;
        _pacing = true;
        foreach (var name in due)
        {
            ct.ThrowIfCancellationRequested();
            var result = await TryOpenOneAsync(token, name, visible, automatic: true, ct);
            if (result.PullRequest is { } pullRequest)
            {
                if (result.IsNewPullRequest) opened++;
                rateLimited = await SupersedeOlderAsync(token, result.Repository, pullRequest, ct);
            }
            else if (result.IsRateLimited)
            {
                rateLimited = true;
            }
            else if (!result.IsUpToDate)
            {
                // One repository's trouble does not stop the others; the first one is
                // what the solution shows, and tomorrow night tries again.
                blocked ??= $"{name} — {result.Refusal}";
            }

            if (rateLimited)
            {
                // Every further write on this account is refused until GitHub lets up.
                // Stop here rather than spend the rest of the list on refusals; the next
                // night carries on.
                _logger.LogWarning(
                    "GitHub is rate limiting user {UserId}, so the automatic update pull requests of solution {ProjectId} stopped at {RepoFullName}.",
                    userId, projectId, name);
                break;
            }
        }

        _logger.LogInformation(
            "Automatic update pull requests for solution {ProjectId} as user {UserId}: {DueCount} repositories behind, {OpenedCount} pull requests opened.",
            projectId, userId, due.Count, opened);
        return new AutomaticUpdatePullRequests(opened, blocked, rateLimited);
    }

    /// <summary>The pause when the person automatic update pull requests are opened as can no longer manage the solution.</summary>
    public const string AutomaticNoAccessMessage =
        "the person they are opened as can no longer manage this solution.";

    /// <summary>The pause when that person no longer has an active account.</summary>
    public const string AutomaticNoOwnerMessage =
        "the person they are opened as no longer has an active account.";

    /// <summary>
    /// The pause when that person has no GitHub account connected - also what a solution
    /// shows straight away when somebody without one changes its repositories and so
    /// becomes that person (see <c>ProjectService.UpdateProjectAsync</c>).
    /// </summary>
    public const string AutomaticNotLinkedMessage =
        "the person they are opened as has not connected their GitHub account. They connect it on their "
        + "account page under Repository access.";

    /// <summary>What a person pressing the button is told when GitHub is rate limiting their account.</summary>
    private const string RateLimitedRefusal =
        "GitHub is limiting how fast your account can make changes just now. Wait a few minutes, then try again.";

    private static string AutomaticReadinessRefusal(GitHubRepositoryReadiness readiness) => readiness switch
    {
        GitHubRepositoryReadiness.NotConfigured => "GitHub is not set up on this server.",
        GitHubRepositoryReadiness.NotConnected =>
            "your organisation has not connected a GitHub organisation. An administrator connects one under "
            + "Administration → Repositories.",
        _ => AutomaticNotLinkedMessage,
    };

    /// <summary>
    /// Closes the older automatic pull requests on <paramref name="repository"/> that
    /// <paramref name="current"/> replaces, the way Dependabot does: two pull requests
    /// moving the same lines to different versions only conflict with each other. A
    /// pull request a person opened is theirs to close. Returns true when GitHub rate
    /// limited the person's account part-way, which stops the closing too.
    /// </summary>
    private async Task<bool> SupersedeOlderAsync(
        string token, string repository, GitHubPullRequest current, CancellationToken ct)
    {
        var key = repository.ToLowerInvariant();
        var records = await _db.GitHubUpdatePullRequests
            .Where(r => r.Repository == key && r.SupersededAt == null)
            .ToListAsync(ct);
        var replacing = records.FirstOrDefault(r => r.PullRequestNumber == current.Number);
        if (replacing is null) return false;

        var older = records
            .Where(r => r.IsAutomatic && r.PullRequestNumber != current.Number
                        && BcVersionComparer.Instance.Compare(r.Version, replacing.Version) < 0)
            .ToList();
        if (older.Count == 0) return false;

        var (owner, name) = (repository.Split('/')[0], repository.Split('/')[1]);
        foreach (var record in older)
        {
            try
            {
                // One already merged or closed has nothing left to close; it only stops
                // being a candidate.
                if (!await _github.IsPullRequestOpenAsync(token, owner, name, record.PullRequestNumber, ct))
                {
                    record.SupersededAt = _clock.GetUtcNow().UtcDateTime;
                    continue;
                }
                await PaceWriteAsync(ct);
                await _github.ClosePullRequestAsync(token, owner, name, record.PullRequestNumber,
                    $"Superseded by #{current.Number}, which targets Business Central {replacing.Version}.\n\n"
                    + "Closed by AL Workbench.", ct);
                record.SupersededAt = _clock.GetUtcNow().UtcDateTime;
            }
            catch (Exception ex) when (ex is GitHubApiException or HttpRequestException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                _logger.LogWarning(ex,
                    "Could not close pull request #{PullRequestNumber} on {RepoFullName}, which #{Replacement} replaces; it stays open.",
                    record.PullRequestNumber, repository, current.Number);
                if (ex is GitHubApiException { IsRateLimited: true })
                {
                    await _db.SaveChangesAsync(ct);
                    return true;
                }
            }
        }
        await _db.SaveChangesAsync(ct);
        return false;
    }

    /// <summary>
    /// Waits until <see cref="WriteSpacing"/> has passed since the last write, when the
    /// run is pacing itself. Called before every request that creates something on GitHub.
    /// </summary>
    private async Task PaceWriteAsync(CancellationToken ct)
    {
        if (!_pacing) return;
        if (_lastWriteAt is { } last)
        {
            var wait = WriteSpacing - (_clock.GetUtcNow() - last);
            if (wait > TimeSpan.Zero) await PauseAsync(wait, ct);
        }
        _lastWriteAt = _clock.GetUtcNow();
    }

    /// <summary>
    /// The Business Central version a repository's findings move it to, as
    /// <c>major.minor</c>. Without an application finding (only the platform or a
    /// dependency moved), it is the version the repository was measured against.
    /// </summary>
    private async Task<string> TargetVersionAsync(
        IReadOnlyList<GitHubRepositoryDrift> rows, OeRelease? release, CancellationToken ct)
    {
        var environmentId = rows.Select(r => r.EnvironmentId).FirstOrDefault(id => id != null);
        return BcArtifactIndex.ToMajorMinor(
            rows.Where(r => r.Field == ApplicationField).Select(r => r.Proposed).FirstOrDefault()
            ?? (environmentId is { } measuredId ? await EnvironmentVersionAsync(measuredId, ct) : null)
            ?? release?.BcVersion
            ?? "0.0");
    }

    /// <summary>Whether the workbench has opened an update pull request for <paramref name="version"/> on this repository before.</summary>
    private Task<bool> WasOfferedAsync(string repository, string version, CancellationToken ct)
    {
        var key = repository.ToLowerInvariant();
        return _db.GitHubUpdatePullRequests.AsNoTracking()
            .AnyAsync(r => r.Repository == key && r.Version == version, ct);
    }

    /// <summary>
    /// Remembers an update pull request the workbench opened or joined, once per pull
    /// request. Losing the race to another run saving the same one is harmless.
    /// </summary>
    private async Task RecordPullRequestAsync(
        string repository, string version, GitHubPullRequest pullRequest, bool automatic, CancellationToken ct)
    {
        var key = repository.ToLowerInvariant();
        if (await _db.GitHubUpdatePullRequests.AsNoTracking()
                .AnyAsync(r => r.Repository == key && r.PullRequestNumber == pullRequest.Number, ct))
        {
            return;
        }

        var record = new GitHubUpdatePullRequest
        {
            OrganizationId = RequireOrganizationId(),
            Repository = key,
            Version = version,
            PullRequestNumber = pullRequest.Number,
            HtmlUrl = pullRequest.HtmlUrl,
            IsAutomatic = automatic,
            OpenedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.GitHubUpdatePullRequests.Add(record);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            _db.Entry(record).State = EntityState.Detached;
            _logger.LogInformation(ex,
                "Pull request #{PullRequestNumber} on {RepoFullName} was already recorded.", pullRequest.Number, repository);
        }
    }

    /// <summary>
    /// The acting person's GitHub token, or why there is none. Why-not before what, as
    /// everywhere else: "that is not a repository we can offer you" is a poor way to
    /// say "connect your GitHub account".
    /// </summary>
    private async Task<(string? Token, GitHubRepositoryReadiness Readiness)> ResolveWriteTokenAsync(
        int userId, CancellationToken ct)
    {
        var access = await _repositories.GetAccessAsync(ct);
        if (!access.IsReady) return (null, access.Readiness);
        var token = await _access.ResolveUserTokenAsync(userId, ct);
        return token is null ? (null, GitHubRepositoryReadiness.NotLinked) : (token, access.Readiness);
    }

    /// <summary><see cref="OpenOneAsync"/>, with every refusal turned into an answer rather than an exception.</summary>
    private async Task<DependencyDriftPullRequest> TryOpenOneAsync(
        string token, string name, IReadOnlySet<string> visible, bool automatic, CancellationToken ct)
    {
        try
        {
            return await OpenOneAsync(token, name, visible, automatic, ct);
        }
        catch (PlanValidationException ex)
        {
            return new DependencyDriftPullRequest(
                name, null, false, 0, ex.Errors.Values.FirstOrDefault() ?? "The pull request was refused.");
        }
        catch (GitHubApiException ex) when (ex.IsRateLimited)
        {
            _logger.LogWarning(ex, "GitHub rate limited the update pull request on {RepoFullName}.", name);
            return new DependencyDriftPullRequest(name, null, false, 0, RateLimitedRefusal, IsRateLimited: true);
        }
        catch (Exception ex) when (ex is GitHubApiException or GitHubAppNotConfiguredException or HttpRequestException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not open the update pull request on {RepoFullName}.", name);
            return new DependencyDriftPullRequest(
                name, null, false, 0,
                "GitHub would not take the change to this repository just now. Try again in a moment.");
        }
    }

    private async Task<DependencyDriftPullRequest> OpenOneAsync(
        string token, string fullName, IReadOnlySet<string> visible, bool automatic, CancellationToken ct)
    {
        if (!visible.Contains(fullName))
        {
            return new DependencyDriftPullRequest(fullName, null, false, 0,
                "This repository is not one of your solutions' repositories. Refresh the page and try again.");
        }

        var rows = await _db.GitHubRepositoryDrift.AsNoTracking()
            .Where(d => d.Repository == fullName)
            .OrderBy(d => d.Path).ThenBy(d => d.Field)
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return new DependencyDriftPullRequest(fullName, null, false, 0,
                "There is nothing left to change in this repository. Check again to see where it stands now.",
                IsUpToDate: true);
        }

        var repo = await _repositories.ResolveAsync(fullName, ct);
        if (repo is null)
        {
            return new DependencyDriftPullRequest(fullName, null, false, 0,
                "That repository is not one the workbench can offer you. Ask an owner of your GitHub organisation "
                + "to give you access to it.");
        }

        var release = await _db.OeReleases.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rows[0].ReleaseId, ct);
        var environmentId = rows.Select(r => r.EnvironmentId).FirstOrDefault(id => id != null);
        // Named only when its solution is one this person may see: the body goes to
        // GitHub, and a Private solution's environment is not theirs to publish.
        var environment = environmentId is { } envId
            ? await EnvironmentForBodyAsync(envId, ct)
            : null;
        var version = await TargetVersionAsync(rows, release, ct);

        var target = await ChooseBranchAsync(token, repo, version, ct);
        // The automatic run offers each version once (issue #1104): an open pull
        // request is added to, but one somebody merged or closed is their answer, and
        // its branch is usually gone with it - or kept, which is why this asks the
        // record rather than the branch. A branch with no open pull request and no
        // record is a run that failed before it opened one; it was stepped past like
        // any other, so one failed night does not strand the version.
        if (automatic && target.ExistingPullRequest is null && await WasOfferedAsync(fullName, version, ct))
        {
            return new DependencyDriftPullRequest(fullName, null, false, 0,
                $"An update pull request for Business Central {version} was already opened here and closed.",
                IsUpToDate: true);
        }

        var blobs = new List<(string Path, string BlobSha)>();
        var edited = new List<(string Path, IReadOnlyList<GitHubRepositoryDrift> Changes)>();
        foreach (var byPath in rows.GroupBy(r => r.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var file = await _github.GetFileAsync(token, repo.Owner, repo.Name, byPath.Key, target.ParentSha, ct);
            if (file is null)
            {
                _logger.LogInformation(
                    "{Path} is no longer in {RepoFullName}, so nothing about it goes in the pull request.",
                    byPath.Key, repo.FullName);
                continue;
            }

            var applied = ApplyChanges(file.Text, byPath.ToList(), repo.FullName, byPath.Key, out var moved);
            if (moved.Count == 0) continue;

            await PaceWriteAsync(ct);
            blobs.Add((byPath.Key, await _github.CreateBlobAsync(
                token, repo.Owner, repo.Name, Encoding.UTF8.GetBytes(applied), ct)));
            edited.Add((byPath.Key, moved));
        }

        if (blobs.Count == 0)
        {
            return new DependencyDriftPullRequest(fullName, null, false, 0,
                "Every value the workbench would have changed here is already up to date. Check again to refresh "
                + "what it knows.", IsUpToDate: true);
        }

        var baseTree = await _github.GetCommitTreeShaAsync(token, repo.Owner, repo.Name, target.ParentSha, ct);
        await PaceWriteAsync(ct);
        var tree = await _github.CreateTreeAsync(token, repo.Owner, repo.Name, baseTree, blobs, ct);
        await PaceWriteAsync(ct);
        var commit = await _github.CreateCommitAsync(
            token, repo.Owner, repo.Name, $"Target Business Central {version}", tree, target.ParentSha, ct: ct);

        await PaceWriteAsync(ct);
        if (target.ExistingPullRequest is null)
        {
            if (!await _github.CreateBranchAsync(token, repo.Owner, repo.Name, target.Branch, commit, ct))
            {
                throw Refuse(
                    $"Branch {target.Branch} appeared on '{repo.FullName}' while this was being prepared. "
                    + "Try again and the workbench will pick the next name.");
            }
        }
        else if (!await _github.UpdateBranchAsync(token, repo.Owner, repo.Name, target.Branch, commit, ct))
        {
            throw Refuse(
                $"Branch {target.Branch} on '{repo.FullName}' moved on while this was being prepared, so nothing "
                + "was committed. Try again to build on what is there now.");
        }

        if (target.ExistingPullRequest is null) await PaceWriteAsync(ct);
        var pullRequest = target.ExistingPullRequest ?? await _github.CreatePullRequestAsync(
            token, repo.Owner, repo.Name,
            title: $"Target Business Central {version}",
            head: target.Branch,
            baseBranch: repo.DefaultBranch,
            body: await BuildBodyAsync(version, edited, release, environment, ct),
            ct);

        await RecordPullRequestAsync(
            repo.FullName, version, pullRequest, automatic && target.ExistingPullRequest is null, ct);

        _logger.LogInformation(
            "Bumped {FileCount} manifest(s) in {RepoFullName} to Business Central {Version} on {Branch} as pull "
            + "request #{PullRequestNumber} ({PullRequestState}, {Trigger}).",
            edited.Count, repo.FullName, version, target.Branch, pullRequest.Number,
            target.ExistingPullRequest is null ? "opened" : "already open", automatic ? "automatic" : "by hand");

        return new DependencyDriftPullRequest(
            repo.FullName, pullRequest, target.ExistingPullRequest is null, edited.Count, null);
    }

    /// <summary>
    /// Applies one manifest's findings to its text, value by value. A value the
    /// repository has already moved past is skipped rather than pushed back -
    /// the scan's picture can be a day old, and the file is the truth.
    /// </summary>
    private string ApplyChanges(
        string text,
        IReadOnlyList<GitHubRepositoryDrift> changes,
        string repoFullName,
        string path,
        out IReadOnlyList<GitHubRepositoryDrift> moved)
    {
        var applied = new List<GitHubRepositoryDrift>();
        var current = text;

        foreach (var change in changes)
        {
            var manifest = AppJsonManifestParser.Parse(current);
            if (manifest is null) break;

            var (isBehind, edit) = Edit(manifest, change, current);
            if (!isBehind) continue;

            if (edit is null)
            {
                _logger.LogWarning(
                    "Could not edit {Field} in {Path} of {RepoFullName} in place; the whole manifest is rewritten, "
                    + "so its formatting changes.", change.Field, path, repoFullName);
                edit = Rewrite(current, change);
                if (edit is null) continue;
            }

            current = edit;
            applied.Add(change);
        }

        moved = applied;
        return current;
    }

    /// <summary>
    /// Whether this finding still applies to the manifest as it stands, and the
    /// text with it applied - null when the targeted edit could not be made.
    /// </summary>
    private static (bool IsBehind, string? Edited) Edit(
        AppJsonManifest manifest, GitHubRepositoryDrift change, string text)
    {
        if (change.Field == ApplicationField)
        {
            return manifest.Application is { } value && BcVersionComparer.Instance.Compare(value, change.Proposed) < 0
                ? (true, AppJsonValueEditor.ReplaceRootProperty(text, ApplicationField, change.Proposed))
                : (false, null);
        }
        if (change.Field == PlatformField)
        {
            return manifest.Platform is { } value && BcVersionComparer.Instance.Compare(value, change.Proposed) < 0
                ? (true, AppJsonValueEditor.ReplaceRootProperty(text, PlatformField, change.Proposed))
                : (false, null);
        }
        if (!change.Field.StartsWith(DependencyFieldPrefix, StringComparison.Ordinal)) return (false, null);

        var id = change.Field[DependencyFieldPrefix.Length..];
        var dependency = manifest.Dependencies.FirstOrDefault(d => AppJsonValueEditor.NormaliseId(d.Id) == id);
        return dependency?.Version is { } depVersion
            && BcVersionComparer.Instance.Compare(depVersion, change.Proposed) < 0
                ? (true, AppJsonValueEditor.ReplaceDependencyVersion(text, id, change.Proposed))
                : (false, null);
    }

    /// <summary>The fallback when the text could not be edited in place: parse, set, write back indented.</summary>
    private static string? Rewrite(string text, GitHubRepositoryDrift change) =>
        AppJsonValueEditor.RewriteWholeDocument(text, root =>
        {
            if (change.Field is ApplicationField or PlatformField)
            {
                root[change.Field] = change.Proposed;
                return;
            }

            var id = change.Field[DependencyFieldPrefix.Length..];
            if (root["dependencies"] is not System.Text.Json.Nodes.JsonArray dependencies) return;
            foreach (var entry in dependencies)
            {
                if (entry is not System.Text.Json.Nodes.JsonObject dependency) continue;
                var entryId = dependency["id"]?.GetValue<string>() ?? dependency["appId"]?.GetValue<string>();
                if (AppJsonValueEditor.NormaliseId(entryId) != id) continue;
                dependency["version"] = change.Proposed;
                return;
            }
        });

    /// <summary>Where the commit is going: which branch, what it sits on, and the pull request it joins if there is one.</summary>
    private sealed record BranchTarget(string Branch, string ParentSha, GitHubPullRequest? ExistingPullRequest);

    /// <summary>
    /// Picks the branch this bump belongs on, walking
    /// <c>aldt/bump-bc-&lt;major.minor&gt;</c>, <c>-2</c>, <c>-3</c> until one
    /// has a pull request still open (join it, so a second run lands in the
    /// review already running) or does not exist at all (start it from the
    /// default branch). A branch with no open pull request - merged, closed, or never
    /// opened because a run failed half-way - is stepped past rather than reused, so an
    /// open <c>-2</c> is still found behind it. Whether the version should be offered at
    /// all is the caller's question, answered from the record of what was opened.
    /// </summary>
    private async Task<BranchTarget> ChooseBranchAsync(
        string token, GitHubRepositorySummary repo, string version, CancellationToken ct)
    {
        var baseName = BranchPrefix + version;
        for (var attempt = 1; attempt <= MaxBranchAttempts; attempt++)
        {
            var branch = attempt == 1 ? baseName : $"{baseName}-{attempt}";

            var open = await _github.FindOpenPullRequestAsync(token, repo.Owner, repo.Name, branch, ct);
            if (open is not null)
            {
                var head = await _github.GetBranchHeadShaAsync(token, repo.Owner, repo.Name, branch, ct)
                    ?? throw Refuse(
                        $"Pull request #{open.Number} on '{repo.FullName}' says it comes from {branch}, but that "
                        + "branch is gone. Close the pull request on GitHub, then try again.");
                return new BranchTarget(branch, head, open);
            }

            if (await _github.GetBranchHeadShaAsync(token, repo.Owner, repo.Name, branch, ct) is null)
            {
                var defaultHead = await _github.GetBranchHeadShaAsync(
                    token, repo.Owner, repo.Name, repo.DefaultBranch, ct)
                    ?? throw Refuse(
                        $"'{repo.FullName}' has no commits on {repo.DefaultBranch} yet, so there is nothing to open "
                        + "a pull request against.");
                return new BranchTarget(branch, defaultHead, null);
            }

            _logger.LogInformation(
                "Branch {Branch} on {RepoFullName} exists with no open pull request; trying the next name.",
                branch, repo.FullName);
        }

        throw Refuse(
            $"'{repo.FullName}' already has branches named {baseName} through {baseName}-{MaxBranchAttempts}, and "
            + "none of them has a pull request open. Tidy those up on GitHub, then try again.");
    }

    /// <summary>
    /// Whether an environment is a production one, and its name when its solution is one
    /// this viewer may see; null when the environment is gone.
    /// </summary>
    private async Task<(bool IsProduction, string? Name)?> EnvironmentForBodyAsync(int environmentId, CancellationToken ct)
    {
        var snapshot = await _projectAccess.GetSnapshotAsync(ct);
        var visibleProjects = _db.OeProjects.AsNoTracking()
            .Where(ProjectAccess.VisibleProjectPredicate(snapshot))
            .Where(p => p.DeletedAt == null)
            .Select(p => p.Id);
        var row = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId)
            .Select(e => new { e.Name, e.Type, Visible = visibleProjects.Contains(e.ProjectId) })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (BcEnvironmentTypes.IsProduction(row.Type), row.Visible ? row.Name : null);
    }

    /// <summary>An environment's version, when it still has one worth comparing with.</summary>
    private async Task<string?> EnvironmentVersionAsync(int environmentId, CancellationToken ct)
    {
        var version = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId)
            .Select(e => e.Version)
            .FirstOrDefaultAsync(ct);
        return IsKnownVersion(version) ? version : null;
    }

    /// <summary>
    /// The pull request's description: every value that moved, per file, and a
    /// link to what changed between the two Business Central releases so a
    /// reviewer can see what they are moving onto.
    /// </summary>
    private async Task<string> BuildBodyAsync(
        string version,
        IReadOnlyList<(string Path, IReadOnlyList<GitHubRepositoryDrift> Changes)> edited,
        OeRelease? release,
        (bool IsProduction, string? Name)? environment,
        CancellationToken ct)
    {
        var names = (await _catalog.GetAllAsync(ct))
            .GroupBy(w => AppJsonValueEditor.NormaliseId(w.DepId))
            .ToDictionary(g => g.Key, g => g.First().DepName);

        var lines = new List<string>
        {
            $"Brings this repository's manifests up to Business Central {version}.",
        };

        foreach (var (path, changes) in edited)
        {
            var body = new StringBuilder($"**{path}**\n");
            foreach (var change in changes)
            {
                body.Append($"\n- {FieldLabel(change.Field, names)}: `{change.Current}` → `{change.Proposed}`");
            }
            lines.Add(body.ToString());
        }

        if (environment is { } measured)
        {
            var kind = measured.IsProduction ? "production environment" : "sandbox (the solution has no production environment)";
            lines.Add(measured.Name is not null
                ? $"Measured against the {kind} {measured.Name}, which was on Business Central {version} when checked."
                : $"Measured against the solution's {kind}, which was on Business Central {version} when checked.");
        }

        // The compare link is only worth giving when it lands on the version this pull
        // request moves to; an environment can be on a wave other than the
        // newest imported release.
        if (release?.BcVersion is { } releaseVersion
            && BcArtifactIndex.ToMajorMinor(releaseVersion) == version
            && await CompareLinkAsync(release, ct) is { } link)
        {
            lines.Add($"What changed between the two releases: {link}");
        }

        lines.Add("Only the values listed above were changed; the rest of each file is untouched.");
        lines.Add("Sent from AL Workbench.");
        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// The release-compare link for the release this bump targets, or null when
    /// there is no earlier release to compare it with - or when the deployment
    /// has not been told its own public address, in which case a link would
    /// point at nothing.
    /// </summary>
    private async Task<string?> CompareLinkAsync(OeRelease release, CancellationToken ct)
    {
        if (_publicOrigin.Configured is not { } origin) return null;
        if (await PreviousReleaseIdAsync(release, ct) is not { } previous) return null;
        return origin + _links.ReleaseCompare(previous, release.Id);
    }

    /// <summary>
    /// The newest first-party release before this one in the same family - the
    /// same localisation, matched on the dedup key's country when the release
    /// has one, because comparing a Danish release with a worldwide one shows
    /// differences nobody made.
    /// </summary>
    private async Task<int?> PreviousReleaseIdAsync(OeRelease release, CancellationToken ct)
    {
        var family = DedupFamily(release.DedupKey);
        var candidates = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Kind == "first_party" && !r.IsPrerelease && r.Id != release.Id && r.BcVersion != null)
            .Select(r => new { r.Id, r.BcVersion, r.DedupKey })
            .ToListAsync(ct);

        return candidates
            .Where(r => family is null || DedupFamily(r.DedupKey) == family)
            .Where(r => BcVersionComparer.Instance.Compare(r.BcVersion, release.BcVersion) < 0)
            .OrderByDescending(r => r.BcVersion, BcVersionComparer.Instance)
            .ThenByDescending(r => r.Id)
            .Select(r => (int?)r.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// The part of a dedup key that says which stream a release belongs to -
    /// <c>bc-onprem:28.2:dk</c> is in the same family as <c>bc-onprem:27.5:dk</c>
    /// but not as the <c>w1</c> one. Null when the release was not deduped, and
    /// then any earlier first-party release will do.
    /// </summary>
    private static string? DedupFamily(string? dedupKey)
    {
        var parts = (dedupKey ?? string.Empty).Split(':');
        return parts.Length >= 3 ? $"{parts[0]}:{parts[^1]}".ToLowerInvariant() : null;
    }

    private static string ReadinessRefusal(GitHubRepositoryReadiness readiness) => readiness switch
    {
        GitHubRepositoryReadiness.NotConfigured =>
            "GitHub is not set up on this server yet, so a pull request cannot be opened. Ask whoever runs "
            + "AL Workbench to set it up.",
        GitHubRepositoryReadiness.NotConnected =>
            "Your organisation has not connected a GitHub organisation yet, so there is nowhere to open a pull "
            + "request. An administrator connects one under Administration → Repositories.",
        GitHubRepositoryReadiness.LinkNeedsRepair =>
            "Your GitHub account is no longer connected to the workbench. Connect it again on your account page "
            + "under Repository access, then try this again.",
        _ =>
            "Connect your own GitHub account first, on your account page under Repository access. The pull request "
            + "is opened in your name, so the workbench needs your GitHub account to do it.",
    };

    private static PlanValidationException Refuse(string message) =>
        new(new Dictionary<string, string> { ["GitHubRepository"] = message });

    private static string Trim(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max];
    }
}
