using System.Text.Json;
using System.Text.Json.Serialization;

namespace ALWorkbench.Gateway.Contracts;

/// <summary>
/// Everything AL Workbench can ask a gateway to do. A closed set: no verb runs a
/// script or a command line the server sends, and a verb either side does not know
/// is refused rather than ignored. On the wire (and in the database) each is the
/// kebab-case word in <see cref="GatewayVerbs"/>. See <c>.design/on-prem-gateway.md</c>,
/// "Commands".
/// </summary>
[JsonConverter(typeof(GatewayVerbJsonConverter))]
public enum GatewayVerb
{
    /// <summary><c>refresh-inventory</c>: report instances, versions, tenants, apps, sessions and the licence.</summary>
    RefreshInventory,

    /// <summary><c>publish-install</c>: publish, sync and install or upgrade apps, then restart the group.</summary>
    PublishInstall,

    /// <summary><c>restart-group</c>: stop and start the tiers of one database.</summary>
    RestartGroup,

    /// <summary><c>restart-instance</c>: restart one tier.</summary>
    RestartInstance,

    /// <summary><c>stop-instance</c>: stop one tier.</summary>
    StopInstance,

    /// <summary><c>start-instance</c>: start one tier.</summary>
    StartInstance,

    /// <summary><c>import-licence</c>: import a licence file, then restart the group.</summary>
    ImportLicence,
}

/// <summary>The wire spelling of each <see cref="GatewayVerb"/>, and the strict parse back.</summary>
public static class GatewayVerbs
{
    private static readonly Dictionary<GatewayVerb, string> ToWireMap = new()
    {
        [GatewayVerb.RefreshInventory] = "refresh-inventory",
        [GatewayVerb.PublishInstall] = "publish-install",
        [GatewayVerb.RestartGroup] = "restart-group",
        [GatewayVerb.RestartInstance] = "restart-instance",
        [GatewayVerb.StopInstance] = "stop-instance",
        [GatewayVerb.StartInstance] = "start-instance",
        [GatewayVerb.ImportLicence] = "import-licence",
    };

    private static readonly Dictionary<string, GatewayVerb> FromWireMap =
        ToWireMap.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    /// <summary>The kebab-case word for <paramref name="verb"/>. Throws for a value outside the enum.</summary>
    public static string ToWire(GatewayVerb verb) =>
        ToWireMap.TryGetValue(verb, out var wire)
            ? wire
            : throw new ArgumentOutOfRangeException(nameof(verb), verb, "Not a gateway verb.");

    /// <summary>
    /// The verb for an exact, case-sensitive wire word. False for anything else -
    /// including a number, the C# name, or a different case - so an unknown verb
    /// is never mistaken for a known one.
    /// </summary>
    public static bool TryParse(string? wire, out GatewayVerb verb) =>
        FromWireMap.TryGetValue(wire ?? string.Empty, out verb);

    /// <summary>The verb for a wire word, or <see cref="FormatException"/>.</summary>
    public static GatewayVerb Parse(string wire) =>
        TryParse(wire, out var verb) ? verb : throw new FormatException($"'{wire}' is not a gateway verb.");

    /// <summary>Every wire word, in enum order.</summary>
    public static IReadOnlyCollection<string> All => ToWireMap.Values;
}

/// <summary>
/// Reads and writes <see cref="GatewayVerb"/> as its wire word. A string that is not
/// one, or a number, fails deserialisation of the whole message: an unknown verb is
/// refused, never defaulted.
/// </summary>
public sealed class GatewayVerbJsonConverter : JsonConverter<GatewayVerb>
{
    public override GatewayVerb Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A gateway verb must be a string.");
        }
        var wire = reader.GetString();
        return GatewayVerbs.TryParse(wire, out var verb)
            ? verb
            : throw new JsonException($"'{wire}' is not a gateway verb this version knows.");
    }

    public override void Write(Utf8JsonWriter writer, GatewayVerb value, JsonSerializerOptions options) =>
        writer.WriteStringValue(GatewayVerbs.ToWire(value));
}
