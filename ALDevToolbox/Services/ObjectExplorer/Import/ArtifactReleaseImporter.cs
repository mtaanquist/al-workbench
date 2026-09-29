using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Coordinates importing a Microsoft OnPrem artifact into a Release: resolve the
/// version → dedup against the catalogue → create the <c>ingesting</c> release
/// row → enqueue a <see cref="ReleaseImportSource.BcArtifact"/> job. Shared by
/// the per-org auto-import scheduler (<see cref="ReleaseAutoImportScheduler"/>)
/// and the Artifacts tab on the Import Release page, so both name and dedup
/// releases identically.
///
/// <para>
/// Dedup keys on the explicit <c>bc-onprem:{Major}.{Minor}:{cc}</c> key
/// (<see cref="BcArtifactIndex.FormatDedupKey"/>), not the display label. A
/// version whose key already exists (non-deleted) is skipped, which is what
/// makes the daily sweep idempotent and stops the Artifacts tab re-downloading a
/// version already in the catalogue. Always <c>first_party</c> — these are
/// Microsoft releases.
/// </para>
///
/// <para>
/// Pre-release builds (<see cref="ImportPreviewsAsync"/>) follow the same shape
/// under the <c>bc-insider:</c> key and the <c>is_prerelease</c> flag, with two
/// twists the sweep relies on: a preview older than <see cref="PreviewRefreshAge"/>
/// is replaced by the newer insider build (the old row is soft-deleted, which
/// frees the key), and <see cref="SupersedePreviewsAsync"/> soft-deletes a
/// preview once the same Major.Minor is in the catalogue as a ready shipped
/// release. See <c>.design/object-explorer.md</c>, "Preview builds".
/// </para>
/// </summary>
public sealed class ArtifactReleaseImporter
{
    /// <summary>
    /// How old an imported preview gets before the sweep swaps it for the
    /// current insider build. Microsoft pushes several insider builds a week
    /// and each import is a multi-GB download plus a full ingest, so tracking
    /// every build is off the table; a fortnight keeps a preview representative
    /// of the upcoming release without turning the catalogue over daily.
    /// </summary>
    public static readonly TimeSpan PreviewRefreshAge = TimeSpan.FromDays(14);

    private readonly BcArtifactService _artifacts;
    private readonly ReleaseImportService _importer;
    private readonly ReleaseImportQueue _queue;
    private readonly PersistedImportJobs _persistedJobs;
    private readonly ReleaseManagementService _management;
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<ArtifactReleaseImporter> _logger;

    public ArtifactReleaseImporter(
        BcArtifactService artifacts,
        ReleaseImportService importer,
        ReleaseImportQueue queue,
        PersistedImportJobs persistedJobs,
        ReleaseManagementService management,
        AppDbContext db,
        IOrganizationContext orgContext,
        TimeProvider clock,
        ILogger<ArtifactReleaseImporter> logger)
    {
        _artifacts = artifacts;
        _importer = importer;
        _queue = queue;
        _persistedJobs = persistedJobs;
        _management = management;
        _db = db;
        _orgContext = orgContext;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Resolves <paramref name="version"/> (newest when null, else the exact /
    /// Major.Minor match) for <paramref name="country"/> and queues its import,
    /// unless the catalogue already has a release with the computed label.
    /// </summary>
    public async Task<ArtifactImportOutcome> ImportAsync(string country, string? version, CancellationToken ct = default)
    {
        var resolved = await _artifacts.ResolveOnPremAsync(country, version, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return new ArtifactImportOutcome(ArtifactImportStatus.NotFound, null, null);
        }

        // Dedup by the explicit key — query-filtered to the current org.
        var existingId = await _db.OeReleases.AsNoTracking()
            .Where(r => r.DedupKey == resolved.DedupKey && r.DeletedAt == null)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (existingId is not null)
        {
            return new ArtifactImportOutcome(ArtifactImportStatus.AlreadyImported, existingId, resolved.Label);
        }

        var releaseId = await QueueAsync(resolved, ct).ConfigureAwait(false);
        return new ArtifactImportOutcome(ArtifactImportStatus.Queued, releaseId, resolved.Label);
    }

    /// <summary>
    /// Queues the pre-release builds <see cref="BcArtifactService.ResolvePreviewsAsync"/>
    /// picks for <paramref name="country"/>: one outcome per upcoming major.
    /// A major whose preview is already in the catalogue is skipped unless that
    /// preview is older than <see cref="PreviewRefreshAge"/> and the insider
    /// channel now offers a different build — then the old row is soft-deleted
    /// and the new build queued (<see cref="ArtifactImportStatus.Replaced"/>).
    /// A preview still ingesting is never replaced. One preview per major is the
    /// contract, so when the insider channel moves a major on to its next minor
    /// (29.0 dropped, 29.1 listed) the preview of the earlier minor is retired
    /// as well, before the new one is queued.
    /// </summary>
    public async Task<IReadOnlyList<ArtifactImportOutcome>> ImportPreviewsAsync(string country, CancellationToken ct = default)
    {
        var resolved = await _artifacts.ResolvePreviewsAsync(country, ct).ConfigureAwait(false);
        var outcomes = new List<ArtifactImportOutcome>(resolved.Count);
        var now = _clock.GetUtcNow().UtcDateTime;
        var cc = country.Trim().ToLowerInvariant();

        foreach (var preview in resolved)
        {
            await RetireOtherMinorsAsync(preview, cc, ct).ConfigureAwait(false);

            var existing = await _db.OeReleases.AsNoTracking()
                .Where(r => r.DedupKey == preview.DedupKey && r.DeletedAt == null)
                .Select(r => new { r.Id, r.Status, r.ImportedAt, r.BcVersion })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            if (existing is not null)
            {
                // BcVersion is stamped from the Base Application manifest at ingest
                // and matches the insider build number, so "same build" is a direct
                // compare; a null (failed before the manifest was read) counts as
                // different so the retry happens once the row is old enough.
                var sameBuild = string.Equals(existing.BcVersion, preview.Version, StringComparison.OrdinalIgnoreCase);
                var stale = now - existing.ImportedAt >= PreviewRefreshAge;
                if (existing.Status == "ingesting" || sameBuild || !stale)
                {
                    outcomes.Add(new ArtifactImportOutcome(ArtifactImportStatus.AlreadyImported, existing.Id, preview.Label));
                    continue;
                }

                // One transaction for the soft-delete and the new rows: if the
                // quota guard (or anything else) refuses the replacement, the org
                // keeps the preview it had instead of ending up with none.
                (int replacementId, long jobRowId) begun;
                await using (var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
                {
                    await _management.SoftDeleteAsync(existing.Id, ct).ConfigureAwait(false);
                    begun = await BeginAsync(preview, ct).ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }
                var replacementId = begun.replacementId;
                await EnqueueAsync(replacementId, begun.jobRowId, preview, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Replaced preview release {OldReleaseId} ({OldVersion}) with build {Version} as release {ReleaseId}.",
                    existing.Id, existing.BcVersion, preview.Version, replacementId);
                outcomes.Add(new ArtifactImportOutcome(ArtifactImportStatus.Replaced, replacementId, preview.Label));
                continue;
            }

            var releaseId = await QueueAsync(preview, ct).ConfigureAwait(false);
            outcomes.Add(new ArtifactImportOutcome(ArtifactImportStatus.Queued, releaseId, preview.Label));
        }

        return outcomes;
    }

    /// <summary>
    /// Soft-deletes every preview release for <paramref name="country"/> whose
    /// major has shipped: a <c>ready</c>, non-deleted <c>bc-onprem:</c> release
    /// of the same or a higher major exists for the country. That is the
    /// "replace them once they get published for real" half of the preview flow,
    /// and comparing majors rather than exact Major.Minor keys means a 29.0
    /// preview is still retired when the org's first shipped 29 is 29.1 (the sweep
    /// was off for a while, or the org joined late). Keyed on the dedup keys, so a
    /// renamed label changes nothing. Returns the number of previews retired.
    /// Soft, not hard: the admin release list can still restore or purge them.
    /// </summary>
    public async Task<int> SupersedePreviewsAsync(string country, CancellationToken ct = default)
    {
        var cc = country.Trim().ToLowerInvariant();
        var previews = await _db.OeReleases.AsNoTracking()
            .Where(r => r.IsPrerelease && r.DeletedAt == null && r.DedupKey != null
                        && r.DedupKey.StartsWith(BcArtifactIndex.PreviewDedupPrefix + ":")
                        && r.DedupKey.EndsWith(":" + cc))
            .Select(r => new { r.Id, r.Label, r.DedupKey })
            .ToListAsync(ct).ConfigureAwait(false);
        if (previews.Count == 0) return 0;

        var shippedKeys = await _db.OeReleases.AsNoTracking()
            .Where(r => r.DeletedAt == null && r.Status == "ready" && r.DedupKey != null
                        && r.DedupKey.StartsWith(BcArtifactIndex.ReleaseDedupPrefix + ":")
                        && r.DedupKey.EndsWith(":" + cc))
            .Select(r => r.DedupKey!)
            .ToListAsync(ct).ConfigureAwait(false);
        var newestShippedMajor = shippedKeys
            .Select(k => BcArtifactIndex.ParseDedupKey(k))
            .Where(p => p is not null)
            .Select(p => BcArtifactIndex.ToMajor(p!.Value.MajorMinor))
            .Where(m => m is not null)
            .Select(m => m!.Value)
            .DefaultIfEmpty(-1)
            .Max();
        if (newestShippedMajor < 0) return 0;

        var retired = 0;
        foreach (var preview in previews)
        {
            var parsed = BcArtifactIndex.ParseDedupKey(preview.DedupKey);
            var previewMajor = parsed is null ? null : BcArtifactIndex.ToMajor(parsed.Value.MajorMinor);
            if (previewMajor is null || previewMajor.Value > newestShippedMajor) continue;

            await _management.SoftDeleteAsync(preview.Id, ct).ConfigureAwait(false);
            retired++;
            _logger.LogInformation(
                "Retired preview release {ReleaseId} ({Label}): version {Major} has shipped for {Country}.",
                preview.Id, preview.Label, previewMajor.Value, cc);
        }
        return retired;
    }

    /// <summary>
    /// Soft-deletes any active preview of <paramref name="preview"/>'s major for
    /// <paramref name="cc"/> under a different minor, so a major never carries two
    /// previews once the channel moves on to its next minor.
    /// </summary>
    private async Task RetireOtherMinorsAsync(ResolvedArtifact preview, string cc, CancellationToken ct)
    {
        var major = BcArtifactIndex.ToMajor(preview.Version);
        if (major is null) return;
        var sameMajorPrefix = $"{BcArtifactIndex.PreviewDedupPrefix}:{major.Value}.";
        var others = await _db.OeReleases.AsNoTracking()
            .Where(r => r.IsPrerelease && r.DeletedAt == null && r.DedupKey != null
                        && r.DedupKey != preview.DedupKey
                        && r.DedupKey.StartsWith(sameMajorPrefix)
                        && r.DedupKey.EndsWith(":" + cc))
            .Select(r => new { r.Id, r.Label })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var other in others)
        {
            await _management.SoftDeleteAsync(other.Id, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Retired preview release {ReleaseId} ({Label}): the insider channel now offers {MajorMinor} for major {Major}.",
                other.Id, other.Label, preview.MajorMinor, major.Value);
        }
    }

    /// <summary>Creates the ingesting release row for <paramref name="resolved"/> and enqueues its download job.</summary>
    private async Task<int> QueueAsync(ResolvedArtifact resolved, CancellationToken ct)
    {
        var (releaseId, jobRowId) = await BeginAsync(resolved, ct).ConfigureAwait(false);
        await EnqueueAsync(releaseId, jobRowId, resolved, ct).ConfigureAwait(false);
        return releaseId;
    }

    /// <summary>The database half of queuing: the ingesting release row plus its persisted job row.</summary>
    private async Task<(int ReleaseId, long JobRowId)> BeginAsync(ResolvedArtifact resolved, CancellationToken ct)
    {
        var metadata = new ReleaseImportMetadata(
            Label: resolved.Label,
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            DedupKey: resolved.DedupKey,
            IsPrerelease: resolved.IsPrerelease);
        var releaseId = await _importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);
        var identity = AmbientOrganizationScope.OrganizationIdentity.FromContext(_orgContext, "queuing an artifact import");
        var source = new ReleaseImportSource.BcArtifact(resolved.ApplicationUrl);
        var jobRowId = await _persistedJobs.CreateAsync(releaseId, identity, source, storeSymbolReference: false, ct).ConfigureAwait(false);
        return (releaseId, jobRowId);
    }

    /// <summary>The in-memory half: hands the job to the worker once its rows are committed.</summary>
    private async Task EnqueueAsync(int releaseId, long jobRowId, ResolvedArtifact resolved, CancellationToken ct)
    {
        var identity = AmbientOrganizationScope.OrganizationIdentity.FromContext(_orgContext, "queuing an artifact import");
        var source = new ReleaseImportSource.BcArtifact(resolved.ApplicationUrl);
        await _queue.EnqueueAsync(
            new ReleaseImportJob(releaseId, identity, source, StoreSymbolReference: false, jobRowId), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Queued BC artifact import {Label} (release {ReleaseId}, version {Version}, prerelease {Prerelease}, {Url}).",
            resolved.Label, releaseId, resolved.Version, resolved.IsPrerelease, resolved.ApplicationUrl);
    }
}

/// <summary>Outcome of <see cref="ArtifactReleaseImporter.ImportAsync"/>.</summary>
public enum ArtifactImportStatus
{
    /// <summary>A new ingesting release was created and the import was enqueued.</summary>
    Queued,
    /// <summary>A non-deleted release with the computed label already exists; nothing was queued.</summary>
    AlreadyImported,
    /// <summary>No artifact matched the requested version/country.</summary>
    NotFound,
    /// <summary>A stale preview was soft-deleted and the current insider build queued in its place (previews only).</summary>
    Replaced,
}

/// <summary>Result of an artifact import attempt: the status plus the affected release id / label when known.</summary>
public sealed record ArtifactImportOutcome(ArtifactImportStatus Status, int? ReleaseId, string? Label);
