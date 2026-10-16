namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A Business Central service tier as an on-premises gateway last reported it: the
/// on-premises mirror (<c>gateway_instances</c>). Written only from the gateway's
/// inventory reports, by <c>GatewayInventoryService</c>; nobody edits it, so it is not
/// audited. Keyed by gateway + machine + instance name (compared without regard to
/// case, as Windows does) with a stable id, so the on-premises environment that links
/// to it (#1366) survives every refresh. A tier the gateway stops reporting is
/// stamped <see cref="MissingSince"/>, never deleted. See
/// <c>.design/on-prem-gateway.md</c>, "Data model" and "Inventory, as built".
///
/// <para>Two kinds of fact. What is true right now (<see cref="State"/>,
/// <see cref="Version"/>, the Windows service, <see cref="SessionCount"/>) is
/// overwritten by every report, null meaning "could not be read". What changes
/// slowly (configuration, tenants, apps, licence) keeps its last known value when a
/// report could not read it, for instance while the tier is stopped.</para>
/// </summary>
public class GatewayInstance
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }

    public int GatewayId { get; set; }
    public Gateway? Gateway { get; set; }

    /// <summary>The computer, as the gateway's policy file names it.</summary>
    public string Machine { get; set; } = string.Empty;

    /// <summary>The server instance, e.g. <c>BC140</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The policy file's word: <c>production</c>, <c>test</c> or <c>development</c>. Customer IT's call; rewritten by every report.</summary>
    public string Environment { get; set; } = "production";

    /// <summary><c>Get-NAVServerInstance</c>'s state (<c>Running</c>, <c>Stopped</c>).</summary>
    public string? State { get; set; }

    public string? Version { get; set; }

    public string? ServiceState { get; set; }
    public string? ServiceStartMode { get; set; }
    public string? ServiceAccount { get; set; }

    public string? DatabaseServer { get; set; }
    public string? DatabaseInstance { get; set; }
    public string? DatabaseName { get; set; }
    public bool? Multitenant { get; set; }
    public int? ClientServicesPort { get; set; }
    public int? SoapServicesPort { get; set; }
    public int? ODataServicesPort { get; set; }
    public int? ManagementServicesPort { get; set; }
    public int? DeveloperServicesPort { get; set; }
    public string? PublicWebBaseUrl { get; set; }

    /// <summary>Open sessions across the tier's tenants at the last report; null when they could not be counted.</summary>
    public int? SessionCount { get; set; }

    /// <summary>
    /// Who had a session open, as JSON (a list of names). Only ever set when the
    /// customer's policy file allows <c>session_users</c> for this tier; personal
    /// data, so cleared by the next report that does not carry it.
    /// </summary>
    public string? SessionUsersJson { get; set; }

    /// <summary>The PowerShell the gateway read it with: <c>5.1</c> or <c>7</c>.</summary>
    public string? PowerShell { get; set; }

    /// <summary>Why the last report could read nothing at all; null when it read the tier.</summary>
    public string? ReadError { get; set; }

    /// <summary>The parts the last report could not read, with the reason (JSON object). <c>{}</c> when all were read.</summary>
    public string UnavailableJson { get; set; } = "{}";

    /// <summary>The server had more tenants than a report carries; tenants beyond them are never marked missing.</summary>
    public bool TenantsTruncated { get; set; }

    /// <summary>When the gateway first reported this tier.</summary>
    public DateTime FirstReportedAt { get; set; }

    /// <summary>The last report about it, successful or not.</summary>
    public DateTime ReportedAt { get; set; }

    /// <summary>The last report that read it. Null until one has.</summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>Set when a report's roster no longer lists the tier; cleared when it comes back.</summary>
    public DateTime? MissingSince { get; set; }

    public List<GatewayInstanceTenant> Tenants { get; set; } = new();

    public GatewayInstanceLicence? Licence { get; set; }
}

/// <summary>One tenant of a reported service tier (<c>gateway_instance_tenants</c>). A single-tenant server has one, <c>default</c>.</summary>
public class GatewayInstanceTenant
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }

    public int InstanceId { get; set; }
    public GatewayInstance? Instance { get; set; }

    /// <summary>The tenant id, e.g. <c>default</c>.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary><c>Get-NAVTenant</c>'s state, e.g. <c>Operational</c>.</summary>
    public string? State { get; set; }

    public string? DatabaseName { get; set; }

    public int? SessionCount { get; set; }

    /// <summary>The tenant had more apps than a report carries; apps beyond them are never marked missing.</summary>
    public bool AppsTruncated { get; set; }

    /// <summary>When its app list was last read; null until it has been.</summary>
    public DateTime? AppsReadAt { get; set; }

    /// <summary>The last report that listed the tenant.</summary>
    public DateTime ReadAt { get; set; }

    public DateTime? MissingSince { get; set; }

    public List<GatewayInstanceApp> Apps { get; set; } = new();
}

/// <summary>
/// One published app as one tenant sees it (<c>gateway_instance_apps</c>). Keyed by
/// app id and version, since several versions of an app are published at once
/// during an upgrade. Only rewritten when something about it changed.
/// </summary>
public class GatewayInstanceApp
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }

    public int TenantRowId { get; set; }
    public GatewayInstanceTenant? Tenant { get; set; }

    /// <summary>The app's id as Business Central prints it (a GUID).</summary>
    public string AppId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;

    /// <summary>The version of its data in the tenant; null where the server does not say (BC 14 without <c>-TenantSpecificProperties</c>).</summary>
    public string? DataVersion { get; set; }

    public string? Scope { get; set; }
    public string? SyncState { get; set; }
    public bool? IsInstalled { get; set; }
    public bool? IsPublished { get; set; }

    public DateTime? MissingSince { get; set; }
}

/// <summary>
/// The licence a service tier reported (<c>gateway_instance_licences</c>), one per
/// tier, as AL Workbench read it from <c>Export-NAVServerLicenseInformation</c>'s text.
/// The text is kept (bounded) so a better reading can be applied to it later without
/// asking the gateway again, and so the range check (#1370) has the source.
/// </summary>
public class GatewayInstanceLicence
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }

    public int InstanceId { get; set; }
    public GatewayInstance? Instance { get; set; }

    public string? Licensee { get; set; }

    public string? SerialNumber { get; set; }

    /// <summary>The expiry date as the licence states it; null when it states none or it could not be read.</summary>
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>The licence information as printed, at most <c>GatewayInventoryLimits.MaxLicenceTextChars</c>.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>SHA-256 of <see cref="Text"/>, so an unchanged licence is not read again.</summary>
    public string TextSha256 { get; set; } = string.Empty;

    /// <summary>The gateway cut the text to size.</summary>
    public bool Truncated { get; set; }

    /// <summary>What could not be read from the text, one sentence a line; null when nothing.</summary>
    public string? ParseProblems { get; set; }

    /// <summary>The last report that carried a licence.</summary>
    public DateTime ReadAt { get; set; }

    public List<GatewayInstanceLicenceRange> Ranges { get; set; } = new();
}

/// <summary>One licensed object range, with its object type (<c>gateway_instance_licence_ranges</c>), for the range check (#1370).</summary>
public class GatewayInstanceLicenceRange
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }

    public int LicenceId { get; set; }
    public GatewayInstanceLicence? Licence { get; set; }

    /// <summary>As the licence names it, in a fixed spelling: <c>Table</c>, <c>TableData</c>, <c>Page</c>, <c>Report</c>, <c>Codeunit</c>, <c>XMLport</c>, <c>Query</c> ...</summary>
    public string ObjectType { get; set; } = string.Empty;

    public int FromId { get; set; }

    public int ToId { get; set; }

    /// <summary>The rest of the line (the permissions columns), as printed; null when there was none.</summary>
    public string? Permissions { get; set; }
}
