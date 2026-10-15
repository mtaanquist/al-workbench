using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Policy;
using ALWorkbench.Gateway.Core.PowerShell;
using ALWorkbench.Gateway.Core.RestartGroup;

namespace ALWorkbench.Gateway.Core.PublishInstall;

/// <summary>
/// The <c>publish-install</c> verb (#1375): publishes, syncs and installs or upgrades a
/// build's apps on one tenant of the database's publishing tier, in the order the command
/// lists them (the build's dependency order), and unpublishes previous versions when
/// asked. See <c>.design/on-prem-gateway.md</c>, "Deploying to on-premises".
///
/// <para><b>Nothing changes before every file is checked.</b> <c>GatewayRunner</c> has
/// already checked the policy file and downloaded the files with their SHA-256. This
/// verb checks the policy again (the file may have changed during a long download), then,
/// for every app before any cmdlet that changes anything runs: its SHA-256 again, that
/// the file is the app the command declares (id, version, publisher and, when sent, name,
/// read from the <c>.app</c>'s own manifest by <see cref="AppFile"/>), that the file's
/// publisher is on the tier's list, and, where the tier does not take unsigned apps, that
/// the file carries a signature whatever the command claimed. Then, in one read-only
/// PowerShell run (<see cref="PublishInstallScript.Examine"/>), that Business Central reads
/// the same app from the file (<c>Get-NAVAppInfo -Path</c>), and what Windows says about
/// its signature and signer. One refusal refuses the command with nothing changed.</para>
///
/// <para><b>Then app by app.</b> <see cref="PublishInstallScript.Inspect"/> reads what the
/// tenant has, <see cref="AppInstallPlanner"/> decides, and
/// <see cref="PublishInstallScript.Apply"/> runs the steps. Each step is a line in the
/// command's log. The first failure stops the command; the apps after it are reported as
/// skipped. The result (<see cref="GatewayPublishInstallResult"/>) lists every app either
/// way.</para>
///
/// <para><b>Around the apps (#1368).</b> Customer IT's before script runs once every file
/// is checked and before anything is published; if it fails, nothing is. Only after every
/// app went through does the database's restart group run (<see cref="RestartGroupRunner"/>),
/// then the after script. A failed install never restarts anything, and an install that
/// changed nothing restarts nothing either.</para>
///
/// <para><b>Signatures.</b> Where the tier does not allow unsigned apps, or the file is
/// signed, the gateway asks Windows (<c>Get-AuthenticodeSignature</c>, which reads an
/// <c>.app</c>'s signature through the component Business Central installs for it) and
/// records the signer's subject and thumbprint. Where the tier does not allow unsigned apps
/// it refuses anything but a valid signature, then publishes without
/// <c>-SkipVerification</c>, so Business Central checks it as well. Where the tier lists
/// <c>signers</c>, a signature from anyone else is refused. Off Windows the check cannot
/// run and fails closed; the tests stand it in.</para>
/// </summary>
public sealed class PublishInstallVerb : ICommandExecutor
{
    private readonly IGatewayPolicy _policy;
    private readonly PowerShellRunner _runner;
    private readonly IServiceTierLocator _locator;
    private readonly string _machineName;
    private readonly RestartGroupRunner _restarts;
    private readonly CustomerScripts _scripts;

    public PublishInstallVerb(IGatewayPolicy policy, PowerShellRunner runner, IServiceTierLocator locator, string machineName,
        RestartGroupRunner restarts, CustomerScripts scripts)
    {
        _policy = policy;
        _runner = runner;
        _locator = locator;
        _machineName = machineName;
        _restarts = restarts;
        _scripts = scripts;
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

        // Read and checked by PolicyRequest; checked here against the tier in force too,
        // so ForceSync never reaches Sync-NAVApp on a tier that does not allow it.
        var syncMode = request.SyncMode;
        if (PolicyEnforcer.ForceSync(tier, syncMode) is { } forceSync)
        {
            return Refuse(forceSync.Reason + " Nothing was changed.");
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
            return RefuseApp(reports, report, GatewayPublishInstallSteps.Verify, refusal, context.Progress);
        }
        var examined = await ExamineAsync(reports, tier, target, context.Progress, ct).ConfigureAwait(false);
        if (examined is { } refused)
        {
            if (refused.Report is null)
            {
                foreach (var report in reports)
                {
                    report.Outcome = GatewayPublishInstallOutcomes.Refused;
                    report.Message = refused.Message;
                    report.Step(GatewayPublishInstallSteps.Verify, GatewayPublishInstallStepOutcomes.Failed, message: refused.Message);
                }
                context.Progress(refused.Message);
                return Finish(reports, refused.Message + " Nothing was changed.");
            }
            return RefuseApp(reports, refused.Report, refused.Step, refused.Message, context.Progress);
        }
        foreach (var report in reports)
        {
            report.Step(GatewayPublishInstallSteps.Verify, GatewayPublishInstallStepOutcomes.Done);
            context.Progress($"{report.Title}: verified the file. It is the app declared, from {report.File!.Publisher}, Business Central reads it the same way, and its SHA-256 matches.");
            if (report.Signature is { } signature)
            {
                report.Step(GatewayPublishInstallSteps.Signature, signature.Valid ? GatewayPublishInstallStepOutcomes.Done : GatewayPublishInstallStepOutcomes.Skipped,
                    message: signature.Valid ? null : $"The signature does not verify ({signature.Status}); the tier takes unsigned apps, so it is published as one.",
                    signature: signature);
                context.Progress($"{report.Title}: {SignedBy(signature)}");
            }
        }

        // Customer IT's before script, once every file is checked and before anything is
        // published (#1368). A failure stops the deployment here.
        var database = _policy.InForce.DatabaseOf(tier.Tier);
        var scripts = new List<GatewayScriptResult>();
        if (database?.Before is { } before)
        {
            var ran = await _scripts.RunAsync(GatewayScriptStages.Before, before, context.Progress, ct).ConfigureAwait(false);
            scripts.Add(ran);
            if (ran.Outcome != GatewayScriptOutcomes.Succeeded)
            {
                foreach (var report in reports)
                {
                    report.Outcome = GatewayPublishInstallOutcomes.Skipped;
                    report.Message = "Not started, because the before script failed.";
                }
                if (database.After is { } notAfter) scripts.Add(CustomerScripts.NotRun(GatewayScriptStages.After, notAfter, "Not run, because the before script failed."));
                return Finish(reports, $"The before script {before} failed: {ran.Message} No app was published.", scripts);
            }
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
            if (database?.After is { } skippedAfter) scripts.Add(CustomerScripts.NotRun(GatewayScriptStages.After, skippedAfter, "Not run, because an app did not install."));
            return Finish(reports, failure + after, scripts);
        }

        return await RestartGroupAsync(reports, tier, database, scripts, context.Progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs after every app went through, and only then (#1368): the restart group of the
    /// database, then the after script customer IT names for it. When nothing was installed
    /// (every app was already on its version) nothing is restarted, since that would sign
    /// everyone out for nothing. The settings are read again first: the apps may have taken
    /// hours, and a tier that no longer allows <c>restart</c> is not restarted. A tier that
    /// does not come back, or an after script that fails, fails the command with every app
    /// still reported as installed.
    /// </summary>
    private async Task<GatewayCommandOutcome> RestartGroupAsync(List<AppReport> reports, EffectiveInstance tier, PolicyDatabase? database,
        List<GatewayScriptResult> scripts, Action<string> progress, CancellationToken ct)
    {
        const string NotRestarted = "Not run, because the service tiers were not restarted.";
        var changed = reports.Any(r => r.Outcome is GatewayPublishInstallOutcomes.Installed or GatewayPublishInstallOutcomes.Upgraded);
        if (!changed)
        {
            var nothing = RestartGroupRunner.NotRun(_policy.InForce, tier.Tier, "Every app was already installed, so nothing changed and the service tiers were not restarted.");
            progress(nothing.Message!);
            if (database?.After is { } notAfter) scripts.Add(CustomerScripts.NotRun(GatewayScriptStages.After, notAfter, NotRestarted));
            return GatewayCommandOutcome.Success(Result(reports, nothing, scripts));
        }

        progress("All apps are installed. Restarting the service tiers, so nobody keeps running the previous version.");
        var snapshot = _policy.Refresh();
        var policy = _policy.InForce;
        string? refusal = null;
        if (snapshot.Report.State != GatewayPolicyStates.Valid)
        {
            refusal = "the customer's gateway settings changed while the apps were installed and now only allow reading, so the service tiers were not restarted.";
        }
        else if (RestartGroupRunner.GroupOf(policy, tier.Tier).Tiers.FirstOrDefault(t => policy.Find(t)?.Allows(PolicyPermission.Restart) != true) is { } notAllowed)
        {
            refusal = $"the customer's gateway settings no longer allow 'restart' on {notAllowed}, so the service tiers were not restarted.";
        }
        if (refusal is not null)
        {
            progress("FAILED Not restarted: " + refusal);
            var notRun = RestartGroupRunner.NotRun(policy, tier.Tier, char.ToUpperInvariant(refusal[0]) + refusal[1..]);
            if (database?.After is { } notAfter) scripts.Add(CustomerScripts.NotRun(GatewayScriptStages.After, notAfter, NotRestarted));
            return GatewayCommandOutcome.Failure($"All apps were installed, but {refusal}", Result(reports, notRun, scripts));
        }

        var restart = await _restarts.RunAsync(policy, tier.Tier, progress, ct).ConfigureAwait(false);
        if (!restart.Restarted)
        {
            var failed = restart.Tiers.FirstOrDefault(t => t.Outcome == GatewayRestartTierOutcomes.Failed);
            if (database?.After is { } notAfter) scripts.Add(CustomerScripts.NotRun(GatewayScriptStages.After, notAfter, "Not run, because the service tiers were not all restarted."));
            return GatewayCommandOutcome.Failure(
                $"All apps were installed, but the service tiers were not all restarted: {failed?.Message ?? restart.Message ?? "the gateway did not say why."}",
                Result(reports, restart, scripts));
        }

        if (database?.After is { } after)
        {
            var ran = await _scripts.RunAsync(GatewayScriptStages.After, after, progress, ct).ConfigureAwait(false);
            scripts.Add(ran);
            if (ran.Outcome != GatewayScriptOutcomes.Succeeded)
            {
                return GatewayCommandOutcome.Failure(
                    $"All apps were installed and the service tiers restarted, but the after script {after} failed: {ran.Message}",
                    Result(reports, restart, scripts));
            }
        }
        return GatewayCommandOutcome.Success(Result(reports, restart, scripts));
    }

    private static GatewayCommandOutcome RefuseApp(List<AppReport> reports, AppReport report, string step, string refusal, Action<string> progress)
    {
        report.Outcome = GatewayPublishInstallOutcomes.Refused;
        report.Message = refusal;
        report.Step(step, GatewayPublishInstallStepOutcomes.Failed, message: refusal, signature: step == GatewayPublishInstallSteps.Signature ? report.Signature : null);
        progress($"{report.Title}: refused. {refusal}");
        foreach (var other in reports.Where(r => r != report))
        {
            other.Outcome = GatewayPublishInstallOutcomes.Skipped;
            other.Message = "Not started, because another app in the deployment was refused.";
        }
        return Finish(reports, $"{report.Title} was refused: {refusal} Nothing was changed.");
    }

    private static string SignedBy(AppSignature signature) =>
        $"signed by {signature.Subject ?? "an unnamed certificate"} (thumbprint {signature.Thumbprint ?? "unknown"})"
        + (signature.Valid ? "; the signature verifies on this computer." : $"; the signature does not verify ({signature.Status}).");

    /// <summary>Whether Windows is asked about a file's signature: always when the tier requires one, and whenever the file carries one, to record who signed it.</summary>
    private static bool AsksSignature(AppFileInfo file, EffectiveInstance tier) => file.HasSignature || !tier.AllowUnsigned;

    /// <summary>Whether the signature decides anything: required by the tier, or checked against its <c>signers</c>.</summary>
    private static bool SignatureCounts(AppFileInfo file, EffectiveInstance tier) => !tier.AllowUnsigned || (file.HasSignature && tier.Signers is not null);

    /// <summary>
    /// Every file read by Business Central and Windows in one read-only run, before anything
    /// changes. Null when every app may go ahead; otherwise the app refused (null for all of
    /// them), the step to blame and why.
    /// </summary>
    private async Task<(AppReport? Report, string Step, string Message)?> ExamineAsync(List<AppReport> reports, EffectiveInstance tier,
        PowerShellTarget target, Action<string> progress, CancellationToken ct)
    {
        var asks = reports.Select(r => (Path: r.Path!, Signature: AsksSignature(r.File!, tier))).ToList();
        var ran = await _runner.RunAsync(PublishInstallScript.Examine, target, PublishInstallScript.ExamineParameters(asks),
            PublishInstallScript.ExamineTimeout, progress, ct).ConfigureAwait(false);
        IReadOnlyDictionary<string, AppExamination> examined;
        try
        {
            if (!ran.Succeeded) throw new FormatException(ran.Error ?? "PowerShell failed without saying why.");
            examined = AppExamination.FromScriptOutput(ran.Output, asks.Select(a => a.Path));
        }
        catch (FormatException ex)
        {
            return (null, GatewayPublishInstallSteps.Verify, $"The gateway could not check the files the way Business Central reads them: {ex.Message}");
        }

        foreach (var report in reports)
        {
            var file = report.File!;
            var name = report.Declared.File;
            var seen = examined[report.Path!];
            if (seen.AppError is { } error || seen.App is null)
            {
                return (report, GatewayPublishInstallSteps.Verify,
                    $"Business Central could not read '{name}' ({seen.AppError ?? "it returned nothing"}), so the gateway cannot be sure it installs the app it checked.");
            }
            var differences = new List<string>();
            if (seen.App.Id != file.Id) differences.Add($"app id {seen.App.Id}, not {file.Id}");
            if (seen.App.Version != file.Version) differences.Add($"version {seen.App.Version}, not {file.Version}");
            if (!string.Equals(seen.App.Publisher.Trim(), file.Publisher.Trim(), StringComparison.OrdinalIgnoreCase)) differences.Add($"publisher '{seen.App.Publisher}', not '{file.Publisher}'");
            if (!string.Equals(seen.App.Name.Trim(), file.Name.Trim(), StringComparison.OrdinalIgnoreCase)) differences.Add($"name '{seen.App.Name}', not '{file.Name}'");
            if (seen.App.PackageId != Guid.Empty && seen.App.PackageId != file.PackageId) differences.Add($"package id {seen.App.PackageId}, not {file.PackageId}");
            if (differences.Count > 0)
            {
                return (report, GatewayPublishInstallSteps.Verify,
                    $"Business Central reads '{name}' as a different app than its manifest says: {string.Join("; ", differences)}. The file may have been built to look like another app.");
            }

            if (!AsksSignature(file, tier)) continue;
            report.Signature = seen.Signature;
            if (!SignatureCounts(file, tier)) continue;
            if (seen.Signature is not { } signature)
            {
                return (report, GatewayPublishInstallSteps.Signature,
                    $"The signature of '{name}' could not be checked on this computer ({seen.SignatureError ?? "Windows did not say why"}).");
            }
            if (!signature.Valid)
            {
                return (report, GatewayPublishInstallSteps.Signature,
                    $"The signature of '{name}' does not verify on this computer ({signature.Status}). {signature.StatusMessage}".TrimEnd());
            }
            if (!tier.AllowsSigner(signature.Subject, signature.Thumbprint))
            {
                return (report, GatewayPublishInstallSteps.Signature,
                    $"'{name}' is signed by {signature.Subject ?? "an unnamed certificate"} (thumbprint {signature.Thumbprint ?? "unknown"}), who is not on {tier.Tier}'s list of signers in the customer's gateway settings.");
            }
        }
        return null;
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

        var plan = AppInstallPlanner.Plan(inspection, app.Version, app.PackageId, tier, request.Tenant, syncMode, request.UnpublishPrevious);
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
        GatewayPublishInstallSteps.Publish => "publishing failed.",
        GatewayPublishInstallSteps.Sync => "synchronising failed.",
        GatewayPublishInstallSteps.Install => "installing failed.",
        GatewayPublishInstallSteps.Upgrade => "the data upgrade failed.",
        GatewayPublishInstallSteps.Unpublish => $"unpublishing version {step.Version} failed; the new version is installed.",
        _ => "a step failed.",
    };

    private static GatewayCommandOutcome Refuse(string message) => GatewayCommandOutcome.Failure(message);

    private static GatewayCommandOutcome Finish(List<AppReport> reports, string message, IReadOnlyList<GatewayScriptResult>? scripts = null) =>
        GatewayCommandOutcome.Failure(message, Result(reports, restart: null, scripts ?? []));

    private static JsonObject Result(IEnumerable<AppReport> reports, GatewayRestartGroupResult? restart, IReadOnlyList<GatewayScriptResult> scripts) =>
        new GatewayPublishInstallResult
        {
            Apps = reports.Select(r => r.ToResult()).ToList(),
            Restarted = restart?.Restarted == true,
            Restart = restart,
            Scripts = scripts,
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

        /// <summary>What Windows said about the file's signature, when it was asked.</summary>
        public AppSignature? Signature { get; set; }

        public string Title => File is { } f ? $"{f.Name} {f.Version}" : Declared.File;

        public void Step(string step, string outcome, string? version = null, string? message = null, AppSignature? signature = null) =>
            _steps.Add(new GatewayPublishInstallStep
            {
                Step = step,
                Outcome = outcome,
                Version = version,
                Message = message,
                SignerSubject = signature?.Subject,
                SignerThumbprint = signature?.Thumbprint,
            });

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
            SignerSubject = Signature?.Subject,
            SignerThumbprint = Signature?.Thumbprint,
            Steps = _steps.ToList(),
        };
    }
}
