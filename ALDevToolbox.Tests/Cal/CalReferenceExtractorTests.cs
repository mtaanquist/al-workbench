using ALDevToolbox.Services.Cal;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Cal;

/// <summary>
/// Unit coverage for the C/AL call-site walker: receiver method calls resolve
/// by id, runtime built-ins are skipped, field-name-taking built-ins emit a
/// field_access for their first argument, bare self-calls resolve to the owner,
/// and implicit <c>Rec</c> field access is captured.
/// </summary>
public sealed class CalReferenceExtractorTests
{
    private static CalExtractScope Scope() => new()
    {
        OwnerKind = "table",
        OwnerId = 18,
        Rec = new CalTypeRef("table", 18),
        Variables =
        {
            ["SalesSetup"] = new CalTypeRef("table", 311),
            ["NoSeriesMgt"] = new CalTypeRef("codeunit", 396),
        },
    };

    private static List<CalRef> Extract(string body) =>
        CalReferenceExtractor.Extract(body, Scope()).References.ToList();

    [Fact]
    public void Resolves_receiver_method_call_by_id()
    {
        var refs = Extract("BEGIN NoSeriesMgt.InitSeries(X,Y); END;");

        refs.Should().ContainSingle(r => r.ReferenceKind == "method_call"
            && r.TargetKind == "codeunit" && r.TargetId == 396 && r.MemberName == "InitSeries");
    }

    [Fact]
    public void Skips_runtime_builtin_methods()
    {
        var refs = Extract("BEGIN SalesSetup.GET; SalesSetup.MODIFY; END;");

        refs.Should().NotContain(r => r.MemberName == "GET" || r.MemberName == "MODIFY");
    }

    [Fact]
    public void A_static_dispatch_is_a_system_call_on_the_object_it_names()
    {
        // PAGE.RUNMODAL(PAGE::"Customer List",Cust) opens that page; the name
        // is resolved to an id at import, as for the literal reference itself.
        var result = CalReferenceExtractor.Extract(
            "BEGIN PAGE.RUNMODAL(PAGE::\"Customer List\",Cust); REPORT.RUN(206); CODEUNIT.RUN(CODEUNIT::80); END;",
            Scope());

        result.SystemReferences.Should().ContainSingle(r => r.MemberName == "RUNMODAL"
            && r.TargetKind == "page" && r.TargetId == null && r.TargetName == "Customer List");
        result.SystemReferences.Should().ContainSingle(r => r.MemberName == "RUN"
            && r.TargetKind == "codeunit" && r.TargetId == 80);
        result.References.Should().Contain(r => r.ReferenceKind == "property_object" && r.TargetName == "Customer List");
    }

    [Fact]
    public void Paren_less_builtin_is_a_system_method_call()
    {
        // `SalesSetup.GET;` is a call, not a field read — classic C/AL drops
        // the parentheses on parameterless built-ins. It belongs in the
        // system-reference bucket like its parenthesised twin. See issue #712.
        var result = CalReferenceExtractor.Extract("BEGIN SalesSetup.GET; END;", Scope());

        result.SystemReferences.Should().ContainSingle(r => r.MemberName == "GET"
            && r.ReferenceKind == "method_call" && r.TargetKind == "table" && r.TargetId == 311);
        result.References.Should().NotContain(r => r.MemberName == "GET");
    }

    [Fact]
    public void Paren_less_member_access_emits_a_row_the_import_can_reclassify()
    {
        // A paren-less call to a procedure on the receiver reads exactly like
        // a field read here — the walker sees one object at a time, so the
        // import's post-pass promotes the row to method_call when the name
        // matches a procedure on the target. See issue #712.
        var refs = Extract("BEGIN SalesSetup.\"Customer Nos.\"; END;");

        refs.Should().ContainSingle(r => r.ReferenceKind == "field_access"
            && r.TargetKind == "table" && r.TargetId == 311 && r.MemberName == "Customer Nos.");
    }

    [Fact]
    public void Field_name_taking_builtin_emits_field_access_for_first_arg()
    {
        var refs = Extract("BEGIN SalesSetup.TESTFIELD(\"Customer Nos.\"); END;");

        // TESTFIELD itself is skipped; its field argument resolves on SalesSetup (table 311).
        refs.Should().NotContain(r => r.MemberName == "TESTFIELD");
        refs.Should().ContainSingle(r => r.ReferenceKind == "field_access"
            && r.TargetKind == "table" && r.TargetId == 311 && r.MemberName == "Customer Nos.");
    }

    [Fact]
    public void Captures_implicit_rec_field_access()
    {
        var refs = Extract("BEGIN IF \"No.\" = '' THEN \"No.\" := '1'; END;");

        refs.Should().Contain(r => r.ReferenceKind == "field_access"
            && r.TargetKind == "table" && r.TargetId == 18 && r.MemberName == "No.");
    }

    [Fact]
    public void Bare_call_resolves_to_owner_object()
    {
        var refs = Extract("BEGIN SetDefaultSalesperson; UpdateReferencedIds; END;");

        // Bare unqualified identifiers followed by no args aren't calls; with args they are.
        var withArgs = Extract("BEGIN DoStuff(1); END;");
        withArgs.Should().ContainSingle(r => r.ReferenceKind == "method_call"
            && r.TargetKind == "table" && r.TargetId == 18 && r.MemberName == "DoStuff");
    }

    // ── Static object receivers (KIND::Name) — issue #713 ───────────────

    [Theory]
    [InlineData("BEGIN CODEUNIT.RUN(CODEUNIT::\"Sales-Post\",Rec); END;", "codeunit", "Sales-Post")]
    [InlineData("BEGIN REPORT.RUNMODAL(REPORT::\"Sales Document - Test\"); END;", "report", "Sales Document - Test")]
    [InlineData("BEGIN PAGE.RUNMODAL(PAGE::\"Customer List\",Rec); END;", "page", "Customer List")]
    [InlineData("BEGIN XMLPORT.RUN(XMLPORT::\"Import Data\"); END;", "xmlport", "Import Data")]
    [InlineData("BEGIN Mgt.Run(QUERY::\"Customer Sales\"); END;", "query", "Customer Sales")]
    [InlineData("BEGIN FORM.RUNMODAL(FORM::\"Customer Card\"); END;", "form", "Customer Card")]
    [InlineData("BEGIN DimMgt.DeleteDefaultDim(DATABASE::Customer,\"No.\"); END;", "table", "Customer")]
    public void Static_receiver_emits_object_reference_by_name(string body, string kind, string name)
    {
        var refs = Extract(body);

        refs.Should().ContainSingle(r => r.ReferenceKind == "property_object"
            && r.TargetKind == kind && r.TargetName == name && r.TargetId == null
            && r.MemberName == null);
    }

    [Theory]
    [InlineData("BEGIN CODEUNIT.RUN(CODEUNIT::\"Sales-Post\",Rec); END;", "Sales-Post")]
    [InlineData("BEGIN PAGE.RUNMODAL(PAGE::\"Customer List\",Rec); END;", "Customer List")]
    [InlineData("BEGIN DimMgt.DeleteDefaultDim(DATABASE::Customer,\"No.\"); END;", "Customer")]
    public void Static_receiver_name_is_not_mistaken_for_a_rec_field(string body, string name)
    {
        // The bug: the object name fell through to the bare-head dispatcher and
        // was emitted as a field_access on the current Rec table (#713).
        var refs = Extract(body);

        refs.Should().NotContain(r => r.ReferenceKind == "field_access" && r.MemberName == name);
    }

    [Fact]
    public void Static_receiver_with_numeric_id_resolves_by_id()
    {
        var refs = Extract("BEGIN CODEUNIT.RUN(CODEUNIT::80,Rec); END;");

        refs.Should().ContainSingle(r => r.ReferenceKind == "property_object"
            && r.TargetKind == "codeunit" && r.TargetId == 80 && r.TargetName == null);
    }

    [Fact]
    public void Option_value_qualifier_is_still_skipped()
    {
        // Only the object keywords introduce an object literal; `x::Value` on an
        // option-typed variable stays a value reference we don't model.
        var refs = Extract("BEGIN IF Status = Status::Released THEN EXIT; END;");

        refs.Should().NotContain(r => r.ReferenceKind == "property_object");
        refs.Should().NotContain(r => r.MemberName == "Released");
    }

    [Fact]
    public void Static_receiver_keeps_the_surrounding_field_arguments()
    {
        // DimMgt.DeleteDefaultDim(DATABASE::Customer,"No.") — the object literal
        // resolves AND the quoted field after it still reads as a Rec field.
        var refs = Extract("BEGIN DimMgt.DeleteDefaultDim(DATABASE::Customer,\"No.\"); END;");

        refs.Should().Contain(r => r.ReferenceKind == "field_access"
            && r.TargetKind == "table" && r.TargetId == 18 && r.MemberName == "No.");
    }

    [Fact]
    public void Skips_bare_runtime_functions_and_unresolved_receivers()
    {
        var refs = Extract("BEGIN MESSAGE('hi'); Unknown.Frobnicate(); END;");

        refs.Should().NotContain(r => r.MemberName == "MESSAGE");
        // Unknown receiver isn't in scope → no reference, but counted as unresolved.
        var result = CalReferenceExtractor.Extract("BEGIN Unknown.Frobnicate(); END;", Scope());
        result.References.Should().BeEmpty();
        result.UnresolvedReceivers.Should().Be(1);
    }
}
