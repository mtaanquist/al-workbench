using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Workers;

/// <summary>
/// The organisations a background sweep visits one at a time, each in an
/// <see cref="AmbientOrganizationScope"/> of its own: every one but those still pending
/// signup approval, which have no users and so nothing to sweep. The system org is
/// included, since in single-tenant deployments it is the working org.
/// </summary>
public static class SweptOrganizations
{
    /// <summary>
    /// Reads the list in a short-lived scope of its own. <c>organizations</c> carries no
    /// tenant filter, so this needs no bypass.
    /// </summary>
    public static async Task<List<(int Id, bool IsSystem)>> ListAsync(IServiceProvider services, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Organizations.AsNoTracking()
            .Where(o => !o.IsPending)
            .Select(o => new { o.Id, o.IsSystem })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(o => (o.Id, o.IsSystem)).ToList();
    }
}
