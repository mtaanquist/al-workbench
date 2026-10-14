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
/// (<see cref="GatewayAuthenticationHandler"/>), under a looser per-address limit
/// that bounds the lookup and signature check a stranger can trigger. See <c>.design/on-prem-gateway.md</c>.
///
/// <para>Every refusal is a <see cref="GatewayErrorResponse"/> with a body, so
/// <c>UseStatusCodePagesWithReExecute</c> leaves it alone and the gateway can log
/// what went wrong.</para>
/// </summary>
public static class GatewayEndpoints
{
    /// <summary>Rate-limit policy name for the registration exchange, registered in <c>OperationsRegistration</c>.</summary>
    public const string RegisterRateLimitPolicy = "gateway-register";

    /// <summary>
    /// Rate-limit policy for every gateway route, per client address, registered in
    /// <c>OperationsRegistration</c>. Set on the group so a new route gets it without
    /// asking; <c>/register</c> replaces it with <see cref="RegisterRateLimitPolicy"/>.
    /// </summary>
    public const string GatewayRateLimitPolicy = "gateway";

    /// <summary>A registration or heartbeat body is a few hundred bytes; this is generous.</summary>
    private const int MaxSmallBodyBytes = 64 * 1024;

    /// <summary>A progress post: up to <see cref="GatewayCommandService.MaxProgressLines"/> lines of text.</summary>
    private const int MaxProgressBodyBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(GatewayProtocol.PathPrefix)
            .DisableAntiforgery()
            .RequireRateLimiting(GatewayRateLimitPolicy);

        group.MapPost("/register", async (HttpContext ctx, GatewayService gateways, CancellationToken ct) =>
        {
            var request = await ReadJsonAsync<GatewayRegistrationRequest>(ctx, MaxSmallBodyBytes, ct);
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

        group.MapPost("/heartbeat", async (HttpContext ctx, GatewayService gateways, GatewayCommandService commands, CancellationToken ct) =>
        {
            var gatewayId = GatewayIdOf(ctx);
            var request = await ReadJsonAsync<GatewayHeartbeatRequest>(ctx, MaxSmallBodyBytes, ct);
            if (request is null) return Malformed();
            try
            {
                var response = await gateways.HeartbeatAsync(gatewayId, request, ct);
                if (response is null) return RevokedResult();
                // Keeps the commands it lists alive, and fails the ones a restart cut
                // short. A pending gateway's lists are ignored: it may be a re-keyed
                // machine nobody has approved yet, and the sweep fails its commands.
                if (IsApproved(ctx))
                {
                    await commands.ApplyHeartbeatAsync(gatewayId, request.RunningCommandIds, request.InterruptedCommandIds, ct);
                }
                return Results.Json(response);
            }
            catch (PlanValidationException ex)
            {
                return Refused(ex.Errors, StatusCodes.Status400BadRequest);
            }
        })
        .RequireAuthorization(GatewayAuthenticationHandler.PolicyName);

        // The long-poll (#1361): held open until a command is queued for this
        // gateway, or for GatewayCommandSignal.LongPollTimeout, then 204.
        group.MapGet("/commands/next", async (HttpContext ctx, GatewayCommandService commands, CancellationToken ct) =>
        {
            if (!IsApproved(ctx)) return NotApprovedResult();
            GatewayClaimResult result;
            try
            {
                result = await commands.WaitForNextAsync(GatewayIdOf(ctx), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The gateway hung up while waiting.
                return Results.Empty;
            }
            return result.Outcome switch
            {
                GatewayClaimOutcome.Claimed => Results.Json(result.Command),
                GatewayClaimOutcome.NotApproved => NotApprovedResult(),
                GatewayClaimOutcome.Revoked => RevokedResult(),
                _ => Results.NoContent(),
            };
        })
        .RequireAuthorization(GatewayAuthenticationHandler.PolicyName);

        group.MapPost("/commands/{id:long}/progress", async (long id, HttpContext ctx, GatewayCommandService commands, CancellationToken ct) =>
        {
            if (!IsApproved(ctx)) return NotApprovedResult();
            var request = await ReadJsonAsync<GatewayCommandProgressRequest>(ctx, MaxProgressBodyBytes, ct);
            if (request is null) return Malformed();
            try
            {
                return await commands.AppendProgressAsync(GatewayIdOf(ctx), id, request.Lines, request.Sequence, ct)
                    ? Results.NoContent()
                    : NotRunningResult();
            }
            catch (PlanValidationException ex)
            {
                return Refused(ex.Errors, StatusCodes.Status400BadRequest);
            }
        })
        .RequireAuthorization(GatewayAuthenticationHandler.PolicyName);

        group.MapPost("/commands/{id:long}/result", async (long id, HttpContext ctx, GatewayCommandService commands, CancellationToken ct) =>
        {
            if (!IsApproved(ctx)) return NotApprovedResult();
            // A result can carry a verb's whole report, so it may use the full body
            // the authentication handler accepts.
            var request = await ReadJsonAsync<GatewayCommandResultRequest>(ctx, GatewayProtocol.MaxRequestBodyBytes, ct);
            if (request is null) return Malformed();
            try
            {
                return await commands.CompleteAsync(GatewayIdOf(ctx), id, request, ct)
                    ? Results.NoContent()
                    : NotRunningResult();
            }
            catch (PlanValidationException ex)
            {
                return Refused(ex.Errors, StatusCodes.Status400BadRequest);
            }
        })
        .RequireAuthorization(GatewayAuthenticationHandler.PolicyName);

        // Checked explicitly beyond the organisation filter: the command is this
        // gateway's, still claimed or running, and the file is that command's.
        group.MapGet("/commands/{id:long}/files/{fileId:long}", async (long id, long fileId, HttpContext ctx, GatewayCommandService commands, CancellationToken ct) =>
        {
            if (!IsApproved(ctx)) return NotApprovedResult();
            var (outcome, file) = await commands.OpenFileAsync(GatewayIdOf(ctx), id, fileId, ct);
            return outcome switch
            {
                GatewayFileOutcome.Found => StreamFile(ctx, commands, file!),
                GatewayFileOutcome.NotRunning => NotRunningResult(),
                _ => Refused(
                    new Dictionary<string, string> { ["file"] = "This gateway has no such file to download." },
                    StatusCodes.Status404NotFound),
            };
        })
        .RequireAuthorization(GatewayAuthenticationHandler.PolicyName);

        return app;
    }

    /// <summary>
    /// Streams the file rather than buffering it (it may be 128 MB). The length is
    /// known up front, so the gateway can tell a cut-short download from a whole one;
    /// if the content vanished between the check and the copy (the command became
    /// final), the connection is aborted rather than answered with a short body.
    /// </summary>
    private static IResult StreamFile(HttpContext ctx, GatewayCommandService commands, GatewayCommandFileDownload file)
    {
        ctx.Response.ContentLength = file.Size;
        return Results.Stream(async body =>
        {
            if (!await commands.CopyFileContentAsync(file.Id, body, ctx.RequestAborted))
            {
                ctx.Abort();
            }
        }, "application/octet-stream", file.FileName);
    }

    private static int GatewayIdOf(HttpContext ctx) =>
        int.Parse(ctx.User.FindFirst(GatewayAuthenticationHandler.GatewayIdClaim)!.Value, CultureInfo.InvariantCulture);

    /// <summary>
    /// The authentication handler reads the gateway's state on every request, so the
    /// claim is as fresh as the request: an Admin replacing the key takes effect on
    /// the next one.
    /// </summary>
    private static bool IsApproved(HttpContext ctx) =>
        ctx.User.FindFirst(GatewayAuthenticationHandler.GatewayApprovedClaim)?.Value == "true";

    private static IResult NotApprovedResult() => Refused(
        new Dictionary<string, string>
        {
            ["authorization"] = "This gateway is waiting for an Admin to approve it under Administration → Gateways. It receives no commands or files until then.",
        },
        StatusCodes.Status403Forbidden);

    private static IResult RevokedResult() => Refused(
        new Dictionary<string, string> { ["authorization"] = "This gateway has been revoked." },
        StatusCodes.Status401Unauthorized);

    private static IResult NotRunningResult() => Refused(
        new Dictionary<string, string>
        {
            ["command"] = "This command is not running on this gateway any more: it finished, expired, or was failed after the gateway went quiet. Stop working on it.",
        },
        StatusCodes.Status409Conflict);

    /// <summary>
    /// Reads a JSON body ourselves rather than through parameter binding, so a
    /// malformed one - an unknown verb included - is answered with our error shape
    /// instead of a bare 400.
    /// </summary>
    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx, int maxBytes, CancellationToken ct) where T : class
    {
        if (ctx.Request.ContentLength > maxBytes) return null;
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
                if (limited.Length > maxBytes) return null;
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
