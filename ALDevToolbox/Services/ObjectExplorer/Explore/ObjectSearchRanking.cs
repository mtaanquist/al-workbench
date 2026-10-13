using System.Linq.Expressions;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Explore;

/// <summary>
/// Search-box tokenizing and result ranking for cross-module object search,
/// extracted from <see cref="ObjectExplorerService"/> so the parsing/ranking
/// rules can be read and tested on their own. Pure aside from the bounded
/// <see cref="ExecuteAndRankAsync"/> materialisation, which runs the supplied
/// EF query and re-orders the page in memory.
/// </summary>
internal static class ObjectSearchRanking
{
    private readonly record struct SearchToken(string Text, bool Negated, bool Quoted);

    // ILike pattern builders. Each escapes the user term's `%`/`_` wildcards
    // (via the shared <see cref="ObjectSearchService.EscapeLike"/>, paired with
    // the "\\" escape-char argument on EF.Functions.ILike) so a literal wildcard
    // matches literally, then wraps it for the match mode. The raw (unescaped)
    // term is what the in-memory ranker scores, so callers pass that separately.
    private static string Contains(string term) => "%" + ObjectSearchService.EscapeLike(term) + "%";
    private static string Prefix(string term) => ObjectSearchService.EscapeLike(term) + "%";
    private static string Suffix(string term) => "%" + ObjectSearchService.EscapeLike(term);
    private static string Exact(string term) => ObjectSearchService.EscapeLike(term);

    /// <summary>
    /// Maps a search-box kind prefix to the canonical lower-case
    /// <see cref="OeModuleObject.Kind"/> value. Both a short letter form
    /// (<c>t</c>) and the full kind name (<c>table</c>) point at the same
    /// kind so users can type whichever they remember; lookups are
    /// case-insensitive. When Microsoft adds a new object kind, add its
    /// shortcut + full name here so <c>kind:name</c> search keeps working.
    /// </summary>
    private static readonly Dictionary<string, string> KindPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["t"] = "table",                       ["table"] = "table",
        ["p"] = "page",                        ["page"] = "page",
        ["c"] = "codeunit",                    ["codeunit"] = "codeunit",
        ["r"] = "report",                      ["report"] = "report",
        ["q"] = "query",                       ["query"] = "query",
        ["x"] = "xmlport",                     ["xmlport"] = "xmlport",
        ["e"] = "enum",                        ["enum"] = "enum",
        ["i"] = "interface",                   ["interface"] = "interface",
        ["ps"] = "permissionset",              ["permissionset"] = "permissionset",
        ["ca"] = "controladdin",               ["controladdin"] = "controladdin",
        ["te"] = "tableextension",             ["tableextension"] = "tableextension",
        ["pe"] = "pageextension",              ["pageextension"] = "pageextension",
        ["re"] = "reportextension",            ["reportextension"] = "reportextension",
        ["ee"] = "enumextension",              ["enumextension"] = "enumextension",
        ["pse"] = "permissionsetextension",    ["permissionsetextension"] = "permissionsetextension",
        ["ms"] = "menusuite",                  ["menusuite"] = "menusuite",
        ["pr"] = "profile",                    ["profile"] = "profile",
        // Legacy C/AL kinds from pre-2013 exports.
        ["form"] = "form",                     ["dataport"] = "dataport",
    };

    /// <summary>
    /// True when <paramref name="prefix"/> is a recognised <c>kind:</c> prefix.
    ///
    /// Exposed so the search-tips panel can state the rule it teaches -- "the
    /// badge in the Type column, lower-cased, is the prefix" (#575) -- and so a
    /// test can hold that rule true as kinds are added. Without it the panel
    /// would restate a private table and drift from it silently.
    /// </summary>
    public static bool IsKindPrefix(string prefix) => KindPrefixes.ContainsKey(prefix);

    /// <summary>
    /// Splits a leading <c>kind:</c> prefix off the search string so a user
    /// can scope a search to one object kind from the box (<c>t:item</c> →
    /// tables named "item", <c>table:item</c> likewise, bare <c>t:</c> →
    /// every table). Only the <em>first</em> whitespace-delimited token is
    /// inspected, and only when it isn't quoted (<c>"t:foo"</c> is a literal
    /// phrase) or negated (<c>-t:foo</c> stays literal). An unrecognised
    /// prefix (<c>foo:bar</c>) returns <c>(null, original)</c> so it falls
    /// through to the normal name match — safe because AL object names never
    /// contain a colon. The returned kind is meant to be ANDed into the
    /// caller's kind filter; the remainder feeds <see cref="ApplySearchTokens"/>.
    /// </summary>
    public static (string? Kind, string Remainder) ExtractKindPrefix(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return (null, search ?? string.Empty);

        var trimmed = search.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is '"' or '-') return (null, search);

        // Only the first token may carry the prefix; `t:sales p:invoice`
        // scopes by table and leaves `p:invoice` as literal text.
        var end = trimmed.IndexOf(' ');
        var firstToken = end < 0 ? trimmed : trimmed[..end];
        var colon = firstToken.IndexOf(':');
        if (colon <= 0) return (null, search);

        var prefix = firstToken[..colon];
        if (!KindPrefixes.TryGetValue(prefix, out var kind)) return (null, search);

        // Strip `prefix:` from the first token, keep the rest of the string
        // (the remainder may be empty for a bare `t:`).
        var rest = firstToken[(colon + 1)..];
        var tail = end < 0 ? string.Empty : trimmed[end..];
        return (kind, (rest + tail).Trim());
    }

    /// <summary>
    /// Trims, lower-cases, and de-duplicates a kind filter list, collapsing an
    /// empty/all-blank list to null so callers can treat "no kinds" uniformly.
    /// </summary>
    public static IReadOnlyList<string>? NormalizeKinds(IReadOnlyList<string>? kinds)
    {
        if (kinds is null) return null;
        var result = kinds
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Applies the parsed search tokens to <paramref name="q"/> as
    /// case-insensitive name filters (a leading <c>-</c> negates), with one
    /// legacy special case: a single bare numeric token also matches by
    /// <c>ObjectId</c>. Returns the positive token texts so the caller can
    /// rank matches; negated tokens only filter and never contribute a score.
    /// </summary>
    public static (IQueryable<OeModuleObject> Query, IReadOnlyList<string> Tokens)
        ApplySearchTokens(IQueryable<OeModuleObject> q, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return (q, Array.Empty<string>());

        var tokens = TokenizeSearch(search);
        if (tokens.Count == 0)
            return (q, Array.Empty<string>());

        if (tokens.Count == 1 && tokens[0] is { Negated: false, Quoted: false } single
            && int.TryParse(single.Text, out var asInt))
        {
            var pattern = Contains(single.Text);
            // The C/AL Version List joins the match here too, so a bare "111"
            // still finds objects tagged "NAVW111.00" (issue #271 applies to
            // the numeric fast-path as much as the text path below).
            q = q.Where(o => o.ObjectId == asInt
                || EF.Functions.ILike(o.Name, pattern, "\\")
                || (o.VersionList != null && EF.Functions.ILike(o.VersionList, pattern, "\\")));
            // Numeric path: ranking by name tokens would be misleading
            // because the id branch matched without a name hit.
            return (q, Array.Empty<string>());
        }

        var rankTokens = new List<string>();
        foreach (var token in tokens)
        {
            var text = token.Text;

            // Quoted run → exact match: the whole object name, or (for a
            // numeric) the exact ObjectId. Stricter than the substring default
            // so `"36"` / `"Sales Header"` return only exact hits. Exact is
            // all-or-nothing, so it never contributes a ranking token.
            // ILike with no wildcards = case-insensitive equality (see #385);
            // the escaped term can't smuggle in a `%`/`_` wildcard.
            if (token.Quoted)
            {
                var exact = Exact(text);
                q = int.TryParse(text, out var exactId)
                    ? ApplyPredicate(q, token.Negated, o => o.ObjectId == exactId || EF.Functions.ILike(o.Name, exact, "\\"))
                    : ApplyPredicate(q, token.Negated, o => EF.Functions.ILike(o.Name, exact, "\\"));
                continue;
            }

            // `lo..hi` → inclusive ObjectId range (e.g. 50000..99999).
            if (TryParseIdRange(text, out var lo, out var hi))
            {
                q = ApplyPredicate(q, token.Negated,
                    o => o.ObjectId != null && o.ObjectId >= lo && o.ObjectId <= hi);
                continue;
            }

            // `sales*` / `*sales` / `*sales*` → anchored glob on the name.
            // All three map to ILike patterns so the pg_trgm GIN index on
            // name backs the scan instead of a full table scan (#385 + the
            // trigram indexes on oe_module_objects).
            var (globKind, needle) = ParseGlob(text);
            if (globKind != GlobKind.None)
            {
                if (needle.Length == 0) continue; // a bare `*` matches everything
                switch (globKind)
                {
                    case GlobKind.Prefix:
                    {
                        var p = Prefix(needle);
                        q = ApplyPredicate(q, token.Negated, o => EF.Functions.ILike(o.Name, p, "\\"));
                        break;
                    }
                    case GlobKind.Suffix:
                    {
                        var p = Suffix(needle);
                        q = ApplyPredicate(q, token.Negated, o => EF.Functions.ILike(o.Name, p, "\\"));
                        break;
                    }
                    // Contains also matches the C/AL Version List, so `*CON*`
                    // finds objects tagged with it (issue #271).
                    default:
                    {
                        var p = Contains(needle);
                        q = ApplyPredicate(q, token.Negated,
                            o => EF.Functions.ILike(o.Name, p, "\\")
                                || (o.VersionList != null && EF.Functions.ILike(o.VersionList, p, "\\")));
                        break;
                    }
                }
                if (!token.Negated) rankTokens.Add(needle);
                continue;
            }

            // Default: case-insensitive substring on the name or the C/AL
            // Version List (so a bare `CON001` finds version-list tagged objects).
            var cp = Contains(text);
            if (token.Negated)
            {
                q = q.Where(o => !(EF.Functions.ILike(o.Name, cp, "\\")
                    || (o.VersionList != null && EF.Functions.ILike(o.VersionList, cp, "\\"))));
            }
            else
            {
                q = q.Where(o => EF.Functions.ILike(o.Name, cp, "\\")
                    || (o.VersionList != null && EF.Functions.ILike(o.VersionList, cp, "\\")));
                rankTokens.Add(text);
            }
        }
        return (q, rankTokens);
    }

    /// <summary>
    /// Hand-rolled tokenizer for the search box. Walks the string once,
    /// honouring an optional leading <c>-</c> (negation) and double-quoted
    /// runs (a literal phrase, spaces and all). Tokens are lower-cased for
    /// case-insensitive matching; empty tokens (a lone <c>-</c> or <c>""</c>)
    /// are dropped.
    /// </summary>
    private static List<SearchToken> TokenizeSearch(string search)
    {
        var tokens = new List<SearchToken>();
        var i = 0;
        var n = search.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(search[i])) i++;
            if (i >= n) break;

            var negated = search[i] == '-';
            if (negated) i++;

            string text;
            bool quoted;
            if (i < n && search[i] == '"')
            {
                quoted = true;
                i++; // opening quote
                var start = i;
                while (i < n && search[i] != '"') i++;
                text = search[start..i];
                if (i < n) i++; // closing quote
            }
            else
            {
                quoted = false;
                var start = i;
                while (i < n && !char.IsWhiteSpace(search[i])) i++;
                text = search[start..i];
            }

            if (text.Length == 0) continue;
            tokens.Add(new SearchToken(text.ToLowerInvariant(), negated, quoted));
        }
        return tokens;
    }

    /// <summary>
    /// Wraps <paramref name="predicate"/> in a <c>Where</c>, negating it (via
    /// <see cref="Expression.Not"/>) when <paramref name="negated"/> is set, so
    /// a leading <c>-</c> works uniformly across exact / range / glob tokens.
    /// </summary>
    private static IQueryable<OeModuleObject> ApplyPredicate(
        IQueryable<OeModuleObject> q, bool negated, Expression<Func<OeModuleObject, bool>> predicate)
    {
        if (!negated) return q.Where(predicate);
        var not = Expression.Lambda<Func<OeModuleObject, bool>>(
            Expression.Not(predicate.Body), predicate.Parameters);
        return q.Where(not);
    }

    /// <summary>
    /// Parses an inclusive id-range token of the form <c>lo..hi</c> (e.g.
    /// <c>50000..99999</c>). Reversed bounds are tolerated. Returns false for
    /// anything that isn't exactly two integers around a single <c>..</c>
    /// (so open-ended <c>50000..</c> / <c>..99999</c> stay plain tokens).
    /// </summary>
    internal static bool TryParseIdRange(string text, out int lo, out int hi)
    {
        lo = 0;
        hi = 0;
        var dots = text.IndexOf("..", StringComparison.Ordinal);
        if (dots <= 0 || dots + 2 >= text.Length) return false;
        if (text.IndexOf("..", dots + 2, StringComparison.Ordinal) >= 0) return false; // only one ".."
        if (!int.TryParse(text[..dots], out lo) || !int.TryParse(text[(dots + 2)..], out hi))
            return false;
        if (lo > hi) (lo, hi) = (hi, lo);
        return true;
    }

    internal enum GlobKind { None, Prefix, Suffix, Contains }

    /// <summary>
    /// Classifies a <c>*</c>-bearing token into an anchored glob: <c>foo*</c>
    /// is a prefix match, <c>*foo</c> a suffix, <c>*foo*</c> (or an internal
    /// <c>*</c>) a contains. A token with no <c>*</c> returns
    /// <see cref="GlobKind.None"/> so the caller keeps the substring default.
    /// The needle is the token with its <c>*</c>s stripped.
    /// </summary>
    internal static (GlobKind Kind, string Needle) ParseGlob(string text)
    {
        if (!text.Contains('*')) return (GlobKind.None, text);
        var starts = text.StartsWith('*');
        var ends = text.EndsWith('*');
        var core = text.Replace("*", "");
        if (starts && ends) return (GlobKind.Contains, core);
        if (ends) return (GlobKind.Prefix, core);
        if (starts) return (GlobKind.Suffix, core);
        return (GlobKind.Contains, core); // internal '*': fall back to contains
    }

    /// <summary>
    /// Materialises the filtered query under the legacy (Kind, ObjectId,
    /// Name) DB order — that order remains the result contract when no
    /// search is supplied — and, when search tokens are present, ranks before
    /// the cap and again after it.
    /// <para>The cap has to come after a ranking step: cutting the Kind-ordered
    /// rows first let codeunits, enums and pages fill the page before a single
    /// table, so searching "item" on the Base Application never showed table 27
    /// "Item" (#1225). So the database orders by a coarse relevance
    /// (<see cref="RelevanceInDatabase"/>) before the <c>Take</c>, and the page
    /// is then re-ranked in memory with the finer rules SQL can't express
    /// cheaply (PascalCase boundaries, earliness).</para>
    /// </summary>
    public static async Task<List<ReleaseObjectMatch>> ExecuteAndRankAsync(
        IQueryable<OeModuleObject> q,
        IReadOnlyList<string> tokens,
        int take,
        CancellationToken ct)
    {
        var ordered = tokens.Count == 0
            ? q.OrderBy(o => o.Kind)
            : q.OrderByDescending(RelevanceInDatabase(tokens)).ThenBy(o => o.Kind);
        var rows = await ordered
            .ThenBy(o => o.ObjectId).ThenBy(o => o.Name)
            .Take(take)
            .Select(o => new ReleaseObjectMatch(
                o.Id, o.Kind, o.ObjectId, o.Name, o.Namespace,
                o.ModuleId, o.Module!.Name,
                o.SourceFileId, o.LineNumber,
                o.SourceFile != null ? o.SourceFile.LineCount : 0,
                o.VersionList))
            .ToListAsync(ct);

        if (tokens.Count == 0)
            return rows;

        var phrase = string.Join(' ', tokens);
        return rows
            .Select(r => (Row: r,
                Exact: string.Equals(r.Name, phrase, StringComparison.OrdinalIgnoreCase),
                Score: ScoreNameMatch(r.Name, tokens)))
            .OrderByDescending(x => x.Exact)
            .ThenByDescending(x => x.Score.BoundaryHits)
            .ThenByDescending(x => x.Score.Earliness)
            .ThenBy(x => x.Row.Kind)
            .ThenBy(x => x.Row.ObjectId)
            .ThenBy(x => x.Row.Name)
            .Select(x => x.Row)
            .ToList();
    }

    /// <summary>
    /// The relevance the database sorts by before the cap: a name equal to the
    /// whole search outranks everything, then one point per token that starts
    /// the name or a word in it (after a space, <c>.</c>, <c>-</c>, <c>_</c> or
    /// <c>/</c>). A coarser cut of <see cref="ScoreNameMatch"/>, which still
    /// orders the page afterwards.
    /// </summary>
    private static Expression<Func<OeModuleObject, int>> RelevanceInDatabase(IReadOnlyList<string> tokens)
    {
        var o = Expression.Parameter(typeof(OeModuleObject), "o");
        var phrase = Exact(string.Join(' ', tokens));
        Expression<Func<OeModuleObject, int>> exact = x => EF.Functions.ILike(x.Name, phrase, "\\") ? 100 : 0;
        Expression score = new ParameterSwap(exact.Parameters[0], o).Visit(exact.Body);
        foreach (var token in tokens)
        {
            var start = Prefix(token);
            var escaped = ObjectSearchService.EscapeLike(token);
            var afterSpace = "% " + escaped + "%";
            var afterDot = "%." + escaped + "%";
            var afterDash = "%-" + escaped + "%";
            var afterUnderscore = "%\\_" + escaped + "%"; // the separator itself escaped
            var afterSlash = "%/" + escaped + "%";
            Expression<Func<OeModuleObject, int>> hit = x =>
                EF.Functions.ILike(x.Name, start, "\\")
                || EF.Functions.ILike(x.Name, afterSpace, "\\")
                || EF.Functions.ILike(x.Name, afterDot, "\\")
                || EF.Functions.ILike(x.Name, afterDash, "\\")
                || EF.Functions.ILike(x.Name, afterUnderscore, "\\")
                || EF.Functions.ILike(x.Name, afterSlash, "\\")
                    ? 1 : 0;
            score = Expression.Add(score, new ParameterSwap(hit.Parameters[0], o).Visit(hit.Body));
        }
        return Expression.Lambda<Func<OeModuleObject, int>>(score, o);
    }

    /// <summary>Rebinds a lambda body to another parameter, so per-token lambdas can be summed into one.</summary>
    private sealed class ParameterSwap : ExpressionVisitor
    {
        private readonly ParameterExpression _from;
        private readonly ParameterExpression _to;

        public ParameterSwap(ParameterExpression from, ParameterExpression to)
        {
            _from = from;
            _to = to;
        }

        protected override Expression VisitParameter(ParameterExpression node) => node == _from ? _to : node;
    }

    /// <summary>
    /// Scores a candidate name against the parsed search tokens. Returns
    /// (a) the number of tokens whose first occurrence starts on a word
    /// boundary (start of name, after a separator, or at a lower→upper
    /// PascalCase transition), and (b) an "earliness" tally that favours
    /// tokens appearing near the front of the name. Higher is better on
    /// both axes; ties fall through to the Kind/ObjectId/Name tiebreakers.
    /// </summary>
    private static (int BoundaryHits, int Earliness) ScoreNameMatch(
        string name, IReadOnlyList<string> tokens)
    {
        var lower = name.ToLowerInvariant();
        var boundaryHits = 0;
        var earliness = 0;
        foreach (var token in tokens)
        {
            var idx = lower.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) continue;
            earliness -= idx;
            if (IsWordBoundary(name, idx))
                boundaryHits++;
        }
        return (boundaryHits, earliness);
    }

    private static bool IsWordBoundary(string original, int index)
    {
        if (index == 0) return true;
        if (index >= original.Length) return false;
        var prev = original[index - 1];
        if (prev is ' ' or '.' or '-' or '_' or '&' or '/' or ',' or '(' or ')')
            return true;
        // PascalCase boundary: a lowercase character followed by an
        // uppercase one acts like a word boundary for identifiers such as
        // SalesHeader, where "Header" should rank like a fresh word.
        return char.IsLower(prev) && char.IsUpper(original[index]);
    }
}
