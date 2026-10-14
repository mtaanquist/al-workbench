using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using ALWorkbench.Gateway;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core;
using ALWorkbench.Gateway.Core.Policy;
using ALWorkbench.Gateway.Core.PowerShell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.EventLog;

// The program customer IT runs. Five commands:
//   register --url <address> --code <code>   make a key, exchange the code, print the fingerprint
//   install [--account DOMAIN\name$]           create the Windows service (see ServiceInstaller)
//   uninstall                                  remove the service, the key and the stored settings
//   check                                      read gateway.toml and try each local tier, changing nothing
//   run                                        the service itself; Windows starts it with this
// See .design/on-prem-gateway.md.

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The AL Workbench Gateway runs on Windows only.");
    return 1;
}
return await GatewayProgram.MainAsync(args);

[SupportedOSPlatform("windows")]
internal static class GatewayProgram
{
    /// <summary><c>%ProgramData%\AL Workbench Gateway</c>.</summary>
    private static readonly GatewayDataFolder Folder = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ServiceInstaller.DisplayName));

    public static async Task<int> MainAsync(string[] args)
    {
        if (WindowsServiceHelpers.IsWindowsService()) return await RunAsync(args);

        Console.OutputEncoding = Encoding.UTF8;
        var command = args.FirstOrDefault()?.ToLowerInvariant();
        try
        {
            return command switch
            {
                "register" => await RegisterAsync(Options(args)),
                "install" => Install(Options(args)),
                "uninstall" => Uninstall(),
                "check" => await CheckAsync(),
                "run" => await RunAsync(args),
                _ => Usage(command),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int Usage(string? command)
    {
        if (command is not null and not ("help" or "--help" or "-h" or "/?"))
        {
            Console.Error.WriteLine($"'{command}' is not a command of the AL Workbench Gateway.");
            Console.Error.WriteLine();
        }
        Console.WriteLine("""
            AL Workbench Gateway

            Usage, from a command prompt opened with "Run as administrator":

              ALWorkbench.Gateway install [--account DOMAIN\name$]
                  Creates the Windows service "AL Workbench Gateway". It runs as its own
                  account (NT SERVICE\ALWorkbenchGateway) unless you name a group managed
                  service account.

              ALWorkbench.Gateway register --url <AL Workbench address> --code <registration code>
                  Connects this computer to AL Workbench. An Admin gets the code in
                  AL Workbench under Administration → Gateways → Add gateway, and then
                  approves the gateway there.

              ALWorkbench.Gateway check
                  Reads gateway.toml beside the program, shows what it allows for each
                  service tier, checks that only administrators can change it, and asks
                  each service tier on this computer for its state. Changes nothing.

              ALWorkbench.Gateway uninstall
                  Removes the service, the gateway's key and its settings. The log files stay.

            Log: Windows event log (Application, source "AL Workbench Gateway") and the
            daily files in %ProgramData%\AL Workbench Gateway\logs.
            """);
        return command is null or "help" or "--help" or "-h" or "/?" ? 0 : 2;
    }

    private static async Task<int> RegisterAsync(Dictionary<string, string> options)
    {
        RequireAdministrator();
        var settingsStore = new JsonFileGatewaySettingsStore(Folder.SettingsFile);
        var account = settingsStore.Load()?.ServiceAccount ?? ServiceInstaller.VirtualAccount;
        DataFolderAccess.Apply(Folder, account);

        using var logs = new RollingFileLoggerProvider(Folder.LogFolder, SystemGatewayClock.Instance);
        var logger = logs.CreateLogger("ALWorkbench.Gateway.Register");
        var keys = new CngSigningKeyStore();
        using var http = GatewayHttp.Create();
        var registration = new GatewayRegistration(http, keys, settingsStore, SystemGatewayClock.Instance, logger);
        if (settingsStore.Load() is not { ServiceAccount: not null })
        {
            // Registering before installing: the key goes to the account install will use by default.
            settingsStore.Save((settingsStore.Load() ?? new GatewaySettings()) with { ServiceAccount = account });
        }

        Console.WriteLine("Registering this computer with AL Workbench...");
        GatewaySettings settings;
        try
        {
            settings = await registration.RegisterAsync(
                Option(options, "url"), Option(options, "code"), Environment.MachineName, GatewayHttp.ProgramVersion, CancellationToken.None);
        }
        catch (GatewayRegistrationException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine($"""

            Registered with {settings.WorkbenchUrl}.
              Gateway name:     {settings.Name}
              Key fingerprint:  {settings.KeyFingerprint}
              Key kept in:      {keys.LastCreatedIn}

            An Admin must now approve this gateway in AL Workbench, under
            Administration → Gateways. Before approving, they should check that the
            fingerprint shown there is {settings.KeyFingerprint}. If it is not, do not approve it.
            """);
        if (keys.PlatformFallbackReason is not null)
        {
            Console.WriteLine($"The key is not in a TPM because the TPM could not be used: {keys.PlatformFallbackReason}");
        }

        if (ServiceInstaller.IsInstalled())
        {
            Console.WriteLine("Restarting the AL Workbench Gateway service so it uses the new key...");
            ServiceInstaller.Stop();
            ServiceInstaller.Start();
            Console.WriteLine("Done. The service is running and will take commands once the gateway is approved.");
        }
        else
        {
            Console.WriteLine("Next, run: ALWorkbench.Gateway install");
        }
        return 0;
    }

    private static int Install(Dictionary<string, string> options)
    {
        RequireAdministrator();
        var account = options.GetValueOrDefault("account")?.Trim();
        if (string.IsNullOrEmpty(account))
        {
            account = ServiceInstaller.VirtualAccount;
        }
        else if (!account.EndsWith('$'))
        {
            throw new UsageException(
                $"'{account}' is not a group managed service account (their names end in $). The gateway never stores a password, " +
                "so it runs either as its own account (leave out --account) or as a group managed service account.");
        }
        if (ServiceInstaller.IsInstalled())
        {
            Console.Error.WriteLine("The AL Workbench Gateway service is already installed. Run 'ALWorkbench.Gateway uninstall' first to install it again.");
            return 1;
        }
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot tell where this program is.");

        var settingsStore = new JsonFileGatewaySettingsStore(Folder.SettingsFile);
        var settings = (settingsStore.Load() ?? new GatewaySettings()) with { ServiceAccount = account };
        settingsStore.Save(settings);
        DataFolderAccess.Apply(Folder, account);
        if (!EventLog.SourceExists(ServiceInstaller.DisplayName))
        {
            EventLog.CreateEventSource(new EventSourceCreationData(ServiceInstaller.DisplayName, "Application"));
        }
        var keys = new CngSigningKeyStore();
        if (settings.KeyName.Length > 0 && keys.Exists(settings.KeyName))
        {
            keys.GrantAccess(settings.KeyName, account);
        }

        ServiceInstaller.Create(executable, account);
        Console.WriteLine($"Installed the service \"{ServiceInstaller.DisplayName}\", running as {account}.");
        if (settings.GatewayId != 0)
        {
            ServiceInstaller.Start();
            Console.WriteLine("Started it. It takes commands once an Admin has approved the gateway in AL Workbench.");
        }
        else
        {
            Console.WriteLine("Next, register it: ALWorkbench.Gateway register --url <AL Workbench address> --code <registration code>");
        }
        return 0;
    }

    private static int Uninstall()
    {
        RequireAdministrator();
        if (ServiceInstaller.IsInstalled())
        {
            ServiceInstaller.Delete();
            Console.WriteLine("Removed the AL Workbench Gateway service.");
        }

        var settingsStore = new JsonFileGatewaySettingsStore(Folder.SettingsFile);
        var settings = settingsStore.Load();
        var keys = new CngSigningKeyStore();
        if (settings is { KeyName.Length: > 0 })
        {
            keys.Delete(settings.KeyName);
            Console.WriteLine("Deleted the gateway's key from the Windows key store.");
        }
        settingsStore.Delete();
        foreach (var folder in new[] { Folder.StateFolder, Folder.WorkFolder })
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        if (EventLog.SourceExists(ServiceInstaller.DisplayName)) EventLog.DeleteEventSource(ServiceInstaller.DisplayName);
        Console.WriteLine($"Deleted the gateway's settings. The log files are kept in {Folder.LogFolder}; delete them when you no longer need them.");
        if (settings is { GatewayId: > 0 })
        {
            Console.WriteLine($"Ask an Admin to revoke gateway '{settings.Name}' in AL Workbench under Administration → Gateways.");
        }
        return 0;
    }

    /// <summary>
    /// What customer IT runs after editing gateway.toml: the file's mistakes, who else
    /// can change it, what each tier is allowed, and a harmless read from each tier on
    /// this computer, which proves the right PowerShell and administration module load.
    /// </summary>
    private static async Task<int> CheckAsync()
    {
        RequireAdministrator();
        var options = GatewayWorker.PolicyOptions();
        var source = new GatewayPolicySource(options, new WindowsFileAccessCheck(), SystemGatewayClock.Instance, TimeZoneInfo.Local, NullLogger.Instance);
        var report = source.Refresh().Report;

        Console.WriteLine($"Settings file: {options.PolicyFile}");
        Console.WriteLine(report.State switch
        {
            GatewayPolicyStates.Valid => "The settings are correct.",
            GatewayPolicyStates.Invalid => "The settings have mistakes. Until they are corrected, the gateway only reads from the tiers the last correct version listed:",
            GatewayPolicyStates.Missing => "There is no gateway.toml yet. Copy gateway.example.toml to gateway.toml and list your service tiers in it.",
            _ => "Someone other than administrators can change the gateway's files, so the gateway will not run:",
        });
        foreach (var problem in report.Problems) Console.WriteLine("  - " + problem);
        Console.WriteLine();

        foreach (var instance in report.Instances)
        {
            Console.WriteLine($"{instance.Machine} / {instance.Name} ({instance.Environment})");
            Console.WriteLine($"  Allowed:       {(instance.Allow.Count == 0 ? "nothing" : string.Join(", ", instance.Allow))}");
            Console.WriteLine($"  Change hours:  {instance.ChangeHours ?? "any time"}");
            Console.WriteLine($"  Tenants:       {string.Join(", ", instance.Tenants)}");
            Console.WriteLine($"  Publishers:    {(instance.Publishers is null ? "any" : string.Join(", ", instance.Publishers))}");
            Console.WriteLine($"  Unsigned apps: {(instance.AllowUnsigned ? "allowed" : "refused")}");
        }

        var failed = report.State != GatewayPolicyStates.Valid;
        var locator = new RegistryServiceTierLocator();
        var runner = new PowerShellRunner(new SystemProcessRunner(), PowerShellExecutables.Windows());
        foreach (var instance in source.InForce.Instances.Where(i =>
                     string.Equals(i.Tier.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine();
            Console.Write($"Asking {instance.Tier} for its state... ");
            var install = locator.Find(instance.Tier.Instance);
            if (install is null)
            {
                Console.WriteLine($"there is no Business Central service for {instance.Tier.Instance} on this computer.");
                failed = true;
                continue;
            }
            var target = new PowerShellTarget(PowerShellChoice.For(instance.PowerShell, install.Version), install.ServiceFolder);
            var result = await runner.RunAsync(PowerShellOperations.SelfTest, target,
                new JsonObject { [GatewayCommandParameters.Instance] = instance.Tier.Instance }, TimeSpan.FromMinutes(2), null, CancellationToken.None);
            if (result.Succeeded)
            {
                Console.WriteLine($"{result.Output.FirstOrDefault()?["state"]}, version {result.Output.FirstOrDefault()?["version"]} (PowerShell {result.Output.FirstOrDefault()?["powerShell"]}).");
            }
            else
            {
                Console.WriteLine("failed.");
                Console.WriteLine("  " + result.Error);
                failed = true;
            }
        }
        return failed ? 1 : 0;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceInstaller.ServiceName);
        builder.Services.AddSingleton(Folder);
        builder.Services.AddHostedService<GatewayWorker>();

        builder.Logging.ClearProviders();
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddProvider(new RollingFileLoggerProvider(Folder.LogFolder, SystemGatewayClock.Instance));
        if (WindowsServiceHelpers.IsWindowsService())
        {
            builder.Logging.AddEventLog(settings =>
            {
                settings.SourceName = ServiceInstaller.DisplayName;
                settings.LogName = "Application";
            });
            // The event log provider only takes warnings unless told otherwise; customer
            // IT wants every command there, which is logged as information.
            builder.Logging.AddFilter<EventLogLoggerProvider>("ALWorkbench", LogLevel.Information);
        }
        else
        {
            builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        }

        await builder.Build().RunAsync();
        return 0;
    }

    private static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UsageException("Run this from a command prompt opened with \"Run as administrator\".");
        }
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException($"'{args[i]}' is not an option. Options start with --, for example --url.");
            }
            if (i + 1 >= args.Length) throw new UsageException($"{args[i]} needs a value after it.");
            options[args[i][2..]] = args[++i];
        }
        return options;
    }

    private static string? Option(Dictionary<string, string> options, string name) => options.GetValueOrDefault(name);

    private sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }
}
