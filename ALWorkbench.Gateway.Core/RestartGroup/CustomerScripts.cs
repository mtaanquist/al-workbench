using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Policy;
using ALWorkbench.Gateway.Core.PowerShell;

namespace ALWorkbench.Gateway.Core.RestartGroup;

/// <summary>
/// Runs customer IT's before and after scripts for a database (#1368): the <c>before</c>
/// and <c>after</c> the policy file names, and nothing else. AL Workbench can neither send
/// a script nor change one, so "no script from the server" holds: the path comes from the
/// file on the gateway's computer, and the file itself from customer IT. See
/// <c>.design/on-prem-gateway.md</c>, "The restart group, as built".
///
/// <para><b>Checked right before it runs.</b> The policy source checks every named script,
/// its folder and every folder above it on its own schedule (a script anyone else can change makes the whole
/// file unsafe); a deployment may run for hours between that check and the after script,
/// so the same rule (<see cref="IFileAccessCheck"/>, judged by <see cref="AccessRules"/>)
/// is asked again here, and a script that is gone or that someone other than administrators
/// can change is not run.</para>
///
/// <para><b>How it runs.</b> In its own Windows PowerShell 5.1 process
/// (<c>powershell.exe -File</c>), started from the same runner and therefore inside the same
/// job object as every cmdlet, with a time limit. The path reaches the fixed
/// <see cref="Run"/> operation as data, is checked with <c>-LiteralPath</c> and passed to
/// <c>-File</c>, which reads it literally; the policy file refuses <c>[</c> and <c>]</c> in
/// a script path besides, so no pattern ever picks another file. Nothing is passed to the
/// script. Everything it writes, errors and warnings included, goes to the command's log.</para>
///
/// <para><b>What fails it.</b> Only an explicit <c>exit N</c> with N other than 0, an error
/// that stops the script, or the time limit. That is what <c>-File</c> reports as the exit
/// code: a script that ends normally exits 0, whatever the last program it ran returned, so
/// a trailing <c>robocopy</c> that returns 1 ("files copied") does not fail it. A script
/// that wants a program's failure to count says so with <c>exit $LASTEXITCODE</c>.</para>
///
/// <para>A failure's message is cut to <see cref="GatewayStepText.MaxMessageChars"/>
/// characters; the whole output is in the command's log.</para>
/// </summary>
public sealed class CustomerScripts
{
    /// <summary>How long one script may run before it, and everything it started, is stopped.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Runs the script at <c>$Parameters.path</c> in a child <c>powershell.exe -File</c>, so
    /// its exit code is the one <c>-File</c> gives: <c>exit N</c>'s N, 1 for an error that
    /// stopped it, otherwise 0. A non-terminating error is logged, not fatal, as from a
    /// prompt, and both output streams are folded into the log.
    /// </summary>
    public static readonly PowerShellOperation Run = new("customer-script", """
        $path = [string]$Parameters.path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('The script ' + $path + ' does not exist.') }
        $ErrorActionPreference = 'Continue'
        $exe = Join-Path $PSHOME 'powershell.exe'
        & $exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $path 2>&1 | ForEach-Object { Write-GatewayLog ([string]$_) }
        [pscustomobject]@{ exitCode = [int]$LASTEXITCODE }
        """, NeedsAdminModule: false);

    private readonly PowerShellRunner _runner;
    private readonly IFileAccessCheck _access;
    private readonly TimeSpan _timeout;

    public CustomerScripts(PowerShellRunner runner, IFileAccessCheck access, TimeSpan? timeout = null)
    {
        _runner = runner;
        _access = access;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The script for a stage, as it was not run, for a result that names it.</summary>
    public static GatewayScriptResult NotRun(string stage, string path, string reason) => new()
    {
        Stage = stage,
        Path = path,
        Outcome = GatewayScriptOutcomes.NotRun,
        Message = reason,
    };

    /// <summary>Runs the script; never throws for the script's own failure, which the result carries.</summary>
    public async Task<GatewayScriptResult> RunAsync(string stage, string path, Action<string> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var name = stage == GatewayScriptStages.Before ? "before script" : "after script";
        GatewayScriptResult Failed(string message, int? exitCode = null)
        {
            message = GatewayStepText.Cap(message)!;
            progress($"FAILED The {name} {path}: {message}");
            return new GatewayScriptResult { Stage = stage, Path = path, Outcome = GatewayScriptOutcomes.Failed, ExitCode = exitCode, Message = message };
        }

        if (!_access.Exists(path))
        {
            return Failed("it does not exist, so it was not run.");
        }
        // The script, its folder and every folder above it, up to the drive root.
        var problems = FileAccessChecks.ScriptProblems(_access, path);
        if (problems.Count > 0)
        {
            return Failed("someone other than administrators can change it, so it was not run. " + string.Join(" ", problems));
        }

        progress($"Running the {name} {path}.");
        PowerShellResult ran;
        try
        {
            ran = await _runner.RunAsync(Run, new PowerShellTarget(PowerShellEdition.WindowsPowerShell, null),
                new JsonObject { ["path"] = path }, _timeout, line => progress($"{name}: {line}"), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The result still matters: what ran before it went through.
            return Failed("it was stopped, with everything it started, because the command was stopped while it ran.");
        }
        if (ran.TimedOut)
        {
            return Failed($"it did not finish within {ServiceControlScript.Duration(_timeout)} and was stopped, with everything it started.");
        }
        if (!ran.Succeeded)
        {
            return Failed($"it stopped with an error: {ran.Error ?? "PowerShell did not say why."}");
        }
        var exitCode = ran.Output.Count == 1 && ran.Output[0] is JsonObject entry && entry["exitCode"] is JsonValue value
            && value.TryGetValue<int>(out var code) ? code : (int?)null;
        if (exitCode is null)
        {
            return Failed("the gateway could not read how it ended.");
        }
        if (exitCode != 0)
        {
            return Failed($"it ended with exit code {exitCode}.", exitCode);
        }
        progress($"The {name} {path} finished.");
        return new GatewayScriptResult { Stage = stage, Path = path, Outcome = GatewayScriptOutcomes.Succeeded, ExitCode = 0 };
    }
}
