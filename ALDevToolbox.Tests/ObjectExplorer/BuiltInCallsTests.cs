using System.Text.Json;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// "Find built-in calls" on an object: Insert / Get / RunModal and the rest,
/// through a variable of the object's type or the static form
/// (<c>Page.RunModal(Page::"X")</c>), reached from a right-click on any name in
/// the code. A synthetic <c>.app</c> runs through the real importer, so the
/// rows and columns are the ones production writes.
/// </summary>
public sealed class BuiltInCallsTests : IDisposable
{
    private const string AppId = "ffffffff-0000-0000-0000-000000000006";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_static_dispatch_is_found_as_a_built_in_call_on_the_page()
    {
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var rows = await NewReferences(read).FindSystemReferencesAsync(releaseId,
            new FindSystemReferencesQuery(Guid.Parse(AppId), "page", 50110, "CRONUS Thing Card"));

        rows.Should().ContainSingle().Which.MemberName.Should().Be("RunModal");
    }

    [Fact]
    public async Task Methods_narrow_the_list_without_regard_to_case()
    {
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var rows = await NewReferences(read).FindSystemReferencesAsync(releaseId,
            new FindSystemReferencesQuery(Guid.Parse(AppId), "table", 50100, "CRONUS Thing",
                SystemMethodNames: ["insert", "GET"]));

        rows.Select(r => r.MemberName).Should().BeEquivalentTo(["Insert", "Get"]);
    }

    [Fact]
    public async Task Right_clicking_a_record_variable_lists_the_calls_on_its_table()
    {
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var (fileId, lines) = await SetterFileAsync(read, releaseId);
        var line = Array.FindIndex(lines, l => l.Contains("ThingRec.Insert")) + 1;
        var column = lines[line - 1].IndexOf("ThingRec", StringComparison.Ordinal) + 2;

        var session = await NewSessions(read).CreateSystemReferencesAtPositionAsync(fileId, line, column, "owner");

        session.Should().NotBeNull();
        session!.Kind.Should().Be("built_in_calls");
        session.TargetName.Should().Be("CRONUS Thing");
        session.Results.Select(r => r.MemberName).Should().BeEquivalentTo(["Insert", "Get"]);
    }

    [Fact]
    public async Task Right_clicking_an_object_name_in_the_code_lists_the_calls_on_it()
    {
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var (fileId, lines) = await SetterFileAsync(read, releaseId);
        var line = Array.FindIndex(lines, l => l.Contains("Page::")) + 1;
        var column = lines[line - 1].IndexOf("\"CRONUS Thing Card\"", StringComparison.Ordinal) + 3;

        var session = await NewSessions(read).CreateSystemReferencesAtPositionAsync(fileId, line, column, "owner");

        session.Should().NotBeNull();
        session!.TargetName.Should().Be("CRONUS Thing Card");
        session.Results.Should().ContainSingle().Which.MemberName.Should().Be("RunModal");
    }

    [Fact]
    public async Task A_variable_named_after_its_table_lists_the_calls_on_the_table()
    {
        // Gadget: Record Gadget, like Customer: Record Customer, is the usual
        // AL spelling; the type name must not resolve back to the variable.
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var (fileId, lines) = await SetterFileAsync(read, releaseId);
        var line = Array.FindIndex(lines, l => l.Contains("Gadget.Modify")) + 1;
        var column = lines[line - 1].IndexOf("Gadget", StringComparison.Ordinal) + 2;

        var session = await NewSessions(read).CreateSystemReferencesAtPositionAsync(fileId, line, column, "owner");

        session.Should().NotBeNull();
        session!.TargetName.Should().Be("Gadget");
        session.Results.Should().ContainSingle().Which.MemberName.Should().Be("Modify");
    }

    [Fact]
    public async Task A_name_that_is_not_an_object_finds_nothing()
    {
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var (fileId, lines) = await SetterFileAsync(read, releaseId);
        var line = Array.FindIndex(lines, l => l.Contains("begin")) + 1;

        (await NewSessions(read).CreateSystemReferencesAtPositionAsync(fileId, line, 10, "owner"))
            .Should().BeNull();
    }

    private static async Task<(long FileId, string[] Lines)> SetterFileAsync(Data.AppDbContext ctx, int releaseId)
    {
        var file = await ctx.OeModuleFiles.AsNoTracking()
            .Where(f => f.Module!.ReleaseId == releaseId && f.Path == "src/Setter.Codeunit.al")
            .Select(f => new { f.Id, f.FileContent!.Content })
            .SingleAsync();
        return (file.Id, file.Content.Replace("\r\n", "\n").Split('\n'));
    }

    private async Task<int> ImportAsync()
    {
        await using var ctx = _db.NewContext();
        using var stream = new MemoryStream(App());
        var summary = await NewImporter(ctx).ImportReleaseAsync(new ReleaseImportRequest(
            "Built-in calls fixture", "third_party", null, null, [new AppFileUpload("app.app", stream, null)]));
        return summary.ReleaseId;
    }

    private static byte[] App()
    {
        var symbols = JsonSerializer.Serialize(new
        {
            RuntimeVersion = "14.0",
            Tables = new object[]
            {
                new
                {
                    Id = 50100,
                    Name = "CRONUS Thing",
                    ReferenceSourceFileName = "src/Thing.Table.al",
                    Fields = new object[] { new { Id = 1, Name = "No.", TypeDefinition = new { Name = "Code[20]" } } },
                },
                new
                {
                    Id = 50102,
                    Name = "Gadget",
                    ReferenceSourceFileName = "src/Gadget.Table.al",
                    Fields = new object[] { new { Id = 1, Name = "No.", TypeDefinition = new { Name = "Code[20]" } } },
                },
            },
            Pages = new object[]
            {
                new { Id = 50110, Name = "CRONUS Thing Card", ReferenceSourceFileName = "src/ThingCard.Page.al" },
            },
            Codeunits = new object[]
            {
                new
                {
                    Id = 50101,
                    Name = "CRONUS Setter",
                    ReferenceSourceFileName = "src/Setter.Codeunit.al",
                    Methods = new object[] { new { Name = "Run", Parameters = Array.Empty<object>() } },
                    Variables = new object[]
                    {
                        new
                        {
                            Name = "ThingRec",
                            TypeDefinition = new { Name = "Record", Subtype = new { Name = "CRONUS Thing", Id = 50100, ModuleId = AppId } },
                        },
                    },
                },
            },
        });
        var source = new Dictionary<string, string>
        {
            ["src/Thing.Table.al"] = """
                table 50100 "CRONUS Thing"
                {
                    fields
                    {
                        field(1; "No."; Code[20]) { }
                    }
                }
                """,
            ["src/Gadget.Table.al"] = """
                table 50102 Gadget
                {
                    fields
                    {
                        field(1; "No."; Code[20]) { }
                    }
                }
                """,
            ["src/ThingCard.Page.al"] = """
                page 50110 "CRONUS Thing Card"
                {
                    SourceTable = "CRONUS Thing";
                }
                """,
            ["src/Setter.Codeunit.al"] = """
                codeunit 50101 "CRONUS Setter"
                {
                    var
                        ThingRec: Record "CRONUS Thing";

                    procedure Run()
                    var
                        Gadget: Record Gadget;
                    begin
                        Gadget.Modify();
                        ThingRec.Insert();
                        ThingRec.Get('A');
                        Page.RunModal(Page::"CRONUS Thing Card", ThingRec);
                    end;
                }
                """,
        };
        return SyntheticApp.Build(AppId, "CRONUS Calls", "CRONUS", "1.0.0.0",
            symbolReferenceJson: symbols, source: source);
    }

    private ReleaseImportService NewImporter(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext,
                new ALDevToolbox.Services.Translation.TranslationMemoryService(ctx, _db.OrgContext,
                    NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
                _db.NewQuotaGuard(ctx), NullLogger<TranslationImportService>.Instance, new ProjectAccess(ctx, _db.OrgContext)),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance, new ProjectAccess(ctx, _db.OrgContext));

    private ReferenceQueryService NewReferences(Data.AppDbContext ctx) =>
        new(ctx, new ProjectAccess(ctx, _db.OrgContext), _db.OrgContext, NullLogger<ReferenceQueryService>.Instance);

    private ReferenceSessionService NewSessions(Data.AppDbContext ctx) =>
        new(new Microsoft.Extensions.Caching.Memory.MemoryCache(
                Microsoft.Extensions.Options.Options.Create(
                    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            NewReferences(ctx), ctx, new ReferenceResolver(ctx), new ProjectAccess(ctx, _db.OrgContext),
            NullLogger<ReferenceSessionService>.Instance);
}
