using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The line classifier behind "where is this field set?", and the other pure
/// rules around it. Kept apart from <see cref="ReferenceUsageTests"/> because
/// these touch no database: in a class holding a <c>TestDb</c>, xUnit clones
/// and drops a database for every row (OEX-7).
/// </summary>
public sealed class ReferenceUsageClassifierTests
{
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

    // ── The MCP usage filter's "was anything cut?" ────────────────────

    /// <summary>
    /// OEX-3: only the reference bucket is capped (and fetched one row past
    /// the cap). The declarations and interface implementations a member
    /// search adds are not, so they must not make a complete answer read as cut.
    /// </summary>
    [Theory]
    [InlineData(ReferenceQueryService.MaxReferenceMatches, 0, true)]
    [InlineData(ReferenceQueryService.MaxReferenceMatches - 1, 2, true)]
    [InlineData(ReferenceQueryService.MaxReferenceMatches + 1, 0, false)]
    [InlineData(ReferenceQueryService.MaxReferenceMatches + 1, 3, false)]
    public void The_usage_filter_is_cut_only_when_the_references_themselves_were(int references, int extras, bool checkedAll)
    {
        var rows = Enumerable.Range(0, references).Select(i => Row(i, i % 2 == 0 ? "call" : "object"))
            .Concat(Enumerable.Range(0, extras).Select(i => Row(-1 - i, i % 2 == 0 ? "declaration" : "implementation")));

        ObjectExplorerTools.UsageFilterCheckedAll(rows).Should().Be(checkedAll);
    }

    private static ReferenceMatch Row(long id, string category) =>
        new(id, 1, "Base Application", 1, "codeunit", "Sales-Post", "field_access", 10, 1, Category: category);
}
