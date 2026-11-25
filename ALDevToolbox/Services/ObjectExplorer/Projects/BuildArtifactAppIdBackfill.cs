using ALDevToolbox.Data;
using ALDevToolbox.Services.ObjectExplorer.Import;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Stamps <c>oe_project_build_artifacts.app_id</c> on the rows written before the
/// column existed, by reading the app id out of each retained <c>.app</c>'s own
/// manifest (#901, Part 3). Done in code rather than in the migration because the
/// id sits inside a zip inside a <c>bytea</c>, which SQL cannot open. New rows are
/// stamped when they are written, so after the first boot this finds only rows
/// whose bytes are not a readable package; those stay null - a build simply never
/// resolves a dependency from them - and are logged, not thrown over.
/// </summary>
public static class BuildArtifactAppIdBackfill
{
    /// <summary>
    /// Runs the pass for every organisation, each inside its own
    /// <see cref="AmbientOrganizationScope"/> and DI scope so every read and write
    /// goes through that organisation's query filter - the startup category of the
    /// tenant fence, without crossing it. Never throws except on cancellation.
    /// </summary>
    public static async Task RunAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        List<(int Id, bool IsSystem)> orgs;
        await using (var scope = services.CreateAsyncScope())
        {
            // organizations is the tenant table itself and carries no query filter.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            orgs = (await db.Organizations.AsNoTracking()
                    .Select(o => new { o.Id, o.IsSystem })
                    .ToListAsync(ct))
                .Select(o => (o.Id, o.IsSystem))
                .ToList();
        }

        foreach (var (orgId, isSystem) in orgs)
        {
            try
            {
                using var ambient = AmbientOrganizationScope.Enter(
                    AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await StampOrganizationAsync(db, logger, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Stamping app ids on the build artifacts of organisation {OrgId} failed; retried on the next start.", orgId);
            }
        }
    }

    /// <summary>
    /// Stamps every artifact of the organisation in scope whose app id is null.
    /// One row at a time, so a large backlog never holds more than one package in
    /// memory. Returns how many rows were stamped.
    /// </summary>
    public static async Task<int> StampOrganizationAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var ids = await db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.AppId == null)
            .OrderBy(a => a.Id)
            .Select(a => a.Id)
            .ToListAsync(ct);

        var stamped = 0;
        foreach (var id in ids)
        {
            var row = await db.OeProjectBuildArtifacts.AsNoTracking()
                .Where(a => a.Id == id)
                .Select(a => new { a.FileName, a.Content })
                .FirstOrDefaultAsync(ct);
            if (row is null) continue;

            var appId = ReadAppId(row.Content);
            if (appId is null)
            {
                logger.LogWarning("Build artifact {ArtifactId} ({FileName}) is not a readable .app; its app id stays unknown.",
                    id, row.FileName);
                continue;
            }
            stamped += await db.OeProjectBuildArtifacts
                .Where(a => a.Id == id && a.AppId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.AppId, appId), ct);
        }

        if (stamped > 0) logger.LogInformation("Stamped the app id on {Count} build artifact(s).", stamped);
        return stamped;
    }

    /// <summary>The app id in <paramref name="content"/>'s manifest, as <see cref="CanonicalAppId"/> text, or null when it is not a readable <c>.app</c>.</summary>
    public static string? ReadAppId(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        return AppPackageReader.TryReadManifest(stream)?.AppId.ToString("D");
    }

    /// <summary>
    /// The one spelling the column holds: lower-case, hyphenated, no braces - what
    /// <c>Guid.ToString("D")</c> gives. Null for text that is not a GUID, so a
    /// malformed <c>app.json</c> id leaves the column empty rather than unmatchable.
    /// </summary>
    public static string? CanonicalAppId(string? raw) =>
        Guid.TryParse(raw?.Trim().Trim('{', '}'), out var id) ? id.ToString("D") : null;
}
