using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.Navigation;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Palette.Sources;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Palette;

/// <summary>
/// What the palette shows before anything is typed: the destinations of the
/// record the page is about (its context block), and the places this browser
/// went recently - each re-checked here, because a remembered link is only a
/// claim that the caller could open it once. Answers <c>GET /palette/context</c>.
/// See <c>.design/command-palette.md</c>, "Where you are" and "Where you have
/// been".
///
/// <para><b>The fence.</b> Every read goes through the organisation query
/// filter and the same visibility rules the pages use -
/// <see cref="ProjectAccess.VisibleProjectPredicate"/>,
/// <see cref="ProjectAccess.VisibleReleasePredicate"/>, the tool gates and the
/// sidebar's own gates for "Go to" pages. No <c>IgnoreQueryFilters()</c>. A
/// context the caller cannot open is no context at all, and a recent the caller
/// can no longer open is dropped rather than shown locked: the palette is for
/// going somewhere.</para>
///
/// <para><b>Nothing the browser sent is echoed back unchecked.</b> A recent is
/// recognised by a strict route pattern and answered with the server's own
/// title and link; anything that matches no pattern, or matches one the caller
/// may not open, simply is not in the answer.</para>
/// </summary>
public sealed partial class PaletteContextService
{
    /// <summary>How many recents are remembered, and so the most that are ever checked.</summary>
    public const int MaxRecents = 8;

    /// <summary>
    /// A solution's environments in its context block. A ceiling rather than a
    /// page size: a customer with more than this is reached by typing, and the
    /// Business Central tab lists them all.
    /// </summary>
    public const int MaxContextEnvironments = 8;

    /// <summary>Longer than any link the palette ever stores; anything longer is not one of ours.</summary>
    private const int MaxHrefLength = 200;

    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;
    private readonly ToolEnablement _tools;
    private readonly IToolAvailability _availability;
    private readonly IOrganizationContext _orgContext;
    private readonly ISingleTenantMode _singleTenant;
    private readonly UpgradeFleetService _fleet;
    private readonly ILogger<PaletteContextService> _logger;

    public PaletteContextService(
        AppDbContext db,
        ProjectAccess access,
        ToolEnablement tools,
        IToolAvailability availability,
        IOrganizationContext orgContext,
        ISingleTenantMode singleTenant,
        UpgradeFleetService fleet,
        ILogger<PaletteContextService> logger)
    {
        _db = db;
        _access = access;
        _tools = tools;
        _availability = availability;
        _orgContext = orgContext;
        _singleTenant = singleTenant;
        _fleet = fleet;
        _logger = logger;
    }

    /// <summary>
    /// The context block for <paramref name="at"/> (null when there is none, or
    /// the caller may not open it) and the recents in <paramref name="recents"/>
    /// the caller may still open, in the order given.
    /// </summary>
    /// <param name="at">What the page said it is: <c>solution:12</c>, <c>environment:7</c>.</param>
    /// <param name="recents">In-app links, most recent first. Only the first <see cref="MaxRecents"/> distinct ones are read.</param>
    public async Task<PaletteContextResult> ResolveAsync(
        ClaimsPrincipal user, string? at, IReadOnlyList<string>? recents, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true) return PaletteContextResult.Empty;

        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);

        PaletteContextBlock? context = null;
        if (PaletteContextRef.TryParse(at, out var reference))
        {
            context = reference.Kind switch
            {
                PaletteContextKind.Solution => await SolutionContextAsync(user, snapshot, reference.Id, ct).ConfigureAwait(false),
                PaletteContextKind.Environment => await EnvironmentContextAsync(user, reference.Id, ct).ConfigureAwait(false),
                _ => null,
            };
        }

        var checkedRecents = await CheckRecentsAsync(user, snapshot, recents, ct).ConfigureAwait(false);

        _logger.LogDebug(
            "Palette context answered {HasContext} with {ContextItems} items and kept {KeptRecents} of {AskedRecents} recents",
            context is not null, context?.Items.Count ?? 0, checkedRecents.Count, recents?.Count ?? 0);

        return new PaletteContextResult(context, checkedRecents);
    }

    // ── Where you are ───────────────────────────────────────────────────

    /// <summary>
    /// A solution's tabs, as <c>ProjectDetail.razor</c> draws them for this
    /// caller, then its environments and its latest build. The tab conditions
    /// are the page's, line for line: Customer, General and Repositories for
    /// anyone who can open it; Business Central (not on-premises) and Pipelines
    /// for whoever manages it; Access for its owner and the admins.
    /// </summary>
    private async Task<PaletteContextBlock?> SolutionContextAsync(
        ClaimsPrincipal user, ProjectAccess.AccessSnapshot snapshot, int projectId, CancellationToken ct)
    {
        if (!_tools.IsEnabled(ToolKey.Projects, user)) return null;

        var project = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Where(ProjectAccess.VisibleProjectPredicate(snapshot))
            .Select(p => new { p.Id, p.Name, p.CreatedByUserId, p.HostingType })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (project is null) return null;

        var canManage = await _access.CanManageAsync(project.Id, project.CreatedByUserId, ct).ConfigureAwait(false);
        // What ProjectService.CanChangeAccessAsync answers, without re-reading the row.
        var canChangeAccess = await _access.CanDeleteAsync(project.CreatedByUserId, ct).ConfigureAwait(false);

        var root = $"/solutions/{project.Id.ToString(CultureInfo.InvariantCulture)}";
        var items = new List<PaletteResultItem>
        {
            Tab("Customer", root + "?tab=customer"),
            Tab("General", root + "?tab=general"),
            Tab("Repositories", root + "?tab=repositories"),
        };
        if (canManage)
        {
            if (!OeProject.IsOnPremisesHosting(project.HostingType)) items.Add(Tab("Business Central", root + "?tab=bc"));
            items.Add(Tab("Pipelines", root + "?tab=pipelines"));
            items.Add(Tab("Symbols", root + "?tab=symbols"));
            if (canChangeAccess) items.Add(Tab("Access", root + "?tab=access"));
        }

        // The Environments source's own filter: an environment Business Central no
        // longer reports, or one the customer deleted, is not somewhere anyone is
        // heading from here.
        var environments = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == project.Id && e.MissingSince == null)
            .Where(EnvironmentQueries.NotSoftDeleted)
            .OrderBy(e => e.Name)
            .Take(MaxContextEnvironments)
            .Select(e => new { e.Id, e.Name, e.Type, e.Version, e.Status })
            .ToListAsync(ct).ConfigureAwait(false);
        items.AddRange(environments.Select(e => new PaletteResultItem(
            "environment",
            e.Name,
            // No solution name: it is the heading this row sits under.
            EnvironmentPaletteSource.Describe(null, e.Type, e.Version, e.Status),
            $"/environments/{e.Id.ToString(CultureInfo.InvariantCulture)}")));

        if (await LatestBuildAsync(user, project.Id, ct).ConfigureAwait(false) is { } build) items.Add(build);

        return new PaletteContextBlock(project.Name, items);
    }

    /// <summary>
    /// The newest build that ran through a pipeline, landing on that pipeline's
    /// page - where its downloads are, and which anyone who can see the solution
    /// may open. Behind the Pipelines tool, as <c>/pipelines</c> is. A build from
    /// before pipelines existed has nowhere to land and is skipped.
    /// </summary>
    private async Task<PaletteResultItem?> LatestBuildAsync(ClaimsPrincipal user, int projectId, CancellationToken ct)
    {
        if (!_tools.IsEnabled(ToolKey.Pipelines, user)) return null;

        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ProjectId == projectId && b.PipelineId != null && b.Pipeline!.DeletedAt == null
                && b.BcTarget == ProjectBuildTarget.Current)
            .OrderByDescending(b => b.StartedAt).ThenByDescending(b => b.Id)
            .Select(b => new { PipelineId = b.PipelineId!.Value, PipelineName = b.Pipeline!.Name, b.Status, b.StartedAt })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (build is null) return null;

        var subtitle = string.Join(" - ", new[]
        {
            build.PipelineName.Trim(),
            BuildStatusWord(build.Status),
            build.StartedAt.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
        }.Where(part => part.Length > 0));

        return new PaletteResultItem(
            "build", "Latest build", subtitle,
            $"/pipelines/{build.PipelineId.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string BuildStatusWord(string status) => status switch
    {
        ProjectBuildStatus.Queued => "Queued",
        ProjectBuildStatus.Building => "Building",
        ProjectBuildStatus.Ready => "Ready",
        ProjectBuildStatus.Failed => "Failed",
        _ => string.Empty,
    };

    /// <summary>
    /// An environment's tabs - all five are open to anyone who can open the page -
    /// then the two ways out to Microsoft, each only when the page itself offers
    /// it (the tenant is known), then its solution. Read through
    /// <see cref="UpgradeFleetService.GetEnvironmentAsync"/>, the page's own read,
    /// so "can open the page" and "gets a context block" are one question.
    /// </summary>
    private async Task<PaletteContextBlock?> EnvironmentContextAsync(
        ClaimsPrincipal user, int environmentId, CancellationToken ct)
    {
        if (!_tools.IsEnabled(ToolKey.Projects, user)) return null;

        var detail = await _fleet.GetEnvironmentAsync(environmentId, ct).ConfigureAwait(false);
        if (detail is null) return null;
        var row = detail.Fleet;

        var root = $"/environments/{row.EnvironmentId.ToString(CultureInfo.InvariantCulture)}";
        var items = new List<PaletteResultItem>
        {
            Tab("Overview", root),
            Tab("Apps", root + "/apps"),
            Tab("Operations", root + "/operations"),
            Tab("Sessions", root + "/sessions"),
            Tab("Workbench history", root + "/history"),
        };
        if (row.BusinessCentralUrl is { } bcUrl)
        {
            items.Add(new PaletteResultItem("external", "Open in Business Central", "Opens in a new tab", bcUrl));
        }
        if (row.AdminCentreUrl is { } adminUrl)
        {
            items.Add(new PaletteResultItem("external", "Open the admin centre", "Opens in a new tab", adminUrl));
        }
        items.Add(new PaletteResultItem(
            "solution", row.ProjectName, "Solution",
            $"/solutions/{row.ProjectId.ToString(CultureInfo.InvariantCulture)}"));

        return new PaletteContextBlock(row.EnvironmentName, items);
    }

    private static PaletteResultItem Tab(string title, string href) => new("tab", title, null, href);

    // ── Where you have been ─────────────────────────────────────────────

    private enum RecentKind { Solution, Environment, Release, Recipe, GoTo }

    /// <summary>
    /// Each recent recognised by exactly one route pattern, then checked in one
    /// read per kind through the rule the matching page and palette source use.
    /// The answer keeps the order it was given and carries the server's own
    /// title, so a renamed solution shows its new name and nothing the browser
    /// stored is ever printed back.
    /// </summary>
    private async Task<List<PaletteResultItem>> CheckRecentsAsync(
        ClaimsPrincipal user, ProjectAccess.AccessSnapshot snapshot, IReadOnlyList<string>? recents, CancellationToken ct)
    {
        if (recents is null || recents.Count == 0) return [];

        var parsed = new List<(RecentKind Kind, int Id, string Href)>(MaxRecents);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var href in recents)
        {
            if (parsed.Count == MaxRecents) break;
            if (string.IsNullOrEmpty(href) || href.Length > MaxHrefLength || !seen.Add(href)) continue;
            if (Classify(href) is { } entry) parsed.Add(entry);
        }
        if (parsed.Count == 0) return [];

        var found = new Dictionary<string, PaletteResultItem>(StringComparer.Ordinal);

        var solutionIds = IdsOf(parsed, RecentKind.Solution);
        if (solutionIds.Count > 0 && _tools.IsEnabled(ToolKey.Projects, user))
        {
            var rows = await _db.OeProjects.AsNoTracking()
                .Where(p => solutionIds.Contains(p.Id) && p.DeletedAt == null)
                .Where(ProjectAccess.VisibleProjectPredicate(snapshot))
                .Select(p => new { p.Id, p.Name, p.ShortName, p.HostingType, p.BcVersion })
                .ToListAsync(ct).ConfigureAwait(false);
            // The same version the Solutions source and the list show - see
            // ProjectCustomerInfoService.ReadProductionFactsAsync.
            var production = rows.Count == 0
                ? new Dictionary<int, ProductionEnvironmentFacts>()
                : await ProjectCustomerInfoService.ReadProductionFactsAsync(_db, rows.Select(r => r.Id).ToList(), ct).ConfigureAwait(false);
            foreach (var r in rows)
            {
                var href = $"/solutions/{r.Id.ToString(CultureInfo.InvariantCulture)}";
                var version = production.TryGetValue(r.Id, out var facts) ? facts.Version : r.BcVersion;
                found[href] = new PaletteResultItem(
                    "solution", r.Name, SolutionPaletteSource.Describe(r.ShortName, r.HostingType, version), href);
            }
        }

        var environmentIds = IdsOf(parsed, RecentKind.Environment);
        if (environmentIds.Count > 0 && _tools.IsEnabled(ToolKey.Projects, user))
        {
            // The environment page's own rule (UpgradeFleetService.GetEnvironmentAsync):
            // an environment Business Central still reports, of a solution the caller
            // may see. A deleted one keeps its page, so it keeps its recent.
            var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
            var rows = await _db.OeProjectEnvironments.AsNoTracking()
                .Where(e => environmentIds.Contains(e.Id) && e.MissingSince == null)
                .Where(e => _db.OeProjects.Where(visible).Any(p => p.Id == e.ProjectId && p.DeletedAt == null))
                .Select(e => new { e.Id, e.Name, e.Type, e.Version, e.Status, ProjectName = e.Project!.Name })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var r in rows)
            {
                var href = $"/environments/{r.Id.ToString(CultureInfo.InvariantCulture)}";
                found[href] = new PaletteResultItem(
                    "environment", r.Name, EnvironmentPaletteSource.Describe(r.ProjectName, r.Type, r.Version, r.Status), href);
            }
        }

        var releaseIds = IdsOf(parsed, RecentKind.Release);
        if (releaseIds.Count > 0 && _tools.IsEnabled(ToolKey.ObjectExplorer, user))
        {
            // The Releases source's filter: a failed import holds no objects.
            var rows = await _db.OeReleases.AsNoTracking()
                .Where(r => releaseIds.Contains(r.Id) && r.DeletedAt == null && r.Status != "failed")
                .Where(_access.VisibleReleasePredicate(snapshot))
                .Select(r => new { r.Id, r.Label, r.BcVersion })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var r in rows)
            {
                var href = $"/object-explorer/release/{r.Id.ToString(CultureInfo.InvariantCulture)}";
                found[href] = new PaletteResultItem(
                    "release", r.Label, string.IsNullOrWhiteSpace(r.BcVersion) ? null : r.BcVersion.Trim(), href);
            }
        }

        var recipeIds = IdsOf(parsed, RecentKind.Recipe);
        if (recipeIds.Count > 0 && _tools.IsEnabled(ToolKey.Cookbook, user))
        {
            // The Recipes source's filter.
            var rows = await _db.Recipes.AsNoTracking()
                .Where(r => recipeIds.Contains(r.Id) && r.DeletedAt == null && !r.Deprecated)
                .Select(r => new { r.Id, r.Title })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var r in rows)
            {
                var href = $"/cookbook/{r.Id.ToString(CultureInfo.InvariantCulture)}";
                found[href] = new PaletteResultItem("recipe", r.Title, "Recipe", href);
            }
        }

        if (parsed.Any(p => p.Kind == RecentKind.GoTo))
        {
            // Checked against the list the palette itself renders for this person,
            // so a page the sidebar would not offer them is not offered here either.
            var viewer = PaletteViewer.For(user, _orgContext, _singleTenant, _availability, snapshot.CanUseEnvironmentOps);
            foreach (var destination in NavDestinations.VisibleTo(viewer))
            {
                var subtitle = destination.SubtitleFor(viewer);
                found.TryAdd(destination.Href, new PaletteResultItem(
                    "goto", destination.LabelFor(viewer), subtitle.Length == 0 ? null : subtitle, destination.Href));
            }
        }

        var answer = new List<PaletteResultItem>(parsed.Count);
        foreach (var (_, _, href) in parsed)
        {
            if (found.TryGetValue(href, out var item)) answer.Add(item);
        }
        return answer;
    }

    private static List<int> IdsOf(List<(RecentKind Kind, int Id, string Href)> parsed, RecentKind kind) =>
        parsed.Where(p => p.Kind == kind).Select(p => p.Id).Distinct().ToList();

    /// <summary>
    /// Which kind of recent <paramref name="href"/> is, or null when it is none of
    /// them. A record link has to be exactly its canonical form - no query, no
    /// trailing segment - and anything else in-app has to be a "Go to" page, which
    /// is checked against the list rather than trusted here.
    /// </summary>
    private static (RecentKind Kind, int Id, string Href)? Classify(string href)
    {
        var match = RecordLink().Match(href);
        if (match.Success)
        {
            if (!int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return null;
            }
            return (RecordKinds[match.Groups[1].Value], id, href);
        }

        return NavDestinations.All.Any(d => string.Equals(d.Href, href, StringComparison.Ordinal))
            ? (RecentKind.GoTo, 0, href)
            : null;
    }

    /// <summary>The first path segment of each record link <see cref="RecordLink"/> accepts, and what it is.</summary>
    private static readonly Dictionary<string, RecentKind> RecordKinds = new(StringComparer.Ordinal)
    {
        ["solutions"] = RecentKind.Solution,
        ["environments"] = RecentKind.Environment,
        ["object-explorer/release"] = RecentKind.Release,
        ["cookbook"] = RecentKind.Recipe,
    };

    [GeneratedRegex(@"^/(solutions|environments|object-explorer/release|cookbook)/([1-9][0-9]{0,8})$", RegexOptions.CultureInvariant)]
    private static partial Regex RecordLink();
}

/// <summary>
/// What <c>GET /palette/context</c> answers. The JSON names are a contract with
/// <c>wwwroot/command-palette.js</c>, which is not compiled against this file -
/// spelled out for the reason <see cref="PaletteSearchResult"/> gives.
/// </summary>
/// <param name="Context">The record the page is about, or null.</param>
/// <param name="Recents">The recents the caller may still open, in the order asked, with the server's titles.</param>
public sealed record PaletteContextResult(
    [property: JsonPropertyName("context")] PaletteContextBlock? Context,
    [property: JsonPropertyName("recents")] IReadOnlyList<PaletteResultItem> Recents)
{
    public static readonly PaletteContextResult Empty = new(null, []);
}

/// <summary>One record's own destinations, under its name.</summary>
/// <param name="Label">The heading: the record's name ("CRONUS Coffee A/S").</param>
/// <param name="Items">
/// Rows of kind <c>tab</c> (a tab of this page), <c>environment</c>,
/// <c>solution</c>, <c>build</c> and <c>external</c> - the last is the one kind
/// whose link leaves the app, and the script accepts an <c>https://</c> link
/// for it and for nothing else.
/// </param>
public sealed record PaletteContextBlock(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("items")] IReadOnlyList<PaletteResultItem> Items);
