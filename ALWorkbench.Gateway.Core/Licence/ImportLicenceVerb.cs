using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Policy;
using ALWorkbench.Gateway.Core.PowerShell;
using ALWorkbench.Gateway.Core.RestartGroup;

namespace ALWorkbench.Gateway.Core.Licence;

/// <summary>
/// The <c>import-licence</c> verb (#1369): imports the command's one licence file on the
/// database's publishing tier, restarts the database's restart group (a tier reads its
/// licence only when it starts), then reads the group's tiers again and sends them to
/// AL Workbench, so the new expiry and object ranges show at once. In that order, and each
/// step only after the one before went through: a failed import restarts nothing. See
/// <c>.design/on-prem-gateway.md</c>, "Licences".
///
/// <para><b>Before anything runs.</b> <c>GatewayRunner</c> has already checked the policy
/// file (<c>licence</c> on the publishing tier, the tenant for a tenant licence,
/// <c>restart</c> on every tier of the group, a <c>licence_target</c> for the database) and
/// downloaded the file with its SHA-256. This verb checks the policy again, that its
/// <c>licence_target</c> is still the one AL Workbench told the person about, that the tier
/// is on this computer, the file's SHA-256 again, its size, and that its format is the one
/// the tier's Business Central version imports (<see cref="GatewayLicenceFormats"/>).</para>
///
/// <para><b>The restart group</b> is the same <see cref="RestartGroupRunner"/> a deployment
/// uses, judged against the settings as they are just before it, like a deployment's. No
/// before or after script runs: those are a deployment's. Like <c>restart-group</c>, it does
/// not check <c>shared_restart</c>; AL Workbench refuses a shared group before sending.</para>
/// </summary>
public sealed class ImportLicenceVerb : ICommandExecutor
{
    private readonly IGatewayPolicy _policy;
    private readonly PowerShellRunner _runner;
    private readonly IServiceTierLocator _locator;
    private readonly string _machineName;
    private readonly RestartGroupRunner _restarts;

    public ImportLicenceVerb(IGatewayPolicy policy, PowerShellRunner runner, IServiceTierLocator locator, string machineName, RestartGroupRunner restarts)
    {
        _policy = policy;
        _runner = runner;
        _locator = locator;
        _machineName = machineName;
        _restarts = restarts;
    }

    public async Task<GatewayCommandOutcome> ExecuteAsync(GatewayCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var command = context.Command;
        if (command.Verb != GatewayVerb.ImportLicence)
        {
            return Refuse($"The gateway sent a '{GatewayVerbs.ToWire(command.Verb)}' command to the import-licence verb.");
        }

        // The customer's file again, as it is now.
        var decision = _policy.Check(command);
        if (!decision.Allowed) return Refuse(decision.Reason ?? "Not allowed by the customer's gateway settings.");
        var request = PolicyRequest.FromCommand(command, out var problem);
        if (request?.Target is null) return Refuse(problem ?? "The command does not say which service tier it is for.");
        var judged = decision.Policy ?? _policy.InForce;
        var tier = judged.Find(request.Target);
        if (tier is null) return Refuse($"{request.Target} is not listed in the customer's gateway settings (gateway.toml).");
        var database = judged.DatabaseOf(tier.Tier);
        if (database?.LicenceTarget is not { } licenceTarget)
        {
            return Refuse($"Not allowed by the customer's gateway settings: no [[database]] section with a licence_target includes {tier.Tier}.");
        }
        var target = PolicyWords.LicenceTargets[licenceTarget];

        // What the person confirmed in AL Workbench is where the licence goes, or nothing happens.
        var asked = command.Parameters?[GatewayImportLicenceParameters.LicenceTargetParameter] is JsonValue value
                    && value.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>()
            : null;
        if (asked is null)
        {
            return Refuse("The command does not say where the licence was meant to go, so the gateway cannot check that against its settings.");
        }
        if (!string.Equals(asked, target, StringComparison.Ordinal))
        {
            return Refuse($"The customer's gateway settings now import licences into {Where(target)}, but AL Workbench asked for {Where(asked)}. Upload the licence again to see where it goes now.");
        }

        if (!string.Equals(tier.Tier.Machine, _machineName, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse($"{tier.Tier} is on another computer. This version of the gateway imports licences only on the service tiers on its own computer ({_machineName}).");
        }
        var install = _locator.Find(tier.Tier.Instance);
        if (install is null)
        {
            return Refuse($"There is no Business Central service for {tier.Tier.Instance} on {_machineName} (no Windows service {ServiceTierInstall.ServiceName(tier.Tier.Instance)}).");
        }

        if (command.Files.Count != 1)
        {
            return Refuse("An import-licence command carries exactly one file, the licence.");
        }
        var file = command.Files[0];
        if (!context.Files.TryGetValue(file.Id, out var path))
        {
            return Refuse($"The licence file {file.Name} was not downloaded.");
        }
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > GatewayLicenceFormats.MaxBytes)
            {
                return Refuse($"{file.Name} is larger than {GatewayLicenceFormats.MaxBytes / (1024 * 1024)} MB, which no licence file is.");
            }
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Refuse($"The file '{file.Name}' is not the one AL Workbench sent: its SHA-256 does not match.");
            }
        }
        if (GatewayLicenceFormats.Refusal(file.Name, install.Version) is { } format)
        {
            return Refuse(format);
        }

        // 1. Import. Only an answer from the script itself says whether Business Central
        // imported it. A run that was cut off (it timed out, or the command or the gateway
        // was stopped) may have got as far as the database, so it says it does not know.
        var shell = new PowerShellTarget(PowerShellChoice.For(tier.PowerShell, install.Version), install.ServiceFolder);
        if (_runner.ModuleProblems(ImportLicenceScript.Import, shell) is { Count: > 0 } unsafeModule)
        {
            return Refuse("Someone other than administrators can change the Business Central administration module the gateway would load for this tier, so it does not run anything there. "
                + string.Join(" ", unsafeModule));
        }
        var tenant = request.Tenant;
        context.Progress($"Importing the licence {file.Name} into {Where(target, tenant)} on {tier.Tier}.");
        PowerShellResult ran;
        try
        {
            ran = await _runner.RunAsync(ImportLicenceScript.Import, shell,
                ImportLicenceScript.Parameters(tier.Tier.Instance, path, target, tenant),
                ImportLicenceScript.Timeout, context.Progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Returned rather than thrown, so the gateway keeps the result and sends it
            // when it can: AL Workbench must not read this as "not imported".
            return Unknown(target, "the import was stopped before Business Central said how it went");
        }
        var entry = ran.Succeeded ? ran.Output.OfType<JsonObject>().FirstOrDefault() : null;
        if (entry?["ok"]?.GetValueKind() == System.Text.Json.JsonValueKind.False)
        {
            var why = entry["error"] is JsonValue text && text.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? text.GetValue<string>()
                : "Business Central gave no reason.";
            context.Progress("FAILED Business Central did not import the licence: " + why);
            return GatewayCommandOutcome.Failure(
                $"Business Central did not import the licence: {why} Nothing was restarted, so the service tiers keep the licence they had.",
                Result(target, imported: false, restart: null, refreshMessage: null));
        }
        if (entry?["ok"]?.GetValueKind() != System.Text.Json.JsonValueKind.True)
        {
            return Unknown(target, ran.TimedOut
                ? "the import did not finish in time and was stopped"
                : "PowerShell ended before it said how the import went (" + (ran.Error ?? "no reason given").TrimEnd('.') + ")");
        }
        context.Progress($"Imported the licence into {Where(target, tenant)}. Restarting the service tiers, which read a new licence only when they start.");

        // 2. Restart, under the settings as they are now: the whole check again, so a
        // file changed during the import (change hours, a tier taken out, read-only)
        // stops the restart the way it would stop a new command.
        var again = _policy.Check(command);
        var policy = again.Policy ?? _policy.InForce;
        string? refusal = null;
        if (!again.Allowed)
        {
            refusal = "the customer's gateway settings no longer allow it: " + (again.Reason ?? "no reason given").TrimEnd('.') + ". So the service tiers were not restarted.";
        }
        else if (policy.Find(tier.Tier) is null)
        {
            refusal = $"{tier.Tier} is no longer listed in the customer's gateway settings, so the service tiers were not restarted.";
        }
        else if (RestartGroupRunner.GroupOf(policy, tier.Tier).Tiers.FirstOrDefault(t => policy.Find(t)?.Allows(PolicyPermission.Restart) != true) is { } notAllowed)
        {
            refusal = $"the customer's gateway settings no longer allow 'restart' on {notAllowed}, so the service tiers were not restarted.";
        }
        GatewayRestartGroupResult restart;
        if (refusal is not null)
        {
            context.Progress("FAILED Not restarted: " + refusal);
            restart = RestartGroupRunner.NotRun(policy, tier.Tier, char.ToUpperInvariant(refusal[0]) + refusal[1..]);
        }
        else
        {
            restart = await _restarts.RunAsync(policy, tier.Tier, context.Progress, ct, command.Id).ConfigureAwait(false);
        }

        // 3. Read the tiers again, so AL Workbench shows the new licence straight away. Done
        // whatever the restart did: the licence is in the database either way.
        var refreshMessage = await RefreshAsync(context, policy, tier.Tier, ct).ConfigureAwait(false);

        var result = Result(target, imported: true, restart, refreshMessage);
        if (refusal is not null)
        {
            return GatewayCommandOutcome.Failure($"The licence was imported, but {refusal} The tiers use the new licence once they are restarted.", result);
        }
        if (!restart.Restarted)
        {
            var failed = restart.Tiers.FirstOrDefault(t => t.Outcome == GatewayRestartTierOutcomes.Failed);
            return GatewayCommandOutcome.Failure(
                $"The licence was imported, but the service tiers were not all restarted: {failed?.Message ?? restart.Message ?? "the gateway did not say why."} A tier uses the new licence once it is restarted.",
                result);
        }
        return GatewayCommandOutcome.Success(result);
    }

    /// <summary>
    /// Reads every tier of the database that the settings let the gateway read, through the
    /// loop. Null when each went through; otherwise the first reason one did not.
    /// </summary>
    private static async Task<string?> RefreshAsync(GatewayCommandContext context, GatewayPolicy policy, TierRef publishing, CancellationToken ct)
    {
        if (context.RefreshInventory is null) return "This gateway does not read inventory.";
        var tiers = (policy.DatabaseOf(publishing)?.Tiers ?? [publishing])
            .Where(t => policy.Find(t)?.Allows(PolicyPermission.Inventory) == true)
            .ToList();
        if (tiers.Count == 0) return "No tier of the database allows inventory, so nothing was read again.";
        string? first = null;
        foreach (var each in tiers)
        {
            if (ct.IsCancellationRequested)
            {
                first ??= "The command was stopped before the service tiers were read again.";
                break;
            }
            var problem = await context.RefreshInventory(each, ct).ConfigureAwait(false);
            if (problem is null) continue;
            context.Progress($"Could not read {each} again: {problem} It is read again within 15 minutes.");
            first ??= problem;
        }
        if (first is null) context.Progress("Read the service tiers again, so AL Workbench shows the new licence.");
        return first;
    }

    private static string Where(string target, string? tenant = null) => target switch
    {
        GatewayLicenceTargets.NavDatabase => "the application database",
        GatewayLicenceTargets.Master => "the master database (every tenant)",
        GatewayLicenceTargets.Default => "the server's default database",
        GatewayLicenceTargets.Tenant => tenant is null ? "one tenant's database" : $"tenant '{tenant}'",
        _ => $"'{target}'",
    };

    /// <summary>
    /// The import was cut off before Business Central answered: nothing is restarted, and
    /// the result says the licence may or may not be in the database.
    /// </summary>
    private static GatewayCommandOutcome Unknown(string target, string why) =>
        GatewayCommandOutcome.Failure(
            $"It is not known whether the licence was imported: {why}. Nothing was restarted. The licence the service tiers show after their next restart is the one in the database; to be sure, upload it again.",
            new GatewayImportLicenceResult { Target = target, ImportUnknown = true }.ToJson());

    private static GatewayCommandOutcome Refuse(string message) => GatewayCommandOutcome.Failure(message + " Nothing was changed.");

    private static JsonObject Result(string target, bool imported, GatewayRestartGroupResult? restart, string? refreshMessage) =>
        new GatewayImportLicenceResult
        {
            Target = target,
            Imported = imported,
            Restart = restart,
            Refreshed = imported && refreshMessage is null,
            RefreshMessage = imported ? refreshMessage : null,
        }.ToJson();
}
