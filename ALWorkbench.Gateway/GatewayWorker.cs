using System.Runtime.Versioning;
using System.Security.Principal;
using ALWorkbench.Gateway.Core;
using ALWorkbench.Gateway.Core.Inventory;
using ALWorkbench.Gateway.Core.Licence;
using ALWorkbench.Gateway.Core.Policy;
using ALWorkbench.Gateway.Core.PowerShell;
using ALWorkbench.Gateway.Core.PublishInstall;
using ALWorkbench.Gateway.Core.RestartGroup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ALWorkbench.Gateway;

/// <summary>
/// The service itself: runs <see cref="GatewayRunner"/> until Windows stops the
/// service. When the runner ends on its own because the gateway is not registered,
/// the service stops cleanly, which Windows does not treat as a crash and so does not
/// restart it every minute. When it ends on an unexpected error, when the data folder's
/// access rights are not the ones the gateway set (event 2008), or when someone other
/// than administrators can change the program folder, gateway.toml or a script it
/// names (event 3003), the process exits with code 1, so the service's recovery
/// options (set by <c>install</c>) restart it and it starts once the rights are fixed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GatewayWorker : BackgroundService
{
    private readonly GatewayDataFolder _folder;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<GatewayWorker> _logger;

    public GatewayWorker(GatewayDataFolder folder, IHostApplicationLifetime lifetime, ILogger<GatewayWorker> logger)
    {
        _folder = folder;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        GatewayRunOutcome outcome;
        try
        {
            outcome = await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            _logger.Failed(ex, ex.Message);
            outcome = GatewayRunOutcome.Failed;
        }

        if (outcome is GatewayRunOutcome.Failed or GatewayRunOutcome.PolicyUnsafe) Environment.ExitCode = 1;
        if (outcome != GatewayRunOutcome.Stopped) _lifetime.StopApplication();
    }

    private async Task<GatewayRunOutcome> RunAsync(CancellationToken stoppingToken)
    {
        var problems = DataFolderAccess.Verify(_folder, ServiceAccounts());
        if (problems.Count > 0)
        {
            _logger.FolderLoosened(_folder.Root, string.Join(" ", problems));
            return GatewayRunOutcome.Failed;
        }

        var clock = SystemGatewayClock.Instance;
        using var http = GatewayHttp.Create();
        var policy = new GatewayPolicySource(PolicyOptions(), new WindowsFileAccessCheck(), clock, TimeZoneInfo.Local, _logger);
        var fileAccess = new WindowsFileAccessCheck();
        var powerShell = new PowerShellRunner(new SystemProcessRunner(new JobObjectContainment()), PowerShellExecutables.Windows(), fileAccess);
        var locator = new RegistryServiceTierLocator();
        var runOnce = new FileRunOnceStore(_folder.RunOnceFile, clock);
        // The run-once store keeps the tiers a restart group has stopped until it has tried
        // to start them, so a stop or crash in between is put right on the next start.
        var restarts = new RestartGroupRunner(powerShell, Environment.MachineName, ledger: runOnce);
        var scripts = new CustomerScripts(powerShell, fileAccess);
        var verbs = new VerbExecutors(new Dictionary<ALWorkbench.Gateway.Contracts.GatewayVerb, ICommandExecutor>
        {
            [ALWorkbench.Gateway.Contracts.GatewayVerb.PublishInstall] = new PublishInstallVerb(policy, powerShell, locator, Environment.MachineName, restarts, scripts),
            [ALWorkbench.Gateway.Contracts.GatewayVerb.RestartGroup] = new RestartGroupVerb(policy, restarts),
            [ALWorkbench.Gateway.Contracts.GatewayVerb.ImportLicence] = new ImportLicenceVerb(policy, powerShell, locator, Environment.MachineName, restarts),
        });
        var runner = new GatewayRunner(
            http,
            new JsonFileGatewaySettingsStore(_folder.SettingsFile),
            new CngSigningKeyStore(),
            runOnce,
            verbs,
            policy,
            clock,
            _logger,
            new GatewayRunnerOptions(_folder.WorkFolder, Environment.MachineName, GatewayHttp.ProgramVersion),
            inventory: new GatewayInventory(new PowerShellInventoryReader(powerShell, locator, Environment.MachineName)),
            recovery: new StoppedTierRecovery(restarts, runOnce, _logger));
        return await runner.RunAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The accounts that may change the state, work and log folders: the service
    /// account <c>install</c> recorded, and the account this process runs as (the same
    /// one under the service; an administrator when run from a command prompt).
    /// </summary>
    private List<SecurityIdentifier> ServiceAccounts()
    {
        var accounts = new List<SecurityIdentifier>();
        if (WindowsIdentity.GetCurrent().User is { } self) accounts.Add(self);
        var recorded = new JsonFileGatewaySettingsStore(_folder.SettingsFile).Load()?.ServiceAccount ?? ServiceInstaller.VirtualAccount;
        try
        {
            accounts.Add(new SecurityIdentifier(CngSigningKeyStore.AccountSid(recorded)));
        }
        catch (SystemException ex)
        {
            _logger.LogWarning("The service account {Account} in the gateway's settings is not known to Windows: {Reason}", recorded, ex.Message);
        }
        return accounts;
    }

    /// <summary><c>gateway.toml</c> beside the program, which customer IT owns.</summary>
    internal static GatewayPolicyOptions PolicyOptions()
    {
        var programFolder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new GatewayPolicyOptions(Path.Combine(programFolder, GatewayPolicyParser.FileName), programFolder, Environment.MachineName);
    }
}
