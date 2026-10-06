using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// Whether Business Central is still holding the app a handed-off deployment left with it
/// for a later update (#1097), read from the database alone so a list can ask it for every
/// row at once. A handed-off row never changes again once written, so "still held" is read
/// from what came after it: a later run of the same pipeline replaced Business Central's
/// copy, and the environment showing that version (or newer) installed means the update has
/// been and gone. Several apps aren't moved: cancelling them one by one can leave half a
/// build held. Nor is one whose pipeline now deploys somewhere else: its replacement would
/// go there instead.
/// <para>
/// The Reschedule dialog, the pipeline page and the deployment pipelines list all ask
/// here, so a run that has already installed never offers a Reschedule the dialog then refuses.
/// </para>
/// </summary>
internal static class HeldInstalls
{
    /// <summary>Why a handed-off deployment can or can't be moved.</summary>
    internal enum Verdict
    {
        /// <summary>Business Central is still holding its one app.</summary>
        Held,
        /// <summary>It left no app, or several, with Business Central.</summary>
        NotOneApp,
        /// <summary>It has installed, or a later run replaced it.</summary>
        Gone,
        /// <summary>Its pipeline now deploys to another environment.</summary>
        PipelineMoved,
    }

    /// <summary>The verdict for one handed-off deployment, with the held app when there is one.</summary>
    internal sealed record Check(Verdict Verdict, int ResultId, Guid AppId, string AppVersion);

    /// <summary>
    /// The verdict for each handed-off deployment among <paramref name="deliveryIds"/>.
    /// Deployments in any other state are left out of the result. A fixed number of queries
    /// however many ids are asked about.
    /// </summary>
    internal static async Task<Dictionary<int, Check>> CheckAsync(
        AppDbContext db, IReadOnlyCollection<int> deliveryIds, CancellationToken ct)
    {
        var result = new Dictionary<int, Check>();
        if (deliveryIds.Count == 0) return result;

        var rows = await db.OeProjectDeliveries.AsNoTracking()
            .Where(d => deliveryIds.Contains(d.Id) && d.Status == ProjectDeliveryStatus.HandedOff)
            .Select(d => new
            {
                d.Id,
                d.ProjectId,
                d.EnvironmentName,
                PipelineEnvironmentId = d.ReleasePipeline!.ProjectEnvironmentId,
                PipelineEnvironmentName = d.ReleasePipeline.ProjectEnvironment!.Name,
                Held = d.Results
                    .Where(r => r.Status == ProjectDeliveryResultStatus.Scheduled)
                    .Select(r => new { r.Id, r.AppId, r.AppVersion })
                    .ToList(),
                Later = db.OeProjectDeliveries.Any(o => o.ReleasePipelineId == d.ReleasePipelineId && o.Id > d.Id
                    && (o.Status == ProjectDeliveryStatus.HandedOff || o.Status == ProjectDeliveryStatus.Deployed
                        || o.Status == ProjectDeliveryStatus.Claimed || o.Status == ProjectDeliveryStatus.Uploading
                        || o.Status == ProjectDeliveryStatus.Installing)),
            })
            .ToListAsync(ct);

        // The environment a delivery installs to is its snapshot name, as the run resolves
        // it - preferring the pipeline's own environment when two share the name.
        var projectIds = rows.Select(r => r.ProjectId).Distinct().ToList();
        var names = rows.Select(r => r.EnvironmentName).Distinct().ToList();
        var environments = await db.OeProjectEnvironments.AsNoTracking()
            .Where(e => projectIds.Contains(e.ProjectId) && names.Contains(e.Name))
            .Select(e => new { e.Id, e.ProjectId, e.Name })
            .ToListAsync(ct);

        var candidates = new List<(int DeliveryId, int EnvironmentId, bool PipelineMoved, int ResultId, Guid AppId, string Version)>();
        foreach (var r in rows)
        {
            if (r.Held.Count != 1 || !Guid.TryParse(r.Held[0].AppId, out var appId) || string.IsNullOrWhiteSpace(r.Held[0].AppVersion))
            {
                result[r.Id] = new Check(Verdict.NotOneApp, 0, Guid.Empty, string.Empty);
                continue;
            }
            var held = r.Held[0];
            if (r.Later)
            {
                result[r.Id] = new Check(Verdict.Gone, held.Id, appId, held.AppVersion);
                continue;
            }
            var environmentId = environments
                .Where(e => e.ProjectId == r.ProjectId && e.Name == r.EnvironmentName)
                .OrderBy(e => e.Id == r.PipelineEnvironmentId ? 0 : 1)
                .Select(e => (int?)e.Id)
                .FirstOrDefault() ?? r.PipelineEnvironmentId;
            var moved = !string.Equals(r.PipelineEnvironmentName, r.EnvironmentName, StringComparison.Ordinal);
            candidates.Add((r.Id, environmentId, moved, held.Id, appId, held.AppVersion));
        }
        if (candidates.Count == 0) return result;

        var environmentIds = candidates.Select(c => c.EnvironmentId).Distinct().ToList();
        var appIds = candidates.Select(c => c.AppId).Distinct().ToList();
        var installed = await db.OeEnvironmentApps.AsNoTracking()
            .Where(a => environmentIds.Contains(a.EnvironmentId) && appIds.Contains(a.AppId))
            .Select(a => new { a.EnvironmentId, a.AppId, a.Version })
            .ToListAsync(ct);

        foreach (var c in candidates)
        {
            var have = installed
                .Where(a => a.EnvironmentId == c.EnvironmentId && a.AppId == c.AppId)
                .Select(a => a.Version)
                .FirstOrDefault();
            var gone = Version.TryParse(have, out var haveVersion) && Version.TryParse(c.Version, out var want) && haveVersion >= want;
            var verdict = gone ? Verdict.Gone : c.PipelineMoved ? Verdict.PipelineMoved : Verdict.Held;
            result[c.DeliveryId] = new Check(verdict, c.ResultId, c.AppId, c.Version);
        }
        return result;
    }

    /// <summary>The handed-off deployments among <paramref name="deliveryIds"/> that Business Central is still holding.</summary>
    internal static async Task<HashSet<int>> StillHeldAsync(
        AppDbContext db, IReadOnlyCollection<int> deliveryIds, CancellationToken ct) =>
        (await CheckAsync(db, deliveryIds, ct))
            .Where(kv => kv.Value.Verdict == Verdict.Held)
            .Select(kv => kv.Key)
            .ToHashSet();
}
