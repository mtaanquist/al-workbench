namespace ALDevToolbox.Services.Mcp;

/// <summary>
/// Configuration knobs for the MCP server. Bound from the <c>Mcp</c>
/// section of <c>appsettings.json</c>. SiteAdmins can flip
/// <see cref="Enabled"/> off without redeploying when an incident makes it
/// useful to shut programmatic access down.
/// </summary>
public sealed class McpOptions
{
    /// <summary>Whether <c>/mcp</c> is mounted at all. <c>true</c> by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Cap on the ZIP a single generate call may produce. Workspaces are
    /// tens-to-hundreds of KB today; the cap refuses pathological inputs, since
    /// the archive is held in memory until its download link expires
    /// (<see cref="GeneratedDownloadStore"/>). 5 MB by default.
    /// </summary>
    public int MaxWorkspaceBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Default lifetime stamped on a new token when the caller doesn't pick one.</summary>
    public int DefaultTokenLifetimeDays { get; set; } = 90;
}
