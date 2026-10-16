using System.Text.Json.Nodes;

namespace ALWorkbench.Gateway.Core.PublishInstall;

/// <summary>What Windows said about a file's signature (<c>Get-AuthenticodeSignature</c>).</summary>
/// <param name="Status">The status word, <c>Valid</c> when it verifies.</param>
/// <param name="StatusMessage">Windows' sentence about it.</param>
/// <param name="Subject">The signing certificate's subject; null when there is none.</param>
/// <param name="Thumbprint">The signing certificate's thumbprint; null when there is none.</param>
public sealed record AppSignature(string Status, string? StatusMessage, string? Subject, string? Thumbprint)
{
    public bool Valid => string.Equals(Status, "Valid", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One file as <see cref="PublishInstallScript.Examine"/> read it: the app Business
/// Central reads from it, or why it could not, and its signature, or why it could not be
/// read (both null when it was not asked for).
/// </summary>
public sealed record AppExamination(string Path, AppFileInfo? App, string? AppError, AppSignature? Signature, string? SignatureError)
{
    /// <summary>
    /// Reads the script's output, one entry per path asked about. Throws
    /// <see cref="FormatException"/> when it is not the shape the script writes, or a
    /// path is missing from it.
    /// </summary>
    public static IReadOnlyDictionary<string, AppExamination> FromScriptOutput(JsonNode? output, IEnumerable<string> paths)
    {
        var found = new Dictionary<string, AppExamination>(StringComparer.Ordinal);
        foreach (var item in AppInspection.Items(output).OfType<JsonObject>())
        {
            var path = AppInspection.Text(item["path"]);
            if (path is null) continue;
            AppFileInfo? app = null;
            if (item["app"] is JsonObject read
                && Guid.TryParse(AppInspection.Text(read["id"]), out var id)
                && AppVersions.TryParse(AppInspection.Text(read["version"]), out var version))
            {
                app = new AppFileInfo(id,
                    AppInspection.Text(read["name"]) ?? string.Empty,
                    AppInspection.Text(read["publisher"]) ?? string.Empty,
                    version,
                    HasSignature: false,
                    PackageId: Guid.TryParse(AppInspection.Text(read["packageId"]), out var package) ? package : Guid.Empty);
            }
            var appError = AppInspection.Text(item["appError"])
                           ?? (app is null ? "Get-NAVAppInfo did not return the app's id and version." : null);
            AppSignature? signature = null;
            if (item["signature"] is JsonObject signed && AppInspection.Text(signed["status"]) is { } status)
            {
                signature = new AppSignature(status, AppInspection.Text(signed["statusMessage"]),
                    AppInspection.Text(signed["subject"]), AppInspection.Text(signed["thumbprint"]));
            }
            found[path] = new AppExamination(path, app, appError, signature, AppInspection.Text(item["signatureError"]));
        }
        foreach (var path in paths)
        {
            if (!found.ContainsKey(path)) throw new FormatException("PowerShell did not report on every file.");
        }
        return found;
    }
}
