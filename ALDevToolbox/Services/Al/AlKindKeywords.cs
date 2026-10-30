using ALDevToolbox.Services.ObjectExplorer.Import;
using System;

namespace ALDevToolbox.Services.Al;

/// <summary>
/// Shared translation between the kind hints extraction sites pass around
/// (AL type keywords like <c>Record</c> / <c>Codeunit</c>, the
/// <c>Database::</c> typed-literal prefix, or an already-canonical catalog
/// kind) and the catalog kind values stored on
/// <c>oe_module_objects.kind</c>.
///
/// Lives here rather than inside the resolver because the extractor's test
/// doubles must rank candidates exactly the way the production
/// <c>CatalogResolver</c> does — otherwise a kind-disambiguation bug
/// (issue #712) can't produce a failing extractor test.
/// </summary>
public static class AlKindKeywords
{
    /// <summary>
    /// Maps a caller's kind hint to a catalog kind, or null when there is
    /// no usable hint. Two keywords don't pass through unchanged:
    /// <c>Record</c> (the AL keyword for a table-typed variable) and
    /// <c>Database</c> (the typed-literal prefix in
    /// <c>Database::"Customer"</c>) both mean <c>table</c>, as does
    /// <c>tabledata</c> in a permission set. Everything else differs
    /// only in casing.
    /// </summary>
    public static string? MapKeywordToKind(string? keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return null;
        var lower = keyword.ToLowerInvariant();
        // `Database::"X"` and `Record X` both name a table. Before this
        // mapping existed, `Database` produced the non-existent catalog
        // kind "database", every candidate missed the exact-kind bucket,
        // and `Database::User` in Base App landed on the User CODEUNIT.
        // `tabledata "Customer"` (permission sets) names the table too.
        if (lower is "record" or "database" or "tabledata") return "table";
        return lower;
    }

    /// <summary>
    /// The static dispatchers whose methods take the object they act on as a
    /// typed first argument: <c>Page.RunModal(Page::"Customer List")</c>,
    /// <c>Report.SaveAsPdf(Report::Statement, ...)</c>.
    /// </summary>
    public static bool IsStaticDispatcher(string? name) =>
        name is not null && StaticDispatchers.Contains(name);

    private static readonly HashSet<string> StaticDispatchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Codeunit", "Page", "Report", "Xmlport", "Query",
    };

    /// <summary>
    /// True for the extension kinds (<c>tableextension</c>,
    /// <c>pageextension</c>, …). A name shared between a base object and
    /// an extension of it should resolve to the base when the caller gave
    /// no kind hint.
    /// </summary>
    public static bool IsExtensionKind(string kind) =>
        kind.EndsWith("extension", StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// True for member kinds that are invoked rather than read — the
    /// procedure family, triggers, and event publishers/subscribers.
    /// AL lets a parameterless call drop its parentheses
    /// (<c>Rec.Modify;</c>, <c>Customer.Insert;</c>), so syntax alone
    /// can't tell a call from a field read; the resolved member's kind
    /// can. Anything else (<c>table_field</c>, <c>page_field</c>, …) is
    /// a read. See issue #712.
    /// </summary>
    public static bool IsCallableMemberKind(string? memberKind)
    {
        if (string.IsNullOrEmpty(memberKind)) return false;
        if (memberKind.Contains("procedure", StringComparison.OrdinalIgnoreCase)) return true;
        return memberKind.Equals("trigger", StringComparison.OrdinalIgnoreCase)
            || memberKind.Equals("method", StringComparison.OrdinalIgnoreCase)
            || memberKind.Equals("event_publisher", StringComparison.OrdinalIgnoreCase)
            || memberKind.Equals("event_subscriber", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The deliberate order candidate objects sharing a name are ranked
    /// in when the click carries no kind hint: the kinds a developer is
    /// most likely to mean first, and every base object ahead of the
    /// extension kind that extends it. Alphabetical order used to decide
    /// this, which put <c>codeunit</c> ahead of <c>table</c> — so a click
    /// on <c>User</c> landed on codeunit <c>User</c>. See issue #712.
    /// Kinds not listed here rank after every listed one.
    /// </summary>
    public static readonly string[] KindPriority =
    {
        "table", "tableextension",
        "page", "pageextension",
        "codeunit",
        "report", "reportextension",
        "query",
        "xmlport",
        "enum", "enumextension",
        "interface",
        "controladdin",
        "profile",
        "permissionset", "permissionsetextension",
    };

    /// <summary>
    /// Rank of <paramref name="kind"/> in <see cref="KindPriority"/>;
    /// unlisted kinds sort last. Lower sorts first.
    /// </summary>
    public static int KindRank(string? kind)
    {
        if (string.IsNullOrEmpty(kind)) return KindPriority.Length;
        var idx = Array.FindIndex(KindPriority, k =>
            k.Equals(kind, StringComparison.OrdinalIgnoreCase));
        return idx < 0 ? KindPriority.Length : idx;
    }
}
