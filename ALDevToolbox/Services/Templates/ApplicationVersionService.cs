using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Templates;

/// <summary>
/// Read- and write-side service for the curated application-version catalogue
/// introduced in Milestone P2.4. The catalogue is a flat list edited the same
/// way as the well-known dependency catalogue: the admin submits the entire
/// ordered list, the service reconciles it row-by-row by primary key, and rows
/// missing from the input are soft-deleted so existing template foreign keys
/// stay resolvable.
/// </summary>
public sealed class ApplicationVersionService
{
    /// <summary>
    /// Sentinel value that means "resolve to the highest-ordered active
    /// catalogue row at request time" rather than a fixed application
    /// version. Carried on <see cref="RuntimeTemplate.DefaultApplicationVersionLatest"/>
    /// for template defaults; the New Workspace / New Extension form also
    /// posts this literal string in the <c>ApplicationVersion</c> +
    /// <c>RuntimeVersion</c> fields when the user picks "Latest" directly.
    /// </summary>
    public const string LatestSentinel = "latest";

    private static readonly Regex ApplicationVersionRegex = new(@"^\d+\.\d+\.\d+\.\d+$", RegexOptions.Compiled);
    private static readonly Regex RuntimeFormatRegex = new(@"^\d+(\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex NumericVersionRegex = new(@"^\d+(\.\d+)*$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly ILogger<ApplicationVersionService> _logger;
    private readonly IOrganizationContext _orgContext;

    public ApplicationVersionService(AppDbContext db, ILogger<ApplicationVersionService> logger, IOrganizationContext orgContext)
    {
        _db = db;
        _logger = logger;
        _orgContext = orgContext;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    /// <summary>
    /// Returns every active (non-deleted) catalogue entry in ordering order.
    /// Drives the user-facing select on the New Workspace / New Extension forms.
    /// </summary>
    public Task<List<ApplicationVersion>> GetActiveAsync(bool includeDeprecated = false, CancellationToken ct = default)
    {
        var query = _db.ApplicationVersions
            .AsNoTracking()
            .Where(a => a.DeletedAt == null);
        if (!includeDeprecated)
        {
            query = query.Where(a => !a.Deprecated);
        }
        return query
            .OrderBy(a => a.Ordering)
            .ThenBy(a => a.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns every catalogue row, including soft-deleted ones. Drives the
    /// admin editor.
    /// </summary>
    public Task<List<ApplicationVersion>> GetAllForAdminAsync(CancellationToken ct = default)
    {
        return _db.ApplicationVersions
            .AsNoTracking()
            .OrderBy(a => a.DeletedAt == null ? 0 : 1)
            .ThenBy(a => a.Ordering)
            .ThenBy(a => a.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the highest-ordered active (non-deleted, non-deprecated) row,
    /// or null when the catalogue is empty. Drives the "Latest" sentinel
    /// resolution on the template editor and the form endpoints.
    /// </summary>
    public Task<ApplicationVersion?> GetLatestAsync(CancellationToken ct = default) => FindLatestAsync(_db, ct);

    /// <summary>
    /// The query behind <see cref="GetLatestAsync"/>, for the generator, which
    /// holds its own context and resolves the sentinel for callers that did not.
    /// </summary>
    internal static Task<ApplicationVersion?> FindLatestAsync(AppDbContext db, CancellationToken ct)
    {
        return db.ApplicationVersions
            .AsNoTracking()
            .Where(a => a.DeletedAt == null && !a.Deprecated)
            .OrderBy(a => a.Ordering)
            .ThenBy(a => a.Name)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Lookup by stable URL-safe key. Used by the seed/TOML round-trip.</summary>
    public Task<ApplicationVersion?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        return _db.ApplicationVersions
            .AsNoTracking()
            .Where(a => a.Key == key)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Returns, for every catalogue row that's currently referenced as a
    /// template's <c>default_application_version</c>, the list of active
    /// (non-deprecated, non-soft-deleted) template names pointing at it.
    /// Drives the in-use captions on the admin editor and the deletion /
    /// deprecation guard in <see cref="SaveAsync"/>. Templates that are
    /// already deprecated or soft-deleted aren't included — the guard is
    /// only here to stop admins from quietly orphaning a template that
    /// end-users still see.
    /// </summary>
    public async Task<Dictionary<int, List<string>>> GetActiveUsageAsync(CancellationToken ct = default)
    {
        var rows = await _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.DeletedAt == null
                        && !t.Deprecated
                        && t.DefaultApplicationVersionId != null)
            .Select(t => new { VersionId = t.DefaultApplicationVersionId!.Value, t.Name })
            .ToListAsync(ct);
        return rows
            .GroupBy(x => x.VersionId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// Replaces the catalogue with the supplied list. Existing rows match on
    /// <see cref="ApplicationVersionInput.Id"/>; rows whose id is null are
    /// inserted; rows missing from the input are soft-deleted (so templates
    /// pointing at them keep resolving for the audit log).
    /// </summary>
    /// <param name="shownIds">
    /// The ids of the rows the editor was showing when it loaded. A row missing
    /// from <paramref name="inputs"/> is only removed when it was shown: one the
    /// daily wave sync added after the editor loaded stays. Null removes every
    /// missing row.
    /// </param>
    public async Task SaveAsync(IReadOnlyList<ApplicationVersionInput> inputs, IReadOnlyCollection<int>? shownIds = null, CancellationToken ct = default)
    {
        // Pad short application/runtime values up to their canonical shapes
        // (28 → 28.0.0.0, 16 → 16.0) before validating, so admins can type the
        // shorthand and the persisted row matches what app.json expects.
        var normalised = inputs
            .Select(i => i with
            {
                Application = PadVersion(i.Application, 4),
                Runtime = PadVersion(i.Runtime, 2),
            })
            .ToList();

        await ValidateAsync(normalised, ct);

        var existing = await _db.ApplicationVersions.ToListAsync(ct);
        var existingById = existing.ToDictionary(e => e.Id);
        var inputIds = normalised.Where(i => i.Id is not null).Select(i => i.Id!.Value).ToHashSet();
        // Rows the editor never showed are neither removed nor guarded: the admin
        // did not drop them, they arrived after the page loaded.
        bool Removed(ApplicationVersion row) =>
            !inputIds.Contains(row.Id) && row.DeletedAt is null && (shownIds is null || shownIds.Contains(row.Id));

        // In-use guard: stop admins from soft-deleting or freshly-deprecating
        // a row that an active (non-deprecated, non-deleted) template still
        // points at. Caught here before any mutation so a partial save can't
        // half-apply the change.
        var usage = await GetActiveUsageAsync(ct);
        var guardErrors = new Dictionary<string, string>();
        foreach (var row in existing)
        {
            if (!usage.TryGetValue(row.Id, out var usingTemplates) || usingTemplates.Count == 0)
            {
                continue;
            }
            var names = string.Join(", ", usingTemplates);
            if (Removed(row))
            {
                guardErrors[$"InUse.{row.Key}"] =
                    $"Can't remove '{row.Name}': in use by {usingTemplates.Count} active template(s) ({names}). " +
                    $"Reassign or deprecate the template first.";
                continue;
            }
            for (var i = 0; i < normalised.Count; i++)
            {
                var input = normalised[i];
                if (input.Id == row.Id && input.Deprecated && !row.Deprecated)
                {
                    guardErrors[$"Entries[{i}].Deprecated"] =
                        $"Can't deprecate: in use by {usingTemplates.Count} active template(s) ({names}).";
                    break;
                }
            }
        }
        if (guardErrors.Count > 0)
        {
            throw new PlanValidationException(guardErrors);
        }

        var now = DateTime.UtcNow;
        var orgId = RequireOrganizationId();

        for (var i = 0; i < normalised.Count; i++)
        {
            var input = normalised[i];
            var key = input.Key.Trim();
            var name = input.Name.Trim();
            var application = input.Application.Trim();
            var runtime = input.Runtime.Trim();

            if (input.Id is int id && existingById.TryGetValue(id, out var row))
            {
                row.Key = key;
                row.Name = name;
                row.Application = application;
                row.Runtime = runtime;
                row.Ordering = i;
                row.Deprecated = input.Deprecated;
                // Re-saving a row implicitly restores it from soft-delete.
                row.DeletedAt = null;
                row.UpdatedAt = now;
            }
            else
            {
                _db.ApplicationVersions.Add(new ApplicationVersion
                {
                    OrganizationId = orgId,
                    Key = key,
                    Name = name,
                    Application = application,
                    Runtime = runtime,
                    Ordering = i,
                    Deprecated = input.Deprecated,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        // Soft-delete rows that fell off the input list. Hard-delete would
        // cascade to NULL on every template's FK, but we want the audit log
        // to retain the row's identity so a curious admin can chase what
        // happened to a referenced version. Already-deleted rows are left
        // alone so we don't churn DeletedAt timestamps.
        foreach (var row in existing)
        {
            if (Removed(row))
            {
                row.DeletedAt = now;
                row.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Saved application-version catalogue: {Count} active entries.",
            normalised.Count);
    }

    /// <summary>
    /// Adds the release waves in <paramref name="shippedVersions"/> that are newer
    /// than every row the catalogue holds, at the top of the list, and returns
    /// them. Driven daily by <see cref="ApplicationVersionSyncScheduler"/> from the
    /// Microsoft symbol feed.
    ///
    /// <para>
    /// Only ever adds. Existing rows, their order and their deprecated flags are
    /// left alone, and older waves are never backfilled: the newest row decides
    /// what counts as new, and removed rows count too, so a wave an admin removed
    /// does not come back. An empty catalogue gets the newest wave only, so
    /// "Latest" has something to resolve to.
    /// </para>
    /// </summary>
    public async Task<List<ApplicationVersion>> AddNewWavesAsync(IReadOnlyList<string> shippedVersions, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var waves = BusinessCentralWaves.FromVersions(shippedVersions);
        if (waves.Count == 0) return [];

        var existing = await _db.ApplicationVersions
            .AsNoTracking()
            .Select(a => new { a.Key, a.Application, a.Ordering })
            .ToListAsync(ct);
        var newestMajor = existing
            .Select(a => BusinessCentralWaves.MajorOf(a.Application))
            .Max();
        var keys = existing.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = (existing.Count == 0 ? waves.Take(1) : waves.Where(w => w.Major > newestMajor)).ToList();
        foreach (var clash in candidates.Where(w => keys.Contains(w.Key)))
        {
            // An older row already holds the key (a typo, most likely); it would
            // be skipped again every day, so say why.
            _logger.LogWarning(
                "Release wave {Application} not added to the application-version catalogue for org {OrgId}: key {Key} is already used by another row.",
                clash.Application, orgId, clash.Key);
        }
        var toAdd = candidates.Where(w => !keys.Contains(w.Key)).ToList();
        if (toAdd.Count == 0) return [];

        // Above the current top row without renumbering anyone else's order:
        // "Latest" is the lowest ordering, and the newest wave belongs there.
        var top = existing.Count == 0 ? 0 : existing.Min(a => a.Ordering);
        var now = DateTime.UtcNow;
        var added = toAdd
            .Select((w, i) => new ApplicationVersion
            {
                OrganizationId = orgId,
                Key = w.Key,
                Name = w.Name,
                Application = w.Application,
                Runtime = w.Runtime,
                Ordering = top - toAdd.Count + i,
                CreatedAt = now,
                UpdatedAt = now,
            })
            .ToList();
        _db.ApplicationVersions.AddRange(added);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Added {Count} release wave(s) to the application-version catalogue for org {OrgId}: {Keys}.",
            added.Count, orgId, string.Join(", ", added.Select(a => a.Key)));
        return added;
    }

    /// <summary>
    /// Pads a short numeric version string up to <paramref name="parts"/>
    /// segments by appending <c>.0</c> per missing segment. Empty input or
    /// anything that doesn't already match the
    /// <c>digits(.digits)*</c> shape is returned unchanged so the regular
    /// validators can complain about it the same way they always do.
    /// </summary>
    private static string PadVersion(string? raw, int parts)
    {
        var trimmed = raw?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return trimmed;
        if (!NumericVersionRegex.IsMatch(trimmed)) return trimmed;
        var segments = trimmed.Split('.').ToList();
        while (segments.Count < parts) segments.Add("0");
        return string.Join('.', segments);
    }

    private async Task ValidateAsync(IReadOnlyList<ApplicationVersionInput> inputs, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pre-load existing rows so we can check for cross-row key collisions
        // against rows that aren't in the input (soft-deleted entries).
        var existing = await _db.ApplicationVersions
            .AsNoTracking()
            .Select(a => new { a.Id, a.Key })
            .ToListAsync(ct);
        var existingByKey = existing.ToDictionary(
            e => e.Key,
            e => e.Id,
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var key = input.Key?.Trim() ?? string.Empty;
            var name = input.Name?.Trim() ?? string.Empty;
            var application = input.Application?.Trim() ?? string.Empty;
            var runtime = input.Runtime?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(key))
            {
                errors[$"Entries[{i}].Key"] = "Key is required.";
            }
            else if (!ValidationPatterns.Key.IsMatch(key))
            {
                errors[$"Entries[{i}].Key"] = "Key must contain only lowercase letters, digits, and hyphens.";
            }
            else if (!seenKeys.Add(key))
            {
                errors[$"Entries[{i}].Key"] = $"Duplicate key '{key}'.";
            }
            else if (existingByKey.TryGetValue(key, out var ownerId) && ownerId != input.Id)
            {
                errors[$"Entries[{i}].Key"] =
                    $"A different application-version row already uses key '{key}'.";
            }

            if (string.IsNullOrEmpty(name))
            {
                errors[$"Entries[{i}].Name"] = "Name is required.";
            }

            if (string.IsNullOrEmpty(application))
            {
                errors[$"Entries[{i}].Application"] = "Application version is required.";
            }
            else if (!ApplicationVersionRegex.IsMatch(application))
            {
                errors[$"Entries[{i}].Application"] =
                    "Must be a four-part version (e.g. 24.0.0.0).";
            }

            if (string.IsNullOrEmpty(runtime))
            {
                errors[$"Entries[{i}].Runtime"] = "Runtime is required.";
            }
            else if (!RuntimeFormatRegex.IsMatch(runtime))
            {
                errors[$"Entries[{i}].Runtime"] =
                    "Runtime must be a number, optionally with one minor part (e.g. 15 or 15.2).";
            }
        }

        if (errors.Count > 0)
        {
            throw new PlanValidationException(errors);
        }
    }
}

/// <summary>
/// One row submitted by the admin application-version editor. <see cref="Id"/>
/// is null for new rows; otherwise it carries the persisted primary key so the
/// service can reconcile the existing row in place.
/// </summary>
public record ApplicationVersionInput(
    int? Id,
    string Key,
    string Name,
    string Application,
    string Runtime,
    bool Deprecated);
