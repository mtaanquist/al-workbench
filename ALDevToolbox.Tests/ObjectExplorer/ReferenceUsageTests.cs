using System.Text.Json;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// "Where is this field set?": each field and variable reference says whether
/// it reads or sets the value, and whether the record it goes through is
/// temporary. The unit half pins the line classifier; the integration half runs
/// a synthetic <c>.app</c> through the real importer, so the columns it reads
/// are the ones production stamps, and then asks through the query service and
/// the MCP tool.
/// </summary>
public sealed class ReferenceUsageTests : IDisposable
{
    private const string AppId = "eeeeeeee-0000-0000-0000-000000000005";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    // ── The line classifier ───────────────────────────────────────────

    // A '|' marks where the reference's name starts; the classifier is handed
    // that 1-based column, as the importer stamps it, and the bare name.
    [Theory]
    [InlineData("Thing.|\"No.\" := 'A';", "No.", "assign", "Thing")]
    [InlineData("Thing.|\"No.\" += 1;", "No.", "assign", "Thing")]
    [InlineData("Thing.|Amount[2] := 1;", "Amount", "assign", "Thing")]
    [InlineData("Thing.Validate(|\"No.\", 'B');", "No.", "validate", "Thing")]
    [InlineData("Validate(|\"No.\");", "No.", "validate", null)]
    [InlineData("Thing.ModifyAll(|\"No.\", 'C');", "No.", "modify_all", "Thing")]
    [InlineData("Evaluate(Thing.|\"No.\", Txt);", "No.", "evaluate", "Thing")]
    [InlineData("Clear(Thing.|\"No.\");", "No.", "clear", "Thing")]
    [InlineData("THING.VALIDATE(|\"No.\",'B');", "No.", "validate", "THING")]
    [InlineData("if Thing.|\"No.\" = '' then", "No.", "read", "Thing")]
    [InlineData("Other.\"No.\" := Thing.|\"No.\";", "No.", "read", "Thing")]
    [InlineData("Thing.Validate(\"No.\", Other.|\"No.\");", "No.", "read", "Other")]
    [InlineData("Thing.SetRange(|\"No.\", 'F');", "No.", "read", "Thing")]
    [InlineData("TempThing.SetRange(|Amount, 0, 10);", "Amount", "read", "TempThing")]
    [InlineData("Evaluate(Dummy, Thing.|\"No.\");", "No.", "read", "Thing")]
    [InlineData("field(\"No.\"; Rec.|\"No.\")", "No.", "read", "Rec")]
    [InlineData("|Counter := Counter + 1;", "Counter", "assign", null)]
    [InlineData("Counter2 := |Counter + 1;", "Counter", "read", null)]
    [InlineData("|Buffer.Init();", "Buffer", "record_write", null)]
    [InlineData("|Buffer.TransferFields(Thing);", "Buffer", "record_write", null)]
    [InlineData("Thing.TransferFields(|Buffer);", "Buffer", "read", null)]
    [InlineData("|Buffer.INIT;", "Buffer", "record_write", null)]
    [InlineData("TempThing.Validate(\"No.\", |\"No.\");", "No.", "read", null)]
    [InlineData("TempThing.CalcFields(Amount, |\"No.\");", "No.", "read", "TempThing")]
    public void Classifies_the_reference_on_its_line(string marked, string name, string usage, string? receiver)
    {
        var column = marked.IndexOf('|') + 1;
        var line = marked.Replace("|", "");

        var site = ReferenceUsage.Classify(line, column, name);

        site.Should().NotBeNull();
        site!.Usage.Should().Be(usage);
        site.Receiver.Should().Be(receiver);
    }

    [Theory]
    [InlineData(new[] { "Thing.|\"No.\" :=", "    'A';" }, "assign", "Thing")]
    [InlineData(new[] { "Thing.Validate(", "    |\"No.\",", "    'B');" }, "validate", "Thing")]
    [InlineData(new[] { "TempThing.ModifyAll(", "    |\"No.\", 'C');" }, "modify_all", "TempThing")]
    [InlineData(new[] { "Foo(Bar);", "|\"No.\" := 1;" }, "assign", null)]
    [InlineData(new[] { "Thing.Validate(\"No.\", 1);", "if Other.|\"No.\" = 1 then" }, "read", "Other")]
    [InlineData(new[] { "Thing.|\"No.\" // :=", "    = 1" }, "read", "Thing")]
    public void Reads_across_a_statement_split_over_lines(string[] marked, string usage, string? receiver)
    {
        var target = Array.FindIndex(marked, l => l.Contains('|'));
        var column = marked[target].IndexOf('|') + 1;
        var lines = marked.Select(l => l.Replace("|", "")).ToArray();

        var site = ReferenceUsage.Classify(lines, target, column, "No.");

        site.Should().NotBeNull();
        site!.Usage.Should().Be(usage);
        site.Receiver.Should().Be(receiver);
    }

    [Fact]
    public void Without_a_column_the_first_whole_word_occurrence_is_used()
    {
        // Rows imported before columns were stamped carry none.
        var site = ReferenceUsage.Classify("    ThingNo := 1; Thing.\"No.\" := 'A';", null, "No.");
        site!.Usage.Should().Be(ReferenceUsage.Assign);
    }

    [Fact]
    public void A_name_that_is_not_on_the_line_is_unknown_rather_than_a_read()
    {
        ReferenceUsage.Classify("    begin", null, "No.").Should().BeNull();
    }

    [Fact]
    public void Temporary_declarations_are_found_in_AL_and_CAL()
    {
        const string source = """
            var
                TempA, "Temp B": Record "Sales Line" temporary;
                Normal: Record "Sales Line";
            procedure Fill(var TempC: Record Customer temporary;
                Other: Integer,
                TempE: Record Microsoft.Sales.Document."Sales Line" temporary)
            VAR
              TempD@1000 : TEMPORARY Record 37;
            """;
        var captured = ReferenceUsage.TemporaryDeclarationRegex.Matches(source)
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);

        ReferenceUsage.ParseTemporaryNames(captured)
            .Should().BeEquivalentTo(["TempA", "Temp B", "TempC", "TempD", "TempE"]);
    }

    [Theory]
    [InlineData("SourceTableTemporary = true;", true)]
    [InlineData("SourceTableTemporary=Yes;", true)]
    [InlineData("TableType = Temporary;", true)]
    [InlineData("SourceTableTemporary = false;", false)]
    public void A_temporary_rec_is_recognised(string property, bool expected)
    {
        ReferenceUsage.TemporaryRecRegex.IsMatch(property).Should().Be(expected);
    }

    [Fact]
    public void Rec_and_a_bare_name_take_the_objects_own_temporary_flag()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TempThing" };
        ReferenceUsage.IsTemporary(null, recIsTemporary: true, names).Should().BeTrue();
        ReferenceUsage.IsTemporary("xRec", recIsTemporary: false, names).Should().BeFalse();
        ReferenceUsage.IsTemporary("tempthing", recIsTemporary: false, names).Should().BeTrue();
        ReferenceUsage.IsTemporary("Thing", recIsTemporary: true, names).Should().BeFalse();
    }

    // ── Through the importer and the query ────────────────────────────

    [Fact]
    public async Task Field_references_say_whether_they_read_or_set_and_whether_the_record_is_temporary()
    {
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var rows = await NewReferences(read).FindReferencesAsync(releaseId, NoField);

        var byLine = rows.Where(r => r.ReferenceKind == "field_access")
            .ToDictionary(r => r.Snippet!.Trim(), r => (r.Usage, r.IsTemporary));
        byLine["Thing.\"No.\" := 'A';"].Should().Be(("assign", false));
        byLine["Thing.Validate(\"No.\", 'B');"].Should().Be(("validate", false));
        byLine["Thing.ModifyAll(\"No.\", 'C');"].Should().Be(("modify_all", false));
        byLine["Evaluate(Thing.\"No.\", 'D');"].Should().Be(("evaluate", false));
        byLine["if Thing.\"No.\" = '' then;"].Should().Be(("read", false));
        byLine["TempThing.\"No.\" := 'E';"].Should().Be(("assign", true));
        byLine["Thing.SetRange(\"No.\", 'F');"].Should().Be(("read", false));
        byLine["\"No.\","].Should().Be(("validate", false), "the Validate( is on the line above");
    }

    [Fact]
    public async Task The_MCP_tool_returns_only_the_writes_outside_temporary_records_when_asked()
    {
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var result = await NewTools(read).FindReferencesAsync(
            releaseId.ToString(), "CRONUS Thing", targetField: "No.", usage: "set", includeTemporary: false);

        result.Rows.Select(r => r.Snippet!.Trim()).Should().BeEquivalentTo(
        [
            "Thing.\"No.\" := 'A';",
            "Thing.Validate(\"No.\", 'B');",
            "Thing.ModifyAll(\"No.\", 'C');",
            "Evaluate(Thing.\"No.\", 'D');",
            "\"No.\",",
        ]);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task The_MCP_tool_returns_only_the_reads_when_asked()
    {
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var result = await NewTools(read).FindReferencesAsync(
            releaseId.ToString(), "CRONUS Thing", targetField: "No.", usage: "read");

        result.Rows.Should().NotBeEmpty();
        result.Rows.Should().OnlyContain(r => r.Usage == ReferenceUsage.Read);
    }

    [Theory]
    [InlineData("written", true)]
    [InlineData("set", false)]
    public async Task The_MCP_usage_filter_is_refused_when_it_cannot_apply(string usage, bool withField)
    {
        var releaseId = await ImportAsync();

        await using var read = _db.NewContext();
        var act = () => NewTools(read).FindReferencesAsync(
            releaseId.ToString(), "CRONUS Thing", targetField: withField ? "No." : null, usage: usage);

        await act.Should().ThrowAsync<McpException>();
    }

    private static readonly FindReferencesQuery NoField = new(
        Guid.Parse(AppId), "table", 50100, "CRONUS Thing", TargetMemberName: "No.", TargetMemberKind: "field");

    private async Task<int> ImportAsync()
    {
        await using var ctx = _db.NewContext();
        using var stream = new MemoryStream(App());
        var summary = await NewImporter(ctx).ImportReleaseAsync(new ReleaseImportRequest(
            "Usage fixture", "third_party", null, null, [new AppFileUpload("app.app", stream, null)]));
        return summary.ReleaseId;
    }

    /// <summary>A table with one field, and a codeunit that sets and reads it every way the classifier knows.</summary>
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
            },
            Codeunits = new object[]
            {
                new
                {
                    Id = 50101,
                    Name = "CRONUS Setter",
                    ReferenceSourceFileName = "src/Setter.Codeunit.al",
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
            ["src/Setter.Codeunit.al"] = """
                codeunit 50101 "CRONUS Setter"
                {
                    var
                        TempThing: Record "CRONUS Thing" temporary;

                    procedure Run()
                    var
                        Thing: Record "CRONUS Thing";
                    begin
                        Thing."No." := 'A';
                        Thing.Validate("No.", 'B');
                        Thing.ModifyAll("No.", 'C');
                        Evaluate(Thing."No.", 'D');
                        if Thing."No." = '' then;
                        TempThing."No." := 'E';
                        Thing.SetRange("No.", 'F');
                        Thing.Validate(
                            "No.",
                            'G');
                    end;
                }
                """,
        };
        return SyntheticApp.Build(AppId, "CRONUS Usage", "CRONUS", "1.0.0.0",
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

    private ObjectExplorerTools NewTools(Data.AppDbContext ctx)
    {
        var access = new ProjectAccess(ctx, _db.OrgContext);
        var references = NewReferences(ctx);
        return new ObjectExplorerTools(
            new ObjectExplorerService(ctx, references, access, NullLogger<ObjectExplorerService>.Instance),
            new ObjectSearchService(ctx, access),
            references,
            new TranslationQueryService(ctx),
            new ReleaseComparisonService(ctx, access, NullLogger<ReleaseComparisonService>.Instance));
    }
}
