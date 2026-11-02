namespace ALDevToolbox.Services.ObjectExplorer.Explore;

/// <summary>
/// The <c>oe_module_symbols.kind</c> values that name a procedure-like member:
/// something with a body that a user thinks of as "a procedure" and a BC stack
/// trace names as a frame. One list shared by the procedure search, the
/// procedure resolver behind the source-reading MCP tools, and the source /
/// compare viewers, so a kind the importer starts storing can't be searchable
/// in one place and invisible in another (#1569).
/// </summary>
/// <remarks>
/// The importer stores every one of these: <c>procedure</c>,
/// <c>internal_procedure</c>, <c>protected_procedure</c>,
/// <c>event_publisher</c> and <c>event_subscriber</c> come from the symbol
/// package, <c>local_procedure</c> from the source pass (local procedures are
/// absent from the package), and the C/AL importer writes
/// <c>procedure</c> / <c>local_procedure</c> / <c>trigger</c>.
/// </remarks>
public static class ProcedureKinds
{
    /// <summary>
    /// All procedure-like kinds, in a stable order. An array (not a set) so EF
    /// translates <c>All.Contains(s.Kind)</c> to <c>kind = ANY(@p)</c>.
    /// </summary>
    public static readonly string[] All =
    [
        "procedure", "local_procedure", "internal_procedure", "protected_procedure",
        "trigger", "event_publisher", "event_subscriber",
    ];

    private static readonly HashSet<string> Set = new(All, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="kind"/> is one of <see cref="All"/>.</summary>
    public static bool IsProcedureLike(string? kind) => kind is not null && Set.Contains(kind);
}
