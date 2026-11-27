using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// An encrypted <c>.app</c> in an upload is left out and listed on the release
/// instead of failing the whole import (#1801). An upload where nothing could be
/// read still fails.
/// </summary>
public sealed class UnreadableAppImportTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    private ReleaseImportService NewService(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext, new ALDevToolbox.Services.Translation.TranslationMemoryService(ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance), _db.NewQuotaGuard(ctx), NullLogger<TranslationImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext)),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext));

    /// <summary>The NAVX header with the NEA magic after it: what an encrypted marketplace .app starts with.</summary>
    private static MemoryStream EncryptedApp()
    {
        var buf = new byte[256];
        "NAVX"u8.CopyTo(buf);
        "NAVX.NEA"u8.CopyTo(buf.AsSpan(36));
        return new MemoryStream(buf);
    }

    private static FileStream DkCore() => File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));

    private async Task<int> BeginAsync(Data.AppDbContext ctx, string label) =>
        await NewService(ctx).BeginReleaseAsync(new ReleaseImportMetadata(
            Label: label, Kind: "third_party", ParentReleaseId: null, ApplicationVersionId: null, Publisher: "Continia Software"));

    [Fact]
    public async Task An_encrypted_app_is_left_out_and_the_rest_imports()
    {
        await using var ctx = _db.NewContext();
        var releaseId = await BeginAsync(ctx, "Continia (mixed)");

        await using var app = DkCore();
        await using var encrypted = EncryptedApp();
        var summary = await NewService(ctx).ProcessReleaseAsync(releaseId,
        [
            new AppFileUpload("Continia Banking 29.1.1.257932 - 29.0.app", encrypted, null),
            new AppFileUpload("Microsoft_DK_Core.app", app, null),
        ]);

        summary.ModulesImported.Should().Be(1);
        summary.Unreadable.Should().ContainSingle()
            .Which.FileName.Should().Be("Continia Banking 29.1.1.257932 - 29.0.app");

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        release.Status.Should().Be("ready", "one unreadable app must not fail the apps that could be read");
        release.StatusMessage.Should().BeNull();
        ReleaseSkippedApp.Deserialize(release.SkippedAppsJson).Should().Equal(
            new ReleaseSkippedApp("Continia Banking 29.1.1.257932 - 29.0.app", ReleaseSkippedApp.EncryptedReason));
        (await read.OeModules.CountAsync(m => m.ReleaseId == releaseId)).Should().Be(1);
    }

    [Fact]
    public async Task A_release_where_nothing_could_be_read_fails_and_names_the_apps()
    {
        await using var ctx = _db.NewContext();
        var releaseId = await BeginAsync(ctx, "Continia (all encrypted)");

        await using var a = EncryptedApp();
        await using var b = EncryptedApp();
        var act = () => NewService(ctx).ProcessReleaseAsync(releaseId,
        [
            new AppFileUpload("Core.app", a, null),
            new AppFileUpload("CFI.app", b, null),
        ]);

        await act.Should().ThrowAsync<PlanValidationException>();

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        release.Status.Should().Be("failed");
        release.StatusMessage.Should().Contain("Core.app").And.Contain("CFI.app");
        release.SkippedAppsJson.Should().BeNull();
    }

    [Fact]
    public async Task A_clean_import_records_no_skipped_apps()
    {
        await using var ctx = _db.NewContext();
        var releaseId = await BeginAsync(ctx, "Clean");

        await using var app = DkCore();
        var summary = await NewService(ctx).ProcessReleaseAsync(releaseId, [new AppFileUpload("Microsoft_DK_Core.app", app, null)]);

        summary.Unreadable.Should().BeEmpty();
        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId))
            .SkippedAppsJson.Should().BeNull();
    }

    [Fact]
    public async Task Adding_modules_keeps_the_earlier_skipped_apps_and_lists_a_repeat_once()
    {
        int releaseId;
        await using (var ctx = _db.NewContext())
        {
            releaseId = await BeginAsync(ctx, "Continia (amend)");
            await using var app = DkCore();
            await using var encrypted = EncryptedApp();
            await NewService(ctx).ProcessReleaseAsync(releaseId,
            [
                new AppFileUpload("Core.app", encrypted, null),
                new AppFileUpload("Microsoft_DK_Core.app", app, null),
            ]);
        }

        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            await using var again = EncryptedApp();
            await using var other = EncryptedApp();
            await using var dup = DkCore();
            await svc.AmendReleaseAsync(releaseId,
            [
                new AppFileUpload("core.app", again, null),
                new AppFileUpload("CFI.app", other, null),
                // A byte-identical re-upload counts as present, so the amend is not "nothing readable".
                new AppFileUpload("Microsoft_DK_Core.app", dup, null),
            ]);
        }

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        release.Status.Should().Be("ready");
        ReleaseSkippedApp.Deserialize(release.SkippedAppsJson).Select(a => a.FileName)
            .Should().Equal(["core.app", "CFI.app"], "the same file name is listed once, and the earlier entry keeps its place");
    }

    [Fact]
    public async Task Adding_a_readable_copy_of_a_left_out_app_takes_it_off_the_list()
    {
        int releaseId;
        await using (var ctx = _db.NewContext())
        {
            releaseId = await BeginAsync(ctx, "Continia (replacement)");
            await using var app = DkCore();
            await using var encrypted = EncryptedApp();
            await NewService(ctx).ProcessReleaseAsync(releaseId,
            [
                new AppFileUpload("Microsoft_DK_Core.app", app, null),
                new AppFileUpload("Microsoft_OIOUBL.app", encrypted, null),
            ]);
        }

        await using (var ctx = _db.NewContext())
        {
            await using var readable = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
            await NewService(ctx).AmendReleaseAsync(releaseId, [new AppFileUpload("Microsoft_OIOUBL.app", readable, null)]);
        }

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        release.SkippedAppsJson.Should().BeNull("the app that was left out is now in the release");
        (await read.OeModules.CountAsync(m => m.ReleaseId == releaseId)).Should().Be(2);
    }

    [Fact]
    public async Task Adding_only_unreadable_apps_is_refused_and_the_release_stays_as_it_was()
    {
        int releaseId;
        await using (var ctx = _db.NewContext())
        {
            releaseId = await BeginAsync(ctx, "Continia (amend refused)");
            await using var app = DkCore();
            await NewService(ctx).ProcessReleaseAsync(releaseId, [new AppFileUpload("Microsoft_DK_Core.app", app, null)]);
        }

        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            await using var encrypted = EncryptedApp();
            var act = () => svc.AmendReleaseAsync(releaseId, [new AppFileUpload("Core.app", encrypted, null)]);
            await act.Should().ThrowAsync<PlanValidationException>();
        }

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        release.Status.Should().Be("ready");
        release.SkippedAppsJson.Should().BeNull();
    }

    [Fact]
    public void A_missing_or_malformed_list_reads_as_empty()
    {
        ReleaseSkippedApp.Deserialize(null).Should().BeEmpty();
        ReleaseSkippedApp.Deserialize("").Should().BeEmpty();
        ReleaseSkippedApp.Deserialize("{not json").Should().BeEmpty();
        ReleaseSkippedApp.Serialize([]).Should().BeNull();
    }
}
