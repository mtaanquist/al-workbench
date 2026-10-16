using ALWorkbench.Gateway.Contracts;

namespace ALWorkbench.Gateway.Core.Policy;

/// <summary>
/// A machine and a server instance on it. Windows compares both without regard to
/// case (computer names and service names), so equality does too, while the
/// spelling customer IT wrote is kept for messages.
/// </summary>
public sealed record TierRef(string Machine, string Instance)
{
    public bool Equals(TierRef? other) => other is not null
        && string.Equals(Machine, other.Machine, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Instance, other.Instance, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(Machine), StringComparer.OrdinalIgnoreCase.GetHashCode(Instance));

    public override string ToString() => $"{Machine} / {Instance}";

    public GatewayPolicyTier ToReport() => new() { Machine = Machine, Instance = Instance };
}

/// <summary>
/// One service tier with the permissions in force for it: its own <c>allow</c> and
/// <c>change_hours</c> where it sets them, otherwise those of
/// <c>[defaults.&lt;environment&gt;]</c>, otherwise read-only with no hours limit.
/// Each of the two replaces the default whole; they are never merged.
/// </summary>
public sealed record EffectiveInstance(
    TierRef Tier,
    PolicyEnvironment Environment,
    IReadOnlySet<PolicyPermission> Allow,
    ChangeWindow? ChangeHours,
    IReadOnlyList<string> Tenants,
    IReadOnlyList<string>? Publishers,
    bool AllowUnsigned,
    PublishScope Scope,
    PowerShellEdition? PowerShell)
{
    public bool Allows(PolicyPermission permission) => Allow.Contains(permission);

    public bool AllowsTenant(string tenant) => Tenants.Contains(tenant, StringComparer.OrdinalIgnoreCase);

    /// <summary>Null publishers means any publisher; otherwise an exact match, ignoring case.</summary>
    public bool AllowsPublisher(string publisher) =>
        Publishers is null || Publishers.Contains(publisher.Trim(), StringComparer.OrdinalIgnoreCase);

    public GatewayPolicyInstance ToReport() => new()
    {
        Machine = Tier.Machine,
        Name = Tier.Instance,
        Environment = PolicyWords.Environments[Environment],
        Allow = Enum.GetValues<PolicyPermission>().Where(Allow.Contains).Select(p => PolicyWords.Permissions[p]).ToList(),
        ChangeHours = ChangeHours?.ToString(),
        Tenants = Tenants,
        Publishers = Publishers,
        AllowUnsigned = AllowUnsigned,
        Scope = PolicyWords.Scopes[Scope],
        PowerShell = PowerShell is { } edition ? PolicyWords.PowerShellEditions[edition] : null,
    };
}

/// <summary>Tiers that share a database: the one that publishes, and the ones restarted together.</summary>
public sealed record PolicyDatabase(
    string Name,
    TierRef PublishOn,
    IReadOnlyList<TierRef> Restart,
    RestartMode RestartMode,
    bool SharedRestart,
    LicenceTarget? LicenceTarget,
    string? Before,
    string? After)
{
    /// <summary>Every tier of the database: the publishing one and the restart group.</summary>
    public IEnumerable<TierRef> Tiers => Restart.Prepend(PublishOn).Distinct();

    public GatewayPolicyDatabase ToReport() => new()
    {
        Name = Name,
        PublishOn = PublishOn.ToReport(),
        Restart = Restart.Select(t => t.ToReport()).ToList(),
        RestartMode = PolicyWords.RestartModes[RestartMode],
        SharedRestart = SharedRestart,
        LicenceTarget = LicenceTarget is { } target ? PolicyWords.LicenceTargets[target] : null,
        Before = Before,
        After = After,
    };
}

/// <summary>How the gateway reaches another machine.</summary>
public sealed record PolicyMachine(string Name, MachineMethod Method)
{
    public GatewayPolicyMachine ToReport() => new() { Name = Name, Method = PolicyWords.MachineMethods[Method] };
}

/// <summary>
/// The local policy file, read and checked: what this gateway may touch and do.
/// Built only by <see cref="GatewayPolicyParser"/>, so every reference in it
/// (a database's tiers, a remote machine) points at something that exists. See
/// <c>.design/on-prem-gateway.md</c>, "The local policy file".
/// </summary>
public sealed class GatewayPolicy
{
    /// <summary>Nothing listed, so nothing allowed.</summary>
    public static readonly GatewayPolicy Empty = new([], [], []);

    public GatewayPolicy(IReadOnlyList<EffectiveInstance> instances, IReadOnlyList<PolicyDatabase> databases, IReadOnlyList<PolicyMachine> machines)
    {
        Instances = instances;
        Databases = databases;
        Machines = machines;
    }

    public IReadOnlyList<EffectiveInstance> Instances { get; }

    public IReadOnlyList<PolicyDatabase> Databases { get; }

    public IReadOnlyList<PolicyMachine> Machines { get; }

    public EffectiveInstance? Find(TierRef tier) => Instances.FirstOrDefault(i => i.Tier.Equals(tier));

    /// <summary>The database whose publishing tier or restart group includes <paramref name="tier"/>; a tier belongs to at most one.</summary>
    public PolicyDatabase? DatabaseOf(TierRef tier) => Databases.FirstOrDefault(d => d.Tiers.Contains(tier));

    public PolicyMachine? Machine(string name) =>
        Machines.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every before and after script the file names.</summary>
    public IEnumerable<string> Scripts => Databases
        .SelectMany(d => new[] { d.Before, d.After })
        .OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The same tiers, allowed to read and nothing else. What stays in force while
    /// the file has mistakes: the last good file still says which tiers may be read,
    /// but the change that broke it may have been meant to take a permission away.
    /// </summary>
    public GatewayPolicy ReadOnly() => new(
        Instances.Select(i => i with
        {
            Allow = i.Allows(PolicyPermission.Inventory) ? new HashSet<PolicyPermission> { PolicyPermission.Inventory } : new HashSet<PolicyPermission>(),
        }).ToList(),
        Databases,
        Machines);

    public GatewayPolicyReport ToReport(string state, IReadOnlyList<string> problems) => new()
    {
        State = state,
        Problems = problems,
        Instances = Instances.Select(i => i.ToReport()).ToList(),
        Databases = Databases.Select(d => d.ToReport()).ToList(),
        Machines = Machines.Select(m => m.ToReport()).ToList(),
    };
}
