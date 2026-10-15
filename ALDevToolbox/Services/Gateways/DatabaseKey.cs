using System.Net;

namespace ALDevToolbox.Services.Gateways;

/// <summary>
/// One spelling for "which SQL Server database is this", so service tiers reported by
/// different gateways, or configured by different people, are recognised as sharing a
/// database (#1367). A service tier's configuration names its database server however its
/// installer typed it: <c>.</c>, <c>(local)</c>, <c>localhost</c>, a short host name or a
/// full one, with the default instance left out or spelled <c>MSSQLSERVER</c>.
/// <list type="bullet">
/// <item>A server that names the reporting computer itself (<c>.</c>, <c>(local)</c>,
/// <c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>, empty) is that gateway's computer.</item>
/// <item>Host names are compared on their first label, ignoring case, so
/// <c>SQL01</c> and <c>sql01.cronus.local</c> match. An IP address is kept whole.</item>
/// <item>A <c>tcp:</c> prefix and a <c>,port</c> suffix are dropped, and an instance written
/// into the server (<c>SQL01\BC</c>) is used when none is given on its own.</item>
/// <item>No instance, an empty one and <c>MSSQLSERVER</c> are the default instance.</item>
/// </list>
/// </summary>
public static class DatabaseKey
{
    private const string DefaultInstance = "MSSQLSERVER";

    /// <summary>
    /// The key for a database, or null when the database name is not known, in which case
    /// nothing can be said to share it.
    /// </summary>
    /// <param name="reportingMachine">The computer of the gateway that reported the tier, for a server named only as "this computer".</param>
    public static string? For(string? server, string? instance, string? database, string? reportingMachine)
    {
        var name = (database ?? string.Empty).Trim();
        if (name.Length == 0) return null;

        var text = (server ?? string.Empty).Trim();
        if (text.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) text = text[4..].Trim();
        var comma = text.IndexOf(',');
        if (comma >= 0) text = text[..comma].Trim();
        var inst = (instance ?? string.Empty).Trim();
        var slash = text.IndexOf('\\');
        if (slash >= 0)
        {
            if (inst.Length == 0) inst = text[(slash + 1)..].Trim();
            text = text[..slash].Trim();
        }
        if (IsThisComputer(text)) text = (reportingMachine ?? string.Empty).Trim();
        if (inst.Equals(DefaultInstance, StringComparison.OrdinalIgnoreCase)) inst = string.Empty;

        return $"{Host(text)}\\{inst.ToLowerInvariant()}|{name.ToLowerInvariant()}";
    }

    private static bool IsThisComputer(string host) =>
        host.Length == 0 || host is "." or "(local)" or "127.0.0.1" or "::1"
        || host.Equals("(local)", StringComparison.OrdinalIgnoreCase)
        || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    private static string Host(string host)
    {
        if (IPAddress.TryParse(host, out _)) return host.ToLowerInvariant();
        var dot = host.IndexOf('.');
        return (dot > 0 ? host[..dot] : host).ToLowerInvariant();
    }
}
