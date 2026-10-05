using System.Text.Json.Serialization;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// The read surface behind the Artifacts tool (and the Projects tool's latest-build
/// chip): project directories with their newest build, per-project build history,
/// a build's commits / changelog / logs / deliverables, the project-scoped compare
/// picker, and the byte fetches the download endpoints stream. All reads are
/// org-scoped by the EF query filter — these run inside a normal authenticated
/// request. Mutations (create/build/delete) live in <see cref="ProjectService"/> /
/// <see cref="ProjectBuildImporter"/>; this service never writes. See
/// <c>.design/artifacts.md</c>.
/// </summary>
public sealed class ArtifactService
{
    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;

    public ArtifactService(AppDbContext db, ProjectAccess access)
    {
        _db = db;
        _access = access;
    }

    // ── Project directory (Projects + Artifacts browsers) ───────────────

    /// <summary>
    /// Active projects with owner, repo count, and a summary of their newest
    /// pipeline build, ordered by name. Optionally filtered by a name/owner/repo
    /// substring. Drives both the Projects directory and the Artifacts landing.
    /// </summary>
    public async Task<List<ProjectArtifactsRow>> ListProjectsAsync(string? search = null, CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);

        // A Private project the caller has no grant on still gets a row, so it
        // doesn't simply vanish and leave them wondering — but the row carries the
        // name and nothing else. Owner, build status, and counts all report
        // activity on the customer, which is the thing being hidden.
        var locked = await _db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Where(ProjectAccess.LockedProjectPredicate(snapshot))
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);

        var projects = await _db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Where(ProjectAccess.VisibleProjectPredicate(snapshot))
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.ShortName,
                p.Slug,
                p.Visibility,
                OwnerName = p.CreatedByUser != null ? p.CreatedByUser.DisplayName : null,
                RepoCount = p.Repositories.Count,
                RepoNames = p.Repositories.Select(r => r.DisplayName).ToList(),
            })
            .ToListAsync(ct);

        // The newest build per project in one query, plus the newest *successful*
        // one (the "Download all" target). Bounded per org, so the in-memory join
        // is cheap and keeps the projection simple.
        //
        // Pipeline builds only. A solution has other build rows - a pull-request
        // build the GitHub App started, a release imported from GitHub - and none
        // of them is what the list's "Latest build" column means: the state of
        // the deliverable a pipeline produces. A solution with no pipeline has no
        // build status at all, however many pull requests have been checked. Preview
        // builds are left out for the same reason: a red next-major check is not the
        // state of what the customer gets, and one can never be deployed (#994).
        var projectIds = projects.Select(p => p.Id).ToList();
        var builds = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => projectIds.Contains(b.ProjectId) && b.PipelineId != null && b.BcTarget == ProjectBuildTarget.Current)
            .Select(b => new
            {
                b.Id, b.ProjectId, b.Status, b.BcVersion, b.Branch, b.DefaultBranch, b.StartedAt, b.FinishedAt,
                ArtifactCount = b.Artifacts.Count,
            })
            .ToListAsync(ct);
        var byProject = builds.GroupBy(b => b.ProjectId).ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.StartedAt).ToList());

        // One representative commit per latest build for the list's "latest build"
        // cell. Builds are multi-repo, so show the first repo's commit (by display
        // name, matching the detail page's ordering).
        var latestBuildIds = byProject.Values.Where(l => l.Count > 0).Select(l => l[0].Id).ToList();
        var commitByBuild = (await _db.OeProjectBuildRepoCommits.AsNoTracking()
                .Where(c => latestBuildIds.Contains(c.ProjectBuildId))
                .OrderBy(c => c.RepoDisplayName)
                .Select(c => new { c.ProjectBuildId, c.CommitHash })
                .ToListAsync(ct))
            .GroupBy(c => c.ProjectBuildId)
            .ToDictionary(g => g.Key, g => g.First().CommitHash);

        // The newest time each solution reached its customer's production environment, in
        // one query rather than one per row. "Reached" is the two terminal successes:
        // deployed, and handed off - Business Central accepted the upload for a later
        // window, which is as far as a scheduled delivery is ever watched. Production is
        // the deployment pipeline's environment's type, compared the way
        // BcEnvironmentTypes.IsProduction does, which EF cannot translate. A delivery
        // whose deployment pipeline was later removed still happened, so it still counts.
        // See .design/solution-customer-info.md.
        var shipped = (await _db.OeProjectDeliveries.AsNoTracking()
                .Where(d => projectIds.Contains(d.ProjectId)
                    && (d.Status == ProjectDeliveryStatus.Deployed || d.Status == ProjectDeliveryStatus.HandedOff)
                    && d.FinishedAt != null
                    && d.ReleasePipeline!.ProjectEnvironment!.Type.Trim().ToUpper() == "PRODUCTION")
                .GroupBy(d => d.ProjectId)
                .Select(g => g
                    .OrderByDescending(d => d.FinishedAt).ThenByDescending(d => d.Id)
                    .Select(d => new
                    {
                        d.ProjectId,
                        FinishedAt = d.FinishedAt!.Value,
                        d.ProjectBuildId,
                        d.ReleasePipelineId,
                        d.Status,
                        ReleasePipelineRemoved = d.ReleasePipeline!.DeletedAt != null,
                    })
                    .First())
                .ToListAsync(ct))
            .ToDictionary(d => d.ProjectId, d => new DeliverySummary(
                d.FinishedAt, d.ProjectBuildId, d.ReleasePipelineId, d.Status, d.ReleasePipelineRemoved));

        var knownDefaults = await KnownDefaultBranchesAsync(projectIds, ct);

        var rows = new List<ProjectArtifactsRow>(projects.Count);
        foreach (var p in projects)
        {
            byProject.TryGetValue(p.Id, out var pb);
            var latest = pb is { Count: > 0 } ? pb[0] : null;
            var latestSuccessful = pb?.FirstOrDefault(b => b.Status == ProjectBuildStatus.Ready);
            string? commitShort = null;
            if (latest is not null && commitByBuild.TryGetValue(latest.Id, out var hash) && !string.IsNullOrEmpty(hash))
                commitShort = hash.Length > 7 ? hash[..7] : hash;
            rows.Add(new ProjectArtifactsRow(
                p.Id, p.Name, p.ShortName, p.OwnerName, p.RepoCount,
                Latest: latest is null ? null : new BuildSummary(
                    latest.Id, latest.Status, latest.BcVersion, latest.Branch, commitShort, latest.StartedAt, latest.FinishedAt, latest.ArtifactCount,
                    latest.DefaultBranch ?? knownDefaults.GetValueOrDefault(p.Id)),
                LatestSuccessfulBuildId: latestSuccessful?.Id,
                RepoNames: p.RepoNames)
            {
                Visibility = p.Visibility,
                Slug = p.Slug,
                LastProductionDelivery = shipped.GetValueOrDefault(p.Id),
            });
        }

        foreach (var p in locked)
        {
            rows.Add(new ProjectArtifactsRow(
                p.Id, p.Name, ShortName: null, OwnerName: null, RepoCount: 0,
                Latest: null, LatestSuccessfulBuildId: null,
                RepoNames: Array.Empty<string>(),
                IsLocked: true)
            {
                Visibility = ProjectVisibility.Private,
            });
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            rows = rows.Where(r =>
                    r.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (r.ShortName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (r.OwnerName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                    || r.RepoNames.Any(n => n.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The project header (name + owner) for the Artifacts builds page, or null when not found / deleted.</summary>
    public async Task<ProjectHeader?> GetProjectHeaderAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        return await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Select(p => new ProjectHeader(
                p.Id, p.Name,
                p.CreatedByUser != null ? p.CreatedByUser.DisplayName : null,
                p.CreatedByUserId))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The project a <c>project</c>-kind Release belongs to, via the build that
    /// produced it — so a user who deep-linked into an unlisted project release in
    /// the Object Explorer can get back to its Artifacts page. Null when the release
    /// isn't a tracked project build. See <c>.design/artifacts.md</c>.
    /// </summary>
    public async Task<int?> GetProjectIdForReleaseAsync(int releaseId, CancellationToken ct = default)
    {
        var projectId = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ReleaseId == releaseId)
            .Select(b => (int?)b.ProjectId)
            .FirstOrDefaultAsync(ct);
        // The "back to the project" link is itself a fact about the project, so a
        // caller who can't see it doesn't get one.
        if (projectId is { } id && !await _access.CanViewAsync(id, ct)) return null;
        return projectId;
    }

    // ── Pipeline directory (Pipelines landing) ──────────────────────────

    /// <summary>
    /// Active pipelines with their project, owner, and a summary of their newest
    /// build, ordered by project then pipeline name. Optionally filtered by a
    /// pipeline/project/owner substring. Drives the Pipelines landing.
    /// </summary>
    public async Task<List<PipelineArtifactsRow>> ListPipelinesAsync(string? search = null, CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);

        // Pipelines inherit their project's visibility; a locked project has no
        // locked pipeline row, it simply isn't listed here.
        var pipelines = await _db.OePipelines.AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Where(p => _db.OeProjects.Where(visible).Any(v => v.Id == p.ProjectId))
            .Select(p => new
            {
                p.Id, p.Name, p.ProjectId, p.PreviewCheck, p.PreviewCheckBlocked, p.AutoVersion,
                ProjectName = p.Project!.Name,
                OwnerName = p.Project.CreatedByUser != null ? p.Project.CreatedByUser.DisplayName : null,
            })
            .ToListAsync(ct);

        // The newest build per pipeline (and the newest successful one) in one query.
        // The nightly preview check's builds are left out: the pipeline's state is
        // what it builds for real, and the check has its own results below.
        var pipelineIds = pipelines.Select(p => p.Id).ToList();
        var builds = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId != null && pipelineIds.Contains(b.PipelineId!.Value)
                        && b.BcTarget == ProjectBuildTarget.Current)
            .Select(b => new
            {
                b.Id, PipelineId = b.PipelineId!.Value, b.ProjectId, b.Status, b.BcVersion, b.Branch, b.DefaultBranch, b.StartedAt, b.FinishedAt,
                ArtifactCount = b.Artifacts.Count,
            })
            .ToListAsync(ct);
        var byPipeline = builds.GroupBy(b => b.PipelineId).ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.StartedAt).ToList());

        var latestBuildIds = byPipeline.Values.Where(l => l.Count > 0).Select(l => l[0].Id).ToList();
        var commitByBuild = (await _db.OeProjectBuildRepoCommits.AsNoTracking()
                .Where(c => latestBuildIds.Contains(c.ProjectBuildId))
                .OrderBy(c => c.RepoDisplayName)
                .Select(c => new { c.ProjectBuildId, c.CommitHash })
                .ToListAsync(ct))
            .GroupBy(c => c.ProjectBuildId)
            .ToDictionary(g => g.Key, g => g.First().CommitHash);

        var knownDefaults = await KnownDefaultBranchesAsync(pipelines.Select(p => p.ProjectId).Distinct().ToList(), ct);

        var previewChecks = await LatestPreviewChecksAsync(
            pipelines.Where(p => p.PreviewCheck).Select(p => p.Id).ToList(), ct);

        var rows = new List<PipelineArtifactsRow>(pipelines.Count);
        foreach (var p in pipelines)
        {
            byPipeline.TryGetValue(p.Id, out var pb);
            var latest = pb is { Count: > 0 } ? pb[0] : null;
            var latestSuccessful = pb?.FirstOrDefault(b => b.Status == ProjectBuildStatus.Ready);
            string? commitShort = null;
            if (latest is not null && commitByBuild.TryGetValue(latest.Id, out var hash) && !string.IsNullOrEmpty(hash))
                commitShort = hash.Length > 7 ? hash[..7] : hash;
            rows.Add(new PipelineArtifactsRow(
                p.Id, p.Name, p.ProjectId, p.ProjectName, p.OwnerName,
                Latest: latest is null ? null : new BuildSummary(
                    latest.Id, latest.Status, latest.BcVersion, latest.Branch, commitShort, latest.StartedAt, latest.FinishedAt, latest.ArtifactCount,
                    latest.DefaultBranch ?? knownDefaults.GetValueOrDefault(p.ProjectId)),
                LatestSuccessfulBuildId: latestSuccessful?.Id,
                PreviewCheck: p.PreviewCheck,
                PreviewChecks: previewChecks.GetValueOrDefault(p.Id, []),
                PreviewCheckBlocked: p.PreviewCheck ? p.PreviewCheckBlocked : null,
                AutoVersion: p.AutoVersion));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            rows = rows.Where(r =>
                    r.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || r.ProjectName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (r.OwnerName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        return rows
            .OrderBy(r => r.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The pipeline header (name + project + owner, and the nightly preview check's
    /// latest results) for the pipeline detail page, or null when not found / deleted.
    /// </summary>
    public async Task<PipelineHeader?> GetPipelineHeaderAsync(int pipelineId, CancellationToken ct = default)
    {
        await EnsureCanViewPipelineAsync(pipelineId, ct);
        var header = await _db.OePipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId && p.DeletedAt == null)
            .Select(p => new PipelineHeader(
                p.Id, p.Name, p.ProjectId, p.Project!.Name,
                p.Project.CreatedByUser != null ? p.Project.CreatedByUser.DisplayName : null,
                p.Project.CreatedByUserId,
                p.PreviewCheck,
                p.PreviewCheck ? p.PreviewCheckBlocked : null,
                null))
            .FirstOrDefaultAsync(ct);
        if (header is null || !header.PreviewCheck) return header;

        var checks = await LatestPreviewChecksAsync([pipelineId], ct);
        return header with { PreviewChecks = checks.GetValueOrDefault(pipelineId, []) };
    }

    /// <summary>
    /// The newest nightly preview check build per preview target, for each of
    /// <paramref name="pipelineIds"/>, in <see cref="ProjectBuildTarget.Previews"/>
    /// order. A target that has never been checked is simply missing. The caller has
    /// already gated the pipelines on visibility.
    /// </summary>
    private async Task<Dictionary<int, IReadOnlyList<PreviewCheckResult>>> LatestPreviewChecksAsync(
        IReadOnlyCollection<int> pipelineIds, CancellationToken ct)
    {
        if (pipelineIds.Count == 0) return [];

        var builds = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId != null && pipelineIds.Contains(b.PipelineId.Value)
                        && b.BcTarget != ProjectBuildTarget.Current)
            .Select(b => new
            {
                b.Id, PipelineId = b.PipelineId!.Value, b.BcTarget, b.Status, b.ReleaseId,
                b.BcArtifactVersion, b.StartedAt, b.FinishedAt,
            })
            .ToListAsync(ct);
        var latest = builds
            .GroupBy(b => (b.PipelineId, b.BcTarget))
            .Select(g => g.OrderByDescending(b => b.StartedAt).ThenByDescending(b => b.Id).First())
            .ToList();

        // A build with some failed extensions still goes ready, so "passed" has to
        // look at the per-extension results too.
        var releaseIds = latest.Where(b => b.ReleaseId != null).Select(b => b.ReleaseId!.Value).ToList();
        var failedByRelease = (await _db.OeProjectBuildResults.AsNoTracking()
                .Where(r => releaseIds.Contains(r.ReleaseId) && r.Status == ProjectBuildResultStatus.Failed)
                .GroupBy(r => r.ReleaseId)
                .Select(g => new { ReleaseId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(r => r.ReleaseId, r => r.Count);

        return latest
            .GroupBy(b => b.PipelineId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<PreviewCheckResult>)g
                    .OrderBy(b => ProjectBuildTarget.Previews.ToList().IndexOf(b.BcTarget))
                    .Select(b => new PreviewCheckResult(
                        b.BcTarget, b.Id, b.Status, b.BcArtifactVersion, b.StartedAt, b.FinishedAt,
                        b.ReleaseId is { } r ? failedByRelease.GetValueOrDefault(r) : 0))
                    .ToList());
    }

    // ── Build history + detail ──────────────────────────────────────────

    /// <summary>One pipeline's builds, newest first — the pipeline detail page's build history.</summary>
    public async Task<List<BuildRow>> ListBuildsAsync(int pipelineId, CancellationToken ct = default)
    {
        await EnsureCanViewPipelineAsync(pipelineId, ct);
        return await ListBuildsCoreAsync(_db.OeProjectBuilds.AsNoTracking().Where(b => b.PipelineId == pipelineId), ct);
    }

    /// <summary>
    /// The apps (name + version) each of a pipeline's builds would install, keyed by
    /// build id. Metadata only — never the <c>.app</c> bytes. The release dialog shows
    /// this so a consultant can see what is about to go into a customer's tenant
    /// before confirming. See <c>.design/saas-delivery.md</c>.
    /// </summary>
    public async Task<Dictionary<int, List<BuildAppRow>>> ListBuildAppsAsync(int pipelineId, CancellationToken ct = default)
    {
        await EnsureCanViewPipelineAsync(pipelineId, ct);
        var rows = await _db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.ProjectBuild!.PipelineId == pipelineId)
            .OrderBy(a => a.AppName)
            .Select(a => new { a.ProjectBuildId, a.AppName, a.AppVersion })
            .ToListAsync(ct);

        return rows
            .GroupBy(a => a.ProjectBuildId)
            .ToDictionary(g => g.Key, g => g.Select(a => new BuildAppRow(a.AppName, a.AppVersion)).ToList());
    }

    /// <summary>All of a project's builds across its pipelines, newest first — the MCP <c>list_solution_builds</c> surface.</summary>
    public async Task<List<BuildRow>> ListBuildsForProjectAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        return await ListBuildsCoreAsync(_db.OeProjectBuilds.AsNoTracking().Where(b => b.ProjectId == projectId), ct);
    }

    /// <summary>
    /// One build as the history lists it, or null when it isn't in the acting org.
    /// Gated on its solution's visibility, like every other build read here — used by
    /// the agent-facing staging tool, which has a build id and needs the row back.
    /// </summary>
    public async Task<BuildRow?> GetBuildRowAsync(int buildId, CancellationToken ct = default)
    {
        var projectId = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.Id == buildId)
            .Select(b => (int?)b.ProjectId)
            .FirstOrDefaultAsync(ct);
        if (projectId is not { } pid) return null;
        await _access.EnsureCanViewAsync(pid, ct);

        var rows = await ListBuildsCoreAsync(_db.OeProjectBuilds.AsNoTracking().Where(b => b.Id == buildId), ct);
        return rows.FirstOrDefault();
    }

    private async Task<List<BuildRow>> ListBuildsCoreAsync(IQueryable<OeProjectBuild> filtered, CancellationToken ct)
    {
        var builds = await filtered
            .OrderByDescending(b => b.StartedAt)
            .Select(b => new
            {
                b.Id, b.ProjectId, b.ReleaseId, b.Status, b.BcVersion, b.Branch, b.DefaultBranch, b.Trigger,
                b.StartedAt, b.FinishedAt, b.FailureMessage,
                b.GithubReleaseTag, b.GithubReleaseUrl, b.GithubReleaseError,
                b.BcTarget, b.BcArtifactVersion,
                StartedByName = b.StartedByUser != null ? b.StartedByUser.DisplayName : null,
                ArtifactCount = b.Artifacts.Count,
            })
            .ToListAsync(ct);

        var buildIds = builds.Select(b => b.Id).ToList();
        var knownDefaults = await KnownDefaultBranchesAsync(builds.Select(b => b.ProjectId).Distinct().ToList(), ct);

        // The changelog ("what changed since the last successful build") names each
        // row and its size drives the "+N more" hint. A first build / a build with
        // no new commits has only a summary note here (empty hash, message only).
        var changelog = await _db.OeProjectBuildCommits.AsNoTracking()
            .Where(c => buildIds.Contains(c.ProjectBuildId))
            .Select(c => new { c.ProjectBuildId, c.ShortHash, c.Message, c.Ordering })
            .ToListAsync(ct);
        var changelogByBuild = changelog
            .GroupBy(c => c.ProjectBuildId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Ordering).ToList());

        // The build's pinned commit (what it was built *at*) — shown when the
        // changelog is just a note, so every real build still surfaces a hash.
        // Representative = first repo by display name, matching the landing + hero.
        var pinnedByBuild = (await _db.OeProjectBuildRepoCommits.AsNoTracking()
                .Where(c => buildIds.Contains(c.ProjectBuildId))
                .OrderBy(c => c.RepoDisplayName)
                .Select(c => new { c.ProjectBuildId, c.CommitHash })
                .ToListAsync(ct))
            .GroupBy(c => c.ProjectBuildId)
            .ToDictionary(g => g.Key, g => g.First().CommitHash);

        return builds.Select(b =>
        {
            changelogByBuild.TryGetValue(b.Id, out var cl);
            // Real commits only (a summary note has an empty hash). When there are
            // new commits, the head names the row so hash + message come from the
            // same commit; otherwise fall back to the build's pinned commit + note.
            var realCommits = cl?.Where(c => !string.IsNullOrEmpty(c.ShortHash)).ToList() ?? [];
            var head = realCommits.Count > 0 ? realCommits[0] : null;

            string? shortHash;
            string? message;
            if (head is not null)
            {
                shortHash = head.ShortHash;
                message = head.Message;
            }
            else
            {
                pinnedByBuild.TryGetValue(b.Id, out var pinned);
                shortHash = string.IsNullOrEmpty(pinned) ? null : (pinned.Length > 7 ? pinned[..7] : pinned);
                message = cl?.FirstOrDefault()?.Message; // the summary note, if any
            }

            return new BuildRow(
                b.Id, b.ReleaseId, b.Status, b.BcVersion, b.Branch,
                b.StartedAt, b.FinishedAt, b.FailureMessage, b.StartedByName, b.ArtifactCount,
                HeadCommitShort: shortHash,
                HeadCommitMessage: string.IsNullOrEmpty(message) ? null : message,
                CommitCount: realCommits.Count,
                GitHubReleaseTag: b.GithubReleaseTag,
                GitHubReleaseUrl: b.GithubReleaseUrl,
                GitHubReleaseError: b.GithubReleaseError,
                BcTarget: b.BcTarget,
                BcArtifactVersion: b.BcArtifactVersion,
                // Older builds of the default branch didn't record which branch that
                // was; the repositories' default branch as known now stands in.
                DefaultBranch: b.Branch is not null || b.Trigger == ProjectBuildTrigger.PullRequest
                    ? null
                    : b.DefaultBranch ?? knownDefaults.GetValueOrDefault(b.ProjectId));
        }).ToList();
    }

    /// <summary>
    /// Each solution's default branch as GitHub last reported it on a push, for builds
    /// made before a build recorded its own. Several names, comma-separated, when the
    /// solution's repositories differ; a solution nobody has pushed to since is absent.
    /// </summary>
    private async Task<Dictionary<int, string>> KnownDefaultBranchesAsync(IReadOnlyCollection<int> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0) return new();
        var heads = await _db.OeRepositoryBranchHeads.AsNoTracking()
            .Where(h => h.IsDefaultBranch && h.DeletedAt == null && projectIds.Contains(h.ProjectRepository!.ProjectId))
            .Select(h => new { h.ProjectRepository!.ProjectId, h.Branch })
            .ToListAsync(ct);
        return heads
            .GroupBy(h => h.ProjectId)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(h => h.Branch).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
    }

    /// <summary>True while any of the pipeline's builds is still queued or building — drives the live status poll.</summary>
    public async Task<bool> HasBuildInFlightAsync(int pipelineId, CancellationToken ct = default)
    {
        await EnsureCanViewPipelineAsync(pipelineId, ct);
        return await _db.OeProjectBuilds.AsNoTracking()
            .AnyAsync(b => b.PipelineId == pipelineId
                           && (b.Status == ProjectBuildStatus.Queued || b.Status == ProjectBuildStatus.Building), ct);
    }

    /// <summary>
    /// One build's full detail: the per-repo commit set, the changelog grouped by
    /// repo, the deliverables (metadata only — no bytes), and the log sections.
    /// Null when the build isn't in the acting org.
    /// </summary>
    public async Task<BuildDetail?> GetBuildDetailAsync(int buildId, CancellationToken ct = default)
    {
        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.Id == buildId)
            .Select(b => new
            {
                b.Id, b.ProjectId, b.PipelineId, b.ReleaseId, b.Status, b.BcVersion, b.Branch,
                b.StartedAt, b.FinishedAt, b.FailureMessage, b.BcTarget, b.BcArtifactVersion,
                StartedBy = b.StartedByUser != null ? b.StartedByUser.DisplayName : null,
                ProjectName = b.Project != null ? b.Project.Name : string.Empty,
                PipelineName = b.Pipeline != null ? b.Pipeline.Name : null,
            })
            .FirstOrDefaultAsync(ct);
        if (build is null) return null;
        await _access.EnsureCanViewAsync(build.ProjectId, ct);

        var repoCommits = await _db.OeProjectBuildRepoCommits.AsNoTracking()
            .Where(c => c.ProjectBuildId == buildId)
            .OrderBy(c => c.RepoDisplayName)
            .Select(c => new RepoCommitRow(c.RepoDisplayName, c.RepoUrl, c.CommitHash, c.CommittedAt))
            .ToListAsync(ct);

        var changelog = await _db.OeProjectBuildCommits.AsNoTracking()
            .Where(c => c.ProjectBuildId == buildId)
            .OrderBy(c => c.Ordering)
            .Select(c => new { c.ProjectRepositoryId, c.ShortHash, c.Message, c.Author, c.CommittedAt })
            .ToListAsync(ct);

        // Group the changelog by repo using the repo commit set's display names so
        // the UI can show "changes in <repo>". A repo id we no longer have a name
        // for (repo removed since) falls back to a generic label.
        var repoNamesById = await _db.OeProjectBuildRepoCommits.AsNoTracking()
            .Where(c => c.ProjectBuildId == buildId && c.ProjectRepositoryId != null)
            .Select(c => new { c.ProjectRepositoryId, c.RepoDisplayName })
            .ToListAsync(ct);
        var nameLookup = repoNamesById
            .GroupBy(x => x.ProjectRepositoryId!.Value)
            .ToDictionary(g => g.Key, g => g.First().RepoDisplayName);

        var changelogGroups = changelog
            .GroupBy(c => c.ProjectRepositoryId)
            .Select(g => new ChangelogGroup(
                g.Key is { } rid && nameLookup.TryGetValue(rid, out var nm) ? nm : "Repository",
                g.Select(c => new ChangelogRow(c.ShortHash, c.Message, c.Author, c.CommittedAt)).ToList()))
            .OrderBy(g => g.RepoName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var artifacts = await ListArtifactRowsAsync(buildId, ct);

        var logSections = await _db.OeProjectBuildLogs.AsNoTracking()
            .Where(l => l.ProjectBuildId == buildId)
            .OrderBy(l => l.Ordering)
            .Select(l => new LogSectionRow(l.Section, l.Content))
            .ToListAsync(ct);

        // Compiler diagnostics are parsed into rows for every build (#627), so the
        // card can say "3 errors, 12 warnings" instead of leaving the reader to
        // scan the raw log for them.
        var diagnosticCounts = await _db.OeProjectBuildDiagnostics.AsNoTracking()
            .Where(d => d.ProjectBuildId == buildId)
            .GroupBy(d => d.Severity)
            .Select(g => new { Severity = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var errorCount = diagnosticCounts
            .Where(c => c.Severity == ProjectBuildDiagnosticSeverity.Error).Sum(c => c.Count);
        var warningCount = diagnosticCounts
            .Where(c => c.Severity == ProjectBuildDiagnosticSeverity.Warning).Sum(c => c.Count);

        // The extensions that did not build, with the reason the build recorded. A
        // build with some of these still goes ready, so without them a partial
        // build reads as a clean one to anyone who never opens the log.
        var failedApps = new List<FailedAppRow>();
        if (build.ReleaseId is { } releaseId)
        {
            var failed = await _db.OeProjectBuildResults.AsNoTracking()
                .Where(r => r.ReleaseId == releaseId && r.Status == ProjectBuildResultStatus.Failed)
                .OrderBy(r => r.AppName)
                .Select(r => new { r.AppName, r.Message })
                .ToListAsync(ct);
            failedApps = failed
                .Select(r => new FailedAppRow(
                    r.AppName,
                    r.Message,
                    MissingDependencyReport.NamesMissingDependency(r.Message)))
                .ToList();
        }

        return new BuildDetail(
            build.Id, build.ProjectId, build.ProjectName, build.PipelineId, build.PipelineName,
            build.ReleaseId, build.Status,
            build.BcVersion, build.Branch, build.StartedAt, build.FinishedAt, build.FailureMessage,
            build.StartedBy, repoCommits, changelogGroups, artifacts, logSections,
            errorCount, warningCount, failedApps, build.BcTarget, build.BcArtifactVersion);
    }

    /// <summary>The deliverables of a build (metadata only), ordered by file name.</summary>
    public async Task<List<ArtifactRow>> ListArtifactRowsAsync(int buildId, CancellationToken ct = default)
    {
        await EnsureCanViewBuildAsync(buildId, ct);
        return await _db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.ProjectBuildId == buildId)
            .OrderBy(a => a.FileName)
            .Select(a => new ArtifactRow(a.Id, a.FileName, a.AppName, a.AppVersion, a.RuntimeVersion, a.SizeBytes))
            .ToListAsync(ct);
    }

    // ── Project-scoped compare ──────────────────────────────────────────

    /// <summary>
    /// The builds of one pipeline that can be compared — those that produced a
    /// navigable Release (ready, with a ReleaseId), newest first. The picker is
    /// deliberately pipeline-scoped: the global Object Explorer compare never lists
    /// project builds. See <c>.design/artifacts.md</c>.
    /// </summary>
    public async Task<List<ComparableBuildRow>> ListComparableBuildsAsync(int pipelineId, CancellationToken ct = default)
    {
        await EnsureCanViewPipelineAsync(pipelineId, ct);
        return await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId == pipelineId && b.Status == ProjectBuildStatus.Ready && b.ReleaseId != null)
            .OrderByDescending(b => b.StartedAt)
            .Select(b => new ComparableBuildRow(b.Id, b.ReleaseId!.Value, b.BcVersion, b.StartedAt, b.BcTarget))
            .ToListAsync(ct);
    }

    // ── Download byte fetches (endpoints) ───────────────────────────────

    /// <summary>One deliverable's bytes for streaming, or null when it isn't in the acting org / build.</summary>
    public async Task<DownloadFile?> GetArtifactBytesAsync(int buildId, int artifactId, CancellationToken ct = default)
    {
        await EnsureCanViewBuildAsync(buildId, ct);
        return await _db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.Id == artifactId && a.ProjectBuildId == buildId)
            .Select(a => new DownloadFile(a.FileName, a.Content))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Every deliverable's bytes for a build's "Download all" zip. Empty when the build has none.</summary>
    public async Task<List<DownloadFile>> GetAllArtifactBytesAsync(int buildId, CancellationToken ct = default)
    {
        await EnsureCanViewBuildAsync(buildId, ct);
        return await _db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.ProjectBuildId == buildId)
            .OrderBy(a => a.FileName)
            .Select(a => new DownloadFile(a.FileName, a.Content))
            .ToListAsync(ct);
    }

    /// <summary>The build's concatenated raw log for the <c>Raw log</c> download, or null when the build has none.</summary>
    public async Task<RawLog?> GetRawLogAsync(int buildId, CancellationToken ct = default)
    {
        await EnsureCanViewBuildAsync(buildId, ct);
        var exists = await _db.OeProjectBuilds.AsNoTracking().AnyAsync(b => b.Id == buildId, ct);
        if (!exists) return null;

        var sections = await _db.OeProjectBuildLogs.AsNoTracking()
            .Where(l => l.ProjectBuildId == buildId)
            .OrderBy(l => l.Ordering)
            .Select(l => new { l.Section, l.Content })
            .ToListAsync(ct);

        var text = string.Join("\n\n", sections.Select(s => $"=== {s.Section} ===\n{s.Content}"));
        return new RawLog($"build-{buildId}-log.txt", text);
    }

    // ── View gates for pipeline- and build-keyed reads ──────────────────
    //
    // Visibility is a property of the project; a pipeline, a build, and a build's
    // bytes all inherit it. These resolve the owning project and defer to the one
    // authority. A row that doesn't exist passes the gate — the read below returns
    // nothing on its own, and refusing here would confirm an id that isn't in this
    // org. See .design/teams-and-visibility.md.

    private async Task EnsureCanViewPipelineAsync(int pipelineId, CancellationToken ct)
    {
        var projectId = await _db.OePipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId)
            .Select(p => (int?)p.ProjectId)
            .FirstOrDefaultAsync(ct);
        if (projectId is { } id) await _access.EnsureCanViewAsync(id, ct);
    }

    private async Task EnsureCanViewBuildAsync(int buildId, CancellationToken ct)
    {
        var projectId = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.Id == buildId)
            .Select(b => (int?)b.ProjectId)
            .FirstOrDefaultAsync(ct);
        if (projectId is { } id) await _access.EnsureCanViewAsync(id, ct);
    }
}

// ── DTOs ────────────────────────────────────────────────────────────────

/// <summary>A project row for the Projects / Artifacts directories: identity, owner, repo count, and its newest build.</summary>
/// <remarks>
/// A <see cref="IsLocked">locked</see> row is a Private project the viewer has no
/// grant on: only <see cref="Name"/> is filled in, every other field is empty, and
/// the list renders it greyed and unclickable. See
/// <c>.design/teams-and-visibility.md</c>.
/// </remarks>
public sealed record ProjectArtifactsRow(
    int Id,
    string Name,
    string? ShortName,
    string? OwnerName,
    int RepoCount,
    BuildSummary? Latest,
    int? LatestSuccessfulBuildId,
    IReadOnlyList<string> RepoNames,
    bool IsLocked = false)
{
    /// <summary>
    /// Who may see and change the solution: <c>Public</c>, <c>ReadOnly</c> or <c>Private</c>.
    /// A locked row is always <c>Private</c> - that is why it is locked. Written as its name
    /// so an assistant reading <c>list_solutions</c> sees a word, not a number.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ProjectVisibility>))]
    public ProjectVisibility Visibility { get; init; } = ProjectVisibility.Public;

    /// <summary>
    /// The newest delivery that reached the customer's production environment, or null
    /// when nothing has. Empty on a locked row.
    /// </summary>
    public DeliverySummary? LastProductionDelivery { get; init; }

    /// <summary>
    /// The solution's key in its web address. Null on a locked row, which has no page
    /// to link to. Page-only: agents address a solution by name or id.
    /// </summary>
    [JsonIgnore]
    public string? Slug { get; init; }

    /// <summary>The solution's own page.</summary>
    [JsonIgnore]
    public string Href => SolutionLinks.Solution(Slug, Id);
}

/// <summary>
/// One delivery to a production environment, for a directory cell. <see cref="Status"/> is
/// <c>deployed</c> (installed, and seen to be) or <c>handed_off</c> (Business Central
/// accepted it and installs it in a later update window, unobserved by us).
/// <see cref="ReleasePipelineRemoved"/> is true when the deployment pipeline it ran through has
/// since been deleted, so there is no page to link to. <c>list_solutions</c> hands this to
/// agents, so the pipeline fields carry the product's word on the wire.
/// </summary>
public sealed record DeliverySummary(
    DateTime FinishedAt,
    int BuildId,
    [property: System.Text.Json.Serialization.JsonPropertyName("deploymentPipelineId")] int ReleasePipelineId,
    string Status,
    [property: System.Text.Json.Serialization.JsonPropertyName("deploymentPipelineRemoved")] bool ReleasePipelineRemoved = false);

/// <summary>A compact summary of one build for a directory chip.</summary>
/// <param name="DefaultBranch">
/// When <paramref name="Branch"/> is null, the default branch the build was made from
/// (recorded on the build, or for an older build the repositories' default branch as
/// known now). Display only.
/// </param>
public sealed record BuildSummary(int BuildId, string Status, string? BcVersion, string? Branch, string? CommitShort, DateTime StartedAt, DateTime? FinishedAt, int ArtifactCount,
    string? DefaultBranch = null)
{
    /// <summary>The branch to show: the one built, its default branch's name, or a plain "(default branch)".</summary>
    [JsonIgnore]
    public string ShownBranch => Branch ?? DefaultBranch ?? "(default branch)";
}

/// <summary>A project's header for the Artifacts builds page.</summary>
public sealed record ProjectHeader(int Id, string Name, string? OwnerName, int? OwnerUserId);

/// <summary>A pipeline row for the Pipelines landing: identity, its project, owner, and its newest build.</summary>
public sealed record PipelineArtifactsRow(
    int Id,
    string Name,
    int ProjectId,
    string ProjectName,
    string? OwnerName,
    BuildSummary? Latest,
    int? LatestSuccessfulBuildId,
    /// <summary>Whether the pipeline runs the nightly preview check.</summary>
    bool PreviewCheck = false,
    /// <summary>The check's newest result per preview target; empty until the first night.</summary>
    IReadOnlyList<PreviewCheckResult>? PreviewChecks = null,
    /// <summary>Why the check could not start last night, when it couldn't.</summary>
    string? PreviewCheckBlocked = null,
    /// <summary>Whether the pipeline's builds add their build number to each app's version.</summary>
    bool AutoVersion = true);

/// <summary>
/// A pipeline's header for the pipeline detail page (its project + owner drive the
/// breadcrumb and manage-gating), with the nightly preview check's state.
/// </summary>
public sealed record PipelineHeader(int Id, string Name, int ProjectId, string ProjectName, string? OwnerName, int? OwnerUserId,
    bool PreviewCheck = false,
    string? PreviewCheckBlocked = null,
    IReadOnlyList<PreviewCheckResult>? PreviewChecks = null);

/// <summary>
/// The newest build of the nightly preview check for one preview target. See
/// <c>.design/object-explorer-project-builds.md</c>, "The nightly preview check".
/// </summary>
/// <param name="BcTarget"><c>next_minor</c> or <c>next_major</c>.</param>
/// <param name="FailedAppCount">Extensions that did not compile, which fail the check even when the build went ready.</param>
public sealed record PreviewCheckResult(
    string BcTarget,
    int BuildId,
    string Status,
    string? BcArtifactVersion,
    DateTime StartedAt,
    DateTime? FinishedAt,
    int FailedAppCount)
{
    /// <summary><c>running</c>, <c>passed</c> or <c>failed</c>.</summary>
    public string Outcome => Status switch
    {
        ProjectBuildStatus.Queued or ProjectBuildStatus.Building => PreviewCheckOutcome.Running,
        ProjectBuildStatus.Ready when FailedAppCount == 0 => PreviewCheckOutcome.Passed,
        _ => PreviewCheckOutcome.Failed,
    };

    /// <summary>"Next minor" or "Next major".</summary>
    public string Label => ProjectBuildTarget.Label(BcTarget);
}

/// <summary>The words <see cref="PreviewCheckResult.Outcome"/> takes.</summary>
public static class PreviewCheckOutcome
{
    public const string Running = "running";
    public const string Passed = "passed";
    public const string Failed = "failed";
}

/// <summary>One build in the history list.</summary>
/// <remarks>
/// <see cref="HeadCommitShort"/> / <see cref="HeadCommitMessage"/> / <see cref="CommitCount"/>
/// let the history show what changed without opening each build: the head changelog
/// commit when there are new commits, otherwise the build's pinned commit hash plus
/// the summary note ("first build" / "no new commits"). All optional — they default
/// to empty for a build with neither a changelog nor a pinned commit.
/// </remarks>
public sealed record BuildRow(
    int Id,
    int? ReleaseId,
    string Status,
    string? BcVersion,
    string? Branch,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string? FailureMessage,
    string? StartedByName,
    int ArtifactCount,
    string? HeadCommitShort = null,
    string? HeadCommitMessage = null,
    int CommitCount = 0,
    /// <summary>The GitHub Release tag this build was published as, or staged from (#632). Null when neither.</summary>
    string? GitHubReleaseTag = null,
    /// <summary>The Release's page on GitHub, when there is one.</summary>
    string? GitHubReleaseUrl = null,
    /// <summary>Why the build was not published as a Release. The build itself still succeeded.</summary>
    string? GitHubReleaseError = null,
    /// <summary>Which Business Central version the build compiled against: <c>current</c>, <c>next_minor</c> or <c>next_major</c>.</summary>
    string BcTarget = ProjectBuildTarget.Current,
    /// <summary>The exact Business Central build the symbols came from (e.g. <c>29.0.52914.0</c>). Null for builds made before it was recorded.</summary>
    string? BcArtifactVersion = null,
    /// <summary>When <see cref="Branch"/> is null, the default branch the build was made from, as best known. Display only.</summary>
    string? DefaultBranch = null)
{
    /// <summary>The branch to show: the one built, its default branch's name, or a plain "(default branch)".</summary>
    [JsonIgnore]
    public string ShownBranch => Branch ?? DefaultBranch ?? "(default branch)";

    /// <summary>True for a build against a preview version: check-only, never deployable or published.</summary>
    public bool IsPreview => ProjectBuildTarget.IsPreview(BcTarget);
}

/// <summary>One build's full detail for the Artifacts build card.</summary>
public sealed record BuildDetail(
    int Id,
    int ProjectId,
    string ProjectName,
    int? PipelineId,
    string? PipelineName,
    int? ReleaseId,
    string Status,
    string? BcVersion,
    string? Branch,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string? FailureMessage,
    string? StartedByName,
    IReadOnlyList<RepoCommitRow> RepoCommits,
    IReadOnlyList<ChangelogGroup> Changelog,
    IReadOnlyList<ArtifactRow> Artifacts,
    IReadOnlyList<LogSectionRow> Logs,
    int ErrorCount = 0,
    int WarningCount = 0,
    IReadOnlyList<FailedAppRow>? FailedApps = null,
    string BcTarget = ProjectBuildTarget.Current,
    string? BcArtifactVersion = null)
{
    /// <summary>True for a build against a preview version: check-only, never deployable or published.</summary>
    public bool IsPreview => ProjectBuildTarget.IsPreview(BcTarget);
}

/// <summary>
/// One extension a build could not produce, and why. <see cref="NeedsSymbols"/> is
/// set when the reason is a dependency the solution has to supply, which is what
/// puts a way to the solution's Symbols tab beside it.
/// </summary>
public sealed record FailedAppRow(string AppName, string? Message, bool NeedsSymbols);

/// <summary>One repository's pinned commit for a build.</summary>
public sealed record RepoCommitRow(string RepoName, string RepoUrl, string CommitHash, DateTime? CommittedAt);

/// <summary>The changelog for one repository within a build.</summary>
public sealed record ChangelogGroup(string RepoName, IReadOnlyList<ChangelogRow> Commits);

/// <summary>One changelog commit (or a summary note when <see cref="ShortHash"/> is empty).</summary>
public sealed record ChangelogRow(string ShortHash, string Message, string Author, DateTime? CommittedAt);

/// <summary>One downloadable deliverable's metadata.</summary>
public sealed record ArtifactRow(int Id, string FileName, string AppName, string AppVersion, string? RuntimeVersion, long SizeBytes);

/// <summary>One app a build produced — its display name and version.</summary>
public sealed record BuildAppRow(string AppName, string AppVersion);

/// <summary>One captured log section.</summary>
public sealed record LogSectionRow(string Section, string Content);

/// <summary>A build eligible for project-scoped comparison.</summary>
/// <param name="BcTarget">Which version it compiled against; the picker labels preview builds and does not preselect them.</param>
public sealed record ComparableBuildRow(int BuildId, int ReleaseId, string? BcVersion, DateTime StartedAt,
    string BcTarget = ProjectBuildTarget.Current);

/// <summary>A file's bytes ready to stream from a download endpoint.</summary>
public sealed record DownloadFile(string FileName, byte[] Content);

/// <summary>A build's raw log ready to stream.</summary>
public sealed record RawLog(string FileName, string Content);
