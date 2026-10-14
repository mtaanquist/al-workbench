namespace ALWorkbench.Gateway.Core.Policy;

/// <summary>What kind of environment customer IT says a service tier is. Production is the default.</summary>
public enum PolicyEnvironment
{
    Production,
    Test,
    Development,
}

/// <summary>
/// The permissions a service tier can be given in <c>gateway.toml</c>. See
/// <c>.design/on-prem-gateway.md</c>, "Permissions per service tier".
/// </summary>
public enum PolicyPermission
{
    /// <summary>Read state, versions, tenants, apps, session counts and licence information.</summary>
    Inventory,
    /// <summary>Also report who is signed in, not only how many.</summary>
    SessionUsers,
    /// <summary>Publish, sync, install and upgrade apps.</summary>
    Publish,
    /// <summary>Remove an app's previous version after an upgrade.</summary>
    Unpublish,
    /// <summary>Restart the tier as part of a deployment or licence import.</summary>
    Restart,
    /// <summary>Stop, start and restart the tier on demand.</summary>
    ServiceControl,
    /// <summary>Import a licence file.</summary>
    Licence,
    /// <summary>Run the before and after scripts named for its database.</summary>
    Scripts,
}

/// <summary>How apps are published on a tier: per tenant (the default) or for the whole server.</summary>
public enum PublishScope
{
    Tenant,
    Global,
}

/// <summary>How the tiers of one database are restarted.</summary>
public enum RestartMode
{
    StopAllThenStartAll,
    OneByOne,
}

/// <summary>The <c>-Database</c> value <c>Import-NAVServerLicense</c> is given.</summary>
public enum LicenceTarget
{
    NavDatabase,
    Master,
    Default,
    Tenant,
}

/// <summary>How the gateway reaches a machine other than its own.</summary>
public enum MachineMethod
{
    ServiceManager,
    PowerShellRemoting,
}

/// <summary>Which PowerShell runs a tier's cmdlets.</summary>
public enum PowerShellEdition
{
    /// <summary>Windows PowerShell 5.1 (<c>powershell.exe</c>), which every Business Central version's module loads in.</summary>
    WindowsPowerShell,
    /// <summary>PowerShell 7 (<c>pwsh.exe</c>).</summary>
    PowerShell7,
}

/// <summary>
/// The exact words <c>gateway.toml</c> uses for each value, and the strict parse back.
/// Case-sensitive on purpose: the file has one spelling for each, so a value that
/// almost matches is a mistake to point out, not a guess to make.
/// </summary>
public static class PolicyWords
{
    public static readonly IReadOnlyDictionary<PolicyEnvironment, string> Environments = new Dictionary<PolicyEnvironment, string>
    {
        [PolicyEnvironment.Production] = "production",
        [PolicyEnvironment.Test] = "test",
        [PolicyEnvironment.Development] = "development",
    };

    public static readonly IReadOnlyDictionary<PolicyPermission, string> Permissions = new Dictionary<PolicyPermission, string>
    {
        [PolicyPermission.Inventory] = "inventory",
        [PolicyPermission.SessionUsers] = "session_users",
        [PolicyPermission.Publish] = "publish",
        [PolicyPermission.Unpublish] = "unpublish",
        [PolicyPermission.Restart] = "restart",
        [PolicyPermission.ServiceControl] = "service_control",
        [PolicyPermission.Licence] = "licence",
        [PolicyPermission.Scripts] = "scripts",
    };

    public static readonly IReadOnlyDictionary<PublishScope, string> Scopes = new Dictionary<PublishScope, string>
    {
        [PublishScope.Tenant] = "tenant",
        [PublishScope.Global] = "global",
    };

    public static readonly IReadOnlyDictionary<RestartMode, string> RestartModes = new Dictionary<RestartMode, string>
    {
        [RestartMode.StopAllThenStartAll] = "stop-all-then-start-all",
        [RestartMode.OneByOne] = "one-by-one",
    };

    public static readonly IReadOnlyDictionary<LicenceTarget, string> LicenceTargets = new Dictionary<LicenceTarget, string>
    {
        [LicenceTarget.NavDatabase] = "NavDatabase",
        [LicenceTarget.Master] = "Master",
        [LicenceTarget.Default] = "Default",
        [LicenceTarget.Tenant] = "Tenant",
    };

    public static readonly IReadOnlyDictionary<MachineMethod, string> MachineMethods = new Dictionary<MachineMethod, string>
    {
        [MachineMethod.ServiceManager] = "service-manager",
        [MachineMethod.PowerShellRemoting] = "powershell-remoting",
    };

    public static readonly IReadOnlyDictionary<PowerShellEdition, string> PowerShellEditions = new Dictionary<PowerShellEdition, string>
    {
        [PowerShellEdition.WindowsPowerShell] = "5.1",
        [PowerShellEdition.PowerShell7] = "7",
    };

    /// <summary>The value whose word is exactly <paramref name="word"/>.</summary>
    public static bool TryParse<T>(IReadOnlyDictionary<T, string> words, string word, out T value) where T : struct, Enum
    {
        foreach (var (key, text) in words)
        {
            if (string.Equals(text, word, StringComparison.Ordinal))
            {
                value = key;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>The allowed words as a list a person reads: <c>"a", "b" or "c"</c>.</summary>
    public static string Choices<T>(IReadOnlyDictionary<T, string> words) where T : notnull
    {
        var quoted = words.Values.Select(w => $"\"{w}\"").ToList();
        return quoted.Count == 1 ? quoted[0] : string.Join(", ", quoted.Take(quoted.Count - 1)) + " or " + quoted[^1];
    }
}
