using ALDevToolbox.Services;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Piper;

/// <summary>
/// Covers <see cref="PiperTransform.Run"/>: the pure transform that backs the
/// <c>/piper</c> page. Each test pins one rule so a regression in delimiter
/// detection, format output, sort behaviour, or escape handling shows up as
/// a focused failure rather than a hard-to-read end-to-end diff.
/// </summary>
public sealed class PiperTransformTests
{
    [Fact]
    public void Empty_input_returns_empty_output_and_awaiting_meta()
    {
        var result = PiperTransform.Run("", new PiperOptions());
        result.Output.Should().BeEmpty();
        result.ItemCount.Should().Be(0);
        result.DelimiterDescription.Should().Be("Awaiting input...");
        result.DetectedSeparatorDisplay.Should().BeNull();
    }

    [Fact]
    public void Oversized_input_is_rejected_without_processing()
    {
        // Run fires per-keystroke on a server circuit; an unbounded paste is a
        // cheap CPU/memory spike. Inputs over the cap return a clear message
        // instead of running the multi-pass pipeline.
        var huge = new string('a', PiperTransform.MaxInputLength + 1);

        var result = PiperTransform.Run(huge, new PiperOptions());

        result.Output.Should().BeEmpty();
        result.ItemCount.Should().Be(0);
        result.DelimiterDescription.Should().Contain("too large");
    }

    // ---------- Format presets ----------

    [Fact]
    public void BcOr_default_format_joins_with_pipe()
    {
        var result = PiperTransform.Run("a,b,c", new PiperOptions());
        result.Output.Should().Be("a|b|c");
        result.ItemCount.Should().Be(3);
    }

    [Fact]
    public void BcAnd_prefixes_each_item_with_lt_gt_and_joins_with_ampersand()
    {
        var result = PiperTransform.Run("1001,1002,1003",
            new PiperOptions { Format = PiperOutputFormat.BcAnd });
        result.Output.Should().Be("<>1001&<>1002&<>1003");
    }

    /// <summary>
    /// #1268 (L46): a value with a filter operator in it is quoted, so Business
    /// Central reads it as one value rather than as "Smith" and "Sons", or a
    /// group. Embedded quotes are doubled; plain values stay unquoted.
    /// </summary>
    [Theory]
    [InlineData(PiperOutputFormat.BcOr, "'Smith & Sons'|'Fabrikam (EU)'|'O''Brien'|10000")]
    [InlineData(PiperOutputFormat.BcAnd, "<>'Smith & Sons'&<>'Fabrikam (EU)'&<>'O''Brien'&<>10000")]
    public void Bc_filters_quote_values_that_contain_operator_characters(PiperOutputFormat format, string expected)
    {
        var result = PiperTransform.RunOnItems(
            ["Smith & Sons", "Fabrikam (EU)", "O'Brien", "10000"],
            new PiperOptions { Format = format },
            "test");
        result.Output.Should().Be(expected);
        result.ItemCount.Should().Be(4);
    }

    [Fact]
    public void Sql_format_wraps_each_item_in_single_quotes_joined_with_comma()
    {
        var result = PiperTransform.Run("a,b,c",
            new PiperOptions { Format = PiperOutputFormat.Sql });
        result.Output.Should().Be("'a','b','c'");
    }

    [Fact]
    public void Sql_format_doubles_embedded_apostrophes()
    {
        // The original piper_app.js used backslash-escape, which produces
        // invalid SQL. The C# port must double the apostrophe instead.
        var result = PiperTransform.Run("O'Brien,Smith",
            new PiperOptions { Format = PiperOutputFormat.Sql });
        result.Output.Should().Be("'O''Brien','Smith'");
    }

    [Fact]
    public void Custom_format_uses_user_supplied_prefix_suffix_join()
    {
        var result = PiperTransform.Run("a,b,c", new PiperOptions
        {
            Format = PiperOutputFormat.Custom,
            CustomPrefix = "[",
            CustomSuffix = "]",
            CustomJoin = " → ",
        });
        result.Output.Should().Be("[a] → [b] → [c]");
    }

    [Fact]
    public void Result_prefix_and_suffix_wrap_the_whole_output_after_format()
    {
        // SQL list inside an IN-clause envelope — the single biggest reason
        // ResultPrefix / ResultSuffix exist.
        var result = PiperTransform.Run("1001,1002,1003", new PiperOptions
        {
            Format = PiperOutputFormat.Sql,
            ResultPrefix = "IN (",
            ResultSuffix = ")",
        });
        result.Output.Should().Be("IN ('1001','1002','1003')");
    }

    // ---------- Auto-detect ----------

    [Theory]
    [InlineData("a,b,c", ",")]
    [InlineData("a;b;c", ";")]
    [InlineData("a\tb\tc", "\t")]
    [InlineData("a|b|c", "|")]
    [InlineData("a\nb\nc", "\n")]
    public void Autodetect_picks_each_candidate_when_it_is_the_only_one_present(string input, string expectedDelim)
    {
        var result = PiperTransform.Run(input, new PiperOptions());
        result.ItemCount.Should().Be(3);
        result.DelimiterDescription.Should().Contain(expectedDelim == "\t" ? "\\t"
            : expectedDelim == "\n" ? "\\n"
            : expectedDelim);
    }

    [Fact]
    public void Autodetect_picks_the_most_prolific_when_multiple_candidates_are_present()
    {
        // Three commas, two semicolons — comma wins.
        var result = PiperTransform.Run("a,b,c,d;e;f", new PiperOptions());
        result.ItemCount.Should().Be(4);
        // Splits on "," → ["a","b","c","d;e;f"], so the semicolons stay inside
        // the last item rather than splitting again.
        result.Output.Should().EndWith("|d;e;f");
    }

    [Fact]
    public void Autodetect_treats_crlf_as_one_separator_not_two_empty_halves()
    {
        // If \n were checked before \r\n, "a\r\nb" would split into
        // ["a\r", "", "b"] and produce a spurious empty item.
        var result = PiperTransform.Run("a\r\nb\r\nc", new PiperOptions());
        result.ItemCount.Should().Be(3);
        result.Output.Should().Be("a|b|c");
    }

    [Fact]
    public void Pipe_is_in_the_autodetect_candidate_set_so_BcOr_output_roundtrips()
    {
        // The "Use result as input" button puts the BC OR output back into
        // the input box. The transform must split on "|" without the user
        // having to type it in the separator override field.
        var result = PiperTransform.Run("a|b|c", new PiperOptions());
        result.Output.Should().Be("a|b|c");
        result.ItemCount.Should().Be(3);
    }

    [Fact]
    public void SplitOnNewlinesOnly_restricts_candidates_so_inline_commas_are_kept()
    {
        var result = PiperTransform.Run("a, b\nc, d", new PiperOptions
        {
            SplitOnNewlinesOnly = true,
        });
        result.ItemCount.Should().Be(2);
        result.Output.Should().Be("a, b|c, d");
    }

    [Fact]
    public void No_delimiter_found_yields_a_single_item_and_clears_detected_separator()
    {
        var result = PiperTransform.Run("loneword", new PiperOptions());
        result.ItemCount.Should().Be(1);
        result.Output.Should().Be("loneword");
        result.DelimiterDescription.Should().Be("no delimiter found");
        result.DetectedSeparatorDisplay.Should().BeNull();
    }

    [Fact]
    public void Detected_separator_display_is_the_escape_form_for_whitespace()
    {
        var result = PiperTransform.Run("a\tb\tc", new PiperOptions());
        result.DetectedSeparatorDisplay.Should().Be("\\t");
    }

    // ---------- Separator override ----------

    [Fact]
    public void Override_preempts_autodetect_and_is_used_literally()
    {
        // Input would auto-detect to "," (most prolific), but the override
        // forces "::" instead.
        var result = PiperTransform.Run("a::b::c,d",
            new PiperOptions { SeparatorOverride = "::" });
        result.ItemCount.Should().Be(3);
        result.Output.Should().Be("a|b|c,d");
        result.DelimiterDescription.Should().Be("custom: \"::\"");
        // Override path produces no auto-detect hint.
        result.DetectedSeparatorDisplay.Should().BeNull();
    }

    [Theory]
    [InlineData("\\t", "\t")]
    [InlineData("\\n", "\n")]
    [InlineData("\\r", "\r")]
    [InlineData("\\\\", "\\")]
    public void Override_unescapes_backslash_t_n_r_and_double_backslash(string typed, string actual)
    {
        // The override input is a plain <input type=text>, so the user can't
        // paste raw whitespace conveniently. The escapes let them type "\t".
        var input = $"a{actual}b{actual}c";
        var result = PiperTransform.Run(input,
            new PiperOptions { SeparatorOverride = typed });
        result.ItemCount.Should().Be(3);
        result.Output.Should().Be("a|b|c");
    }

    [Fact]
    public void Override_leaves_unknown_backslash_sequences_alone()
    {
        // "\z" isn't a recognised escape — pass it through as the literal
        // two characters so the user isn't silently surprised.
        var result = PiperTransform.Run("a\\zb\\zc",
            new PiperOptions { SeparatorOverride = "\\z" });
        result.ItemCount.Should().Be(3);
        result.Output.Should().Be("a|b|c");
    }

    // ---------- Modifiers ----------

    [Fact]
    public void TrimItems_strips_surrounding_whitespace_per_item()
    {
        var result = PiperTransform.Run("  a , b ,  c  ",
            new PiperOptions { TrimItems = true });
        result.Output.Should().Be("a|b|c");
    }

    [Fact]
    public void SkipEmpty_defaults_to_true_so_adjacent_delimiters_collapse()
    {
        var result = PiperTransform.Run("a,,b", new PiperOptions());
        result.ItemCount.Should().Be(2);
        result.Output.Should().Be("a|b");
    }

    [Fact]
    public void SkipEmpty_false_keeps_empty_items_for_callers_that_want_them()
    {
        // Also turn off RemoveDuplicates so empties past the first don't
        // collapse via dedup — this test is specifically about empties.
        var result = PiperTransform.Run("a,,,b", new PiperOptions
        {
            SkipEmpty = false,
            RemoveDuplicates = false,
        });
        result.ItemCount.Should().Be(4);
        result.Output.Should().Be("a|||b");
    }

    [Fact]
    public void RemoveDuplicates_defaults_to_true_and_preserves_first_occurrence_order()
    {
        var result = PiperTransform.Run("c,a,b,a,c,d", new PiperOptions());
        result.Output.Should().Be("c|a|b|d");
    }

    [Fact]
    public void RemoveDuplicates_false_keeps_repeats()
    {
        var result = PiperTransform.Run("a,b,a",
            new PiperOptions { RemoveDuplicates = false });
        result.Output.Should().Be("a|b|a");
    }

    // ---------- Sort ----------

    [Fact]
    public void Sort_ascending_uses_numeric_order_when_all_items_parse_as_decimal()
    {
        // Lexical sort would put "999" after "1001" — wrong for AL ID lists.
        // The transform must detect the all-numeric case and sort numerically.
        var result = PiperTransform.Run("1001,999,1002,50",
            new PiperOptions { Sort = PiperSortOrder.Ascending });
        result.Output.Should().Be("50|999|1001|1002");
    }

    [Fact]
    public void Sort_descending_uses_numeric_order_when_all_items_parse_as_decimal()
    {
        var result = PiperTransform.Run("1001,999,1002,50",
            new PiperOptions { Sort = PiperSortOrder.Descending });
        result.Output.Should().Be("1002|1001|999|50");
    }

    [Fact]
    public void Sort_falls_back_to_lexical_when_any_item_is_not_numeric()
    {
        // One non-numeric item — every item gets compared lexically.
        var result = PiperTransform.Run("banana,apple,Cherry",
            new PiperOptions { Sort = PiperSortOrder.Ascending });
        result.Output.Should().Be("apple|banana|Cherry");
    }

    [Fact]
    public void Sort_none_keeps_input_order()
    {
        var result = PiperTransform.Run("c,a,b",
            new PiperOptions { Sort = PiperSortOrder.None });
        result.Output.Should().Be("c|a|b");
    }

    // ---------- Pipeline ordering ----------

    [Fact]
    public void Modifiers_apply_in_order_trim_then_skip_empty_then_dedup_then_sort()
    {
        // " 2 ,, 1, 2 , 3 " trimmed → ["2","","1","2","3"]
        //                  skip empty → ["2","1","2","3"]
        //                       dedup → ["2","1","3"]
        //                  sort asc → ["1","2","3"]
        // BC OR format → "1|2|3"
        var result = PiperTransform.Run(" 2 ,, 1, 2 , 3 ", new PiperOptions
        {
            TrimItems = true,
            SkipEmpty = true,
            RemoveDuplicates = true,
            Sort = PiperSortOrder.Ascending,
        });
        result.Output.Should().Be("1|2|3");
    }

    // ---------- ParseTable (Table input mode) ----------

    private const string SampleBcPaste =
        "Nummer\tNavn\tAnsvarscenter\tLokationskode\tTelefon\tGLN\tKontakt\tTillad flere bogføringsgrupper\tSaldo (RV)\tForfaldne beløb (RV)\tSalg (RV)\tBetalt (RV)\tOutputprofil\tForsegl PDF-dokumenter\n" +
        "10000\tKontorcentralen A/S\t\t\t\t\tRobert Townes\tNej\t0,00\t0,00\t1.606.446,00\t2.008.057,50\t\tNej\n" +
        "20000\tRavel Møbler\t\t\t\t\tHelen Ray\tNej\t20.081,25\t20.081,25\t538.217,00\t652.690,00\t\tNej\n" +
        "30000\tLauritzen Kontormøbler A/S\t\t\t\t\tAmalie Hansen\tNej\t349.380,00\t349.380,00\t1.509.225,00\t1.537.151,25\t\tNej\n" +
        "40000\tDeerfield Graphics Company\t\t\t\t\tIan Deberry\tNej\t22.413,00\t22.413,00\t549.806,00\t527.393,00\t\tNej\n" +
        "50000\tGuildford Water Department\t\t\t\t\tMathias Nilsson\tNej\t46.314,00\t46.314,00\t564.301,00\t517.987,00\t\tNej\n";

    [Fact]
    public void ParseTable_parses_the_sample_business_central_paste_headers_and_rows()
    {
        var table = PiperTransform.ParseTable(SampleBcPaste);
        table.Should().NotBeNull();
        table!.Headers.Should().HaveCount(14);
        table.Headers[0].Should().Be("Nummer");
        table.Headers[1].Should().Be("Navn");
        table.Headers[13].Should().Be("Forsegl PDF-dokumenter");
        table.Rows.Should().HaveCount(5);
        table.Rows[0][0].Should().Be("10000");
        table.Rows[0][1].Should().Be("Kontorcentralen A/S");
        // Ansvarscenter is empty in every sample row.
        table.Rows.Should().AllSatisfy(row => row[2].Should().BeEmpty());
    }

    [Fact]
    public void ParseTable_returns_null_when_input_has_only_a_header_row()
    {
        // No data rows means the dropdown would be useless — caller renders
        // an empty-state message instead.
        var table = PiperTransform.ParseTable("Name\tAge");
        table.Should().BeNull();
    }

    [Fact]
    public void ParseTable_returns_null_when_input_has_no_tabs_anywhere()
    {
        // Without a tab there's no column structure to project; bail out so
        // the page can prompt the user with the right hint.
        var table = PiperTransform.ParseTable("just a list\nwith two lines\nbut no tabs");
        table.Should().BeNull();
    }

    [Fact]
    public void ParseTable_returns_null_for_empty_input()
    {
        PiperTransform.ParseTable("").Should().BeNull();
        PiperTransform.ParseTable(null!).Should().BeNull();
    }

    [Fact]
    public void ParseTable_trims_a_single_trailing_blank_row()
    {
        // BC clipboards usually end with a trailing newline; that should
        // not become a phantom all-empty row in the parsed output.
        var input = "A\tB\n1\t2\n3\t4\n";
        var table = PiperTransform.ParseTable(input);
        table.Should().NotBeNull();
        table!.Rows.Should().HaveCount(2);
    }

    [Fact]
    public void ParseTable_preserves_interior_blank_rows()
    {
        // A blank row mid-list might be intentional in the source data —
        // the parser doesn't try to second-guess it.
        var input = "A\tB\n1\t2\n\n3\t4";
        var table = PiperTransform.ParseTable(input);
        table.Should().NotBeNull();
        table!.Rows.Should().HaveCount(3);
        table.Rows[1][0].Should().BeEmpty();
        table.Rows[1][1].Should().BeEmpty();
    }

    [Fact]
    public void ParseTable_pads_short_rows_to_header_width()
    {
        // BC sometimes elides trailing empty cells on a row.
        var input = "A\tB\tC\n1\t2";
        var table = PiperTransform.ParseTable(input);
        table.Should().NotBeNull();
        table!.Rows[0].Should().HaveCount(3);
        table.Rows[0][2].Should().BeEmpty();
    }

    [Fact]
    public void ParseTable_truncates_over_wide_rows_to_header_width()
    {
        // An over-wide row also shouldn't poison column indexing.
        var input = "A\tB\n1\t2\t3\t4";
        var table = PiperTransform.ParseTable(input);
        table.Should().NotBeNull();
        table!.Rows[0].Should().HaveCount(2);
        table.Rows[0][0].Should().Be("1");
        table.Rows[0][1].Should().Be("2");
    }

    [Fact]
    public void ParseTable_disambiguates_duplicate_header_names_with_numeric_suffixes()
    {
        // Lets a <select> built from headers use the displayed name as a
        // unique option value without losing either column.
        var input = "Name\tValue\tName\n1\t2\t3\n";
        var table = PiperTransform.ParseTable(input);
        table.Should().NotBeNull();
        table!.Headers.Should().Equal("Name", "Value", "Name (2)");
    }

    [Theory]
    [InlineData("A\tB\r\n1\t2\r\n3\t4\r\n")]
    [InlineData("A\tB\n1\t2\n3\t4\n")]
    [InlineData("A\tB\r1\t2\r3\t4\r")]
    public void ParseTable_recognises_crlf_lf_and_lone_cr_as_row_separators(string input)
    {
        var table = PiperTransform.ParseTable(input);
        table.Should().NotBeNull();
        table!.Headers.Should().Equal("A", "B");
        table.Rows.Should().HaveCount(2);
        table.Rows[1][0].Should().Be("3");
    }

    // ---------- ParseTable: configurable separator + headerless ----------

    [Fact]
    public void ParseTable_with_comma_separator_parses_csv_style_input()
    {
        var input = "name,city,age\nAlice,NYC,30\nBob,Paris,25\n";
        var table = PiperTransform.ParseTable(input, ",", hasHeaders: true);
        table.Should().NotBeNull();
        table!.Headers.Should().Equal("name", "city", "age");
        table.Rows.Should().HaveCount(2);
        table.Rows[0][0].Should().Be("Alice");
        table.Rows[1][2].Should().Be("25");
    }

    [Fact]
    public void ParseTable_with_semicolon_separator_parses_european_csv_style_input()
    {
        var input = "name;age\nAlice;30\nBob;25";
        var table = PiperTransform.ParseTable(input, ";", hasHeaders: true);
        table.Should().NotBeNull();
        table!.Headers.Should().Equal("name", "age");
        table.Rows.Should().HaveCount(2);
    }

    [Fact]
    public void ParseTable_with_pipe_separator_works()
    {
        var input = "a|b|c\n1|2|3";
        var table = PiperTransform.ParseTable(input, "|", hasHeaders: true);
        table.Should().NotBeNull();
        table!.Headers.Should().Equal("a", "b", "c");
        table.Rows.Should().HaveCount(1);
    }

    [Fact]
    public void ParseTable_returns_null_when_chosen_separator_is_absent()
    {
        // Wrong separator selected — fail closed so the caller renders a
        // hint asking the user to switch.
        PiperTransform.ParseTable("name,city\nAlice,NYC", "\t", hasHeaders: true).Should().BeNull();
    }

    [Fact]
    public void ParseTable_without_headers_labels_columns_by_one_based_position()
    {
        var input = "10000\tKontorcentralen A/S\n20000\tRavel Møbler";
        var table = PiperTransform.ParseTable(input, "\t", hasHeaders: false);
        table.Should().NotBeNull();
        table!.Headers.Should().Equal("Column 1", "Column 2");
        table.Rows.Should().HaveCount(2);
        table.Rows[0][0].Should().Be("10000");
        table.Rows[0][1].Should().Be("Kontorcentralen A/S");
    }

    [Fact]
    public void ParseTable_without_headers_accepts_a_single_data_row()
    {
        // With headers we need at least 2 rows (header + 1 data). Without
        // headers, one row is enough — every cell becomes a value.
        var table = PiperTransform.ParseTable("1\t2\t3", "\t", hasHeaders: false);
        table.Should().NotBeNull();
        table!.Headers.Should().Equal("Column 1", "Column 2", "Column 3");
        table.Rows.Should().HaveCount(1);
        table.Rows[0].Should().Equal("1", "2", "3");
    }

    [Fact]
    public void ParseTable_without_headers_uses_widest_row_as_column_count()
    {
        // Row widths: 2, 3, 1. The table is sized to the widest row;
        // narrower rows pad with empty strings.
        var input = "1,2\n3,4,5\n6";
        var table = PiperTransform.ParseTable(input, ",", hasHeaders: false);
        table.Should().NotBeNull();
        table!.Headers.Should().Equal("Column 1", "Column 2", "Column 3");
        table.Rows.Should().HaveCount(3);
        table.Rows[0].Should().Equal("1", "2", "");
        table.Rows[1].Should().Equal("3", "4", "5");
        table.Rows[2].Should().Equal("6", "", "");
    }

    [Fact]
    public void ParseTable_without_headers_treats_first_row_as_data_not_labels()
    {
        // The cell "name" looks like a header but with hasHeaders=false it
        // must still appear in row 0 as data.
        var input = "name,city\nAlice,NYC";
        var table = PiperTransform.ParseTable(input, ",", hasHeaders: false);
        table.Should().NotBeNull();
        table!.Headers.Should().Equal("Column 1", "Column 2");
        table.Rows.Should().HaveCount(2);
        table.Rows[0].Should().Equal("name", "city");
        table.Rows[1].Should().Equal("Alice", "NYC");
    }

    [Fact]
    public void ParseTable_default_separator_is_tab_so_existing_callers_keep_working()
    {
        // The first overload-less ParseTable() in the codebase relies on
        // the default ("\t", hasHeaders: true) — pin that contract.
        var table = PiperTransform.ParseTable("A\tB\n1\t2");
        table.Should().NotBeNull();
        table!.Headers.Should().Equal("A", "B");
        table.Rows.Should().ContainSingle().Which.Should().Equal("1", "2");
    }

    // ---------- RunOnItems (Table input mode → pipeline) ----------

    [Fact]
    public void RunOnItems_pipeline_matches_Run_defaults_with_BcOr_format()
    {
        var result = PiperTransform.RunOnItems(
            new[] { "a", "b", "c" },
            new PiperOptions(),
            "table column: \"X\"");
        result.Output.Should().Be("a|b|c");
        result.ItemCount.Should().Be(3);
        result.DelimiterDescription.Should().Be("table column: \"X\"");
        result.DetectedSeparatorDisplay.Should().BeNull();
    }

    [Fact]
    public void RunOnItems_applies_trim_skip_empty_dedup_sort_in_the_same_order_as_Run()
    {
        var result = PiperTransform.RunOnItems(
            new[] { " 2 ", "", " 1", "2 ", " 3 " },
            new PiperOptions
            {
                TrimItems = true,
                SkipEmpty = true,
                RemoveDuplicates = true,
                Sort = PiperSortOrder.Ascending,
            },
            "table column: \"Nummer\"");
        result.Output.Should().Be("1|2|3");
    }

    [Fact]
    public void RunOnItems_empty_list_yields_empty_output_and_zero_count()
    {
        var result = PiperTransform.RunOnItems(
            Array.Empty<string>(),
            new PiperOptions(),
            "table column: \"empty\"");
        result.Output.Should().BeEmpty();
        result.ItemCount.Should().Be(0);
        result.DelimiterDescription.Should().Be("table column: \"empty\"");
    }

    [Fact]
    public void RunOnItems_applies_format_presets_and_result_wrappers()
    {
        var result = PiperTransform.RunOnItems(
            new[] { "10000", "20000", "30000" },
            new PiperOptions
            {
                Format = PiperOutputFormat.Sql,
                ResultPrefix = "IN (",
                ResultSuffix = ")",
            },
            "table column: \"Nummer\"");
        result.Output.Should().Be("IN ('10000','20000','30000')");
    }

    [Fact]
    public void RunOnItems_picks_the_chosen_column_from_a_parsed_BC_table()
    {
        // End-to-end check of how the Piper page composes ParseTable +
        // RunOnItems: pull the "Nummer" column out of the sample paste and
        // build a BC OR filter string.
        var table = PiperTransform.ParseTable(SampleBcPaste);
        table.Should().NotBeNull();

        var columnIndex = table!.Headers.ToList().IndexOf("Nummer");
        var values = table.Rows.Select(r => r[columnIndex]).ToArray();

        var result = PiperTransform.RunOnItems(values, new PiperOptions(), "table column: \"Nummer\"");
        result.Output.Should().Be("10000|20000|30000|40000|50000");
        result.ItemCount.Should().Be(5);
    }
}
