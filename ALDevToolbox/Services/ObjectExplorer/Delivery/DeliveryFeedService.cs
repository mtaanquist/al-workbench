using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// Deliveries across every solution the caller can see, newest first - the question
/// "what failed to deploy this week?" that the per-pipeline history in
/// <see cref="DeliveryService.ListDeliveryHistoryAsync"/> cannot answer without being
/// asked once per deployment pipeline. Read-only, and reached through
/// <see cref="ProjectAccess.VisibleProjectPredicate"/> like the fleet reads, so a Private
/// solution the caller is not on contributes nothing. Its only caller today is the
/// <c>list_recent_deployments</c> MCP tool. See <c>.design/saas-delivery.md</c>, "MCP parity".
/// </summary>
public sealed class DeliveryFeedService
{
    /// <summary>The most one call returns, whatever the caller asks for.</summary>
    public const int MaxLimit = 200;

    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;

    public DeliveryFeedService(AppDbContext db, ProjectAccess access)
    {
        _db = db;
        _access = access;
    }

    /// <summary>
    /// Deliveries newest first (by when they were asked for), optionally narrowed to one
    /// status and to those created at or after <paramref name="sinceUtc"/>. The per-app rows
    /// ride along only on a failed delivery, which is the one where they say something the
    /// status does not.
    /// </summary>
    public async Task<List<RecentDeliveryRow>> ListRecentAsync(
        string? status, DateTime? sinceUtc, int limit, int? projectId = null, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, MaxLimit);
        var snapshot = await _access.GetSnapshotAsync(ct);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);

        var query = _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => _db.OeProjects.Where(visible).Any(p => p.Id == d.ProjectId && p.DeletedAt == null));
        if (!string.IsNullOrWhiteSpace(status))
        {
            var wanted = status.Trim().ToLowerInvariant();
            query = query.Where(d => d.Status == wanted);
        }
        if (sinceUtc is { } since)
        {
            query = query.Where(d => d.CreatedAt >= since);
        }
        if (projectId is { } pid)
        {
            query = query.Where(d => d.ProjectId == pid);
        }

        return await query
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .Take(take)
            .Select(d => new RecentDeliveryRow(
                d.Id,
                d.ProjectId,
                d.Project!.Name,
                d.ReleasePipelineId,
                d.ReleasePipeline!.Name,
                d.EnvironmentName,
                d.ProjectBuildId,
                d.Status,
                d.CreatedAt,
                d.ScheduledFor,
                d.StartedAt,
                d.FinishedAt,
                d.FailureMessage,
                d.TriggeredByUser != null ? d.TriggeredByUser.DisplayName : null,
                d.Status == ProjectDeliveryStatus.Failed
                    ? d.Results.OrderBy(r => r.Ordering)
                        .Select(r => new DeliveryAppRow(r.AppName, r.AppVersion, r.Status, r.Message))
                        .ToList()
                    : new List<DeliveryAppRow>()))
            .ToListAsync(ct);
    }
}

/// <summary>One delivery in the cross-solution feed. <paramref name="Apps"/> is filled only for a failed one.</summary>
public sealed record RecentDeliveryRow(
    int Id,
    int ProjectId,
    string ProjectName,
    int ReleasePipelineId,
    string ReleasePipelineName,
    string EnvironmentName,
    int BuildId,
    string Status,
    DateTime CreatedAt,
    DateTime ScheduledFor,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? FailureMessage,
    string? TriggeredByName,
    List<DeliveryAppRow> Apps);
