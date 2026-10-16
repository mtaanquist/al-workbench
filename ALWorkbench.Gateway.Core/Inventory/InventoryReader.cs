using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Policy;
using ALWorkbench.Gateway.Core.PowerShell;

namespace ALWorkbench.Gateway.Core.Inventory;

/// <summary>Reads one service tier for the inventory. The seam the loop's tests replace.</summary>
public interface IInventoryReader
{
    /// <summary>
    /// Reads <paramref name="tier"/>. Never fails: a tier that cannot be read comes back
    /// with <see cref="GatewayInstanceInventory.Error"/> set. Throws only when cancelled.
    /// </summary>
    Task<GatewayInstanceInventory> ReadAsync(EffectiveInstance tier, Action<string>? progress, CancellationToken ct);
}

/// <summary>What <see cref="PowerShellInventoryReader.ReadRawAsync"/> got: the script's output as it printed it, or why there is none.</summary>
/// <param name="Output">The script's output, before <see cref="InventoryNormaliser"/>; null when it did not run or failed.</param>
/// <param name="Error">Why nothing was read; null when <paramref name="Output"/> is set.</param>
/// <param name="PowerShell">The PowerShell used, as the policy file spells it; null when nothing ran.</param>
public sealed record RawInventory(JsonNode? Output, string? Error, string? PowerShell);

/// <summary>
/// <see cref="IInventoryReader"/> over <see cref="InventoryScript"/>: finds the tier's
/// install folder from its Windows service, picks its PowerShell
/// (<see cref="PowerShellChoice"/>), and runs the script with the
/// <see cref="PowerShellRunner"/>, which refuses a tier whose folder someone other than
/// administrators can change. Only tiers on this computer are read; a tier on another
/// computer is reported as not read (reaching other computers comes with the restart
/// group, #1368).
/// </summary>
public sealed class PowerShellInventoryReader : IInventoryReader
{
    private readonly PowerShellRunner _runner;
    private readonly IServiceTierLocator _locator;
    private readonly string _machineName;

    public PowerShellInventoryReader(PowerShellRunner runner, IServiceTierLocator locator, string machineName)
    {
        _runner = runner;
        _locator = locator;
        _machineName = machineName;
    }

    public async Task<GatewayInstanceInventory> ReadAsync(EffectiveInstance tier, Action<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tier);
        var raw = await ReadRawAsync(tier, progress, ct).ConfigureAwait(false);
        return raw.Output is null
            ? InventoryNormaliser.Failed(tier, raw.Error ?? "The service tier could not be read.", raw.PowerShell)
            : InventoryNormaliser.FromScriptOutput(raw.Output, tier) with { PowerShell = raw.PowerShell };
    }

    /// <summary>
    /// Runs the script and returns its output untouched: what <c>check --inventory</c>
    /// prints, so customer IT can capture real samples.
    /// </summary>
    public async Task<RawInventory> ReadRawAsync(EffectiveInstance tier, Action<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tier);
        if (!string.Equals(tier.Tier.Machine, _machineName, StringComparison.OrdinalIgnoreCase))
        {
            return new RawInventory(null,
                $"{tier.Tier.Machine} is another computer. This version of the gateway reads only the service tiers on its own computer ({_machineName}).",
                null);
        }
        var install = _locator.Find(tier.Tier.Instance);
        if (install is null)
        {
            return new RawInventory(null,
                $"There is no Business Central service for {tier.Tier.Instance} on {_machineName} (no Windows service {ServiceTierInstall.ServiceName(tier.Tier.Instance)}).",
                null);
        }
        var edition = PowerShellChoice.For(tier.PowerShell, install.Version);
        var word = PolicyWords.PowerShellEditions[edition];
        var parameters = InventoryScript.Parameters(tier.Tier.Instance, install.ServiceFolder, tier.Allows(PolicyPermission.SessionUsers));
        var result = await _runner.RunAsync(InventoryScript.Operation, new PowerShellTarget(edition, install.ServiceFolder), parameters,
            InventoryScript.ProcessTimeout, progress, ct).ConfigureAwait(false);
        if (!result.Succeeded) return new RawInventory(null, result.Error, word);
        return new RawInventory(result.Output.Count == 1 ? result.Output[0]?.DeepClone() : result.Output.DeepClone(), null, word);
    }
}
