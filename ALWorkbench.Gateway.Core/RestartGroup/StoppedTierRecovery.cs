using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Policy;
using Microsoft.Extensions.Logging;

namespace ALWorkbench.Gateway.Core.RestartGroup;

/// <summary>
/// Starts the service tiers a restart group stopped and never started again, because the
/// gateway stopped or crashed between the two (#1368). <see cref="RestartGroupRunner"/>
/// records each tier in the run-once store before it sends the stop and clears it once
/// the start has been tried; whatever is still recorded when the gateway starts is started
/// here, before the gateway takes any command, and logged (events 1010 to 1012).
///
/// <para>The command that stopped them is then reported to AL Workbench as failed, saying
/// which tiers were started again, rather than as merely interrupted. A command whose
/// result was already saved keeps that result.</para>
/// </summary>
public sealed class StoppedTierRecovery
{
    private readonly RestartGroupRunner _restarts;
    private readonly IRunOnceStore _store;
    private readonly ILogger _logger;

    public StoppedTierRecovery(RestartGroupRunner restarts, IRunOnceStore store, ILogger logger)
    {
        _restarts = restarts;
        _store = store;
        _logger = logger;
    }

    /// <summary>Starts every recorded tier under <paramref name="policy"/>, then forgets them. Never cancelled: a start runs to its own time limit.</summary>
    public async Task RecoverAsync(GatewayPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var saved = _store.PendingResults();
        foreach (var (commandId, tiers) in _store.StoppedTiers())
        {
            _logger.TiersLeftStopped(commandId, string.Join(", ", tiers));
            var lines = new List<string>();
            foreach (var tier in tiers)
            {
                var (running, message) = await _restarts.StartLeftStoppedAsync(policy, new TierRef(tier.Machine, tier.Instance)).ConfigureAwait(false);
                if (running) _logger.TierStartedAgain(commandId, tier.ToString());
                else _logger.TierNotStartedAgain(commandId, tier.ToString(), message);
                lines.Add(message);
            }
            _store.SetStoppedTiers(commandId, []);
            if (saved.ContainsKey(commandId)) continue;
            _store.SaveResult(commandId, new GatewayCommandResultRequest
            {
                Succeeded = false,
                FailureMessage = $"The gateway service stopped while it was restarting the service tiers, with {string.Join(", ", tiers)} stopped. "
                    + $"When it started again it started them: {string.Join(" ", lines)}",
            });
        }
    }
}
