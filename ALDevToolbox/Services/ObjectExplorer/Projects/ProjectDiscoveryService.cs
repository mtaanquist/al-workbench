using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// The request-side surface over the per-project discovered-extensions cache: it
/// reads the cached checklist for the pipeline editor and gates the enqueue of a
/// background refresh. The clone-and-walk itself runs in
/// <see cref="ProjectDiscoveryWorker"/> via <see cref="ProjectBuildService.DiscoverExtensionsForCacheAsync"/>;
/// this service never clones. Org-scoped via the EF query filter. See
/// <c>.design/artifacts.md</c>.
/// </summary>
public sealed class ProjectDiscoveryService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly ProjectDiscoveryQueue _queue;
    private readonly ILogger<ProjectDiscoveryService> _logger;

    public ProjectDiscoveryService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        ProjectDiscoveryQueue queue,
        ILogger<ProjectDiscoveryService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _queue = queue;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; project discovery called outside an authenticated request.");

    /// <summary>
    /// Access-gates (owner / org Admin / SiteAdmin) and enqueues a background
    /// discovery for <paramref name="projectId"/>, capturing the current user's
    /// identity so the worker can resolve their repository token off-request. A
    /// discovery already queued or running for the project is coalesced (no-op).
    /// Throws <see cref="PlanValidationException"/> when the project is gone and
    /// <see cref="ProjectAccessDeniedException"/> when the caller may not manage it.
    /// </summary>
    public async Task RequestDiscoveryAsync(int projectId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId && c.DeletedAt == null)
            .Select(c => new { c.CreatedByUserId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Discovery"] = "This solution no longer exists.",
            });

        await _access.EnsureCanManageAsync(projectId, project.CreatedByUserId, ct).ConfigureAwait(false);

        var enqueued = await _queue.EnqueueAsync(new ProjectDiscoveryJob(projectId, AmbientOrganizationScope.OrganizationIdentity.FromContext(_orgContext, "queuing a discovery")), ct).ConfigureAwait(false);
        if (enqueued)
        {
            _logger.LogInformation("Queued extension discovery for project {ProjectId}.", projectId);
        }
    }

    /// <summary>
    /// Reads the project's cached discovery for the pipeline editor: the parsed
    /// extension list, when it was last discovered, the last failure (if any), and
    /// whether a refresh is in flight right now. Never clones — a stale/empty cache
    /// is reported as-is for the dialog to act on (auto-trigger a first discovery).
    /// </summary>
    public async Task<ProjectDiscovery> GetDiscoveryAsync(int projectId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        await _access.EnsureCanViewAsync(projectId, ct).ConfigureAwait(false);
        var row = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId && c.DeletedAt == null)
            .Select(c => new { c.DiscoveredExtensionsJson, c.DiscoveredAt, c.DiscoveryError })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var inFlight = _queue.IsInFlight(projectId);
        if (row is null)
        {
            return new ProjectDiscovery(Array.Empty<DiscoveredExtension>(), DiscoveredAt: null, Error: null, InFlight: inFlight);
        }
        return new ProjectDiscovery(ParseCached(row.DiscoveredExtensionsJson), row.DiscoveredAt, row.DiscoveryError, inFlight);
    }

    /// <summary>Parses the cached JSON array, tolerating a null/blank/corrupt value as an empty list.</summary>
    internal static IReadOnlyList<DiscoveredExtension> ParseCached(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<DiscoveredExtension>();
        try
        {
            return JsonSerializer.Deserialize<List<DiscoveredExtension>>(json) ?? (IReadOnlyList<DiscoveredExtension>)Array.Empty<DiscoveredExtension>();
        }
        catch (JsonException)
        {
            return Array.Empty<DiscoveredExtension>();
        }
    }
}

/// <summary>
/// The pipeline editor's view of a project's discovered extensions: the cached
/// checklist, when it was last refreshed, the last failure reason (shown when
/// there's no usable cache), and whether a refresh is running now (drives the
/// "Discovering…" spinner and the poll).
/// </summary>
public sealed record ProjectDiscovery(
    IReadOnlyList<DiscoveredExtension> Extensions,
    DateTime? DiscoveredAt,
    string? Error,
    bool InFlight);
