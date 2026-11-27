using System.Text.Json;
using System.Text.Json.Serialization;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// One uploaded <c>.app</c> an import left out because it could not be read, with
/// the reason shown to the admin. Stored as a list on
/// <see cref="Domain.Entities.ObjectExplorer.OeRelease.SkippedAppsJson"/> so the
/// release says it is partial instead of failing outright (#1801).
/// </summary>
public sealed record ReleaseSkippedApp(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("reason")] string Reason)
{
    /// <summary>The reason recorded for an NEA-encrypted <c>.app</c>.</summary>
    public const string EncryptedReason = "encrypted, so its objects can't be read";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The JSON column value for <paramref name="apps"/>, or null when there are none.</summary>
    public static string? Serialize(IReadOnlyCollection<ReleaseSkippedApp> apps) =>
        apps.Count == 0 ? null : JsonSerializer.Serialize(apps, Options);

    /// <summary>Reads the JSON column back; null, empty or malformed reads as no skipped apps.</summary>
    public static IReadOnlyList<ReleaseSkippedApp> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ReleaseSkippedApp>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The skipped list after an amend: the earlier entries, then the new ones,
    /// one entry per file name (the latest reason wins) so re-uploading the same
    /// encrypted app does not list it twice. Entries named in
    /// <paramref name="imported"/> are dropped: the amend brought them in.
    /// </summary>
    public static IReadOnlyList<ReleaseSkippedApp> Merge(
        IReadOnlyList<ReleaseSkippedApp> existing, IReadOnlyList<ReleaseSkippedApp> added,
        IReadOnlySet<string>? imported = null)
    {
        var byName = new Dictionary<string, ReleaseSkippedApp>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var app in existing.Concat(added))
        {
            if (!byName.ContainsKey(app.FileName)) order.Add(app.FileName);
            byName[app.FileName] = app;
        }
        return order.Where(n => imported is null || !imported.Contains(n)).Select(n => byName[n]).ToList();
    }
}
