using System.Runtime.Versioning;
using System.Security.Principal;
using ALWorkbench.Gateway.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ALWorkbench.Gateway;

/// <summary>
/// The service itself: runs <see cref="GatewayRunner"/> until Windows stops the
/// service. When the runner ends on its own because the gateway is not registered,
/// the service stops cleanly, which Windows does not treat as a crash and so does not
/// restart it every minute. When it ends on an unexpected error, or the data folder's
/// access rights are not the ones the gateway set, the process exits with code 1, so
/// the service's recovery options (set by <c>install</c>) apply.
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

        if (outcome == GatewayRunOutcome.Failed) Environment.ExitCode = 1;
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
        var runner = new GatewayRunner(
            http,
            new JsonFileGatewaySettingsStore(_folder.SettingsFile),
            new CngSigningKeyStore(),
            new FileRunOnceStore(_folder.RunOnceFile, clock),
            new NotSupportedCommandExecutor(),
            clock,
            _logger,
            new GatewayRunnerOptions(_folder.WorkFolder, Environment.MachineName, GatewayHttp.ProgramVersion));
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
}
