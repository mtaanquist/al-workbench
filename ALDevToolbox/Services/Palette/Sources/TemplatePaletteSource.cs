using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Palette.Sources;

/// <summary>
/// Workspace templates, found by name and by key - the key being what a
/// template is called in a link and on its own page. Enter opens the template's
/// page, <c>/templates/{key}</c>, where "New workspace" starts from it. See
/// <c>.design/command-palette.md</c>, "Sources", and issue #885.
///
/// <para><b>What the Templates list shows, no more.</b> Soft-deleted templates
/// are left out. Deprecated ones are offered, because <c>/templates</c> lists
/// them too, and their subtitle says "Deprecated" the way that list's row does.
/// The gate is the browser's, not the admin pages': this lands on the page every
/// member may open, so every member may find it.</para>
///
/// <para>Modules are left out: the Templates list shows them, but a module has
/// no page of its own to land on, and a palette row that goes nowhere is a dead
/// end.</para>
///
/// <para><b>The fence.</b> Templates are organisation content, so the
/// organisation query filter is the whole of it - which also keeps the system
/// organisation's canonical templates out of every other organisation's
/// results. No <c>IgnoreQueryFilters()</c>. A small table, so no pre-filter.</para>
/// </summary>
public sealed class TemplatePaletteSource : IPaletteSource
{
    /// <summary>A ceiling on a pathological organisation, not a page size.</summary>
    public const int MaxTemplatesScanned = 2000;

    private readonly AppDbContext _db;
    private readonly ToolEnablement _tools;

    public TemplatePaletteSource(AppDbContext db, ToolEnablement tools)
    {
        _db = db;
        _tools = tools;
    }

    public string Id => "templates";

    public string Label => "Templates";

    public int Order => PaletteGroupOrder.Templates;

    /// <summary>
    /// Exactly the gate on the sidebar's Templates entry: signed in, and the
    /// Templates tool switched on site-wide and for this organisation. Not the
    /// content-author gate the admin template pages carry.
    /// </summary>
    public Task<bool> IsAvailableAsync(ClaimsPrincipal user, CancellationToken ct) =>
        Task.FromResult(user?.Identity?.IsAuthenticated == true && _tools.IsEnabled(ToolKey.Templates, user));

    public async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(
        PaletteQuery query, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsUsable || limit <= 0) return [];

        var rows = await _db.RuntimeTemplates.AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .OrderBy(t => t.Name)
            .Take(MaxTemplatesScanned)
            .Select(t => new { t.Key, t.Name, t.Runtime, t.Deprecated })
            .ToListAsync(ct);

        var candidates = rows.Select(r => new PaletteCandidate(
            "template",
            r.Name,
            Describe(r.Key, r.Runtime, r.Deprecated),
            $"/templates/{Uri.EscapeDataString(r.Key)}"));

        return PaletteRanking.Rank(query, candidates).Take(limit).Select(m => m.Candidate).ToList();
    }

    /// <summary>
    /// The key and the runtime - the first two facts on the template's own page,
    /// under the same labels - then "Deprecated" for one the list marks so. The
    /// key is shown rather than searched silently, so a row found by it says why.
    /// </summary>
    private static string Describe(string key, string? runtime, bool deprecated)
    {
        var parts = new List<string>(3) { key };
        if (!string.IsNullOrWhiteSpace(runtime)) parts.Add($"Runtime {runtime.Trim()}");
        if (deprecated) parts.Add("Deprecated");
        return string.Join(" - ", parts);
    }
}
