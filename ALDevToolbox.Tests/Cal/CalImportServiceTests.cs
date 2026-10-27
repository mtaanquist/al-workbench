using System.Text;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Cal;

/// <summary>
/// End-to-end coverage for <see cref="CalImportService"/> against a real
/// Postgres database: the C/AL TXT is parsed into the same <c>oe_*</c> rows the
/// AL path produces, the release flips to <c>ready</c>, and numeric object
/// references resolve to names within the single self-contained module.
/// </summary>
public sealed class CalImportServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    static CalImportServiceTests() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private ReleaseImportService NewImporter(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext, new ALDevToolbox.Services.Translation.TranslationMemoryService(ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance), _db.NewQuotaGuard(ctx), NullLogger<TranslationImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext)),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext));

    private CalImportService NewCalImporter(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), NullLogger<CalImportService>.Instance);

    private async Task<int> ImportAsync(string txtPath, string label, string encoding = "850",
        string kind = "first_party", int? parentId = null)
    {
        int releaseId;
        await using (var ctx = _db.NewContext())
        {
            releaseId = await NewImporter(ctx).BeginReleaseAsync(
                new ReleaseImportMetadata(label, kind, parentId, null));
        }
        await using (var ctx = _db.NewContext())
        {
            await NewCalImporter(ctx).ProcessReleaseAsync(releaseId, txtPath, encoding);
        }
        return releaseId;
    }

    [Fact]
    public async Task Imports_fixture_to_ready_release_with_objects_and_source()
    {
        var releaseId = await ImportAsync(CalObjectSplitterTests.FixturePath(), "C/AL Base");

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        release.Status.Should().Be("ready");

        var module = await read.OeModules.AsNoTracking().SingleAsync(m => m.ReleaseId == releaseId);
        module.Version.Should().StartWith("NAV");

        var objects = await read.OeModuleObjects.AsNoTracking()
            .Where(o => o.ModuleId == module.Id).ToListAsync();
        objects.Should().HaveCount(6);

        var customer = objects.Single(o => o.Kind == "table" && o.ObjectId == 18);
        customer.Name.Should().Be("Customer");
        customer.SourceFileId.Should().NotBeNull();

        var symbols = await read.OeModuleSymbols.AsNoTracking()
            .Where(s => s.ObjectId == customer.Id).ToListAsync();
        symbols.Should().Contain(s => s.Kind == "table_field" && s.Name == "No." && s.Signature == "Code20");
        symbols.Should().Contain(s => s.Kind == "procedure" && s.Name == "AssistEdit");
        symbols.Should().Contain(s => s.Kind == "trigger" && s.Name == "OnInsert");

        // The stored source slice round-trips with the OEM codepage intact.
        var content = await read.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == customer.SourceFileId)
            .Select(f => f.FileContent!.Content)
            .SingleAsync();
        content.Should().Contain("OBJECT Table 18 Customer");
        content.Should().NotContain("�");
    }

    [Fact]
    public async Task Emits_call_site_references_from_bodies()
    {
        var releaseId = await ImportAsync(CalObjectSplitterTests.FixturePath(), "C/AL Base");

        await using var read = _db.NewContext();
        var module = await read.OeModules.AsNoTracking().SingleAsync(m => m.ReleaseId == releaseId);
        var customer = await read.OeModuleObjects.AsNoTracking()
            .SingleAsync(o => o.ModuleId == module.Id && o.Kind == "table" && o.ObjectId == 18);

        var refs = await read.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceObjectId == customer.Id).ToListAsync();

        // The Customer table's triggers/procedures call into other objects.
        refs.Should().Contain(r => r.ReferenceKind == "method_call" && r.TargetMemberName != null);
        // Implicit Rec field access (e.g. "No." inside OnInsert) resolves to table 18 itself.
        refs.Should().Contain(r => r.ReferenceKind == "field_access"
            && r.TargetObjectKind == "table" && r.TargetObjectId == 18);
        // Call-site refs are stamped with the calling procedure/trigger symbol.
        refs.Should().Contain(r => r.SourceSymbolId != null && r.ReferenceKind == "method_call");
    }

    [Fact]
    public async Task Static_object_receivers_resolve_to_the_named_object()
    {
        // #713: CODEUNIT::"Sales-Post" and friends used to emit nothing for the
        // object and a bogus field_access named after it on the current Rec.
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Cal", "Fixtures", "CalStaticReceivers.txt");
        var releaseId = await ImportAsync(fixturePath, "C/AL Static Receivers");

        await using var read = _db.NewContext();
        var module = await read.OeModules.AsNoTracking().SingleAsync(m => m.ReleaseId == releaseId);
        var caller = await read.OeModuleObjects.AsNoTracking()
            .SingleAsync(o => o.ModuleId == module.Id && o.Kind == "codeunit" && o.ObjectId == 50000);

        var refs = await read.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceObjectId == caller.Id).ToListAsync();

        var objectRefs = refs.Where(r => r.ReferenceKind == "property_object").ToList();
        objectRefs.Should().Contain(r => r.TargetObjectKind == "codeunit"
            && r.TargetObjectName == "Sales-Post" && r.TargetObjectId == 80);
        objectRefs.Should().Contain(r => r.TargetObjectKind == "page"
            && r.TargetObjectName == "Customer List" && r.TargetObjectId == 22);
        objectRefs.Should().Contain(r => r.TargetObjectKind == "report"
            && r.TargetObjectName == "Sales Document - Test" && r.TargetObjectId == 202);
        objectRefs.Should().Contain(r => r.TargetObjectKind == "xmlport"
            && r.TargetObjectName == "Import Data" && r.TargetObjectId == 5150);
        objectRefs.Should().Contain(r => r.TargetObjectKind == "table"
            && r.TargetObjectName == "Customer" && r.TargetObjectId == 18);

        // And none of those names leaked out as a field on some table.
        refs.Should().NotContain(r => r.ReferenceKind == "field_access"
            && (r.TargetMemberName == "Sales-Post" || r.TargetMemberName == "Customer List"
                || r.TargetMemberName == "Sales Document - Test" || r.TargetMemberName == "Import Data"));
    }

    [Fact]
    public async Task Report_dataset_and_xmlport_elements_emit_references()
    {
        // #713: the DATASET / ELEMENTS sections were never walked, so every
        // reference inside them was missing.
        var releaseId = await ImportAsync(CalDataSectionTests.FixturePath(), "C/AL Data Sections");

        await using var read = _db.NewContext();
        var module = await read.OeModules.AsNoTracking().SingleAsync(m => m.ReleaseId == releaseId);
        var objects = await read.OeModuleObjects.AsNoTracking()
            .Where(o => o.ModuleId == module.Id).ToListAsync();
        var report = objects.Single(o => o.Kind == "report" && o.ObjectId == 50000);
        var xmlport = objects.Single(o => o.Kind == "xmlport" && o.ObjectId == 50001);

        var symbols = await read.OeModuleSymbols.AsNoTracking()
            .Where(s => s.ObjectId == report.Id).ToListAsync();
        symbols.Should().Contain(s => s.Kind == "trigger" && s.Name == "OnPreDataItem");
        symbols.Should().Contain(s => s.Kind == "trigger" && s.Name == "OnAfterGetRecord");

        var reportRefs = await read.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceObjectId == report.Id).ToListAsync();

        // Each data item's DataItemTable binds its table by name, like a global would.
        reportRefs.Should().Contain(r => r.ReferenceKind == "variable_type"
            && r.TargetObjectKind == "table" && r.TargetObjectId == 18 && r.TargetObjectName == "Customer");
        reportRefs.Should().Contain(r => r.ReferenceKind == "variable_type"
            && r.TargetObjectKind == "table" && r.TargetObjectId == 37 && r.TargetObjectName == "Sales Line");

        // A data item trigger's calls carry the trigger symbol.
        reportRefs.Should().Contain(r => r.ReferenceKind == "method_call"
            && r.TargetObjectKind == "codeunit" && r.TargetObjectId == 80
            && r.TargetMemberName == "CheckCustomer" && r.SourceSymbolId != null);
        // The nested data item's trigger resolves its field against its own table.
        reportRefs.Should().Contain(r => r.ReferenceKind == "field_access"
            && r.TargetObjectKind == "table" && r.TargetObjectId == 37
            && r.TargetMemberName == "Line Amount");
        // A column's SourceExpr is code too — it calls a codeunit.
        reportRefs.Should().Contain(r => r.ReferenceKind == "method_call"
            && r.TargetObjectKind == "codeunit" && r.TargetObjectId == 80
            && r.TargetMemberName == "CalcAmount");

        var xmlportRefs = await read.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceObjectId == xmlport.Id).ToListAsync();
        xmlportRefs.Should().Contain(r => r.ReferenceKind == "variable_type"
            && r.TargetObjectKind == "table" && r.TargetObjectId == 18);
        xmlportRefs.Should().Contain(r => r.ReferenceKind == "method_call"
            && r.TargetObjectKind == "codeunit" && r.TargetObjectId == 80
            && r.TargetMemberName == "PrepareCustomer");
    }

    [Fact]
    public async Task Columns_are_file_relative_for_code_on_the_begin_line()
    {
        // #713: a body starting on its BEGIN line used to be numbered from the
        // body, so the stamped column pointed at the wrong character.
        var releaseId = await ImportAsync(CalDataSectionTests.FixturePath(), "C/AL Begin Line");

        await using var read = _db.NewContext();
        var module = await read.OeModules.AsNoTracking().SingleAsync(m => m.ReleaseId == releaseId);
        var report = await read.OeModuleObjects.AsNoTracking()
            .SingleAsync(o => o.ModuleId == module.Id && o.Kind == "report" && o.ObjectId == 50000);

        var onPreReport = await read.OeModuleSymbols.AsNoTracking()
            .SingleAsync(s => s.ObjectId == report.Id && s.Kind == "trigger" && s.Name == "OnPreReport");
        var call = await read.OeModuleReferences.AsNoTracking()
            .SingleAsync(r => r.SourceSymbolId == onPreReport.Id && r.TargetMemberName == "CheckCustomer");

        // "    OnPreReport=BEGIN SalesPost.CheckCustomer(''); END;" — the called
        // member starts at file column 33.
        var line = (await read.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == report.SourceFileId)
            .Select(f => f.FileContent!.Content)
            .SingleAsync()).Split('\n')[call.LineNumber!.Value - 1];
        line.Should().Contain("OnPreReport=BEGIN");
        call.ColumnNumber.Should().Be(33);
        line.Substring(call.ColumnNumber!.Value - 1).Should().StartWith("CheckCustomer");
    }

    [Fact]
    public async Task Emits_system_references_for_builtin_calls()
    {
        // #279: a built-in call on a receiver (Cust.INSERT) becomes a system
        // reference in oe_module_system_references, not a normal reference.
        const string cal =
            "OBJECT Codeunit 50000 Mgt\r\n"
          + "{\r\n"
          + "  CODE\r\n"
          + "  {\r\n"
          + "    VAR\r\n"
          + "      Cust@1000 : Record 18;\r\n"
          + "\r\n"
          + "    PROCEDURE Foo@1();\r\n"
          + "    BEGIN\r\n"
          + "      Cust.INSERT();\r\n"
          + "    END;\r\n"
          + "\r\n"
          + "    BEGIN\r\n"
          + "    END.\r\n"
          + "  }\r\n"
          + "}\r\n";
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, cal, Encoding.ASCII);
        try
        {
            var releaseId = await ImportAsync(path, "Sys");
            await using var read = _db.NewContext();
            var sysRefs = await read.OeModuleSystemReferences.AsNoTracking()
                .Where(r => r.Module!.ReleaseId == releaseId)
                .ToListAsync();
            sysRefs.Should().Contain(r => r.SystemMethodName.ToUpper() == "INSERT"
                && r.ReferenceKind == "method_call"
                && r.TargetObjectId == 18);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Paren_less_calls_are_classified_as_method_calls()
    {
        // #712: `Cust.MODIFY;` (built-in) and `Mgt.Bar;` (a procedure on the
        // receiver) are calls, but C/AL writes them exactly like a field read.
        // The built-in half is decided in the walker; the user-procedure half
        // by the import's post-pass, once the module's symbols are stored.
        const string cal =
            "OBJECT Codeunit 50000 Mgt\r\n"
          + "{\r\n"
          + "  CODE\r\n"
          + "  {\r\n"
          + "    VAR\r\n"
          + "      Cust@1000 : Record 18;\r\n"
          + "      Helper@1001 : Codeunit 50001;\r\n"
          + "\r\n"
          + "    PROCEDURE Foo@1();\r\n"
          + "    BEGIN\r\n"
          + "      Cust.MODIFY;\r\n"
          + "      Helper.Bar;\r\n"
          + "      Cust.\"No.\";\r\n"
          + "    END;\r\n"
          + "\r\n"
          + "    BEGIN\r\n"
          + "    END.\r\n"
          + "  }\r\n"
          + "}\r\n"
          + "OBJECT Codeunit 50001 Helper\r\n"
          + "{\r\n"
          + "  CODE\r\n"
          + "  {\r\n"
          + "    PROCEDURE Bar@1();\r\n"
          + "    BEGIN\r\n"
          + "    END;\r\n"
          + "\r\n"
          + "    BEGIN\r\n"
          + "    END.\r\n"
          + "  }\r\n"
          + "}\r\n"
          + "OBJECT Table 18 Customer\r\n"
          + "{\r\n"
          + "  FIELDS\r\n"
          + "  {\r\n"
          + "    { 1   ;   ;No.                 ;Code20         }\r\n"
          + "  }\r\n"
          + "  CODE\r\n"
          + "  {\r\n"
          + "    BEGIN\r\n"
          + "    END.\r\n"
          + "  }\r\n"
          + "}\r\n";
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, cal, Encoding.ASCII);
        try
        {
            var releaseId = await ImportAsync(path, "Paren-less");
            await using var read = _db.NewContext();

            var sysRefs = await read.OeModuleSystemReferences.AsNoTracking()
                .Where(r => r.Module!.ReleaseId == releaseId).ToListAsync();
            sysRefs.Should().Contain(r => r.SystemMethodName.ToUpper() == "MODIFY"
                && r.ReferenceKind == "method_call" && r.TargetObjectId == 18);

            var refs = await read.OeModuleReferences.AsNoTracking()
                .Where(r => r.Module!.ReleaseId == releaseId).ToListAsync();
            refs.Should().Contain(r => r.TargetMemberName == "Bar"
                && r.TargetObjectKind == "codeunit" && r.TargetObjectId == 50001
                && r.ReferenceKind == "method_call" && r.TargetMemberKind == "procedure");
            // A genuine field read with the same shape stays a read.
            refs.Should().Contain(r => r.TargetMemberName == "No."
                && r.TargetObjectId == 18 && r.ReferenceKind == "field_access");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BackfillSystemReferencesAsync_repopulates_cal_system_references()
    {
        // #291: simulate a pre-#279 C/AL release (no system refs), then backfill
        // from stored source and confirm the Cust.INSERT system ref comes back.
        const string cal =
            "OBJECT Codeunit 50000 Mgt\r\n"
          + "{\r\n"
          + "  CODE\r\n"
          + "  {\r\n"
          + "    VAR\r\n"
          + "      Cust@1000 : Record 18;\r\n"
          + "\r\n"
          + "    PROCEDURE Foo@1();\r\n"
          + "    BEGIN\r\n"
          + "      Cust.INSERT();\r\n"
          + "    END;\r\n"
          + "\r\n"
          + "    BEGIN\r\n"
          + "    END.\r\n"
          + "  }\r\n"
          + "}\r\n";
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, cal, Encoding.ASCII);
        try
        {
            var releaseId = await ImportAsync(path, "Backfill");

            // Wipe to mimic a release imported before system references existed.
            await using (var wipe = _db.NewContext())
            {
                await wipe.Database.ExecuteSqlRawAsync(
                    "DELETE FROM oe_module_system_references WHERE module_id IN (SELECT id FROM oe_modules WHERE release_id = {0});",
                    releaseId);
            }
            await using (var check = _db.NewContext())
            {
                (await check.OeModuleSystemReferences.AsNoTracking()
                    .CountAsync(r => r.Module!.ReleaseId == releaseId)).Should().Be(0);
            }

            await using (var b = _db.NewContext())
            {
                await NewCalImporter(b).BackfillSystemReferencesAsync(releaseId);
            }

            await using var read = _db.NewContext();
            var sysRefs = await read.OeModuleSystemReferences.AsNoTracking()
                .Where(r => r.Module!.ReleaseId == releaseId)
                .ToListAsync();
            sysRefs.Should().Contain(r => r.SystemMethodName.ToUpper() == "INSERT"
                && r.ReferenceKind == "method_call"
                && r.TargetObjectId == 18
                && r.SourceSymbolId != null);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Stores_per_object_version_list_from_object_properties()
    {
        // The C/SIDE "Version List" property is per-object; the import stamps it
        // onto each oe_module_objects row so it can be shown + searched (#271).
        const string cal =
            "OBJECT Table 50000 Acme Setup\r\n"
          + "{\r\n"
          + "  OBJECT-PROPERTIES\r\n"
          + "  {\r\n"
          + "    Date=01-01-25;\r\n"
          + "    Time=12:00:00;\r\n"
          + "    Version List=NAVW111.00,CON001;\r\n"
          + "  }\r\n"
          + "  PROPERTIES\r\n"
          + "  {\r\n"
          + "  }\r\n"
          + "  FIELDS\r\n"
          + "  {\r\n"
          + "    { 1   ;   ;Code                ;Code10        }\r\n"
          + "  }\r\n"
          + "  CODE\r\n"
          + "  {\r\n"
          + "    BEGIN\r\n"
          + "    END.\r\n"
          + "  }\r\n"
          + "}\r\n";
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, cal, Encoding.ASCII);
        try
        {
            var releaseId = await ImportAsync(path, "VL");
            await using var read = _db.NewContext();
            var obj = await read.OeModuleObjects.AsNoTracking()
                .SingleAsync(o => o.Module!.ReleaseId == releaseId && o.ObjectId == 50000);
            obj.VersionList.Should().NotBeNull();
            obj.VersionList!.Should().Contain("CON001");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Resolves_object_targets_by_id_within_the_release()
    {
        // A tiny self-contained export: a codeunit with a global typed to the
        // table that ships alongside it. The id post-pass must name the target.
        const string cal =
            "OBJECT Table 18 Customer\r\n{\r\n  FIELDS\r\n  {\r\n    { 1 ; ;No. ;Code20 }\r\n  }\r\n  CODE\r\n  {\r\n    BEGIN\r\n    END.\r\n  }\r\n}\r\n" +
            "OBJECT Codeunit 50000 My Mgt\r\n{\r\n  CODE\r\n  {\r\n    VAR\r\n      Cust@1000 : Record 18;\r\n    BEGIN\r\n    END.\r\n  }\r\n}\r\n";
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, cal, Encoding.ASCII);
        try
        {
            var releaseId = await ImportAsync(path, "Synthetic");

            await using var read = _db.NewContext();
            var module = await read.OeModules.AsNoTracking().SingleAsync(m => m.ReleaseId == releaseId);

            var global = await read.OeModuleVariables.AsNoTracking()
                .SingleAsync(v => v.ModuleId == module.Id && v.Name == "Cust");
            global.TargetObjectKind.Should().Be("table");
            global.TargetObjectId.Should().Be(18);
            global.TargetObjectName.Should().Be("Customer");        // resolved by id
            global.TargetAppId.Should().Be(module.AppId);

            var reference = await read.OeModuleReferences.AsNoTracking()
                .SingleAsync(r => r.ModuleId == module.Id && r.ReferenceKind == "variable_type");
            reference.TargetObjectId.Should().Be(18);
            reference.TargetObjectName.Should().Be("Customer");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Call_site_reference_line_is_slice_relative_not_body_relative()
    {
        // The call sits on line 10 of the object slice. The walker numbers it
        // from the body start, so without the slice offset it would be stored
        // against line ~2 (the OBJECT-PROPERTIES / VAR area) — the bug that made
        // find-references show "Version List=…" snippets.
        const string cal =
            "OBJECT Codeunit 50000 Mgt\r\n"  // 1
          + "{\r\n"                            // 2
          + "  CODE\r\n"                       // 3
          + "  {\r\n"                          // 4
          + "    VAR\r\n"                      // 5
          + "      Helper@1000 : Codeunit 50001;\r\n" // 6
          + "\r\n"                             // 7
          + "    PROCEDURE Foo@1();\r\n"       // 8
          + "    BEGIN\r\n"                    // 9
          + "      Helper.DoStuff();\r\n"      // 10  <- the call
          + "    END;\r\n"                     // 11
          + "\r\n"                             // 12
          + "    BEGIN\r\n"                    // 13
          + "    END.\r\n"                     // 14
          + "  }\r\n"                          // 15
          + "}\r\n";                           // 16
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, cal, Encoding.ASCII);
        try
        {
            var releaseId = await ImportAsync(path, "Lines");
            await using var read = _db.NewContext();
            var module = await read.OeModules.AsNoTracking().SingleAsync(m => m.ReleaseId == releaseId);
            var call = await read.OeModuleReferences.AsNoTracking()
                .SingleAsync(r => r.ModuleId == module.Id && r.ReferenceKind == "method_call"
                    && r.TargetMemberName == "DoStuff");
            call.TargetObjectKind.Should().Be("codeunit");
            call.TargetObjectId.Should().Be(50001);
            call.LineNumber.Should().Be(10);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Resolves_nav_platform_virtual_table_names()
    {
        // A global typed to a NAV virtual table (2000000038 = AllObj) resolves by
        // the NAV map — not the BC map, which names that id differently.
        const string cal =
            "OBJECT Codeunit 50000 Mgt\r\n{\r\n  CODE\r\n  {\r\n    VAR\r\n      Obj@1000 : Record 2000000038;\r\n    BEGIN\r\n    END.\r\n  }\r\n}\r\n";
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, cal, Encoding.ASCII);
        try
        {
            var releaseId = await ImportAsync(path, "NAV VT");
            await using var read = _db.NewContext();
            var module = await read.OeModules.AsNoTracking().SingleAsync(m => m.ReleaseId == releaseId);
            var global = await read.OeModuleVariables.AsNoTracking()
                .SingleAsync(v => v.ModuleId == module.Id && v.Name == "Obj");
            global.TargetObjectId.Should().Be(2000000038);
            global.TargetObjectName.Should().Be("AllObj");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Reimport_under_a_new_label_is_independent()
    {
        // Two releases (base + customer) from the same file get distinct
        // synthetic AppIds — each self-contained, no cross-release shadowing.
        var baseId = await ImportAsync(CalObjectSplitterTests.FixturePath(), "Base");
        var custId = await ImportAsync(CalObjectSplitterTests.FixturePath(), "Customer",
            kind: "project", parentId: baseId);

        await using var read = _db.NewContext();
        var baseApp = await read.OeModules.AsNoTracking().Where(m => m.ReleaseId == baseId).Select(m => m.AppId).SingleAsync();
        var custApp = await read.OeModules.AsNoTracking().Where(m => m.ReleaseId == custId).Select(m => m.AppId).SingleAsync();
        baseApp.Should().NotBe(custApp);
    }

    [Fact]
    public async Task Over_quota_refusal_marks_the_release_failed_with_a_readable_message()
    {
        // #1261: the quota check ran before the try that stamps "failed", so a
        // refusal left the release "ingesting", where Retry cannot reach it.
        int releaseId;
        await using (var ctx = _db.NewContext())
        {
            releaseId = await NewImporter(ctx).BeginReleaseAsync(
                new ReleaseImportMetadata("Over quota", "first_party", null, null));
        }

        await using (var ctx = _db.NewContext())
        {
            // A 0 MB default quota puts every org at or over quota.
            var settings = await ctx.SystemSettings.FirstOrDefaultAsync(s => s.Id == 1);
            if (settings is null)
            {
                settings = new ALDevToolbox.Domain.Entities.SystemSettings { Id = 1 };
                ctx.SystemSettings.Add(settings);
            }
            settings.DefaultStorageQuotaMb = 0;
            await ctx.SaveChangesAsync();
            // BeginReleaseAsync cached an under-quota decision; drop it.
            _db.NewQuotaGuard(ctx).Invalidate(TestDb.DefaultOrgId);
        }

        await using (var ctx = _db.NewContext())
        {
            var act = () => NewCalImporter(ctx).ProcessReleaseAsync(
                releaseId, CalObjectSplitterTests.FixturePath(), "850");
            await act.Should().ThrowAsync<PlanValidationException>();
        }

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        release.Status.Should().Be("failed");
        release.StatusMessage.Should().Contain("storage quota");
    }
}
