using System.Text.RegularExpressions;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.Translation;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Translator;

/// <summary>
/// The byte-fidelity contract for the Translator's export
/// (<see cref="XliffTargetWriter"/>): a no-edit run is byte-identical, and an
/// edit touches only the trans-unit it belongs to so git diffs stay clean.
/// Exercised against the real Microsoft OIOUBL XLIFF plus small hand-crafted
/// shapes (missing target, state changes, escaping).
/// </summary>
public sealed class XliffTargetWriterTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer", "OIOUBL.daDK.xlf");

    private static readonly Regex TransUnitRegex =
        new(@"<trans-unit\b[^>]*>.*?</trans-unit>", RegexOptions.Singleline);

    [Fact]
    public void No_edits_is_byte_identical()
    {
        var original = File.ReadAllText(FixturePath);

        var result = XliffTargetWriter.ApplyEdits(original, new Dictionary<string, TargetEdit>());

        result.Should().Be(original, because: "exporting without edits must reproduce the input exactly");
    }

    [Fact]
    public void Single_edit_changes_only_its_own_trans_unit()
    {
        var original = File.ReadAllText(FixturePath);

        // Pick a real trans-unit id from the fixture.
        using var fs = File.OpenRead(FixturePath);
        var parsed = AlXliffParser.Parse(fs);
        var target = parsed.Units.First(u => !string.IsNullOrEmpty(u.SourceText));

        var edits = new Dictionary<string, TargetEdit>
        {
            [target.Id] = new TargetEdit("ÆØÅ helt ny oversættelse", "translated"),
        };

        var result = XliffTargetWriter.ApplyEdits(original, edits);

        var origBlocks = TransUnitRegex.Matches(original).Select(m => m.Value).ToList();
        var resBlocks = TransUnitRegex.Matches(result).Select(m => m.Value).ToList();
        resBlocks.Should().HaveCount(origBlocks.Count, because: "no trans-units are added or removed");

        var changed = Enumerable.Range(0, origBlocks.Count).Count(i => origBlocks[i] != resBlocks[i]);
        changed.Should().Be(1, because: "exactly one trans-unit was edited");

        // The bytes outside trans-units (declaration, file header, whitespace)
        // are untouched.
        Between(original, TransUnitRegex).Should().Be(Between(result, TransUnitRegex));

        // Re-parsing confirms the new target landed and nothing else moved.
        using var rs = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(result));
        var reparsed = AlXliffParser.Parse(rs);
        reparsed.Units.Single(u => u.Id == target.Id).TargetText.Should().Be("ÆØÅ helt ny oversættelse");
    }

    [Fact]
    public void Inserts_a_target_when_the_unit_has_none()
    {
        const string xml =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
              <file source-language="en-US" target-language="da-DK" original="Demo">
                <body>
                  <group>
                    <trans-unit id="Table 1 - Field 1 - Property Caption" size-unit="char">
                      <source>Customer No.</source>
                    </trans-unit>
                  </group>
                </body>
              </file>
            </xliff>
            """;

        var result = XliffTargetWriter.ApplyEdits(xml, new Dictionary<string, TargetEdit>
        {
            ["Table 1 - Field 1 - Property Caption"] = new TargetEdit("Debitornr.", "translated"),
        });

        result.Should().Contain("<source>Customer No.</source>");
        result.Should().Contain("<target state=\"translated\">Debitornr.</target>");
    }

    [Fact]
    public void Replaces_existing_target_and_state()
    {
        const string xml =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
              <file source-language="en-US" target-language="da-DK" original="Demo"><body>
                <trans-unit id="t1"><source>Posting Date</source><target state="needs-translation">x</target></trans-unit>
              </body></file>
            </xliff>
            """;

        var result = XliffTargetWriter.ApplyEdits(xml, new Dictionary<string, TargetEdit>
        {
            ["t1"] = new TargetEdit("Bogføringsdato", "translated"),
        });

        result.Should().Contain("<target state=\"translated\">Bogføringsdato</target>");
        result.Should().NotContain("needs-translation");
    }

    [Fact]
    public void Escapes_xml_special_characters_in_the_target()
    {
        const string xml =
            """
            <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2"><file source-language="en-US" target-language="da-DK" original="Demo"><body>
            <trans-unit id="t1"><source>A &amp; B</source><target>old</target></trans-unit>
            </body></file></xliff>
            """;

        var result = XliffTargetWriter.ApplyEdits(xml, new Dictionary<string, TargetEdit>
        {
            ["t1"] = new TargetEdit("A & B < C > D", null),
        });

        result.Should().Contain("<target>A &amp; B &lt; C &gt; D</target>");
    }

    [Fact]
    public void Escapes_a_hostile_state_attribute_value()
    {
        // The state value is fully client-controlled. A quote-bearing value must
        // not break out of the attribute and inject markup into the export — which
        // is later re-parsed into the translation memory. See issue #373.
        const string xml =
            """
            <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2"><file source-language="en-US" target-language="da-DK" original="Demo"><body>
            <trans-unit id="t1"><source>Posting Date</source><target state="needs-translation">x</target></trans-unit>
            </body></file></xliff>
            """;

        var result = XliffTargetWriter.ApplyEdits(xml, new Dictionary<string, TargetEdit>
        {
            ["t1"] = new TargetEdit("Bogføringsdato", "translated\" foo=\"bar"),
        });

        // The injected attribute boundary is escaped, so no second attribute appears.
        result.Should().NotContain("foo=\"bar\"");
        result.Should().Contain("state=\"translated&quot; foo=&quot;bar\"");

        // And the export still parses as well-formed XML.
        using var rs = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(result));
        var reparsed = AlXliffParser.Parse(rs);
        reparsed.Units.Single(u => u.Id == "t1").TargetText.Should().Be("Bogføringsdato");
    }

    [Fact]
    public void Inserted_target_escapes_a_hostile_state_attribute_value()
    {
        const string xml =
            """
            <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2"><file source-language="en-US" target-language="da-DK" original="Demo"><body>
            <trans-unit id="t1"><source>Posting Date</source></trans-unit>
            </body></file></xliff>
            """;

        var result = XliffTargetWriter.ApplyEdits(xml, new Dictionary<string, TargetEdit>
        {
            ["t1"] = new TargetEdit("Bogføringsdato", "translated\"/><evil>"),
        });

        result.Should().NotContain("<evil>");
        using var rs = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(result));
        var reparsed = AlXliffParser.Parse(rs);
        reparsed.Units.Single(u => u.Id == "t1").TargetText.Should().Be("Bogføringsdato");
    }

    // ── Restating the target language (issue #625) ───────────────────────
    // A translation started from the compiler's generated .g.xlf inherits that
    // file's target-language, which is its source language. The new language
    // file has to say which language it is actually in.

    [Fact]
    public void Setting_the_target_language_rewrites_the_attribute_and_nothing_else()
    {
        const string original = """
            <?xml version="1.0" encoding="utf-8"?>
            <xliff version="1.2">
              <file datatype="xml" source-language="en-US" target-language="en-US" original="App">
                <body />
              </file>
            </xliff>
            """;

        var result = XliffTargetWriter.SetTargetLanguage(original, "da-DK");

        result.Should().Contain("target-language=\"da-DK\"");
        result.Should().Contain("source-language=\"en-US\"", "only the target language is being restated");
        result.Should().Be(original.Replace("target-language=\"en-US\"", "target-language=\"da-DK\""));
    }

    [Fact]
    public void A_file_element_with_no_target_language_gets_one()
    {
        const string original = """<xliff><file source-language="en-US"><body /></file></xliff>""";

        XliffTargetWriter.SetTargetLanguage(original, "nb-NO")
            .Should().Contain("<file source-language=\"en-US\" target-language=\"nb-NO\">");
    }

    [Fact]
    public void Only_the_first_file_elements_language_is_restated()
    {
        const string original = """
            <xliff><file target-language="en-US"><body /></file><file target-language="en-US"><body /></file></xliff>
            """;

        var result = XliffTargetWriter.SetTargetLanguage(original, "da-DK");

        result.Should().Contain("<file target-language=\"da-DK\">");
        result.Should().Contain("<file target-language=\"en-US\">",
            "the parser only ever reads the first <file>, so the rest is left alone");
    }

    [Fact]
    public void Something_that_is_not_an_xliff_comes_back_unchanged()
    {
        const string original = "not xliff at all";
        XliffTargetWriter.SetTargetLanguage(original, "da-DK").Should().Be(original);
    }

    [Theory]
    [InlineData("""<xliff><file source-language="en-US" target-language="en-US"><body /></file></xliff>""", "da-DK", "da-DK")]
    [InlineData("""<xliff><file source-language="en-US"><body /></file></xliff>""", "da-DK", "da-DK")]
    [InlineData("""<xliff><file source-language="en-US" target-language="da-DK"><body /></file></xliff>""", "sv-SE", "da-DK")]
    [InlineData("""<xliff><file source-language="en-US" target-language="en-US"><body /></file></xliff>""", "en-US", "en-US")]
    [InlineData("""<xliff><file source-language="en-US" target-language="en-US"><body /></file></xliff>""", "", "en-US")]
    public void Only_a_file_still_marked_untranslated_has_its_language_named(string original, string language, string expected)
    {
        var result = XliffTargetWriter.NameTargetLanguageOfUntranslatedFile(original, language);

        result.Should().Contain($"target-language=\"{expected}\"");
        if (expected != language) result.Should().Be(original, "anything else is left byte for byte");
    }

    /// <summary>Concatenates everything that sits *outside* the matched blocks.</summary>
    private static string Between(string s, Regex blocks)
    {
        var sb = new System.Text.StringBuilder();
        var last = 0;
        foreach (Match m in blocks.Matches(s))
        {
            sb.Append(s, last, m.Index - last);
            last = m.Index + m.Length;
        }
        sb.Append(s, last, s.Length - last);
        return sb.ToString();
    }
}
