using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Templates;

/// <summary>
/// Read- and write-side service for the well-known dependency catalogue. The
/// catalogue is a flat list with no soft-delete, so the admin editor submits
/// the entire ordered list and this service reconciles it row by row, like
/// <see cref="TemplateService"/> does for folders. Stable primary keys for
/// unchanged rows keep the audit log honest.
/// </summary>
public class CatalogService
{
    private readonly AppDbContext _db;
    private readonly ILogger<CatalogService> _logger;
    private readonly IOrganizationContext _orgContext;

    public CatalogService(AppDbContext db, ILogger<CatalogService> logger, IOrganizationContext orgContext)
    {
        _db = db;
        _logger = logger;
        _orgContext = orgContext;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    /// <summary>
    /// Returns every catalogue row, ordered by category then explicit ordering.
    /// Used by both the admin editor and the New Extension dependency picker.
    /// </summary>
    public Task<List<WellKnownDependency>> GetAllAsync(CancellationToken ct = default)
    {
        return _db.WellKnownDependencies
            .AsNoTracking()
            .OrderBy(w => w.Category)
            .ThenBy(w => w.Ordering)
            .ThenBy(w => w.DepName)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Replaces the catalogue with the supplied list. Existing rows are matched
    /// by primary key (preserved through <see cref="CatalogEntryInput.Id"/>);
    /// rows whose id is null are inserted; rows missing from the input are
    /// deleted. Each surviving row's <see cref="WellKnownDependency.Ordering"/>
    /// is rewritten to its position in the input list so the picker UI groups
    /// stay stable.
    /// </summary>
    public async Task SaveAsync(IReadOnlyList<CatalogEntryInput> inputs, CancellationToken ct = default)
    {
        Validate(inputs);
        var orgId = RequireOrganizationId();

        var existing = await _db.WellKnownDependencies.ToListAsync(ct);
        var existingById = existing.ToDictionary(e => e.Id);
        var inputIds = inputs.Where(i => i.Id is not null).Select(i => i.Id!.Value).ToHashSet();

        var now = DateTime.UtcNow;

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            // Stored in the one form app.json uses, so "{...}" or 32 digits typed
            // here reach the picker and the generated file as a plain GUID (#1615).
            var depId = Guid.Parse(input.DepId.Trim()).ToString("D");
            var depName = input.DepName.Trim();
            var depPublisher = input.DepPublisher.Trim();
            var depVersionDefault = input.DepVersionDefault.Trim();
            var category = string.IsNullOrWhiteSpace(input.Category) ? null : input.Category.Trim();

            if (input.Id is int id && existingById.TryGetValue(id, out var row))
            {
                row.DepId = depId;
                row.DepName = depName;
                row.DepPublisher = depPublisher;
                row.DepVersionDefault = depVersionDefault;
                row.Category = category;
                row.Ordering = i;
                row.UpdatedAt = now;
            }
            else
            {
                _db.WellKnownDependencies.Add(new WellKnownDependency
                {
                    OrganizationId = orgId,
                    DepId = depId,
                    DepName = depName,
                    DepPublisher = depPublisher,
                    DepVersionDefault = depVersionDefault,
                    Category = category,
                    Ordering = i,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        foreach (var row in existing)
        {
            if (!inputIds.Contains(row.Id))
            {
                _db.WellKnownDependencies.Remove(row);
            }
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Saved well-known catalogue: {Count} entries.",
            inputs.Count);
    }

    private static void Validate(IReadOnlyList<CatalogEntryInput> inputs)
    {
        var errors = new Dictionary<string, string>();
        var seenDepIds = new HashSet<Guid>();

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var depId = input.DepId?.Trim() ?? string.Empty;
            var depName = input.DepName?.Trim() ?? string.Empty;
            var depPublisher = input.DepPublisher?.Trim() ?? string.Empty;
            var depVersionDefault = input.DepVersionDefault?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(depId))
            {
                errors[$"Entries[{i}].DepId"] = "Dependency id is required.";
            }
            else if (!Guid.TryParse(depId, out var parsedId))
            {
                errors[$"Entries[{i}].DepId"] = "Dependency id must be a GUID.";
            }
            // Compared as GUIDs, so the same id typed in two forms is still a duplicate.
            else if (!seenDepIds.Add(parsedId))
            {
                errors[$"Entries[{i}].DepId"] = $"Duplicate dependency id '{depId}'.";
            }

            if (string.IsNullOrEmpty(depName))
            {
                errors[$"Entries[{i}].DepName"] = "Dependency name is required.";
            }
            if (string.IsNullOrEmpty(depPublisher))
            {
                errors[$"Entries[{i}].DepPublisher"] = "Dependency publisher is required.";
            }
            if (string.IsNullOrEmpty(depVersionDefault))
            {
                errors[$"Entries[{i}].DepVersionDefault"] = "Default version is required.";
            }
        }

        if (errors.Count > 0)
        {
            throw new PlanValidationException(errors);
        }
    }
}

/// <summary>
/// One row submitted by the catalogue editor. <see cref="Id"/> is null for new
/// rows the admin just added; otherwise it carries the persisted primary key
/// so the service can update the existing row in place.
/// </summary>
public record CatalogEntryInput(
    int? Id,
    string DepId,
    string DepName,
    string DepPublisher,
    string DepVersionDefault,
    string? Category);
