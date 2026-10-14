using System.Globalization;
using System.Text.Json;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Gateways;
using ALWorkbench.Gateway.Contracts;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// The routes an on-premises gateway calls. Machine clients only: no cookie, no
/// antiforgery, JSON in and out. <c>/gateway/register</c> is anonymous and
/// rate-limited per client address, because the registration code in its body is
/// the credential; everything else requires the gateway policy and nothing else
/// (<see cref="GatewayAuthenticationHandler"/>). See <c>.design/on-prem-gateway.md</c>.
///
/// <para>Every refusal is a <see cref="GatewayErrorResponse"/> with a body, so
/// <c>UseStatusCodePagesWithReExecute</c> leaves it alone and the gateway can log
/// what went wrong.</para>
/// </summary>
public static class GatewayEndpoints
{
    /// <summary>Rate-limit policy name for the registration exchange, registered in <c>OperationsRegistration</c>.</summary>
    public const string RegisterRateLimitPolicy = "gateway-register";

    /// <summary>A registration or heartbeat body is a few hundred bytes; this is generous.</summary>
    private const int MaxSmallBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(GatewayProtocol.PathPrefix).DisableAntiforgery();

        group.MapPost("/register", async (HttpContext ctx, GatewayService gateways, CancellationToken ct) =>
        {
            var request = await ReadJsonAsync<GatewayRegistrationRequest>(ctx, ct);
            if (request is null) return Malformed();
            try
            {
                return Results.Json(await gateways.RegisterAsync(request, ct));
            }
            catch (PlanValidationException ex)
            {
                return Refused(ex.Errors, StatusCodes.Status400BadRequest);
            }
        })
        .AllowAnonymous()
        .RequireRateLimiting(RegisterRateLimitPolicy);

        group.MapPost("/heartbeat", async (HttpContext ctx, GatewayService gateways, CancellationToken ct) =>
        {
            var gatewayId = int.Parse(ctx.User.FindFirst(GatewayAuthenticationHandler.GatewayIdClaim)!.Value, CultureInfo.InvariantCulture);
            var request = await ReadJsonAsync<GatewayHeartbeatRequest>(ctx, ct);
            if (request is null) return Malformed();
            try
            {
                var response = await gateways.HeartbeatAsync(gatewayId, request, ct);
                return response is null
                    ? Refused(new Dictionary<string, string> { ["authorization"] = "This gateway has been revoked." }, StatusCodes.Status401Unauthorized)
                    : Results.Json(response);
            }
            catch (PlanValidationException ex)
            {
                return Refused(ex.Errors, StatusCodes.Status400BadRequest);
            }
        })
        .RequireAuthorization(GatewayAuthenticationHandler.PolicyName);

        return app;
    }

    /// <summary>
    /// Reads a small JSON body ourselves rather than through parameter binding, so a
    /// malformed one is answered with our error shape instead of a bare 400.
    /// </summary>
    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx, CancellationToken ct) where T : class
    {
        if (ctx.Request.ContentLength > MaxSmallBodyBytes) return null;
        try
        {
            // Bounded by what has arrived, not by the declared length: the register
            // route is anonymous, and a chunked body declares none.
            using var limited = new MemoryStream();
            var buffer = new byte[8 * 1024];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                limited.Write(buffer, 0, read);
                if (limited.Length > MaxSmallBodyBytes) return null;
            }
            limited.Position = 0;
            return await JsonSerializer.DeserializeAsync<T>(limited, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult Malformed() => Refused(
        new Dictionary<string, string> { ["body"] = "The request body is not the JSON this AL Workbench expects. Check that the gateway is up to date." },
        StatusCodes.Status400BadRequest);

    private static IResult Refused(IReadOnlyDictionary<string, string> errors, int status) =>
        Results.Json(new GatewayErrorResponse { Errors = errors }, statusCode: status);
}
