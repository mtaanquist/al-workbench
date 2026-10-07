using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Import;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

namespace ALDevToolbox.Services.ObjectExplorer.Explore;

/// <summary>
/// Read-only query API over the <c>oe_*</c> schema: the release / module /
/// object browse surfaces, the forward-edge MCP lookups, the object outline,
/// and procedure source/calls. The outline enriches interface objects with
/// their implementers via <see cref="ReferenceQueryService"/>.
///
/// The other read surfaces have their own focused services:
/// <see cref="ReferenceQueryService"/> (find-references / dependencies /
/// interface implementers), <see cref="SourceViewerService"/> (source-file
/// content / outline / navigation), <see cref="ObjectSearchService"/>
/// (cross-module search), <see cref="ReleaseComparisonService"/> (release
/// diffs), and <see cref="TranslationQueryService"/> (translations).
///
/// All methods are <c>AsNoTracking</c> and respect the tenant query filter
/// on <see cref="AppDbContext"/>.
/// </summary>
public class ObjectExplorerService
{
    private readonly AppDbContext _db;
    private readonly ReferenceQueryService _references;
    private readonly ProjectAccess _access;
    private readonly ILogger<ObjectExplorerService> _logger;

    public ObjectExplorerService(AppDbContext db, ReferenceQueryService references, ProjectAccess access, ILogger<ObjectExplorerService> logger)
    {
        _db = db;
        _references = references;
        _access = access;
        _logger = logger;
    }

    // ── Project-visibility fence ────────────────────────────────────────
    // A Release produced by (or imported under) a Private project is only for
    // that project's people. Every read below that names a release — or a
    // module, object, or symbol inside one — resolves the release and asks the
    // one authority. Denied reads return the same "nothing here" the caller
    // gets for an id in another org, so a Private project is never confirmed to
    // exist. See ProjectAccess.IsReleaseVisibleAsync and
    // .design/teams-and-visibility.md.

    private Task<bool> ReleaseVisibleAsync(int releaseId, CancellationToken ct)
        => _access.IsReleaseVisibleAsync(releaseId, ct);

    private async Task<bool> ModuleVisibleAsync(long moduleId, CancellationToken ct)
    {
        var releaseId = await _db.OeModules.AsNoTracking()
            .Where(m => m.Id == moduleId)
            .Select(m => (int?)m.ReleaseId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return releaseId is null || await ReleaseVisibleAsync(releaseId.Value, ct).ConfigureAwait(false);
    }

    private async Task<bool> ObjectVisibleAsync(long objectId, CancellationToken ct)
    {
        var releaseId = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Id == objectId)
            .Select(o => (int?)o.Module!.ReleaseId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return releaseId is null || await ReleaseVisibleAsync(releaseId.Value, ct).ConfigureAwait(false);
    }

    private async Task<bool> SymbolVisibleAsync(long symbolId, CancellationToken ct)
    {
        var releaseId = await _db.OeModuleSymbols.AsNoTracking()
            .Where(sym => sym.Id == symbolId)
            .Select(sym => (int?)sym.Object!.Module!.ReleaseId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return releaseId is null || await ReleaseVisibleAsync(releaseId.Value, ct).ConfigureAwait(false);
    }

    // ── Releases ────────────────────────────────────────────────────────

    /// <summary>
    /// Lists active Releases visible to the current org. Failed and
    /// in-progress Releases come along — the picker UI badges them
    /// distinctly but admins still need to see them.
    /// <para>
    /// <c>project</c>-kind Releases (one per project build) are <em>excluded</em> by
    /// default: there can be thousands, and they belong to the Artifacts tool, not
    /// the global Object Explorer release list or its org-wide compare picker. They
    /// stay reachable by direct id (<c>/object-explorer/release/{id}</c>) and through
    /// the project-scoped Artifacts compare. Pass
    /// <paramref name="includeProjectBuilds"/> only for a genuinely project-scoped
    /// caller. See <c>.design/artifacts.md</c>.
    /// </para>
    /// <para>
    /// <paramref name="browsableOnly"/> narrows the list to what the releases page and
    /// the compare picker show: ready releases, without the symbol packages a build
    /// pulled from the feeds with no files in them (#1092). Those stay in the database
    /// so builds can resolve references into them.
    /// </para>
    /// </summary>
    public async Task<List<ReleaseListItem>> ListReleasesAsync(
        bool includeSoftDeleted = false, bool includeProjectBuilds = false, bool browsableOnly = false,
        CancellationToken ct = default)
    {
        var q = _db.OeReleases.AsNoTracking().AsQueryable();
        if (!includeSoftDeleted)
        {
            q = q.Where(r => r.DeletedAt == null);
        }
        if (browsableOnly)
        {
            q = q.Where(r => r.Status == "ready").Where(OeRelease.NotAnEmptySymbolPackage);
        }
        if (!includeProjectBuilds)
        {
            q = q.Where(r => r.Kind != "project");
        }
        else
        {
            // Project releases are the ones that can belong to a Private
            // project; the general list excludes them already.
            var snapshot = await _access.GetSnapshotAsync(ct);
            q = q.Where(_access.VisibleReleasePredicate(snapshot));
        }
        var rows = await q
            .Select(r => new ReleaseListItem(
                r.Id, r.Label, r.Kind, r.Status, r.BcVersion, r.ParentReleaseId,
                ParentLabel: r.ParentRelease != null ? r.ParentRelease.Label : null,
                Publisher: r.Publisher,
                ProjectName: r.ProjectName,
                ImportedAt: r.ImportedAt,
                // Denormalised counters stamped at ingest time. The Releases
                // picker on a busy org used to spend most of its load budget
                // here — a correlated subquery summing LENGTH(content) over
                // multi-thousand-row file tables. ReleaseImportService now
                // pins these once when the Release flips to ready.
                SourceFileCount: r.SourceFileCount,
                SourceContentLength: r.SourceContentLength,
                DeletedAt: r.DeletedAt,
                StatusMessage: r.StatusMessage,
                PipelineName: null,
                IsPrerelease: r.IsPrerelease))
            .ToListAsync(ct);

        // Sort in memory: active rows first, then by BC version descending
        // (so "28.10" sorts above "28.2"), then by ImportedAt descending so
        // a re-import of the same version wins. The list is bounded — a
        // production org caps out at a few dozen releases — so the
        // in-memory sort cost is negligible compared to the row I/O.
        rows.Sort((a, b) =>
        {
            var deletedCmp = (a.DeletedAt == null ? 0 : 1).CompareTo(b.DeletedAt == null ? 0 : 1);
            if (deletedCmp != 0) return deletedCmp;
            var versionCmp = BcVersionComparer.Instance.Compare(b.BcVersion, a.BcVersion);
            if (versionCmp != 0) return versionCmp;
            return b.ImportedAt.CompareTo(a.ImportedAt);
        });
        return rows;
    }

    /// <summary>
    /// The newest ready build's Release for each pipeline — the one exception to
    /// "project builds stay out of the Object Explorer" (see
    /// <see cref="ListReleasesAsync"/>): the Third-party tab on
    /// <c>/object-explorer</c> shows the latest ready build per pipeline so a
    /// pipeline's current objects are findable without going through the
    /// Artifacts tool first. Older builds stay Artifacts-only. One row per
    /// pipeline, <see cref="ReleaseListItem.PipelineName"/> and
    /// <see cref="ReleaseListItem.ProjectName"/> filled for the card's context
    /// line. See <c>.design/artifacts.md</c>.
    /// </summary>
    public async Task<List<ReleaseListItem>> ListLatestPipelineBuildReleasesAsync(CancellationToken ct = default)
    {
        // Two-step for translatability: pick the newest ready build id per
        // pipeline, then project those builds' releases. The candidate set is
        // small (one row per pipeline survives the grouping).
        // The Releases browser shows each pipeline's latest build, which is the
        // one place a project's release surfaces outside the Artifacts tool —
        // so it is also the one place the visibility fence has to be applied to
        // a list rather than a single id.
        var snapshot = await _access.GetSnapshotAsync(ct);
        var visibleRelease = _access.VisibleReleasePredicate(snapshot);

        var latestBuildIds = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId != null
                        && b.ReleaseId != null
                        && b.Status == ProjectBuildStatus.Ready
                        && b.Release!.Status == "ready"
                        && b.Release!.DeletedAt == null
                        && b.Pipeline!.DeletedAt == null
                        && b.BcTarget == ProjectBuildTarget.Current)
            .GroupBy(b => b.PipelineId)
            .Select(g => g.OrderByDescending(b => b.StartedAt).ThenByDescending(b => b.Id).First().Id)
            .ToListAsync(ct);

        var rows = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => latestBuildIds.Contains(b.Id))
            .Where(b => _db.OeReleases.Where(visibleRelease).Any(r => r.Id == b.ReleaseId))
            .Select(b => new ReleaseListItem(
                b.Release!.Id, b.Release.Label, b.Release.Kind, b.Release.Status,
                b.Release.BcVersion, b.Release.ParentReleaseId,
                ParentLabel: b.Release.ParentRelease != null ? b.Release.ParentRelease.Label : null,
                Publisher: b.Release.Publisher,
                ProjectName: b.Project!.Name,
                ImportedAt: b.Release.ImportedAt,
                SourceFileCount: b.Release.SourceFileCount,
                SourceContentLength: b.Release.SourceContentLength,
                DeletedAt: b.Release.DeletedAt,
                StatusMessage: b.Release.StatusMessage,
                PipelineName: b.Pipeline!.Name))
            .ToListAsync(ct);

        rows.Sort((a, b) => string.Compare(a.ProjectName, b.ProjectName, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    /// <summary>
    /// The builds a <c>project</c>-kind Release can be compared with: the other
    /// ready builds of the same project, its own pipeline's first, newest first
    /// within each. This is the project-scoped compare picker
    /// <see cref="ListReleasesAsync"/> defers to; the global list leaves project
    /// builds out, so the release page cannot filter them out of it (#1075).
    /// <para>
    /// Every build of a project shares the label "{project} on BC {version}", so
    /// each row's <see cref="ReleaseListItem.Label"/> is rewritten to name the
    /// build and its pipeline. Empty when the release is not a tracked build or
    /// the caller cannot see it. See <c>.design/artifacts.md</c>.
    /// </para>
    /// </summary>
    public async Task<List<ReleaseListItem>> ListBuildCompareCandidatesAsync(int releaseId, CancellationToken ct = default)
    {
        if (!await ReleaseVisibleAsync(releaseId, ct)) return [];
        var own = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ReleaseId == releaseId)
            .OrderBy(b => b.Id)
            .Select(b => new { b.ProjectId, b.PipelineId })
            .FirstOrDefaultAsync(ct);
        if (own is null) return [];

        var snapshot = await _access.GetSnapshotAsync(ct);
        var visibleRelease = _access.VisibleReleasePredicate(snapshot);
        var rows = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ProjectId == own.ProjectId
                        && b.ReleaseId != null
                        && b.ReleaseId != releaseId
                        && b.Status == ProjectBuildStatus.Ready
                        && b.Release!.Status == "ready"
                        && b.Release!.DeletedAt == null
                        // A deleted pipeline's builds go with it, as on the Pipelines pages.
                        && (b.Pipeline == null || b.Pipeline.DeletedAt == null)
                        // Preview checks stopped indexing their objects (#1140); only the older ones that did can be compared.
                        && (b.BcTarget == ProjectBuildTarget.Current || b.Release!.SourceFileCount > 0))
            .Where(b => _db.OeReleases.Where(visibleRelease).Any(r => r.Id == b.ReleaseId))
            .OrderBy(b => own.PipelineId != null && b.PipelineId == own.PipelineId ? 0 : 1)
            .ThenByDescending(b => b.StartedAt)
            .ThenByDescending(b => b.Id)
            .Take(BuildCompareCandidateCap)
            .Select(b => new
            {
                BuildId = b.Id,
                b.BcTarget,
                PipelineName = b.Pipeline != null ? b.Pipeline.Name : null,
                Item = new ReleaseListItem(
                    b.Release!.Id, b.Release.Label, b.Release.Kind, b.Release.Status,
                    b.Release.BcVersion, b.Release.ParentReleaseId,
                    ParentLabel: b.Release.ParentRelease != null ? b.Release.ParentRelease.Label : null,
                    Publisher: b.Release.Publisher,
                    ProjectName: b.Project!.Name,
                    ImportedAt: b.Release.ImportedAt,
                    SourceFileCount: b.Release.SourceFileCount,
                    SourceContentLength: b.Release.SourceContentLength,
                    DeletedAt: b.Release.DeletedAt,
                    StatusMessage: b.Release.StatusMessage,
                    PipelineName: b.Pipeline != null ? b.Pipeline.Name : null,
                    IsPrerelease: b.Release.IsPrerelease),
            })
            .ToListAsync(ct);

        return rows
            .Select(r => r.Item with { Label = BuildCompareLabel(r.BuildId, r.BcTarget, r.PipelineName, r.Item.BcVersion) })
            .ToList();
    }

    /// <summary>
    /// How many builds the compare picker offers. A busy solution has hundreds;
    /// the recent ones are the ones anyone compares against, and the picker is a
    /// plain dropdown.
    /// </summary>
    internal const int BuildCompareCandidateCap = 50;

    private static string BuildCompareLabel(int buildId, string bcTarget, string? pipelineName, string? bcVersion)
    {
        var label = bcTarget == ProjectBuildTarget.Current
            ? $"Build #{buildId}"
            : $"{ProjectBuildTarget.Label(bcTarget)} preview build #{buildId}";
        if (!string.IsNullOrWhiteSpace(pipelineName)) label += $" of {pipelineName}";
        return string.IsNullOrWhiteSpace(bcVersion) ? label : $"{label} on BC {bcVersion}";
    }

    /// <summary>
    /// Returns the Release header plus a denormalised module count for the
    /// page title.
    /// </summary>
    public async Task<ReleaseDetail?> GetReleaseAsync(int releaseId, CancellationToken ct = default)
    {
        if (!await ReleaseVisibleAsync(releaseId, ct)) return null;
        var row = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Id == releaseId)
            .Select(r => new
            {
                r.Id, r.Label, r.Kind, r.Status, r.StatusMessage, r.BcVersion, r.ParentReleaseId, r.ImportedAt,
                r.Publisher, r.ProjectName, r.DeletedAt, r.IsPrerelease,
                ParentLabel = r.ParentRelease != null ? r.ParentRelease.Label : null,
            })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;

        var moduleCount = await _db.OeModules.AsNoTracking()
            .CountAsync(m => m.ReleaseId == releaseId, ct);

        return new ReleaseDetail(
            Id: row.Id,
            Label: row.Label,
            Kind: row.Kind,
            Status: row.Status,
            StatusMessage: row.StatusMessage,
            BcVersion: row.BcVersion,
            ParentReleaseId: row.ParentReleaseId,
            ParentLabel: row.ParentLabel,
            Publisher: row.Publisher,
            ProjectName: row.ProjectName,
            ImportedAt: row.ImportedAt,
            DeletedAt: row.DeletedAt,
            ModuleCount: moduleCount,
            IsPrerelease: row.IsPrerelease);
    }

    /// <summary>
    /// The vendor Releases a pipeline build resolved its dependency symbols from
    /// (the release's dependency links, #901), by label. Empty for a release with
    /// no links. Feeds the manage page's "Third-party dependencies" card.
    /// </summary>
    public async Task<List<ReleaseDependencyRow>> GetDependencyReleasesAsync(int releaseId, CancellationToken ct = default)
    {
        if (!await ReleaseVisibleAsync(releaseId, ct)) return new();
        return await _db.OeReleaseDependencies.AsNoTracking()
            .Where(d => d.ReleaseId == releaseId)
            .OrderBy(d => d.DependencyRelease!.Label)
            .Select(d => new ReleaseDependencyRow(
                d.DependencyReleaseId,
                d.DependencyRelease!.Label,
                d.DependencyRelease.Publisher,
                d.DependencyRelease.Status,
                // A symbols release holds the one app it was named for.
                d.DependencyRelease.Modules.OrderBy(m => m.Id).Select(m => m.Name).FirstOrDefault(),
                d.DependencyRelease.Modules.OrderBy(m => m.Id).Select(m => m.Version).FirstOrDefault()))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The per-app build report for a project Release, newest-meaningful order
    /// (failures first so the admin sees what to fix). Empty for non-project
    /// releases. Read-only; feeds the manage page's build panel + partial badge.
    /// </summary>
    public async Task<List<ProjectBuildResultRow>> GetProjectBuildResultsAsync(int releaseId, CancellationToken ct = default)
    {
        if (!await ReleaseVisibleAsync(releaseId, ct)) return new();
        return await _db.OeProjectBuildResults.AsNoTracking()
            .Where(r => r.ReleaseId == releaseId)
            // Failed rows first, then by app name, so the report reads as a
            // "here's what to fix" list rather than ingest order.
            .OrderBy(r => r.Status == ProjectBuildResultStatus.Failed ? 0 : 1)
            .ThenBy(r => r.AppName)
            .Select(r => new ProjectBuildResultRow(r.AppName, r.AppId, r.Status, r.Message, r.RepoUrl, r.CommitSha, r.CommitDate))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The per-app build reports for several project Releases in one query, keyed
    /// by release id. Lets the Object Explorer project drill-down label each
    /// release card with the extension(s) it built and their commit provenance
    /// without an N+1 over <see cref="GetProjectBuildResultsAsync"/>. Rows are
    /// ordered by app name within a release; releases with no build rows simply
    /// don't appear in the dictionary.
    /// </summary>
    public async Task<Dictionary<int, List<ProjectBuildResultRow>>> GetProjectBuildResultsForReleasesAsync(
        IReadOnlyCollection<int> releaseIds, CancellationToken ct = default)
    {
        if (releaseIds.Count == 0) return new Dictionary<int, List<ProjectBuildResultRow>>();

        // Drop any release the caller can't see before the join, so a Private
        // project's build report can't ride in on a batch of ids.
        var snapshot = await _access.GetSnapshotAsync(ct);
        var visibleIds = await _db.OeReleases.AsNoTracking()
            .Where(r => releaseIds.Contains(r.Id))
            .Where(_access.VisibleReleasePredicate(snapshot))
            .Select(r => r.Id)
            .ToListAsync(ct);
        if (visibleIds.Count == 0) return new Dictionary<int, List<ProjectBuildResultRow>>();

        var rows = await _db.OeProjectBuildResults.AsNoTracking()
            .Where(r => visibleIds.Contains(r.ReleaseId))
            .OrderBy(r => r.AppName)
            .Select(r => new { r.ReleaseId, Row = new ProjectBuildResultRow(r.AppName, r.AppId, r.Status, r.Message, r.RepoUrl, r.CommitSha, r.CommitDate) })
            .ToListAsync(ct);

        return rows
            .GroupBy(x => x.ReleaseId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Row).ToList());
    }

    // ── Modules ─────────────────────────────────────────────────────────

    /// <summary>
    /// Lists modules in a Release. Test apps / internal apps / language packs
    /// are filtered out by default — admins can flip them in via the filter
    /// toggles.
    /// </summary>
    public async Task<List<ModuleListItem>> ListModulesAsync(int releaseId, ModuleListFilter filter, CancellationToken ct = default)
    {
        if (!await ReleaseVisibleAsync(releaseId, ct)) return new();
        var q = _db.OeModules.AsNoTracking().Where(m => m.ReleaseId == releaseId);
        if (!filter.IncludeTest) q = q.Where(m => !m.IsTest);
        if (!filter.IncludeInternal) q = q.Where(m => !m.IsInternal);
        if (!filter.IncludeLanguagePack) q = q.Where(m => !m.IsLanguagePack);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // ILike instead of ToLower().Contains: the latter wraps the column
            // in a function (no index) and lowers the needle with the current
            // culture. Escape %/_ so a literal wildcard matches literally. #690
            var pattern = "%" + ObjectSearchService.EscapeLike(filter.Search.Trim()) + "%";
            q = q.Where(m => EF.Functions.ILike(m.Name, pattern, "\\")
                || EF.Functions.ILike(m.Publisher, pattern, "\\"));
        }

        return await q.OrderBy(m => m.Publisher).ThenBy(m => m.Name)
            .Select(m => new ModuleListItem(
                m.Id, m.AppId, m.Name, m.Publisher, m.Version, m.Target,
                m.IsTest, m.IsInternal, m.IsLanguagePack,
                m.Objects.Count))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The header facts for one module's detail page — its identity plus the
    /// Release it came from. Null when the module doesn't exist or belongs to a
    /// Release this person can't see.
    /// </summary>
    public async Task<ModuleHeader?> GetModuleHeaderAsync(long moduleId, CancellationToken ct = default)
    {
        if (!await ModuleVisibleAsync(moduleId, ct)) return null;
        return await _db.OeModules.AsNoTracking()
            .Where(m => m.Id == moduleId)
            .Select(m => new ModuleHeader(
                m.AppId, m.Name, m.Publisher, m.Version, m.ReleaseId, m.Release!.Label))
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>
    /// A Release's label on its own — the object detail page needs it to name
    /// the Release a base object is being viewed "from". Null when the Release
    /// doesn't exist or isn't visible.
    /// </summary>
    public async Task<string?> GetReleaseLabelAsync(int releaseId, CancellationToken ct = default)
    {
        if (!await ReleaseVisibleAsync(releaseId, ct)) return null;
        return await _db.OeReleases.AsNoTracking()
            .Where(r => r.Id == releaseId)
            .Select(r => r.Label)
            .SingleOrDefaultAsync(ct);
    }

    // ── Objects ─────────────────────────────────────────────────────────

    /// <summary>
    /// Paginated object list within a module — feeds the object browser table.
    /// </summary>
    public async Task<ObjectListPage> ListObjectsAsync(
        long moduleId, ObjectListFilter filter, int skip, int take, CancellationToken ct = default)
    {
        if (!await ModuleVisibleAsync(moduleId, ct)) return new ObjectListPage(new List<ObjectListItem>(), 0);
        var q = _db.OeModuleObjects.AsNoTracking().Where(o => o.ModuleId == moduleId);

        var kinds = ObjectSearchRanking.NormalizeKinds(filter.Kinds);
        if (kinds is { Count: > 0 })
        {
            q = q.Where(o => kinds.Contains(o.Kind));
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            // Numeric search matches the object id; substring otherwise. The
            // search box accepts either, mirroring the convention from the
            // earlier base-app browser the new schema replaces. The C/AL
            // Version List joins the substring match so a module drill-down
            // finds "NAVDK14.49"-tagged objects the same way the release
            // search does (issue #271). ILike (not ToLower().Contains) so the
            // trigram indexes on name / version_list are usable and the casing
            // isn't current-culture; %/_ escaped so a literal wildcard in the
            // term matches literally. #690
            var pattern = "%" + ObjectSearchService.EscapeLike(s) + "%";
            if (int.TryParse(s, out var asInt))
            {
                q = q.Where(o => o.ObjectId == asInt
                    || EF.Functions.ILike(o.Name, pattern, "\\")
                    || (o.VersionList != null && EF.Functions.ILike(o.VersionList, pattern, "\\")));
            }
            else
            {
                q = q.Where(o => EF.Functions.ILike(o.Name, pattern, "\\")
                    || (o.VersionList != null && EF.Functions.ILike(o.VersionList, pattern, "\\")));
            }
        }

        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(o => o.Kind).ThenBy(o => o.Name)
            .Skip(skip).Take(take)
            .Select(o => new ObjectListItem(
                o.Id, o.Kind, o.ObjectId, o.Name, o.Namespace,
                o.ExtendsAppId, o.ExtendsObjectName,
                o.SourceFileId, o.LineNumber))
            .ToListAsync(ct);
        return new ObjectListPage(rows, total);
    }

    /// <summary>
    /// Returns one object's full detail: module context, source file pointer,
    /// inline symbol and variable lists. The inspector panel uses this so it
    /// doesn't paginate within the object — these lists are bounded by the
    /// object's own structure.
    /// </summary>
    public async Task<ObjectDetail?> GetObjectAsync(long objectId, CancellationToken ct = default)
    {
        if (!await ObjectVisibleAsync(objectId, ct)) return null;
        var header = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Id == objectId)
            .Select(o => new
            {
                o.Id, o.Kind, o.ObjectId, o.Name, o.Namespace, o.ModuleId,
                ModuleName = o.Module!.Name,
                o.ExtendsAppId, o.ExtendsObjectName,
                o.SourceFileId,
                SourceFilePath = o.SourceFile != null ? o.SourceFile.Path : null,
                o.LineNumber,
            })
            .SingleOrDefaultAsync(ct);
        if (header is null) return null;

        var symbols = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.ObjectId == objectId)
            .OrderBy(s => s.Kind).ThenBy(s => s.Name)
            .Select(s => new ObjectSymbolRow(
                s.Id, s.Kind, s.Name, s.Signature, s.ReturnType, s.FieldId, s.LineNumber, s.Doc))
            .ToListAsync(ct);

        var variables = await _db.OeModuleVariables.AsNoTracking()
            .Where(v => v.ObjectId == objectId)
            .OrderBy(v => v.Name)
            .Select(v => new ObjectVariableRow(
                v.Id, v.Name, v.TypeKeyword, v.TypeName,
                v.TargetAppId, v.TargetObjectKind, v.TargetObjectId, v.TargetObjectName))
            .ToListAsync(ct);

        return new ObjectDetail(
            Id: header.Id,
            Kind: header.Kind,
            ObjectId: header.ObjectId,
            Name: header.Name,
            Namespace: header.Namespace,
            ModuleId: header.ModuleId,
            ModuleName: header.ModuleName,
            ExtendsAppId: header.ExtendsAppId,
            ExtendsObjectName: header.ExtendsObjectName,
            SourceFileId: header.SourceFileId,
            SourceFilePath: header.SourceFilePath,
            LineNumber: header.LineNumber,
            Symbols: symbols,
            Variables: variables);
    }

    // ── Forward-edge MCP surface (#180) ───────────────────────────────

    /// <summary>
    /// Resolves an object by case-insensitive (kind, name) within a
    /// release, used by the forward-edge MCP tools to translate the
    /// agent's natural "Sales-Post" form into the row id everything
    /// downstream keys on. Returns null when no match — tool wrappers
    /// throw <c>McpException</c> with a "try search_objects" hint.
    /// </summary>
    public async Task<ObjectDetail?> GetObjectByNameAsync(
        int releaseId,
        string objectKind,
        string objectName,
        CancellationToken ct = default)
    {
        if (!await ReleaseVisibleAsync(releaseId, ct)) return null;
        var kind = objectKind.Trim().ToLowerInvariant();
        // Exact case-insensitive match as a wildcard-free ILike pattern: it
        // leaves the column unwrapped so the name trigram index can serve it,
        // where LOWER(name) = @p forced a scan of the release. Escaped so an
        // object name containing % or _ matches literally. #690
        var name = ObjectSearchService.EscapeLike(objectName.Trim());
        var id = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId
                        && o.Kind == kind
                        && EF.Functions.ILike(o.Name, name, "\\"))
            .Select(o => (long?)o.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (id is null) return null;
        return await GetObjectAsync(id.Value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Projects an object's header + symbol rows into the slim outline
    /// shape the <c>get_object_outline</c> MCP tool returns. Skips the
    /// variables list (not used for trace navigation) and the
    /// inspector-only namespace / extends-* columns. Reads the rows
    /// directly rather than going through <see cref="GetObjectAsync"/>
    /// so the variables query doesn't run and the symbols come back
    /// already sorted by line number (agent-friendly top-to-bottom
    /// reading; the inspector's kind+name sort is the wrong grain
    /// here).
    /// </summary>
    public async Task<ObjectOutline?> GetObjectOutlineAsync(
        int releaseId,
        string objectKind,
        string objectName,
        CancellationToken ct = default)
    {
        if (!await ReleaseVisibleAsync(releaseId, ct)) return null;
        var kind = objectKind.Trim().ToLowerInvariant();
        // Wildcard-free ILike pattern — index-usable exact case-insensitive
        // match; see GetObjectByNameAsync. #690
        var name = ObjectSearchService.EscapeLike(objectName.Trim());
        var header = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId
                        && o.Kind == kind
                        && EF.Functions.ILike(o.Name, name, "\\"))
            .Select(o => new
            {
                o.Id, o.Kind, o.ObjectId, o.Name, o.ModuleId,
                ModuleName = o.Module!.Name,
                o.SourceFileId,
                SourceFilePath = o.SourceFile != null ? o.SourceFile.Path : null,
                o.LineNumber,
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (header is null) return null;

        var symbols = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.ObjectId == header.Id)
            .OrderBy(s => s.LineNumber).ThenBy(s => s.Name)
            .Select(s => new ObjectSymbolRow(
                s.Id, s.Kind, s.Name, s.Signature, s.ReturnType, s.FieldId, s.LineNumber, s.Doc))
            .ToListAsync(ct).ConfigureAwait(false);

        // For interface targets, attach the implementing codeunits so
        // MCP agents calling get_object_outline on an interface get the
        // same "implemented by" surface the source-viewer outline shows.
        IReadOnlyList<InterfaceImplementer>? implementedBy = null;
        if (string.Equals(header.Kind, "interface", StringComparison.OrdinalIgnoreCase))
        {
            var rows = await _references.FindInterfaceImplementersAsync(header.ModuleId, header.Name, ct);
            implementedBy = rows
                .Select(r => new InterfaceImplementer(r.SourceObjectId, r.SourceObjectName, r.SourceModuleName))
                .ToList();
        }

        return new ObjectOutline(
            Id: header.Id,
            Kind: header.Kind,
            ObjectId: header.ObjectId,
            Name: header.Name,
            ModuleId: header.ModuleId,
            ModuleName: header.ModuleName,
            SourceFileId: header.SourceFileId,
            SourceFilePath: header.SourceFilePath,
            LineNumber: header.LineNumber,
            Symbols: symbols,
            ImplementedBy: implementedBy);
    }

    /// <summary>
    /// Returns the AL source slice for a procedure / trigger / event
    /// publisher / event subscriber, looked up by <see cref="OeModuleSymbol.Id"/>.
    /// Slices from the declaration line through the body's matching
    /// <c>end;</c>; when <c>EndLine</c> is null (legacy / pre-#181
    /// ingest), falls back to the next-sibling-symbol's start line as
    /// an approximation. Applies <paramref name="maxLines"/> as a cap
    /// and stamps <see cref="ProcedureSource.Truncated"/> when applied.
    /// Returns null when the symbol id doesn't exist or doesn't have a
    /// source file attached to its parent object.
    /// </summary>
    public async Task<ProcedureSource?> GetProcedureSourceAsync(
        long symbolId,
        int maxLines,
        CancellationToken ct = default)
    {
        if (!await SymbolVisibleAsync(symbolId, ct)) return null;
        var row = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Id == symbolId)
            .Select(s => new
            {
                s.Id,
                s.Kind,
                s.Name,
                s.Signature,
                s.ReturnType,
                s.Doc,
                s.LineNumber,
                s.EndLine,
                OwnerId = s.Object!.Id,
                OwnerName = s.Object.Name,
                OwnerKind = s.Object.Kind,
                SourceFileId = s.Object.SourceFileId,
            })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null || row.SourceFileId is null) return null;

        // Fallback for legacy rows: take the next symbol's line on the
        // same owner as the close, minus one. The (Owner, LineNumber)
        // lookup is satisfied by ix_oe_module_symbols_object_line.
        int endLine;
        if (row.EndLine is int explicitEnd)
        {
            endLine = explicitEnd;
        }
        else
        {
            var nextLine = await _db.OeModuleSymbols.AsNoTracking()
                .Where(s => s.ObjectId == row.OwnerId && s.LineNumber > row.LineNumber)
                .OrderBy(s => s.LineNumber)
                .Select(s => (int?)s.LineNumber)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            endLine = nextLine is int n ? n - 1 : int.MaxValue;
        }

        var fileContent = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == row.SourceFileId)
            .Select(f => f.FileContent!.Content)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (fileContent is null) return null;

        // SplitLines normalises line endings (the previous raw Split('\n') left
        // stray '\r' in the returned source). See issue #387.
        var lines = OeSourceText.SplitLines(fileContent);
        var startIdx = Math.Max(0, row.LineNumber - 1);
        var endIdx = Math.Min(lines.Length - 1, endLine - 1);
        if (endIdx < startIdx) endIdx = startIdx;
        var available = endIdx - startIdx + 1;
        var truncated = false;
        if (available > maxLines)
        {
            endIdx = startIdx + maxLines - 1;
            truncated = true;
        }
        var sliceLines = new string[endIdx - startIdx + 1];
        Array.Copy(lines, startIdx, sliceLines, 0, sliceLines.Length);
        var source = string.Join('\n', sliceLines);
        if (truncated)
        {
            source += $"\n// ... (truncated at {maxLines} of {available} lines; call list_procedure_calls or narrow the question)";
        }

        return new ProcedureSource(
            SymbolId: row.Id,
            ObjectName: row.OwnerName,
            ObjectKind: row.OwnerKind,
            Kind: row.Kind,
            Name: row.Name,
            Signature: row.Signature,
            ReturnType: row.ReturnType,
            StartLine: row.LineNumber,
            EndLine: endLine == int.MaxValue ? lines.Length : endLine,
            Truncated: truncated,
            Source: source,
            Doc: row.Doc);
    }

    /// <summary>
    /// Returns the outgoing references emitted from inside the body of
    /// the procedure / trigger identified by <paramref name="symbolId"/>.
    /// The primary path keys on <c>source_symbol_id</c> (stamped at
    /// import time on rows from #181-or-later ingests); when the symbol
    /// has <c>EndLine</c> set but no references carry the FK, falls
    /// back to <c>(SourceObjectId, LineNumber BETWEEN start AND end)</c>
    /// so the tool stays useful on releases imported before the
    /// migration. Capped at <paramref name="maxResults"/>.
    /// </summary>
    public async Task<IReadOnlyList<ProcedureCall>?> ListProcedureCallsAsync(
        long symbolId,
        int maxResults,
        CancellationToken ct = default)
    {
        if (!await SymbolVisibleAsync(symbolId, ct)) return null;
        var symbol = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Id == symbolId)
            .Select(s => new
            {
                OwnerId = s.Object!.Id,
                s.LineNumber,
                s.EndLine,
            })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (symbol is null) return null;

        // Primary indexed seek via ix_oe_module_references_source_symbol.
        var direct = await _db.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceSymbolId == symbolId)
            .OrderBy(r => r.LineNumber).ThenBy(r => r.ColumnNumber).ThenBy(r => r.Id)
            .Take(maxResults)
            .Select(r => new ProcedureCall(
                r.Id, r.TargetAppId, r.TargetObjectKind, r.TargetObjectId, r.TargetObjectName,
                r.TargetMemberName, r.TargetMemberKind, r.ReferenceKind, r.LineNumber, r.ColumnNumber))
            .ToListAsync(ct).ConfigureAwait(false);
        if (direct.Count > 0 || symbol.EndLine is null) return direct;

        // Lazy-backfill fallback: rows from pre-#181 ingests don't carry
        // source_symbol_id. Scope by line range — only safe when EndLine
        // is set so we know where the body closes. Slower path; the
        // expectation is to migrate releases off this fallback over time
        // by re-ingesting.
        var endLine = symbol.EndLine.Value;
        var startLine = symbol.LineNumber;
        return await _db.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceObjectId == symbol.OwnerId
                        && r.SourceSymbolId == null
                        && r.LineNumber >= startLine
                        && r.LineNumber <= endLine)
            .OrderBy(r => r.LineNumber).ThenBy(r => r.ColumnNumber).ThenBy(r => r.Id)
            .Take(maxResults)
            .Select(r => new ProcedureCall(
                r.Id, r.TargetAppId, r.TargetObjectKind, r.TargetObjectId, r.TargetObjectName,
                r.TargetMemberName, r.TargetMemberKind, r.ReferenceKind, r.LineNumber, r.ColumnNumber))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lightweight module list used by the search-filter dropdown on the
    /// Release search page. Test / internal / language-pack flags don't
    /// matter for the filter widget so they're omitted; ordering matches
    /// the main module list page so the dropdown feels familiar.
    /// </summary>
    public async Task<List<ReleaseModuleSummary>> ListModuleSummariesAsync(int releaseId, CancellationToken ct = default)
    {
        if (!await ReleaseVisibleAsync(releaseId, ct)) return new();
        return await _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .OrderBy(m => m.Publisher).ThenBy(m => m.Name)
            .Select(m => new ReleaseModuleSummary(m.Id, m.Name, m.Publisher))
            .ToListAsync(ct);
    }

    // ── MCP id resolvers ────────────────────────────────────────────────
    //
    // The MCP tools used to carry these as private helpers with their own
    // AppDbContext, which meant a visibility rule added here did not reach
    // them. They live on the service that owns the entity so there is one
    // home per gate. They throw McpException because their refusal copy is
    // written for an agent; nothing on the web surface calls them.
    // See .design/teams-and-visibility.md.

    /// <summary>
    /// Resolves a release by label or numeric id for an MCP caller, refusing
    /// one the caller cannot see with the same "does not exist" copy an
    /// unknown id gets. This is the choke point every release-keyed MCP tool
    /// passes through, so the project-visibility fence lives here.
    /// </summary>
    public async Task<int> ResolveReleaseAsync(string releaseLabelOrId, CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);
        var visible = _access.VisibleReleasePredicate(snapshot);
        if (int.TryParse(releaseLabelOrId, out var asId))
        {
            var exists = await _db.OeReleases.AsNoTracking()
                .Where(visible)
                .AnyAsync(r => r.Id == asId && r.DeletedAt == null, ct);
            if (!exists) throw new McpException($"Release {asId} does not exist.");
            return asId;
        }
        var label = releaseLabelOrId.Trim();
        var row = await _db.OeReleases.AsNoTracking()
            .Where(visible)
            .Where(r => r.Label == label && r.DeletedAt == null)
            .Select(r => new { r.Id })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            throw new McpException($"Release '{releaseLabelOrId}' was not found. Call list_releases to see available labels.");
        }
        return row.Id;
    }

    /// <summary>
    /// A caller who passes a symbol id straight to get_procedure_source /
    /// list_procedure_calls never resolves a release, and a symbol id is
    /// guessable — so the symbol's own release is checked here. Refuses with
    /// the same "doesn't exist" copy an unknown id gets.
    /// </summary>
    public async Task EnsureSymbolVisibleAsync(long symbolId, CancellationToken ct = default)
    {
        var releaseId = await _db.OeModuleSymbols.AsNoTracking()
            .Where(sym => sym.Id == symbolId)
            .Select(sym => (int?)sym.Module!.ReleaseId)
            .FirstOrDefaultAsync(ct);
        if (releaseId is not null && !await ReleaseVisibleAsync(releaseId.Value, ct))
        {
            throw new McpException($"Symbol id {symbolId} doesn't exist. Call get_object_outline to see the current ids for an object.");
        }
    }

    /// <summary>
    /// Body-bearing symbol kinds — what counts as a "procedure" the
    /// forward-edge tools can read source for. Mirrors the kinds the
    /// procedure walker pushes a scope frame for.
    /// </summary>
    private static readonly HashSet<string> BodyBearingKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "procedure", "local_procedure", "internal_procedure", "protected_procedure",
        "trigger", "event_publisher", "event_subscriber",
    };

    /// <summary>
    /// Resolves a procedure / trigger to its <c>oe_module_symbols.id</c>
    /// by (release, object, kind, procedure name). Throws
    /// <see cref="McpException"/> with copy steering the agent to the
    /// outline + symbolId form when the name is ambiguous on the
    /// object (page-action OnAction triggers, table-field OnValidate
    /// triggers, multiple overloads of the same procedure). Gated by
    /// <see cref="ResolveReleaseAsync"/>.
    /// </summary>
    public async Task<long> ResolveProcedureSymbolIdAsync(
        string releaseLabelOrId,
        string? objectName,
        string? objectKind,
        string? procedureName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(objectName)
            || string.IsNullOrWhiteSpace(objectKind)
            || string.IsNullOrWhiteSpace(procedureName))
        {
            throw new McpException("Must supply either symbolId or all of (objectName, objectKind, procedureName).");
        }

        var releaseId = await ResolveReleaseAsync(releaseLabelOrId, ct);
        var ownerKind = objectKind.Trim().ToLowerInvariant();
        var procName = procedureName.Trim();
        // Wildcard-free ILike patterns: an exact case-insensitive match that
        // leaves the columns unwrapped, so the name trigram indexes stay usable
        // where LOWER(name) = @p forced a scan. Escaped so a name containing
        // % or _ matches literally. #690
        var ownerNamePattern = ObjectSearchService.EscapeLike(objectName.Trim());
        var procNamePattern = ObjectSearchService.EscapeLike(procName);

        var candidates = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.Module!.ReleaseId == releaseId
                        && s.Object.Kind == ownerKind
                        && EF.Functions.ILike(s.Object.Name, ownerNamePattern, "\\")
                        && EF.Functions.ILike(s.Name, procNamePattern, "\\"))
            .Select(s => new { s.Id, s.Kind, s.LineNumber })
            .ToListAsync(ct);

        var bodied = candidates.Where(c => BodyBearingKinds.Contains(c.Kind)).ToList();
        if (bodied.Count == 0)
        {
            throw new McpException($"No procedure or trigger named '{procName}' on {ownerKind} '{objectName}' in release {releaseLabelOrId}. Call get_object_outline to see what this object exposes.");
        }
        if (bodied.Count > 1)
        {
            throw new McpException($"Multiple symbols named '{procName}' on {ownerKind} '{objectName}' (typical for page-action OnAction triggers and table-field OnValidate triggers). Call get_object_outline to see their line numbers and ids, then pass symbolId instead of procedureName.");
        }
        return bodied[0].Id;
    }

    /// <summary>
    /// The full per-module row the <c>list_release_modules</c> MCP tool
    /// returns, capped at <paramref name="maxResults"/>. Distinct from
    /// <see cref="ListModuleSummariesAsync"/>, which is the three-column form
    /// the search-filter dropdown needs. The release must already have come
    /// from <see cref="ResolveReleaseAsync"/>.
    /// </summary>
    public async Task<List<McpReleaseModuleRow>> ListReleaseModulesForMcpAsync(
        int releaseId, int maxResults, CancellationToken ct = default)
    {
        return await _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .OrderBy(m => m.Name)
            .Take(maxResults)
            .Select(m => new McpReleaseModuleRow(
                m.Id,
                m.Name,
                m.Publisher,
                m.Version,
                m.AppId,
                m.IsTest,
                m.IsInternal,
                m.IsLanguagePack,
                m.DependencyCount,
                m.SymbolReferenceContentHash != null))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Identity + stored-symbol-file state for one module in a release, looked
    /// up by name, for the <c>download_symbol_reference</c> MCP tool. Returns
    /// <see langword="null"/> when the release has no module by that name. The
    /// release must already have come from <see cref="ResolveReleaseAsync"/>.
    /// </summary>
    public async Task<McpModuleSymbolReference?> GetModuleSymbolReferenceAsync(
        int releaseId, string moduleName, CancellationToken ct = default)
    {
        var name = moduleName.Trim();
        return await _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId && m.Name.ToLower() == name.ToLower())
            .Select(m => new McpModuleSymbolReference(
                m.Id,
                m.Name,
                m.Version,
                m.SymbolReferenceContentHash,
                (int?)(m.SymbolReferenceContent != null ? m.SymbolReferenceContent.ContentLength : 0)))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Names of the modules in a release that <em>do</em> have a stored
    /// SymbolReference.json — the "try one of these instead" list.
    /// </summary>
    public async Task<List<string>> ListModulesWithStoredSymbolReferenceAsync(
        int releaseId, CancellationToken ct = default)
    {
        return await _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId && m.SymbolReferenceContentHash != null)
            .OrderBy(m => m.Name)
            .Select(m => m.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Chain-aware object lookup for the MCP find-references tools: resolves a
    /// target object by name across the release's visible ancestry. The seed
    /// release must already have come from <see cref="ResolveReleaseAsync"/>.
    /// </summary>
    public Task<ChainObjectHit?> ResolveChainObjectAsync(
        int seedReleaseId, string name, string? kind, int? objectId, CancellationToken ct = default)
        => ChainObjectResolution.ResolveObjectAsync(_db, seedReleaseId, name, kind, objectId, ct);
}

/// <summary>One module row for the <c>list_release_modules</c> MCP tool.</summary>
public sealed record McpReleaseModuleRow(
    long Id,
    string Name,
    string Publisher,
    string Version,
    Guid AppId,
    bool IsTest,
    bool IsInternal,
    bool IsLanguagePack,
    int DependencyCount,
    bool HasStoredSymbolReference);

/// <summary>One module's stored-SymbolReference.json state for the <c>download_symbol_reference</c> MCP tool.</summary>
public sealed record McpModuleSymbolReference(
    long Id,
    string Name,
    string Version,
    string? ContentHash,
    int? ContentLength);
