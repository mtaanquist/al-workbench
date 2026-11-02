using System.ComponentModel;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// MCP tools over the Artifacts surface — the agent-facing parallel of the
/// Projects/Pipelines web tools. Agents can list projects and pipelines (a pipeline
/// is a named build flow under a project), list a pipeline's or project's builds
/// (with the per-repo commit set and changelog), inspect one build's deliverables
/// and logs, and compare two of a project's builds at the object level. The
/// compiled <c>.app</c> bytes never travel through MCP (they can be tens of MB);
/// each deliverable carries a <c>DownloadPath</c> the agent shares with the user,
/// who appends it to the app's base URL and fetches it via the streaming endpoint
/// — the same pattern as <c>download_symbol_reference</c>. All reads are org-scoped
/// by the EF query filter, and project-scoped by <see cref="ProjectAccess"/>: a
/// Private project the caller has no grant on is absent from <c>list_solutions</c>
/// (a locked name is no use to an agent) and unresolvable by every other tool
/// here. See <c>.design/artifacts.md</c> and <c>.design/teams-and-visibility.md</c>.
/// </summary>
[McpServerToolType]
public sealed class ArtifactsTools
{
    /// <summary>Row cap on <c>compare_solution_builds</c>: the 200 the Object Explorer compare tools use (#1228).</summary>
    private const int MaxCompareRows = 200;

    /// <summary>How many compiler errors <c>get_solution_build</c> spells out; the rest are counted in <c>ErrorCount</c>.</summary>
    internal const int MaxBuildErrors = 10;

    /// <summary>How many changelog commits per repository <c>get_solution_build</c> spells out; the rest are counted in a summary row.</summary>
    internal const int MaxChangelogCommits = 20;

    private const int DefaultSolutionLimit = 50;
    private const int DefaultPipelineLimit = 25;

    private readonly ArtifactService _artifacts;
    private readonly ReleaseComparisonService _comparison;
    private readonly ProjectService _projects;

    public ArtifactsTools(ArtifactService artifacts, ReleaseComparisonService comparison, ProjectService projects)
    {
        _artifacts = artifacts;
        _comparison = comparison;
        _projects = projects;
    }

    [McpServerTool(Name = "list_solutions", ReadOnly = true)]
    [Description("Lists the solutions you can see in the organisation — each points at one or more Git repositories that get compiled into downloadable .app files. Returns each solution's id, name, short name (the abbreviation used in extension names, may be empty), owner, repository count, a summary of its newest build (status, BC version), its visibility ('Public', 'ReadOnly' or 'Private'), and its last production delivery (when it last reached the customer's production environment: finish time, build id, deployment pipeline id, and status - 'deployed', or 'handed_off' when Business Central accepted it to install in a later update window; null when it never has). Private solutions you are not on the team for are not listed. Returns the first 50 unless you raise limit; Truncated and Note say whether more were left out, so pass search to narrow it. Use the id with list_solution_builds. To read or search a solution's own code, pass the releaseId of one of its ready builds (prefer one with isPreview false, compiled against the version the customer runs) to the Object Explorer tools (search_objects, get_object_outline, search_content and the rest); list_releases with the solution's name as search finds its newest build directly.")]
    public async Task<CappedRows<ProjectArtifactsRow>> ListProjectsAsync(
        [Description("Optional substring to filter by solution name, short name, owner, or repository name.")] string? search = null,
        [Description(McpCatalogueLimit.Description + " Default 50.")] int limit = DefaultSolutionLimit,
        CancellationToken ct = default)
    {
        var rows = await _artifacts.ListProjectsAsync(search, ct);
        // The web list keeps a locked, name-only row so a project doesn't appear
        // to vanish for a human reading /projects. An agent has no such
        // confusion to spare — drop them entirely.
        var visible = rows.Where(r => !r.IsLocked).ToList();
        return CappedRows<ProjectArtifactsRow>.From(visible, McpCatalogueLimit.Clamp(limit), "solutions", "ordered by name",
            "Pass search (a customer, short name or repository name) to narrow the list.",
            CappedRows<ProjectArtifactsRow>.RaiseLimitHint);
    }

    [McpServerTool(Name = "list_solution_builds", ReadOnly = true)]
    [Description("Lists a solution's builds, newest first. Each build is a compile of the solution's repositories at a point in time; returns its id, status ('queued'/'building'/'ready'/'failed'), BC version, timings, who started it, the number of downloadable .app files, the Object Explorer release id (when ready), and its bcTarget ('current', 'next_minor' or 'next_major'); a build with isPreview true is check-only and can't be deployed. Use a build id with get_solution_build. Returns the newest 20 unless you ask for more with limit, and fewer when the builds carry long messages, so the answer stays readable; Truncated and Note say when more were left out. To read further back, pass the oldest id returned as beforeBuildId. A failure message is cut at 300 characters, and get_solution_build has the whole of it.")]
    public async Task<CappedRows<BuildRow>> ListProjectBuildsAsync(
        [Description("Solution name or numeric id (from list_solutions).")] string solutionNameOrId,
        [Description(McpListLimit.Description)] int limit = McpListLimit.Default,
        [Description(BeforeBuildIdDescription)] int? beforeBuildId = null,
        CancellationToken ct = default)
    {
        var projectId = await _projects.ResolveProjectAsync(solutionNameOrId, ct);
        var cap = McpListLimit.Clamp(limit);
        return CapBuilds(await _artifacts.ListBuildsForProjectAsync(projectId, cap + 1, ct, beforeBuildId), cap);
    }

    private const string BeforeBuildIdDescription =
        "Optional build id from this list: returns the builds older than it, for reading further back than the newest 50. An id that is not in this list returns nothing.";

    [McpServerTool(Name = "list_pipelines", ReadOnly = true)]
    [Description("Lists the pipelines you can see in the organisation. A pipeline is a named build flow under a solution that compiles a chosen subset of the solution's extensions (a solution can have several). Returns each pipeline's id, name, its solution, owner, a summary of its newest build (status, BC version), and its nightly preview check: previewCheck says whether it is on, previewChecks holds the newest result per upcoming version (bcTarget 'next_minor' or 'next_major', outcome 'passed'/'failed'/'running', the exact bcArtifactVersion, and the buildId to pass to get_solution_build for the errors), and previewCheckBlocked says why the check is paused, when it is. autoVersion says whether its builds add their build number to the third part of each app's version (28.2.0.0 in app.json is built as 28.2.4812.0 in build 4812), so the versions in its builds differ from app.json by design. Preview check builds are check-only and can't be deployed; the newest build summary leaves them out. disabled is true when someone disabled the pipeline: it does not build, on request or on its own, until a person enables it again in the web UI. Pipelines under a private solution you are not on the team for are not listed. Returns the first 25 unless you raise limit; Truncated and Note say whether more were left out, so pass search to narrow it. Use the id with list_pipeline_builds.")]
    public async Task<CappedRows<PipelineArtifactsRow>> ListPipelinesAsync(
        [Description("Optional substring to filter by pipeline name, solution name, or owner.")] string? search = null,
        [Description(McpCatalogueLimit.Description + " Default 25.")] int limit = DefaultPipelineLimit,
        CancellationToken ct = default)
    {
        var rows = await _artifacts.ListPipelinesAsync(search, ct);
        return CappedRows<PipelineArtifactsRow>.From(rows, McpCatalogueLimit.Clamp(limit), "pipelines", "ordered by solution",
            "Pass search (a solution or pipeline name) to narrow the list.",
            CappedRows<PipelineArtifactsRow>.RaiseLimitHint);
    }

    [McpServerTool(Name = "list_pipeline_builds", ReadOnly = true)]
    [Description("Lists one pipeline's builds, newest first. Each build is a run of the pipeline — a compile of its chosen extensions at a point in time; returns its id, status ('queued'/'building'/'ready'/'failed'), BC version (bcArtifactVersion is the exact Business Central build it compiled against), timings, who started it, the number of downloadable .app files, the Object Explorer release id (when ready), and its bcTarget ('current', 'next_minor' or 'next_major'). A build with isPreview true is one of the nightly preview check's builds, compiled against an upcoming Business Central version to check for breaking changes: it is never published as a GitHub release and can't be deployed. Use a build id with get_solution_build. Returns the newest 20 unless you ask for more with limit, and fewer when the builds carry long messages, so the answer stays readable; Truncated and Note say when more were left out. To read further back, pass the oldest id returned as beforeBuildId. A failure message is cut at 300 characters, and get_solution_build has the whole of it.")]
    public async Task<CappedRows<BuildRow>> ListPipelineBuildsAsync(
        [Description("Pipeline id (from list_pipelines).")] int pipelineId,
        [Description(McpListLimit.Description)] int limit = McpListLimit.Default,
        [Description(BeforeBuildIdDescription)] int? beforeBuildId = null,
        CancellationToken ct = default)
    {
        try
        {
            var cap = McpListLimit.Clamp(limit);
            return CapBuilds(await _artifacts.ListBuildsAsync(pipelineId, cap + 1, ct: ct, beforeBuildId: beforeBuildId), cap);
        }
        catch (ProjectAccessDeniedException)
        {
            throw NotFound($"Pipeline {pipelineId} was not found in this organisation.");
        }
    }

    [McpServerTool(Name = "get_solution_build", ReadOnly = true)]
    [Description("Returns one build's full detail: the per-repository commit it was built from, the changelog since the solution's last successful build (grouped by repository), and the downloadable deliverables. Each deliverable and the whole-build zip and raw log carry a DownloadPath the user appends to the app's base URL to fetch (the bytes are not returned inline). When the build is ready it also returns the Object Explorer release id so its objects can be searched/compared. bcTarget says which Business Central version it compiled against ('current', 'next_minor' or 'next_major') and bcArtifactVersion the exact build; a build with isPreview true is check-only and can't be deployed. A build can be 'ready' with some extensions missing: failedApps lists each extension that did not build and why (needsSymbols is true when the reason is a dependency the solution has to supply on its Symbols tab), so a ready build is only clean when failedApps is empty. errorCount and warningCount are the compiler's totals, and errors holds the first few compiler errors, in the order the compiler reported them, with their repository, file, line, code and message (errorCount says how many there are in all).")]
    public async Task<ProjectBuildDetailResult> GetProjectBuildAsync(
        [Description("Build id (from list_solution_builds).")] int buildId,
        CancellationToken ct = default)
    {
        BuildDetail? detail;
        try
        {
            // Only whether a log exists is reported, so its text stays in the database.
            detail = await _artifacts.GetBuildDetailAsync(buildId, ct, includeLogText: false);
        }
        catch (ProjectAccessDeniedException)
        {
            detail = null;
        }
        if (detail is null) throw NotFound($"Build {buildId} was not found in this organisation.");

        // A build goes ready even when some extensions failed (#1330), so the agent
        // needs the failures and the errors behind them, not just the status.
        var errors = detail.ErrorCount > 0
            ? await _artifacts.ListBuildErrorsAsync(buildId, MaxBuildErrors, ct)
            : [];

        var apps = detail.Artifacts
            .Select(a => new BuildAppDownload(
                a.FileName, a.AppName, a.AppVersion, a.RuntimeVersion, a.SizeBytes,
                DownloadPath: $"/artifacts/build/{buildId}/app/{a.Id}",
                UnchangedSinceBuildId: a.CarriedFromBuildId))
            .ToList();

        return new ProjectBuildDetailResult(
            BuildId: detail.Id,
            SolutionId: detail.ProjectId,
            SolutionName: detail.ProjectName,
            PipelineId: detail.PipelineId,
            PipelineName: detail.PipelineName,
            Status: detail.Status,
            BcVersion: detail.BcVersion,
            StartedAt: detail.StartedAt,
            FinishedAt: detail.FinishedAt,
            FailureMessage: detail.FailureMessage,
            StartedByName: detail.StartedByName,
            ReleaseId: detail.ReleaseId,
            RepoCommits: detail.RepoCommits,
            Changelog: CapChangelog(detail.Changelog),
            Apps: apps,
            DownloadAllPath: apps.Count > 0 ? $"/artifacts/build/{buildId}/all" : null,
            RawLogPath: detail.Logs.Count > 0 ? $"/artifacts/build/{buildId}/log" : null,
            BcTarget: detail.BcTarget,
            BcArtifactVersion: detail.BcArtifactVersion,
            ErrorCount: detail.ErrorCount,
            WarningCount: detail.WarningCount,
            FailedApps: detail.FailedApps ?? [],
            Errors: errors);
    }

    [McpServerTool(Name = "compare_solution_builds", ReadOnly = true)]
    [Description("Diffs two of the SAME solution's builds at the object level (added / removed / modified / unchanged), so you can see what objects changed between two compiles. Both builds must be 'ready'; nightly preview check builds hold compile results only and cannot be compared. Capped at 200 rows ordered by kind, then id; Truncated and Note say whether anything was left out, and kind / namePattern narrow it. This is deliberately solution-scoped — use compare_releases for Microsoft/third-party releases.")]
    public async Task<ObjectCompareResult> CompareProjectBuildsAsync(
        [Description("First (earlier / base) build id.")] int baseBuildId,
        [Description("Second (later) build id.")] int otherBuildId,
        [Description("When true (default), omit unchanged objects and return only added / removed / modified.")] bool changesOnly = true,
        [Description("Optional AL kind to compare only ('table', 'page', 'codeunit', ...). Null compares every kind.")] string? kind = null,
        [Description("Optional case-insensitive substring of the object name. Null compares every object.")] string? namePattern = null,
        CancellationToken ct = default)
    {
        var (leftProject, leftRelease) = await _projects.ResolveReadyBuildAsync(baseBuildId, ct);
        var (rightProject, rightRelease) = await _projects.ResolveReadyBuildAsync(otherBuildId, ct);
        if (leftProject != rightProject)
        {
            throw new McpException(
                "Both builds must belong to the same solution. compare_solution_builds compares builds within one solution; use compare_releases for cross-release diffs.");
        }

        var set = await _comparison.CompareReleaseObjectsAsync(
            leftRelease, rightRelease, MaxCompareRows, includeUnchanged: !changesOnly, kind, namePattern, ct);
        return ObjectCompareResult.From(set, MaxCompareRows);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// The one refusal shape these tools use. A project the caller may not see
    /// and an id that doesn't exist answer identically on purpose — a distinct
    /// "denied" would confirm the project is there. See
    /// <c>.design/teams-and-visibility.md</c>.
    /// </summary>
    private static McpException NotFound(string message) => new(message);

    /// <summary>
    /// A build list's rows, fetched one past <paramref name="cap"/>, with long messages cut
    /// and the list stopped at the row cap or the answer budget, whichever comes first
    /// (#1572): fifty builds, each with a GitHub release error, ran past what an assistant reads.
    /// </summary>
    private static CappedRows<BuildRow> CapBuilds(IEnumerable<BuildRow> rows, int cap)
    {
        var shortened = rows
            .Select(r => r with { FailureMessage = McpListLimit.ShortenFailure(r.FailureMessage, "get_solution_build") })
            .ToList();
        return CappedRows<BuildRow>.From(shortened, cap, "builds", "newest first",
            "Pass the oldest build's id as beforeBuildId to read the rest.",
            cap < McpListLimit.Max ? CappedRows<BuildRow>.RaiseLimitHint : null);
    }

    /// <summary>
    /// The first commits of each repository's changelog, with a summary row (no
    /// hash, as the service's own notes have) counting the rest. A long gap
    /// between builds ran to 100 commits per repository (#1511). The service's
    /// own hashless notes are not commits: they are left out of the count and
    /// kept after the summary, and when one is there the stored list was itself
    /// cut, so the count is a lower bound.
    /// </summary>
    internal static IReadOnlyList<ChangelogGroup> CapChangelog(IReadOnlyList<ChangelogGroup> groups) =>
        groups.Select(g =>
        {
            var commits = g.Commits.Where(c => !string.IsNullOrEmpty(c.ShortHash)).ToList();
            if (commits.Count <= MaxChangelogCommits) return g;
            var notes = g.Commits.Where(c => string.IsNullOrEmpty(c.ShortHash)).ToList();
            var more = commits.Count - MaxChangelogCommits;
            var summary = notes.Count == 0
                ? $"{more} more commits are not shown; the build page has the full changelog."
                : $"At least {more} more commits are not shown; the build page lists more of them.";
            return g with
            {
                Commits = commits.Take(MaxChangelogCommits)
                    .Append(new ChangelogRow(string.Empty, summary, string.Empty, null))
                    .Concat(notes)
                    .ToList(),
            };
        }).ToList();
}

/// <summary>One build's detail for the <c>get_solution_build</c> MCP tool, with download paths for its deliverables.</summary>
public sealed record ProjectBuildDetailResult(
    int BuildId,
    int SolutionId,
    string SolutionName,
    int? PipelineId,
    string? PipelineName,
    string Status,
    string? BcVersion,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string? FailureMessage,
    string? StartedByName,
    int? ReleaseId,
    IReadOnlyList<RepoCommitRow> RepoCommits,
    IReadOnlyList<ChangelogGroup> Changelog,
    IReadOnlyList<BuildAppDownload> Apps,
    string? DownloadAllPath,
    string? RawLogPath,
    string BcTarget = ProjectBuildTarget.Current,
    string? BcArtifactVersion = null,
    int ErrorCount = 0,
    int WarningCount = 0,
    IReadOnlyList<FailedAppRow>? FailedApps = null,
    IReadOnlyList<BuildErrorRow>? Errors = null)
{
    /// <summary>True for a build against a preview version: check-only, never deployable or published.</summary>
    public bool IsPreview => ProjectBuildTarget.IsPreview(BcTarget);
}

/// <summary>One downloadable deliverable for an MCP caller — metadata plus the path the user fetches it from.</summary>
public sealed record BuildAppDownload(
    string FileName,
    string AppName,
    string AppVersion,
    string? RuntimeVersion,
    long SizeBytes,
    string DownloadPath,
    /// <summary>
    /// Set when the app had no changes and the build reused the .app from this earlier
    /// build instead of publishing a new version (the pipeline releases and deploys only
    /// extensions that changed). Null for an app this build published itself.
    /// </summary>
    int? UnchangedSinceBuildId = null);
