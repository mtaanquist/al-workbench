using System.Buffers.Text;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.Sealed;
using ALDevToolbox.Services.Tools;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// The one endpoint a Sealed link's recipient calls: spend a view and get the
/// ciphertext back, for their browser to decrypt with the key in the link. Anonymous
/// by design (the recipient has no account), so it is rate limited per address and
/// takes a small body. The sender's half runs on the Blazor page and needs no endpoint.
/// See <c>.design/sealed.md</c>.
/// </summary>
internal static class SealedEndpoints
{
    /// <summary>Per-IP rate-limit policy, registered in <c>OperationsRegistration</c>.</summary>
    public const string RevealRateLimitPolicy = "sealed-reveal";

    /// <summary>Two base64url values of 32 bytes and their JSON; anything bigger is not a reveal.</summary>
    private const int MaxRequestBodyBytes = 1024;

    public static IEndpointRouteBuilder MapSealedEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sealed/{token}/reveal", async (
            string token, SealedRevealRequest request, HttpContext ctx,
            SealedLinkService sealedLinks, IToolAvailability tools, CancellationToken ct) =>
        {
            // Nothing a proxy or the browser may keep: the body is the secret, sealed.
            ctx.Response.Headers.CacheControl = "no-store";

            // Switched off site-wide closes every link, like the sender's page. A
            // per-organisation switch cannot apply: there is no organisation here yet.
            if (!tools.IsSiteEnabled(ToolKey.Sealed)) return Gone();

            var ip = ctx.Connection.RemoteIpAddress?.ToString();
            var result = await sealedLinks.RevealAsync(token, request.Check, request.Passphrase, ip, ct);
            return result.Status switch
            {
                SealedRevealStatus.Revealed => Results.Json(new
                {
                    ciphertext = Base64Url.EncodeToString(result.Ciphertext!),
                    iv = Base64Url.EncodeToString(result.Iv!),
                    viewsLeft = result.ViewsLeft,
                    creator = result.ByCreator,
                }),
                SealedRevealStatus.WrongPassphrase => Results.Json(
                    new { error = "wrong-passphrase", attemptsLeft = result.AttemptsLeft },
                    statusCode: StatusCodes.Status403Forbidden),
                SealedRevealStatus.LockedOut => Results.Json(
                    new { error = "locked" }, statusCode: StatusCodes.Status403Forbidden),
                _ => Gone(),
            };
        })
        .AllowAnonymous()
        .DisableAntiforgery()
        .RequireRateLimiting(RevealRateLimitPolicy)
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRequestBodyBytes));

        return app;
    }

    private static IResult Gone() => Results.Json(new { error = "gone" }, statusCode: StatusCodes.Status404NotFound);
}

/// <summary>The recipient browser's proofs: one from the key in the link, one from the passphrase if there is one.</summary>
internal sealed record SealedRevealRequest(string? Check, string? Passphrase);
