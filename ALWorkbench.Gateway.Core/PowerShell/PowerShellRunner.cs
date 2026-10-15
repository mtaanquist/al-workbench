using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Core.Policy;

namespace ALWorkbench.Gateway.Core.PowerShell;

/// <summary>
/// One fixed piece of PowerShell the gateway can run. <see cref="Script"/> is part of
/// the gateway program, written by us and compiled in; it is never built from
/// anything a command carries. It reads its input from <c>$Parameters</c>, the
/// command's parameters as an object, and whatever it outputs is the result.
/// <c>Write-GatewayLog 'text'</c> adds a line to the command's log.
/// </summary>
/// <param name="Name">For logs, e.g. <c>self-test</c>.</param>
/// <param name="Script">The script body. A constant in the gateway's code.</param>
/// <param name="NeedsAdminModule">Whether the tier's Business Central administration module is loaded first.</param>
public sealed record PowerShellOperation(string Name, string Script, bool NeedsAdminModule = true);

/// <summary>Where and how to run an operation for one service tier.</summary>
/// <param name="Edition">Windows PowerShell 5.1 or PowerShell 7.</param>
/// <param name="ServiceFolder">The tier's own install folder (where its server program is), which its administration module is loaded from.</param>
public sealed record PowerShellTarget(PowerShellEdition Edition, string? ServiceFolder);

/// <summary>How an operation ended.</summary>
/// <param name="Succeeded">True when the script ran to its end and returned a result.</param>
/// <param name="Output">What the script output, as a JSON array (one entry per object it output).</param>
/// <param name="Error">Why it failed, as PowerShell said it; null on success.</param>
/// <param name="Log">The lines the script wrote besides its result.</param>
/// <param name="TimedOut">True when it ran past its timeout and was killed.</param>
public sealed record PowerShellResult(bool Succeeded, JsonArray Output, string? Error, IReadOnlyList<string> Log, bool TimedOut);

/// <summary>Where the two PowerShells are installed.</summary>
public sealed record PowerShellExecutables(string WindowsPowerShell, string PowerShell7)
{
    /// <summary>The standard places on Windows.</summary>
    public static PowerShellExecutables Windows()
    {
        var system = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new PowerShellExecutables(
            Path.Combine(system, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
            Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
    }
}

/// <summary>
/// Picks the PowerShell a tier's cmdlets run in. Customer IT's choice in the policy
/// file (<c>powershell = "5.1"</c> or <c>"7"</c>) wins. Otherwise the tier's version
/// decides: from <see cref="FirstMajorVersionForPowerShell7"/> the administration
/// module is taken to need modern .NET, which loads in PowerShell 7 and not in
/// Windows PowerShell; older modules load in Windows PowerShell 5.1. With no version
/// known, 5.1, as the design says ("uses 5.1 when unsure").
/// </summary>
public static class PowerShellChoice
{
    /// <summary>
    /// The first Business Central major version whose administration module needs
    /// PowerShell 7. To be confirmed against real servers with the inventory verb
    /// (#1365); customer IT can override it per tier in the meantime.
    /// </summary>
    public const int FirstMajorVersionForPowerShell7 = 24;

    public static PowerShellEdition For(PowerShellEdition? configured, Version? serverVersion) =>
        configured
        ?? (serverVersion is { Major: >= FirstMajorVersionForPowerShell7 } ? PowerShellEdition.PowerShell7 : PowerShellEdition.WindowsPowerShell);
}

/// <summary>
/// Runs a <see cref="PowerShellOperation"/> in a child PowerShell process, one process
/// per operation. See <c>.design/on-prem-gateway.md</c>, "Running the cmdlets".
///
/// <para><b>Nothing a command carries becomes script.</b> The child is started with
/// fixed arguments and an encoded command that is <see cref="Bootstrap"/> alone, so
/// the command line is the same for every operation and whatever the parameters are.
/// The operation's own constant script, the parameters, the module folder and the
/// result marker travel on standard input, as base64 of UTF-8 JSON (so no console code
/// page can mangle them). The bootstrap turns the operation's script, and only that,
/// into a script block; the parameters reach it as one object read with
/// <c>ConvertFrom-Json</c>, so a quote, a <c>;</c>, a <c>$( )</c> or a backtick in a
/// value stays a character in a string. The script used to be part of the encoded
/// command, which capped an operation at about 11,000 characters (Windows' 32,767 for
/// a whole command line, after UTF-16 and base64); the inventory script is longer.
/// Standard input is a pipe between the gateway and its own child, so moving the
/// constant there gives nobody a new way in.</para>
///
/// <para><b>The administration module</b> comes from the tier's own install folder, so
/// one gateway can manage tiers of several Business Central versions on one machine.
/// In that folder the script dot-sources <c>NavAdminTool.ps1</c>, which is what
/// Microsoft's "Business Central Administration Shell" shortcut runs and which loads
/// both the server and the apps management modules. Without it, it imports whichever
/// of <c>Microsoft.Dynamics.Nav.Management</c>, <c>Microsoft.Dynamics.Nav.Apps.Management</c>,
/// <c>Microsoft.BusinessCentral.Management</c> and <c>Microsoft.BusinessCentral.Apps.Management</c>
/// it finds there (<c>.psd1</c>, then <c>.psm1</c>, then <c>.dll</c>), and fails when it
/// finds none. The folder itself comes from the tier's Windows service
/// (<see cref="ServiceTierInstall"/>). Whoever can change a file in that folder gets
/// code running in the gateway's PowerShell, so before loading it the runner checks
/// the folder and everything in it as it checks the gateway's own files, and refuses
/// to run against a tier where someone other than administrators can change them.</para>
///
/// <para><b>The result</b> comes back on standard output between two lines holding a
/// marker that is random for each run, as base64 of UTF-8 JSON; everything else the
/// child writes there is log. A failure is a non-zero exit code with PowerShell's
/// error on standard error.</para>
/// </summary>
public sealed class PowerShellRunner
{
    /// <summary>The longest command line Windows accepts is 32,767 characters; this leaves room for the program path.</summary>
    public const int MaxCommandLineChars = 30_000;

    /// <summary>
    /// The fixed script every operation runs inside, and the whole of the encoded
    /// command. It runs the envelope's <c>operation</c>, the operation's constant
    /// script, with the envelope's <c>parameters</c> as <c>$Parameters</c>.
    /// Works in Windows PowerShell 5.1 and PowerShell 7.
    /// </summary>
    public const string Bootstrap = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $ConfirmPreference = 'None'
        try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }
        $__raw = [Console]::In.ReadToEnd()
        $__envelope = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($__raw.Trim())) | ConvertFrom-Json
        $__marker = [string]$__envelope.marker
        function Write-GatewayLog([string]$Line) { [Console]::Out.WriteLine($Line) }
        try {
            if ($null -ne $__envelope.moduleFolder) {
                $__folder = [string]$__envelope.moduleFolder
                $__tool = Join-Path $__folder 'NavAdminTool.ps1'
                if (Test-Path -LiteralPath $__tool -PathType Leaf) {
                    . $__tool | Out-Null
                } else {
                    $__found = $false
                    foreach ($__name in @('Microsoft.Dynamics.Nav.Management', 'Microsoft.Dynamics.Nav.Apps.Management', 'Microsoft.BusinessCentral.Management', 'Microsoft.BusinessCentral.Apps.Management')) {
                        foreach ($__extension in @('.psd1', '.psm1', '.dll')) {
                            $__candidate = Join-Path $__folder ($__name + $__extension)
                            if (Test-Path -LiteralPath $__candidate -PathType Leaf) {
                                Import-Module -Name $__candidate -DisableNameChecking -Force | Out-Null
                                $__found = $true
                                break
                            }
                        }
                    }
                    if (-not $__found) { throw ('No Business Central administration module was found in ' + $__folder + '.') }
                }
            }
            $__operation = [scriptblock]::Create("param(`$Parameters)`n" + [string]$__envelope.operation)
            $__result = @(& $__operation $__envelope.parameters)
            $__json = ConvertTo-Json -InputObject $__result -Depth 8 -Compress
            [Console]::Out.WriteLine($__marker)
            [Console]::Out.WriteLine([Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($__json)))
            [Console]::Out.WriteLine($__marker)
            [Console]::Out.Flush()
            exit 0
        } catch {
            [Console]::Error.WriteLine(($_ | Out-String).Trim())
            exit 1
        }
        """;

    private readonly IProcessRunner _processes;
    private readonly PowerShellExecutables _executables;
    private readonly IFileAccessCheck _access;

    public PowerShellRunner(IProcessRunner processes, PowerShellExecutables executables, IFileAccessCheck access)
    {
        _processes = processes;
        _executables = executables;
        _access = access;
    }

    /// <summary>
    /// Who other than administrators can change the administration module the
    /// operation would load for <paramref name="target"/>: its folder and every file in
    /// it. Empty when it is safe to load, or when the operation loads no module.
    /// </summary>
    public IReadOnlyList<string> ModuleProblems(PowerShellOperation operation, PowerShellTarget target)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(target);
        if (!operation.NeedsAdminModule || string.IsNullOrWhiteSpace(target.ServiceFolder)) return [];
        return FileAccessChecks.FolderAndContents(_access, target.ServiceFolder);
    }

    /// <summary>
    /// What would be started for this operation. Public so tests can check that the
    /// command line never carries a parameter value and the values arrive intact on
    /// standard input.
    /// </summary>
    public ProcessSpec BuildSpec(PowerShellOperation operation, PowerShellTarget target, JsonObject parameters, TimeSpan timeout, string marker)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(parameters);
        if (operation.NeedsAdminModule && string.IsNullOrWhiteSpace(target.ServiceFolder))
        {
            throw new ArgumentException("The operation needs the tier's administration module, and the tier's install folder is not known.", nameof(target));
        }

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Bootstrap));
        string[] arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded];
        var program = target.Edition == PowerShellEdition.PowerShell7 ? _executables.PowerShell7 : _executables.WindowsPowerShell;
        if (program.Length + arguments.Sum(a => a.Length + 3) > MaxCommandLineChars)
        {
            throw new InvalidOperationException("The PowerShell bootstrap is too long for a Windows command line.");
        }

        var envelope = new JsonObject
        {
            ["marker"] = marker,
            ["operation"] = operation.Script,
            ["moduleFolder"] = operation.NeedsAdminModule ? target.ServiceFolder : null,
            ["parameters"] = parameters.DeepClone(),
        };
        var input = Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope.ToJsonString()));
        return new ProcessSpec(program, arguments, input, timeout);
    }

    /// <summary>
    /// Runs the operation. <paramref name="progress"/> gets each log line as it is
    /// written. Cancelling kills the child and everything it started.
    /// </summary>
    public async Task<PowerShellResult> RunAsync(
        PowerShellOperation operation, PowerShellTarget target, JsonObject parameters, TimeSpan timeout,
        Action<string>? progress, CancellationToken ct)
    {
        var unsafeModule = ModuleProblems(operation, target);
        if (unsafeModule.Count > 0)
        {
            return new PowerShellResult(false, [],
                "Someone other than administrators can change the Business Central administration module the gateway would load for this tier, so it does not run anything there. "
                + string.Join(" ", unsafeModule),
                [], false);
        }
        var marker = "ALWB-RESULT-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var insideResult = false;
        var spec = BuildSpec(operation, target, parameters, timeout, marker) with
        {
            OnOutputLine = line =>
            {
                if (line == marker)
                {
                    insideResult = !insideResult;
                    return;
                }
                if (!insideResult) progress?.Invoke(line);
            },
        };
        var outcome = await _processes.RunAsync(spec, ct).ConfigureAwait(false);
        return Interpret(outcome, marker);
    }

    /// <summary>Reads the child's output: the result between the markers, the rest as log.</summary>
    public static PowerShellResult Interpret(ProcessOutcome outcome, string marker)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var lines = outcome.StandardOutput.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var first = lines.IndexOf(marker);
        var last = lines.LastIndexOf(marker);
        var log = lines.Where((line, i) => (first < 0 || i < first || i > last) && line.Length > 0).ToList();
        var error = outcome.StandardError.Trim();

        if (outcome.TimedOut)
        {
            return new PowerShellResult(false, [], "PowerShell did not finish in time and was stopped.", log, true);
        }
        if (outcome.ExitCode != 0)
        {
            return new PowerShellResult(false, [], error.Length > 0 ? error : $"PowerShell ended with exit code {outcome.ExitCode}.", log, false);
        }
        if (first < 0 || last != first + 2)
        {
            return new PowerShellResult(false, [], "PowerShell ended without returning a result." + (error.Length > 0 ? " " + error : string.Empty), log, false);
        }
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(lines[first + 1]));
            var node = JsonNode.Parse(json);
            var output = node as JsonArray ?? new JsonArray(node);
            return new PowerShellResult(true, output, null, log, false);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return new PowerShellResult(false, [], "PowerShell returned a result the gateway could not read.", log, false);
        }
    }
}

/// <summary>The operations the gateway itself defines. Verbs add theirs from #1365 on.</summary>
public static class PowerShellOperations
{
    /// <summary>
    /// A harmless read that proves the whole path for one tier: the right PowerShell
    /// starts, the tier's administration module loads, and a cmdlet answers. Used by
    /// the <c>check</c> command customer IT runs.
    /// </summary>
    public static readonly PowerShellOperation SelfTest = new("self-test", """
        $instance = Get-NAVServerInstance -ServerInstance ([string]$Parameters.instance)
        [pscustomobject]@{
            serverInstance = [string]$instance.ServerInstance
            state = [string]$instance.State
            version = [string]$instance.Version
            powerShell = [string]$PSVersionTable.PSVersion
        }
        """);
}

/// <summary>Where a service tier is installed, read from its Windows service.</summary>
/// <param name="ServiceFolder">The folder of the server program, which holds its administration module.</param>
/// <param name="Version">The server program's file version, when it could be read.</param>
public sealed record ServiceTierInstall(string ServiceFolder, Version? Version)
{
    /// <summary>The Windows service name Business Central gives a server instance.</summary>
    public static string ServiceName(string instance) => "MicrosoftDynamicsNavServer$" + instance;

    /// <summary>
    /// The folder of the program in a service's image path, e.g.
    /// <c>"C:\Program Files\Microsoft Dynamics 365 Business Central\140\Service\Microsoft.Dynamics.Nav.Server.exe" $BC140</c>.
    /// Null when it cannot be read.
    /// </summary>
    public static string? FolderFromImagePath(string? imagePath) =>
        ProgramFromImagePath(imagePath) is { } program ? GatewayPolicySource.WindowsFolderOf(program) : null;

    /// <summary>The program in a service's image path: the quoted part, or up to the first <c>.exe</c> when it is not quoted.</summary>
    public static string? ProgramFromImagePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var text = imagePath.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : null;
        }
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe < 0 ? null : text[..(exe + 4)];
    }
}

/// <summary>Finds where a tier on this computer is installed. The Windows implementation reads the service's registry entry.</summary>
public interface IServiceTierLocator
{
    /// <summary>Null when this computer has no service for <paramref name="instance"/>.</summary>
    ServiceTierInstall? Find(string instance);
}
