using System.Net;
using System.Reflection;
using ALWorkbench.Gateway.Contracts;

namespace ALWorkbench.Gateway.Core;

/// <summary>
/// The one <see cref="HttpClient"/> the gateway talks to AL Workbench with. It goes
/// out through the machine's proxy and trusts the machine's certificate store, so a
/// corporate proxy and TLS inspection work without settings of our own. See
/// <c>.design/on-prem-gateway.md</c>, "Connection model".
/// </summary>
public static class GatewayHttp
{
    /// <summary>
    /// A client with the system proxy (<see cref="HttpClient.DefaultProxy"/>: on Windows
    /// the WinHTTP / Internet Options proxy, or <c>HTTPS_PROXY</c> when set), signed in
    /// to that proxy as the service account where it asks, and the operating system's
    /// certificate validation, which on Windows is the machine's trusted root store.
    /// No timeout on the client itself: every call sets its own, because the long-poll
    /// and a file download need much longer than a heartbeat.
    /// </summary>
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            // Proxy left null: that is what makes the handler use the system proxy,
            // and DefaultProxyCredentials only applies to that one.
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            // Connections are reused across the long-poll; recycle them now and then so
            // a DNS change (a workbench moved behind a new address) is picked up.
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ALWorkbench.Gateway/{ProgramVersion}");
        // On every request, so AL Workbench can refuse a gateway too old for it with a
        // message that says so, before it reads anything else (GatewayProtocol.MinimumGatewayVersion).
        client.DefaultRequestHeaders.Add(GatewayProtocol.GatewayVersionHeader, ProgramVersion);
        return client;
    }

    /// <summary>
    /// The gateway program's version, as reported to AL Workbench: the release it was
    /// published with (release.yml stamps it from the tag), <c>0.0.0-staging.&lt;commit&gt;</c>
    /// for a staging preview, or <c>0.0.0-dev</c> for a local build.
    /// </summary>
    public static string ProgramVersion { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(GatewayHttp).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
