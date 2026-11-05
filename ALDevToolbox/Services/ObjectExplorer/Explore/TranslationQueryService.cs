using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Explore;

/// <summary>
/// Read-only queries over <c>oe_module_translations</c>: the per-release and
/// per-module language summaries that drive the admin translations page, and
/// the substring search that backs the MCP <c>search_translations</c> tool.
/// Split out of <see cref="ObjectExplorerService"/> so the translation surface
/// stands on its own. All methods are <c>AsNoTracking</c> and respect the
/// tenant query filter on <see cref="AppDbContext"/>.
/// </summary>
public sealed class TranslationQueryService
{
    private readonly AppDbContext _db;

    public TranslationQueryService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Lists every target language that has at least one translation row in
    /// the release, with a per-language row count. Drives the MCP
    /// <c>list_translation_languages</c> tool and the admin "languages
    /// uploaded" chips on the per-release translations admin page.
    ///
    /// Filter uses a subquery against <c>oe_modules</c> rather than the
    /// <c>Module</c> navigation property: the Npgsql provider can't
    /// translate <c>GroupBy</c> when its source query carries a
    /// nav-property join, but the equivalent <c>WHERE EXISTS</c> form
    /// generates clean SQL. Same goes for <see cref="ListModuleTranslationLanguagesAsync"/>.
    /// We project to an anonymous type first and remap to the record DTO
    /// in memory so the EF translator doesn't have to materialise a
    /// record constructor inside the grouped select.
    /// </summary>
    public async Task<List<TranslationLanguageSummary>> ListTranslationLanguagesAsync(
        int releaseId, CancellationToken ct = default)
    {
        var rows = await _db.OeModuleTranslations.AsNoTracking()
            .Where(t => _db.OeModules.Any(m => m.Id == t.ModuleId && m.ReleaseId == releaseId))
            .GroupBy(t => t.LanguageCode)
            .Select(g => new { LanguageCode = g.Key, Count = g.Count() })
            .OrderBy(x => x.LanguageCode)
            .ToListAsync(ct);
        return rows.Select(r => new TranslationLanguageSummary(r.LanguageCode, r.Count)).ToList();
    }

    /// <summary>
    /// Per-module, per-language counts — drives the admin translations
    /// page so each module row can show "da-DK · 1,247  de-DE · 1,250"
    /// chips and a per-module upload button. Same subquery + remap
    /// shape as <see cref="ListTranslationLanguagesAsync"/>.
    /// </summary>
    public async Task<List<ModuleTranslationLanguageRow>> ListModuleTranslationLanguagesAsync(
        int releaseId, CancellationToken ct = default)
    {
        var rows = await _db.OeModuleTranslations.AsNoTracking()
            .Where(t => _db.OeModules.Any(m => m.Id == t.ModuleId && m.ReleaseId == releaseId))
            .GroupBy(t => new { t.ModuleId, t.LanguageCode })
            .Select(g => new { g.Key.ModuleId, g.Key.LanguageCode, Count = g.Count() })
            .OrderBy(x => x.ModuleId).ThenBy(x => x.LanguageCode)
            .ToListAsync(ct);
        return rows.Select(r => new ModuleTranslationLanguageRow(r.ModuleId, r.LanguageCode, r.Count)).ToList();
    }

    /// <summary>
    /// Substring search over <c>oe_module_translations.target_text</c> within
    /// a release. Backs the MCP <c>search_translations</c> tool used by an
    /// agent to map a native-language caption / error message back to the
    /// AL object that produced it. The default kind filter
    /// (<c>caption,label</c>) honours the user's stated priority — captions
    /// + errors first; tooltips are opt-in via <c>kinds=any</c>.
    /// </summary>
    public async Task<List<TranslationMatch>> SearchTranslationsInReleaseAsync(
        int releaseId,
        string query,
        string? language,
        IReadOnlySet<string>? kindFilter,
        string? moduleNamePattern,
        int maxResults,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<TranslationMatch>();
        // ILike (escaped) rather than ToLower().Contains — see #385.
        var targetPattern = "%" + ObjectSearchService.EscapeLike(query.Trim()) + "%";

        var q = _db.OeModuleTranslations.AsNoTracking()
            .Where(t => t.Module!.ReleaseId == releaseId)
            .Where(t => EF.Functions.ILike(t.TargetText, targetPattern, "\\"));

        if (!string.IsNullOrWhiteSpace(language))
        {
            // Normalise the user-supplied language to the same shape we
            // store (xx-XX). Hand-crafted MCP callers might pass "da" or
            // "da_DK" — split on '-' / '_' and uppercase the region.
            var raw = language.Trim().Replace('_', '-');
            var dash = raw.IndexOf('-');
            string normalised;
            if (dash <= 0 || dash >= raw.Length - 1)
            {
                normalised = raw.ToLowerInvariant();
                q = q.Where(t => t.LanguageCode.StartsWith(normalised));
            }
            else
            {
                normalised = raw.Substring(0, dash).ToLowerInvariant() + "-" + raw.Substring(dash + 1).ToUpperInvariant();
                q = q.Where(t => t.LanguageCode == normalised);
            }
        }

        if (kindFilter is { Count: > 0 } && !kindFilter.Contains("any"))
        {
            q = q.Where(t => kindFilter.Contains(t.Kind));
        }

        if (!string.IsNullOrWhiteSpace(moduleNamePattern))
        {
            var modPat = "%" + ObjectSearchService.EscapeLike(moduleNamePattern.Trim()) + "%";
            q = q.Where(t => EF.Functions.ILike(t.Module!.Name, modPat, "\\"));
        }

        return await q.OrderBy(t => t.Module!.Name).ThenBy(t => t.ObjectName)
            .Take(maxResults)
            .Select(t => new TranslationMatch(
                t.Id,
                t.LanguageCode,
                t.Module!.Name,
                t.ObjectKind,
                t.ObjectName,
                t.SubKind,
                t.SubName,
                t.PropertyName,
                t.Kind,
                t.SourceText,
                t.TargetText,
                t.SymbolId))
            .ToListAsync(ct);
    }
}
