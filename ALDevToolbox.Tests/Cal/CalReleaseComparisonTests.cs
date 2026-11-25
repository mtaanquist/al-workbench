using System.Text;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Cal;

/// <summary>
/// Object-level Base-vs-Customer comparison over two self-contained C/AL
/// releases: objects line up by (kind, id) and are classified by their
/// source-slice hash — modified / unchanged / added / removed.
/// </summary>
public sealed class CalReleaseComparisonTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    static CalReleaseComparisonTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private async Task<int> ImportAsync(string cal, string label, string kind, int? parentId)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, cal, Encoding.ASCII);
        try
        {
            int releaseId;
            await using (var ctx = _db.NewContext())
                releaseId = await new ReleaseImportService(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
                    new TranslationImportService(ctx, _db.OrgContext, new ALDevToolbox.Services.Translation.TranslationMemoryService(ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance), _db.NewQuotaGuard(ctx), NullLogger<TranslationImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext)),
                    new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext))
                    .BeginReleaseAsync(new ReleaseImportMetadata(label, kind, parentId, null));
            await using (var ctx = _db.NewContext())
                await new CalImportService(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
                    NullLogger<CalImportService>.Instance)
                    .ProcessReleaseAsync(releaseId, path, "850");
            return releaseId;
        }
        finally { File.Delete(path); }
    }

    private static string Obj(string header, string body) => $"OBJECT {header}\r\n{{\r\n{body}\r\n}}\r\n";

    [Fact]
    public async Task Classifies_objects_by_kind_id_and_hash()
    {
        // Base: Table 18 (v1), Codeunit 50000 (shared), Page 21 (base-only).
        var baseCal =
            Obj("Table 18 Customer", "  FIELDS\r\n  {\r\n    { 1 ; ;No. ;Code20 }\r\n  }")
          + Obj("Codeunit 50000 Mgt", "  CODE\r\n  {\r\n    BEGIN\r\n    END.\r\n  }")
          + Obj("Page 21 Customer Card", "  PROPERTIES\r\n  {\r\n    SourceTable=Table18;\r\n  }");

        // Customer: Table 18 (v2, extra field → modified), Codeunit 50000 (identical
        // → unchanged), Table 50001 (customer-only → added). No Page 21 → removed.
        var custCal =
            Obj("Table 18 Customer", "  FIELDS\r\n  {\r\n    { 1 ; ;No. ;Code20 }\r\n    { 2 ; ;Name ;Text50 }\r\n  }")
          + Obj("Codeunit 50000 Mgt", "  CODE\r\n  {\r\n    BEGIN\r\n    END.\r\n  }")
          + Obj("Table 50001 Loyalty", "  FIELDS\r\n  {\r\n    { 1 ; ;Code ;Code20 }\r\n  }");

        var baseId = await ImportAsync(baseCal, "Base", "first_party", null);
        var custId = await ImportAsync(custCal, "Customer", "project", baseId);

        await using var ctx = _db.NewContext();
        var svc = new ReleaseComparisonService(ctx, new ProjectAccess(ctx, _db.OrgContext), NullLogger<ReleaseComparisonService>.Instance);
        var rows = (await svc.CompareReleaseObjectsAsync(baseId, custId, includeUnchanged: true)).Rows;

        rows.Single(r => r.Kind == "table" && r.ObjectId == 18).Status.Should().Be("modified");
        rows.Single(r => r.Kind == "table" && r.ObjectId == 18).LeftFileId.Should().NotBeNull();
        rows.Single(r => r.Kind == "table" && r.ObjectId == 18).RightFileId.Should().NotBeNull();

        rows.Single(r => r.Kind == "codeunit" && r.ObjectId == 50000).Status.Should().Be("unchanged");
        rows.Single(r => r.Kind == "page" && r.ObjectId == 21).Status.Should().Be("removed");
        rows.Single(r => r.Kind == "table" && r.ObjectId == 50001).Status.Should().Be("added");

        // #1228: by default the unchanged bulk is left out, but still counted.
        var changed = await svc.CompareReleaseObjectsAsync(baseId, custId);
        changed.Rows.Should().NotContain(r => r.Status == "unchanged");
        changed.Rows.Should().HaveCount(3);
        changed.ObjectCount.Should().Be(4);
        changed.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Object_compare_is_capped_and_narrows_by_kind_and_name_before_the_cap()
    {
        // #1228: the object diff used to return every object of both releases.
        var baseCal =
            Obj("Table 18 Customer", "  FIELDS\r\n  {\r\n    { 1 ; ;No. ;Code20 }\r\n  }")
          + Obj("Page 21 Customer Card", "  PROPERTIES\r\n  {\r\n    SourceTable=Table18;\r\n  }");
        var custCal =
            Obj("Table 50001 Loyalty", "  FIELDS\r\n  {\r\n    { 1 ; ;Code ;Code20 }\r\n  }")
          + Obj("Table 50002 Loyalty Tier", "  FIELDS\r\n  {\r\n    { 1 ; ;Code ;Code20 }\r\n  }");

        var baseId = await ImportAsync(baseCal, "Base capped", "first_party", null);
        var custId = await ImportAsync(custCal, "Customer capped", "project", baseId);

        await using var ctx = _db.NewContext();
        var svc = new ReleaseComparisonService(ctx, new ProjectAccess(ctx, _db.OrgContext), NullLogger<ReleaseComparisonService>.Instance);

        var capped = await svc.CompareReleaseObjectsAsync(baseId, custId, take: 2);
        capped.Rows.Should().HaveCount(2);
        capped.Truncated.Should().BeTrue("four objects changed and the cap is two");

        var exact = await svc.CompareReleaseObjectsAsync(baseId, custId, take: 4);
        exact.Truncated.Should().BeFalse("an exactly-fitting result is not cut");

        var tables = await svc.CompareReleaseObjectsAsync(baseId, custId, take: 2, kind: "table", nameContains: "loyalty");
        tables.Rows.Select(r => r.ObjectId).Should().Equal(50001, 50002);
        tables.Truncated.Should().BeFalse("the filters narrow the set before the cap");
    }
}
