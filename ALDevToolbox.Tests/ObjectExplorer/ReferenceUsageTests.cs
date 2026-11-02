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
/// temporary. A synthetic <c>.app</c> runs through the real importer, so the
/// columns it reads are the ones production stamps, and then the tests ask
/// through the query service and the MCP tool. The line classifier itself is
/// pinned in <see cref="ReferenceUsageClassifierTests"/>, which needs no
/// database.
/// </summary>
public sealed class ReferenceUsageTests : IDisposable
{
    private const string AppId = "eeeeeeee-0000-0000-0000-000000000005";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

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
