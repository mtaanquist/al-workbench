using System.Text.Json.Serialization;

namespace ALWorkbench.Gateway.Contracts;

/// <summary>
/// Body of <c>POST /gateway/inventory</c>: what the gateway read from its service
/// tiers. Sent every <see cref="GatewayInventoryLimits.Interval"/>, after any
/// command, and when AL Workbench asks with a <c>refresh-inventory</c> command. One
/// post carries one tier (or a part of one, below), so a slow tier never holds back
/// the others and no post comes near the request size limit. AL Workbench keeps the
/// result as the on-premises mirror. See <c>.design/on-prem-gateway.md</c>,
/// "Inventory, as built".
/// </summary>
public sealed record GatewayInventoryReport
{
    /// <summary>The shape of this message. AL Workbench refuses a version it does not know rather than guessing.</summary>
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// Every tier this round of inventory reads: the tiers the local policy file lets
    /// the gateway read. AL Workbench marks a tier it knows for this gateway but that
    /// is not here as missing. Null on a report that must not mark anything missing,
    /// such as a refresh of one named tier.
    /// </summary>
    [JsonPropertyName("roster")]
    public IReadOnlyList<GatewayPolicyTier>? Roster { get; init; }

    /// <summary>The tiers read. Usually one; none on a report that only carries the roster.</summary>
    [JsonPropertyName("instances")]
    public IReadOnlyList<GatewayInstanceInventory> Instances { get; init; } = [];
}

/// <summary>
/// One service tier as the gateway read it. Each part (configuration, tenants, apps,
/// sessions, licence) is read on its own with its own time limit, and one that could
/// not be read is null with the reason in <see cref="Unavailable"/>: a cmdlet or a
/// parameter an older Business Central lacks, a tier that is stopped, a time-out. A
/// missing part never fails the whole tier.
/// </summary>
public sealed record GatewayInstanceInventory
{
    /// <summary>The computer, as the policy file names it.</summary>
    [JsonPropertyName("machine")]
    public string Machine { get; init; } = string.Empty;

    /// <summary>The server instance, e.g. <c>BC140</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The policy file's word for it: <c>production</c>, <c>test</c> or <c>development</c>.</summary>
    [JsonPropertyName("environment")]
    public string Environment { get; init; } = "production";

    /// <summary>The PowerShell the gateway read it with: <c>5.1</c> or <c>7</c>. Null when nothing ran.</summary>
    [JsonPropertyName("powerShell")]
    public string? PowerShell { get; init; }

    /// <summary>
    /// Why nothing could be read at all (no such service on the computer, the
    /// administration module did not load, the read timed out). Null when the tier
    /// was read, even if some parts were not.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary><c>Get-NAVServerInstance</c>'s state, e.g. <c>Running</c> or <c>Stopped</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary>The server's version, e.g. <c>14.0.47786.0</c>.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("service")]
    public GatewayServiceInventory? Service { get; init; }

    [JsonPropertyName("configuration")]
    public GatewayInstanceConfiguration? Configuration { get; init; }

    /// <summary>Null when the tenants could not be read; a single-tenant server has one, <c>default</c>.</summary>
    [JsonPropertyName("tenants")]
    public IReadOnlyList<GatewayTenantInventory>? Tenants { get; init; }

    /// <summary>The server had more than <see cref="GatewayInventoryLimits.MaxTenantsPerInstance"/> tenants; only the first are listed.</summary>
    [JsonPropertyName("tenantsTruncated")]
    public bool TenantsTruncated { get; init; }

    /// <summary>Open sessions on the tier, all tenants together. Null when they could not be counted.</summary>
    [JsonPropertyName("sessionCount")]
    public int? SessionCount { get; init; }

    /// <summary>
    /// Who has a session open. Only when the policy file allows <c>session_users</c>
    /// for this tier (it is personal data); otherwise null and never read.
    /// </summary>
    [JsonPropertyName("sessionUsers")]
    public IReadOnlyList<string>? SessionUsers { get; init; }

    [JsonPropertyName("licence")]
    public GatewayLicenceInventory? Licence { get; init; }

    /// <summary>
    /// Parts that could not be read, by name (<see cref="GatewayInventorySections"/>,
    /// <c>apps:&lt;tenant&gt;</c> for one tenant's apps), with the reason.
    /// </summary>
    [JsonPropertyName("unavailable")]
    public IReadOnlyDictionary<string, string> Unavailable { get; init; } = new Dictionary<string, string>();
}

/// <summary>The tier's Windows service.</summary>
public sealed record GatewayServiceInventory
{
    /// <summary>As Windows reports it, e.g. <c>Running</c>, <c>Stopped</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary>e.g. <c>Auto</c>, <c>Manual</c>, <c>Disabled</c>.</summary>
    [JsonPropertyName("startMode")]
    public string? StartMode { get; init; }

    /// <summary>The account the service runs as.</summary>
    [JsonPropertyName("account")]
    public string? Account { get; init; }
}

/// <summary>
/// A fixed, short list of the tier's configuration keys: where its database is and
/// which ports it listens on. Nothing else is read, so no secret leaves the server.
/// </summary>
public sealed record GatewayInstanceConfiguration
{
    [JsonPropertyName("databaseServer")]
    public string? DatabaseServer { get; init; }

    [JsonPropertyName("databaseInstance")]
    public string? DatabaseInstance { get; init; }

    [JsonPropertyName("databaseName")]
    public string? DatabaseName { get; init; }

    [JsonPropertyName("multitenant")]
    public bool? Multitenant { get; init; }

    [JsonPropertyName("clientServicesPort")]
    public int? ClientServicesPort { get; init; }

    [JsonPropertyName("soapServicesPort")]
    public int? SoapServicesPort { get; init; }

    [JsonPropertyName("odataServicesPort")]
    public int? ODataServicesPort { get; init; }

    [JsonPropertyName("managementServicesPort")]
    public int? ManagementServicesPort { get; init; }

    [JsonPropertyName("developerServicesPort")]
    public int? DeveloperServicesPort { get; init; }

    [JsonPropertyName("publicWebBaseUrl")]
    public string? PublicWebBaseUrl { get; init; }
}

/// <summary>One tenant of the tier.</summary>
public sealed record GatewayTenantInventory
{
    /// <summary>The tenant id; <c>default</c> on a single-tenant server.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = GatewayCommandParameters.DefaultTenant;

    /// <summary><c>Get-NAVTenant</c>'s state, e.g. <c>Operational</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("databaseName")]
    public string? DatabaseName { get; init; }

    [JsonPropertyName("sessionCount")]
    public int? SessionCount { get; init; }

    /// <summary>
    /// The tenant's apps. Null when they are not in this post: they could not be read
    /// (the reason is in the tier's <c>unavailable</c>), or they come in another post of
    /// the same tier. AL Workbench leaves the stored apps alone then.
    /// </summary>
    [JsonPropertyName("apps")]
    public IReadOnlyList<GatewayAppInventory>? Apps { get; init; }

    /// <summary>More than <see cref="GatewayInventoryLimits.MaxAppsPerTenant"/> apps; only the first are listed.</summary>
    [JsonPropertyName("appsTruncated")]
    public bool AppsTruncated { get; init; }
}

/// <summary>One published app as one tenant sees it (<c>Get-NAVAppInfo -Tenant</c>).</summary>
public sealed record GatewayAppInventory
{
    [JsonPropertyName("appId")]
    public string AppId { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("publisher")]
    public string Publisher { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    /// <summary>The version of the app's data in the tenant. Null where <c>-TenantSpecificProperties</c> does not exist.</summary>
    [JsonPropertyName("dataVersion")]
    public string? DataVersion { get; init; }

    /// <summary><c>Global</c>, <c>Tenant</c> or <c>Dev</c>; null where the server does not say.</summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    /// <summary>e.g. <c>Synced</c>, <c>NotSynced</c>; null where the server does not say.</summary>
    [JsonPropertyName("syncState")]
    public string? SyncState { get; init; }

    [JsonPropertyName("isInstalled")]
    public bool? IsInstalled { get; init; }

    [JsonPropertyName("isPublished")]
    public bool? IsPublished { get; init; }
}

/// <summary>
/// The tier's licence as <c>Export-NAVServerLicenseInformation</c> prints it. Sent as
/// text and read by AL Workbench (licensee, expiry, object ranges), so a correction to
/// the reading needs no gateway update; see the design's "Inventory, as built".
/// </summary>
public sealed record GatewayLicenceInventory
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    /// <summary>The text was longer than <see cref="GatewayInventoryLimits.MaxLicenceTextChars"/> and was cut.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }
}

/// <summary>The names of the parts of a tier's inventory, as <see cref="GatewayInstanceInventory.Unavailable"/> keys them.</summary>
public static class GatewayInventorySections
{
    public const string Instance = "instance";
    public const string Service = "service";
    public const string Configuration = "configuration";
    public const string Tenants = "tenants";
    public const string Sessions = "sessions";
    public const string Licence = "licence";

    /// <summary>All apps (the cmdlet itself failed).</summary>
    public const string Apps = "apps";

    /// <summary>One tenant's apps.</summary>
    public static string AppsOf(string tenant) => "apps:" + tenant;

    /// <summary>Data version and sync state, where <c>-TenantSpecificProperties</c> does not exist.</summary>
    public const string AppTenantProperties = "apps.tenantSpecificProperties";
}

/// <summary>
/// Bounds both sides keep. The gateway cuts what is over a limit and says so with a
/// flag (<c>tenantsTruncated</c>, <c>appsTruncated</c>, <c>truncated</c>) rather than
/// sending nothing, because a partial list is still worth seeing and a flag tells AL
/// Workbench not to mark the rest missing. AL Workbench refuses a report over the
/// limits outright: only a gateway that does not cut can send one.
/// </summary>
public static class GatewayInventoryLimits
{
    /// <summary>How often the gateway reads its tiers when nothing asks it to.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>The policy file lists at most 200 tiers.</summary>
    public const int MaxRosterTiers = 200;

    public const int MaxInstancesPerReport = 200;

    public const int MaxTenantsPerInstance = 1000;

    public const int MaxAppsPerTenant = 1000;

    public const int MaxSessionUsers = 500;

    public const int MaxUnavailable = 100;

    /// <summary>A licence's printed information. Real ones are tens of kilobytes.</summary>
    public const int MaxLicenceTextChars = 512 * 1024;

    /// <summary>Any other text field; longer values are cut by the gateway.</summary>
    public const int MaxTextChars = 1000;

    /// <summary>
    /// The size the gateway keeps each post under, as UTF-8 JSON bytes, by spreading a
    /// tier's apps over several posts; well inside <see cref="GatewayProtocol.MaxRequestBodyBytes"/>.
    /// </summary>
    public const int TargetPostBytes = 2 * 1024 * 1024;
}
