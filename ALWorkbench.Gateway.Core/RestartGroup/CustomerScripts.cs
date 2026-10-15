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
/// <para><b>Checked right before it runs.</b> The policy source checks every named script
/// and its folder on its own schedule (a script anyone else can change makes the whole
/// file unsafe); a deployment may run for hours between that check and the after script,
/// so the same rule (<see cref="IFileAccessCheck"/>, judged by <see cref="AccessRules"/>)
/// is asked again here, and a script that is gone or that someone other than administrators
/// can change is not run.</para>
///
/// <para><b>How it runs.</b> In Windows PowerShell 5.1 (<c>powershell.exe</c>), through the
/// same runner and therefore the same job object as every cmdlet, with a time limit. The
/// path reaches the fixed <see cref="Run"/> operation as data and is called with
/// <c>&amp;</c>; nothing is passed to the script. Everything it writes, errors and warnings
/// included, goes to the command's log. It succeeds when it ends with exit code 0 and
/// throws nothing; <c>exit 1</c> (or any other code) fails it.</para>
/// </summary>
public sealed class CustomerScripts
{
    /// <summary>How long one script may run before it, and everything it started, is stopped.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Calls the script at <c>$Parameters.path</c>. Preferences are put back to PowerShell's
    /// defaults first, so a script runs as it would from a prompt (a non-terminating error
    /// is logged, not fatal), and every stream is folded into the log.
    /// </summary>
    public static readonly PowerShellOperation Run = new("customer-script", """
        $path = [string]$Parameters.path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('The script ' + $path + ' does not exist.') }
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = 0
        & $path *>&1 | ForEach-Object { Write-GatewayLog ([string]$_) }
        $code = $global:LASTEXITCODE
        if ($null -eq $code) { $code = 0 }
        [pscustomobject]@{ exitCode = [int]$code }
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
            progress($"FAILED The {name} {path}: {message}");
            return new GatewayScriptResult { Stage = stage, Path = path, Outcome = GatewayScriptOutcomes.Failed, ExitCode = exitCode, Message = message };
        }

        if (!_access.Exists(path))
        {
            return Failed("it does not exist, so it was not run.");
        }
        var problems = new List<string>();
        if (_access.Problem(path) is { } own) problems.Add(own);
        if (GatewayPolicySource.WindowsFolderOf(path) is { } folder && _access.Problem(folder) is { } inFolder) problems.Add(inFolder);
        if (problems.Count > 0)
        {
            return Failed("someone other than administrators can change it, so it was not run. " + string.Join(" ", problems));
        }

        progress($"Running the {name} {path}.");
        var ran = await _runner.RunAsync(Run, new PowerShellTarget(PowerShellEdition.WindowsPowerShell, null),
            new JsonObject { ["path"] = path }, _timeout, line => progress($"{name}: {line}"), ct).ConfigureAwait(false);
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
