using System.Runtime.Versioning;
using ALWorkbench.Gateway.Core;
using ALWorkbench.Gateway.Core.Policy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ALWorkbench.Gateway;

/// <summary>
/// The service itself: runs <see cref="GatewayRunner"/> until Windows stops the
/// service. When the runner ends on its own (the gateway is not registered) the
/// service stops cleanly (also when someone other than administrators can change
/// its settings files, which the policy source logs), which Windows does not treat as a
/// crash and so does not restart it every minute.
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
        var clock = SystemGatewayClock.Instance;
        using var http = GatewayHttp.Create();
        var policy = new GatewayPolicySource(PolicyOptions(), new WindowsFileAccessCheck(), clock, TimeZoneInfo.Local, _logger);
        var runner = new GatewayRunner(
            http,
            new JsonFileGatewaySettingsStore(_folder.SettingsFile),
            new CngSigningKeyStore(),
            new FileRunOnceStore(_folder.RunOnceFile, clock),
            new NotSupportedCommandExecutor(),
            policy,
            clock,
            _logger,
            new GatewayRunnerOptions(_folder.WorkFolder, Environment.MachineName, GatewayHttp.ProgramVersion));

        var outcome = await runner.RunAsync(stoppingToken).ConfigureAwait(false);
        if (outcome != GatewayRunOutcome.Stopped)
        {
            _lifetime.StopApplication();
        }
    }

    /// <summary><c>gateway.toml</c> beside the program, which customer IT owns.</summary>
    internal static GatewayPolicyOptions PolicyOptions()
    {
        var programFolder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new GatewayPolicyOptions(Path.Combine(programFolder, GatewayPolicyParser.FileName), programFolder, Environment.MachineName);
    }
}
