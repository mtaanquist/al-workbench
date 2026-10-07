using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Write-path operations on an existing <see cref="Domain.Entities.ObjectExplorer.OeRelease"/>.
/// Soft-delete is the default — reversible, sets <c>deleted_at</c>, hides the
/// Release from the public picker. Hard-delete drops the Release row and
/// cascades through every dependent <c>oe_modules</c> / <c>oe_module_*</c>
/// row via the FK relationships set up in PR 1; it's the path admins use
/// when storage actually needs reclaiming.
///
/// All operations require an Admin user in scope (gated at the endpoint
/// layer) and are scoped to <see cref="IOrganizationContext.CurrentOrganizationId"/>
/// via the query filter on <see cref="AppDbContext"/>.
/// </summary>
public class ReleaseManagementService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<ReleaseManagementService> _logger;

    public ReleaseManagementService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ILogger<ReleaseManagementService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; ReleaseManagementService called outside an authenticated request.");

    /// <summary>
    /// Sets <c>deleted_at</c> to now. Reversible via <see cref="RestoreAsync"/>.
    /// Soft-deleted Releases stay queryable for admins but disappear from the
    /// regular browse list.
    /// </summary>
    public async Task SoftDeleteAsync(int releaseId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var release = await _db.OeReleases
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct).ConfigureAwait(false)
            ?? throw NotFound(releaseId);

        if (release.DeletedAt is not null)
        {
            // Idempotent: already soft-deleted is a no-op.
            return;
        }
        release.DeletedAt = DateTime.UtcNow;
        release.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Soft-deleted Release {ReleaseId} ({Label})", release.Id, release.Label);
    }

    /// <summary>
    /// Clears <c>deleted_at</c>, making the Release visible again. Inverse of
    /// <see cref="SoftDeleteAsync"/>.
    /// </summary>
    public async Task RestoreAsync(int releaseId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var release = await _db.OeReleases
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct).ConfigureAwait(false)
            ?? throw NotFound(releaseId);

        if (release.DeletedAt is null) return;

        // The active-rows unique index on dedup_key would refuse the restore
        // when a successor already holds the key (a replaced or superseded
        // preview is the everyday case): say so instead of surfacing a
        // constraint violation.
        if (release.DedupKey is not null)
        {
            var successor = await _db.OeReleases.AsNoTracking()
                .Where(r => r.Id != release.Id && r.DeletedAt == null && r.DedupKey == release.DedupKey)
                .Select(r => r.Label)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (successor is not null)
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["Release"] = $"Can't restore \"{release.Label}\": \"{successor}\" has taken its place. Delete that release first if you want this one back.",
                });
            }
        }

        release.DeletedAt = null;
        release.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Restored Release {ReleaseId} ({Label})", release.Id, release.Label);
    }

    /// <summary>
    /// Updates the editable free-text metadata on a Release — the publisher and
    /// (for pipeline-build Releases only) the project name. Both collapse to null
    /// when blank. The manage form only renders the Project field for
    /// <c>kind = 'project'</c>, so an absent/blank <paramref name="projectName"/>
    /// on any other kind must not wipe a legacy stored value — those survive the
    /// project → third_party merge hidden, not deleted.
    /// Deliberately does <em>not</em> touch <c>parent_release_id</c>: re-parenting
    /// after ingest would invalidate the already-resolved cross-module references
    /// (the chain walk runs at import time), so the parent stays set on import only.
    /// </summary>
    public async Task UpdateMetadataAsync(
        int releaseId, string? publisher, string? projectName, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var release = await _db.OeReleases
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct).ConfigureAwait(false)
            ?? throw NotFound(releaseId);

        release.Publisher = NullIfBlank(publisher);
        if (release.Kind == "project")
        {
            release.ProjectName = NullIfBlank(projectName);
        }
        release.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Updated Release {ReleaseId} ({Label}) metadata: Publisher={Publisher} ProjectName={ProjectName}",
            release.Id, release.Label, release.Publisher, release.ProjectName);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Removes the Release row plus every dependent <c>oe_*</c> row via FK
    /// cascade. Refuses when another Release still points at this one as its
    /// <c>parent_release_id</c> — pre-checked here so the failure message is
    /// precise instead of a generic Postgres FK-violation.
    ///
    /// The supplied <paramref name="confirmLabel"/> must match the Release's
    /// actual label exactly. The UI requires admins to type the label before
    /// the POST, making accidental clicks harmless.
    /// </summary>
    public async Task HardDeleteAsync(int releaseId, string confirmLabel, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var release = await _db.OeReleases
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct).ConfigureAwait(false)
            ?? throw NotFound(releaseId);

        if (!string.Equals(confirmLabel?.Trim(), release.Label, StringComparison.Ordinal))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ConfirmLabel"] = "Type the exact Release label to confirm permanent deletion.",
            });
        }

        var hasChildren = await _db.OeReleases.AsNoTracking()
            .AnyAsync(r => r.ParentReleaseId == releaseId, ct).ConfigureAwait(false);
        if (hasChildren)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Release"] = "Another Release uses this one as its parent. Delete the dependent Release first.",
            });
        }

        // Capture the distinct content hashes this Release's files reference
        // BEFORE the cascade removes the oe_module_files rows. The shared
        // oe_file_contents blobs aren't cascade-deleted (the FK is Restrict, and
        // they may be shared with other releases/orgs), so we reclaim the ones
        // that become orphaned afterwards. Org-scoped read here is fine — we
        // only need this org's referenced hashes as GC candidates.
        var candidateHashes = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Module!.ReleaseId == releaseId)
            .Select(f => f.ContentHash)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        // The single cascading DELETE fans out through every dependent oe_*
        // table; for a large first-party release (tens of thousands of objects)
        // that runs well past Npgsql's default 30 s command timeout, surfacing
        // as a spurious "transient failure". Grant the same headroom the import
        // passes use.
        _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        _db.OeReleases.Remove(release);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var reclaimed = await ReclaimOrphanedBlobsAsync(candidateHashes, ct).ConfigureAwait(false);

        _logger.LogWarning(
            "Hard-deleted Release {ReleaseId} ({Label}). Dependent oe_* rows cascade-removed; reclaimed {Reclaimed} orphaned shared content blob(s) of {Candidates} candidate(s).",
            release.Id, release.Label, reclaimed, candidateHashes.Count);
    }

    /// <summary>
    /// Wipes every ingested <c>oe_module*</c> row for a Release while keeping the
    /// Release row itself, reclaiming any shared content blobs that become
    /// orphaned. Used by the import-retry path so a re-run starts from a clean
    /// slate rather than tripping the per-app idempotency skip on a module the
    /// previous (failed) attempt left half-written. Deleting the
    /// <c>oe_modules</c> rows cascades through every dependent module table
    /// (objects / files / symbols / variables / references / translations) via
    /// the FK relationships. Scoped by <c>release_id</c>, which is a global
    /// identity already validated against the caller's org by the retry endpoint.
    /// </summary>
    public async Task ClearIngestedDataAsync(int releaseId, CancellationToken ct = default)
    {
        RequireOrganizationId();

        // Capture the file content hashes before the cascade drops the
        // oe_module_files rows, same as HardDeleteAsync, so we can GC blobs that
        // no longer have any referrer afterwards.
        var candidateHashes = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Module!.ReleaseId == releaseId)
            .Select(f => f.ContentHash)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        // Same cascade exposure as HardDeleteAsync — the module-wide DELETE can
        // run long on a large release, so lift the command timeout off the 30 s
        // default before issuing it.
        _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        var removedModules = await _db.Database.ExecuteSqlRawAsync(
            "DELETE FROM oe_modules WHERE release_id = {0}",
            new object[] { releaseId }, ct).ConfigureAwait(false);

        var reclaimed = await ReclaimOrphanedBlobsAsync(candidateHashes, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Cleared ingested data for Release {ReleaseId}: removed {Modules} module(s), reclaimed {Reclaimed} orphaned blob(s) of {Candidates} candidate(s).",
            releaseId, removedModules, reclaimed, candidateHashes.Count);
    }

    /// <summary>
    /// Deletes the supplied content-hash blobs from <c>oe_file_contents</c> that
    /// no <c>oe_module_files</c> row references any more. Raw SQL so the
    /// <c>NOT EXISTS</c> check sees ALL orgs (the EF query filter doesn't apply)
    /// — a blob still used by another tenant must survive. Returns the count
    /// reclaimed. Shared by hard-delete and retry-clear.
    /// <para>
    /// A blob an import is saving at that moment is locked by it
    /// (<see cref="OeIngestHelpers.UpsertFileContentsAsync"/>) and is skipped, not
    /// waited for: the import is about to reference it. Should an import commit its
    /// references between this statement's snapshot and its delete, the foreign key
    /// refuses the delete; nothing is reclaimed this time and the blobs stay, which
    /// costs only space (#1181).
    /// </para>
    /// </summary>
    private async Task<int> ReclaimOrphanedBlobsAsync(IReadOnlyList<string> candidateHashes, CancellationToken ct)
    {
        if (candidateHashes.Count == 0) return 0;
        try
        {
            return await _db.Database.ExecuteSqlRawAsync(
                "DELETE FROM oe_file_contents c WHERE c.content_hash IN (" +
                "SELECT o.content_hash FROM oe_file_contents o WHERE o.content_hash = ANY({0}) " +
                "AND NOT EXISTS (SELECT 1 FROM oe_module_files f WHERE f.content_hash = o.content_hash) " +
                "FOR UPDATE SKIP LOCKED)",
                new object[] { candidateHashes.ToArray() }, ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            _logger.LogWarning("Left {Candidates} shared content blob(s) in place: an import started using one of them.", candidateHashes.Count);
            return 0;
        }
    }

    private static PlanValidationException NotFound(int releaseId) =>
        new(new Dictionary<string, string> { ["Release"] = $"Release {releaseId} not found in this organisation." });
}
