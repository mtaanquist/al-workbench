using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Import;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Supplies a build's dependencies on the organisation's own extensions from the
/// <c>.app</c>s its earlier builds retained: when solution A depends on a PTE that
/// solution B's pipeline builds, B's newest successful build already holds the
/// symbols, so resolving them is a query, not an integration (#901, Part 3). Runs
/// after the public symbol feeds and before the stored uploads, so a feed package
/// wins over an artifact and an upload wins over both. See
/// <c>.design/object-explorer-project-builds.md</c>, "Resolve symbols".
///
/// <para>
/// <b>Which builds are candidates.</b> The build runs in a worker with an
/// organisation scope and no person behind it, so <see cref="ProjectAccess"/> has
/// nobody to answer for. The rule is therefore static: an artifact is a candidate
/// only when its solution is <see cref="ProjectVisibility.Public"/> or
/// <see cref="ProjectVisibility.ReadOnly"/> - everyone in the organisation may read
/// those anyway - or is the solution being built. A Private solution's builds
/// never resolve another solution's dependency. Everything is read through the
/// organisation's query filter; nothing here crosses it.
/// </para>
///
/// <para>
/// Of the candidates, the highest version at or above the <c>app.json</c> floor
/// wins (newest build breaking a tie), from a <c>ready</c> build that was not a
/// pull-request check - a pull request's apps are unreviewed - nor a preview build,
/// whose apps were compiled against symbols no shipped Business Central has, and was
/// not compiled against a newer Business Central than this build targets. A resolved app's own
/// dependencies are followed the same way, and the ones no artifact carries are
/// handed back so the caller can give the feeds a second look at them.
/// </para>
/// </summary>
internal sealed class BuildArtifactSymbolResolver
{
    private readonly AppDbContext _db;
    private readonly ILogger _logger;

    public BuildArtifactSymbolResolver(AppDbContext db, ILogger logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Copies an artifact into <see cref="BuildArtifactSymbolRequest.TargetDirectory"/>
    /// for every wanted app the directory does not already hold at a version that
    /// fits, and walks the copied apps' own dependencies. Never throws except on
    /// cancellation or a database failure.
    /// </summary>
    public async Task<BuildArtifactSymbolOutcome> ResolveAsync(BuildArtifactSymbolRequest request, CancellationToken ct)
    {
        var resolved = new List<ResolvedBuildArtifact>();
        var transitiveMisses = new List<SymbolDependency>();
        var present = AlSymbolFeedResolver.ScanPresent(request.TargetDirectory);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var target = AlSymbolFeedResolver.ParseVersion(request.ApplicationVersion);

        var queue = new Queue<(SymbolDependency Dependency, bool Transitive)>(
            request.Dependencies.Select(d => (d, false)));
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dependency, transitive) = queue.Dequeue();
            var appId = AlSymbolFeedResolver.NormalizeId(dependency.AppId);
            if (appId.Length == 0 || request.ProvidedAppIds.Contains(appId) || !visited.Add(appId)) continue;

            var floor = AlSymbolFeedResolver.ParseVersion(dependency.MinVersion);
            if (present.TryGetValue(appId, out var have) && (floor is null || (have is not null && have >= floor))) continue;

            var hit = await FindAsync(request.ProjectId, appId, floor, target, ct).ConfigureAwait(false);
            if (hit is null)
            {
                if (transitive) transitiveMisses.Add(dependency with { AppId = appId });
                continue;
            }

            var content = await _db.OeProjectBuildArtifacts.AsNoTracking()
                .Where(a => a.Id == hit.ArtifactId)
                .Select(a => a.Content)
                .FirstAsync(ct).ConfigureAwait(false);
            // Only a plain file name is ever written: the row's name came from a
            // compiler or a GitHub Release asset, and neither is trusted as a path.
            var fileName = Path.GetFileName(hit.FileName);
            var dest = Path.Combine(request.TargetDirectory, fileName);
            if (!File.Exists(dest)) await File.WriteAllBytesAsync(dest, content, ct).ConfigureAwait(false);
            present[appId] = hit.Version;
            resolved.Add(new ResolvedBuildArtifact(appId, hit.AppName, hit.AppVersion, hit.ProjectId, hit.ProjectName, hit.BuildId, fileName));
            _logger.LogInformation("Resolved symbols for {App} {Version} from build {BuildId} of solution {Project}.",
                hit.AppName, hit.AppVersion, hit.BuildId, hit.ProjectName);

            using var stream = new MemoryStream(content, writable: false);
            foreach (var next in AppPackageReader.TryReadManifest(stream)?.Dependencies ?? [])
            {
                queue.Enqueue((new SymbolDependency(next.AppId.ToString(), next.Name, next.Version), true));
            }
        }

        return new BuildArtifactSymbolOutcome(resolved, transitiveMisses);
    }

    /// <summary>The best retained artifact for <paramref name="appId"/> under the rules in the class summary, or null.</summary>
    private async Task<Candidate?> FindAsync(int projectId, string appId, Version? floor, Version? target, CancellationToken ct)
    {
        var rows = await _db.OeProjectBuildArtifacts.AsNoTracking()
            // A carried row (#1094) is a copy of an earlier build's .app; the original
            // row says which Business Central version it was really built on.
            .Where(a => a.AppId == appId && a.CarriedFromBuildId == null
                && a.ProjectBuild!.Status == ProjectBuildStatus.Ready
                && a.ProjectBuild.Trigger != ProjectBuildTrigger.PullRequest
                && a.ProjectBuild.BcTarget == ProjectBuildTarget.Current
                && a.ProjectBuild.Project!.DeletedAt == null
                && (a.ProjectBuild.ProjectId == projectId || a.ProjectBuild.Project.Visibility != ProjectVisibility.Private))
            .Select(a => new
            {
                a.Id,
                a.FileName,
                a.AppName,
                a.AppVersion,
                BuildId = a.ProjectBuildId,
                a.ProjectBuild!.BcVersion,
                a.ProjectBuild.FinishedAt,
                a.ProjectBuild.ProjectId,
                ProjectName = a.ProjectBuild.Project!.Name,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Select(r => new Candidate(r.Id, r.FileName, r.AppName, r.AppVersion, AlSymbolFeedResolver.ParseVersion(r.AppVersion),
                r.BuildId, r.ProjectId, r.ProjectName, AlSymbolFeedResolver.ParseVersion(r.BcVersion), r.FinishedAt))
            .Where(c => c.Version is not null && (floor is null || c.Version >= floor))
            // Built against a newer Business Central than this build: its own
            // dependency on the base app would not be met by this build's symbols.
            .Where(c => target is null || c.BuiltOn is null || c.BuiltOn <= target)
            .OrderByDescending(c => c.Version)
            .ThenByDescending(c => c.FinishedAt)
            .ThenByDescending(c => c.BuildId)
            .FirstOrDefault();
    }

    private sealed record Candidate(
        int ArtifactId, string FileName, string AppName, string AppVersion, Version? Version,
        int BuildId, int ProjectId, string ProjectName, Version? BuiltOn, DateTime? FinishedAt);
}

/// <summary>What a build asks its earlier builds for. See <see cref="BuildArtifactSymbolResolver.ResolveAsync"/>.</summary>
/// <param name="ProjectId">The solution being built; its own earlier builds are candidates whatever its visibility.</param>
/// <param name="Dependencies">The apps still wanted, each with the lowest version accepted.</param>
/// <param name="TargetDirectory">The build's shared package cache.</param>
/// <param name="ProvidedAppIds">Lower-cased app ids something else supplies (the apps this build compiles, stored uploads); never taken from an artifact.</param>
/// <param name="ApplicationVersion">The Business Central Major.Minor the build compiles against.</param>
internal sealed record BuildArtifactSymbolRequest(
    int ProjectId,
    IReadOnlyList<SymbolDependency> Dependencies,
    string TargetDirectory,
    IReadOnlySet<string> ProvidedAppIds,
    string? ApplicationVersion);

/// <summary>One artifact copied into the package cache, and the build it came from.</summary>
internal sealed record ResolvedBuildArtifact(string AppId, string AppName, string AppVersion, int ProjectId, string ProjectName, int BuildId, string FileName);

/// <summary>
/// The artifacts copied, and the dependencies of those artifacts that no artifact
/// carried - the caller hands these to the feeds, which have not seen them yet.
/// </summary>
internal sealed record BuildArtifactSymbolOutcome(IReadOnlyList<ResolvedBuildArtifact> Resolved, IReadOnlyList<SymbolDependency> TransitiveMisses);
