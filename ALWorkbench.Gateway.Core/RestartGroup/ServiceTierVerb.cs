using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Policy;

namespace ALWorkbench.Gateway.Core.RestartGroup;

/// <summary>
/// The <c>restart-instance</c>, <c>stop-instance</c> and <c>start-instance</c> verbs (#1371):
/// one tier, on request from the environment page. The policy check needs
/// <c>service_control</c> on that tier (<see cref="PolicyEnforcer"/>); it is made here, before
/// any process runs, so the verb never acts on a tier the settings in force do not allow,
/// whatever reached it. Service control is <see cref="RestartGroupRunner.RunTierActionAsync"/>'s:
/// a restart keeps the restart group's guarantee that a tier sent a stop is sent a start, and
/// a stop that was asked for is never started again, by this verb or by
/// <see cref="StoppedTierRecovery"/>. No script runs. <c>shared_restart</c> is AL Workbench's to
/// check, as for <c>restart-group</c>: only it knows which Solutions use a tier. See
/// <c>.design/on-prem-gateway.md</c>, "Service tier actions, as built".
/// </summary>
public sealed class ServiceTierVerb : ICommandExecutor
{
    private readonly IGatewayPolicy _policy;
    private readonly RestartGroupRunner _restarts;

    public ServiceTierVerb(IGatewayPolicy policy, RestartGroupRunner restarts)
    {
        _policy = policy;
        _restarts = restarts;
    }

    /// <summary>The verbs this executor runs, for the table that hands commands out.</summary>
    public static readonly IReadOnlyList<GatewayVerb> Verbs = [GatewayVerb.RestartInstance, GatewayVerb.StopInstance, GatewayVerb.StartInstance];

    public async Task<GatewayCommandOutcome> ExecuteAsync(GatewayCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var command = context.Command;
        if (GatewayServiceTierActions.For(command.Verb) is not { } action)
        {
            return GatewayCommandOutcome.Failure($"The gateway sent a '{GatewayVerbs.ToWire(command.Verb)}' command to the service tier verb. Nothing was changed.");
        }
        var decision = _policy.Check(command);
        if (!decision.Allowed) return GatewayCommandOutcome.Failure((decision.Reason ?? "Not allowed by the customer's gateway settings.") + " Nothing was changed.");
        var request = PolicyRequest.FromCommand(command, out var problem);
        if (request?.Target is null) return GatewayCommandOutcome.Failure((problem ?? "The command does not say which service tier it is for.") + " Nothing was changed.");

        // How to reach the tier's computer comes from the settings just judged, not a later read.
        var result = await _restarts.RunTierActionAsync(decision.Policy ?? _policy.InForce, request.Target, action, context.Progress, ct, command.Id).ConfigureAwait(false);
        return result.Done
            ? GatewayCommandOutcome.Success(result.ToJson())
            : GatewayCommandOutcome.Failure(result.Message ?? $"{request.Target} did not {action}.", result.ToJson());
    }
}
