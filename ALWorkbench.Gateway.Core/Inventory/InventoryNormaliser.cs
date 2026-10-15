using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Policy;

namespace ALWorkbench.Gateway.Core.Inventory;

/// <summary>
/// Turns what <see cref="InventoryScript"/> printed for one tier into the
/// <see cref="GatewayInstanceInventory"/> sent to AL Workbench, within
/// <see cref="GatewayInventoryLimits"/>. The script has already dealt with every
/// difference between Business Central versions; this reads its one shape, and is
/// lenient only about what Windows PowerShell 5.1's <c>ConvertTo-Json</c> is known to
/// do to it (an array wrapped as <c>{"value": [...], "Count": n}</c>, a number where a
/// string was meant). The tier's name, computer and kind come from the policy file,
/// never from the output.
///
/// <para>Session user names are kept only when the policy file allows
/// <c>session_users</c> for the tier, whatever the script returned: the script is not
/// asked for them otherwise, and this is the second lock on the same door.</para>
/// </summary>
public static class InventoryNormaliser
{
    /// <summary>A tier nothing could be read from, with the reason.</summary>
    public static GatewayInstanceInventory Failed(EffectiveInstance tier, string error, string? powerShell = null)
    {
        ArgumentNullException.ThrowIfNull(tier);
        return new GatewayInstanceInventory
        {
            Machine = tier.Tier.Machine,
            Name = tier.Tier.Instance,
            Environment = PolicyWords.Environments[tier.Environment],
            PowerShell = powerShell,
            Error = Cut(error) ?? "The service tier could not be read.",
        };
    }

    /// <summary>Reads the script's output. A shape it cannot read becomes <see cref="Failed"/>, never an exception.</summary>
    public static GatewayInstanceInventory FromScriptOutput(JsonNode? output, EffectiveInstance tier)
    {
        ArgumentNullException.ThrowIfNull(tier);
        if (Unwrap(output) is JsonArray { Count: > 0 } array) output = array[0];
        if (output is not JsonObject root)
        {
            return Failed(tier, "The gateway's inventory script returned something it could not read.");
        }
        var schema = Int(root["schema"]);
        if (schema != InventoryScript.OutputSchema)
        {
            return Failed(tier, $"The gateway's inventory script returned schema {schema?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}, not {InventoryScript.OutputSchema}.");
        }

        var unavailable = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root["unavailable"] is JsonObject reasons)
        {
            foreach (var (key, value) in reasons)
            {
                if (unavailable.Count >= GatewayInventoryLimits.MaxUnavailable) break;
                if (Cut(key) is { } section && Text(value) is { } reason) unavailable[section] = reason;
            }
        }

        var (tenants, tenantsCut) = Tenants(root["tenants"]);
        var sessions = root["sessions"] as JsonObject;
        IReadOnlyList<string>? users = null;
        if (tier.Allows(PolicyPermission.SessionUsers) && Unwrap(sessions?["users"]) is JsonArray names)
        {
            users = names.Select(Text).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(GatewayInventoryLimits.MaxSessionUsers).ToList();
        }

        return new GatewayInstanceInventory
        {
            Machine = tier.Tier.Machine,
            Name = tier.Tier.Instance,
            Environment = PolicyWords.Environments[tier.Environment],
            PowerShell = PowerShellWord(Text(root["powerShell"])),
            State = Text(root["state"]),
            Version = Text(root["version"]),
            Service = root["service"] is JsonObject service
                ? new GatewayServiceInventory
                {
                    State = Text(service["state"]),
                    StartMode = Text(service["startMode"]),
                    Account = Text(service["account"]),
                }
                : null,
            Configuration = root["configuration"] is JsonObject configuration ? Configuration(configuration) : null,
            Tenants = tenants,
            TenantsTruncated = tenantsCut,
            SessionCount = Int(sessions?["total"]),
            SessionUsers = users,
            Licence = Licence(root["licence"] as JsonObject),
            Unavailable = unavailable,
        };
    }

    /// <summary>Major version 6 and up is PowerShell 7 (6 was "PowerShell Core"); below is Windows PowerShell.</summary>
    internal static string? PowerShellWord(string? version)
    {
        if (version is null) return null;
        var major = version.Split('.')[0];
        return int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? PolicyWords.PowerShellEditions[number >= 6 ? PowerShellEdition.PowerShell7 : PowerShellEdition.WindowsPowerShell]
            : null;
    }

    private static GatewayInstanceConfiguration Configuration(JsonObject values)
    {
        // The keys are Business Central's own names, as the script kept them.
        string? Value(string key) => Text(values.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value);
        return new GatewayInstanceConfiguration
        {
            DatabaseServer = Value("DatabaseServer"),
            DatabaseInstance = Value("DatabaseInstance"),
            DatabaseName = Value("DatabaseName"),
            Multitenant = Bool(Value("Multitenant")),
            ClientServicesPort = Port(Value("ClientServicesPort")),
            SoapServicesPort = Port(Value("SOAPServicesPort")),
            ODataServicesPort = Port(Value("ODataServicesPort")),
            ManagementServicesPort = Port(Value("ManagementServicesPort")),
            DeveloperServicesPort = Port(Value("DeveloperServicesPort")),
            PublicWebBaseUrl = Value("PublicWebBaseUrl"),
        };
    }

    private static (IReadOnlyList<GatewayTenantInventory>? Tenants, bool Cut) Tenants(JsonNode? node)
    {
        if (node is null) return (null, false);
        if (Unwrap(node) is not JsonArray array) return (null, false);
        var tenants = new List<GatewayTenantInventory>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cut = false;
        foreach (var item in array)
        {
            if (item is not JsonObject tenant || Text(tenant["id"]) is not { } id || !seen.Add(id)) continue;
            if (tenants.Count >= GatewayInventoryLimits.MaxTenantsPerInstance)
            {
                cut = true;
                break;
            }
            var (apps, appsCut) = Apps(tenant["apps"]);
            tenants.Add(new GatewayTenantInventory
            {
                Id = id,
                State = Text(tenant["state"]),
                DatabaseName = Text(tenant["databaseName"]),
                SessionCount = Int(tenant["sessionCount"]),
                Apps = apps,
                AppsTruncated = appsCut,
            });
        }
        return (tenants, cut);
    }

    private static (IReadOnlyList<GatewayAppInventory>? Apps, bool Cut) Apps(JsonNode? node)
    {
        if (node is null) return (null, false);
        if (Unwrap(node) is not JsonArray array) return (null, false);
        var apps = new List<GatewayAppInventory>();
        var cut = false;
        foreach (var item in array)
        {
            if (item is not JsonObject app || Text(app["appId"]) is not { } appId || Text(app["version"]) is not { } version) continue;
            if (apps.Count >= GatewayInventoryLimits.MaxAppsPerTenant)
            {
                cut = true;
                break;
            }
            apps.Add(new GatewayAppInventory
            {
                AppId = appId,
                Name = Text(app["name"]) ?? string.Empty,
                Publisher = Text(app["publisher"]) ?? string.Empty,
                Version = version,
                DataVersion = Text(app["dataVersion"]),
                Scope = Text(app["scope"]),
                SyncState = Text(app["syncState"]),
                IsInstalled = Bool(Text(app["isInstalled"])),
                IsPublished = Bool(Text(app["isPublished"])),
            });
        }
        return (apps, cut);
    }

    private static GatewayLicenceInventory? Licence(JsonObject? licence)
    {
        if (licence?["text"] is not JsonValue value || value.GetValueKind() != JsonValueKind.String) return null;
        var text = value.GetValue<string>();
        var cut = Bool(Text(licence["truncated"])) == true;
        if (text.Length > GatewayInventoryLimits.MaxLicenceTextChars)
        {
            text = text[..GatewayInventoryLimits.MaxLicenceTextChars];
            cut = true;
        }
        return string.IsNullOrWhiteSpace(text) ? null : new GatewayLicenceInventory { Text = text, Truncated = cut };
    }

    /// <summary>
    /// Windows PowerShell 5.1 serialises an array that went through the pipeline as
    /// <c>{"value": [...], "Count": n}</c>; this takes the array back out.
    /// </summary>
    private static JsonNode? Unwrap(JsonNode? node) =>
        node is JsonObject wrapped && wrapped.Count == 2 && wrapped["value"] is JsonArray inner && wrapped.ContainsKey("Count")
            ? inner
            : node;

    /// <summary>A non-empty string value, cut to <see cref="GatewayInventoryLimits.MaxTextChars"/>; numbers and booleans as text.</summary>
    private static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        var text = value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
        return Cut(text);
    }

    private static string? Cut(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length > GatewayInventoryLimits.MaxTextChars ? text[..GatewayInventoryLimits.MaxTextChars] : text;
    }

    private static int? Int(JsonNode? node) =>
        Text(node) is { } text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0
            ? number
            : null;

    private static int? Port(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;

    private static bool? Bool(string? text) => text?.ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };
}
