using ALDevToolbox.Services.Gateways;
using ALWorkbench.Gateway.Contracts;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Refuses every gateway route to a gateway older than
/// <see cref="GatewayProtocol.MinimumGatewayVersion"/>, with 426 and a message for
/// customer IT that says which version is needed and how to update. See
/// <c>.design/on-prem-gateway.md</c>, "Versions and updates".
///
/// <para>Middleware rather than an endpoint filter so it runs before the gateway's
/// token is read: the point of a minimum version is that a gateway too old to speak
/// today's protocol is told so, instead of getting a 401 for a token it built the old
/// way. It reads only <see cref="GatewayProtocol.GatewayVersionHeader"/>; a request
/// without it is let through to the usual checks (no released gateway omits it).
/// The per-address rate limit has already run.</para>
/// </summary>
public static class GatewayVersionGate
{
    public static IApplicationBuilder UseGatewayVersionGate(this IApplicationBuilder app) =>
        app.UseWhen(
            ctx => ctx.Request.Path.StartsWithSegments(GatewayProtocol.PathPrefix, StringComparison.OrdinalIgnoreCase),
            branch => branch.Use(async (ctx, next) =>
            {
                var reported = ctx.Request.Headers[GatewayProtocol.GatewayVersionHeader].ToString();
                if (GatewayBuild.IsBelowMinimum(reported))
                {
                    ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(GatewayVersionGate))
                        .LogWarning("Refused {Method} {Path} from a gateway at version {GatewayVersion}, below the minimum {MinimumVersion}.",
                            ctx.Request.Method, ctx.Request.Path.Value, reported, GatewayProtocol.MinimumGatewayVersion);
                    ctx.Response.StatusCode = GatewayProtocol.OutdatedStatusCode;
                    await ctx.Response.WriteAsJsonAsync(new GatewayErrorResponse
                    {
                        Errors = new Dictionary<string, string> { ["version"] = RefusalMessage(reported) },
                    }, ctx.RequestAborted);
                    return;
                }
                await next(ctx);
            }));

    /// <summary>What the gateway logs when it is refused, written for customer IT.</summary>
    public static string RefusalMessage(string reported) =>
        $"This gateway is version {reported.Trim()}, and this AL Workbench needs version {GatewayProtocol.MinimumGatewayVersion} or later. "
        + "An Admin can download the current gateway from Administration → Gateways in AL Workbench.";
}
