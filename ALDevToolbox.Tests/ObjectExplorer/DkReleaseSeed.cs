using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.Translation;
using ALDevToolbox.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The release most Object Explorer tests start from: the real
/// <c>Microsoft_DK_Core.app</c> and <c>Microsoft_OIOUBL.app</c> fixtures
/// imported into one first-party release labelled "BC 25.18 DK".
///
/// Importing them costs over a second, and ObjectExplorerServiceTests and
/// ExplorerTreeTests did it in nearly every test (#1552). The import now runs
/// once per test process into a seeded template, and each test gets a clone.
/// It still goes through <see cref="ReleaseImportService"/>, so the rows are
/// exactly what production writes.
/// </summary>
internal static class DkReleaseSeed
{
    public const string Label = "BC 25.18 DK";

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    /// <summary>A fresh fixture that already holds the release, and the release's id.</summary>
    public static TestDb NewDatabase(out int releaseId) =>
        TestDb.FromSeed("object-explorer-dk-release", ImportAsync, out releaseId);

    /// <summary>Imports the release into <paramref name="db"/> the slow way, for a fixture that already holds rows.</summary>
    public static async Task<int> ImportAsync(TestDb db)
    {
        await using var ctx = db.NewContext();
        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var summary = await NewImporter(db, ctx).ImportReleaseAsync(new ReleaseImportRequest(
            Label: Label, Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[]
            {
                new AppFileUpload("Microsoft_DK_Core.app", s1, null),
                new AppFileUpload("Microsoft_OIOUBL.app", s2, null),
            }));
        return summary.ReleaseId;
    }

    private static ReleaseImportService NewImporter(TestDb db, Data.AppDbContext ctx) =>
        new(ctx, db.OrgContext, db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, db.OrgContext,
                new TranslationMemoryService(ctx, db.OrgContext, NullLogger<TranslationMemoryService>.Instance),
                db.NewQuotaGuard(ctx), NullLogger<TranslationImportService>.Instance, new ProjectAccess(ctx, db.OrgContext)),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance, new ProjectAccess(ctx, db.OrgContext));
}
