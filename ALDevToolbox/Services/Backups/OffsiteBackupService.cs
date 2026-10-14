using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Offsite;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Services.Backups;

/// <summary>
/// Result of <see cref="OffsiteBackupService.TestConnectionAsync"/>. The
/// settings form renders the message inline so SiteAdmins can verify the
/// credentials before flipping the schedule on. Shared with the storage
/// providers in <see cref="Offsite"/>, which produce backend-specific messages.
/// </summary>
public sealed record OffsiteTestResult(bool Success, string Message);

/// <summary>
/// One object returned by <see cref="OffsiteBackupService.ListAsync"/>.
/// The DR-restore catalogue on <c>/site-admin/backups</c> renders these
/// directly. <c>FileName</c> is the catalogue's natural identity — what
/// the row will be stored under locally; <c>Key</c> includes the bucket
/// prefix and is the value the download endpoint takes back in.
/// <c>FromAnotherDeployment</c> is set when the object carries another
/// installation's stamp: the download refuses it, unless this is a fresh
/// installation recovering that one (#1322).
/// </summary>
public sealed record OffsiteObjectInfo(string Key, string FileName, long Size, DateTime LastModified, bool FromAnotherDeployment = false);

/// <summary>
/// One per-tenant snapshot object returned by
/// <see cref="OffsiteBackupService.ListPerTenantAsync"/>. Carries the slug
/// parsed out of the object key so the catalogue UI can group rows by
/// organisation without an extra round-trip. <c>FromAnotherDeployment</c> is
/// set when the object carries another deployment's stamp; the download and
/// the restore both refuse such a snapshot (#1257).
/// </summary>
public sealed record OffsitePerTenantObjectInfo(
    string Key, string Slug, string FileName, long Size, DateTime LastModified, bool FromAnotherDeployment = false);

/// <summary>
/// Uploads full-database pg_dump backups and per-tenant snapshot ZIPs to
/// the configured off-site object store and prunes objects older than
/// <c>OffsiteRetentionDays</c>. Whole-DB dumps live at
/// <c>&lt;prefix&gt;&lt;filename&gt;</c>; per-tenant snapshots namespace
/// under <c>&lt;prefix&gt;tenants/&lt;slug&gt;/&lt;filename&gt;</c> so the
/// two catalogues stay separable and a DR restore can pull either back
/// down independently. After a whole-deployment loss, the per-tenant
/// copies are what keeps the "restore one org to yesterday" surface alive.
///
/// <para>
/// The backend (S3-compatible or Azure Blob) is chosen per request from the
/// resolved settings via <see cref="IOffsiteStorageProviderFactory"/>. This
/// service owns all orchestration — object-key shapes, <c>.partial</c>
/// staging, the deployment-id fingerprint gate, retention filtering, and DB
/// bookkeeping — so both backends behave identically; only the raw transport
/// lives behind <see cref="IOffsiteStorageProvider"/>.
/// </para>
/// </summary>
public sealed class OffsiteBackupService
{
    /// <summary>Sub-prefix under the configured bucket prefix where per-tenant snapshots live.</summary>
    public const string PerTenantKeyPrefix = "tenants/";

    /// <summary>
    /// User-metadata key stamped on whole-DB dumps so a restore can verify the
    /// dump came from this deployment rather than a neighbour sharing the
    /// bucket. Providers normalise the key to their backend's rules (S3 sends
    /// it as <c>x-amz-meta-deployment-id</c>; Azure stores <c>deploymentid</c>)
    /// and map it back on read, so this canonical form is all the service sees.
    /// </summary>
    public const string DeploymentMetadataKey = "deployment-id";

    /// <summary>
    /// Number of most-recent whole-DB dumps the off-site prune keeps regardless
    /// of age. Off-site has no pin concept (unlike local backups, where
    /// DownloadAsync auto-pins a freshly-pulled DR snapshot), so without this an
    /// operator's most recent disaster-recovery dump could be deleted by the next
    /// scheduler tick mid-restore. See issue #380.
    /// </summary>
    private const int PruneKeepRecent = 3;

    /// <summary>How many object stamps are read at once.</summary>
    private const int StampReadConcurrency = 8;

    private readonly AppDbContext _db;
    private readonly SystemSettingsService _systemSettings;
    private readonly BackupService _backups;
    private readonly PerTenantBackupService _perTenantBackups;
    private readonly IOffsiteStorageProviderFactory _providerFactory;
    private readonly ILogger<OffsiteBackupService> _logger;
    private readonly TimeProvider _clock;
    private readonly DeploymentIdentity _deployment;

    public OffsiteBackupService(
        AppDbContext db,
        SystemSettingsService systemSettings,
        BackupService backups,
        PerTenantBackupService perTenantBackups,
        IOffsiteStorageProviderFactory providerFactory,
        ILogger<OffsiteBackupService> logger,
        TimeProvider clock,
        DeploymentIdentity deployment)
    {
        _db = db;
        _systemSettings = systemSettings;
        _backups = backups;
        _perTenantBackups = perTenantBackups;
        _providerFactory = providerFactory;
        _logger = logger;
        _clock = clock;
        _deployment = deployment;
    }

    /// <summary>Reachability + auth check against the configured bucket/container. Returns a structured result for the UI to render inline.</summary>
    public async Task<OffsiteTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null)
        {
            return new OffsiteTestResult(false, "Off-site backup is not fully configured. Save the form with credentials and a bucket first.");
        }
        var provider = _providerFactory.Create(settings);
        try
        {
            return await provider.TestConnectionAsync(ct);
        }
        finally
        {
            DisposeProvider(provider);
        }
    }

    /// <summary>
    /// Uploads a single backup row's file to the configured store and stamps
    /// the row with <c>OffsiteUploadedAt</c> + <c>OffsiteObjectKey</c>. No-op
    /// when off-site is disabled or the file is missing locally.
    /// </summary>
    public async Task<string?> UploadAsync(int backupId, CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null) return null;

        var row = await _db.Backups.FirstOrDefaultAsync(b => b.Id == backupId, ct);
        if (row is null) return null;

        // Route the DB-sourced file name through the same traversal guard the
        // local backup service uses, so a tampered backups.file_name can't make
        // the upload read an arbitrary app-readable file (e.g. the Data
        // Protection key ring) and exfiltrate it off-site. See #480.
        var path = _backups.ResolveFilePath(row.FileName);
        if (!File.Exists(path))
        {
            _logger.LogWarning("Refusing to upload {FileName}: local file missing.", row.FileName);
            return null;
        }

        var objectKey = BuildObjectKey(settings.Prefix, row.FileName);
        var provider = _providerFactory.Create(settings);
        try
        {
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
            {
                // Fingerprint the dump with this deployment's id so a later restore
                // can refuse a neighbour deployment's dump found under the same prefix.
                var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [DeploymentMetadataKey] = _deployment.Id };
                await provider.UploadAsync(objectKey, stream, "application/octet-stream", metadata, ct);
            }

            row.OffsiteUploadedAt = _clock.GetUtcNow().UtcDateTime;
            row.OffsiteObjectKey = objectKey;
            await SaveOrCleanupOrphanAsync(provider, objectKey, ct);
        }
        finally
        {
            DisposeProvider(provider);
        }

        _logger.LogInformation(
            "Uploaded backup {FileName} to off-site object {Bucket}/{Key}.",
            row.FileName, settings.Bucket, objectKey);
        return objectKey;
    }

    /// <summary>
    /// Uploads a per-tenant snapshot ZIP to
    /// <c>&lt;prefix&gt;tenants/&lt;slug&gt;/&lt;filename&gt;</c> and stamps
    /// the row with <c>OffsiteUploadedAt</c> + <c>OffsiteObjectKey</c>. No-op
    /// when off-site is disabled, the row is missing, or the local ZIP has
    /// been removed out-of-band.
    /// </summary>
    public async Task<string?> UploadPerTenantAsync(int perTenantBackupId, CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null) return null;

        var row = await _db.PerTenantBackups
            .Include(b => b.Organization)
            .FirstOrDefaultAsync(b => b.Id == perTenantBackupId, ct);
        if (row is null || row.Organization is null) return null;

        // Same traversal guard as the whole-DB path (#480): refuse a tampered
        // per_tenant_backups.file_name before combining it with the org's
        // snapshot directory.
        var path = _perTenantBackups.ResolveFilePath(row.Organization.Slug, row.FileName);
        if (!File.Exists(path))
        {
            _logger.LogWarning(
                "Refusing to upload per-tenant snapshot {FileName} for {Slug}: local file missing.",
                row.FileName, row.Organization.Slug);
            return null;
        }

        var objectKey = BuildPerTenantObjectKey(settings.Prefix, row.Organization.Slug, row.FileName);
        var provider = _providerFactory.Create(settings);
        try
        {
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
            {
                // Stamped like the whole-database dumps. The manifest names the
                // owning org, but slugs and org ids repeat across deployments
                // sharing a bucket, so it cannot tell a neighbour's snapshot
                // from ours (#1257). The manifest carries the same stamp.
                var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [DeploymentMetadataKey] = _deployment.Id };
                await provider.UploadAsync(objectKey, stream, "application/zip", metadata, ct);
            }

            row.OffsiteUploadedAt = _clock.GetUtcNow().UtcDateTime;
            row.OffsiteObjectKey = objectKey;
            await SaveOrCleanupOrphanAsync(provider, objectKey, ct);
        }
        finally
        {
            DisposeProvider(provider);
        }

        _logger.LogInformation(
            "Uploaded per-tenant snapshot {FileName} for {Slug} to off-site object {Bucket}/{Key}.",
            row.FileName, row.Organization.Slug, settings.Bucket, objectKey);
        return objectKey;
    }

    /// <summary>
    /// Lists the newest <paramref name="maxObjects"/> dump-shaped objects under
    /// the configured prefix, newest first. Drives the off-site catalogue on
    /// <c>/site-admin/backups</c>; returns an empty list when off-site isn't
    /// configured rather than throwing, so the page renders the "not
    /// configured" hint cleanly. With <paramref name="flagOtherDeployments"/>
    /// the stamps of the rows returned are read to set
    /// <see cref="OffsiteObjectInfo.FromAnotherDeployment"/>.
    /// </summary>
    /// <param name="maxObjects">Upper bound on results returned. The whole
    /// prefix is listed and sorted first, so the cut keeps the newest (#1333);
    /// a backend listing comes back in key order, not date order.</param>
    public async Task<IReadOnlyList<OffsiteObjectInfo>> ListAsync(int maxObjects, bool flagOtherDeployments, CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null) return Array.Empty<OffsiteObjectInfo>();
        if (maxObjects < 1) maxObjects = 1;

        var prefix = settings.Prefix ?? string.Empty;
        var provider = _providerFactory.Create(settings);
        try
        {
            // Dumps sit directly under the prefix; stepping over the per-tenant
            // subtree keeps the listing to the dumps alone.
            var objects = await provider.ListAsync(
                prefix.Length == 0 ? string.Empty : EnsureTrailingSlash(prefix), topLevelOnly: true, ct);

            var results = new List<OffsiteObjectInfo>();
            foreach (var obj in objects)
            {
                if (!IsWholeDbDumpKey(obj.Key, prefix, out var fileName)) continue;
                results.Add(new OffsiteObjectInfo(obj.Key, fileName, obj.Size, obj.LastModifiedUtc));
            }
            results = results
                .OrderByDescending(r => r.LastModified)
                .Take(maxObjects)
                .ToList();

            // Flag another installation's dumps, so the catalogue can offer the
            // recovery path for them instead of a download that will be refused
            // (#1322). Only the rows shown are read.
            if (flagOtherDeployments && _deployment.IsPersistent)
            {
                var owners = await ReadOwnersAsync(provider, results.Select(r => r.Key), ct);
                for (var i = 0; i < results.Count; i++)
                {
                    if (owners[results[i].Key] == OffsiteOwner.Foreign)
                    {
                        results[i] = results[i] with { FromAnotherDeployment = true };
                    }
                }
            }
            return results;
        }
        finally
        {
            DisposeProvider(provider);
        }
    }

    /// <summary>
    /// Lists the newest <paramref name="maxPerOrganization"/> per-tenant
    /// snapshots of each organisation under
    /// <c>&lt;prefix&gt;tenants/&lt;slug&gt;/</c>, grouped by organisation and
    /// newest first within each. Drives the off-site catalogue on
    /// <c>/site-admin/tenant-backups</c>; returns an empty list when off-site
    /// isn't configured. With <paramref name="flagOtherDeployments"/> the
    /// stamps of the rows returned are read to set
    /// <see cref="OffsitePerTenantObjectInfo.FromAnotherDeployment"/>.
    /// </summary>
    /// <param name="maxPerOrganization">How many snapshots to keep per
    /// organisation. The cut is per organisation and after sorting by date: a
    /// single overall cap over a key-ordered listing kept the oldest snapshots
    /// of the alphabetically first organisations and hid everyone else (#1333).</param>
    public async Task<IReadOnlyList<OffsitePerTenantObjectInfo>> ListPerTenantAsync(int maxPerOrganization, bool flagOtherDeployments, CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null) return Array.Empty<OffsitePerTenantObjectInfo>();
        if (maxPerOrganization < 1) maxPerOrganization = 1;

        var prefix = settings.Prefix ?? string.Empty;
        var tenantPrefix = (prefix.Length == 0 ? string.Empty : EnsureTrailingSlash(prefix)) + PerTenantKeyPrefix;
        var provider = _providerFactory.Create(settings);
        try
        {
            var parsed = new List<OffsitePerTenantObjectInfo>();
            foreach (var obj in await provider.ListAsync(tenantPrefix, topLevelOnly: false, ct))
            {
                // Expected shape: "<slug>/<filename>.tenant.zip". Reject
                // anything with extra path segments or the wrong suffix.
                if (!TryParsePerTenantKey(obj.Key, tenantPrefix, out var slug, out var fileName)) continue;
                parsed.Add(new OffsitePerTenantObjectInfo(
                    obj.Key, slug, fileName, obj.Size, obj.LastModifiedUtc));
            }
            var results = parsed
                .GroupBy(r => r.Slug, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .SelectMany(g => g.OrderByDescending(r => r.LastModified).Take(maxPerOrganization))
                .ToList();

            // Flag a neighbour deployment's snapshots, so the catalogue can say
            // so instead of offering a download that will be refused (#1257).
            // One small request per object, so only the rows shown are read and
            // only the page that shows the flag asks for it.
            if (flagOtherDeployments && _deployment.IsPersistent)
            {
                var owners = await ReadOwnersAsync(provider, results.Select(r => r.Key), ct);
                for (var i = 0; i < results.Count; i++)
                {
                    if (owners[results[i].Key] == OffsiteOwner.Foreign)
                    {
                        results[i] = results[i] with { FromAnotherDeployment = true };
                    }
                }
            }
            return results;
        }
        finally
        {
            DisposeProvider(provider);
        }
    }

    /// <summary>
    /// The file name of the whole-database dump at <paramref name="objectKey"/>,
    /// or <see langword="null"/> when the key is not a dump directly under the
    /// configured prefix or no such object exists. The download endpoint checks
    /// a submitted key with this rather than against a listing, which a cap
    /// could cut the key out of (#1333).
    /// </summary>
    public async Task<string?> FindDumpAsync(string objectKey, CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null) return null;
        var prefix = settings.Prefix ?? string.Empty;
        var listPrefix = prefix.Length == 0 ? string.Empty : EnsureTrailingSlash(prefix);
        if (!objectKey.StartsWith(listPrefix, StringComparison.Ordinal)
            || !IsWholeDbDumpKey(objectKey, prefix, out var fileName))
        {
            return null;
        }
        return await ExistsAsync(settings, objectKey, ct) ? fileName : null;
    }

    /// <summary>
    /// The file name of the per-tenant snapshot at <paramref name="objectKey"/>,
    /// or <see langword="null"/> when the key is not shaped
    /// <c>&lt;prefix&gt;tenants/&lt;slug&gt;/&lt;name&gt;.tenant.zip</c> or no
    /// such object exists. The per-tenant twin of <see cref="FindDumpAsync"/>.
    /// </summary>
    public async Task<string?> FindPerTenantSnapshotAsync(string objectKey, CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null) return null;
        var prefix = settings.Prefix ?? string.Empty;
        var tenantPrefix = (prefix.Length == 0 ? string.Empty : EnsureTrailingSlash(prefix)) + PerTenantKeyPrefix;
        if (!TryParsePerTenantKey(objectKey, tenantPrefix, out _, out var fileName)) return null;
        return await ExistsAsync(settings, objectKey, ct) ? fileName : null;
    }

    private async Task<bool> ExistsAsync(ResolvedOffsiteSettings settings, string objectKey, CancellationToken ct)
    {
        var provider = _providerFactory.Create(settings);
        try
        {
            await provider.GetMetadataAsync(objectKey, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Neither SDK has a cheaper "does it exist" than a metadata read, and
            // both report a missing object by throwing.
            _logger.LogInformation(ex, "Off-site object {Key} could not be read; treating it as missing.", objectKey);
            return false;
        }
        finally
        {
            DisposeProvider(provider);
        }
    }

    /// <summary>
    /// True while this installation is fresh enough to take over another
    /// installation's off-site backups in a disaster recovery (#1322): it holds
    /// no organisation besides the system one, and at most one user (the
    /// bootstrap admin). Past that point a dump stamped by another
    /// installation is refused, because restoring it would overwrite real data
    /// with a neighbour's.
    ///
    /// <para>
    /// The user count is read through the normal query filter, so it counts the
    /// caller's own organisation. That is enough: when no other organisation
    /// exists, every user belongs to the system org, which is the SiteAdmin
    /// caller's. A single-tenant installation has only the system org, so the
    /// user count is what tells a used one from a fresh one. Off a request (the
    /// download worker) the filter matches no org and only the organisation
    /// check bites; the endpoint has already run the full check by then.
    /// </para>
    /// </summary>
    public async Task<bool> CanRecoverAnotherInstallationAsync(CancellationToken ct)
    {
        if (!_deployment.IsPersistent) return false;
        if (await _db.Organizations.AsNoTracking().AnyAsync(o => !o.IsSystem, ct)) return false;
        return await _db.Users.AsNoTracking().CountAsync(ct) <= 1;
    }

    /// <summary>
    /// Downloads a single object from the store into the local backups
    /// directory and inserts a <c>backups</c> row pointing at it so the
    /// existing Restore button can take over. Reports byte-level progress
    /// via <paramref name="progress"/> so the SiteAdmin page can render a
    /// progress bar.
    ///
    /// <para>
    /// Refuses to overwrite an existing local file with the same name —
    /// the caller (a worker job) surfaces that as a Failed status rather
    /// than silently clobbering a row that may already be on disk. The
    /// downloaded file is staged under <c>&lt;name&gt;.partial</c> and
    /// renamed atomically on completion so a crash mid-download doesn't
    /// leave a half-written dump masquerading as a valid backup.
    /// </para>
    ///
    /// <para>
    /// A dump stamped by another installation is refused, unless
    /// <paramref name="recoverFromAnotherInstallation"/> is set and this
    /// installation is still fresh (<see cref="CanRecoverAnotherInstallationAsync"/>):
    /// that is a host rebuilt after losing the one that made the dump. Its id
    /// is then recorded as a former id of this installation
    /// (<see cref="DeploymentIdentity.Trust"/>), so its per-tenant snapshots
    /// can be pulled after the restore and retention prunes its objects (#1322).
    /// </para>
    /// </summary>
    /// <returns>The id of the inserted <c>backups</c> row.</returns>
    public async Task<int> DownloadAsync(
        string objectKey,
        IProgress<(long BytesDownloaded, long? TotalBytes)>? progress,
        bool recoverFromAnotherInstallation,
        CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct)
            ?? throw new InvalidOperationException("Off-site backup is not configured.");

        var fileName = StripPrefix(objectKey, settings.Prefix ?? string.Empty);
        if (string.IsNullOrEmpty(fileName)
            || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to download object with suspicious leaf name: {objectKey}");
        }
        if (!fileName.EndsWith(BackupService.BackupFileSuffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to download non-dump object: {objectKey}");
        }

        Directory.CreateDirectory(_backups.BackupsDirectory);
        var finalPath = Path.Combine(_backups.BackupsDirectory, fileName);
        var stagingPath = finalPath + ".partial";

        if (File.Exists(finalPath))
        {
            throw new InvalidOperationException(
                $"A local backup named '{fileName}' already exists. Delete or rename it before re-importing the off-site copy.");
        }
        if (File.Exists(stagingPath))
        {
            // Stale staging file from a prior aborted attempt. Safe to remove
            // because *.partial is never linked to a backups row.
            File.Delete(stagingPath);
        }

        var provider = _providerFactory.Create(settings);
        try
        {
            await using var download = await provider.OpenReadAsync(objectKey, ct);

            // Provenance: refuse a dump another deployment stamped. A missing stamp
            // is treated as legacy (uploaded before fingerprinting existed) and
            // allowed with a warning; enforcement is skipped when our own id is
            // ephemeral, since it would otherwise spuriously reject after a restart.
            download.Metadata.TryGetValue(DeploymentMetadataKey, out var stampedDeployment);
            if (_deployment.IsForeign(stampedDeployment))
            {
                if (!recoverFromAnotherInstallation)
                {
                    throw new InvalidOperationException(
                        $"Refusing to import '{fileName}': it was created by a different deployment " +
                        "(deployment-id mismatch). Restoring it would overwrite this database with " +
                        "another deployment's data.");
                }
                if (!await CanRecoverAnotherInstallationAsync(ct))
                {
                    throw new InvalidOperationException(
                        $"Refusing to recover from '{fileName}': it was made by another installation, and " +
                        "this one already has organisations or users of its own. Recovery is only offered " +
                        "on a fresh installation.");
                }
                // Recorded before the body is fetched: a download that fails part
                // way is retried, and the retry should not need the override again.
                _deployment.Trust(stampedDeployment!);
                _logger.LogWarning(
                    "Recovering from off-site object {Key}, made by another deployment; that deployment's off-site backups are now treated as this one's.",
                    objectKey);
            }
            if (string.IsNullOrEmpty(stampedDeployment))
            {
                _logger.LogWarning(
                    "Off-site object {Key} carries no deployment-id stamp; importing it anyway (legacy upload). " +
                    "Verify it belongs to this deployment before restoring.", objectKey);
            }

            await StreamToStagingAsync(download.Body, download.ContentLength, stagingPath, progress, ct);
        }
        finally
        {
            DisposeProvider(provider);
        }

        File.Move(stagingPath, finalPath);
        var size = new FileInfo(finalPath).Length;

        // Best-effort timestamp parse from our own filename shape:
        // aldevtoolbox-yyyyMMddTHHmmss[fff]Z-(scheduled|adhoc).dump
        var createdAt = TryParseTimestamp(fileName) ?? _clock.GetUtcNow().UtcDateTime;
        var row = new Backup
        {
            FileName = fileName,
            FileSizeBytes = size,
            CreatedAt = createdAt,
            // Downloaded files are operator-driven, even when the original
            // upload was scheduled. Marking AdHoc keeps the scheduled-backup
            // "latest scheduled at" decision matrix honest.
            Kind = BackupKind.AdHoc,
            // Auto-pin so the next retention sweep can't bin a DR snapshot
            // that the operator just hauled down. They can unpin
            // after the restore.
            IsPinned = true,
            OffsiteObjectKey = objectKey,
            OffsiteUploadedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.Backups.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            // Don't leave a downloaded dump on disk with no backups row — a
            // retry would otherwise trip the "a local backup already exists"
            // guard and force a manual cleanup.
            TryDeleteFile(finalPath);
            throw;
        }

        _logger.LogInformation(
            "Downloaded off-site backup {Bucket}/{Key} ({Bytes} bytes) into local row {BackupId}.",
            settings.Bucket, objectKey, size, row.Id);
        return row.Id;
    }

    /// <summary>
    /// Downloads a per-tenant snapshot ZIP into the per-tenant subdirectory
    /// and inserts a <c>per_tenant_backups</c> row pointing at it, so the
    /// existing Restore button on <c>/site-admin/tenant-backups</c> can take
    /// over. Mirror of <see cref="DownloadAsync"/> for the per-tenant case;
    /// reports byte-level progress for the same progress-bar surface.
    ///
    /// <para>
    /// Reads the snapshot's <c>manifest.json</c> to determine the owning
    /// organisation and refuses if the slug parsed from the object key
    /// doesn't match, the org isn't present locally, or the snapshot's
    /// <c>organization_id</c> can't be reconciled with the local org id.
    /// Stages under <c>&lt;name&gt;.partial</c> and renames atomically so a
    /// crash mid-download can't register a corrupt row.
    /// </para>
    /// </summary>
    /// <returns>The id of the inserted <c>per_tenant_backups</c> row.</returns>
    public async Task<int> DownloadPerTenantAsync(string objectKey, IProgress<(long BytesDownloaded, long? TotalBytes)>? progress, CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct)
            ?? throw new InvalidOperationException("Off-site backup is not configured.");

        var prefix = settings.Prefix ?? string.Empty;
        var tenantPrefix = (prefix.Length == 0 ? string.Empty : EnsureTrailingSlash(prefix)) + PerTenantKeyPrefix;
        var remainder = StripPrefix(objectKey, tenantPrefix);
        var slash = remainder.IndexOf('/');
        if (slash <= 0 || slash == remainder.Length - 1)
        {
            throw new InvalidOperationException($"Refusing to download per-tenant object with malformed key: {objectKey}");
        }
        var slug = remainder[..slash];
        var fileName = remainder[(slash + 1)..];
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to download per-tenant object with suspicious leaf name: {objectKey}");
        }
        if (!fileName.EndsWith(PerTenantBackupService.FileSuffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to download non-per-tenant-snapshot object: {objectKey}");
        }

        // The slug must already exist locally — restoring a tenant snapshot
        // into a brand-new org would tangle FK references the manifest
        // doesn't know about. Look it up before downloading so we fail fast.
        var org = await _db.Organizations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Slug == slug, ct)
            ?? throw new InvalidOperationException(
                $"No local organisation with slug '{slug}' — create or restore the org before pulling its per-tenant snapshot.");

        var dir = _perTenantBackups.DirectoryFor(slug);
        Directory.CreateDirectory(dir);
        var finalPath = Path.Combine(dir, fileName);
        var stagingPath = finalPath + ".partial";

        if (File.Exists(finalPath))
        {
            throw new InvalidOperationException(
                $"A local per-tenant snapshot named '{fileName}' already exists for org '{slug}'. Delete or rename it before re-importing the off-site copy.");
        }
        if (File.Exists(stagingPath))
        {
            File.Delete(stagingPath);
        }

        var provider = _providerFactory.Create(settings);
        try
        {
            await using var download = await provider.OpenReadAsync(objectKey, ct);

            // Provenance, as for whole-database dumps: slugs and org ids repeat
            // across deployments, so only the stamp tells a neighbour's
            // snapshot apart (#1257). A missing stamp is a legacy upload.
            download.Metadata.TryGetValue(DeploymentMetadataKey, out var stampedDeployment);
            if (_deployment.IsForeign(stampedDeployment))
            {
                throw new InvalidOperationException(
                    $"Refusing to import '{fileName}': it was created by a different deployment " +
                    "(deployment-id mismatch). Restoring it would bring another deployment's users " +
                    "and settings into this organisation.");
            }
            if (string.IsNullOrEmpty(stampedDeployment))
            {
                _logger.LogWarning(
                    "Off-site object {Key} carries no deployment-id stamp; importing it anyway (legacy upload). " +
                    "Verify it belongs to this deployment before restoring.", objectKey);
            }

            await StreamToStagingAsync(download.Body, download.ContentLength, stagingPath, progress, ct);
        }
        finally
        {
            DisposeProvider(provider);
        }

        // Verify the manifest before promoting the staging file to a row —
        // an object that doesn't carry a manifest, or whose manifest names
        // a different org, is either corrupt or someone else's snapshot.
        PerTenantBackupManifest manifest;
        await using (var zipStream = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            manifest = await PerTenantBackupService.ReadManifestAsync(zipStream, ct);
        }
        if (!string.Equals(manifest.organization_slug, slug, StringComparison.Ordinal))
        {
            File.Delete(stagingPath);
            throw new InvalidOperationException(
                $"Snapshot manifest names org '{manifest.organization_slug}' but the object key is under '{slug}'.");
        }
        if (_deployment.IsForeign(manifest.deployment_id))
        {
            File.Delete(stagingPath);
            throw new InvalidOperationException(
                $"Refusing to import '{fileName}': its manifest names a different deployment (deployment-id mismatch).");
        }

        File.Move(stagingPath, finalPath);
        var size = new FileInfo(finalPath).Length;

        var row = new PerTenantBackup
        {
            OrganizationId = org.Id,
            FileName = fileName,
            FileSizeBytes = size,
            CreatedAt = manifest.created_at,
            Kind = BackupKind.AdHoc,
            SchemaVersion = manifest.schema_version,
            // Auto-pin so retention can't bin the row before the operator
            // restores from it.
            IsPinned = true,
            OffsiteObjectKey = objectKey,
            OffsiteUploadedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.PerTenantBackups.Add(row);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Downloaded off-site per-tenant snapshot {Bucket}/{Key} ({Bytes} bytes) for org {Slug} into local row {RowId}.",
            settings.Bucket, objectKey, size, slug, row.Id);
        return row.Id;
    }

    /// <summary>
    /// Deletes this deployment's off-site objects older than
    /// <c>OffsiteRetentionDays</c>: whole-database dumps, and per-tenant
    /// snapshots per organisation. The newest <see cref="PruneKeepRecent"/> of
    /// each are kept whatever their age. Idempotent, so safe to call on every
    /// scheduler tick.
    ///
    /// <para>
    /// The bucket and prefix may be shared with another deployment (#1258).
    /// Ownership is read from the deployment stamp on each object, since a key
    /// says nothing about who wrote it: another deployment's objects are never
    /// deleted and never count towards the newest kept. Objects with no stamp
    /// were uploaded before stamping existed; they are treated as this
    /// deployment's only while no whole-database dump under the prefix carries
    /// another deployment's stamp, and otherwise left alone with a warning.
    /// </para>
    /// </summary>
    public async Task PruneAsync(CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null) return;
        var cutoff = _clock.GetUtcNow().UtcDateTime.AddDays(-settings.RetentionDays);
        var prefix = settings.Prefix ?? string.Empty;
        var listPrefix = prefix.Length == 0 ? string.Empty : EnsureTrailingSlash(prefix);

        var provider = _providerFactory.Create(settings);
        try
        {
            // Top level only: the per-tenant subtree holds a snapshot per
            // organisation per night and is pruned on its own below.
            var dumps = (await provider.ListAsync(listPrefix, topLevelOnly: true, ct))
                .Where(o => IsWholeDbDumpKey(o.Key, prefix, out _))
                .ToList();
            // Every dump's stamp is read: there are few of them, and whether any
            // belongs to a neighbour decides how unstamped objects are treated.
            var owners = await ReadOwnersAsync(provider, dumps.Select(o => o.Key), ct);
            var shared = owners.Values.Any(o => o == OffsiteOwner.Foreign);

            var tally = new PruneTally();
            var staleDumps = await SelectStaleAsync(provider, dumps, owners, shared, cutoff, tally, ct);

            var tenantPrefix = listPrefix + PerTenantKeyPrefix;
            var snapshots = (await provider.ListAsync(tenantPrefix, topLevelOnly: false, ct))
                .Select(o => (Object: o, Slug: TryParsePerTenantKey(o.Key, tenantPrefix, out var slug, out _) ? slug : null))
                .Where(x => x.Slug is not null)
                .GroupBy(x => x.Slug!, x => x.Object, StringComparer.Ordinal)
                .ToList();
            // Read up front, in parallel, every stamp the selection below is
            // likely to need: each organisation's newest few and everything past
            // the cutoff. Anything else it needs is read when it gets there.
            var likelyNeeded = snapshots.SelectMany(g => g
                .OrderByDescending(o => o.LastModifiedUtc)
                .Where((o, i) => i < PruneKeepRecent || o.LastModifiedUtc < cutoff)
                .Select(o => o.Key));
            foreach (var (key, owner) in await ReadOwnersAsync(provider, likelyNeeded, ct)) owners[key] = owner;

            var staleSnapshots = new List<string>();
            foreach (var group in snapshots)
            {
                staleSnapshots.AddRange(await SelectStaleAsync(provider, group, owners, shared, cutoff, tally, ct));
            }

            var stale = staleDumps.Concat(staleSnapshots).ToList();
            if (stale.Count > 0)
            {
                await provider.DeleteAsync(stale, ct);
                _logger.LogInformation(
                    "Off-site prune deleted {DumpCount} database backups and {SnapshotCount} organisation snapshots older than {Days} days from {Bucket}/{Prefix}.",
                    staleDumps.Count, staleSnapshots.Count, settings.RetentionDays, settings.Bucket, prefix);
            }
            if (tally.UnstampedLeftAlone > 0)
            {
                _logger.LogWarning(
                    "Off-site prune left {Count} expired objects in {Bucket}/{Prefix} alone: they carry no deployment stamp, and the prefix also holds another deployment's backups, so they cannot be told apart. Delete them by hand once you know whose they are.",
                    tally.UnstampedLeftAlone, settings.Bucket, prefix);
            }
        }
        finally
        {
            DisposeProvider(provider);
        }
    }

    /// <summary>Who wrote an off-site object, as far as its stamp says.</summary>
    private enum OffsiteOwner
    {
        /// <summary>Stamped with this deployment's id.</summary>
        Ours,
        /// <summary>Stamped with another deployment's id.</summary>
        Foreign,
        /// <summary>No stamp: uploaded before stamping existed.</summary>
        Unstamped,
        /// <summary>The stamp could not be read (the object went away, or the read failed).</summary>
        Unknown,
    }

    private sealed class PruneTally
    {
        public int UnstampedLeftAlone;
    }

    /// <summary>
    /// Picks the keys to delete from one group of objects (the whole-database
    /// dumps, or one organisation's snapshots): this deployment's objects past
    /// <paramref name="cutoff"/>, after its newest <see cref="PruneKeepRecent"/>.
    /// </summary>
    private async Task<List<string>> SelectStaleAsync(
        IOffsiteStorageProvider provider,
        IEnumerable<OffsiteStorageObject> group,
        Dictionary<string, OffsiteOwner> owners,
        bool shared,
        DateTime cutoff,
        PruneTally tally,
        CancellationToken ct)
    {
        var kept = 0;
        var stale = new List<string>();
        foreach (var obj in group.OrderByDescending(o => o.LastModifiedUtc))
        {
            var expired = obj.LastModifiedUtc < cutoff;
            // With the newest kept, only an expired object needs looking at.
            if (kept >= PruneKeepRecent && !expired) continue;

            if (!owners.TryGetValue(obj.Key, out var owner))
            {
                owner = await ReadOwnerAsync(provider, obj.Key, ct);
                owners[obj.Key] = owner;
            }
            if (owner == OffsiteOwner.Unstamped && !shared) owner = OffsiteOwner.Ours;
            if (owner != OffsiteOwner.Ours)
            {
                if (owner == OffsiteOwner.Unstamped && expired) tally.UnstampedLeftAlone++;
                continue;
            }

            if (kept < PruneKeepRecent)
            {
                kept++;
                continue;
            }
            stale.Add(obj.Key);
        }
        return stale;
    }

    /// <summary>
    /// Reads the stamps of <paramref name="keys"/>, a few at a time. No backend
    /// returns metadata in a listing, so each is one small request.
    /// </summary>
    private async Task<Dictionary<string, OffsiteOwner>> ReadOwnersAsync(
        IOffsiteStorageProvider provider, IEnumerable<string> keys, CancellationToken ct)
    {
        var owners = new System.Collections.Concurrent.ConcurrentDictionary<string, OffsiteOwner>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            keys.Distinct(StringComparer.Ordinal),
            new ParallelOptions { MaxDegreeOfParallelism = StampReadConcurrency, CancellationToken = ct },
            async (key, token) => owners[key] = await ReadOwnerAsync(provider, key, token));
        return new Dictionary<string, OffsiteOwner>(owners, StringComparer.Ordinal);
    }

    private async Task<OffsiteOwner> ReadOwnerAsync(IOffsiteStorageProvider provider, string key, CancellationToken ct)
    {
        try
        {
            var metadata = await provider.GetMetadataAsync(key, ct);
            metadata.TryGetValue(DeploymentMetadataKey, out var stamp);
            if (string.IsNullOrEmpty(stamp)) return OffsiteOwner.Unstamped;
            // A former id this installation was recovered from counts as ours
            // (#1322): its objects are pruned and offered like our own.
            return _deployment.IsOwn(stamp) ? OffsiteOwner.Ours : OffsiteOwner.Foreign;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never delete, and never flag, an object whose stamp could not be read.
            _logger.LogWarning(ex, "Could not read the deployment stamp of off-site object {Key}.", key);
            return OffsiteOwner.Unknown;
        }
    }

    /// <summary>
    /// Splits a per-tenant object key into its organisation slug and file
    /// name, refusing anything not shaped
    /// <c>&lt;tenantPrefix&gt;&lt;slug&gt;/&lt;name&gt;.tenant.zip</c>.
    /// </summary>
    private static bool TryParsePerTenantKey(string objectKey, string tenantPrefix, out string slug, out string fileName)
    {
        slug = string.Empty;
        fileName = string.Empty;
        if (!objectKey.StartsWith(tenantPrefix, StringComparison.Ordinal)) return false;
        var remainder = objectKey[tenantPrefix.Length..];
        var slash = remainder.IndexOf('/');
        if (slash <= 0 || slash == remainder.Length - 1) return false;
        var leaf = remainder[(slash + 1)..];
        if (leaf.Contains('/') || leaf.Contains('\\') || leaf.Contains("..", StringComparison.Ordinal)) return false;
        if (!leaf.EndsWith(PerTenantBackupService.FileSuffix, StringComparison.Ordinal)) return false;
        slug = remainder[..slash];
        fileName = leaf;
        return true;
    }

    private static void DisposeProvider(IOffsiteStorageProvider provider)
    {
        if (provider is IDisposable disposable) disposable.Dispose();
    }

    /// <summary>
    /// Persists the post-upload row stamp; on a save failure deletes the
    /// just-uploaded object so it doesn't linger remotely with no DB record.
    /// Mirrors the download-side cleanup (<see cref="DownloadAsync"/>). The
    /// delete is best-effort — a failed delete only leaves the object for the
    /// next prune-by-age sweep — and runs on <see cref="CancellationToken.None"/>
    /// so cancellation doesn't strand the orphan. #406
    /// </summary>
    private async Task SaveOrCleanupOrphanAsync(IOffsiteStorageProvider provider, string objectKey, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            try
            {
                await provider.DeleteAsync(new[] { objectKey }, CancellationToken.None);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx,
                    "Uploaded off-site object {Key} but couldn't record it and couldn't delete the orphan; it will be reaped by prune-by-age.",
                    objectKey);
            }
            throw;
        }
    }

    private static string BuildObjectKey(string? prefix, string fileName) =>
        string.IsNullOrEmpty(prefix)
            ? fileName
            : prefix.TrimEnd('/') + "/" + fileName;

    private static string BuildPerTenantObjectKey(string? prefix, string slug, string fileName)
    {
        // Slugs are validated at org-creation time, but a stray '/' or ".."
        // here would silently write outside the tenants/<slug>/ namespace (the
        // download side already rejects such leaf names) — so fail closed
        // rather than compose a key that escapes the convention.
        if (string.IsNullOrEmpty(slug)
            || slug.Contains('/') || slug.Contains('\\') || slug.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to build an off-site key for suspicious slug: '{slug}'.");
        }
        var head = string.IsNullOrEmpty(prefix) ? string.Empty : prefix.TrimEnd('/') + "/";
        return head + PerTenantKeyPrefix + slug + "/" + fileName;
    }

    /// <summary>
    /// True when <paramref name="objectKey"/> is shaped like a whole-DB dump
    /// under <paramref name="prefix"/> — i.e. it sits directly in the prefix
    /// (not the per-tenant namespace), carries the dump suffix, and has no
    /// path-traversal characters in its leaf. The off-site catalogue listing
    /// and the retention prune share this so they can't disagree about what is
    /// a dump. It says nothing about which deployment wrote it; that is the
    /// stamp's job (#1258).
    /// </summary>
    private static bool IsWholeDbDumpKey(string objectKey, string prefix, out string fileName)
    {
        fileName = StripPrefix(objectKey, prefix);
        if (fileName.StartsWith(PerTenantKeyPrefix, StringComparison.Ordinal)) return false;
        if (!fileName.EndsWith(BackupService.BackupFileSuffix, StringComparison.Ordinal)) return false;
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal)) return false;
        return true;
    }

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith('/') ? value : value + "/";

    private static string StripPrefix(string objectKey, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return objectKey;
        var normalised = prefix.EndsWith('/') ? prefix : prefix + "/";
        return objectKey.StartsWith(normalised, StringComparison.Ordinal)
            ? objectKey[normalised.Length..]
            : objectKey;
    }

    /// <summary>
    /// Streams an object body into <paramref name="stagingPath"/> (created
    /// fresh — never overwriting) in 64 KB chunks, reporting byte-level
    /// progress. Both the whole-DB and per-tenant download paths stage their
    /// bodies the same way before the atomic rename to the final path. The
    /// caller owns <paramref name="body"/> (via the <see cref="OffsiteDownload"/>
    /// it came from) and disposes it.
    /// </summary>
    private static async Task StreamToStagingAsync(
        Stream body,
        long? total,
        string stagingPath,
        IProgress<(long BytesDownloaded, long? TotalBytes)>? progress,
        CancellationToken ct)
    {
        progress?.Report((0L, total));

        long bytes = 0;
        await using var destination = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            bytes += read;
            progress?.Report((bytes, total));
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove orphaned download {Path} after a failed import.", path);
        }
    }

    /// <summary>
    /// The two stamps a dump's name has carried: milliseconds since #371
    /// (see <see cref="BackupService"/>), and whole seconds before it, which
    /// off-site copies from older deployments still use.
    /// </summary>
    private static readonly string[] TimestampFormats = ["yyyyMMddTHHmmssfffZ", "yyyyMMddTHHmmssZ"];

    /// <summary>
    /// Best-effort recovery of the original UTC timestamp from a filename
    /// shaped like <c>aldevtoolbox-yyyyMMddTHHmmss[fff]Z-(scheduled|adhoc).dump</c>.
    /// Returns <see langword="null"/> if the shape doesn't match — the
    /// caller falls back to "now" so the row still saves.
    /// </summary>
    private static DateTime? TryParseTimestamp(string fileName)
    {
        const string prefix = "aldevtoolbox-";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = fileName[prefix.Length..];
        var dash = rest.IndexOf('-');
        if (dash <= 0) return null;
        var stamp = rest[..dash];
        return DateTime.TryParseExact(
            stamp,
            TimestampFormats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}
