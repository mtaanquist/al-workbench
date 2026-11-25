using System.Text.Json;
using System.Text.Json.Nodes;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Services.Migration;

/// <summary>One <c>app.json</c> property a migration changes.</summary>
/// <param name="Property">The property's name, as it is spelled in the file.</param>
/// <param name="Before">The source's value as compact JSON, or null when the source did not have it.</param>
/// <param name="After">The value after the migration, or null when it is removed.</param>
public sealed record MigrationAppJsonChange(string Property, string? Before, string? After);

/// <summary>
/// Rebuilds a source extension's <c>app.json</c> on the template's (issue #1700,
/// decided by Mads on #1698): the result starts from the <c>app.json</c> the
/// template generates, then takes back from the source what describes that
/// particular app rather than the house style. See
/// <c>.design/repository-migration.md</c>, "Part 2".
/// </summary>
public static class MigrationAppJson
{
    /// <summary>
    /// The app's identity and wiring. Always the source's, and removed when the
    /// source has none, since the template's values belong to the template's
    /// own first extension.
    /// </summary>
    internal static readonly string[] SourceOwned =
    [
        "id", "name", "publisher", "version", "idRanges", "idRange", "dependencies",
    ];

    /// <summary>
    /// What describes or configures this app rather than the house style: its
    /// text, logo and screenshots, its languages, who may see its internals,
    /// how it compiles, and where it sends telemetry. The source's when it has
    /// them, the template's otherwise.
    /// </summary>
    internal static readonly string[] SourceWhenSet =
    [
        "brief", "description", "logo", "screenshots", "supportedLocales",
        "target", "internalsVisibleTo", "propagateDependencies", "preprocessorSymbols", "resourceFolders",
        "applicationInsightsConnectionString", "applicationInsightsKey", "keyVaultUrls",
    ];

    /// <summary>
    /// The Business Central versions the app targets. Never the template's: they
    /// follow the customer's production environment when it is known, and stay
    /// the source's otherwise (Mads, 2026-11-04). The template's is used only when
    /// the source has none and production is not known.
    /// </summary>
    internal static readonly string[] Versions = ["application", "platform", "runtime"];

    private const string Features = "features";

    private static readonly JsonDocumentOptions Tolerant = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Reads an <c>app.json</c> leniently, the way the AL compiler does. Null when it is not a JSON object.</summary>
    public static JsonObject? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: Tolerant) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The source's <c>app.json</c> rebuilt on <paramref name="template"/>, and
    /// every property that ends up different from the source's. Neither input
    /// is changed. The result follows the template's property order and
    /// spelling; kept settings the template lacks follow it.
    /// </summary>
    /// <param name="productionMajor">
    /// The major version of the customer's production environment, when known. A
    /// version whose major differs from it is set to that wave's first release
    /// (26 gives application and platform 26.0.0.0, runtime 15.0); one on the same
    /// major is left alone, so a source on runtime 15.1 keeps it.
    /// </param>
    /// <param name="fileExists">
    /// Whether a path the source's <c>app.json</c> names (its logo) is in the new
    /// workspace. A logo that did not come along is replaced by the template's.
    /// Null takes every path as present.
    /// </param>
    public static (JsonObject AppJson, IReadOnlyList<MigrationAppJsonChange> Changes) Normalize(
        JsonObject template, JsonObject source, int? productionMajor = null, Func<string, bool>? fileExists = null)
    {
        var result = (JsonObject)template.DeepClone();
        var sourceProps = new Dictionary<string, (string Key, JsonNode? Value)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source) sourceProps[key] = (key, value);

        foreach (var name in SourceOwned)
        {
            Remove(result, name);
            if (sourceProps.TryGetValue(name, out var p)) result[p.Key] = p.Value?.DeepClone();
        }

        // An old manifest names its range with idRange; the template's idRanges must not sit beside it.
        if (sourceProps.ContainsKey("idRange") && !sourceProps.ContainsKey("idRanges")) Remove(result, "idRanges");

        foreach (var name in SourceWhenSet)
        {
            if (sourceProps.TryGetValue(name, out var p) && IsSet(p.Value)
                && !(name == "logo" && fileExists is not null && p.Value is JsonValue logo
                     && logo.TryGetValue<string>(out var logoPath) && !fileExists(logoPath)))
            {
                Remove(result, name);
                result[p.Key] = p.Value!.DeepClone();
            }
        }

        var wave = productionMajor is >= BusinessCentralWaves.MinimumMajor and var major
            ? BusinessCentralWaves.ForMajor(major)
            : null;
        foreach (var name in Versions)
        {
            var hasSource = sourceProps.TryGetValue(name, out var p);
            if (wave is not null)
            {
                var wanted = name == "runtime" ? wave.Runtime : wave.Application;
                var wantedMajor = name == "runtime" ? wave.Major - 11 : wave.Major;
                var key = Find(result, name)?.Key ?? (hasSource ? p.Key : name);
                Remove(result, name);
                result[key] = hasSource && TryVersion(p.Value, out var sourceVersion) && sourceVersion.Major == wantedMajor
                    ? p.Value!.DeepClone()
                    : wanted;
            }
            else if (hasSource)
            {
                Remove(result, name);
                result[p.Key] = p.Value?.DeepClone();
            }
        }

        // A feature switch changes how the code compiles, so one the source turned
        // on stays on beside the template's.
        if (sourceProps.TryGetValue(Features, out var sourceFeatures) && sourceFeatures.Value is JsonArray fromSource)
        {
            var merged = Find(result, Features)?.Value is JsonArray fromTemplate ? (JsonArray)fromTemplate.DeepClone() : new JsonArray();
            foreach (var feature in fromSource)
            {
                if (feature is JsonValue v && v.TryGetValue<string>(out var text)
                    && !merged.Any(m => m is JsonValue mv && mv.TryGetValue<string>(out var mt) && string.Equals(mt, text, StringComparison.OrdinalIgnoreCase)))
                {
                    merged.Add(text);
                }
            }
            var key = Find(result, Features)?.Key ?? sourceFeatures.Key;
            result[key] = merged;
        }

        result = InTemplateOrder(template, result);
        return (result, Diff(source, result));
    }

    /// <summary>
    /// The same properties laid out as the template lays them out (Mads, #1700):
    /// every property the template has sits where the template puts it, spelled
    /// as the template spells it, and the source's kept settings that the
    /// template lacks follow in the source's order. Sources written over the years put the same settings
    /// in different places; after the move they all read alike.
    /// </summary>
    private static JsonObject InTemplateOrder(JsonObject template, JsonObject merged)
    {
        var ordered = new JsonObject();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, _) in template)
        {
            if (Find(merged, key) is not { } found) continue;
            ordered[key] = found.Value?.DeepClone();
            placed.Add(key);
        }
        foreach (var (key, value) in merged)
        {
            if (placed.Add(key)) ordered[key] = value?.DeepClone();
        }
        return ordered;
    }

    private static List<MigrationAppJsonChange> Diff(JsonObject before, JsonObject after)
    {
        var changes = new List<MigrationAppJsonChange>();
        foreach (var (key, value) in after)
        {
            var old = Find(before, key);
            if (old is null)
            {
                changes.Add(new MigrationAppJsonChange(key, null, Compact(value)));
            }
            else if (!JsonNode.DeepEquals(old.Value.Value, value))
            {
                changes.Add(new MigrationAppJsonChange(key, Compact(old.Value.Value), Compact(value)));
            }
        }
        foreach (var (key, value) in before)
        {
            if (Find(after, key) is null) changes.Add(new MigrationAppJsonChange(key, Compact(value), null));
        }
        return changes;
    }

    private static (string Key, JsonNode? Value)? Find(JsonObject obj, string name)
    {
        foreach (var (key, value) in obj)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return (key, value);
        }
        return null;
    }

    private static void Remove(JsonObject obj, string name)
    {
        if (Find(obj, name) is { } found) obj.Remove(found.Key);
    }

    private static bool IsSet(JsonNode? value) => value switch
    {
        null => false,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        _ => true,
    };

    private static bool TryVersion(JsonNode? value, out Version version)
    {
        version = new Version();
        if (value is not JsonValue v || !v.TryGetValue<string>(out var text)) return false;
        text = text.Trim();
        // A runtime is often written as a bare major version ("15").
        if (int.TryParse(text, out var major) && major >= 0) text += ".0";
        return Version.TryParse(text, out version!);
    }

    private static string Compact(JsonNode? node) => node is null ? "null" : node.ToJsonString();
}
