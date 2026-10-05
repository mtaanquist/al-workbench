using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.ObjectExplorer.Import;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// The project-build pipeline core. For one project it clones each repository,
/// discovers every extension's <c>app.json</c>, resolves and downloads the
/// matching Microsoft symbols (auto-importing the parent BC release inline when
/// the catalogue lacks it), compiles each extension with <c>alc</c> in dependency
/// order (source embedded), and returns the compiled <c>.app</c>s as
/// <see cref="AppFileUpload"/>s ready for the existing ingest seam plus a per-app
/// <see cref="OeProjectBuildResult"/> report. Partial failures are isolated — one
/// repo or extension that can't be cloned/compiled fails only itself. See
/// <c>.design/object-explorer-project-builds.md</c>.
///
/// <para>
/// Run by <see cref="ReleaseImportWorker"/> inside the submitter's org scope. The
/// IO (git clone, artifact download, <c>alc</c>) sits behind
/// <see cref="IProcessRunner"/> / <see cref="BcArtifactService"/> /
/// <see cref="AlCompilerProvisioner"/> so the orchestration and the pure helpers
/// (manifest parse, app discovery, dependency ordering) are unit-testable. Every
/// transient artefact lives under one temp build root deleted in
/// <c>finally</c>; the compiled <c>.app</c> bytes are buffered into memory so the
/// returned uploads outlive the root.
/// </para>
/// </summary>
public sealed class ProjectBuildService
{
    /// <summary>Temp-dir prefix for a build root, mirroring the <c>oe-artifact-</c> / <c>oe-dvd-</c> convention.</summary>
    public const string TempPrefix = "oe-build-";

    /// <summary>
    /// Hard ceiling for a discovery clone — a stalled remote becomes a logged
    /// failure, not a hang. Discovery fetches only trees + app.json blobs (no
    /// working-tree checkout), so this is ample even for repos whose history is
    /// bloated by committed binaries.
    /// </summary>
    private static readonly TimeSpan DiscoveryCloneTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Default build-clone ceiling; generous because a build clones the full working tree, which can be gigabytes when <c>.alpackages</c> binaries are committed. Override via <c>OE_BUILD_CLONE_TIMEOUT_MINUTES</c>.</summary>
    private const int DefaultBuildCloneTimeoutMinutes = 30;

    /// <summary>The build-clone ceiling (env-overridable). git's low-speed abort (~60s) catches genuine stalls; this only backstops a stuck process.</summary>
    private static TimeSpan BuildCloneTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("OE_BUILD_CLONE_TIMEOUT_MINUTES");
        return int.TryParse(raw, out var m) && m > 0 ? TimeSpan.FromMinutes(m) : TimeSpan.FromMinutes(DefaultBuildCloneTimeoutMinutes);
    }

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly BcArtifactService _artifacts;
    private readonly ReleaseImportService _importer;
    private readonly AlCompilerProvisioner _compiler;
    private readonly AlSymbolFeedResolver _symbolFeeds;
    private readonly CloneCredentialResolver _credentials;
    private readonly IProcessRunner _processRunner;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectBuildService> _logger;

    public ProjectBuildService(
        AppDbContext db,
        IOrganizationContext orgContext,
        BcArtifactService artifacts,
        ReleaseImportService importer,
        AlCompilerProvisioner compiler,
        AlSymbolFeedResolver symbolFeeds,
        CloneCredentialResolver credentials,
        IProcessRunner processRunner,
        TimeProvider clock,
        ILogger<ProjectBuildService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _artifacts = artifacts;
        _importer = importer;
        _compiler = compiler;
        _symbolFeeds = symbolFeeds;
        _credentials = credentials;
        _processRunner = processRunner;
        _clock = clock;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; ProjectBuildService called outside an authenticated request.");

    /// <summary>
    /// Builds <paramref name="projectId"/> into the already-created ingesting
    /// Release <paramref name="releaseId"/>: clone → discover → resolve symbols →
    /// compile. Finalises the Release's label and parent pointer, then returns the
    /// compiled uploads and the per-app report. Throws only on whole-build failures
    /// (project gone, compiler unavailable, no apps found, symbols unresolvable) —
    /// per-app problems come back as <c>failed</c> results, not exceptions.
    /// </summary>
    public async Task<ProjectBuildOutcome> BuildAsync(
        int projectId, int releaseId, ProjectBuildOptions? options = null, CancellationToken ct = default)
    {
        options ??= ProjectBuildOptions.Manual;
        RequireOrganizationId();
        var project = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId && c.DeletedAt == null)
            .Include(c => c.Repositories)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project {projectId} not found for build.");

        // The first-class build row the importer created and linked to this
        // release. The clone/changelog/log/artifact provenance hangs off it. Null
        // only for a release without a ProjectBuild (legacy / synthetic) — the new
        // persistence then no-ops, leaving the old per-app report as the record.
        var build = await _db.OeProjectBuilds
            .FirstOrDefaultAsync(b => b.ReleaseId == releaseId, ct).ConfigureAwait(false);
        if (build is not null)
        {
            build.Status = ProjectBuildStatus.Building;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // The build row carries the target it was started with (so a restart-resumed
        // job builds the same thing). An explicit target in the options wins, which is
        // how a caller with no pipeline asks for one.
        if (options.Target == BcBuildTarget.Current && build is not null)
        {
            options = options with { Target = ProjectBuildTarget.ToBuildTarget(build.BcTarget) };
        }
        else if (build is not null && ProjectBuildTarget.ToBuildTarget(build.BcTarget) != options.Target)
        {
            // Every guard (publishing, deploying, symbols) reads the row, so the row has
            // to say what was actually built.
            build.BcTarget = options.Target switch
            {
                BcBuildTarget.NextMinor => ProjectBuildTarget.NextMinor,
                BcBuildTarget.NextMajor => ProjectBuildTarget.NextMajor,
                _ => ProjectBuildTarget.Current,
            };
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // A next-major build compiles with the newest beta compiler, because the
        // stable one may not read the next major's symbols; everything else keeps
        // the stable compiler every build has used.
        var compiler = await _compiler.ResolveAsync(prerelease: options.Target == BcBuildTarget.NextMajor, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The AL compiler isn't available yet. It's downloaded from NuGet on first use — check the server has outbound access, then retry.");

        var buildRoot = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(buildRoot);
        var results = new List<BuildAppResult>();
        var logs = new List<PendingLog>();
        // Compiler diagnostics as rows, not only as log text - the build page
        // counts them and the pull-request check run draws each one against its
        // own line (#627).
        var diagnostics = new List<PendingDiagnostic>();
        try
        {
            // 1. Clone every repo. A clone failure fails only that repo.
            // A pipeline build (manual or the nightly preview check) checks out the
            // branch its pipeline watches; a pull-request build keeps its own head and
            // ignores it (#963).
            var branch = build is not null && build.Trigger != ProjectBuildTrigger.PullRequest ? build.Branch : null;
            var clones = await CloneRepositoriesAsync(project, buildRoot, results, logs, options, branch, ct).ConfigureAwait(false);

            // Record the per-repo commit set + changelog while the clones are still
            // on disk (the changelog runs `git log` against them). Best-effort: a
            // provenance failure never sinks the build.
            if (build is not null)
            {
                await PersistRepoProvenanceAsync(build, project, clones, logs, ct).ConfigureAwait(false);
            }

            // 2. Discover extensions across the successful clones.
            var discovered = new List<DiscoveredApp>();
            foreach (var clone in clones)
            {
                foreach (var projectDir in DiscoverAppProjectDirs(clone.Dir))
                {
                    var manifest = TryReadManifest(projectDir);
                    if (manifest is null)
                    {
                        results.Add(new BuildAppResult(Path.GetFileName(projectDir), string.Empty,
                            ProjectBuildResultStatus.Failed, "Could not read app.json in this folder.",
                            RepoUrl: clone.Url, CommitSha: clone.CommitSha, CommitDate: clone.CommitDate));
                        continue;
                    }
                    discovered.Add(new DiscoveredApp(projectDir, manifest, clone));
                }
            }
            if (discovered.Count == 0)
            {
                throw new InvalidOperationException(DescribeNothingToBuild(results));
            }

            // 2b. Narrow to the extensions the user picked in the "New build"
            //     dialog (null selection = build everything, the default). A note
            //     in the log explains why the output is smaller than the repos.
            var selectedIds = ParseSelectedAppIds(build?.RequestedAppIdsJson);
            if (selectedIds is not null)
            {
                var kept = FilterBySelection(discovered, selectedIds);
                var skipped = discovered.Count - kept.Count;
                if (kept.Count == 0)
                {
                    throw new InvalidOperationException(
                        "None of the selected extensions were found in the repositories. They may have moved or been removed since the build was requested.");
                }
                if (skipped > 0)
                {
                    logs.Add(new PendingLog(null, "Build",
                        $"Compiling {kept.Count} of {discovered.Count} discovered extension(s); {skipped} were excluded by the build's selection."));
                }
                discovered = kept;
            }

            // 2c. Number the apps: the build's number goes into the third part of each
            //     version, in this build's own copy of the repository only. Before the
            //     compile so the .app, its file name and every record of it agree.
            if (build is not null
                && MayNumberApps(build.PipelineId, build.Trigger, options.Target)
                && await PipelineNumbersAppsAsync(_db, build.PipelineId!.Value, ct).ConfigureAwait(false))
            {
                discovered = StampBuildNumber(discovered, build.Id, logs);
            }

            // 3. Resolve the target BC version + country, download Microsoft symbols.
            var country = ResolveCountry(project.DefaultArtifactCountry);
            var majorMinor = SelectTargetMajorMinor(discovered.Select(d => d.Manifest));
            if (majorMinor is null)
            {
                throw new InvalidOperationException(
                    "None of the extensions declare an 'application' (or 'platform') version, so the matching Business Central symbols can't be resolved.");
            }

            ResolvedArtifact resolved;
            if (options.Target == BcBuildTarget.Current)
            {
                resolved = await _artifacts.ResolveOnPremAsync(country, majorMinor, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"No Business Central artifact matched application {majorMinor} for country '{country}'. Check the version and country.");
            }
            else
            {
                // The manifests are left as they are: the build compiles the code
                // as it stands against the next version's symbols, which is the point.
                var nextName = options.Target == BcBuildTarget.NextMajor ? "next major" : "next minor";
                resolved = await _artifacts.ResolveTargetAsync(country, options.Target, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Microsoft has not published a preview of the {nextName} Business Central version for country '{country}' yet.");
                logs.Add(new PendingLog(null, "Build",
                    $"Building against the {nextName} Business Central version: preview build {resolved.Version} ({country}) "
                    + $"from Microsoft's insider artifacts, compiled with AL compiler {compiler.Version}. "
                    + "The extensions' app.json files are not changed."));
            }

            // Stamped now rather than when the build finishes: a preview build that
            // fails to compile is the expected outcome, and it still has to say which
            // Business Central build it failed against.
            if (build is not null)
            {
                build.BcArtifactVersion = resolved.Version;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            var symbolsDir = Path.Combine(buildRoot, "symbols");
            Directory.CreateDirectory(symbolsDir);
            var download = await _artifacts.DownloadArtifactSetAsync(resolved.ApplicationUrl, ct).ConfigureAwait(false);
            int? parentReleaseId;
            IReadOnlyList<ResolvedSymbolPackage> fromFeeds = [];
            try
            {
                ExtractArtifactSymbols(download, symbolsDir);
                CopyCommittedSymbols(clones.Select(c => c.Dir).ToList(), symbolsDir);
                // Whatever is still missing comes from Microsoft's public symbol
                // feeds, then from the organisation's own earlier builds (#901).
                // Stored uploads are read first so neither fetches an app the
                // operator has deliberately supplied.
                var supplemental = await LoadSupplementalSymbolsAsync(projectId, ct).ConfigureAwait(false);
                fromFeeds = await ResolveDependencySymbolsAsync(projectId, discovered, supplemental, symbolsDir, resolved.MajorMinor, country, logs, ct)
                    .ConfigureAwait(false);
                // Operator-supplied symbols (the manual-symbols recovery path) are
                // written last so they win over a stale committed/artifact/feed/build
                // copy of the same package — the upload is the deliberate fix.
                await WriteSupplementalSymbolsAsync(projectId, supplemental, symbolsDir, ct).ConfigureAwait(false);
                // 4. Auto-import the parent BC release inline (best-effort) so
                //    cross-release references into Base App resolve. Reuses the
                //    artifact we already downloaded.
                parentReleaseId = await EnsureParentReleaseAsync(resolved, download, ct).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(download.ApplicationZipPath);
                if (download.PlatformZipPath is not null) TryDelete(download.PlatformZipPath);
            }

            // 4b. Put each vendor package the feeds resolved into the Object
            //     Explorer, once per (app id, version), so our code's references
            //     into it resolve (#901, Part 4). Best-effort, like the parent.
            var dependencyReleaseIds = await EnsureVendorReleasesAsync(fromFeeds, symbolsDir, parentReleaseId, logs, ct)
                .ConfigureAwait(false);

            // 5. Compile each extension in dependency order; a compiled sibling
            //    becomes a symbol for the apps that depend on it.
            var uploads = new List<AppFileUpload>();
            var artifacts = new List<PendingArtifact>();
            List<StoredSymbolPackage>? storedSymbols = null;
            List<InstalledAppFact>? installedApps = null;
            foreach (var app in TopologicalOrder(discovered))
            {
                ct.ThrowIfCancellationRequested();
                var (compiled, compileLog) = await CompileAsync(app, symbolsDir, compiler, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(compileLog))
                {
                    logs.Add(new PendingLog(app.Repo.RepositoryId, $"Compile: {app.Manifest.Name}", compileLog));
                    foreach (var diagnostic in AlcOutputParser.Parse(compileLog))
                    {
                        // The compiler names the file by its absolute path inside
                        // this build's temp root; a check-run annotation needs the
                        // path the repository knows it by.
                        diagnostics.Add(new PendingDiagnostic(
                            app.Repo.RepositoryId,
                            AlcOutputParser.MakeRelative(diagnostic.Path, app.Repo.Dir),
                            diagnostic));
                    }
                }
                if (compiled is null)
                {
                    // Name the dependency when that is what stopped it, so the row
                    // says what to supply rather than pointing at the log.
                    string? missingMessage = null;
                    var missing = AlcOutputParser.ParseMissingPackages(compileLog);
                    if (missing.Count > 0)
                    {
                        storedSymbols ??= await ReadStoredSymbolPackagesAsync(projectId, ct).ConfigureAwait(false);
                        installedApps ??= await ReadInstalledAppsAsync(projectId, ct).ConfigureAwait(false);
                        var failedSiblings = results
                            .Where(r => r.Status == ProjectBuildResultStatus.Failed && !string.IsNullOrEmpty(r.AppId))
                            .Select(r => r.AppId)
                            .ToList();
                        missingMessage = MissingDependencyReport.Compose(app.Manifest, missing, storedSymbols, failedSiblings,
                        [
                            $"the Business Central {resolved.MajorMinor} ({country}) symbols",
                            "the repositories' .alpackages folders",
                            "Microsoft's public symbol feeds",
                            "the builds of this organisation's Public and Read-only solutions",
                            "the symbols stored on this solution",
                        ], installedApps);
                    }
                    results.Add(new BuildAppResult(app.Manifest.Name, app.Manifest.Id,
                        ProjectBuildResultStatus.Failed, missingMessage ?? $"Compilation failed (see the build report for {app.Manifest.Name}).",
                        RepoUrl: app.Repo.Url, CommitSha: app.Repo.CommitSha, CommitDate: app.Repo.CommitDate));
                    continue;
                }
                // Read the .app into memory so the upload survives the temp-root
                // cleanup, and copy it into the symbol dir for dependents.
                var bytes = await File.ReadAllBytesAsync(compiled, ct).ConfigureAwait(false);
                var fileName = Path.GetFileName(compiled);
                uploads.Add(new AppFileUpload(
                    FileName: fileName,
                    AppStream: new MemoryStream(bytes, writable: false),
                    SourceZipStream: null));
                // Retain the compiled .app as a downloadable deliverable. Packaging
                // artifacts (.dep.app) are never compiler output here, but guard
                // anyway so they can't slip in as a download. See .design/artifacts.md.
                if (!fileName.EndsWith(".dep.app", StringComparison.OrdinalIgnoreCase))
                {
                    artifacts.Add(new PendingArtifact(fileName, BuildArtifactAppIdBackfill.CanonicalAppId(app.Manifest.Id), app.Manifest.Name, app.Manifest.Version, app.Manifest.Runtime, bytes));
                }
                results.Add(new BuildAppResult(app.Manifest.Name, app.Manifest.Id,
                    ProjectBuildResultStatus.Compiled, null,
                    RepoUrl: app.Repo.Url, CommitSha: app.Repo.CommitSha, CommitDate: app.Repo.CommitDate));
            }

            // Persist the retained deliverables against the build (best-effort; the
            // captured logs are persisted in the finally so they survive a throw too).
            if (build is not null)
            {
                await PersistArtifactsAsync(build, artifacts, ct).ConfigureAwait(false);
            }

            // Project labels aren't unique (the release id is their identity), so
            // a rebuild of the same project+version reuses the same clean label.
            var finalLabel = $"{project.Name} on BC {resolved.MajorMinor}";
            await FinalizeReleaseAsync(releaseId, finalLabel, parentReleaseId, dependencyReleaseIds, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Project build for {Project} (release {ReleaseId}): {Compiled} compiled, {Failed} failed, parent release {ParentReleaseId}.",
                project.Name, releaseId, uploads.Count, results.Count(r => r.Status == ProjectBuildResultStatus.Failed), parentReleaseId);

            return new ProjectBuildOutcome(uploads, results, parentReleaseId, finalLabel, resolved.MajorMinor);
        }
        finally
        {
            // Persist whatever logs we captured even on a whole-build failure (e.g.
            // unresolved symbols throws before compile), so the user can diagnose.
            if (build is not null && logs.Count > 0)
            {
                try { await PersistLogsAsync(build, logs, ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist build logs for release {ReleaseId}.", releaseId); }
            }
            if (build is not null)
            {
                try { await PersistDiagnosticsAsync(build, diagnostics, ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist build diagnostics for release {ReleaseId}.", releaseId); }
            }
            TryDeleteDirectory(buildRoot);
        }
    }

    // ── Build numbers in app versions ───────────────────────────────────

    /// <summary>
    /// Whether the pipeline has numbering on. Read at build time, so a build queued
    /// before the setting changed follows it. See <see cref="BuildVersionStamp"/>.
    /// </summary>
    internal static async Task<bool> PipelineNumbersAppsAsync(AppDbContext db, int pipelineId, CancellationToken ct) =>
        await db.OePipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId)
            .Select(p => p.AutoVersion)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// The kinds of build that can number their apps: a pipeline's own build against the
    /// current version. A pull-request check and a preview check are never deployed or
    /// published, so they compile the manifests as they are.
    /// </summary>
    internal static bool MayNumberApps(int? pipelineId, string trigger, BcBuildTarget target) =>
        pipelineId is not null && trigger != ProjectBuildTrigger.PullRequest && target == BcBuildTarget.Current;

    /// <summary>
    /// Writes each app's numbered version into its <c>app.json</c> in the clone and
    /// returns the apps with their manifests saying the same, plus one build-log section
    /// listing what each became. An app whose version can't be read or written keeps
    /// its own and says so in the log; it still compiles.
    /// </summary>
    internal static List<DiscoveredApp> StampBuildNumber(List<DiscoveredApp> apps, int buildNumber, List<PendingLog> logs)
    {
        var lines = new List<string>();
        var stamped = new List<DiscoveredApp>(apps.Count);
        foreach (var app in apps)
        {
            var version = BuildVersionStamp.Compute(app.Manifest.Version, buildNumber);
            if (version is null)
            {
                lines.Add($"{app.Manifest.Name}: kept {app.Manifest.Version}, because that version isn't made of whole numbers.");
                stamped.Add(app);
                continue;
            }
            if (!TryWriteVersion(app, version))
            {
                lines.Add($"{app.Manifest.Name}: kept {app.Manifest.Version}, because its app.json couldn't be updated.");
                stamped.Add(app);
                continue;
            }
            lines.Add($"{app.Manifest.Name}: {app.Manifest.Version} in app.json is built as {version}.");
            stamped.Add(app with { Manifest = app.Manifest with { Version = version } });
        }
        logs.Add(new PendingLog(null, "Version",
            $"Build #{buildNumber} adds its build number to the third part of each app's version. The app.json files in the repositories are not changed.\n" + string.Join("\n", lines)));
        return stamped;
    }

    /// <summary>
    /// Writes <paramref name="version"/> into the app's <c>app.json</c> in the clone.
    /// False, leaving the file alone, when it can't be done safely: the file is a link
    /// (a repository could point one anywhere the server can write, so it is only ever
    /// read through), it doesn't sit inside the clone, or reading or writing it fails.
    /// </summary>
    private static bool TryWriteVersion(DiscoveredApp app, string version)
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(app.ProjectDir, "app.json"));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(app.Repo.Dir)) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.Ordinal)) return false;
            // Every folder between the clone and the file as well, since a linked folder
            // would carry the write out of the clone just the same.
            FileSystemInfo? info = new FileInfo(path);
            while (info is not null && info.FullName.Length >= root.Length)
            {
                if (info.LinkTarget is not null) return false;
                info = info is FileInfo file ? file.Directory : ((DirectoryInfo)info).Parent;
            }

            var rewritten = BuildVersionStamp.WriteVersion(File.ReadAllText(path), version);
            if (rewritten is null) return false;
            File.WriteAllText(path, rewritten);
            return true;
        }
        // ArgumentException and JsonException: a manifest the whole-document fallback
        // can't hold (duplicate keys, say) keeps its version rather than failing the build.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or ArgumentException or JsonException)
        {
            return false;
        }
    }

    // ── Extension discovery (the pipeline editor's checklist cache) ─────────

    /// <summary>
    /// Re-discovers <paramref name="projectId"/>'s extensions and writes the result
    /// to the project's denormalised cache (<see cref="OeProject.DiscoveredExtensionsJson"/>
    /// / <see cref="OeProject.DiscoveredAt"/> / <see cref="OeProject.DiscoveryError"/>), so
    /// the pipeline editor's checklist appears instantly from cache. Run by
    /// <see cref="ProjectDiscoveryWorker"/> in the background under the requesting
    /// user's identity — it has <em>no</em> access check (the request-side
    /// <see cref="ProjectDiscoveryService"/> gates the enqueue) and captures failures
    /// into <see cref="OeProject.DiscoveryError"/> instead of throwing, leaving any prior
    /// good list intact. See <c>.design/artifacts.md</c>.
    /// </summary>
    public async Task DiscoverExtensionsForCacheAsync(int projectId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        // Tracked (not AsNoTracking) — we write the cache back onto the row.
        var project = await _db.OeProjects
            .Where(c => c.Id == projectId && c.DeletedAt == null)
            .Include(c => c.Repositories)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (project is null)
        {
            _logger.LogWarning("Discovery: project {ProjectId} no longer exists; skipping cache warm.", projectId);
            return;
        }

        var (extensions, error) = await DiscoverCoreAsync(project, ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (error is not null)
        {
            // Keep the prior good list; only record why the refresh couldn't improve it.
            project.DiscoveryError = Truncate(error, 2000);
        }
        else
        {
            project.DiscoveredExtensionsJson = JsonSerializer.Serialize(extensions);
            project.DiscoveredAt = now;
            project.DiscoveryError = null;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The clone-and-walk core behind discovery: shallow-clones each of
    /// <paramref name="project"/>'s repositories (blobless + sparse <c>app.json</c>, so
    /// it stays fast even on repos whose <c>.git</c> is bloated by committed binaries),
    /// reads each <c>app.json</c>, and returns the de-duplicated extension list. Returns
    /// a human-readable <c>Error</c> instead of throwing when nothing can be discovered
    /// (no repos, no token, clone failed, no app.json), so callers can cache it. Has no
    /// access check — the caller owns authorization.
    /// </summary>
    private async Task<(IReadOnlyList<DiscoveredExtension> Extensions, string? Error)> DiscoverCoreAsync(
        OeProject project, CancellationToken ct)
    {
        if (project.Repositories.Count == 0)
        {
            return (Array.Empty<DiscoveredExtension>(), "Add at least one repository to this project before building.");
        }

        var gitPath = NullIfBlank(Environment.GetEnvironmentVariable("GIT_PATH")) ?? "git";
        var root = Path.Combine(Path.GetTempPath(), TempPrefix + "discover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _logger.LogInformation("Discovery: cloning {RepoCount} repo(s) for project {ProjectId}.",
            project.Repositories.Count, project.Id);
        var discovered = new List<DiscoveredExtension>();
        var failures = new List<string>();
        try
        {
            var index = 0;
            foreach (var repo in project.Repositories)
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.Combine(root, $"repo-{index++}");
                var credentials = await _credentials.ResolveAsync(repo.Provider, ct).ConfigureAwait(false);
                if (credentials.Count == 0)
                {
                    failures.Add($"Couldn't reach \"{repo.DisplayName}\": {CloneCredentialResolver.NothingToCloneWith(repo.Provider)}");
                    continue;
                }

                // Discovery only needs app.json — never the (often gigabytes of
                // committed .alpackages) working tree. A blobless, no-checkout,
                // shallow clone fetches just commit + trees; a non-cone
                // sparse-checkout limited to app.json then materialises only those
                // files, lazily fetching only their tiny blobs. This keeps discovery
                // fast even on repos whose .git is bloated by committed binaries. The
                // token travels in git config (http.extraHeader), never the URL or argv.
                var (clone, used, cloneFailure) = await CloneWithAsync(gitPath,
                    new[] { "clone", "--filter=blob:none", "--no-checkout", "--depth", "1", "--single-branch", "--no-tags", "--quiet", repo.Url, dest },
                    root, dest, DiscoveryCloneTimeout, credentials, repo, ct).ConfigureAwait(false);
                var pat = used.Secret;
                var env = GitAuthEnv(repo.Provider, pat);
                if (!clone.Succeeded || !Directory.Exists(dest))
                {
                    failures.Add($"Couldn't clone \"{repo.DisplayName}\": {cloneFailure}".Trim());
                    _logger.LogWarning("Discovery: clone of {Repo} for project {ProjectId} failed (exit {Exit}).",
                        repo.DisplayName, project.Id, clone.ExitCode);
                    continue;
                }

                // Limit the working tree to app.json (gitignore-style match at any
                // depth) and materialise it — fetches only those blobs.
                await _processRunner.RunAsync(new ProcessRunRequest(gitPath,
                    new[] { "-C", dest, "sparse-checkout", "set", "--no-cone", "app.json" }, dest, env, DiscoveryCloneTimeout), ct).ConfigureAwait(false);
                var checkout = await _processRunner.RunAsync(new ProcessRunRequest(gitPath,
                    new[] { "-C", dest, "checkout" }, dest, env, DiscoveryCloneTimeout), ct).ConfigureAwait(false);
                if (!checkout.Succeeded)
                {
                    failures.Add($"Couldn't read \"{repo.DisplayName}\": {Sanitize(checkout.StdErr, pat)}".Trim());
                    _logger.LogWarning("Discovery: checkout of {Repo} for project {ProjectId} failed (exit {Exit}).",
                        repo.DisplayName, project.Id, checkout.ExitCode);
                    continue;
                }

                foreach (var projectDir in DiscoverAppProjectDirs(dest))
                {
                    var manifest = TryReadManifest(projectDir);
                    if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id)) continue;
                    discovered.Add(new DiscoveredExtension(
                        manifest.Id, manifest.Name, manifest.Publisher, manifest.Version, repo.Url, repo.DisplayName));
                }
            }

            if (discovered.Count == 0)
            {
                var reason = failures.Count > 0
                    ? string.Join(" ", failures)
                    : "No extensions with an app.json were found outside test folders.";
                _logger.LogWarning("Discovery: found no extensions for project {ProjectId}. {Reason}", project.Id, reason);
                return (Array.Empty<DiscoveredExtension>(), reason);
            }

            // Stable, de-duplicated by app id (the same app cloned twice is one row),
            // ordered by name for a predictable checklist.
            var deduped = discovered
                .GroupBy(d => NormalizeAppId(d.AppId))
                .Select(g => g.First())
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _logger.LogInformation("Discovery: found {Count} extension(s) for project {ProjectId}.", deduped.Count, project.Id);
            return (deduped, null);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    // ── Persistence helpers (worker calls these around ProcessReleaseAsync) ──

    /// <summary>
    /// Replaces the per-app build report for a release with <paramref name="results"/>.
    /// Clears any prior rows first so a rebuild/retry reports the latest attempt
    /// rather than accumulating duplicates.
    /// </summary>
    public async Task PersistResultsAsync(int releaseId, IReadOnlyList<BuildAppResult> results, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var now = _clock.GetUtcNow().UtcDateTime;

        var stale = await _db.OeProjectBuildResults
            .Where(r => r.ReleaseId == releaseId)
            .ToListAsync(ct).ConfigureAwait(false);
        if (stale.Count > 0) _db.OeProjectBuildResults.RemoveRange(stale);

        foreach (var r in results)
        {
            _db.OeProjectBuildResults.Add(new OeProjectBuildResult
            {
                OrganizationId = orgId,
                ReleaseId = releaseId,
                AppName = Truncate(r.AppName, 250),
                AppId = Truncate(r.AppId, 50),
                Status = r.Status,
                Message = r.Message,
                RepoUrl = r.RepoUrl,
                CommitSha = r.CommitSha,
                CommitDate = r.CommitDate,
                CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Promotes the <c>compiled</c> rows to <c>ingested</c> once the shared
    /// importer has accepted the uploads — called by the worker after a successful
    /// <see cref="ReleaseImportService.ProcessReleaseAsync"/>.
    /// </summary>
    public async Task MarkCompiledResultsIngestedAsync(int releaseId, CancellationToken ct = default)
    {
        var rows = await _db.OeProjectBuildResults
            .Where(r => r.ReleaseId == releaseId && r.Status == ProjectBuildResultStatus.Compiled)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in rows) row.Status = ProjectBuildResultStatus.Ingested;
        if (rows.Count > 0) await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── ProjectBuild lifecycle (worker calls these around the build) ────────

    /// <summary>
    /// Flips the build that produced <paramref name="releaseId"/> to <c>ready</c>,
    /// stamping its BC version and finish time. No-op when the release has no
    /// <see cref="OeProjectBuild"/> (legacy / synthetic). Mirrors the Release flip.
    /// </summary>
    public async Task MarkBuildReadyAsync(int releaseId, string? bcVersion, CancellationToken ct = default)
    {
        var build = await _db.OeProjectBuilds.FirstOrDefaultAsync(b => b.ReleaseId == releaseId, ct).ConfigureAwait(false);
        if (build is null) return;
        build.Status = ProjectBuildStatus.Ready;
        build.BcVersion = bcVersion ?? build.BcVersion;
        build.FinishedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Flips the build that produced <paramref name="releaseId"/> to <c>failed</c>
    /// with <paramref name="message"/> and a finish time. No-op when the release
    /// has no <see cref="OeProjectBuild"/>.
    /// </summary>
    public async Task MarkBuildFailedAsync(int releaseId, string message, CancellationToken ct = default)
    {
        var build = await _db.OeProjectBuilds.FirstOrDefaultAsync(b => b.ReleaseId == releaseId, ct).ConfigureAwait(false);
        if (build is null) return;
        build.Status = ProjectBuildStatus.Failed;
        build.FailureMessage = Truncate(message, 2000);
        build.FinishedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── ProjectBuild provenance (repo commit set + changelog) ───────────────

    /// <summary>The newest N commits we record in the changelog before collapsing the tail into a summary note.</summary>
    internal const int ChangelogCommitCap = 100;

    /// <summary>
    /// Records the per-repo commit set (<see cref="OeProjectBuildRepoCommit"/>) and
    /// the changelog (<see cref="OeProjectBuildCommit"/>) for the build, computing the
    /// latter as <c>git log &lt;prev&gt;..&lt;HEAD&gt;</c> against the project's last
    /// <em>successful</em> build per repo. Best-effort: a provenance failure logs and
    /// returns rather than sinking the build.
    /// </summary>
    private async Task PersistRepoProvenanceAsync(OeProjectBuild build, OeProject project, List<ClonedRepo> clones, List<PendingLog> logs, CancellationToken ct)
    {
        try
        {
            var orgId = build.OrganizationId;
            // The commit set for this build.
            foreach (var clone in clones)
            {
                _db.OeProjectBuildRepoCommits.Add(new OeProjectBuildRepoCommit
                {
                    OrganizationId = orgId,
                    ProjectBuildId = build.Id,
                    ProjectRepositoryId = clone.RepositoryId,
                    RepoUrl = Truncate(clone.Url, 2000),
                    RepoDisplayName = Truncate(clone.DisplayName, 250),
                    CommitHash = Truncate(clone.CommitSha ?? string.Empty, 64),
                    CommittedAt = clone.CommitDate,
                });
            }
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            await ComputeAndPersistChangelogAsync(build, project, clones, logs, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record build provenance for build {BuildId} (release {ReleaseId}).", build.Id, build.ReleaseId);
        }
    }

    /// <summary>
    /// Computes and persists the per-repo changelog for the build. For each repo it
    /// finds the commit the project's last successful build pinned, then records
    /// <c>git log &lt;prev&gt;..&lt;HEAD&gt;</c>. A repo with no prior build, or whose
    /// previous commit is no longer an ancestor (force-push / rebase), gets a single
    /// summary note instead of a commit list. Over-cap ranges are truncated with a
    /// "...and N more" note.
    /// </summary>
    private async Task ComputeAndPersistChangelogAsync(OeProjectBuild build, OeProject project, List<ClonedRepo> clones, List<PendingLog> logs, CancellationToken ct)
    {
        var gitPath = NullIfBlank(Environment.GetEnvironmentVariable("GIT_PATH")) ?? "git";
        var orgId = build.OrganizationId;

        // The previous successful build's commit per repo (the changelog baseline). A
        // preview build is a check, not something that shipped, so it never becomes the
        // baseline a current build's "what changed" is measured from (#994).
        var prevBuildId = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ProjectId == project.Id && b.Id != build.Id && b.Status == ProjectBuildStatus.Ready
                        && b.BcTarget == ProjectBuildTarget.Current)
            .OrderByDescending(b => b.StartedAt)
            .Select(b => (int?)b.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var prevByRepo = new Dictionary<int, string>();
        if (prevBuildId is not null)
        {
            var prevCommits = await _db.OeProjectBuildRepoCommits.AsNoTracking()
                .Where(c => c.ProjectBuildId == prevBuildId && c.ProjectRepositoryId != null && c.CommitHash != "")
                .Select(c => new { RepoId = c.ProjectRepositoryId!.Value, c.CommitHash })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var c in prevCommits) prevByRepo[c.RepoId] = c.CommitHash;
        }

        foreach (var clone in clones)
        {
            if (clone.RepositoryId is null || clone.CommitSha is null) continue;

            var rows = new List<OeProjectBuildCommit>();

            if (!prevByRepo.TryGetValue(clone.RepositoryId.Value, out var prevSha))
            {
                rows.Add(SummaryNote(orgId, build.Id, clone.RepositoryId.Value,
                    "First build of this repository — no previous successful build to compare against."));
            }
            else if (prevSha == clone.CommitSha)
            {
                rows.Add(SummaryNote(orgId, build.Id, clone.RepositoryId.Value, "No new commits since the last successful build."));
            }
            else
            {
                var ancestry = await _processRunner.RunAsync(new ProcessRunRequest(
                    gitPath, new[] { "-C", clone.Dir, "merge-base", "--is-ancestor", prevSha, "HEAD" }, clone.Dir), ct).ConfigureAwait(false);
                if (!ancestry.Succeeded)
                {
                    rows.Add(SummaryNote(orgId, build.Id, clone.RepositoryId.Value,
                        $"The previous build's commit ({Short(prevSha)}) is no longer in history — the branch was force-pushed or rebased, so the changelog can't be computed."));
                }
                else
                {
                    var log = await _processRunner.RunAsync(new ProcessRunRequest(
                        gitPath,
                        new[] { "-C", clone.Dir, "log", "--no-merges", "-n", (ChangelogCommitCap + 1).ToString(),
                                "--pretty=format:%h%an%cI%s", $"{prevSha}..HEAD" },
                        clone.Dir), ct).ConfigureAwait(false);
                    var (parsed, truncated) = ParseChangelog(log.StdOut, ChangelogCommitCap);
                    var ordering = 0;
                    foreach (var entry in parsed)
                    {
                        rows.Add(new OeProjectBuildCommit
                        {
                            OrganizationId = orgId,
                            ProjectBuildId = build.Id,
                            ProjectRepositoryId = clone.RepositoryId,
                            ShortHash = Truncate(entry.ShortHash, 64),
                            Message = entry.Subject,
                            Author = Truncate(entry.Author, 250),
                            CommittedAt = entry.CommittedAt,
                            Ordering = ordering++,
                        });
                    }
                    if (truncated)
                    {
                        var more = SummaryNote(orgId, build.Id, clone.RepositoryId.Value,
                            $"...and more commits not shown (the changelog is capped at {ChangelogCommitCap}).");
                        more.Ordering = ordering;
                        rows.Add(more);
                    }
                    if (parsed.Count == 0 && !truncated)
                    {
                        rows.Add(SummaryNote(orgId, build.Id, clone.RepositoryId.Value, "No new commits since the last successful build."));
                    }
                }
            }

            _db.OeProjectBuildCommits.AddRange(rows);
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static OeProjectBuildCommit SummaryNote(int orgId, int buildId, int? repoId, string text) => new()
    {
        OrganizationId = orgId,
        ProjectBuildId = buildId,
        ProjectRepositoryId = repoId,
        ShortHash = string.Empty,
        Message = text,
        Author = string.Empty,
        CommittedAt = null,
        Ordering = 0,
    };

    /// <summary>
    /// Parses <c>git log</c> output formatted as short-hash / author / committer-date
    /// / subject, separated by the ASCII unit-separator (0x1F) with one commit per
    /// line, into changelog entries. Returns whether the range exceeded
    /// <paramref name="cap"/> (the caller passed <c>cap + 1</c> to <c>-n</c>).
    /// </summary>
    internal static (IReadOnlyList<ChangelogEntry> Entries, bool Truncated) ParseChangelog(string stdout, int cap)
    {
        var entries = new List<ChangelogEntry>();
        if (string.IsNullOrWhiteSpace(stdout)) return (entries, false);

        foreach (var line in stdout.Split('\n'))
        {
            if (line.Length == 0) continue;
            var parts = line.Split('');
            if (parts.Length < 4) continue;
            DateTime? date = DateTimeOffset.TryParse(parts[2].Trim(), out var dto) ? dto.UtcDateTime : null;
            entries.Add(new ChangelogEntry(parts[0].Trim(), parts[1].Trim(), date, parts[3].Trim()));
        }

        var truncated = entries.Count > cap;
        if (truncated) entries = entries.Take(cap).ToList();
        return (entries, truncated);
    }

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    // ── ProjectBuild artifacts + logs ───────────────────────────────────────

    /// <summary>Replaces the build's retained deliverables with <paramref name="artifacts"/> (clears stale rows so a retry doesn't duplicate).</summary>
    private async Task PersistArtifactsAsync(OeProjectBuild build, List<PendingArtifact> artifacts, CancellationToken ct)
    {
        var stale = await _db.OeProjectBuildArtifacts.Where(a => a.ProjectBuildId == build.Id).ToListAsync(ct).ConfigureAwait(false);
        if (stale.Count > 0) _db.OeProjectBuildArtifacts.RemoveRange(stale);

        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var a in artifacts)
        {
            _db.OeProjectBuildArtifacts.Add(new OeProjectBuildArtifact
            {
                OrganizationId = build.OrganizationId,
                ProjectBuildId = build.Id,
                FileName = Truncate(a.FileName, 400),
                AppId = a.AppId,
                AppName = Truncate(a.AppName, 250),
                AppVersion = Truncate(a.AppVersion, 50),
                RuntimeVersion = a.Runtime is null ? null : Truncate(a.Runtime, 50),
                SizeBytes = a.Content.LongLength,
                Content = a.Content,
                CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Replaces the build's logs with <paramref name="logs"/> (idempotent across the success-path and the failure finally).</summary>
    private async Task PersistLogsAsync(OeProjectBuild build, List<PendingLog> logs, CancellationToken ct)
    {
        var stale = await _db.OeProjectBuildLogs.Where(l => l.ProjectBuildId == build.Id).ToListAsync(ct).ConfigureAwait(false);
        if (stale.Count > 0) _db.OeProjectBuildLogs.RemoveRange(stale);

        var now = _clock.GetUtcNow().UtcDateTime;
        var ordering = 0;
        foreach (var l in logs)
        {
            _db.OeProjectBuildLogs.Add(new OeProjectBuildLog
            {
                OrganizationId = build.OrganizationId,
                ProjectBuildId = build.Id,
                ProjectRepositoryId = l.RepoId,
                Section = Truncate(l.Section, 250),
                Content = l.Content,
                Ordering = ordering++,
                CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the build's compiler diagnostics with <paramref name="diagnostics"/>.
    /// Clears stale rows first, like the logs, so a rebuild reports the latest
    /// attempt rather than accumulating both.
    /// </summary>
    private async Task PersistDiagnosticsAsync(OeProjectBuild build, List<PendingDiagnostic> diagnostics, CancellationToken ct)
    {
        var stale = await _db.OeProjectBuildDiagnostics
            .Where(d => d.ProjectBuildId == build.Id).ToListAsync(ct).ConfigureAwait(false);
        if (stale.Count == 0 && diagnostics.Count == 0) return;
        if (stale.Count > 0) _db.OeProjectBuildDiagnostics.RemoveRange(stale);

        var ordering = 0;
        foreach (var d in diagnostics)
        {
            _db.OeProjectBuildDiagnostics.Add(new OeProjectBuildDiagnostic
            {
                OrganizationId = build.OrganizationId,
                ProjectBuildId = build.Id,
                ProjectRepositoryId = d.RepoId,
                Path = Truncate(d.RelativePath, 1000),
                Line = d.Diagnostic.Line,
                Column = d.Diagnostic.Column,
                Severity = d.Diagnostic.Severity,
                Code = Truncate(d.Diagnostic.Code, 50),
                Message = d.Diagnostic.Message,
                Ordering = ordering++,
            });
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── Clone ───────────────────────────────────────────────────────────

    private async Task<List<ClonedRepo>> CloneRepositoriesAsync(
        OeProject project, string buildRoot, List<BuildAppResult> results, List<PendingLog> logs,
        ProjectBuildOptions options, string? branch, CancellationToken ct)
    {
        var gitPath = NullIfBlank(Environment.GetEnvironmentVariable("GIT_PATH")) ?? "git";
        var clones = new List<ClonedRepo>();

        // The branch came from a form a person filled in, so it is held to the
        // same rule here, at the boundary that runs git, as the pipeline editor
        // held it to - a stored value that predates the rule, or a second caller,
        // must not reach the command line unchecked.
        if (branch is not null && !GitBranchName.IsValid(branch))
        {
            foreach (var repo in project.Repositories)
            {
                results.Add(new BuildAppResult(repo.DisplayName, string.Empty, ProjectBuildResultStatus.Failed,
                    "The pipeline's branch name is not one git accepts. Edit the pipeline and correct it.", RepoUrl: repo.Url));
            }
            logs.Add(new PendingLog(null, "Build", "Refused a branch name that git does not accept."));
            return clones;
        }
        var index = 0;
        foreach (var repo in project.Repositories)
        {
            var dest = Path.Combine(buildRoot, $"repo-{index++}");
            // A pull-request build has no user, so there is nothing personal to
            // clone with: it clones as the app's installation instead, in the same
            // http.extraHeader shape a token travels in (#627). A manual build
            // clones as the person who started it - their connected GitHub
            // account first, their stored build token after that.
            IReadOnlyList<CloneCredential> credentials;
            if (options.InstallationToken is not null)
            {
                credentials = repo.Provider == RepositoryProvider.GitHub
                    ? [new CloneCredential(options.InstallationToken, "the installation")]
                    : [];
            }
            else
            {
                credentials = await _credentials.ResolveAsync(repo.Provider, ct).ConfigureAwait(false);
            }
            if (credentials.Count == 0)
            {
                var reason = options.InstallationToken is not null
                    ? $"This build was started from a pull request, and the workbench can only reach {RepositoryProvider.GitHub.DisplayName()} repositories that way. Build this solution by hand to include the {repo.Provider.DisplayName()} repository."
                    : $"{CloneCredentialResolver.NothingToCloneWith(repo.Provider)} Then rebuild.";
                results.Add(new BuildAppResult(repo.DisplayName, string.Empty, ProjectBuildResultStatus.Failed,
                    reason, RepoUrl: repo.Url));
                logs.Add(new PendingLog(repo.Id, repo.DisplayName,
                    options.InstallationToken is not null
                        ? $"Skipped: a pull-request build has no credential for a {repo.Provider.DisplayName()} repository."
                        : $"Skipped: nothing to reach {repo.Provider.DisplayName()} with for the user who started this build."));
                continue;
            }

            // A blobless single-branch clone keeps the full commit history (so the
            // changelog's `git log <prev>..<new>` and the force-push ancestry check
            // work) while fetching file blobs lazily on checkout — close to a
            // depth-1 clone's transfer for the working tree, but with the metadata
            // the changelog needs. The token travels in the environment
            // (GIT_CONFIG_* http.extraHeader), never in the URL, on disk, or in the
            // world-readable process argv.
            var args = new List<string> { "clone", "--filter=blob:none", "--single-branch", "--quiet" };
            // The pipeline's watched branch, when it names one. A repository
            // without that branch fails its clone with git's own message, which
            // names the branch - the same as any other clone failure.
            if (branch is not null) args.AddRange(["--branch", branch]);
            args.AddRange([repo.Url, dest]);
            var (result, used, cloneFailure) = await CloneWithAsync(gitPath, args, buildRoot, dest, BuildCloneTimeout(), credentials, repo, ct)
                .ConfigureAwait(false);
            var pat = used.Secret;
            var env = GitAuthEnv(repo.Provider, pat);
            var cloneLog = Sanitize(string.Join("\n", new[] { result.StdOut, result.StdErr }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim(), credentials);
            if (result.Succeeded && Directory.Exists(dest))
            {
                // The pull request's own repository is moved onto the head commit
                // the delivery named. The clone above is of the default branch, so
                // the commit under review usually is not in it yet - fetch exactly
                // that object and detach onto it. Everything else about the build
                // (symbols, dependency order, ingest) is then identical to a manual
                // one, which is the point.
                if (options.HeadSha is { Length: > 0 } headSha && options.RepositoryId == repo.Id)
                {
                    var checkedOut = await CheckoutCommitAsync(gitPath, dest, headSha, env, pat, repo, logs, results, ct)
                        .ConfigureAwait(false);
                    if (!checkedOut) continue;
                }

                var (sha, date) = await CaptureCommitAsync(gitPath, dest, ct).ConfigureAwait(false);
                clones.Add(new ClonedRepo(dest, repo.Url, sha, date, repo.Id, repo.DisplayName));
                logs.Add(new PendingLog(repo.Id, repo.DisplayName,
                    $"Cloned {repo.Url} at {(sha is null ? "(unknown commit)" : sha)} using {used.Source}.{(cloneLog.Length > 0 ? "\n" + cloneLog : "")}"));
            }
            else
            {
                results.Add(new BuildAppResult(repo.DisplayName, string.Empty, ProjectBuildResultStatus.Failed,
                    $"git clone failed: {cloneFailure}".Trim(), RepoUrl: repo.Url));
                logs.Add(new PendingLog(repo.Id, repo.DisplayName, $"git clone failed (exit {result.ExitCode}): {cloneFailure}".Trim()));
                _logger.LogWarning("Project {ProjectId}: clone of {Repo} exited {Exit}.", project.Id, repo.DisplayName, result.ExitCode);
            }
        }
        return clones;
    }

    /// <summary>
    /// Runs a clone with each of <paramref name="credentials"/> in turn until one
    /// succeeds, and returns the last outcome with the credential it was tried
    /// with.
    ///
    /// <para>Trying rather than choosing is deliberate: the connected GitHub
    /// account reaches only the repositories the App is installed on, and the
    /// build token may reach more or fewer, and only git knows which for the
    /// repository in front of it. A failed attempt's directory is removed before
    /// the next, because git refuses to clone into a path that exists.</para>
    ///
    /// <para><c>Failure</c> describes every attempt, not just the last, so a person
    /// reading a failed clone can tell which of their credentials was refused and
    /// why - the last error alone once sent someone chasing the wrong token.</para>
    /// </summary>
    private async Task<(ProcessRunResult Result, CloneCredential Used, string Failure)> CloneWithAsync(
        string gitPath, IReadOnlyList<string> args, string workDir, string dest, TimeSpan timeout,
        IReadOnlyList<CloneCredential> credentials, OeProjectRepository repo, CancellationToken ct)
    {
        ProcessRunResult result = null!;
        CloneCredential used = null!;
        var attempts = new List<(CloneCredential Credential, string StdErr)>(credentials.Count);
        for (var i = 0; i < credentials.Count; i++)
        {
            used = credentials[i];
            result = await _processRunner.RunAsync(new ProcessRunRequest(
                gitPath, args, workDir, GitAuthEnv(repo.Provider, used.Secret), timeout), ct).ConfigureAwait(false);
            if (result.Succeeded && Directory.Exists(dest)) return (result, used, string.Empty);
            attempts.Add((used, result.StdErr));
            if (i + 1 < credentials.Count)
            {
                _logger.LogInformation(
                    "Clone of {Repo} using {Source} exited {Exit}; trying {Next}.",
                    repo.DisplayName, used.Source, result.ExitCode, credentials[i + 1].Source);
                TryDeleteDirectory(dest);
            }
        }
        return (result, used, DescribeCloneFailures(attempts, credentials));
    }

    /// <summary>
    /// The whole-build error when nothing was found to compile. The per-repository
    /// reasons are the only record of why, and a thrown build keeps only this
    /// message, so a repository that could not be checked out (no credential for
    /// the person who started the build, a failed clone) is named here rather than
    /// reported as a repository without extensions.
    /// </summary>
    internal static string DescribeNothingToBuild(IReadOnlyList<BuildAppResult> failures)
    {
        if (failures.Count == 0)
        {
            return "No buildable extensions were found. Check the repositories contain an app.json outside test folders.";
        }
        var reasons = failures.Select(f => f.Message).Distinct(StringComparer.Ordinal).ToList();
        return reasons.Count == 1
            ? $"Nothing was built. {reasons[0]}"
            : "Nothing was built. " + string.Join(" ", failures.Select(f => $"{f.AppName}: {f.Message}"));
    }

    /// <summary>
    /// Words for a clone that failed with every credential it had. One attempt
    /// reads as git's own error, as it always has; several name each credential
    /// before its error, so "the build token was refused" and "the connected
    /// account was refused" stop looking identical. Secrets are scrubbed from
    /// every attempt's output, not only the one that produced it.
    /// </summary>
    internal static string DescribeCloneFailures(
        IReadOnlyList<(CloneCredential Credential, string StdErr)> attempts,
        IReadOnlyList<CloneCredential> credentials)
    {
        if (attempts.Count == 0) return string.Empty;
        if (attempts.Count == 1) return Sanitize(attempts[0].StdErr ?? string.Empty, credentials).Trim();

        return string.Join(" ", attempts.Select(a =>
        {
            var error = Regex.Replace(Sanitize(a.StdErr ?? string.Empty, credentials), @"\s+", " ").Trim().TrimEnd('.');
            return $"With {a.Credential.Source}: {(error.Length > 0 ? error : "failed without a message")}.";
        }));
    }

    /// <summary>
    /// Fetches one commit into an already-cloned repository and detaches onto it,
    /// returning whether it worked.
    ///
    /// <para>A shallow single-commit fetch is what a pull-request build needs and
    /// all it needs: the head commit is on a branch the blobless single-branch
    /// clone did not follow, and nothing downstream reads history from this
    /// repository - the changelog is a manual build's concern. A failure is that
    /// repository's failure, not the whole build's, so it is recorded the way a
    /// clone failure is: the head may have been force-pushed away between GitHub
    /// telling us and us asking, which is an ordinary race rather than a fault.</para>
    /// </summary>
    private async Task<bool> CheckoutCommitAsync(
        string gitPath, string cloneDir, string commitSha, Dictionary<string, string> env, string pat,
        OeProjectRepository repo, List<PendingLog> logs, List<BuildAppResult> results, CancellationToken ct)
    {
        // The commit reaches this method from a GitHub webhook, so it is checked
        // against what a git object name can be before it goes on a command line.
        // The webhook parser refuses anything else too; this is the guard at the
        // boundary that actually runs git, and it is the one that has to hold if a
        // second caller ever arrives.
        if (!CommitShaRegex.IsMatch(commitSha))
        {
            results.Add(new BuildAppResult(repo.DisplayName, string.Empty, ProjectBuildResultStatus.Failed,
                "The pull request did not name a commit that could be checked out.", RepoUrl: repo.Url));
            logs.Add(new PendingLog(repo.Id, repo.DisplayName, "Refused a commit name that is not a git object id."));
            _logger.LogWarning(
                "Pull-request build: refused a commit name that is not a git object id in {Repo}.", repo.DisplayName);
            return false;
        }

        var outcome = await _processRunner.RunAsync(new ProcessRunRequest(
            // "--" ends git's option parsing, so a revision beginning with a dash
            // can never be read as a switch.
            gitPath, new[] { "-C", cloneDir, "fetch", "--depth", "1", "origin", "--", commitSha }, cloneDir, env, BuildCloneTimeout()), ct)
            .ConfigureAwait(false);
        if (outcome.Succeeded)
        {
            outcome = await _processRunner.RunAsync(new ProcessRunRequest(
                gitPath, new[] { "-C", cloneDir, "checkout", "--detach", commitSha }, cloneDir, env), ct)
                .ConfigureAwait(false);
            if (outcome.Succeeded)
            {
                logs.Add(new PendingLog(repo.Id, repo.DisplayName, $"Checked out {commitSha} for the pull request."));
                return true;
            }
        }

        var detail = Sanitize(string.IsNullOrWhiteSpace(outcome.StdErr) ? outcome.StdOut : outcome.StdErr, pat).Trim();
        results.Add(new BuildAppResult(repo.DisplayName, string.Empty, ProjectBuildResultStatus.Failed,
            $"Could not check out the commit the pull request points at ({Short(commitSha)}). It may have been replaced since.",
            RepoUrl: repo.Url));
        logs.Add(new PendingLog(repo.Id, repo.DisplayName, $"Could not check out {commitSha}: {detail}".Trim()));
        _logger.LogWarning("Pull-request build: could not check out {CommitSha} in {Repo}.", commitSha, repo.DisplayName);
        return false;
    }

    /// <summary>
    /// A git object name: hex, and between an abbreviated and a full SHA-1.
    ///
    /// <para><c>git checkout</c> cannot take a <c>--</c> before a revision - that
    /// turns the revision into a pathspec and git refuses it - so this, not an
    /// end-of-options marker, is what keeps a hostile name off that command line.
    /// <c>git fetch</c> does take one, and gets it.</para>
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex CommitShaRegex =
        new("^[0-9a-fA-F]{7,40}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Reads the cloned repo's pinned commit — full SHA + committer date (UTC) —
    /// for build provenance. Best-effort: a failure (shallow clone oddity, missing
    /// HEAD) returns nulls rather than failing the build.
    /// </summary>
    private async Task<(string? Sha, DateTime? Date)> CaptureCommitAsync(string gitPath, string cloneDir, CancellationToken ct)
    {
        try
        {
            // %H = full SHA, %cI = committer date (strict ISO-8601), tab-separated.
            var r = await _processRunner.RunAsync(new ProcessRunRequest(
                gitPath, new[] { "-C", cloneDir, "show", "-s", "--format=%H%x09%cI", "HEAD" }, cloneDir), ct).ConfigureAwait(false);
            if (!r.Succeeded) return (null, null);
            var parts = r.StdOut.Trim().Split('\t');
            var sha = parts.Length > 0 && parts[0].Trim().Length > 0 ? parts[0].Trim() : null;
            DateTime? date = parts.Length > 1 && DateTimeOffset.TryParse(parts[1].Trim(), out var dto)
                ? dto.UtcDateTime : null;
            return (sha, date);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the commit for {CloneDir}; build provenance will be incomplete.", cloneDir);
            return (null, null);
        }
    }

    // ── Symbols ─────────────────────────────────────────────────────────

    /// <summary>Extracts every Microsoft <c>.app</c> from the downloaded artifact set into <paramref name="symbolsDir"/>.</summary>
    private void ExtractArtifactSymbols(BcArtifactDownload download, string symbolsDir)
    {
        var openedStreams = new List<Stream>();
        try
        {
            var (appUploads, appArchive) = ReleaseZipStaging.OpenBcArtifactZip(download.ApplicationZipPath, isPlatform: false, openedStreams);
            using (appArchive) WriteSymbolApps(appUploads, symbolsDir);
            if (download.PlatformZipPath is not null)
            {
                var (platUploads, platArchive) = ReleaseZipStaging.OpenBcArtifactZip(download.PlatformZipPath, isPlatform: true, openedStreams);
                using (platArchive) WriteSymbolApps(platUploads, symbolsDir);
            }
        }
        finally
        {
            foreach (var s in openedStreams) { try { s.Dispose(); } catch { /* swallow */ } }
        }
    }

    private static void WriteSymbolApps(IReadOnlyList<AppFileUpload> uploads, string symbolsDir)
    {
        foreach (var upload in uploads)
        {
            var dest = Path.Combine(symbolsDir, Path.GetFileName(upload.FileName));
            using var file = File.Create(dest);
            upload.AppStream.CopyTo(file);
        }
    }

    /// <summary>
    /// Reads the operator-supplied dependency symbols stored for the project (the
    /// manual-symbols recovery path). Persisted at the project level, so every
    /// later build benefits. See <c>.design/object-explorer-project-builds.md</c>.
    /// </summary>
    private async Task<List<SupplementalSymbol>> LoadSupplementalSymbolsAsync(int projectId, CancellationToken ct) =>
        await _db.OeProjectSymbols.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .Select(s => new SupplementalSymbol(s.FileName, s.Content))
            .ToListAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Writes the stored uploads into the symbol dir, overwriting a same-named
    /// copy so the upload — the deliberate fix for a dependency nothing else
    /// supplies — takes effect.
    /// </summary>
    private async Task WriteSupplementalSymbolsAsync(int projectId, List<SupplementalSymbol> symbols, string symbolsDir, CancellationToken ct)
    {
        foreach (var symbol in symbols)
        {
            var dest = Path.Combine(symbolsDir, Path.GetFileName(symbol.FileName));
            await File.WriteAllBytesAsync(dest, symbol.Content, ct).ConfigureAwait(false);
        }
        if (symbols.Count > 0)
        {
            _logger.LogInformation("Merged {Count} supplemental symbol(s) into the build cache for project {ProjectId}.",
                symbols.Count, projectId);
        }
    }

    /// <summary>
    /// Supplies the dependencies nothing else supplied - from Microsoft's public
    /// symbol feeds first, then from the <c>.app</c>s the organisation's earlier
    /// builds retained (a PTE another solution builds, #901 Part 3) - and records in
    /// the build log where each one came from or why it could not be found. An app
    /// counts as supplied when the artifact or a committed <c>.alpackages/</c>
    /// already put it in the symbol dir, when this build compiles it, or when a
    /// stored upload carries it. Never fails the build: an unresolved app fails only
    /// the extensions that need it, at compile time, as a missing dependency always has.
    /// Returns the packages the feeds supplied, which Part 4 ingests as vendor releases.
    /// </summary>
    private async Task<IReadOnlyList<ResolvedSymbolPackage>> ResolveDependencySymbolsAsync(
        int projectId, IReadOnlyList<DiscoveredApp> discovered, List<SupplementalSymbol> supplemental, string symbolsDir,
        string applicationVersion, string country, List<PendingLog> logs, CancellationToken ct)
    {
        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in discovered) provided.Add(NormalizeAppId(app.Manifest.Id));
        foreach (var upload in supplemental)
        {
            using var stream = new MemoryStream(upload.Content, writable: false);
            if (AppPackageReader.TryReadManifest(stream) is { } manifest) provided.Add(NormalizeAppId(manifest.AppId.ToString()));
        }

        var dependencies = discovered
            .SelectMany(d => d.Manifest.Dependencies)
            .Where(d => NormalizeAppId(d.Id).Length > 0)
            .GroupBy(d => NormalizeAppId(d.Id))
            .Select(g => new SymbolDependency(g.Key, g.First().Name,
                g.Select(d => d.Version).OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : null).First()))
            .ToList();

        var lines = new List<string>();
        var fromFeeds = new List<ResolvedSymbolPackage>();
        var unresolved = new List<UnresolvedSymbol>(
            await ResolveFromSymbolFeedsAsync(dependencies, symbolsDir, provided, applicationVersion, country, lines, fromFeeds, ct)
                .ConfigureAwait(false));

        // The feeds only know published apps; a PTE another solution in the
        // organisation builds is in that solution's retained build output.
        var fromBuilds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var outcome = await new BuildArtifactSymbolResolver(_db, _logger).ResolveAsync(
                new BuildArtifactSymbolRequest(projectId, dependencies, symbolsDir, provided, applicationVersion), ct).ConfigureAwait(false);
            foreach (var hit in outcome.Resolved)
            {
                fromBuilds.Add(hit.AppId);
                var source = hit.ProjectId == projectId
                    ? $"this solution's build #{hit.BuildId}"
                    : $"solution {hit.ProjectName}'s build #{hit.BuildId}";
                lines.Add($"Resolved {hit.AppName} {hit.AppVersion} from {source}.");
            }
            // What those apps need in turn and no build carried: the feeds have
            // not been asked about these yet.
            if (outcome.TransitiveMisses.Count > 0)
            {
                unresolved.AddRange(await ResolveFromSymbolFeedsAsync(
                    outcome.TransitiveMisses, symbolsDir, provided, applicationVersion, country, lines, fromFeeds, ct).ConfigureAwait(false));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Resolving symbols from earlier builds failed; the build continues with the symbols it already has.");
            lines.Add($"Could not look up dependencies in earlier builds: {ex.Message}");
        }

        foreach (var missing in unresolved.Where(u => !fromBuilds.Contains(NormalizeAppId(u.AppId))))
        {
            var wanted = missing.MinVersion is null ? string.Empty : $" {missing.MinVersion} or later";
            lines.Add($"Could not resolve {missing.Name ?? "app"} ({missing.AppId}){wanted}: {missing.Reason}, "
                + "and no successful build of this solution or of a Public or Read-only solution has it.");
        }
        if (lines.Count > 0) logs.Add(new PendingLog(null, "Symbols", string.Join("\n", lines)));
        return fromFeeds;
    }

    /// <summary>
    /// One pass over Microsoft's public symbol feeds: logs a line per package
    /// fetched, adds what it fetched to <paramref name="resolved"/>, and returns
    /// what could not be found. Never throws except on cancellation.
    /// </summary>
    private async Task<IReadOnlyList<UnresolvedSymbol>> ResolveFromSymbolFeedsAsync(
        IReadOnlyList<SymbolDependency> dependencies, string symbolsDir, IReadOnlySet<string> provided,
        string applicationVersion, string country, List<string> lines, List<ResolvedSymbolPackage> resolved, CancellationToken ct)
    {
        SymbolFeedOutcome outcome;
        try
        {
            outcome = await _symbolFeeds.ResolveAsync(
                new SymbolFeedRequest(dependencies, symbolsDir, provided, applicationVersion, country), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The resolver promises not to throw; this keeps that promise from
            // being the only thing between a feed bug and a sunk build.
            _logger.LogWarning(ex, "Symbol feed resolution failed; the build continues with the symbols it already has.");
            lines.Add($"Could not look up dependencies on the public symbol feeds: {ex.Message}");
            return [];
        }

        foreach (var package in outcome.Resolved)
        {
            lines.Add($"Resolved {package.Name} {package.Version} from the {package.Feed}{(package.FromCache ? " (cached)" : string.Empty)}.");
        }
        resolved.AddRange(outcome.Resolved);
        return outcome.Unresolved;
    }

    /// <summary>Copies any third-party symbols the repos committed under <c>.alpackages/</c> into the symbol dir.</summary>
    private static void CopyCommittedSymbols(IReadOnlyList<string> cloneDirs, string symbolsDir)
    {
        foreach (var cloneDir in cloneDirs)
        {
            foreach (var pkgDir in Directory.EnumerateDirectories(cloneDir, ".alpackages", SearchOption.AllDirectories))
            {
                foreach (var app in Directory.EnumerateFiles(pkgDir, "*.app", SearchOption.TopDirectoryOnly))
                {
                    var dest = Path.Combine(symbolsDir, Path.GetFileName(app));
                    if (!File.Exists(dest)) File.Copy(app, dest);
                }
            }
        }
    }

    /// <summary>
    /// Ensures a non-deleted first-party Release exists for the resolved artifact
    /// (so the project Release's <c>ParentReleaseId</c> can point at it), importing
    /// it inline from the already-downloaded zips when absent. Best-effort: a failed
    /// parent import logs and returns null rather than sinking the project build.
    /// </summary>
    private async Task<int?> EnsureParentReleaseAsync(ResolvedArtifact resolved, BcArtifactDownload download, CancellationToken ct)
    {
        var existing = await _db.OeReleases.AsNoTracking()
            .Where(r => r.DedupKey == resolved.DedupKey && r.DeletedAt == null)
            .Select(r => new { r.Id, r.Status, r.ImportedAt, r.BcVersion })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        // A next-version build parents onto the catalogue's preview of that
        // Major.Minor, the one the daily sweep imports (#999), and refreshes it
        // by the sweep's own rule: older than PreviewRefreshAge, a different
        // insider build, and not mid-ingest. The org keeps one preview per
        // Major.Minor and country whether or not it has opted into the sweep.
        int? stalePreviewId = null;
        if (existing is not null)
        {
            // A failed preview is replaced straight away rather than after the
            // refresh age: it holds no objects to parent onto.
            var refresh = resolved.IsPrerelease
                && existing.Status != "ingesting"
                && (existing.Status == "failed"
                    || (!string.Equals(existing.BcVersion, resolved.Version, StringComparison.OrdinalIgnoreCase)
                        && _clock.GetUtcNow().UtcDateTime - existing.ImportedAt >= ArtifactReleaseImporter.PreviewRefreshAge));
            if (!refresh) return existing.Id;
            stalePreviewId = existing.Id;
        }

        try
        {
            var metadata = new ReleaseImportMetadata(
                Label: resolved.Label, Kind: "first_party", ParentReleaseId: null, ApplicationVersionId: null,
                DedupKey: resolved.DedupKey, IsPrerelease: resolved.IsPrerelease);
            int parentId;
            if (stalePreviewId is null)
            {
                parentId = await _importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);
            }
            else
            {
                // One transaction, so a refused replacement (the quota guard) leaves
                // the org the preview it had - the same rule the daily sweep keeps.
                await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                var stale = await _db.OeReleases.FirstAsync(r => r.Id == stalePreviewId.Value, ct).ConfigureAwait(false);
                stale.DeletedAt = stale.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                parentId = await _importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("Replaced preview release {OldReleaseId} with build {Version} for a project build.",
                    stalePreviewId.Value, resolved.Version);
            }

            var openedStreams = new List<Stream>();
            System.IO.Compression.ZipArchive? appArchive = null;
            System.IO.Compression.ZipArchive? platArchive = null;
            try
            {
                List<AppFileUpload> uploads;
                (uploads, appArchive) = ReleaseZipStaging.OpenBcArtifactZip(download.ApplicationZipPath, isPlatform: false, openedStreams);
                if (download.PlatformZipPath is not null)
                {
                    var (platUploads, archive) = ReleaseZipStaging.OpenBcArtifactZip(download.PlatformZipPath, isPlatform: true, openedStreams);
                    platArchive = archive;
                    uploads = uploads.Concat(platUploads).ToList();
                }
                await _importer.ProcessReleaseAsync(parentId, uploads, storeSymbolReference: false, ct).ConfigureAwait(false);
            }
            finally
            {
                foreach (var s in openedStreams) { try { s.Dispose(); } catch { /* swallow */ } }
                appArchive?.Dispose();
                platArchive?.Dispose();
            }

            _logger.LogInformation("Auto-imported parent BC release {Label} (release {ParentId}) for a project build.", resolved.Label, parentId);
            return parentId;
        }
        catch (Exception ex)
        {
            // A concurrent first-party/artifact import (independent of the
            // single-reader project-build worker) may have won the unique-
            // dedup_key insert, surfacing here as a Postgres 23505. Re-query: if a
            // good parent release now exists, adopt it rather than losing the
            // cross-release link by returning null. See issue #431.
            _db.ChangeTracker.Clear();
            // A preview whose ingest just failed is not adopted: the next build
            // replaces it, and until then the build carries on without a parent.
            var adopted = await _db.OeReleases.AsNoTracking()
                .Where(r => r.DedupKey == resolved.DedupKey && r.DeletedAt == null)
                .Where(r => !resolved.IsPrerelease || r.Status != "failed")
                .Select(r => (int?)r.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (adopted is not null)
            {
                _logger.LogInformation(
                    "Adopted concurrently-created parent BC release {Label} (release {ParentId}) for a project build.",
                    resolved.Label, adopted);
                return adopted;
            }

            _logger.LogError(ex,
                "Failed to auto-import the parent BC release {Label}; the project build continues without cross-release resolution.",
                resolved.Label);
            return null;
        }
    }

    /// <summary>
    /// Ensures a <c>third_party</c> Release exists for each package the symbol
    /// feeds resolved, and returns their ids for the project Release to link.
    /// One Release per (app id, version), found again by its dedup key, so every
    /// build that resolves the same vendor version shares it - the way the
    /// Microsoft parent is shared. It is parented onto this build's Microsoft
    /// Release so the vendor's own references into Base App resolve. A package
    /// whose ingest fails is left out and the build carries on; one whose earlier
    /// ingest failed is not linked until someone retries it from its manage page.
    /// See <c>.design/object-explorer-project-builds.md</c> ("Ingest the resolved
    /// symbols").
    /// </summary>
    private async Task<IReadOnlyList<int>> EnsureVendorReleasesAsync(
        IReadOnlyList<ResolvedSymbolPackage> packages, string symbolsDir, int? parentReleaseId,
        List<PendingLog> logs, CancellationToken ct)
    {
        var ids = new List<int>();
        var lines = new List<string>();
        foreach (var package in packages)
        {
            ct.ThrowIfCancellationRequested();
            var id = await EnsureVendorReleaseAsync(package, symbolsDir, parentReleaseId, lines, ct).ConfigureAwait(false);
            if (id is { } found && !ids.Contains(found)) ids.Add(found);
        }
        if (lines.Count > 0) logs.Add(new PendingLog(null, "Symbols", string.Join("\n", lines)));
        return ids;
    }

    /// <summary>The dedup key a vendor symbols Release is found again by: one per (app id, version) per organisation.</summary>
    internal static string VendorDedupKey(string appId, string version) =>
        $"symbols:{NormalizeAppId(appId)}:{version.Trim()}";

    private async Task<int?> EnsureVendorReleaseAsync(
        ResolvedSymbolPackage package, string symbolsDir, int? parentReleaseId, List<string> lines, CancellationToken ct)
    {
        var dedupKey = VendorDedupKey(package.AppId, package.Version);
        var label = $"{package.Name} {package.Version} (symbols)";
        var existing = await FindVendorReleaseAsync(dedupKey, ct).ConfigureAwait(false);
        if (existing is not null) return Adopt(existing.Value, label, lines);

        byte[] bytes;
        AppManifest? manifest;
        try
        {
            bytes = await File.ReadAllBytesAsync(Path.Combine(symbolsDir, Path.GetFileName(package.FileName)), ct).ConfigureAwait(false);
            using var probe = new MemoryStream(bytes, writable: false);
            manifest = AppPackageReader.TryReadManifest(probe);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read the resolved symbols package {File} for ingest.", package.FileName);
            lines.Add($"Could not add {label} to the Object Explorer: the package could not be read.");
            return null;
        }
        // A stored upload written after the feed could have replaced the file;
        // only ingest what the feed actually resolved.
        if (manifest is null
            || NormalizeAppId(manifest.AppId.ToString()) != NormalizeAppId(package.AppId)
            || !string.Equals(manifest.Version, package.Version, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"Did not add {label} to the Object Explorer: the package in the build is not the one the feed supplied.");
            return null;
        }

        try
        {
            var metadata = new ReleaseImportMetadata(
                Label: label, Kind: "third_party", ParentReleaseId: parentReleaseId, ApplicationVersionId: null,
                Publisher: manifest.Publisher, DedupKey: dedupKey);
            var vendorId = await _importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes, writable: false);
            await _importer.ProcessReleaseAsync(
                vendorId, [new AppFileUpload(Path.GetFileName(package.FileName), stream, SourceZipStream: null)],
                storeSymbolReference: false, ct).ConfigureAwait(false);
            _logger.LogInformation("Ingested vendor symbols {Label} (release {ReleaseId}) for a project build.", label, vendorId);
            lines.Add($"Added {label} to the Object Explorer.");
            return vendorId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Same race as the parent (#431): another build may have won the
            // unique dedup-key insert. Adopt its Release if one now exists.
            _db.ChangeTracker.Clear();
            var adopted = await FindVendorReleaseAsync(dedupKey, ct).ConfigureAwait(false);
            if (adopted is not null) return Adopt(adopted.Value, label, lines);

            _logger.LogWarning(ex, "Failed to ingest vendor symbols {Label}; the build continues without them.", label);
            lines.Add($"Could not add {label} to the Object Explorer: {ex.Message}");
            return null;
        }
    }

    private async Task<(int Id, string Status)?> FindVendorReleaseAsync(string dedupKey, CancellationToken ct)
    {
        var row = await _db.OeReleases.AsNoTracking()
            .Where(r => r.DedupKey == dedupKey && r.DeletedAt == null)
            .Select(r => new { r.Id, r.Status })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : (row.Id, row.Status);
    }

    private static int? Adopt((int Id, string Status) release, string label, List<string> lines)
    {
        if (release.Status != "failed") return release.Id;
        lines.Add($"{label} is in the Object Explorer but its import failed; retry it there to link it to this build.");
        return null;
    }

    // ── Compile ─────────────────────────────────────────────────────────

    /// <summary>
    /// Compiles one extension with <c>alc</c> against <paramref name="symbolsDir"/>,
    /// returning the output <c>.app</c> path (null on failure) and the captured
    /// compiler output for the build log. The output lands in the symbol dir so
    /// apps later in the order can depend on it.
    /// </summary>
    private async Task<(string? OutFile, string Log)> CompileAsync(DiscoveredApp app, string symbolsDir, AlCompilerInfo compiler, CancellationToken ct)
    {
        var outFile = Path.Combine(symbolsDir, SafeAppFileName(app.Manifest));
        var args = new List<string>(compiler.LeadingArguments)
        {
            "/project:" + app.ProjectDir,
            "/packagecachepath:" + symbolsDir,
            "/out:" + outFile,
        };
        // A net8-targeted compiler needs roll-forward to run on the net10 host.
        var env = compiler.NeedsRollForward
            ? new Dictionary<string, string> { ["DOTNET_ROLL_FORWARD"] = "LatestMajor" }
            : null;

        var result = await _processRunner.RunAsync(new ProcessRunRequest(compiler.FileName, args, app.ProjectDir, env), ct).ConfigureAwait(false);
        // alc writes diagnostics to stdout; keep both streams for the build log.
        var log = string.Join("\n", new[] { result.StdOut, result.StdErr }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        if (result.Succeeded && File.Exists(outFile))
        {
            return (outFile, log);
        }
        _logger.LogWarning("alc failed for {App} (exit {Exit}): {Err}", app.Manifest.Name, result.ExitCode,
            Truncate(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr, 2000));
        return (null, log);
    }

    /// <summary>
    /// The solution's stored symbol packages, identified by their own manifests, for
    /// the missing-dependency report. Only read when a compile has already failed for
    /// want of a package, so a green build never pays for it. A stored file that
    /// cannot be read is skipped - it cannot be the version the build needed either.
    /// </summary>
    private async Task<List<StoredSymbolPackage>> ReadStoredSymbolPackagesAsync(int projectId, CancellationToken ct)
    {
        var rows = await _db.OeProjectSymbols.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .Select(s => new { s.FileName, s.Content })
            .ToListAsync(ct).ConfigureAwait(false);
        var packages = new List<StoredSymbolPackage>(rows.Count);
        foreach (var row in rows)
        {
            var manifest = await AppPackageReader.TryReadManifestAsync(row.Content, ct).ConfigureAwait(false);
            if (manifest is null) continue;
            packages.Add(new StoredSymbolPackage(row.FileName, manifest.AppId, manifest.Publisher, manifest.Name, manifest.Version));
        }
        return packages;
    }

    /// <summary>
    /// What the solution's Business Central environments last reported installed, from
    /// the installed-apps mirror rather than the tenant: the build has no business
    /// holding the customer's credentials, and a mirror a day old still says which
    /// version of a vendor app the customer runs. Empty for a solution with no
    /// connection. See <c>.design/solution-customer-info.md</c>, "Modules".
    /// </summary>
    private async Task<List<InstalledAppFact>> ReadInstalledAppsAsync(int projectId, CancellationToken ct)
    {
        var environments = _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.MissingSince == null)
            .Where(Bc.EnvironmentQueries.NotSoftDeleted);
        var rows = await (
                from e in environments
                join a in _db.OeEnvironmentApps.AsNoTracking() on e.Id equals a.EnvironmentId
                select new { e.Name, e.Type, a.AppId, a.Version })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows
            .Select(r => new InstalledAppFact(r.Name, Bc.BcEnvironmentTypes.IsProduction(r.Type), r.AppId, r.Version))
            .ToList();
    }

    // ── Release finalisation ────────────────────────────────────────────

    /// <summary>
    /// Stamps the project Release's label and parent, and replaces its dependency
    /// links with the vendor Releases this build resolved, so a rebuild that no
    /// longer needs a vendor stops seeing it. Runs before the caller ingests the
    /// compiled apps, because the ingest's reference pass reads the chain.
    /// </summary>
    private async Task FinalizeReleaseAsync(
        int releaseId, string label, int? parentReleaseId, IReadOnlyList<int> dependencyReleaseIds, CancellationToken ct)
    {
        var release = await _db.OeReleases.FirstOrDefaultAsync(r => r.Id == releaseId, ct).ConfigureAwait(false);
        if (release is null) return;
        release.Label = label;
        release.ParentReleaseId = parentReleaseId;
        release.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

        var stale = await _db.OeReleaseDependencies.Where(d => d.ReleaseId == releaseId).ToListAsync(ct).ConfigureAwait(false);
        _db.OeReleaseDependencies.RemoveRange(stale.Where(d => !dependencyReleaseIds.Contains(d.DependencyReleaseId)));
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var dependencyId in dependencyReleaseIds.Where(id => id != releaseId && stale.All(d => d.DependencyReleaseId != id)))
        {
            _db.OeReleaseDependencies.Add(new OeReleaseDependency
            {
                OrganizationId = release.OrganizationId,
                ReleaseId = releaseId,
                DependencyReleaseId = dependencyId,
                CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── Pure helpers (unit-tested) ──────────────────────────────────────

    // The excluded-folder and test-folder rules now live in AppJsonManifestParser,
    // because repository discovery (#629) asks the same questions of a manifest it
    // read over the GitHub API rather than cloned. Kept as forwarders so the build
    // pipeline's own call sites and tests stay put.
    internal static bool IsTestSegment(string segment) => AppJsonManifestParser.IsTestSegment(segment);

    /// <summary>
    /// Walks <paramref name="root"/> for folders containing an <c>app.json</c>,
    /// pruning excluded (<c>.alpackages</c>, <c>.git</c>, …) and test folders during
    /// descent. Returns the project directories (the folders holding app.json).
    /// </summary>
    internal static IReadOnlyList<string> DiscoverAppProjectDirs(string root)
    {
        var results = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (File.Exists(Path.Combine(dir, "app.json"))) results.Add(dir);
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (AppJsonManifestParser.IsExcludedSegment(name) || AppJsonManifestParser.IsTestSegment(name)) continue;
                stack.Push(sub);
            }
        }
        return results;
    }

    /// <summary>Parses an <c>app.json</c> body into a manifest, tolerant of trailing commas / comments. Null when unreadable.</summary>
    internal static AppJsonManifest? ParseManifest(string json) => AppJsonManifestParser.Parse(json);

    /// <summary>The highest <c>application</c> (else <c>platform</c>) Major.Minor any extension requires, or null when none declare one.</summary>
    internal static string? SelectTargetMajorMinor(IEnumerable<AppJsonManifest> manifests)
    {
        Version? best = null;
        foreach (var m in manifests)
        {
            var raw = !string.IsNullOrWhiteSpace(m.Application) ? m.Application : m.Platform;
            if (Version.TryParse(raw, out var v) && (best is null || v > best)) best = v;
        }
        return best is null ? null : $"{best.Major}.{best.Minor}";
    }

    /// <summary>
    /// Orders extensions dependencies-first: an app comes after every app in the set
    /// it (transitively) depends on, so a sibling is compiled before its dependents.
    /// Cycle-safe (best-effort) and stable in input order.
    /// </summary>
    internal static IReadOnlyList<DiscoveredApp> TopologicalOrder(IReadOnlyList<DiscoveredApp> apps) =>
        DependencyOrder.Sort(apps, a => a.Manifest.Id, a => a.Manifest.Dependencies.Select(d => d.Id));

    /// <summary>The git <c>http.extraHeader</c> value carrying basic auth for the provider's PAT.</summary>
    internal static string BasicAuthHeaderValue(RepositoryProvider provider, string pat)
    {
        // Azure DevOps: empty username, PAT as password. GitHub: PAT as the
        // password behind the conventional x-access-token username.
        var raw = provider == RepositoryProvider.GitHub ? $"x-access-token:{pat}" : $":{pat}";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        return $"Authorization: Basic {b64}";
    }

    /// <summary>
    /// Builds the environment for a git invocation that needs the PAT, carrying
    /// the basic-auth header via <c>GIT_CONFIG_COUNT</c>/<c>GIT_CONFIG_KEY_0</c>/
    /// <c>GIT_CONFIG_VALUE_0</c> rather than a <c>-c http.extraHeader=…</c> argv.
    /// The app process is multi-tenant; an argv is visible via the world-readable
    /// <c>/proc/&lt;pid&gt;/cmdline</c>, whereas the environment block isn't.
    /// Always sets <c>GIT_TERMINAL_PROMPT=0</c> so a bad token fails fast. See #430.
    /// </summary>
    private static Dictionary<string, string> GitAuthEnv(RepositoryProvider provider, string pat) => new()
    {
        // Never block on an interactive prompt or a credential helper — the PAT
        // travels in http.extraHeader. A configured helper (manager/cache/store) on
        // the host could otherwise stall the clone indefinitely; disabling it
        // (credential.helper="") plus a low-speed abort turns a hung/unreachable
        // remote into a fast, non-zero failure instead of a forever-hang.
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
        ["GIT_CONFIG_COUNT"] = "4",
        ["GIT_CONFIG_KEY_0"] = "http.extraHeader",
        ["GIT_CONFIG_VALUE_0"] = BasicAuthHeaderValue(provider, pat),
        ["GIT_CONFIG_KEY_1"] = "credential.helper",
        ["GIT_CONFIG_VALUE_1"] = "",
        ["GIT_CONFIG_KEY_2"] = "http.lowSpeedLimit",
        ["GIT_CONFIG_VALUE_2"] = "1000",
        ["GIT_CONFIG_KEY_3"] = "http.lowSpeedTime",
        ["GIT_CONFIG_VALUE_3"] = "60",
    };

    /// <summary>
    /// Parses the build's stored selection (a JSON array of app-id strings) into a
    /// normalised set. Returns <c>null</c> for a null/blank/invalid value — meaning
    /// "build everything", the default and the back-compat behaviour for resumed or
    /// migration-synthesised builds.
    /// </summary>
    internal static IReadOnlySet<string>? ParseSelectedAppIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(json);
            if (ids is null) return null;
            return ids.Select(NormalizeAppId).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps only the discovered apps whose manifest id is in <paramref name="selectedIds"/>
    /// (compared on the normalised id). The pure core of the per-build extension
    /// selection, separated so it's unit-testable without a clone.
    /// </summary>
    internal static List<DiscoveredApp> FilterBySelection(IReadOnlyList<DiscoveredApp> discovered, IReadOnlySet<string> selectedIds) =>
        discovered.Where(d => selectedIds.Contains(NormalizeAppId(d.Manifest.Id))).ToList();

    /// <summary>
    /// Canonicalises an app-id GUID for comparison — trims, strips surrounding
    /// braces, and lower-cases — so a selection captured at discovery still matches
    /// the manifest read at build time regardless of brace/case formatting.
    /// </summary>
    internal static string NormalizeAppId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim().Trim('{', '}').ToLowerInvariant();

    /// <summary>
    /// The base symbols are always the project's own country — the base app is
    /// broadly the same across localisations, but some ship extra regulatory
    /// features, so which one to compile against is a per-project decision, not
    /// an org-wide fallback (the old chain fell back to the org's auto-import
    /// country, which is now a multi-country list and no longer names a single
    /// base). New/edited projects require the field; a legacy project that
    /// predates that rule gets a friendly failure telling the user where to set it.
    /// </summary>
    internal static string ResolveCountry(string? projectCountry)
    {
        if (string.IsNullOrWhiteSpace(projectCountry))
        {
            throw new InvalidOperationException(
                "This project doesn't have a country code yet. Set one under the project's " +
                "settings (e.g. 'dk', or 'w1' for the worldwide base) so the right Microsoft " +
                "symbols can be resolved, then rebuild.");
        }
        return projectCountry.Trim().ToLowerInvariant();
    }

    private static AppJsonManifest? TryReadManifest(string projectDir)
    {
        try { return ParseManifest(File.ReadAllText(Path.Combine(projectDir, "app.json"))); }
        catch { return null; }
    }

    private static string SafeAppFileName(AppJsonManifest m)
    {
        var stem = $"{m.Publisher}_{m.Name}_{m.Version}";
        foreach (var c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(stem.Replace("_", ""))) stem = m.Id.Length > 0 ? m.Id : Guid.NewGuid().ToString("N");
        return stem + ".app";
    }

    /// <summary>
    /// Strips any accidental occurrence of the PAT from a tool's output before
    /// it's stored/logged. Redacts the raw PAT and the base64 basic-auth forms
    /// it's actually carried as in git config (GitHub <c>x-access-token:pat</c>,
    /// Azure <c>:pat</c>), in case a verbose git error echoes the header value.
    /// Defense-in-depth: the PAT lives in git config, not the URL, so it
    /// shouldn't reach these streams in the first place (#434). The compiler and
    /// commit-capture invocations never receive the PAT, so their output can't
    /// carry it — only git transport (clone / ls-remote) is routed through here.
    /// </summary>
    private static string Sanitize(string text, IReadOnlyList<CloneCredential> credentials)
    {
        foreach (var credential in credentials) text = Sanitize(text, credential.Secret);
        return text;
    }

    private static string Sanitize(string text, string secret)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(secret)) return text;
        text = text.Replace(secret, "***");
        foreach (var form in new[] { $"x-access-token:{secret}", $":{secret}" })
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(form));
            text = text.Replace(b64, "***");
        }
        return text;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex)
        {
            // Best-effort, but observable: a failed cleanup leaves cloned
            // project source + downloaded symbols on the shared temp volume,
            // which should be visible rather than silently accumulating. #435
            _logger.LogWarning(ex,
                "Failed to clean up the project build directory {BuildRoot}; cloned source and downloaded symbols may remain on the temp volume.",
                path);
        }
    }

    /// <summary>A stored operator upload, read before the feeds run so its app id is never fetched over it.</summary>
    private sealed record SupplementalSymbol(string FileName, byte[] Content);

    /// <summary>A captured log section accumulated during a build, before it's persisted as a <see cref="OeProjectBuildLog"/>.</summary>
    internal sealed record PendingLog(int? RepoId, string Section, string Content);

    /// <summary>A compiled deliverable held in memory, before it's persisted as a <see cref="OeProjectBuildArtifact"/>.</summary>
    private sealed record PendingArtifact(string FileName, string? AppId, string AppName, string AppVersion, string? Runtime, byte[] Content);

    /// <summary>One parsed compiler diagnostic with its path already made repository-relative, before it becomes a row.</summary>
    private sealed record PendingDiagnostic(int? RepoId, string RelativePath, AlcDiagnostic Diagnostic);
}

/// <summary>
/// What makes a build something other than "clone every repository's default
/// branch as the person who pressed the button".
///
/// <para>Only the pull-request gate (#627) passes anything but
/// <see cref="Manual"/>: it names the repository under review, the commit to
/// check out, and the installation token to clone with, because a webhook build
/// has no user whose token could be used. Keeping it one record rather than
/// three more parameters means a future caller that needs one of them does not
/// widen the signature for everyone.</para>
/// </summary>
/// <param name="RepositoryId">The project repository the head commit belongs to; every other repository keeps its default branch.</param>
/// <param name="HeadSha">The commit to check that repository out at.</param>
/// <param name="InstallationToken">The GitHub installation token to clone with, in place of a per-user token.</param>
/// <param name="Target">Which Business Central version to compile against; <see cref="BcBuildTarget.Current"/> is the version the manifests ask for.</param>
public sealed record ProjectBuildOptions(
    int? RepositoryId = null,
    string? HeadSha = null,
    string? InstallationToken = null,
    BcBuildTarget Target = BcBuildTarget.Current)
{
    /// <summary>The ordinary build: default branches, the acting user's own repository tokens.</summary>
    public static readonly ProjectBuildOptions Manual = new();
}

/// <summary>A discovered extension: the folder holding its app.json, the parsed manifest, and the repo it came from.</summary>
public sealed record DiscoveredApp(string ProjectDir, AppJsonManifest Manifest, ClonedRepo Repo);

/// <summary>
/// One extension surfaced by the live discovery clone for the "New build" picker:
/// its app-id (the stable selector persisted on the build), display fields, and the
/// repository it came from. Carries no file paths or bytes — it's a UI choice list.
/// </summary>
public sealed record DiscoveredExtension(
    string AppId,
    string Name,
    string Publisher,
    string Version,
    string RepoUrl,
    string RepoDisplayName);

/// <summary>
/// A successfully cloned repository plus the commit it's pinned at — provenance
/// carried onto each built app and persisted as a <see cref="OeProjectBuildRepoCommit"/>.
/// <see cref="RepositoryId"/> / <see cref="DisplayName"/> identify the source
/// <see cref="OeProjectRepository"/> so the per-repo changelog and build record link back.
/// </summary>
public sealed record ClonedRepo(string Dir, string Url, string? CommitSha, DateTime? CommitDate, int? RepositoryId = null, string DisplayName = "");

/// <summary>One extension's outcome from a build, before it's persisted as a <see cref="OeProjectBuildResult"/> row. Carries the source provenance (repo + commit) when known.</summary>
public sealed record BuildAppResult(
    string AppName,
    string AppId,
    string Status,
    string? Message,
    string? RepoUrl = null,
    string? CommitSha = null,
    DateTime? CommitDate = null);

/// <summary>
/// The product of a project build: the compiled uploads ready for the shared
/// ingest seam, the per-app report, the resolved parent release (when known), the
/// finalised Release label, and the resolved BC Major.Minor the build compiled
/// against (stamped onto the <see cref="OeProjectBuild"/>).
/// </summary>
public sealed record ProjectBuildOutcome(
    IReadOnlyList<AppFileUpload> Uploads,
    IReadOnlyList<BuildAppResult> Results,
    int? ParentReleaseId,
    string? FinalLabel,
    string? BcVersion = null);

/// <summary>One parsed changelog commit (short hash, author, committer date, subject).</summary>
public sealed record ChangelogEntry(string ShortHash, string Author, DateTime? CommittedAt, string Subject);
