using System.Data;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Npgsql;

using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Services.Backups;

/// <summary>
/// Row view returned by <see cref="PerTenantBackupService.ListAsync"/>.
/// Carries the metadata the SiteAdmin UI renders without re-stating files.
/// </summary>
public sealed record PerTenantBackupRow(
    int Id,
    int OrganizationId,
    string OrganizationName,
    string OrganizationSlug,
    string FileName,
    long FileSizeBytes,
    DateTime CreatedAt,
    int? CreatedByUserId,
    string? CreatedByEmail,
    BackupKind Kind,
    int SchemaVersion,
    bool IsPinned,
    DateTime? OffsiteUploadedAt,
    string? OffsiteObjectKey);

/// <summary>
/// Logical, per-organisation snapshot service. Writes a ZIP containing one
/// JSON array per tenanted content table for the given organisation, plus a
/// <c>manifest.json</c> with metadata and schema version. Restore opens a
/// single transaction that deletes the organisation's existing rows in
/// FK-reverse order and re-inserts from the snapshot via
/// <c>jsonb_populate_recordset</c>, then resets each table's identity
/// sequence.
///
/// Auth and audit tables are excluded by design — see
/// <see cref="TenantTableCatalog"/> — so restoring a snapshot doesn't
/// tangle login state or forensic history. Restore refuses a snapshot
/// whose <c>SchemaVersion</c> doesn't match the current code; this is
/// strict on purpose so a pre-migration backup doesn't quietly drop
/// unknown columns post-migration.
/// </summary>
public sealed class PerTenantBackupService
{
    /// <summary>Version baked into every snapshot ZIP. Bump when a migration touches a tenanted table.</summary>
    /// <remarks>
    /// Bumped to 3 with #665, which added the Projects / Pipelines / Deliveries /
    /// Teams / Translator-memory tables and five cascade-orphaned children to the
    /// catalogue. A version-2 snapshot has no entries for them, so restoring one
    /// under this code would delete those rows and leave them deleted — the exact
    /// data loss #665 fixes. Refusing the old snapshot is the safe outcome.
    /// </remarks>
    public const int CurrentSchemaVersion = 3;

    /// <summary>
    /// Snapshot entry for the shared content-addressed source store. Not a
    /// tenanted table (no organization_id), so it isn't in
    /// <see cref="TenantTableCatalog"/>; the snapshot carries the org's
    /// referenced blobs explicitly and the restore upserts them first.
    /// </summary>
    private const string FileContentsEntryTable = "oe_file_contents";

    /// <summary>Filename suffix every per-tenant snapshot carries.</summary>
    public const string FileSuffix = ".tenant.zip";

    /// <summary>Manifest entry name inside the ZIP.</summary>
    public const string ManifestEntryName = "manifest.json";

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly StorageQuotaGuard _quotaGuard;
    private readonly OrganizationConfigService _orgConfig;
    private readonly ILogger<PerTenantBackupService> _logger;
    private readonly TimeProvider _clock;
    private readonly string _connectionString;
    private readonly string _backupsRoot;
    private readonly DeploymentIdentity? _deployment;

    public PerTenantBackupService(
        AppDbContext db,
        IOrganizationContext orgContext,
        StorageQuotaGuard quotaGuard,
        OrganizationConfigService orgConfig,
        IConfiguration configuration,
        ILogger<PerTenantBackupService> logger,
        TimeProvider clock,
        // Optional so the tests that build this service by hand keep compiling.
        // In production DI it is always the instance registered from configuration.
        BackupOptions? options = null,
        // Optional for the same reason. Stamped into every manifest and checked
        // on restore, so a snapshot another deployment took is refused (#1257).
        DeploymentIdentity? deployment = null)
    {
        _db = db;
        _orgContext = orgContext;
        _quotaGuard = quotaGuard;
        _orgConfig = orgConfig;
        _logger = logger;
        _clock = clock;
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is required for PerTenantBackupService.");
        _backupsRoot = (options ?? new BackupOptions()).Directory;
        _deployment = deployment;
    }

    /// <summary>Absolute path to the per-tenant subdirectory for the given org slug.</summary>
    public string DirectoryFor(string slug) => Path.Combine(_backupsRoot, "tenants", slug);

    /// <summary>
    /// Cross-org listing of per-tenant snapshots, optionally filtered to a
    /// single organisation. SiteAdmin-only — bypasses the EF query filter.
    /// </summary>
    public async Task<List<PerTenantBackupRow>> ListAsync(int? organizationId = null, CancellationToken ct = default)
    {
        _orgContext.RequireSiteAdmin();
        var q = _db.PerTenantBackups
            // Fence category 2 (SiteAdmin cross-org console): RequireSiteAdmin() above.
            // per_tenant_backups carries no query filter of its own — this bypass is for
            // the CreatedByUser include, since User IS filtered and this list spans orgs.
            // Do not drop it: without it the creator column blanks out for every other org.
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(b => b.Organization)
            .Include(b => b.CreatedByUser)
            .AsQueryable();
        if (organizationId is int orgId)
        {
            q = q.Where(b => b.OrganizationId == orgId);
        }
        return await q
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new PerTenantBackupRow(
                b.Id,
                b.OrganizationId,
                b.Organization!.Name,
                b.Organization.Slug,
                b.FileName,
                b.FileSizeBytes,
                b.CreatedAt,
                b.CreatedByUserId,
                b.CreatedByUser == null ? null : b.CreatedByUser.Email,
                b.Kind,
                b.SchemaVersion,
                b.IsPinned,
                b.OffsiteUploadedAt,
                b.OffsiteObjectKey))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The slugs of every organisation on this installation: the ones an
    /// off-site snapshot can be pulled back for, by the same rule
    /// <see cref="OffsiteBackupService.DownloadPerTenantAsync"/> applies. The
    /// off-site catalogue once derived this from local snapshot rows, which a
    /// whole-database restore leaves empty, and so called every organisation it
    /// had just brought back unknown (#1334). SiteAdmin-only; the
    /// <c>organizations</c> table has no query filter, so nothing is bypassed.
    /// </summary>
    public async Task<HashSet<string>> ListOrganizationSlugsAsync(CancellationToken ct = default)
    {
        _orgContext.RequireSiteAdmin();
        var slugs = await _db.Organizations
            .AsNoTracking()
            .Select(o => o.Slug)
            .ToListAsync(ct);
        return slugs.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Streams a per-tenant snapshot to the caller. Returns <c>null</c> when
    /// the row is missing or the file on disk has been removed out-of-band.
    /// </summary>
    public async Task<(PerTenantBackup Row, FileStream Stream)?> OpenForDownloadAsync(int id, CancellationToken ct = default)
    {
        _orgContext.RequireSiteAdmin();
        var row = await _db.PerTenantBackups
            .AsNoTracking()
            .Include(b => b.Organization)
            .FirstOrDefaultAsync(b => b.Id == id, ct);
        if (row is null) return null;
        var path = ResolveFilePath(row.Organization!.Slug, row.FileName);
        if (!File.Exists(path)) return null;
        return (row, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true));
    }

    /// <summary>
    /// Writes a new per-tenant snapshot for <paramref name="organizationId"/>.
    /// Scheduled invocations pass <see cref="BackupKind.Scheduled"/>;
    /// ad-hoc calls require a SiteAdmin caller. The snapshot is one
    /// <c>jsonb</c> array per tenanted content table inside a ZIP, plus a
    /// <c>manifest.json</c> with the schema version and table list.
    /// </summary>
    public async Task<PerTenantBackup> CreateAsync(int organizationId, BackupKind kind, CancellationToken ct)
    {
        // Authorisation hinges entirely on `kind`: AdHoc is the only
        // request-reachable path and requires a SiteAdmin caller, while
        // Scheduled both skips that gate AND backs up an arbitrary
        // `organizationId` (not derived from the org context). Scheduled MUST
        // therefore only ever be passed by the background BackupScheduler,
        // which runs without an authenticated principal — never wire a request
        // endpoint to forward a caller-controlled `kind`, or it would let any
        // user snapshot any org. (Verified: the only Scheduled call site is
        // BackupScheduler; every endpoint passes AdHoc.)
        if (kind == BackupKind.AdHoc) _orgContext.RequireSiteAdmin();
        var org = await _db.Organizations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == organizationId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["OrganizationId"] = $"Organization {organizationId} not found.",
            });

        var dir = DirectoryFor(org.Slug);
        Directory.CreateDirectory(dir);

        var timestamp = _clock.GetUtcNow().UtcDateTime;
        var label = kind == BackupKind.Scheduled ? "scheduled" : "adhoc";
        var fileName = $"{org.Slug}-{timestamp:yyyyMMddTHHmmssZ}-{label}{FileSuffix}";
        var targetPath = Path.Combine(dir, fileName);
        // Written under a staging name and renamed once complete, so a failed or
        // cancelled snapshot never leaves a file retention cannot see (#1269).
        var stagingPath = targetPath + ".partial";
        if (File.Exists(targetPath))
        {
            throw new IOException($"A per-tenant snapshot named '{fileName}' already exists.");
        }

        var sw = Stopwatch.StartNew();
        long size;
        var promoted = false;
        PerTenantBackup row;
        try
        {
            await using (var stream = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            await using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync(ct);
                // Every table is read from one snapshot (#1217). On autocommit
                // reads each SELECT sees the database as of its own moment, so a
                // parent and child committed between two reads land in the ZIP as
                // an orphan child, and the restore - which inserts in FK order
                // with immediate constraints - rolls back on it. REPEATABLE READ
                // fixes the snapshot at the first query; READ ONLY because this
                // transaction must never write.
                await using var readTx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
                await using (var readOnly = connection.CreateCommand())
                {
                    readOnly.Transaction = readTx;
                    readOnly.CommandText = "SET TRANSACTION READ ONLY";
                    await readOnly.ExecuteNonQueryAsync(ct);
                }
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

                var includedTables = new List<string>();
                foreach (var table in TenantTableCatalog.ContentTables)
                {
                    if (!TenantTableCatalog.TablesWithDirectOrgColumn.Contains(table)) continue;
                    var entry = zip.CreateEntry(table + ".json", CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await WriteTableAsJsonAsync(connection, table, organizationId, entryStream, ct);
                    includedTables.Add(table);
                }

                // The shared, content-addressed source-blob store. Captured as the
                // subset this org's files reference so the snapshot stays
                // self-contained (oe_file_contents has no organization_id column).
                {
                    var entry = zip.CreateEntry(FileContentsEntryTable + ".json", CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await WriteFileContentsAsJsonAsync(connection, organizationId, entryStream, ct);
                    includedTables.Add(FileContentsEntryTable);
                }

                var manifest = new
                {
                    schema_version = CurrentSchemaVersion,
                    organization_id = organizationId,
                    organization_slug = org.Slug,
                    organization_name = org.Name,
                    created_at = timestamp,
                    tables = includedTables,
                    deployment_id = _deployment?.Id,
                };
                var manifestEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, ManifestJson, ct);
                await readTx.CommitAsync(ct);
            }
            File.Move(stagingPath, targetPath);
            promoted = true;
            size = new FileInfo(targetPath).Length;
            sw.Stop();

            row = new PerTenantBackup
            {
                OrganizationId = organizationId,
                FileName = fileName,
                FileSizeBytes = size,
                CreatedAt = timestamp,
                CreatedByUserId = kind == BackupKind.AdHoc ? _orgContext.CurrentUserId : null,
                Kind = kind,
                SchemaVersion = CurrentSchemaVersion,
                IsPinned = false,
            };
            _db.PerTenantBackups.Add(row);
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            TryDelete(stagingPath);
            // Only a file this call put there: never an earlier snapshot of the same name.
            if (promoted) TryDelete(targetPath);
            throw;
        }

        _logger.LogInformation(
            "Per-tenant snapshot {FileName} written for org {OrgSlug} in {ElapsedMs} ms ({SizeBytes} bytes, kind={Kind}).",
            fileName, org.Slug, sw.ElapsedMilliseconds, size, kind);

        _quotaGuard.Invalidate(organizationId);
        await PruneRetentionAsync(organizationId, ct);
        return row;
    }

    /// <summary>
    /// Atomic restore. Reads the snapshot, verifies its schema version,
    /// then in a single transaction deletes the organisation's existing
    /// content rows in FK-reverse order and re-inserts from the snapshot
    /// in FK order via <c>jsonb_populate_recordset</c>. Identity sequences
    /// are realigned to MAX(id) afterwards so the next insert doesn't
    /// collide with re-imported rows.
    /// </summary>
    public async Task RestoreAsync(int id, CancellationToken ct = default)
    {
        _orgContext.RequireSiteAdmin();
        var row = await _db.PerTenantBackups
            .AsNoTracking()
            .Include(b => b.Organization)
            .FirstOrDefaultAsync(b => b.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["BackupId"] = "Per-tenant backup not found.",
            });
        if (row.SchemaVersion != CurrentSchemaVersion)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["SchemaVersion"] = $"Snapshot schema version {row.SchemaVersion} does not match current ({CurrentSchemaVersion}). Restore aborted.",
            });
        }

        var path = ResolveFilePath(row.Organization!.Slug, row.FileName);
        if (!File.Exists(path))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["BackupId"] = "Snapshot file is missing from the backups directory.",
            });
        }

        // Cross-operation guard (#370): take the shared backup/restore advisory
        // lock so this single-tenant restore can't run while a full pg_dump or
        // an in-place restore (which drops the whole public schema) is in flight.
        // Global maintenance mode is deliberately not entered here — a per-tenant
        // restore shouldn't 503 every other org — but the lock keeps it from
        // overlapping the destructive full-database operations.
        await using var coordination = await BackupCoordination.AcquireAsync(_connectionString, ct);

        var sw = Stopwatch.StartNew();
        await using var zipStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var manifestEntry = zip.GetEntry(ManifestEntryName)
            ?? throw new InvalidOperationException("Snapshot is missing manifest.json.");
        PerTenantBackupManifest manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<PerTenantBackupManifest>(manifestStream, ManifestJson, ct)
                ?? throw new InvalidOperationException("Snapshot manifest.json is empty.");
        }
        if (manifest.organization_id != row.OrganizationId)
        {
            throw new InvalidOperationException(
                $"Snapshot manifest organisation id {manifest.organization_id} does not match per_tenant_backups row {row.OrganizationId}.");
        }
        // Slugs and organisation ids repeat across deployments ("default", 1),
        // so the org check above passes for a neighbour's snapshot pulled from a
        // shared off-site bucket. Its rows would then point at another
        // deployment's users. A manifest without a stamp predates it and is let
        // through, as unstamped off-site dumps are.
        if (_deployment?.IsForeign(manifest.deployment_id) == true)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["BackupId"] = "This snapshot was taken by another AL Workbench installation that shares the off-site storage. Restoring it would bring in that installation's users and settings, so it has been refused.",
            });
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        // Delete in FK-reverse order so children go before parents.
        foreach (var table in TenantTableCatalog.ContentTables.Reverse()
                     .Where(TenantTableCatalog.TablesWithDirectOrgColumn.Contains))
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE organization_id = @org";
            cmd.Parameters.AddWithValue("@org", row.OrganizationId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Restore the shared source blobs FIRST (FK order: oe_module_files
        // references oe_file_contents.content_hash). ON CONFLICT DO NOTHING
        // because the blob may already exist — shared with another org, or left
        // behind by the delete phase above (which intentionally never touches
        // the shared store). It is never deleted on restore for the same reason.
        {
            var entry = zip.GetEntry(FileContentsEntryTable + ".json");
            if (entry is not null)
            {
                await using var entryStream = entry.Open();
                await foreach (var batchJson in PerTenantBackupJson.BatchJsonArrayAsync(entryStream, RestoreBatchBytes, ct))
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        $"INSERT INTO {FileContentsEntryTable} SELECT * FROM " +
                        $"jsonb_populate_recordset(NULL::{FileContentsEntryTable}, @rows::jsonb) " +
                        "ON CONFLICT (content_hash) DO NOTHING";
                    cmd.Parameters.AddWithValue("@rows", batchJson);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }
        }

        // Insert in FK order. jsonb_populate_recordset takes a row type that
        // PostgreSQL synthesises automatically for the table, so the JSON
        // keys map onto column names without us re-declaring the column list.
        //
        // Batching by serialised byte length keeps each @rows::jsonb cast
        // well under Postgres' 256 MB jsonb-value ceiling. Loading the
        // whole file then casting it as one blob is what crashed
        // blob-heavy tenants (oe_module_files in particular) before this
        // change.
        foreach (var table in TenantTableCatalog.ContentTables
                     .Where(TenantTableCatalog.TablesWithDirectOrgColumn.Contains))
        {
            var entry = zip.GetEntry(table + ".json");
            if (entry is null) continue;

            var insertedAny = false;
            await using (var entryStream = entry.Open())
            {
                await foreach (var batchJson in PerTenantBackupJson.BatchJsonArrayAsync(entryStream, RestoreBatchBytes, ct))
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        $"INSERT INTO {table} SELECT * FROM jsonb_populate_recordset(NULL::{table}, @rows::jsonb)";
                    cmd.Parameters.AddWithValue("@rows", batchJson);
                    await cmd.ExecuteNonQueryAsync(ct);
                    insertedAny = true;
                }
            }
            if (!insertedAny) continue;

            // Re-align identity sequence so the next INSERT doesn't collide
            // with restored row ids. pg_get_serial_sequence returns NULL
            // for tables without identity columns; we tolerate that.
            await using var resetCmd = connection.CreateCommand();
            resetCmd.Transaction = tx;
            resetCmd.CommandText = $"""
                SELECT setval(pg_get_serial_sequence('{table}', 'id'),
                              GREATEST((SELECT COALESCE(MAX(id), 0) FROM {table}), 1))
                WHERE pg_get_serial_sequence('{table}', 'id') IS NOT NULL
                """;
            await resetCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        sw.Stop();
        _logger.LogInformation(
            "Restored per-tenant snapshot {FileName} for org {OrgSlug} in {ElapsedMs} ms.",
            row.FileName, row.Organization.Slug, sw.ElapsedMilliseconds);
        _quotaGuard.Invalidate(row.OrganizationId);
        // The restore rewrote organization_settings, organization_assets and
        // organization_files underneath the cached config, which has no expiry
        // (#1218). Every other writer of those tables drops it the same way.
        _orgConfig.InvalidateCache(row.OrganizationId);
    }

    /// <summary>Pins or unpins a snapshot (exempts it from retention pruning).</summary>
    public async Task SetPinnedAsync(int id, bool pinned, CancellationToken ct = default)
    {
        _orgContext.RequireSiteAdmin();
        var row = await _db.PerTenantBackups
            .FirstOrDefaultAsync(b => b.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["BackupId"] = "Backup not found." });
        if (row.IsPinned == pinned) return;
        row.IsPinned = pinned;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes a snapshot file and its DB row. Pinned snapshots must be unpinned first.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        _orgContext.RequireSiteAdmin();
        var row = await _db.PerTenantBackups
            .Include(b => b.Organization)
            .FirstOrDefaultAsync(b => b.Id == id, ct);
        if (row is null) return;
        if (row.IsPinned)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["BackupId"] = "Unpin the snapshot before deleting it.",
            });
        }
        TryDeleteFile(row.Organization!.Slug, row.FileName);
        _db.PerTenantBackups.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Drops unpinned snapshots past the retention count for a single organisation.</summary>
    public async Task PruneRetentionAsync(int organizationId, CancellationToken ct = default)
    {
        var retention = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => (int?)s.PerTenantBackupRetentionCount)
            .FirstOrDefaultAsync(ct) ?? 30;
        if (retention < 1) retention = 1;

        var rows = await _db.PerTenantBackups
            // Retention pruning is driven by the backup scheduler; pinned to
            // b.OrganizationId == organizationId.
            .Include(b => b.Organization)
            .Where(b => b.OrganizationId == organizationId && !b.IsPinned)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
        if (rows.Count <= retention) return;

        foreach (var row in rows.Skip(retention))
        {
            TryDeleteFile(row.Organization!.Slug, row.FileName);
            _db.PerTenantBackups.Remove(row);
            _logger.LogInformation(
                "Pruned per-tenant snapshot {FileName} for org {OrgSlug} (retention={Retention}).",
                row.FileName, row.Organization.Slug, retention);
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Per-batch cap when restoring. Each batch turns into a single
    /// <c>jsonb_populate_recordset(@rows::jsonb)</c> call, so it must stay
    /// well below Postgres' 256 MB jsonb-value ceiling. 32 MB gives the
    /// row-side metadata plenty of headroom and keeps managed-heap pressure
    /// bounded.
    /// </summary>
    private const long RestoreBatchBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Streams <c>to_jsonb(t)</c> rows one at a time into
    /// <paramref name="destination"/>, framing them as a JSON array. Replaces
    /// the older <c>jsonb_agg</c> path that built a single jsonb on the
    /// server and tripped the 256 MB limit on blob-heavy tenants.
    /// </summary>
    private static async Task WriteTableAsJsonAsync(
        NpgsqlConnection connection,
        string table,
        int organizationId,
        Stream destination,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT to_jsonb(t)::text FROM {table} t WHERE t.organization_id = @org";
        cmd.Parameters.AddWithValue("@org", organizationId);
        await WriteRowsAsJsonAsync(cmd, destination, ct);
    }

    /// <summary>
    /// Writes the subset of the shared, content-addressed <c>oe_file_contents</c>
    /// store referenced by this org's <c>oe_module_files</c>. The table has no
    /// <c>organization_id</c> (it's cross-tenant shared), so the snapshot must
    /// carry the org's referenced blobs explicitly — otherwise a restore into a
    /// fresh database would violate the <c>content_hash</c> FK and lose source.
    /// </summary>
    private static async Task WriteFileContentsAsJsonAsync(
        NpgsqlConnection connection,
        int organizationId,
        Stream destination,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT to_jsonb(c)::text FROM oe_file_contents c WHERE EXISTS " +
            "(SELECT 1 FROM oe_module_files f WHERE f.content_hash = c.content_hash AND f.organization_id = @org)";
        cmd.Parameters.AddWithValue("@org", organizationId);
        await WriteRowsAsJsonAsync(cmd, destination, ct);
    }

    private static async Task WriteRowsAsJsonAsync(NpgsqlCommand cmd, Stream destination, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await destination.WriteAsync("["u8.ToArray(), ct);
        var first = true;
        while (await reader.ReadAsync(ct))
        {
            if (!first) await destination.WriteAsync(","u8.ToArray(), ct);
            first = false;
            var rowJson = reader.GetString(0);
            await destination.WriteAsync(Encoding.UTF8.GetBytes(rowJson), ct);
        }
        await destination.WriteAsync("]"u8.ToArray(), ct);
    }

    /// <summary>
    /// Resolves a <c>per_tenant_backups</c> row's <c>FileName</c> to an absolute
    /// path inside the org's snapshot directory, refusing any name that could
    /// escape it. Public so sibling services that combine a DB-sourced file name
    /// with <see cref="DirectoryFor"/> (e.g. <see cref="OffsiteBackupService"/>'s
    /// upload path) reuse the same guard instead of re-implementing it. See #480.
    /// </summary>
    public string ResolveFilePath(string slug, string fileName)
    {
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to handle suspicious snapshot file name: {fileName}");
        }
        return Path.Combine(DirectoryFor(slug), fileName);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove {Path} after a failed snapshot.", path);
        }
    }

    private void TryDeleteFile(string slug, string fileName)
    {
        try
        {
            var path = ResolveFilePath(slug, fileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete per-tenant snapshot {FileName}; row will be removed regardless.", fileName);
        }
    }

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    /// <summary>
    /// Reads and parses the <c>manifest.json</c> entry from a per-tenant
    /// snapshot ZIP. The caller (typically the off-site download flow)
    /// uses it to learn the snapshot's owning organisation and schema
    /// version before deciding whether it's safe to register locally.
    /// Throws <see cref="InvalidOperationException"/> if the manifest is
    /// missing or empty.
    /// </summary>
    public static async Task<PerTenantBackupManifest> ReadManifestAsync(Stream zipStream, CancellationToken ct)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = zip.GetEntry(ManifestEntryName)
            ?? throw new InvalidOperationException("Snapshot is missing manifest.json.");
        await using var manifestStream = manifestEntry.Open();
        return await JsonSerializer.DeserializeAsync<PerTenantBackupManifest>(manifestStream, ManifestJson, ct)
            ?? throw new InvalidOperationException("Snapshot manifest.json is empty.");
    }
}

/// <summary>
/// Shape of the <c>manifest.json</c> entry written into every per-tenant
/// snapshot ZIP. Public so off-site code can read manifests from
/// downloaded files without re-defining the contract.
/// </summary>
public sealed record PerTenantBackupManifest(
    int schema_version,
    int organization_id,
    string organization_slug,
    string organization_name,
    DateTime created_at,
    IReadOnlyList<string> tables,
    // Absent from snapshots taken before #1257.
    string? deployment_id = null);
