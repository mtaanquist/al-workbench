using System.Text.Json.Serialization;

namespace ALWorkbench.Gateway.Contracts;

/// <summary>
/// What the gateway's local policy file (<c>gateway.toml</c>, owned by customer IT)
/// allows, as the gateway enforces it right now. Sent with a heartbeat after the
/// gateway starts, whenever the file changes, and every
/// <see cref="GatewayPolicyReport.ResendInterval"/>, so AL Workbench can show what
/// each service tier allows. AL Workbench only displays it: it cannot change the
/// file, and the gateway refuses whatever the file does not allow, whatever this
/// report says. See <c>.design/on-prem-gateway.md</c>, "The local policy file".
/// </summary>
public sealed record GatewayPolicyReport
{
    /// <summary>How often the report is sent again although nothing changed.</summary>
    public static readonly TimeSpan ResendInterval = TimeSpan.FromMinutes(15);

    /// <summary>The most problems a report lists; the rest are counted in one last line. The gateway's own log has them all.</summary>
    public const int MaxProblems = 50;

    /// <summary>
    /// The largest report the gateway sends, as UTF-8 JSON bytes; well inside what AL
    /// Workbench accepts with a heartbeat, so a large file can never get the heartbeat
    /// refused and the gateway shown offline. A larger one is replaced by a short
    /// report with <see cref="TooLarge"/> set.
    /// </summary>
    public const int MaxJsonBytes = 128 * 1024;

    /// <summary>One of <see cref="GatewayPolicyStates"/>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = GatewayPolicyStates.Missing;

    /// <summary>What is wrong with the file, one sentence each, with line numbers where there are any. Empty when <see cref="State"/> is valid.</summary>
    [JsonPropertyName("problems")]
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>The service tiers the gateway may touch, each with the permissions in force for it.</summary>
    [JsonPropertyName("instances")]
    public IReadOnlyList<GatewayPolicyInstance> Instances { get; init; } = [];

    /// <summary>Tiers that share a database: which one publishes, which restart together.</summary>
    [JsonPropertyName("databases")]
    public IReadOnlyList<GatewayPolicyDatabase> Databases { get; init; } = [];

    /// <summary>How the gateway reaches machines other than its own.</summary>
    [JsonPropertyName("machines")]
    public IReadOnlyList<GatewayPolicyMachine> Machines { get; init; } = [];

    /// <summary>
    /// The full report was over <see cref="MaxJsonBytes"/>, so this one carries only
    /// <see cref="State"/> and a problem saying so. AL Workbench keeps the last full
    /// report it has and marks it out of date; the gateway still enforces the file as
    /// it is.
    /// </summary>
    [JsonPropertyName("tooLarge")]
    public bool TooLarge { get; init; }
}

/// <summary>The values of <see cref="GatewayPolicyReport.State"/>.</summary>
public static class GatewayPolicyStates
{
    /// <summary>The file was read and is in force as written.</summary>
    public const string Valid = "valid";

    /// <summary>
    /// The file has mistakes. The last version that was read without mistakes is
    /// still used, but only to read (inventory); every change is refused until the
    /// file is corrected. Before any good version was read, nothing is allowed.
    /// </summary>
    public const string Invalid = "invalid";

    /// <summary>There is no policy file: nothing is allowed.</summary>
    public const string Missing = "missing";

    /// <summary>
    /// The file, a script it names or the program folder can be changed by someone
    /// other than administrators: every command is refused until that is fixed.
    /// </summary>
    public const string Unsafe = "unsafe";
}

/// <summary>One service tier, with the permissions in force for it after defaults are applied.</summary>
public sealed record GatewayPolicyInstance
{
    [JsonPropertyName("machine")]
    public string Machine { get; init; } = string.Empty;

    /// <summary>The server instance name, e.g. <c>BC140</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary><c>production</c>, <c>test</c> or <c>development</c>, as customer IT set it.</summary>
    [JsonPropertyName("environment")]
    public string Environment { get; init; } = "production";

    /// <summary>The permissions in force, e.g. <c>inventory</c>, <c>publish</c>, <c>restart</c>.</summary>
    [JsonPropertyName("allow")]
    public IReadOnlyList<string> Allow { get; init; } = [];

    /// <summary>The local time window changes may run in, e.g. <c>20:00-06:00</c>; null for any time.</summary>
    [JsonPropertyName("changeHours")]
    public string? ChangeHours { get; init; }

    /// <summary>The tenants the gateway may target on this tier.</summary>
    [JsonPropertyName("tenants")]
    public IReadOnlyList<string> Tenants { get; init; } = [];

    /// <summary>The app publishers it may publish; null when any publisher is allowed.</summary>
    [JsonPropertyName("publishers")]
    public IReadOnlyList<string>? Publishers { get; init; }

    /// <summary>Whether unsigned apps may be published.</summary>
    [JsonPropertyName("allowUnsigned")]
    public bool AllowUnsigned { get; init; }

    /// <summary><c>tenant</c> or <c>global</c>: how apps are published.</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; init; } = "tenant";

    /// <summary><c>5.1</c> or <c>7</c> when customer IT chose; null when the gateway picks.</summary>
    [JsonPropertyName("powerShell")]
    public string? PowerShell { get; init; }
}

/// <summary>A machine and a server instance on it.</summary>
public sealed record GatewayPolicyTier
{
    [JsonPropertyName("machine")]
    public string Machine { get; init; } = string.Empty;

    [JsonPropertyName("instance")]
    public string Instance { get; init; } = string.Empty;
}

/// <summary>Tiers that share one database.</summary>
public sealed record GatewayPolicyDatabase
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The tier that publishes and imports licences for the database.</summary>
    [JsonPropertyName("publishOn")]
    public GatewayPolicyTier PublishOn { get; init; } = new();

    /// <summary>The tiers restarted after an install or a licence import.</summary>
    [JsonPropertyName("restart")]
    public IReadOnlyList<GatewayPolicyTier> Restart { get; init; } = [];

    /// <summary><c>stop-all-then-start-all</c> or <c>one-by-one</c>.</summary>
    [JsonPropertyName("restartMode")]
    public string RestartMode { get; init; } = "stop-all-then-start-all";

    /// <summary>Whether restarts that reach tiers linked to more than one Solution are allowed.</summary>
    [JsonPropertyName("sharedRestart")]
    public bool SharedRestart { get; init; }

    /// <summary>Where a licence is imported (<c>NavDatabase</c>, <c>Master</c>, <c>Default</c>, <c>Tenant</c>); null when licence import is not set up.</summary>
    [JsonPropertyName("licenceTarget")]
    public string? LicenceTarget { get; init; }

    /// <summary>Customer IT's script run before a deployment, if any.</summary>
    [JsonPropertyName("before")]
    public string? Before { get; init; }

    /// <summary>Customer IT's script run after the restart, if any.</summary>
    [JsonPropertyName("after")]
    public string? After { get; init; }
}

/// <summary>How the gateway reaches another machine.</summary>
public sealed record GatewayPolicyMachine
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary><c>service-manager</c> or <c>powershell-remoting</c>.</summary>
    [JsonPropertyName("method")]
    public string Method { get; init; } = "service-manager";
}
