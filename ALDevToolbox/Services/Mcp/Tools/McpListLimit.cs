namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// The <c>limit</c> parameter the list tools whose results grow without end share
/// (builds, deployments): the newest few by default, so an agent asking for a
/// pipeline's builds doesn't get thousands of them back (#1138).
/// </summary>
internal static class McpListLimit
{
    public const int Default = 20;
    public const int Max = 200;
    public const string Description = "How many to return, newest first. Default 20, at least 1, at most 200. Raise it when the build you need is older than the oldest one returned.";

    public static int Clamp(int limit) => Math.Clamp(limit, 1, Max);
}
