using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Drives <see cref="ArtifactReleaseImporter"/> against the shared
/// <see cref="TestDb"/> fixture with a stubbed artifact index: the dedup rule
/// the daily scheduler and the Artifacts tab both rely on (a version whose
/// explicit <c>bc-onprem:{Major}.{Minor}:{cc}</c> key already exists is skipped,
/// no import is queued) and the happy path (a new key creates an ingesting
/// release, stamps the key, and enqueues a BcArtifact job).
/// </summary>
public sealed class ArtifactReleaseImporterTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private const string CountryJson = """[ { "Version": "28.2.50931.51727" } ]""";
    private const string PlatformJson = """[ { "Version": "28.2.50931.51727" } ]""";

    // The insider (preview) channel: a minor preview of the shipped major, the
    // upcoming major in two builds, and the one after.
    private const string InsiderCountryJson = """
    [ { "Version": "28.3.51000.0" }, { "Version": "29.0.55000.0" }, { "Version": "29.0.55190.0" }, { "Version": "30.0.55227.0" } ]
    """;

    private readonly TimeProvider _clock = TimeProvider.System;

    private ArtifactReleaseImporter NewImporter(Data.AppDbContext ctx, ReleaseImportQueue queue, bool withInsider = false)
    {
        // The stub matches on URL substrings, so the channel-specific needles come
        // first: "/onprem/indexes/dk.json" vs "/sandbox/indexes/dk.json".
        var responses = new Dictionary<string, string>
        {
            ["/onprem/indexes/dk.json"] = CountryJson,
            ["/onprem/indexes/platform.json"] = PlatformJson,
        };
        if (withInsider)
        {
            responses["/sandbox/indexes/dk.json"] = InsiderCountryJson;
            responses["/sandbox/indexes/platform.json"] = InsiderCountryJson;
        }
        var factory = new StubHttpClientFactory(responses);
        var artifacts = new BcArtifactService(factory, ctx, _db.OrgContext, NullLogger<BcArtifactService>.Instance);
        var translations = new TranslationImportService(
            ctx, _db.OrgContext,
            new ALDevToolbox.Services.Translation.TranslationMemoryService(
                ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
            NullLogger<TranslationImportService>.Instance);
        var importer = new ReleaseImportService(
            ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), translations,
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance);
        var persistedJobs = new PersistedImportJobs(ctx, TimeProvider.System);
        var management = new ReleaseManagementService(ctx, _db.OrgContext, NullLogger<ReleaseManagementService>.Instance);
        return new ArtifactReleaseImporter(
            artifacts, importer, queue, persistedJobs, management, ctx, _db.OrgContext, _clock,
            NullLogger<ArtifactReleaseImporter>.Instance);
    }

    private static OeRelease Release(string label, string? dedupKey, string status = "ready",
        bool prerelease = false, string? bcVersion = null, DateTime? importedAt = null) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        Label = label,
        DedupKey = dedupKey,
        Kind = "first_party",
        Status = status,
        IsPrerelease = prerelease,
        BcVersion = bcVersion,
        ImportedAt = importedAt ?? DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task ImportAsync_skips_a_version_whose_dedup_key_is_already_in_the_catalogue()
    {
        await using var ctx = _db.NewContext();
        var existing = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            // A differently-labelled release with the SAME dedup key still dedups —
            // the key, not the display label, is what's matched.
            Label = "BC 28.2 Denmark (renamed)",
            DedupKey = "bc-onprem:28.2:dk",
            Kind = "first_party",
            Status = "ready",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(existing);
        await ctx.SaveChangesAsync();

        var queue = new ReleaseImportQueue();
        var outcome = await NewImporter(ctx, queue).ImportAsync("dk", version: null);

        outcome.Status.Should().Be(ArtifactImportStatus.AlreadyImported);
        outcome.ReleaseId.Should().Be(existing.Id);
        queue.Reader.TryRead(out _).Should().BeFalse("nothing should be enqueued for a dedup hit");

        await using var read = _db.NewContext();
        (await read.OeReleases.CountAsync(r => r.DedupKey == "bc-onprem:28.2:dk")).Should().Be(1);
    }

    [Fact]
    public async Task ImportAsync_is_not_blocked_by_a_manual_release_sharing_the_label()
    {
        // A manual upload that happens to use the same display label carries no
        // dedup key, so it must not stop the artifact import from running.
        await using var ctx = _db.NewContext();
        ctx.OeReleases.Add(new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "Business Central 28.2 (DK)",
            DedupKey = null,
            Kind = "first_party",
            Status = "ready",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var queue = new ReleaseImportQueue();
        var outcome = await NewImporter(ctx, queue).ImportAsync("dk", version: null);

        outcome.Status.Should().Be(ArtifactImportStatus.Queued, "the keyless manual release doesn't dedup");
    }

    [Fact]
    public async Task ImportAsync_creates_an_ingesting_release_and_enqueues_a_bc_artifact_job()
    {
        await using var ctx = _db.NewContext();
        var queue = new ReleaseImportQueue();

        var outcome = await NewImporter(ctx, queue).ImportAsync("dk", version: null);

        outcome.Status.Should().Be(ArtifactImportStatus.Queued);
        outcome.Label.Should().Be("Business Central 28.2 (DK)");
        outcome.ReleaseId.Should().NotBeNull();

        await using var read = _db.NewContext();
        var release = await read.OeReleases.SingleAsync(r => r.Id == outcome.ReleaseId);
        release.Label.Should().Be("Business Central 28.2 (DK)");
        release.DedupKey.Should().Be("bc-onprem:28.2:dk");
        release.Kind.Should().Be("first_party");
        release.Status.Should().Be("ingesting");

        queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.ReleaseId.Should().Be(outcome.ReleaseId);
        job.Source.Should().BeOfType<ReleaseImportSource.BcArtifact>()
            .Which.ApplicationUrl.Should().Be($"https://{BcArtifactIndex.CdnHost}/onprem/28.2.50931.51727/dk");

        var jobRow = await read.OeImportJobs.SingleAsync(j => j.ReleaseId == outcome.ReleaseId);
        jobRow.Kind.Should().Be("bc_artifact");
    }

    [Fact]
    public async Task ImportPreviewsAsync_queues_one_flagged_preview_per_upcoming_major()
    {
        await using var ctx = _db.NewContext();
        var queue = new ReleaseImportQueue();

        var outcomes = await NewImporter(ctx, queue, withInsider: true).ImportPreviewsAsync("dk");

        // 28.3 is a minor preview of the shipped major (28) and is left out;
        // 29 gets its newest 29.0 build; 30 its only build.
        outcomes.Select(o => (o.Status, o.Label)).Should().Equal(
            (ArtifactImportStatus.Queued, "Business Central 29.0 (DK) Preview"),
            (ArtifactImportStatus.Queued, "Business Central 30.0 (DK) Preview"));

        await using var read = _db.NewContext();
        var previews = await read.OeReleases.Where(r => r.IsPrerelease).OrderBy(r => r.DedupKey).ToListAsync();
        previews.Select(r => r.DedupKey).Should().Equal("bc-insider:29.0:dk", "bc-insider:30.0:dk");
        previews.Should().OnlyContain(r => r.Kind == "first_party" && r.Status == "ingesting");

        queue.Reader.TryRead(out var first).Should().BeTrue();
        first!.Source.Should().BeOfType<ReleaseImportSource.BcArtifact>()
            .Which.ApplicationUrl.Should().Be($"https://{BcArtifactIndex.InsiderCdnHost}/sandbox/29.0.55190.0/dk");
    }

    [Fact]
    public async Task ImportPreviewsAsync_is_empty_when_the_insider_index_is_unavailable()
    {
        await using var ctx = _db.NewContext();
        var queue = new ReleaseImportQueue();

        // No insider responses stubbed: the channel 404s, which is "nothing in preview".
        var outcomes = await NewImporter(ctx, queue, withInsider: false).ImportPreviewsAsync("dk");

        outcomes.Should().BeEmpty();
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task ImportPreviewsAsync_keeps_a_fresh_or_same_build_preview_and_replaces_a_stale_one()
    {
        await using var ctx = _db.NewContext();
        // 29.0: imported a month ago from an older build -> stale, replaced.
        var stale = Release("Business Central 29.0 (DK) Preview", "bc-insider:29.0:dk",
            prerelease: true, bcVersion: "29.0.55000.0", importedAt: DateTime.UtcNow.AddDays(-30));
        // 30.0: imported a month ago but it IS the current insider build -> kept.
        var current = Release("Business Central 30.0 (DK) Preview", "bc-insider:30.0:dk",
            prerelease: true, bcVersion: "30.0.55227.0", importedAt: DateTime.UtcNow.AddDays(-30));
        ctx.OeReleases.AddRange(stale, current);
        await ctx.SaveChangesAsync();

        var queue = new ReleaseImportQueue();
        var outcomes = await NewImporter(ctx, queue, withInsider: true).ImportPreviewsAsync("dk");

        outcomes.Select(o => o.Status).Should().Equal(ArtifactImportStatus.Replaced, ArtifactImportStatus.AlreadyImported);
        outcomes[1].ReleaseId.Should().Be(current.Id);

        await using var read = _db.NewContext();
        (await read.OeReleases.SingleAsync(r => r.Id == stale.Id)).DeletedAt.Should().NotBeNull("the stale preview is retired, not kept beside its replacement");
        var replacement = await read.OeReleases.SingleAsync(r => r.Id == outcomes[0].ReleaseId);
        replacement.DedupKey.Should().Be("bc-insider:29.0:dk");
        replacement.IsPrerelease.Should().BeTrue();
        replacement.Status.Should().Be("ingesting");
        (await read.OeReleases.SingleAsync(r => r.Id == current.Id)).DeletedAt.Should().BeNull();

        queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.ReleaseId.Should().Be(replacement.Id);
        queue.Reader.TryRead(out _).Should().BeFalse("only the stale preview was re-queued");
    }

    [Fact]
    public async Task ImportPreviewsAsync_never_replaces_a_preview_that_is_still_ingesting()
    {
        await using var ctx = _db.NewContext();
        var ingesting = Release("Business Central 29.0 (DK) Preview", "bc-insider:29.0:dk", status: "ingesting",
            prerelease: true, bcVersion: null, importedAt: DateTime.UtcNow.AddDays(-30));
        var other = Release("Business Central 30.0 (DK) Preview", "bc-insider:30.0:dk",
            prerelease: true, bcVersion: "30.0.55227.0");
        ctx.OeReleases.AddRange(ingesting, other);
        await ctx.SaveChangesAsync();

        var outcomes = await NewImporter(ctx, new ReleaseImportQueue(), withInsider: true).ImportPreviewsAsync("dk");

        outcomes.Should().OnlyContain(o => o.Status == ArtifactImportStatus.AlreadyImported);
        await using var read = _db.NewContext();
        (await read.OeReleases.SingleAsync(r => r.Id == ingesting.Id)).DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task SupersedePreviewsAsync_retires_a_preview_once_its_version_ships_and_is_ready()
    {
        await using var ctx = _db.NewContext();
        var shippedPreview = Release("Business Central 29.0 (DK) Preview", "bc-insider:29.0:dk", prerelease: true);
        var shipped = Release("Business Central 29.0 (DK)", "bc-onprem:29.0:dk");
        // 30.0 has no shipped counterpart yet: stays.
        var pending = Release("Business Central 30.0 (DK) Preview", "bc-insider:30.0:dk", prerelease: true);
        // 29.0 for another country shipped only as far as "ingesting": its preview stays too.
        var w1Preview = Release("Business Central 29.0 (W1) Preview", "bc-insider:29.0:w1", prerelease: true);
        var w1Ingesting = Release("Business Central 29.0 (W1)", "bc-onprem:29.0:w1", status: "ingesting");
        ctx.OeReleases.AddRange(shippedPreview, shipped, pending, w1Preview, w1Ingesting);
        await ctx.SaveChangesAsync();

        var importer = NewImporter(ctx, new ReleaseImportQueue());
        (await importer.SupersedePreviewsAsync("DK")).Should().Be(1);
        (await importer.SupersedePreviewsAsync("w1")).Should().Be(0);

        await using var read = _db.NewContext();
        (await read.OeReleases.SingleAsync(r => r.Id == shippedPreview.Id)).DeletedAt.Should().NotBeNull();
        (await read.OeReleases.SingleAsync(r => r.Id == pending.Id)).DeletedAt.Should().BeNull();
        (await read.OeReleases.SingleAsync(r => r.Id == w1Preview.Id)).DeletedAt.Should().BeNull();
        (await read.OeReleases.SingleAsync(r => r.Id == shipped.Id)).DeletedAt.Should().BeNull();

        // Idempotent: a second pass finds nothing left to retire.
        (await importer.SupersedePreviewsAsync("dk")).Should().Be(0);
    }

    [Fact]
    public async Task ImportAsync_is_not_satisfied_by_a_preview_of_the_same_version()
    {
        // The shipped 28.2 must still import even though a 28.2 preview sits in the
        // catalogue: the two keys are deliberately distinct.
        await using var ctx = _db.NewContext();
        ctx.OeReleases.Add(Release("Business Central 28.2 (DK) Preview", "bc-insider:28.2:dk", prerelease: true));
        await ctx.SaveChangesAsync();

        var outcome = await NewImporter(ctx, new ReleaseImportQueue()).ImportAsync("dk", version: null);

        outcome.Status.Should().Be(ArtifactImportStatus.Queued);
        await using var read = _db.NewContext();
        (await read.OeReleases.SingleAsync(r => r.Id == outcome.ReleaseId)).IsPrerelease.Should().BeFalse();
    }
}
