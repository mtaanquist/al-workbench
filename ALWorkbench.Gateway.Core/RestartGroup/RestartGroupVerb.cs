using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Policy;

namespace ALWorkbench.Gateway.Core.RestartGroup;

/// <summary>
/// The <c>restart-group</c> verb (#1368): restarts the restart group of the database the
/// named tier belongs to, on its own, for a manual restart from AL Workbench (#1371). The
/// same <see cref="RestartGroupRunner"/> runs after a deployment's apps and after a licence
/// import (#1369). The policy check before the command needs <c>service_control</c> on
/// every tier of the group; the group comes from the policy file only. No before or after
/// script runs: those belong to a deployment. It does not check <c>shared_restart</c>: only
/// AL Workbench knows which Solutions a tier serves, so whatever sends this verb (#1371,
/// #1369) must refuse a shared group there first, as deployments do.
/// </summary>
public sealed class RestartGroupVerb : ICommandExecutor
{
    private readonly IGatewayPolicy _policy;
    private readonly RestartGroupRunner _restarts;

    public RestartGroupVerb(IGatewayPolicy policy, RestartGroupRunner restarts)
    {
        _policy = policy;
        _restarts = restarts;
    }

    public async Task<GatewayCommandOutcome> ExecuteAsync(GatewayCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var command = context.Command;
        if (command.Verb != GatewayVerb.RestartGroup)
        {
            return GatewayCommandOutcome.Failure($"The gateway sent a '{GatewayVerbs.ToWire(command.Verb)}' command to the restart-group verb. Nothing was changed.");
        }
        var decision = _policy.Check(command);
        if (!decision.Allowed) return GatewayCommandOutcome.Failure((decision.Reason ?? "Not allowed by the customer's gateway settings.") + " Nothing was changed.");
        var request = PolicyRequest.FromCommand(command, out var problem);
        if (request?.Target is null) return GatewayCommandOutcome.Failure((problem ?? "The command does not say which service tier it is for.") + " Nothing was changed.");

        // The group comes from the policy that was just judged, not a later one.
        var result = await _restarts.RunAsync(decision.Policy ?? _policy.InForce, request.Target, context.Progress, ct, command.Id).ConfigureAwait(false);
        if (result.Restarted) return GatewayCommandOutcome.Success(result.ToJson());
        var failed = result.Tiers.FirstOrDefault(t => t.Outcome == GatewayRestartTierOutcomes.Failed);
        return GatewayCommandOutcome.Failure(
            failed?.Message ?? result.Message ?? "The service tiers were not all restarted.",
            result.ToJson());
    }
}
