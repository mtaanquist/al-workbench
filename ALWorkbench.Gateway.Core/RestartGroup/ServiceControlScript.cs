using System.Globalization;
using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Core.Policy;
using ALWorkbench.Gateway.Core.PowerShell;

namespace ALWorkbench.Gateway.Core.RestartGroup;

/// <summary>How one service control step ended, as the script reported it.</summary>
/// <param name="Before">The service's state before the step.</param>
/// <param name="State">Its state after the step, or when the wait ran out.</param>
/// <param name="TimedOut">True when it did not reach the state asked for within the time allowed.</param>
public sealed record ServiceControlReport(string? Before, string? State, bool TimedOut)
{
    /// <summary>Reads what <see cref="ServiceControlScript.Control"/> output; throws <see cref="FormatException"/> for anything else.</summary>
    public static ServiceControlReport FromScriptOutput(JsonArray output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Count != 1 || output[0] is not JsonObject entry)
        {
            throw new FormatException("The service control script did not return one result.");
        }
        static string? Text(JsonNode? node) =>
            node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String ? value.GetValue<string>() : null;
        var timedOut = entry["timedOut"] is JsonValue flag && flag.GetValueKind() == System.Text.Json.JsonValueKind.True;
        return new ServiceControlReport(Text(entry["before"]), Text(entry["state"]), timedOut);
    }
}

/// <summary>
/// The one fixed piece of PowerShell that stops or starts a service tier's Windows
/// service, on this computer or another, and waits for it (#1368). See
/// <c>.design/on-prem-gateway.md</c>, "The restart group, as built".
///
/// <para><b>Nothing from a command becomes script.</b> The service name is built by the
/// gateway from the instance the policy file lists (<c>MicrosoftDynamicsNavServer$BC140</c>),
/// and the machine and method come from the same file; they reach this script as data, as
/// every operation's parameters do. The action is one of two words, checked here and by
/// the script.</para>
///
/// <para><b>Always Windows PowerShell 5.1</b> (<c>powershell.exe</c>), whatever the tier's
/// own <c>powershell</c> says: <c>Get-Service -ComputerName</c>, which the service manager
/// method needs, was removed in PowerShell 7, and Windows PowerShell is part of every
/// Windows Server the gateway runs on. No Business Central module is loaded: this is
/// Windows' service manager, not a Business Central cmdlet.</para>
///
/// <para><b>Methods.</b> The tier on this computer is controlled directly. A
/// <c>service-manager</c> machine is reached through Windows' remote service manager
/// (<c>Get-Service -ComputerName</c>, which returns a controller bound to that machine).
/// A <c>powershell-remoting</c> machine runs the same steps there through
/// <c>Invoke-Command -ComputerName</c>, the block a constant and its values passed as
/// arguments. Either way the controller's own <c>Stop()</c> or <c>Start()</c> is called and
/// <c>WaitForStatus</c> waits with the time allowed, rather than <c>Stop-Service</c>, whose
/// own wait has no limit: a tier that hangs while stopping is reported as not stopping,
/// not left to the process timeout.</para>
/// </summary>
public static class ServiceControlScript
{
    public const string Stop = "stop";

    public const string Start = "start";

    /// <summary>A little longer than the wait the script itself is given, so the script's own timeout is what is reported.</summary>
    public static readonly TimeSpan ProcessMargin = TimeSpan.FromMinutes(2);

    public static readonly PowerShellOperation Control = new("service-control", """
        $service = [string]$Parameters.service
        $machine = [string]$Parameters.machine
        $method = [string]$Parameters.method
        $action = [string]$Parameters.action
        $seconds = [int]$Parameters.timeoutSeconds
        if ($action -ne 'stop' -and $action -ne 'start') { throw ('Unknown service action: ' + $action) }
        if ($method -ne 'local' -and $method -ne 'service-manager' -and $method -ne 'powershell-remoting') { throw ('Unknown way to reach a machine: ' + $method) }
        $control = {
            param([string]$Service, [string]$Action, [int]$Seconds, [string]$Machine)
            $ErrorActionPreference = 'Stop'
            if ($Machine) { $controller = Get-Service -ComputerName $Machine -Name $Service } else { $controller = Get-Service -Name $Service }
            $before = [string]$controller.Status
            $wanted = if ($Action -eq 'stop') { 'Stopped' } else { 'Running' }
            $timedOut = $false
            if ([string]$controller.Status -ne $wanted) {
                if ($Action -eq 'stop' -and [string]$controller.Status -ne 'StopPending') { $controller.Stop() }
                if ($Action -eq 'start' -and [string]$controller.Status -ne 'StartPending') { $controller.Start() }
                try {
                    $controller.WaitForStatus($wanted, [TimeSpan]::FromSeconds($Seconds))
                } catch {
                    # PowerShell wraps a .NET method's exception; look for the timeout inside.
                    $inner = $_.Exception
                    while ($null -ne $inner -and $inner.GetType().FullName -ne 'System.ServiceProcess.TimeoutException') { $inner = $inner.InnerException }
                    if ($null -eq $inner) { throw }
                    $timedOut = $true
                }
            }
            $controller.Refresh()
            [pscustomobject]@{ before = $before; state = [string]$controller.Status; timedOut = $timedOut }
        }
        if ($method -eq 'powershell-remoting') {
            $report = Invoke-Command -ComputerName $machine -ScriptBlock $control -ArgumentList $service, $action, $seconds, ''
        } elseif ($method -eq 'service-manager') {
            $report = & $control $service $action $seconds $machine
        } else {
            $report = & $control $service $action $seconds ''
        }
        [pscustomobject]@{ before = [string]$report.before; state = [string]$report.state; timedOut = [bool]$report.timedOut }
        """, NeedsAdminModule: false);

    /// <summary>The words for <see cref="MachineMethod"/> and this computer, as the script reads them.</summary>
    public static string MethodWord(MachineMethod? method) => method switch
    {
        null => "local",
        MachineMethod.ServiceManager => "service-manager",
        MachineMethod.PowerShellRemoting => "powershell-remoting",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Not a way to reach a machine."),
    };

    /// <summary>The parameters for one step. <paramref name="method"/> null means this computer, and then no machine is passed.</summary>
    public static JsonObject Parameters(TierRef tier, MachineMethod? method, string action, TimeSpan wait)
    {
        ArgumentNullException.ThrowIfNull(tier);
        if (action is not (Stop or Start)) throw new ArgumentOutOfRangeException(nameof(action), action, "Not a service action.");
        return new JsonObject
        {
            ["service"] = ServiceTierInstall.ServiceName(tier.Instance),
            ["machine"] = method is null ? string.Empty : tier.Machine,
            ["method"] = MethodWord(method),
            ["action"] = action,
            ["timeoutSeconds"] = (int)Math.Ceiling(wait.TotalSeconds),
        };
    }

    /// <summary>"5 minutes" or "90 seconds", for messages.</summary>
    public static string Duration(TimeSpan span) => span.TotalSeconds % 60 == 0 && span.TotalMinutes >= 1
        ? (span.TotalMinutes == 1 ? "1 minute" : string.Create(CultureInfo.InvariantCulture, $"{span.TotalMinutes:0} minutes"))
        : string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0} seconds");
}
