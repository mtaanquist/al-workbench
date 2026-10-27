using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Services.Organizations;

/// <summary>
/// Computes per-organisation database disk usage by combining
/// <c>pg_total_relation_size</c> and <c>pg_indexes_size</c> over the tables
/// enumerated in <see cref="TenantTableCatalog"/>, prorated by row count.
///
/// The result is an approximation: PostgreSQL does not track per-tenant
/// bytes natively. Tables with unusually wide rows for a single org can
/// over- or under-shoot reality. For the SiteAdmin storage view and the
/// quota guard this is accurate enough; if it becomes a billing dispute
/// surface, switch to schema-per-tenant or per-table TOAST inspection.
///
/// The Object Explorer fact tables are estimated a second time over: their
/// per-org row share comes from the org's share of <c>oe_modules</c> rather
/// than a count of the fact rows themselves, because counting them meant a
/// full scan of the largest tables in the schema on every sweep (#684). See
/// <c>EstimateModuleShareRowsAsync</c>.
/// </summary>
public sealed class DatabaseUsageService
{
    private readonly AppDbContext _db;
    private readonly SystemSettingsService _systemSettings;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<DatabaseUsageService> _logger;
    private readonly TimeProvider _clock;

    public DatabaseUsageService(
        AppDbContext db,
        SystemSettingsService systemSettings,
        IOrganizationContext orgContext,
        ILogger<DatabaseUsageService> logger,
        TimeProvider clock)
    {
        _db = db;
        _systemSettings = systemSettings;
        _orgContext = orgContext;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>Per-org usage row for the SiteAdmin storage page.</summary>
    public sealed record OrgUsageRow(
        int OrganizationId,
        string Name,
        string Slug,
        bool IsSystem,
        long LogicalBytes,
        long IndexBytes,
        long TotalBytes,
        long BillableBytes,
        int? EffectiveQuotaMb,
        int? OrgOverrideMb,
        int? SystemDefaultMb,
        decimal Multiplier,
        double? UsedFraction,
        // UTC instant the figures were computed. Null on a live computation
        // (ListAsync / GetForCurrentOrgAsync); set when read from a persisted
        // snapshot so the SiteAdmin page can show "updated N ago".
        DateTime? ComputedAt = null);

    /// <summary>
    /// Cross-organisation usage listing. SiteAdmin-only. The orgs table carries
    /// no tenant query filter, so the enumeration spans every organisation
    /// without a bypass; the caller is gated by the SiteAdmin role.
    /// </summary>
    public async Task<IReadOnlyList<OrgUsageRow>> ListAsync(CancellationToken ct)
    {
        var settings = await _systemSettings.GetViewAsync(ct);
        var multiplier = settings.IndexSizeMultiplier;
        var systemDefault = settings.DefaultStorageQuotaMb;

        // SiteAdmin-only surface: the only caller is /site-admin/backup-storage/storage,
        // gated by [Authorize(Roles = SiteAdminRole)].
        var orgs = await _db.Organizations
            .OrderBy(o => o.IsSystem ? 0 : 1).ThenBy(o => o.Name)
            .Select(o => new { o.Id, o.Name, o.Slug, o.IsSystem, o.StorageQuotaMb })
            .ToListAsync(ct);

        var tableSizes = await ReadTableSizesAsync(ct);
        var perOrgCounts = await ReadPerOrgRowCountsAsync(orgs.Select(o => o.Id).ToList(), tableSizes, ct);
        var oeSourceBytes = await ReadOeSourceLogicalBytesAsync(orgs.Select(o => o.Id).ToList(), ct);

        var rows = new List<OrgUsageRow>(orgs.Count);
        foreach (var org in orgs)
        {
            long logical = 0, index = 0;
            foreach (var table in TenantTableCatalog.AllTenantedTables)
            {
                if (!tableSizes.TryGetValue(table, out var size)) continue;
                if (size.TotalRows == 0) continue;
                if (!perOrgCounts.TryGetValue((org.Id, table), out var orgRows) || orgRows == 0) continue;

                var share = (double)orgRows / size.TotalRows;
                logical += (long)(size.LogicalBytes * share);
                index += (long)(size.IndexBytes * share);
            }
            if (oeSourceBytes.TryGetValue(org.Id, out var oeBytes)) logical += oeBytes;

            var total = logical + index;
            var billable = logical + (long)(index * (double)multiplier);
            var effectiveQuota = org.StorageQuotaMb ?? systemDefault;
            double? usedFraction = effectiveQuota is { } q && q > 0
                ? (double)billable / (q * 1024d * 1024d)
                : null;

            rows.Add(new OrgUsageRow(
                org.Id, org.Name, org.Slug, org.IsSystem,
                logical, index, total, billable,
                effectiveQuota, org.StorageQuotaMb, systemDefault,
                multiplier, usedFraction));
        }

        return rows;
    }

    /// <summary>Usage row for the current organisation (sidebar bar + dashboard tile).</summary>
    public async Task<OrgUsageRow?> GetForCurrentOrgAsync(CancellationToken ct)
    {
        var orgId = _orgContext.CurrentOrganizationId;
        if (orgId is null) return null;

        var settings = await _systemSettings.GetViewAsync(ct);
        // Pinned to o.Id == orgId.Value, taken from the authenticated principal.
        var org = await _db.Organizations
            .Where(o => o.Id == orgId.Value)
            .Select(o => new { o.Id, o.Name, o.Slug, o.IsSystem, o.StorageQuotaMb })
            .FirstOrDefaultAsync(ct);
        if (org is null) return null;

        var tableSizes = await ReadTableSizesAsync(ct);
        var perOrgCounts = await ReadPerOrgRowCountsAsync([orgId.Value], tableSizes, ct);
        var oeSourceBytes = await ReadOeSourceLogicalBytesAsync([orgId.Value], ct);

        long logical = 0, index = 0;
        foreach (var table in TenantTableCatalog.AllTenantedTables)
        {
            if (!tableSizes.TryGetValue(table, out var size)) continue;
            if (size.TotalRows == 0) continue;
            if (!perOrgCounts.TryGetValue((orgId.Value, table), out var orgRows) || orgRows == 0) continue;
            var share = (double)orgRows / size.TotalRows;
            logical += (long)(size.LogicalBytes * share);
            index += (long)(size.IndexBytes * share);
        }
        if (oeSourceBytes.TryGetValue(orgId.Value, out var oeBytes)) logical += oeBytes;

        var total = logical + index;
        var billable = logical + (long)(index * (double)settings.IndexSizeMultiplier);
        var effectiveQuota = org.StorageQuotaMb ?? settings.DefaultStorageQuotaMb;
        double? usedFraction = effectiveQuota is { } q && q > 0
            ? (double)billable / (q * 1024d * 1024d)
            : null;

        return new OrgUsageRow(
            org.Id, org.Name, org.Slug, org.IsSystem,
            logical, index, total, billable,
            effectiveQuota, org.StorageQuotaMb, settings.DefaultStorageQuotaMb,
            settings.IndexSizeMultiplier, usedFraction);
    }

    /// <summary>
    /// Recomputes every organisation's footprint and persists it to
    /// <c>organization_usage_snapshots</c>. Called on a schedule by
    /// <c>UsageSnapshotScheduler</c> so the per-navigation display surfaces
    /// (<c>StorageBar</c>, the SiteAdmin storage page) read a cheap indexed
    /// lookup instead of the expensive live <c>COUNT(*)</c> sweep.
    ///
    /// Runs off-request (no org context): writes go through raw SQL UPSERT so
    /// they neither trip the EF tenant filter nor need an audit principal.
    /// Snapshots for deleted orgs are reaped by the FK cascade, so we only
    /// ever insert/update rows for orgs that still exist.
    /// </summary>
    public async Task RecomputeSnapshotsAsync(CancellationToken ct)
    {
        var rows = await ListAsync(ct);
        var now = _clock.GetUtcNow().UtcDateTime;

        var connection = _db.Database.GetDbConnection();
        await EnsureOpenAsync(connection, ct);
        // One transaction for the whole sweep so a partial failure mid-loop
        // rolls back rather than leaving a mix of fresh and stale computed_at
        // across orgs — the snapshot set is read as a single point-in-time. #408
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var dbTransaction = transaction.GetDbTransaction();
        foreach (var row in rows)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = dbTransaction;
            cmd.CommandText = """
                INSERT INTO organization_usage_snapshots
                    (organization_id, logical_bytes, index_bytes, computed_at)
                VALUES (@org, @logical, @index, @at)
                ON CONFLICT (organization_id) DO UPDATE SET
                    logical_bytes = EXCLUDED.logical_bytes,
                    index_bytes = EXCLUDED.index_bytes,
                    computed_at = EXCLUDED.computed_at
                """;
            AddParam(cmd, "@org", row.OrganizationId);
            AddParam(cmd, "@logical", row.LogicalBytes);
            AddParam(cmd, "@index", row.IndexBytes);
            AddParam(cmd, "@at", now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "Recomputed storage usage snapshots for {OrgCount} organisation(s).", rows.Count);
    }

    /// <summary>
    /// Reads the persisted snapshot for the acting organisation, deriving
    /// billable size / quota / used-fraction from the *current* settings so a
    /// quota change applies before the next recompute. Returns
    /// <see langword="null"/> if no snapshot has been written yet (e.g. the
    /// first few seconds after a cold start, before the scheduler's first run).
    /// Cheap: a single primary-key lookup, no per-table counts.
    /// </summary>
    public async Task<OrgUsageRow?> GetSnapshotForCurrentOrgAsync(CancellationToken ct)
    {
        var orgId = _orgContext.CurrentOrganizationId;
        if (orgId is null) return null;

        var settings = await _systemSettings.GetViewAsync(ct);
        var connection = _db.Database.GetDbConnection();
        await EnsureOpenAsync(connection, ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT o.id, o.name, o.slug, o.is_system, o.storage_quota_mb,
                   s.logical_bytes, s.index_bytes, s.computed_at
            FROM organizations o
            JOIN organization_usage_snapshots s ON s.organization_id = o.id
            WHERE o.id = @org
            """;
        AddParam(cmd, "@org", orgId.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return MapRow(reader, settings.IndexSizeMultiplier, settings.DefaultStorageQuotaMb);
    }

    /// <summary>
    /// Cross-organisation snapshot listing for the SiteAdmin storage page.
    /// LEFT JOIN so an org with no snapshot yet still shows (as zero bytes,
    /// null <c>ComputedAt</c>). Refuses a caller who is not a SiteAdmin; reads raw SQL so the EF tenant
    /// filter doesn't apply (matching <see cref="ListAsync"/>'s cross-org reach).
    /// </summary>
    public async Task<IReadOnlyList<OrgUsageRow>> ListFromSnapshotsAsync(CancellationToken ct)
    {
        // Raw SQL across every organisation, so the page's SiteAdmin role is
        // not the only gate (fence category 2).
        _orgContext.RequireSiteAdmin();
        var settings = await _systemSettings.GetViewAsync(ct);
        var connection = _db.Database.GetDbConnection();
        await EnsureOpenAsync(connection, ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT o.id, o.name, o.slug, o.is_system, o.storage_quota_mb,
                   COALESCE(s.logical_bytes, 0), COALESCE(s.index_bytes, 0), s.computed_at
            FROM organizations o
            LEFT JOIN organization_usage_snapshots s ON s.organization_id = o.id
            ORDER BY o.is_system DESC, o.name
            """;
        var rows = new List<OrgUsageRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(MapRow(reader, settings.IndexSizeMultiplier, settings.DefaultStorageQuotaMb));
        }
        return rows;
    }

    /// <summary>
    /// Projects a snapshot result row (org columns + logical/index bytes +
    /// computed_at) into an <see cref="OrgUsageRow"/>, deriving billable size,
    /// effective quota and used fraction from the supplied settings. Column
    /// order matches the SELECTs in <see cref="GetSnapshotForCurrentOrgAsync"/>
    /// and <see cref="ListFromSnapshotsAsync"/>.
    /// </summary>
    private static OrgUsageRow MapRow(
        System.Data.Common.DbDataReader reader, decimal multiplier, int? systemDefault)
    {
        var id = reader.GetInt32(0);
        var name = reader.GetString(1);
        var slug = reader.GetString(2);
        var isSystem = reader.GetBoolean(3);
        int? orgOverride = reader.IsDBNull(4) ? null : reader.GetInt32(4);
        var logical = reader.GetInt64(5);
        var index = reader.GetInt64(6);
        DateTime? computedAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7);

        var total = logical + index;
        var billable = logical + (long)(index * (double)multiplier);
        var effectiveQuota = orgOverride ?? systemDefault;
        double? usedFraction = effectiveQuota is { } q && q > 0
            ? (double)billable / (q * 1024d * 1024d)
            : null;

        return new OrgUsageRow(
            id, name, slug, isSystem,
            logical, index, total, billable,
            effectiveQuota, orgOverride, systemDefault,
            multiplier, usedFraction, computedAt);
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// Persists a per-organisation quota override. Refuses a caller who is not a
    /// SiteAdmin. Setting
    /// the value to <see langword="null"/> reverts to the system default.
    /// </summary>
    public async Task SetOrgQuotaAsync(int organizationId, int? quotaMb, CancellationToken ct)
    {
        if (quotaMb is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quotaMb), "Quota must be non-negative.");
        }
        // Writes any organisation's row by id, so it checks for SiteAdmin itself
        // rather than leaning on the endpoint's role alone (fence category 2).
        _orgContext.RequireSiteAdmin();
        // Quota override, reached only from /site-admin/backup-storage/storage;
        // pinned to o.Id == organizationId.
        var org = await _db.Organizations
            .FirstOrDefaultAsync(o => o.Id == organizationId, ct)
            ?? throw new InvalidOperationException($"Organization {organizationId} not found.");
        org.StorageQuotaMb = quotaMb;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("SiteAdmin set storage quota for {OrgSlug} to {QuotaMb} MB.",
            org.Slug, quotaMb?.ToString() ?? "(default)");
    }

    private async Task<Dictionary<string, (long LogicalBytes, long IndexBytes, long TotalRows)>>
        ReadTableSizesAsync(CancellationToken ct)
    {
        // pg_class.reltuples is the planner's row estimate — fast but stale
        // until ANALYZE runs. Adequate for prorating disk bytes; the goal is
        // billing-relevant, not row-exact.
        var sql = """
            SELECT c.relname AS table_name,
                   pg_indexes_size(c.oid) AS index_bytes,
                   pg_total_relation_size(c.oid) - pg_indexes_size(c.oid) AS logical_bytes,
                   c.reltuples::bigint AS estimated_rows
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'r'
            """;

        var result = new Dictionary<string, (long, long, long)>();
        var connection = _db.Database.GetDbConnection();
        await EnsureOpenAsync(connection, ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var indexBytes = reader.GetInt64(1);
            var logicalBytes = reader.GetInt64(2);
            var rows = reader.GetInt64(3);
            result[name] = (Math.Max(0, logicalBytes), Math.Max(0, indexBytes), Math.Max(0, rows));
        }
        return result;
    }

    /// <summary>
    /// Per-org row counts for every catalogued table, used to prorate each
    /// table's on-disk size. Exact <c>COUNT(*)</c> everywhere except the Object
    /// Explorer fact tables in
    /// <see cref="TenantTableCatalog.ModuleShareEstimatedTables"/>, whose share
    /// is estimated from <c>oe_modules</c> instead — see
    /// <see cref="EstimateModuleShareRowsAsync"/>.
    /// </summary>
    private async Task<Dictionary<(int OrgId, string Table), long>> ReadPerOrgRowCountsAsync(
        IReadOnlyList<int> orgIds,
        IReadOnlyDictionary<string, (long LogicalBytes, long IndexBytes, long TotalRows)> tableSizes,
        CancellationToken ct)
    {
        var counts = new Dictionary<(int, string), long>();
        if (orgIds.Count == 0) return counts;

        var connection = _db.Database.GetDbConnection();
        await EnsureOpenAsync(connection, ct);

        // Direct organization_id tables, minus the OE fact tables we estimate.
        foreach (var table in TenantTableCatalog.AllTenantedTables
                     .Where(TenantTableCatalog.TablesWithDirectOrgColumn.Contains)
                     .Where(t => !TenantTableCatalog.ModuleShareEstimatedTables.Contains(t)))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT organization_id, COUNT(*) FROM {table} "
                              + "WHERE organization_id = ANY(@ids) GROUP BY organization_id";
            var param = cmd.CreateParameter();
            param.ParameterName = "@ids";
            ((NpgsqlParameter)param).NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer;
            param.Value = orgIds.ToArray();
            cmd.Parameters.Add(param);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (await reader.IsDBNullAsync(0, ct)) continue;
                counts[(reader.GetInt32(0), table)] = reader.GetInt64(1);
            }
        }

        // Auth-adjacent tables joined through users.
        foreach (var (table, userIdColumn) in TenantTableCatalog.TablesLinkedThroughUser)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT u.organization_id, COUNT(*) FROM {table} t "
                              + $"JOIN users u ON u.id = t.{userIdColumn} "
                              + "WHERE u.organization_id = ANY(@ids) GROUP BY u.organization_id";
            var param = cmd.CreateParameter();
            param.ParameterName = "@ids";
            ((NpgsqlParameter)param).NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer;
            param.Value = orgIds.ToArray();
            cmd.Parameters.Add(param);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (await reader.IsDBNullAsync(0, ct)) continue;
                counts[(reader.GetInt32(0), table)] = reader.GetInt64(1);
            }
        }

        await EstimateModuleShareRowsAsync(orgIds, tableSizes, counts, ct);

        return counts;
    }

    /// <summary>
    /// Fills in *estimated* row counts for the Object Explorer fact tables
    /// (<see cref="TenantTableCatalog.ModuleShareEstimatedTables"/>) instead of
    /// counting them.
    ///
    /// Every fact row belongs to exactly one <c>oe_modules</c> row, and
    /// <c>oe_modules</c> is small and org-scoped, so an org's share of the
    /// modules is a defensible stand-in for its share of the facts. We count
    /// modules once (a single scan of a small table), then hand each fact table
    /// a synthetic row count of <c>share x planner-estimated total rows</c>, so
    /// the caller's existing proration arithmetic — and therefore the figure the
    /// quota guard compares against — is unchanged in shape.
    ///
    /// The alternative was a <c>COUNT(*)</c> per fact table on every sweep: on a
    /// loaded catalogue that is tens of millions of index tuples read four times
    /// an hour, and it evicts Object Explorer's hot pages from the buffer cache
    /// each time (#684). The usage figure drives a storage bar and a write
    /// guard, not an invoice, and the whole computation is already an estimate.
    ///
    /// An org with no modules gets no entry (and so contributes nothing), which
    /// is right: no modules means no fact rows.
    /// </summary>
    private async Task EstimateModuleShareRowsAsync(
        IReadOnlyList<int> orgIds,
        IReadOnlyDictionary<string, (long LogicalBytes, long IndexBytes, long TotalRows)> tableSizes,
        Dictionary<(int, string), long> counts,
        CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();
        await EnsureOpenAsync(connection, ct);

        // Whole-table group-by (no org predicate): we need the global total to
        // form a share, and the basis table is small enough that scanning it in
        // full is cheaper than a second query for the total.
        var modulesPerOrg = new Dictionary<int, long>();
        long totalModules = 0;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                $"SELECT organization_id, COUNT(*) FROM {TenantTableCatalog.ModuleShareBasisTable} "
                + "GROUP BY organization_id";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (await reader.IsDBNullAsync(0, ct)) continue;
                var rows = reader.GetInt64(1);
                modulesPerOrg[reader.GetInt32(0)] = rows;
                totalModules += rows;
            }
        }
        if (totalModules == 0) return;

        foreach (var orgId in orgIds)
        {
            if (!modulesPerOrg.TryGetValue(orgId, out var orgModules) || orgModules == 0) continue;
            var share = (double)orgModules / totalModules;
            foreach (var table in TenantTableCatalog.ModuleShareEstimatedTables)
            {
                if (!tableSizes.TryGetValue(table, out var size) || size.TotalRows == 0) continue;
                counts[(orgId, table)] = (long)Math.Round(share * size.TotalRows);
            }
        }
    }

    /// <summary>
    /// Per-org logical size of Object Explorer source text, read from the
    /// denormalised <c>oe_releases.source_content_length</c> (the full footprint
    /// each org imported, duplicates included). Object Explorer source blobs are
    /// physically deduplicated into the shared, org-less <c>oe_file_contents</c>
    /// store, which therefore can't be prorated by row share. Attributing the
    /// logical size keeps each org's bill identical to the pre-dedup world (when
    /// content was stored per-org); the platform simply reaps the physical
    /// saving. The dedup'd <c>oe_file_contents</c> heap and its indexes are
    /// intentionally unattributed platform overhead.
    /// </summary>
    private async Task<Dictionary<int, long>> ReadOeSourceLogicalBytesAsync(
        IReadOnlyList<int> orgIds, CancellationToken ct)
    {
        var result = new Dictionary<int, long>();
        if (orgIds.Count == 0) return result;

        var connection = _db.Database.GetDbConnection();
        await EnsureOpenAsync(connection, ct);
        await using var cmd = connection.CreateCommand();
        // No deleted_at filter: soft-deleted releases still occupy storage until
        // hard-purged, matching the prior per-row proration which counted them.
        cmd.CommandText =
            "SELECT organization_id, COALESCE(SUM(source_content_length), 0) FROM oe_releases "
            + "WHERE organization_id = ANY(@ids) GROUP BY organization_id";
        var param = cmd.CreateParameter();
        param.ParameterName = "@ids";
        ((NpgsqlParameter)param).NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer;
        param.Value = orgIds.ToArray();
        cmd.Parameters.Add(param);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (await reader.IsDBNullAsync(0, ct)) continue;
            result[reader.GetInt32(0)] = reader.GetInt64(1);
        }
        return result;
    }

    private static async Task EnsureOpenAsync(System.Data.Common.DbConnection connection, CancellationToken ct)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }
    }
}
