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
    public async Task Picking_a_method_queries_that_method_again()
    {
        // A list cut at the cap cannot be filtered in hand, so the picker asks
        // the server; the answer keeps the methods to offer next.
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var (fileId, lines) = await SetterFileAsync(read, releaseId);
        var line = Array.FindIndex(lines, l => l.Contains("ThingRec.Insert")) + 1;
        var column = lines[line - 1].IndexOf("ThingRec", StringComparison.Ordinal) + 2;
        var sessions = NewSessions(read);
        var session = await sessions.CreateSystemReferencesAtPositionAsync(fileId, line, column, "owner");

        var narrowed = await sessions.NarrowSystemReferencesAsync(session!.Token, "owner", "get");

        narrowed.Should().NotBeNull();
        narrowed!.Results.Should().ContainSingle().Which.MemberName.Should().Be("Get");
        narrowed.Method.Should().Be("get");
        narrowed.MethodNames.Should().BeEquivalentTo(["Insert", "Get"]);
        narrowed.TargetName.Should().Be("CRONUS Thing");

        var widened = await sessions.NarrowSystemReferencesAsync(narrowed.Token, "owner", "");
        widened!.Results.Select(r => r.MemberName).Should().BeEquivalentTo(["Insert", "Get"]);
        widened.Method.Should().BeNull();
    }

    /// <summary>
    /// OEX-5: each pick used to cache another full session under a new token
    /// for half an hour. A pick now replaces the list under the token it came
    /// from, so flicking through the methods holds one session, not one per pick.
    /// </summary>
    [Fact]
    public async Task Picking_methods_keeps_one_cached_session_under_the_same_token()
    {
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var (fileId, lines) = await SetterFileAsync(read, releaseId);
        var line = Array.FindIndex(lines, l => l.Contains("ThingRec.Insert")) + 1;
        var column = lines[line - 1].IndexOf("ThingRec", StringComparison.Ordinal) + 2;
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var sessions = NewSessions(read, cache);
        var session = await sessions.CreateSystemReferencesAtPositionAsync(fileId, line, column, "owner");
        var before = cache.Count;

        var narrowed = await sessions.NarrowSystemReferencesAsync(session!.Token, "owner", "Get");
        var widened = await sessions.NarrowSystemReferencesAsync(narrowed!.Token, "owner", "");

        narrowed.Token.Should().Be(session.Token);
        widened!.Token.Should().Be(session.Token);
        cache.Count.Should().Be(before, "a pick replaces the cached list rather than adding one");
        (await sessions.GetAsync(session.Token, "owner"))!.Method.Should().BeNull("the last pick is what the token now holds");
    }

    [Fact]
    public async Task A_cut_list_offers_every_method_even_those_called_only_past_the_cut()
    {
        // The list keeps the alphabetically first objects. CRONUS Alpha's two
        // Insert calls fill a cap of two; Modify and Delete sit past the cut,
        // so a picker built from the rows in hand could not offer them (#1543).
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var entryId = await ObjectIdAsync(read, releaseId, "CRONUS Entry");
        var sessions = NewSessions(read);
        sessions.MaxMatches = 2;

        var session = await sessions.CreateSystemReferencesFromObjectAsync(entryId, "owner");

        session.Should().NotBeNull();
        session!.Truncated.Should().BeTrue();
        session.Results.Should().HaveCount(2).And.OnlyContain(r => r.SourceObjectName == "CRONUS Alpha");
        session.MethodNames.Should().Equal("Insert", "Delete", "Modify");
        session.MethodCounts.Should().Equal(3, 1, 1);

        var narrowed = await sessions.NarrowSystemReferencesAsync(session.Token, "owner", "Delete");

        narrowed!.Results.Should().ContainSingle().Which.SourceObjectName.Should().Be("CRONUS Zulu");
        narrowed.MethodNames.Should().Equal("Insert", "Delete", "Modify");
        narrowed.MethodCounts.Should().Equal(3, 1, 1);
    }

    [Fact]
    public async Task The_method_list_is_empty_for_a_release_in_another_organisation()
    {
        var releaseId = await ImportAsync();
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;

        await using var read = _db.NewContext();
        var methods = await NewReferences(read).ListSystemReferenceMethodsAsync(releaseId,
            new FindSystemReferencesQuery(Guid.Parse(AppId), "table", 50104, "CRONUS Entry"));

        methods.Should().BeEmpty();
    }

    [Fact]
    public async Task A_list_under_the_cap_leaves_the_picker_to_the_rows_in_hand()
    {
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var entryId = await ObjectIdAsync(read, releaseId, "CRONUS Entry");

        var session = await NewSessions(read).CreateSystemReferencesFromObjectAsync(entryId, "owner");

        session!.Truncated.Should().BeFalse();
        session.Results.Should().HaveCount(5);
        session.MethodNames.Should().BeNull();
    }

    [Fact]
    public async Task The_method_list_counts_every_call_without_regard_to_case()
    {
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var methods = await NewReferences(read).ListSystemReferenceMethodsAsync(releaseId,
            new FindSystemReferencesQuery(Guid.Parse(AppId), "table", 50104, "CRONUS Entry",
                // Ignored: the picker lists every method, whatever is picked.
                SystemMethodNames: ["modify"]));

        methods.Select(m => (m.Name, m.Count))
            .Should().Equal(("Insert", 3), ("Delete", 1), ("Modify", 1));
    }

    [Fact]
    public async Task Someone_elses_list_cannot_be_narrowed()
    {
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var (fileId, lines) = await SetterFileAsync(read, releaseId);
        var line = Array.FindIndex(lines, l => l.Contains("ThingRec.Insert")) + 1;
        var column = lines[line - 1].IndexOf("ThingRec", StringComparison.Ordinal) + 2;
        var sessions = NewSessions(read);
        var session = await sessions.CreateSystemReferencesAtPositionAsync(fileId, line, column, "owner");

        (await sessions.NarrowSystemReferencesAsync(session!.Token, "someone else", "Get")).Should().BeNull();
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

    private static Task<long> ObjectIdAsync(Data.AppDbContext ctx, int releaseId, string name) =>
        ctx.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId && o.Kind == "table" && o.Name == name)
            .Select(o => o.Id)
            .SingleAsync();

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
                new
                {
                    Id = 50104,
                    Name = "CRONUS Entry",
                    ReferenceSourceFileName = "src/Entry.Table.al",
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
                new
                {
                    Id = 50105,
                    Name = "CRONUS Alpha",
                    ReferenceSourceFileName = "src/Alpha.Codeunit.al",
                    Methods = new object[] { new { Name = "Run", Parameters = Array.Empty<object>() } },
                },
                new
                {
                    Id = 50106,
                    Name = "CRONUS Zulu",
                    ReferenceSourceFileName = "src/Zulu.Codeunit.al",
                    Methods = new object[] { new { Name = "Run", Parameters = Array.Empty<object>() } },
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
            ["src/Entry.Table.al"] = """
                table 50104 "CRONUS Entry"
                {
                    fields
                    {
                        field(1; "No."; Code[20]) { }
                    }
                }
                """,
            // Alpha sorts first, so a cut list keeps its calls; Zulu's are the
            // ones past the cut. INSERT is the C/AL spelling of Insert.
            ["src/Alpha.Codeunit.al"] = """
                codeunit 50105 "CRONUS Alpha"
                {
                    procedure Run()
                    var
                        Entry: Record "CRONUS Entry";
                    begin
                        Entry.Insert();
                        Entry.Insert();
                        Entry.Modify();
                    end;
                }
                """,
            ["src/Zulu.Codeunit.al"] = """
                codeunit 50106 "CRONUS Zulu"
                {
                    procedure Run()
                    var
                        Entry: Record "CRONUS Entry";
                    begin
                        Entry.INSERT();
                        Entry.Delete();
                    end;
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

    private ReferenceSessionService NewSessions(Data.AppDbContext ctx,
        Microsoft.Extensions.Caching.Memory.MemoryCache? cache = null) =>
        new(cache ?? new Microsoft.Extensions.Caching.Memory.MemoryCache(
                Microsoft.Extensions.Options.Options.Create(
                    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            NewReferences(ctx), ctx, new ReferenceResolver(ctx), new ProjectAccess(ctx, _db.OrgContext),
            NullLogger<ReferenceSessionService>.Instance);
}
