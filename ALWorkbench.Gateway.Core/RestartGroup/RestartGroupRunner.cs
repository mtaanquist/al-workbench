using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Policy;
using ALWorkbench.Gateway.Core.PowerShell;

namespace ALWorkbench.Gateway.Core.RestartGroup;

/// <summary>How long a tier may take to stop, and to report running again.</summary>
public sealed record RestartTimeouts(TimeSpan Stop, TimeSpan Start)
{
    /// <summary>
    /// Five minutes to stop (a tier waits for its sessions' transactions to end), ten to
    /// start (a BC 14 tier with many companies compiles and loads a while). Fixed rather
    /// than set per database; a setting can follow when a real server needs one.
    /// </summary>
    public static readonly RestartTimeouts Default = new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
}

/// <summary>
/// Restarts the restart group of one database (#1368): the tiers the policy file lists
/// under <c>restart</c> for the database the target tier belongs to, or only the target
/// when no <c>[[database]]</c> names it. The group comes only from the policy file, never
/// from the command, so the server can name a tier but never widen what is restarted.
/// Shared by <c>publish-install</c> (after every app went through), the
/// <c>restart-group</c> verb, and the licence import (#1369). See
/// <c>.design/on-prem-gateway.md</c>, "The restart group, as built".
///
/// <para><b>Order.</b> <c>stop-all-then-start-all</c> (the default) stops every tier in the
/// order listed, then starts every one, so old and new event subscribers never run side by
/// side. A tier that does not stop ends the stopping. <c>one-by-one</c> stops and starts
/// each tier before the next, so users always have a running tier; the first tier that does
/// not come back ends the group there, so one bad restart cannot take down the others.</para>
///
/// <para><b>Every tier sent a stop is sent a start.</b> Whatever happens after a stop has
/// gone out - the stop timed out, PowerShell died, the command was cancelled because AL
/// Workbench failed it or the service is stopping - the gateway tries to start that tier
/// again, so a failure never leaves a tier down that it found running. The starts ignore
/// cancellation (each still has its own timeout), and the tiers waiting for a start are
/// kept on disk in the run-once store, so a crash between the two is put right by
/// <see cref="StoppedTierRecovery"/> on the next start.</para>
///
/// <para>Each step is one child process (<see cref="ServiceControlScript"/>), with its own
/// wait, so each tier has its own line and its own timeout. A tier is "restarted" only when
/// it reports running again.</para>
/// </summary>
public sealed class RestartGroupRunner
{
    private readonly PowerShellRunner _runner;
    private readonly string _machineName;
    private readonly RestartTimeouts _timeouts;
    private readonly IRunOnceStore? _ledger;

    /// <param name="ledger">Where the tiers waiting for a start are kept across a stop or crash; null keeps them in memory only (tests, <c>check</c>).</param>
    public RestartGroupRunner(PowerShellRunner runner, string machineName, RestartTimeouts? timeouts = null, IRunOnceStore? ledger = null)
    {
        _runner = runner;
        _machineName = machineName;
        _timeouts = timeouts ?? RestartTimeouts.Default;
        _ledger = ledger;
    }

    /// <summary>How long one start may take, process margin included: what a stopping service waits for.</summary>
    public TimeSpan LongestStart => _timeouts.Start + ServiceControlScript.ProcessMargin;

    /// <summary>The tiers restarted for <paramref name="target"/>, in order, and how; from the policy only.</summary>
    public static (IReadOnlyList<TierRef> Tiers, RestartMode Mode) GroupOf(GatewayPolicy policy, TierRef target)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(target);
        var database = policy.DatabaseOf(target);
        return database is null
            ? ([target], RestartMode.StopAllThenStartAll)
            : (database.Restart, database.RestartMode);
    }

    /// <summary>
    /// Restarts the group of <paramref name="target"/> under <paramref name="policy"/>.
    /// Never throws for a tier that fails; the result says which. The permission check is
    /// the caller's (the policy check before the command, and again before restarting).
    /// <paramref name="ct"/> stops further stops; once a stop has gone out, the starts run
    /// whatever it says. <paramref name="commandId"/> names the command the tiers waiting
    /// for a start are recorded under.
    /// </summary>
    public async Task<GatewayRestartGroupResult> RunAsync(GatewayPolicy policy, TierRef target, Action<string> progress,
        CancellationToken ct, long? commandId = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var (tiers, mode) = GroupOf(policy, target);
        var modeWord = PolicyWords.RestartModes[mode];
        var reports = tiers.Select(t => new TierReport(t)).ToList();
        var waiting = new List<TierReport>();
        progress(tiers.Count == 1
            ? $"Restarting {tiers[0]}."
            : mode == RestartMode.OneByOne
                ? $"Restarting {tiers.Count} service tiers one at a time: {string.Join(", ", tiers)}."
                : $"Restarting {tiers.Count} service tiers: stopping all of them, then starting all of them: {string.Join(", ", tiers)}.");

        void Record()
        {
            if (_ledger is null || commandId is not { } id) return;
            _ledger.SetStoppedTiers(id, waiting.Select(r => new StoppedTier { Machine = r.Tier.Machine, Instance = r.Tier.Instance }).ToList());
        }

        // Sends the stop, after recording the tier as waiting for its start.
        async Task<bool> StopOneAsync(TierReport report)
        {
            waiting.Add(report);
            Record();
            var stopped = await StepAsync(policy, report, ServiceControlScript.Stop, _timeouts.Stop, progress, ct).ConfigureAwait(false);
            if (!report.Sent)
            {
                // Nothing reached the tier (no way to reach its computer): nothing to start.
                waiting.Remove(report);
                Record();
            }
            return stopped;
        }

        // Starts a tier sent a stop, whatever came after; never cancelled.
        async Task<bool> StartOneAsync(TierReport report)
        {
            var started = await StepAsync(policy, report, ServiceControlScript.Start, _timeouts.Start, progress, CancellationToken.None).ConfigureAwait(false);
            waiting.Remove(report);
            Record();
            return started;
        }

        if (mode == RestartMode.OneByOne)
        {
            for (var i = 0; i < reports.Count; i++)
            {
                var report = reports[i];
                if (ct.IsCancellationRequested)
                {
                    foreach (var later in reports.Skip(i)) later.Leave(Cancelled, progress);
                    break;
                }
                var stopped = await StopOneAsync(report).ConfigureAwait(false);
                var started = waiting.Contains(report) && await StartOneAsync(report).ConfigureAwait(false);
                if (stopped && started)
                {
                    report.Done(progress);
                    continue;
                }
                if (!stopped && started) report.StartedAgain(progress);
                foreach (var later in reports.Skip(i + 1))
                {
                    later.Leave(stopped || !started
                        ? $"Not restarted, because {report.Tier} did not come back."
                        : $"Not restarted, because {report.Tier} did not stop.", progress);
                }
                break;
            }
        }
        else
        {
            TierReport? stuck = null;
            var cancelled = false;
            foreach (var report in reports)
            {
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                if (!await StopOneAsync(report).ConfigureAwait(false))
                {
                    stuck = report;
                    break;
                }
            }
            // Every tier sent a stop is started again, the stuck one included, whatever
            // happened after it.
            foreach (var report in waiting.ToList())
            {
                var started = await StartOneAsync(report).ConfigureAwait(false);
                if (!started) continue;
                if (report == stuck) report.StartedAgain(progress);
                else if (stuck is null && !cancelled) report.Done(progress);
                else report.Leave(stuck is not null
                    ? $"Stopped and started again, but the group was not restarted, because {stuck.Tier} did not stop."
                    : "Stopped and started again, but the group was not restarted, because the command was stopped.", progress, restartedAnyway: true);
            }
            foreach (var report in reports.Where(r => r.Untouched))
            {
                report.Leave(stuck is not null ? $"Not restarted, because {stuck.Tier} did not stop." : Cancelled, progress);
            }
        }

        return new GatewayRestartGroupResult
        {
            Mode = modeWord,
            Restarted = reports.All(r => r.Outcome == GatewayRestartTierOutcomes.Restarted && r.Message is null),
            Tiers = reports.Select(r => r.ToResult()).ToList(),
        };
    }

    private const string Cancelled = "Not restarted, because the command was stopped before the gateway reached it.";

    /// <summary>
    /// Starts one tier left stopped by a restart group that did not finish (a stop or crash
    /// of the gateway between the stop and the start). Never cancelled. The message says
    /// how it went.
    /// </summary>
    public async Task<(bool Running, string Message)> StartLeftStoppedAsync(GatewayPolicy policy, TierRef tier)
    {
        ArgumentNullException.ThrowIfNull(tier);
        var report = new TierReport(tier);
        var lines = new List<string>();
        var started = await StepAsync(policy, report, ServiceControlScript.Start, _timeouts.Start, lines.Add, CancellationToken.None).ConfigureAwait(false);
        return (started, started ? $"{tier} is running again." : report.Message ?? $"{tier} could not be started.");
    }

    /// <summary>A result for a group that was not restarted at all, every tier named, for the reason given.</summary>
    public static GatewayRestartGroupResult NotRun(GatewayPolicy policy, TierRef target, string reason)
    {
        var (tiers, mode) = GroupOf(policy, target);
        return new GatewayRestartGroupResult
        {
            Mode = PolicyWords.RestartModes[mode],
            Restarted = false,
            Message = reason,
            Tiers = tiers.Select(t => new GatewayRestartTierResult
            {
                Machine = t.Machine,
                Instance = t.Instance,
                Outcome = GatewayRestartTierOutcomes.NotRestarted,
                Message = reason,
            }).ToList(),
        };
    }

    /// <summary>One stop or start of one tier. True when the tier reached the state asked for.</summary>
    private async Task<bool> StepAsync(GatewayPolicy? policy, TierReport report, string action, TimeSpan wait,
        Action<string> progress, CancellationToken ct)
    {
        var tier = report.Tier;
        MachineMethod? method = null;
        if (!string.Equals(tier.Machine, _machineName, StringComparison.OrdinalIgnoreCase))
        {
            // The parser makes every other computer name a [[machine]]; checked again, so a
            // tier is never reached some way the file does not say.
            if (policy?.Machine(tier.Machine) is not { } machine)
            {
                report.Fail($"{tier} is on another computer, and the customer's gateway settings do not say how to reach it (no [[machine]] section).", null, progress);
                return false;
            }
            method = machine.Method;
        }

        var verb = action == ServiceControlScript.Stop ? "stop" : "start";
        progress(action == ServiceControlScript.Stop ? $"{tier}: stopping." : $"{tier}: starting.");
        report.Sent = true;
        PowerShellResult ran;
        try
        {
            ran = await _runner.RunAsync(ServiceControlScript.Control, new PowerShellTarget(PowerShellEdition.WindowsPowerShell, null),
                ServiceControlScript.Parameters(tier, method, action, wait), wait + ServiceControlScript.ProcessMargin, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            report.Fail($"{tier}: the command was stopped while the gateway waited for it to {verb}.", null, progress);
            return false;
        }
        if (!ran.Succeeded)
        {
            var why = ran.TimedOut
                ? $"PowerShell did not answer within {ServiceControlScript.Duration(wait + ServiceControlScript.ProcessMargin)} and was stopped."
                : ran.Error ?? "PowerShell failed without saying why.";
            report.Fail($"{tier} could not be told to {verb}: {why}", null, progress);
            return false;
        }
        ServiceControlReport answer;
        try
        {
            answer = ServiceControlReport.FromScriptOutput(ran.Output);
        }
        catch (FormatException ex)
        {
            report.Fail($"{tier}: the gateway could not read whether it did {verb}: {ex.Message}", null, progress);
            return false;
        }
        report.State = answer.State;
        if (answer.TimedOut)
        {
            report.Fail(action == ServiceControlScript.Stop
                ? $"{tier} did not stop within {ServiceControlScript.Duration(wait)}; it was {answer.State ?? "in an unknown state"} when the gateway stopped waiting."
                : $"{tier} did not report running within {ServiceControlScript.Duration(wait)}; it was {answer.State ?? "in an unknown state"} when the gateway stopped waiting.",
                answer.State, progress);
            return false;
        }
        var wanted = action == ServiceControlScript.Stop ? "Stopped" : "Running";
        if (!string.Equals(answer.State, wanted, StringComparison.OrdinalIgnoreCase))
        {
            report.Fail($"{tier} is {answer.State ?? "in an unknown state"}, not {wanted.ToLowerInvariant()}.", answer.State, progress);
            return false;
        }
        progress(action == ServiceControlScript.Stop ? $"{tier}: stopped." : $"{tier}: running again.");
        return true;
    }

    /// <summary>One tier's way through the group.</summary>
    private sealed class TierReport(TierRef tier)
    {
        public TierRef Tier { get; } = tier;

        public string Outcome { get; private set; } = GatewayRestartTierOutcomes.NotRestarted;

        public string? Message { get; private set; }

        public string? State { get; set; }

        /// <summary>A stop or start was sent to it (it could be reached).</summary>
        public bool Sent { get; set; }

        /// <summary>Nothing was sent to it.</summary>
        public bool Untouched => Message is null && Outcome == GatewayRestartTierOutcomes.NotRestarted;

        public void Done(Action<string> progress)
        {
            if (Outcome == GatewayRestartTierOutcomes.Failed) return;
            Outcome = GatewayRestartTierOutcomes.Restarted;
            Message = null;
            progress($"Restarted {Tier}.");
        }

        public void Fail(string message, string? state, Action<string> progress)
        {
            // The first failure is the one that says what went wrong; a later one is added.
            Message = GatewayStepText.Cap(Outcome == GatewayRestartTierOutcomes.Failed && Message is not null ? $"{Message} Then: {message}" : message);
            Outcome = GatewayRestartTierOutcomes.Failed;
            State = state ?? State;
            progress("FAILED " + message);
        }

        /// <summary>A tier whose stop failed was started again and runs: still failed, with its final state said.</summary>
        public void StartedAgain(Action<string> progress)
        {
            Message = $"{GatewayStepText.Cap(Message)} The gateway started it again, and it is running.";
            progress($"{Tier}: started again, and running.");
        }

        /// <summary>Left out of the restart; <paramref name="restartedAnyway"/> when it was stopped and started regardless.</summary>
        public void Leave(string message, Action<string> progress, bool restartedAnyway = false)
        {
            if (Outcome == GatewayRestartTierOutcomes.Failed) return;
            Outcome = restartedAnyway ? GatewayRestartTierOutcomes.Restarted : GatewayRestartTierOutcomes.NotRestarted;
            Message = message;
            progress($"{Tier}: {message}");
        }

        public GatewayRestartTierResult ToResult() => new()
        {
            Machine = Tier.Machine,
            Instance = Tier.Instance,
            Outcome = Outcome,
            Message = Message,
            State = State,
        };
    }
}
