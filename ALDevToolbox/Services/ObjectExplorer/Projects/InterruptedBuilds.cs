using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Fails the build rows a restart left at queued or building with no job behind them, so
/// a row does not sit there for good (#1111). That is any row whose release is no longer
/// importing: the startup sweep has just failed the interrupted ones, an earlier start
/// may have failed others, and a deleted release leaves the row with none at all. A
/// build being resumed keeps its release importing and is left alone. Pull-request
/// builds are left to <c>GitHubWebhookRecoveryScheduler</c>, which also closes their
/// check runs on GitHub. See <c>.design/object-explorer-project-builds.md</c>.
/// </summary>
public static class InterruptedBuilds
{
    /// <summary>What a build whose job was lost to a restart says on its row.</summary>
    public const string RestartedMessage =
        "The workbench restarted before this build finished. Start the build again.";

    /// <summary>
    /// Marks those rows failed in every organisation a sweep visits (see
    /// <see cref="SweptOrganizations"/>). Runs at startup only, before any
    /// worker can pick a build up. Each organisation runs in an ambient scope of its own,
    /// so the tenant filter stays on. Returns how many rows changed.
    /// </summary>
    public static async Task<int> FailAsync(IServiceProvider services, DateTime nowUtc, CancellationToken ct)
    {
        var orgs = await SweptOrganizations.ListAsync(services, ct).ConfigureAwait(false);

        var failed = 0;
        foreach (var (orgId, system) in orgs)
        {
            using var ambient = AmbientOrganizationScope.Enter(
                AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, system));
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            failed += await db.OeProjectBuilds
                .Where(b => (b.Status == ProjectBuildStatus.Queued || b.Status == ProjectBuildStatus.Building)
                    && b.Trigger != ProjectBuildTrigger.PullRequest
                    && (b.Release == null || b.Release.Status != "ingesting"))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, ProjectBuildStatus.Failed)
                    .SetProperty(b => b.FailureMessage, RestartedMessage)
                    .SetProperty(b => b.FinishedAt, nowUtc), ct)
                .ConfigureAwait(false);
        }
        return failed;
    }
}
