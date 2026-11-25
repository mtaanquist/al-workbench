using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.BcQuality;

/// <summary>One ranked search hit: enough for an agent to decide whether to open the article.</summary>
public sealed record BcQualitySearchHit(
    string Id,
    string Title,
    string Domain,
    string Layer,
    string BcVersion,
    IReadOnlyList<string> Keywords,
    string Summary,
    string Snippet,
    int SampleCount);

/// <summary>An article in full, with the sample files that ship beside it.</summary>
public sealed record BcQualityArticleDetail(
    string Id,
    string Title,
    string Domain,
    string Layer,
    string BcVersion,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Technologies,
    IReadOnlyList<string> Countries,
    IReadOnlyList<string> ApplicationAreas,
    string Summary,
    string Content,
    IReadOnlyList<BcQualitySampleDetail> Samples,
    string CommitSha);

/// <summary>A sample file, cited by its repo-relative path so the agent can quote the source.</summary>
public sealed record BcQualitySampleDetail(string Path, string Kind, string Language, string Content);

/// <summary>
/// Reads the mirrored BCQuality knowledge base. Behaviour is specified in
/// <c>.design/bcquality.md</c>.
///
/// <para>
/// Search is Postgres full-text search over a weighted, stored
/// <c>tsvector</c> column: a title hit outranks a keyword hit outranks a body
/// hit, which is what makes ranking mean anything on a corpus where every
/// article is about AL. The query side uses <c>websearch_to_tsquery</c>, so an
/// agent can pass quoted phrases and <c>-excluded</c> terms and get the
/// behaviour it expects from a search box instead of a syntax error.
/// </para>
///
/// <para>
/// The tables carry no organisation and no query filter (public Microsoft
/// content, identical for every tenant), so nothing on this path calls
/// <c>IgnoreQueryFilters()</c> — there is no filter to escape.
/// </para>
/// </summary>
public sealed class BcQualitySearchService
{
    /// <summary>Cap on returned hits. High enough for a review sweep, low enough that an agent's context survives it.</summary>
    public const int MaxResults = 50;

    private const int DefaultResults = 10;

    /// <summary>Roughly how much body text a snippet shows around the first matching term.</summary>
    private const int SnippetLength = 320;

    private readonly AppDbContext _db;

    public BcQualitySearchService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>True once at least one article has been ingested. Lets a caller tell "no matches" from "no mirror yet".</summary>
    public Task<bool> HasContentAsync(CancellationToken ct = default) =>
        _db.BcQualityArticles.AsNoTracking().AnyAsync(ct);

    /// <summary>
    /// Ranked full-text search across the knowledge base.
    /// </summary>
    /// <param name="query">Free text. Accepts the <c>websearch</c> dialect: bare words, "quoted phrases", <c>-excluded</c>.</param>
    /// <param name="bcVersion">
    /// A BC major version (e.g. 26). When supplied, only articles applicable to
    /// it come back — the <c>[all]</c> sentinel, an explicit list containing it,
    /// or an open-ended range at or below it. Omitting it means no version
    /// filtering rather than a guess.
    /// </param>
    /// <param name="domain">Optional domain tag (<c>performance</c>, <c>ui</c>, ...). Matched case-insensitively.</param>
    /// <param name="limit">Maximum hits, clamped to <see cref="MaxResults"/>.</param>
    /// <exception cref="PlanValidationException">The query is empty, or the BC version is not a plausible major version.</exception>
    public async Task<List<BcQualitySearchHit>> SearchAsync(
        string query,
        int? bcVersion = null,
        string? domain = null,
        int limit = DefaultResults,
        CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(query))
        {
            errors["query"] = "Enter something to search for.";
        }
        // BC majors have run in the teens and twenties since the AL era; a
        // negative or four-digit value is a caller passing a build number or a
        // year, and silently returning nothing would hide the mistake.
        if (bcVersion is { } version && (version < 1 || version > 999))
        {
            errors["bcVersion"] = "Give a Business Central major version, such as 26.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var take = Math.Clamp(limit <= 0 ? DefaultResults : limit, 1, MaxResults);
        var trimmed = query.Trim();

        var rows = _db.BcQualityArticles.AsNoTracking()
            .Where(a => a.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("english", trimmed)));

        if (bcVersion is { } target)
        {
            rows = rows.Where(a => a.BcVersionAll
                || (a.BcVersionFrom != null && target >= a.BcVersionFrom)
                || a.BcVersions.Contains(target));
        }

        if (!string.IsNullOrWhiteSpace(domain))
        {
            var wanted = domain.Trim().ToLowerInvariant();
            rows = rows.Where(a => a.Domain == wanted);
        }

        var hits = await rows
            .OrderByDescending(a => a.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery("english", trimmed)))
            // A deterministic tiebreak so equally-ranked hits come back in the
            // same order every call.
            .ThenBy(a => a.ArticleKey)
            .Take(take)
            .Select(a => new
            {
                a.ArticleKey,
                a.Title,
                a.Domain,
                a.Layer,
                a.BcVersionRaw,
                a.Keywords,
                a.Summary,
                a.Content,
                SampleCount = a.Samples.Count,
            })
            .ToListAsync(ct);

        return hits.Select(h => new BcQualitySearchHit(
            h.ArticleKey,
            h.Title,
            h.Domain,
            h.Layer,
            h.BcVersionRaw,
            h.Keywords,
            h.Summary,
            BuildSnippet(h.Content, h.Summary, trimmed),
            h.SampleCount)).ToList();
    }

    /// <summary>
    /// Returns one article in full by its repo-relative path, with its sample
    /// files and the upstream commit the mirror is at. Null when no such
    /// article is in the mirror.
    /// </summary>
    public async Task<BcQualityArticleDetail?> GetAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["id"] = "Give the article path, for example microsoft/knowledge/ui/no-nested-grids.md.",
            });
        }

        var key = NormaliseKey(id);
        var row = await _db.BcQualityArticles.AsNoTracking()
            .Include(a => a.Samples)
            .FirstOrDefaultAsync(a => a.ArticleKey == key, ct);

        // Callers cite articles by path and drop the extension often enough
        // that retrying once is cheaper than an unhelpful "not found".
        if (row is null && !key.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var withExtension = key + ".md";
            row = await _db.BcQualityArticles.AsNoTracking()
                .Include(a => a.Samples)
                .FirstOrDefaultAsync(a => a.ArticleKey == withExtension, ct);
        }
        if (row is null) return null;

        var commitSha = await _db.BcQualityIngestState.AsNoTracking()
            .Where(s => s.Id == BcQualityIngestState.SingletonId)
            .Select(s => s.CommitSha)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var folder = row.ArticleKey.Contains('/', StringComparison.Ordinal)
            ? row.ArticleKey[..row.ArticleKey.LastIndexOf('/')]
            : string.Empty;

        return new BcQualityArticleDetail(
            row.ArticleKey,
            row.Title,
            row.Domain,
            row.Layer,
            row.BcVersionRaw,
            row.Keywords,
            row.Technologies,
            row.Countries,
            row.ApplicationAreas,
            row.Summary,
            row.Content,
            row.Samples
                .OrderBy(s => s.FileName, StringComparer.Ordinal)
                .Select(s => new BcQualitySampleDetail(
                    folder.Length == 0 ? s.FileName : folder + "/" + s.FileName,
                    s.Kind,
                    s.Language,
                    s.Content))
                .ToList(),
            commitSha);
    }

    /// <summary>Accepts a citation path in the forms callers actually paste: leading slash, backslashes, stray whitespace.</summary>
    private static string NormaliseKey(string id) =>
        id.Trim().Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// A plain-text window of the body around the first query term, so a hit
    /// list shows why each article matched. Built here rather than with
    /// <c>ts_headline</c>: the highlighting function is the expensive half of
    /// Postgres FTS (it re-parses each document), and an agent reading the
    /// result has no use for the <c>&lt;b&gt;</c> markup it adds.
    /// </summary>
    internal static string BuildSnippet(string content, string summary, string query)
    {
        var terms = query
            .Split([' ', '\t', '\n', '"', '\'', ',', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('-'))
            .Where(t => t.Length > 2)
            .ToList();

        var index = -1;
        foreach (var term in terms)
        {
            index = content.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) break;
        }

        // No term in the body: the description paragraph is the honest fallback
        // (the match may have come from the title or the keywords).
        if (index < 0)
        {
            return summary.Length <= SnippetLength ? summary : summary[..SnippetLength].TrimEnd() + "...";
        }

        var start = Math.Max(0, index - SnippetLength / 3);
        var length = Math.Min(SnippetLength, content.Length - start);
        var window = Flatten(content.Substring(start, length));
        var prefix = start > 0 ? "..." : string.Empty;
        var suffix = start + length < content.Length ? "..." : string.Empty;
        return prefix + window + suffix;
    }

    /// <summary>
    /// Folds a window of markdown into one readable line: drop the heading
    /// markers (the hit already carries Title separately, so repeating "#" runs
    /// is noise) and collapse the whitespace the fold leaves behind.
    /// </summary>
    private static string Flatten(string window)
    {
        var parts = window
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim().TrimStart('#').Trim())
            .Where(line => line.Length > 0);
        var text = string.Join(' ', parts);
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }
        return text.Trim();
    }
}
