using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Gateways;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Downloading a licence file someone uploaded to an on-premises environment (#1369).
/// Only people who can manage the Solution may, which <see cref="OnPremisesLicenceService"/>
/// re-checks on the request's org-filtered context: an upload in another organisation, and
/// one the caller may not manage, both answer 404, so the refusal does not confirm the
/// Solution exists. A plain GET link, so no antiforgery token. See
/// <c>.design/on-prem-gateway.md</c>, "Licences".
/// </summary>
internal static class LicenceEndpoints
{
    public static IEndpointRouteBuilder MapLicenceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/licences/{licenceId:int}/file",
            async (int licenceId, OnPremisesLicenceService licences, CancellationToken ct) =>
            {
                LicenceDownload? file;
                try
                {
                    file = await licences.DownloadAsync(licenceId, ct);
                }
                catch (ProjectAccessDeniedException)
                {
                    return Results.NotFound();
                }
                if (file is null) return Results.NotFound();
                return Results.File(file.Content, "application/octet-stream", SanitiseFileName(file.FileName));
            })
            .RequireAuthorization();
        return app;
    }
}
