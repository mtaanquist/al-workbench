using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Tests for <see cref="ReleaseManagementService"/> — soft-delete /
/// restore / hard-delete. Seeds a Release through the real import service
/// (rather than fake rows) so the cascade-delete coverage exercises the
/// production FK shapes from PR 1.
/// </summary>
public sealed class ReleaseManagementServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    private ReleaseImportService NewImporter(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext, new ALDevToolbox.Services.Translation.TranslationMemoryService(ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance), NullLogger<TranslationImportService>.Instance),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance);

    private ReleaseManagementService NewManagement(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, NullLogger<ReleaseManagementService>.Instance);

    private async Task<int> SeedReleaseAsync(string label = "Test Release", int? parentId = null, string kind = "first_party")
    {
        await using var ctx = _db.NewContext();
        var svc = NewImporter(ctx);
        await using var s = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: label, Kind: kind, ParentReleaseId: parentId, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload($"{label}.app", s, null) }));
        return summary.ReleaseId;
    }

    // ── Soft delete ─────────────────────────────────────────────────────

    [Fact]
    public async Task SoftDeleteAsync_sets_deleted_at_and_hides_from_default_list()
    {
        var id = await SeedReleaseAsync();

        await using (var ctx = _db.NewContext())
        {
            await NewManagement(ctx).SoftDeleteAsync(id);
        }

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == id);
        release.DeletedAt.Should().NotBeNull();

        // Default list filters out soft-deleted rows; admin list still shows them.
        var query = new ObjectExplorerService(read, new ReferenceQueryService(read, new ProjectAccess(read, _db.OrgContext), _db.OrgContext, NullLogger<ReferenceQueryService>.Instance), new ProjectAccess(read, _db.OrgContext), NullLogger<ObjectExplorerService>.Instance);
        var visible = await query.ListReleasesAsync(includeSoftDeleted: false);
        visible.Should().NotContain(r => r.Id == id);
        var adminVisible = await query.ListReleasesAsync(includeSoftDeleted: true);
        adminVisible.Should().Contain(r => r.Id == id);
    }

    [Fact]
    public async Task SoftDeleteAsync_is_idempotent_on_an_already_deleted_release()
    {
        var id = await SeedReleaseAsync();
        await using var ctx = _db.NewContext();
        var svc = NewManagement(ctx);
        await svc.SoftDeleteAsync(id);
        // Second call should be a no-op, not throw.
        await svc.SoftDeleteAsync(id);
    }

    [Fact]
    public async Task SoftDeleteAsync_throws_PlanValidationException_when_the_release_does_not_exist()
    {
        await using var ctx = _db.NewContext();
        var act = async () => await NewManagement(ctx).SoftDeleteAsync(releaseId: 999_999);
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Release");
    }

    // ── Restore ────────────────────────────────────────────────────────

    [Fact]
    public async Task RestoreAsync_clears_deleted_at()
    {
        var id = await SeedReleaseAsync();

        await using (var ctx = _db.NewContext())
        {
            var svc = NewManagement(ctx);
            await svc.SoftDeleteAsync(id);
            await svc.RestoreAsync(id);
        }

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == id);
        release.DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task RestoreAsync_refuses_when_a_successor_holds_the_dedup_key()
    {
        // A replaced preview is the everyday case: the old row is soft-deleted and a
        // new one carries the same key. Restoring the old one must be a field error,
        // not a unique-index violation from the database.
        int oldId, newId;
        await using (var ctx = _db.NewContext())
        {
            var old = new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId, Label = "Business Central 29.0 (DK) Preview",
                DedupKey = "bc-insider:29.0:dk", Kind = "first_party", Status = "ready", IsPrerelease = true,
                DeletedAt = DateTime.UtcNow, ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            var successor = new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId, Label = "Business Central 29.0 (DK) Preview",
                DedupKey = "bc-insider:29.0:dk", Kind = "first_party", Status = "ingesting", IsPrerelease = true,
                ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            ctx.OeReleases.AddRange(old, successor);
            await ctx.SaveChangesAsync();
            (oldId, newId) = (old.Id, successor.Id);
        }

        await using var ctx2 = _db.NewContext();
        var act = () => NewManagement(ctx2).RestoreAsync(oldId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Release");
        await using var read = _db.NewContext();
        (await read.OeReleases.SingleAsync(r => r.Id == oldId)).DeletedAt.Should().NotBeNull();
        (await read.OeReleases.SingleAsync(r => r.Id == newId)).DeletedAt.Should().BeNull();
    }

    // ── Metadata edit ───────────────────────────────────────────────────

    [Fact]
    public async Task UpdateMetadataAsync_preserves_a_legacy_project_name_on_non_project_kinds()
    {
        // The manage form only renders the Project field for pipeline-build
        // releases, so its POST omits ProjectName for everything else — that
        // must not wipe a value left behind by the project → third_party merge.
        var id = await SeedReleaseAsync(label: "Merged bundle", kind: "third_party");
        await using (var setup = _db.NewContext())
        {
            var r = await setup.OeReleases.SingleAsync(r => r.Id == id);
            r.ProjectName = "CRONUS";
            await setup.SaveChangesAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            await NewManagement(ctx).UpdateMetadataAsync(id, publisher: "Continia Software", projectName: null);
        }

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == id);
        release.Publisher.Should().Be("Continia Software");
        release.ProjectName.Should().Be("CRONUS", "hidden legacy values survive metadata edits");
    }

    [Fact]
    public async Task UpdateMetadataAsync_edits_project_name_on_project_kind_releases()
    {
        var id = await SeedReleaseAsync(label: "Pipeline build", kind: "project");

        await using (var ctx = _db.NewContext())
        {
            await NewManagement(ctx).UpdateMetadataAsync(id, publisher: null, projectName: "CRONUS");
        }

        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == id))
            .ProjectName.Should().Be("CRONUS");
    }

    // ── Hard delete ────────────────────────────────────────────────────

    [Fact]
    public async Task HardDeleteAsync_removes_the_release_and_cascades_dependent_rows()
    {
        var id = await SeedReleaseAsync(label: "Cascade Test");

        await using (var ctx = _db.NewContext())
        {
            await NewManagement(ctx).HardDeleteAsync(id, confirmLabel: "Cascade Test");
        }

        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().AnyAsync(r => r.Id == id)).Should().BeFalse();
        // Cascade went through: no orphaned modules or files left behind.
        (await read.OeModules.AsNoTracking().AnyAsync(m => m.ReleaseId == id)).Should().BeFalse();
        (await read.OeModuleFiles.AsNoTracking().AnyAsync(f => f.Module!.ReleaseId == id)).Should().BeFalse();
    }

    [Fact]
    public async Task HardDeleteAsync_refuses_when_another_release_has_this_one_as_parent()
    {
        var parentId = await SeedReleaseAsync(label: "Parent");
        await SeedReleaseAsync(label: "Customer X", parentId: parentId, kind: "project");

        await using var ctx = _db.NewContext();
        var act = async () => await NewManagement(ctx).HardDeleteAsync(parentId, confirmLabel: "Parent");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Release");
    }

    [Fact]
    public async Task HardDeleteAsync_refuses_when_confirm_label_does_not_match()
    {
        var id = await SeedReleaseAsync(label: "Exactly This");
        await using var ctx = _db.NewContext();
        var act = async () => await NewManagement(ctx).HardDeleteAsync(id, confirmLabel: "wrong");
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("ConfirmLabel");

        // And the Release is still there afterwards.
        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().AnyAsync(r => r.Id == id)).Should().BeTrue();
    }

    // ── Clear ingested data (retry support) ─────────────────────────────

    [Fact]
    public async Task ClearIngestedDataAsync_removes_modules_but_keeps_the_release_row()
    {
        var id = await SeedReleaseAsync(label: "Clear me");

        await using (var before = _db.NewContext())
        {
            (await before.OeModules.AsNoTracking().AnyAsync(m => m.ReleaseId == id))
                .Should().BeTrue("the seed import wrote at least one module");
        }

        await using (var ctx = _db.NewContext())
        {
            await NewManagement(ctx).ClearIngestedDataAsync(id);
        }

        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().AnyAsync(r => r.Id == id))
            .Should().BeTrue("the release row is kept so the retry re-runs into it");
        (await read.OeModules.AsNoTracking().AnyAsync(m => m.ReleaseId == id))
            .Should().BeFalse();
        (await read.OeModuleFiles.AsNoTracking().AnyAsync(f => f.Module!.ReleaseId == id))
            .Should().BeFalse("the module cascade wiped the dependent file rows");
    }

    [Fact]
    public async Task HardDeleteAsync_works_on_an_already_soft_deleted_release()
    {
        // Bad-actor scenario: admin soft-deletes a bloated Release and then
        // wants to reclaim the disk. The hard-delete path must work on
        // rows whose DeletedAt is already set.
        var id = await SeedReleaseAsync(label: "Bloated");
        await using (var ctx = _db.NewContext())
        {
            var svc = NewManagement(ctx);
            await svc.SoftDeleteAsync(id);
            await svc.HardDeleteAsync(id, confirmLabel: "Bloated");
        }

        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().AnyAsync(r => r.Id == id)).Should().BeFalse();
    }
}
