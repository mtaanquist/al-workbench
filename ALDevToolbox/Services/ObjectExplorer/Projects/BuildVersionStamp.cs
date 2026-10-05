using ALDevToolbox.Services.GitHub;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// The version a pipeline build gives an app: the <c>app.json</c> version with the
/// build's number added to its third part (Build). <c>28.2.0.0</c> built as build
/// #4812 compiles as <c>28.2.4812.0</c>; somebody's own bump to <c>28.2.5.0</c>
/// compiles as <c>28.2.4817.0</c>.
///
/// <para><strong>Why added, not replaced.</strong> Adding keeps every hand-made
/// version below the first computed one, so turning numbering on for a solution whose
/// apps are already installed can never produce an apparent downgrade, and a solution
/// moving from another build system carries on from its last number by putting it in
/// <c>app.json</c> once. Build numbers only ever grow, so successive builds of the
/// same Major.Minor do too, across every pipeline of every solution.</para>
///
/// <para>See <c>.design/object-explorer-project-builds.md</c>, "Build numbers in app versions".</para>
/// </summary>
public static class BuildVersionStamp
{
    /// <summary>
    /// <paramref name="version"/> with <paramref name="buildNumber"/> added to its third
    /// part, always as four parts (missing parts read as 0). Null when the version is
    /// not one to four non-negative whole numbers, or the sum would not fit in a part.
    /// </summary>
    public static string? Compute(string? version, int buildNumber)
    {
        if (string.IsNullOrWhiteSpace(version) || buildNumber < 0) return null;
        var segments = version.Trim().Split('.');
        if (segments.Length is < 1 or > 4) return null;

        var parts = new long[4];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }
            parts[i] = value;
        }

        parts[2] += buildNumber;
        if (parts[2] > int.MaxValue) return null;
        return string.Join('.', parts);
    }

    /// <summary>
    /// <paramref name="appJson"/> with its <c>version</c> set to <paramref name="version"/>,
    /// every other byte left as it was where the text allows. Null when the file has no
    /// <c>version</c> it can set.
    /// </summary>
    public static string? WriteVersion(string appJson, string version) =>
        AppJsonValueEditor.ReplaceRootProperty(appJson, "version", version)
        ?? AppJsonValueEditor.RewriteWholeDocument(appJson, root => root["version"] = version);
}
