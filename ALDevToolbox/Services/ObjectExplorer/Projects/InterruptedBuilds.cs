using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Fails the build rows whose release the startup sweep has just marked interrupted, so
/// a row does not sit at queued or building for good once its job is gone (#1111).
/// Pull-request builds are left to <c>GitHubWebhookRecoveryScheduler</c>, which also
/// closes their check runs on GitHub. See <c>.design/object-explorer-project-builds.md</c>.
/// </summary>
public static class InterruptedBuilds
{
    /// <summary>What a build whose job was lost to a restart says on its row.</summary>
    public const string RestartedMessage =
        "The workbench restarted before this build finished. Start the build again.";

    /// <summary>
    /// Marks the queued or building rows of <paramref name="releases"/> failed. Each
    /// organisation runs in an ambient scope of its own, so the tenant filter stays on:
    /// every row read is pinned to that organisation and to one of its release ids.
    /// Returns how many rows changed.
    /// </summary>
    public static async Task<int> FailAsync(
        IServiceProvider services, IReadOnlyCollection<(int OrganizationId, int ReleaseId)> releases,
        DateTime nowUtc, CancellationToken ct)
    {
        if (releases.Count == 0) return 0;

        Dictionary<int, bool> isSystem;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var orgIds = releases.Select(r => r.OrganizationId).Distinct().ToList();
            // organizations carries no tenant filter, so this needs no bypass.
            isSystem = await db.Organizations.AsNoTracking()
                .Where(o => orgIds.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id, o => o.IsSystem, ct).ConfigureAwait(false);
        }

        var failed = 0;
        foreach (var org in releases.GroupBy(r => r.OrganizationId))
        {
            if (!isSystem.TryGetValue(org.Key, out var system)) continue;
            using var ambient = AmbientOrganizationScope.Enter(
                AmbientOrganizationScope.OrganizationIdentity.ForOrganization(org.Key, system));
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var releaseIds = org.Select(r => (int?)r.ReleaseId).ToList();
            failed += await db.OeProjectBuilds
                .Where(b => releaseIds.Contains(b.ReleaseId)
                    && (b.Status == ProjectBuildStatus.Queued || b.Status == ProjectBuildStatus.Building)
                    && b.Trigger != ProjectBuildTrigger.PullRequest)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, ProjectBuildStatus.Failed)
                    .SetProperty(b => b.FailureMessage, RestartedMessage)
                    .SetProperty(b => b.FinishedAt, nowUtc), ct)
                .ConfigureAwait(false);
        }
        return failed;
    }
}
