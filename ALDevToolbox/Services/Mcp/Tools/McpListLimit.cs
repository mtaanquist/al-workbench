namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// The <c>limit</c> parameter the list tools whose results grow without end share
/// (builds, deployments): the newest few by default, so an agent asking for a
/// pipeline's builds doesn't get thousands of them back (#1138). The ceiling is
/// 50 rather than 200: 200 build rows with failure messages ran well past what an
/// assistant can read in one response (#1511).
/// </summary>
internal static class McpListLimit
{
    public const int Default = 20;
    public const int Max = 50;
    public const string Description = "How many to return, newest first. Default 20, at least 1, at most 50.";

    /// <summary>How long a failure message may run in a list row; get_solution_build has the whole message.</summary>
    public const int MaxListFailureMessage = 300;

    public static int Clamp(int limit) => Math.Clamp(limit, 1, Max);

    /// <summary>Shortens <paramref name="message"/> for a list row, saying where the rest is.</summary>
    public static string? ShortenFailure(string? message, string whereTheRestIs) =>
        message is { Length: > MaxListFailureMessage }
            ? message[..MaxListFailureMessage] + $"... (cut; {whereTheRestIs} has the whole message)"
            : message;
}

/// <summary>
/// The <c>limit</c> parameter of the catalogue lists (solutions, pipelines,
/// deployment pipelines, repositories): one row per thing the organisation has,
/// so the list grows with the business. A modest default with a note when it
/// cuts, and a search to narrow it (#1511).
/// </summary>
internal static class McpCatalogueLimit
{
    public const int Max = 100;
    public const string Description = "How many to return. At least 1, at most 100. Prefer search over raising it.";

    public static int Clamp(int limit) => Math.Clamp(limit, 1, Max);
}
