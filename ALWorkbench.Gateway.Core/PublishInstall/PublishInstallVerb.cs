using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Policy;
using ALWorkbench.Gateway.Core.PowerShell;

namespace ALWorkbench.Gateway.Core.PublishInstall;

/// <summary>
/// The <c>publish-install</c> verb (#1375): publishes, syncs and installs or upgrades a
/// build's apps on one tenant of the database's publishing tier, in the order the command
/// lists them (the build's dependency order), and unpublishes previous versions when
/// asked. See <c>.design/on-prem-gateway.md</c>, "Deploying to on-premises".
///
/// <para><b>Nothing runs before every file is checked.</b> <c>GatewayRunner</c> has
/// already checked the policy file and downloaded the files with their SHA-256. This
/// verb checks the policy again (the file may have changed during a long download), then,
/// for every app before any cmdlet runs: its SHA-256 again, that the file is the app the
/// command declares (id, version, publisher and, when sent, name, read from the
/// <c>.app</c>'s own manifest by <see cref="AppFile"/>), that the file's publisher is on
/// the tier's list, and, where the tier does not take unsigned apps, that the file carries
/// a signature whatever the command claimed. One refusal refuses the command with nothing
/// changed.</para>
///
/// <para><b>Then app by app.</b> <see cref="PublishInstallScript.Inspect"/> reads what the
/// tenant has, <see cref="AppInstallPlanner"/> decides, and
/// <see cref="PublishInstallScript.Apply"/> runs the steps. Each step is a line in the
/// command's log. The first failure stops the command; the apps after it are reported as
/// skipped. The result (<see cref="GatewayPublishInstallResult"/>) lists every app either
/// way.</para>
///
/// <para><b>Signatures.</b> Where the tier does not allow unsigned apps the gateway asks
/// Windows (<c>Get-AuthenticodeSignature</c>, which reads an <c>.app</c>'s signature
/// through the component Business Central installs for it) and refuses anything but a
/// valid signature, then publishes without <c>-SkipVerification</c>, so Business Central
/// checks it as well. Off Windows the first check cannot run; the tests stand it in.</para>
/// </summary>
public sealed class PublishInstallVerb : ICommandExecutor
{
    private readonly IGatewayPolicy _policy;
    private readonly PowerShellRunner _runner;
    private readonly IServiceTierLocator _locator;
    private readonly string _machineName;

    public PublishInstallVerb(IGatewayPolicy policy, PowerShellRunner runner, IServiceTierLocator locator, string machineName)
    {
        _policy = policy;
        _runner = runner;
        _locator = locator;
        _machineName = machineName;
    }

    public async Task<GatewayCommandOutcome> ExecuteAsync(GatewayCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var command = context.Command;
        if (command.Verb != GatewayVerb.PublishInstall)
        {
            return GatewayCommandOutcome.Failure($"The gateway sent a '{GatewayVerbs.ToWire(command.Verb)}' command to the publish-install verb. Nothing was changed.");
        }

        // The customer's file again, as it is now: the download may have taken a while.
        var decision = _policy.Check(command);
        if (!decision.Allowed) return Refuse((decision.Reason ?? "Not allowed by the customer's gateway settings.") + " Nothing was changed.");
        var request = PolicyRequest.FromCommand(command, out var problem);
        if (request?.Target is null) return Refuse((problem ?? "The command does not say which service tier it is for.") + " Nothing was changed.");
        var tier = _policy.InForce.Find(request.Target);
        if (tier is null) return Refuse($"{request.Target} is not listed in the customer's gateway settings (gateway.toml). Nothing was changed.");

        var syncMode = GatewaySyncModes.Add;
        if (command.Parameters[GatewayCommandParameters.SyncMode] is { } modeNode)
        {
            syncMode = modeNode.GetValueKind() == System.Text.Json.JsonValueKind.String ? modeNode.GetValue<string>() : string.Empty;
            if (!GatewaySyncModes.All.Contains(syncMode, StringComparer.Ordinal))
            {
                return Refuse($"The command asks for a schema sync mode the gateway does not know; it takes {string.Join(" or ", GatewaySyncModes.All)}. Nothing was changed.");
            }
        }
        if (!string.Equals(tier.Tier.Machine, _machineName, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse($"{tier.Tier} is on another computer. This version of the gateway installs apps only on the service tiers on its own computer ({_machineName}). Nothing was changed.");
        }
        var install = _locator.Find(tier.Tier.Instance);
        if (install is null)
        {
            return Refuse($"There is no Business Central service for {tier.Tier.Instance} on {_machineName} (no Windows service {ServiceTierInstall.ServiceName(tier.Tier.Instance)}). Nothing was changed.");
        }
        var target = new PowerShellTarget(PowerShellChoice.For(tier.PowerShell, install.Version), install.ServiceFolder);

        // Every app checked before anything is run.
        var reports = request.Apps.Select(a => new AppReport(a)).ToList();
        for (var i = 0; i < reports.Count; i++)
        {
            var report = reports[i];
            var refusal = await VerifyAsync(report, command, context.Files, tier, reports.Take(i), ct).ConfigureAwait(false);
            if (refusal is null) continue;
            report.Outcome = GatewayPublishInstallOutcomes.Refused;
            report.Message = refusal;
            report.Step(GatewayPublishInstallSteps.Verify, GatewayPublishInstallStepOutcomes.Failed, message: refusal);
            context.Progress($"{report.Title}: refused. {refusal}");
            foreach (var other in reports.Where(r => r != report))
            {
                other.Outcome = GatewayPublishInstallOutcomes.Skipped;
                other.Message = "Not started, because another app in the deployment was refused.";
            }
            return Finish(reports, $"{report.Title} was refused: {refusal} Nothing was changed.");
        }
        foreach (var report in reports)
        {
            report.Step(GatewayPublishInstallSteps.Verify, GatewayPublishInstallStepOutcomes.Done);
            context.Progress($"{report.Title}: checked. The file is the app declared, from {report.File!.Publisher}, and its SHA-256 matches"
                             + (tier.AllowUnsigned ? "." : "; it carries a signature."));
        }

        // App by app, in the order the command lists them.
        for (var i = 0; i < reports.Count; i++)
        {
            var report = reports[i];
            context.Progress($"App {i + 1} of {reports.Count}: {report.Title} on tenant '{request.Tenant}'.");
            var failure = await InstallAsync(report, request, tier, target, syncMode, context.Progress, ct).ConfigureAwait(false);
            if (failure is null) continue;
            foreach (var later in reports.Skip(i + 1))
            {
                later.Outcome = GatewayPublishInstallOutcomes.Skipped;
                later.Message = $"Not started, because {report.Title} failed.";
                context.Progress($"{later.Title}: skipped, because {report.Title} failed.");
            }
            var after = reports.Count > i + 1 ? " The apps after it were not installed." : string.Empty;
            return Finish(reports, failure + after);
        }

        var restarted = await RunRestartGroupAsync(context, ct).ConfigureAwait(false);
        return GatewayCommandOutcome.Success(Result(reports, restarted));
    }

    /// <summary>
    /// Runs after every app succeeded, and only then: the restart group of the database
    /// (#1368), and the after script customer IT names for it. Not built yet, so the
    /// tiers are not restarted, and the log says so.
    /// </summary>
    private static Task<bool> RunRestartGroupAsync(GatewayCommandContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        context.Progress("All apps are installed. The service tiers were not restarted: this version of the gateway does not restart them yet.");
        return Task.FromResult(false);
    }

    /// <summary>Null when the app may go ahead; otherwise why not, in a sentence.</summary>
    private static async Task<string?> VerifyAsync(AppReport report, GatewayCommandMessage command, IReadOnlyDictionary<long, string> files,
        EffectiveInstance tier, IEnumerable<AppReport> earlier, CancellationToken ct)
    {
        var declared = report.Declared;
        var file = command.Files.FirstOrDefault(f => string.Equals(f.Name, declared.File, StringComparison.Ordinal));
        if (file is null || !files.TryGetValue(file.Id, out var path))
        {
            return $"The command has no file '{declared.File}' for it.";
        }
        if (!Guid.TryParse(declared.Id, out var id))
        {
            return $"The command does not give the app id of '{declared.File}', so the gateway cannot check that the file is the app meant.";
        }
        if (!AppVersions.TryParse(declared.Version, out var version))
        {
            return $"The command does not give the version of '{declared.File}', so the gateway cannot check that the file is the app meant.";
        }
        report.Path = path;

        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return $"The file '{declared.File}' is not the one AL Workbench sent: its SHA-256 does not match.";
            }
        }

        AppFileInfo info;
        try
        {
            info = AppFile.Read(path);
        }
        catch (AppFileException ex)
        {
            return $"'{declared.File}' cannot be installed. {ex.Message}";
        }
        report.File = info;

        var mismatches = new List<string>();
        if (info.Id != id) mismatches.Add($"its app id is {info.Id}, not {id}");
        if (info.Version != version) mismatches.Add($"its version is {info.Version}, not {version}");
        if (!string.Equals(info.Publisher.Trim(), declared.Publisher.Trim(), StringComparison.OrdinalIgnoreCase)) mismatches.Add($"its publisher is '{info.Publisher}', not '{declared.Publisher}'");
        if (declared.Name is { } name && !string.Equals(info.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)) mismatches.Add($"its name is '{info.Name}', not '{name}'");
        if (mismatches.Count > 0)
        {
            return $"'{declared.File}' is not the app the command declares: {string.Join("; ", mismatches)}.";
        }
        if (earlier.Any(r => r.File?.Id == info.Id))
        {
            return $"The command lists the app {info.Name} ({info.Id}) more than once.";
        }
        if (!tier.AllowsPublisher(info.Publisher))
        {
            return $"Not allowed by the customer's gateway settings: {tier.Tier} does not take apps from the publisher '{info.Publisher}'.";
        }
        if (!tier.AllowUnsigned && !info.HasSignature)
        {
            return $"'{declared.File}' is not signed, and {tier.Tier} does not take unsigned apps (allow_unsigned is off in the customer's gateway settings).";
        }
        return null;
    }

    /// <summary>Inspects, plans and applies one app. Null when it went through; otherwise the command's failure message.</summary>
    private async Task<string?> InstallAsync(AppReport report, PolicyRequest request, EffectiveInstance tier, PowerShellTarget target,
        string syncMode, Action<string> progress, CancellationToken ct)
    {
        var app = report.File!;
        var inspected = await _runner.RunAsync(PublishInstallScript.Inspect, target,
            PublishInstallScript.InspectParameters(tier.Tier.Instance, request.Tenant, app.Id, request.UnpublishPrevious),
            PublishInstallScript.InspectTimeout, progress, ct).ConfigureAwait(false);
        AppInspection inspection;
        try
        {
            if (!inspected.Succeeded) throw new FormatException(inspected.Error ?? "PowerShell failed without saying why.");
            inspection = AppInspection.FromScriptOutput(inspected.Output);
        }
        catch (FormatException ex)
        {
            return Fail(report, null, $"The gateway could not read what tenant '{request.Tenant}' has of {report.Title}, so it changed nothing for it: {ex.Message}", progress);
        }

        var plan = AppInstallPlanner.Plan(inspection, app.Version, tier, request.Tenant, syncMode, request.UnpublishPrevious);
        if (plan.Refusal is not null)
        {
            return Fail(report, null, $"{report.Title} cannot be installed: {plan.Refusal}", progress);
        }
        if (plan.Outcome == GatewayPublishInstallOutcomes.AlreadyInstalled)
        {
            report.Outcome = plan.Outcome;
            report.Message = $"Version {app.Version} is already installed on tenant '{request.Tenant}', so nothing was done.";
            progress($"{report.Title}: {report.Message}");
            return null;
        }
        report.From = plan.From;

        var steps = plan.ToRun.ToList();
        var applied = await _runner.RunAsync(PublishInstallScript.Apply, target,
            PublishInstallScript.ApplyParameters(tier.Tier.Instance, request.Tenant, app, report.Path!, steps),
            PublishInstallScript.ApplyTimeout, progress, ct).ConfigureAwait(false);
        if (!applied.Succeeded)
        {
            // Killed or crashed: which steps finished is unknown, so none is blamed.
            return Fail(report, null,
                $"{report.Title}: PowerShell stopped before it reported how the steps went. {applied.Error} The command's log shows the last step it started; check tenant '{request.Tenant}' before deploying again.",
                progress);
        }
        var ran = applied.Output.OfType<JsonObject>().ToList();

        var index = 0;
        foreach (var planned in plan.Steps)
        {
            if (planned.Run is null)
            {
                report.Step(planned.Step, GatewayPublishInstallStepOutcomes.Skipped, planned.Version, planned.SkipReason);
                progress($"{report.Title}: {planned.SkipReason}");
                continue;
            }
            var entry = index < ran.Count ? ran[index++] : null;
            if (entry?["ok"]?.GetValueKind() == System.Text.Json.JsonValueKind.True)
            {
                report.Step(planned.Step, GatewayPublishInstallStepOutcomes.Done, planned.Version);
                progress($"{report.Title}: {Done(planned, request.Tenant, report.From, syncMode, tier)}");
                continue;
            }
            var error = entry?["error"] is System.Text.Json.Nodes.JsonValue text && text.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? text.GetValue<string>()
                : "PowerShell ended before it reported on this step.";
            return Fail(report, planned, $"{report.Title}: {Failed(planned)} {error}", progress);
        }
        report.Outcome = plan.Outcome!;
        return null;
    }

    private static string Fail(AppReport report, PlannedStep? at, string message, Action<string> progress)
    {
        report.Outcome = GatewayPublishInstallOutcomes.Failed;
        report.Message = message;
        if (at is not null) report.Step(at.Step, GatewayPublishInstallStepOutcomes.Failed, at.Version, message);
        progress(message);
        return message;
    }

    private static string Done(PlannedStep step, string tenant, Version? from, string syncMode, EffectiveInstance tier) => step.Step switch
    {
        GatewayPublishInstallSteps.Signature => "the signature verifies.",
        GatewayPublishInstallSteps.Publish => tier.Scope == PublishScope.Global
            ? "published for every tenant on the server."
            : $"published to tenant '{tenant}'.",
        GatewayPublishInstallSteps.Sync => $"synchronised ({syncMode}).",
        GatewayPublishInstallSteps.Install => "installed.",
        GatewayPublishInstallSteps.Upgrade => $"upgraded from {from}.",
        GatewayPublishInstallSteps.Unpublish => $"unpublished version {step.Version}.",
        _ => "done.",
    };

    private static string Failed(PlannedStep step) => step.Step switch
    {
        GatewayPublishInstallSteps.Signature => "the signature check failed.",
        GatewayPublishInstallSteps.Publish => "publishing failed.",
        GatewayPublishInstallSteps.Sync => "synchronising failed.",
        GatewayPublishInstallSteps.Install => "installing failed.",
        GatewayPublishInstallSteps.Upgrade => "the data upgrade failed.",
        GatewayPublishInstallSteps.Unpublish => $"unpublishing version {step.Version} failed; the new version is installed.",
        _ => "a step failed.",
    };

    private static GatewayCommandOutcome Refuse(string message) => GatewayCommandOutcome.Failure(message);

    private static GatewayCommandOutcome Finish(List<AppReport> reports, string message) =>
        GatewayCommandOutcome.Failure(message, Result(reports, restarted: false));

    private static JsonObject Result(IEnumerable<AppReport> reports, bool restarted) => new GatewayPublishInstallResult
    {
        Apps = reports.Select(r => r.ToResult()).ToList(),
        Restarted = restarted,
    }.ToJson();

    /// <summary>One app's progress through the command, for its log lines and its entry in the result.</summary>
    private sealed class AppReport(AppToPublish declared)
    {
        private readonly List<GatewayPublishInstallStep> _steps = [];

        public AppToPublish Declared { get; } = declared;

        /// <summary>What the file says, once read.</summary>
        public AppFileInfo? File { get; set; }

        public string? Path { get; set; }

        public string Outcome { get; set; } = GatewayPublishInstallOutcomes.Skipped;

        public string? Message { get; set; }

        public Version? From { get; set; }

        public string Title => File is { } f ? $"{f.Name} {f.Version}" : Declared.File;

        public void Step(string step, string outcome, string? version = null, string? message = null) =>
            _steps.Add(new GatewayPublishInstallStep { Step = step, Outcome = outcome, Version = version, Message = message });

        public GatewayPublishInstallAppResult ToResult() => new()
        {
            File = Declared.File,
            Id = File?.Id.ToString("D") ?? Declared.Id ?? string.Empty,
            Name = File?.Name ?? Declared.Name ?? string.Empty,
            Publisher = File?.Publisher ?? Declared.Publisher,
            Version = File?.Version.ToString() ?? Declared.Version ?? string.Empty,
            Outcome = Outcome,
            FromVersion = From?.ToString(),
            Message = Message,
            Steps = _steps.ToList(),
        };
    }
}
