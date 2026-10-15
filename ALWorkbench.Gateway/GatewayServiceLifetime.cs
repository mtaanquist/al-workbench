using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ALWorkbench.Gateway;

/// <summary>
/// The Windows service lifetime, asking Windows for more time when the service is stopped.
/// Windows waits only about 20 seconds for a service to stop unless the service asks for
/// more; a stop that catches a restart group with service tiers down waits for them to
/// start again (#1368), which can take minutes, so it asks for the host's whole shutdown
/// timeout. When the computer itself shuts down Windows does not wait that long whatever
/// the service asks; the tiers still stopped are recorded and started on the next start
/// (<c>StoppedTierRecovery</c>).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GatewayServiceLifetime : WindowsServiceLifetime
{
    private readonly TimeSpan _shutdownTimeout;

    public GatewayServiceLifetime(IHostEnvironment environment, IHostApplicationLifetime applicationLifetime, ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor, IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _shutdownTimeout = optionsAccessor.Value.ShutdownTimeout;
    }

    protected override void OnStop()
    {
        try
        {
            RequestAdditionalTime((int)Math.Min(_shutdownTimeout.TotalMilliseconds, int.MaxValue));
        }
        catch (InvalidOperationException)
        {
            // Not stopping under the service control manager (a console run): nothing to ask.
        }
        base.OnStop();
    }
}
