using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace ALWorkbench.Gateway;

/// <summary>
/// Creates and removes the Windows service with Windows' own <c>sc.exe</c>, so what
/// <c>install</c> does is what an administrator would type, and can be read back
/// with <c>sc qc ALWorkbenchGateway</c>. Every argument is ours; nothing from AL
/// Workbench reaches a command line.
///
/// <para><b>The account.</b> By default the service runs as its own virtual account,
/// <c>NT SERVICE\ALWorkbenchGateway</c>: Windows creates and manages it, it has no
/// password, it is not an administrator, and on the network it acts as the computer
/// account. <c>install --account DOMAIN\name$</c> uses a group managed service
/// account instead, for when the gateway must reach other machines as a domain
/// identity (remote service control, #1373); that account must already be installed
/// on this computer and hold "Log on as a service". The gateway never stores a
/// password, so an ordinary user account is not offered. What the account may do in
/// Business Central (the administration PowerShell module, the database) is granted
/// by customer IT and described in the setup guide (#1376).</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceInstaller
{
    public const string ServiceName = "ALWorkbenchGateway";
    public const string DisplayName = "AL Workbench Gateway";
    public const string VirtualAccount = @"NT SERVICE\" + ServiceName;

    private const string Description =
        "Connects this network's Business Central service tiers to AL Workbench. It only calls out to AL Workbench over HTTPS; nothing connects in.";

    public static bool IsInstalled() =>
        ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Creates the service, stopped, set to start automatically (delayed) with the computer.</summary>
    public static void Create(string executablePath, string account)
    {
        var args = new List<string>
        {
            "create", ServiceName,
            "binPath=", $"\"{executablePath}\" run",
            "start=", "delayed-auto",
            "DisplayName=", DisplayName,
            "obj=", account,
        };
        if (account.EndsWith('$'))
        {
            // A group managed service account: Windows supplies the password.
            args.AddRange(["password=", ""]);
        }
        Sc(args);
        Sc(["description", ServiceName, Description]);
        // The service SID is what the key and folder access lists name.
        Sc(["sidtype", ServiceName, "unrestricted"]);
        // Restart after a crash: after one minute, twice, then after five minutes; the
        // count resets after a day. A clean stop - including the gateway stopping
        // itself because it is not registered - is not a crash.
        Sc(["failure", ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/300000"]);
    }

    public static void Delete()
    {
        Stop();
        Sc(["delete", ServiceName]);
    }

    public static void Start()
    {
        using var service = new ServiceController(ServiceName);
        if (service.Status == ServiceControllerStatus.Running) return;
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
    }

    public static void Stop()
    {
        if (!IsInstalled()) return;
        using var service = new ServiceController(ServiceName);
        if (service.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
        {
            if (service.Status == ServiceControllerStatus.StopPending) service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            return;
        }
        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
    }

    public static bool IsRunning()
    {
        if (!IsInstalled()) return false;
        using var service = new ServiceController(ServiceName);
        return service.Status == ServiceControllerStatus.Running;
    }

    private static void Sc(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start sc.exe.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"sc.exe {arguments[0]} failed ({process.ExitCode}): {output.Trim()}");
        }
    }
}
