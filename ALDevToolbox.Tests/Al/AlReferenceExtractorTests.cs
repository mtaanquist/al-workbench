using System;
using System.Collections.Generic;
using System.Linq;
using ALDevToolbox.Services.Al;
using AwesomeAssertions;
using ALDevToolbox.Services.ObjectExplorer.Import;

namespace ALDevToolbox.Tests.Al;

public sealed class AlReferenceExtractorTests
{
    // ── Test fixtures: a small in-memory type catalog ───────────────

    private static readonly Guid BaseAppId = Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972");

    /// <summary>
    /// Small catalog with the few types every test below references.
    /// Real imports build the catalog from oe_module_objects /
    /// oe_module_symbols at start-of-pipeline; the resolver interface
    /// stays narrow so unit tests stub it directly.
    /// </summary>
    private static StubResolver MakeResolver()
    {
        var r = new StubResolver();
        // Types.
        r.AddType("Customer", new AlTypeRef(BaseAppId, "table", 18, "Customer"));
        r.AddType("Sales Header", new AlTypeRef(BaseAppId, "table", 36, "Sales Header"));
        r.AddType("Sales Line", new AlTypeRef(BaseAppId, "table", 37, "Sales Line"));
        r.AddType("Sales-Post", new AlTypeRef(BaseAppId, "codeunit", 80, "Sales-Post"));
        // Members on Customer.
        r.AddMember("Customer", new AlMember("Insert", "procedure", null, null));
        r.AddMember("Customer", new AlMember("Validate", "procedure", null, null));
        r.AddMember("Customer", new AlMember("Get", "procedure", null, null));
        r.AddMember("Customer", new AlMember("No.", "table_field",null, null));
        r.AddMember("Customer", new AlMember("Name", "table_field",null, null));
        // Members on Sales Header — one field returns a record so we can
        // test chained access through a record-typed field.
        r.AddMember("Sales Header", new AlMember("Sell-to Customer No.", "table_field",null, null));
        r.AddMember("Sales Header", new AlMember("Customer", "table_field","Record", "Customer"));
        // Members on Sales Line.
        r.AddMember("Sales Line", new AlMember("InitRecord", "procedure", null, null));
        r.AddMember("Sales Line", new AlMember("No.", "table_field",null, null));
        // Members on Sales-Post.
        r.AddMember("Sales-Post", new AlMember("Run", "procedure", null, null));
        return r;
    }

    private static AlExtractContext OwnerCodeunit(StubResolver resolver,
        Dictionary<string, ResolvedVariableType>? globals = null,
        string? tableNo = null) => new(
            OwnerKind: "codeunit",
            OwnerName: "MyHelper",
            OwnerObjectId: 50000,
            OwnerAppId: BaseAppId,
            GlobalVars: globals ?? new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: resolver,
            OwnerSourceTableName: tableNo);

    private static AlExtractContext OwnerTable(StubResolver resolver, string tableName,
        Dictionary<string, ResolvedVariableType>? globals = null) => new(
            OwnerKind: "table",
            OwnerName: tableName,
            OwnerObjectId: null,
            OwnerAppId: BaseAppId,
            GlobalVars: globals ?? new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: resolver);

    private static AlExtractContext OwnerPage(StubResolver resolver, string pageName, string? sourceTable,
        Dictionary<string, ResolvedVariableType>? globals = null) => new(
            OwnerKind: "page",
            OwnerName: pageName,
            OwnerObjectId: null,
            OwnerAppId: BaseAppId,
            GlobalVars: globals ?? new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: resolver,
            OwnerSourceTableName: sourceTable);

    private static AlExtractContext OwnerTableExtension(StubResolver resolver, string extName, string? baseTable,
        Dictionary<string, ResolvedVariableType>? globals = null) => new(
            OwnerKind: "tableextension",
            OwnerName: extName,
            OwnerObjectId: null,
            OwnerAppId: BaseAppId,
            GlobalVars: globals ?? new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: resolver,
            OwnerSourceTableName: baseTable);

    private static AlExtractContext OwnerPageExtension(StubResolver resolver, string extName, string? sourceTable,
        Dictionary<string, ResolvedVariableType>? globals = null) => new(
            OwnerKind: "pageextension",
            OwnerName: extName,
            OwnerObjectId: null,
            OwnerAppId: BaseAppId,
            GlobalVars: globals ?? new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: resolver,
            OwnerSourceTableName: sourceTable);

    private static AlExtractContext OwnerXmlPort(StubResolver resolver, string xmlPortName,
        Dictionary<string, ResolvedVariableType>? globals = null) => new(
            OwnerKind: "xmlport",
            OwnerName: xmlPortName,
            OwnerObjectId: null,
            OwnerAppId: BaseAppId,
            GlobalVars: globals ?? new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: resolver);

    private static AlExtractContext OwnerReport(StubResolver resolver, string reportName,
        Dictionary<string, ResolvedVariableType>? globals = null) => new(
            OwnerKind: "report",
            OwnerName: reportName,
            OwnerObjectId: null,
            OwnerAppId: BaseAppId,
            GlobalVars: globals ?? new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: resolver);

    // ── Behavioural tests ───────────────────────────────────────────

    [Fact]
    public void Method_call_on_parameter_resolves_to_owner_table()
    {
        const string src = """
            procedure Process(var SalesLine: Record "Sales Line")
            begin
                SalesLine.InitRecord();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Sales Line"
            && r.TargetMemberName == "InitRecord"
            && r.TargetMemberKind == "procedure");
        // Parameter type `Record "Sales Line"` also emits a property_object
        // ref so the type name in the parameter list is underlined.
        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "property_object"
            && r.TargetObjectName == "Sales Line"
            && r.TargetObjectKind == "table");
        result.Stats.ResolvedReferences.Should().Be(2);
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Field_access_on_local_var_resolves_to_table_field()
    {
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust."No." := 'C001';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "field_access"
            && r.TargetObjectName == "Customer"
            && r.TargetMemberName == "No.");
    }

    [Fact]
    public void Object_scoped_global_variable_resolves_across_procedures()
    {
        var globals = new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cust"] = new ResolvedVariableType("Record", "Customer"),
        };
        const string src = """
            procedure A() begin Cust.Insert(); end;
            procedure B() begin Cust.Validate(); end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver(), globals));

        result.References.Should().HaveCount(2);
        result.References.Should().AllSatisfy(r =>
            r.TargetObjectName.Should().Be("Customer"));
        result.References.Select(r => r.TargetMemberName).Should().BeEquivalentTo(new[] { "Insert", "Validate" });
    }

    [Fact]
    public void Local_var_shadows_global_with_same_name()
    {
        // Object-global `Cust` is a Sales Line, but the procedure-local
        // `Cust` is a Customer. The reference inside the procedure
        // resolves to the LOCAL type.
        var globals = new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cust"] = new ResolvedVariableType("Record", "Sales Line"),
        };
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.Validate();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver(), globals));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "Validate");
    }

    [Fact]
    public void Rec_inside_a_table_resolves_to_the_owning_table()
    {
        // Inside a table object, `Rec` is the table itself.
        const string src = """
            trigger OnInsert()
            begin
                Rec."No." := 'C001';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Customer"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "No.");
    }

    [Fact]
    public void System_typed_variable_yields_no_reference()
    {
        // `Client: HttpClient` is a runtime type, not an AL object.
        // HttpClient lives in `AlBuiltinMethods.KnownSystemTypes`, so
        // the chain through it is silently skipped — no reference
        // emitted AND no unresolved-counter bump. Without the system-
        // type filter every HttpClient.Get call in BC's REST stack
        // would inflate the diagnostic.
        const string src = """
            procedure Foo()
            var
                Client: HttpClient;
            begin
                Client.Get('http://example.com');
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Chained_access_through_record_field_resolves_at_each_step()
    {
        // SalesHeader."Sell-to Customer No." is a plain field access
        // (scalar). SalesHeader.Customer is a record-typed field that
        // would let chaining continue (Customer."Name") — both refs
        // should appear in the output.
        const string src = """
            procedure Foo()
            var
                SalesHeader: Record "Sales Header";
            begin
                Message(SalesHeader."Sell-to Customer No.");
                Message(SalesHeader.Customer."No.");
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        // Four references in total:
        //   1. SalesHeader: Record "Sales Header" → property_object on type name
        //   2. SalesHeader."Sell-to Customer No." → field on Sales Header
        //   3. SalesHeader.Customer → field on Sales Header (record-typed)
        //   4. Customer."No." → field on Customer (chained via Customer field)
        result.References.Should().HaveCount(4);
        result.References.Should().Contain(r =>
            r.ReferenceKind == "property_object" && r.TargetObjectName == "Sales Header");
        result.References.Should().Contain(r =>
            r.TargetObjectName == "Sales Header" && r.TargetMemberName == "Sell-to Customer No.");
        result.References.Should().Contain(r =>
            r.TargetObjectName == "Sales Header" && r.TargetMemberName == "Customer");
        result.References.Should().Contain(r =>
            r.TargetObjectName == "Customer" && r.TargetMemberName == "No.");
    }

    [Fact]
    public void Extension_member_resolves_and_targets_the_extension_object()
    {
        // CustomerExt is a tableextension on Customer that adds a
        // procedure named DKValidate. `Cust.DKValidate()` should
        // resolve through the extension widening and emit a reference
        // whose target is the EXTENSION (not the base Customer), so
        // Find references on DKValidate's declaration row picks the
        // call up.
        var resolver = MakeResolver();
        resolver.AddType("CustomerExt", new AlTypeRef(BaseAppId, "tableextension", 50000, "CustomerExt"));
        resolver.AddExtensionOf("Customer", "CustomerExt");
        resolver.AddMember("CustomerExt", new AlMember("DKValidate", "procedure", null, null));

        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.DKValidate();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "CustomerExt"
            && r.TargetObjectKind == "tableextension"
            && r.TargetMemberName == "DKValidate"
            && r.ReferenceKind == "method_call");
    }

    [Fact]
    public void Base_member_shadows_same_name_extension_member()
    {
        // Customer has a Validate procedure of its own AND
        // CustomerExt also has one. AL dispatch picks the base, so the
        // extractor should emit Customer.Validate — not the extension.
        var resolver = MakeResolver();
        resolver.AddType("CustomerExt", new AlTypeRef(BaseAppId, "tableextension", 50000, "CustomerExt"));
        resolver.AddExtensionOf("Customer", "CustomerExt");
        resolver.AddMember("CustomerExt", new AlMember("Validate", "procedure", null, null));

        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.Validate();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "Validate");
    }

    [Fact]
    public void Type_literal_receiver_resolves_directly()
    {
        // `Customer.Insert(true)` — the qualifier is a type name, not a
        // variable. The extractor recognises it via the type catalog and
        // resolves the member on the type itself.
        const string src = """
            procedure Foo()
            begin
                Customer.Insert(true);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "Insert");
    }

    [Fact]
    public void Unresolved_receiver_drops_the_reference_and_records_the_stat()
    {
        // `unknownVar.DoSomething()` — neither a variable nor a known
        // type. Receiver resolution returns null; nothing is emitted.
        const string src = """
            procedure Foo() begin unknownVar.DoSomething(); end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(1);
    }

    [Fact]
    public void Record_builtin_method_is_skipped_silently_not_counted_as_unresolved()
    {
        // SetRange / FindFirst / IsEmpty are AL runtime built-ins, not
        // user-declared procedures. The receiver resolves to Customer
        // but the member lookup misses (built-ins aren't in the
        // catalog). AlBuiltinMethods.IsBuiltin recognises them and
        // filters them out so the unresolved counter stays clean.
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.SetRange("No.", 'C001');
                Cust.FindFirst();
                if Cust.IsEmpty() then exit;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Where(r => r.TargetMemberName is "SetRange" or "FindFirst" or "IsEmpty")
            .Should().BeEmpty(
                because: "SetRange/FindFirst/IsEmpty are runtime built-ins, not user-declared catalog members");
        result.Stats.UnresolvedReceivers.Should().Be(0,
            because: "built-ins are filtered explicitly so they don't muddy the diagnostic counter");
    }

    [Fact]
    public void Enum_static_method_on_name_colliding_type_literal_is_silenced()
    {
        // `"Ext. File Storage Connector".Ordinals()` — BC ships an enum and a
        // same-named table, so the type-literal head resolves to the table.
        // Ordinals() is an enum-static built-in, so it must be filtered, not
        // counted as a chain-step unresolved against the table.
        var resolver = MakeResolver();
        resolver.AddType("Ext. File Storage Connector",
            new AlTypeRef(BaseAppId, "table", 9450, "Ext. File Storage Connector"));
        const string src = """
            procedure GetOrdinals(): List of [Integer]
            begin
                exit("Ext. File Storage Connector".Ordinals());
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedReceivers.Should().Be(0,
            because: "enum-static reflection methods are built-ins even when the name resolves to a table");
    }

    [Fact]
    public void User_declared_method_with_same_name_as_builtin_still_emits_reference()
    {
        // Customer.Validate IS in the resolver (added by MakeResolver).
        // The lookup succeeds before the built-in filter would run, so
        // the user-declared Validate procedure wins — the reference
        // gets emitted normally.
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.Validate();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer" && r.TargetMemberName == "Validate");
    }

    [Fact]
    public void System_field_on_record_is_treated_as_builtin()
    {
        // SystemId, SystemCreatedAt etc. are added to every table by
        // the AL runtime — not declared in source. Don't emit a
        // reference and don't count as unresolved.
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                if Cust."SystemId" <> NullGuid then exit;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        // Only the property_object on `Record Customer`'s type name; the
        // SystemId field access is a built-in and emits nothing.
        result.References.Should().ContainSingle();
        result.References[0].ReferenceKind.Should().Be("property_object");
        result.References[0].TargetObjectName.Should().Be("Customer");
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Method_call_emits_method_call_kind_field_access_emits_field_access_kind()
    {
        // The reference_kind comes from syntactic context: followed by
        // `(` is a method_call, anything else is a field_access.
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.Insert();
                Cust."Name";
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().Contain(r =>
            r.TargetMemberName == "Insert" && r.ReferenceKind == "method_call");
        result.References.Should().Contain(r =>
            r.TargetMemberName == "Name" && r.ReferenceKind == "field_access");
    }

    [Fact]
    public void Paren_less_call_to_a_user_procedure_is_a_method_call()
    {
        // AL lets a parameterless call drop its parentheses. `Cust.Insert;`
        // is a call, not a read — the resolved member's kind says so even
        // though the syntax doesn't. See issue #712.
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.Insert;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.TargetMemberName == "Insert" && r.ReferenceKind == "method_call");
    }

    [Fact]
    public void Paren_less_field_read_stays_a_field_access()
    {
        // The counterpart to the test above: same shape, but the member
        // resolves to a table field, so it stays a read.
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                if Cust.Name = '' then exit;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.TargetMemberName == "Name" && r.ReferenceKind == "field_access");
    }

    [Fact]
    public void Paren_less_builtin_is_a_method_call_but_a_system_field_is_not()
    {
        // Built-ins land in the system-reference table. `Modify` without
        // parens is still a call; `SystemId` is a runtime field, so it stays
        // a read. See issue #712.
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.Modify;
                if IsNullGuid(Cust.SystemId) then exit;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.SystemReferences.Should().ContainSingle(r =>
            r.SystemMethodName == "Modify" && r.ReferenceKind == "method_call");
        result.SystemReferences.Should().ContainSingle(r =>
            r.SystemMethodName == "SystemId" && r.ReferenceKind == "field_access");
    }

    [Fact]
    public void Non_ascii_identifiers_lex_and_resolve()
    {
        // Danish, German and French AL codebases use accented characters in
        // variable and procedure names. The lexer used to accept ASCII
        // letters only, so `Købsordre` split into two tokens and every
        // member access on it stranded. See issue #712.
        var resolver = MakeResolver();
        resolver.AddType("Købsordre", new AlTypeRef(BaseAppId, "table", 50100, "Købsordre"));
        resolver.AddMember("Købsordre", new AlMember("Åbn Salgsordre", "procedure", null, null));
        resolver.AddMember("Købsordre", new AlMember("Beløb", "table_field", null, null));

        const string src = """
            procedure Foo()
            var
                Købsordre: Record Købsordre;
            begin
                Købsordre."Åbn Salgsordre"();
                if Købsordre.Beløb > 0 then exit;
            end;
            """;

        AlLexer.Tokenize("Købsordre.Beløb").Select(t => t.Value)
            .Should().Equal(new[] { "Købsordre", ".", "Beløb" });

        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Købsordre"
            && r.TargetMemberName == "Åbn Salgsordre"
            && r.ReferenceKind == "method_call");
        result.References.Should().ContainSingle(r =>
            r.TargetMemberName == "Beløb" && r.ReferenceKind == "field_access");
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Procedure_with_parameters_only_picks_up_param_types()
    {
        // No var block — parameter `c` should still be in scope inside
        // the body.
        const string src = """
            procedure Foo(c: Record Customer; n: Integer)
            begin
                c.Insert();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer" && r.TargetMemberName == "Insert");
    }

    [Fact]
    public void Typed_literal_receiver_resolves_kind_colon_colon_name()
    {
        // `Codeunit::"Sales-Post".Run(SalesHeader)` — the receiver is
        // a Kind::"Name" type literal, not a variable. Extremely common
        // BC pattern; should resolve to the Sales-Post codeunit and
        // emit a method_call reference for Run.
        const string src = """
            procedure Foo()
            var
                SalesHeader: Record "Sales Header";
            begin
                Codeunit::"Sales-Post".Run(SalesHeader);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Sales-Post"
            && r.TargetMemberName == "Run");
    }

    [Fact]
    public void Typed_literal_with_unknown_name_drops_chain_as_unresolved()
    {
        // `Codeunit::"Unknown Helper".DoX()` — the literal name doesn't
        // resolve to any object in the catalog; chain drops without
        // false positives.
        const string src = """
            procedure Foo()
            begin
                Codeunit::"Unknown Helper".DoX();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(1);
    }

    [Fact]
    public void Generic_typed_var_does_not_swallow_the_next_declaration()
    {
        // `Dictionary of [Text, Integer]` parsed naively eats the `of`
        // as the next variable name, breaks looking for `:`, then
        // skips to the next `;` — silently dropping the var declared
        // right after. ReadTypeReference now consumes `of [...]` so
        // every subsequent declaration in the var block parses cleanly.
        const string src = """
            procedure Foo()
            var
                Lookup: Dictionary of [Text, Integer];
                Cust: Record Customer;
            begin
                Cust.Insert();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        // Cust IS in scope after the Dictionary declaration; its
        // .Insert resolves to Customer because the test fixture has
        // Customer.Insert in the user-declared catalog.
        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer" && r.TargetMemberName == "Insert");
    }

    [Fact]
    public void List_typed_var_with_generic_parameter_parses_cleanly()
    {
        const string src = """
            procedure Foo()
            var
                Names: List of [Text];
                Cust: Record Customer;
            begin
                Cust.Validate();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer" && r.TargetMemberName == "Validate");
    }

    [Fact]
    public void Reference_carries_source_line_and_column_of_the_member()
    {
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.Insert();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        var insertRef = result.References.Single(r => r.TargetMemberName == "Insert");
        insertRef.Line.Should().Be(5,
            because: "Insert sits on the fifth line of the snippet (1-based)");
        // The member name starts at column 10 (after "Cust." inside the indent).
        insertRef.Column.Should().BeGreaterThan(1);
    }

    // ── Rec on pages / pageextensions resolves to SourceTable ──────

    [Fact]
    public void Rec_inside_a_page_resolves_to_the_source_table()
    {
        // On page 42 "Sales Order" with SourceTable = "Sales Header",
        // `Rec."No."` should resolve to the field "No." on Sales Header —
        // not as a member on the page itself (which doesn't have fields).
        const string src = """
            trigger OnAfterGetCurrRecord()
            begin
                Rec."Sell-to Customer No." := 'C001';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(MakeResolver(), "Sales Order", "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "Sell-to Customer No."
            && r.ReferenceKind == "field_access");
    }

    [Fact]
    public void Rec_method_call_inside_a_page_resolves_via_the_source_table()
    {
        // `Rec.Insert()` on a page triggers the Record's Insert built-in
        // via the SourceTable — but more importantly, calls to USER
        // procedures defined on the source table (or on tableextensions
        // of it) must resolve. AL pages routinely call procedures the
        // table itself declares; this guarantees those calls underline.
        var resolver = MakeResolver();
        // Add a user-declared procedure on Sales Header.
        resolver.AddMember("Sales Header", new AlMember("CheckCreditMaxBeforeInsert", "procedure", null, null));

        const string src = """
            trigger OnValidate()
            begin
                Rec.CheckCreditMaxBeforeInsert();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(resolver, "Sales Order", "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "CheckCreditMaxBeforeInsert"
            && r.ReferenceKind == "method_call");
    }

    [Fact]
    public void Rec_inside_a_pageextension_resolves_to_the_propagated_source_table()
    {
        // Pageextensions don't carry SourceTable in their own symbol-package
        // properties; the importer's second-pass copies it from the base
        // page. The extractor consumes that propagated value the same way.
        const string src = """
            trigger OnAfterGetCurrRecord()
            begin
                Rec."Sell-to Customer No." := 'C001';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src,
            OwnerPageExtension(MakeResolver(), "Sales Order Ext", "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "Sell-to Customer No.");
    }

    [Fact]
    public void Rec_inside_a_page_without_source_table_falls_back_silently()
    {
        // Pages without an explicit SourceTable (rare — listpages typically
        // have one, but pages without a table source exist). The extractor
        // shouldn't crash and shouldn't produce a wrong reference; it just
        // can't resolve the chain.
        const string src = """
            trigger OnOpenPage()
            begin
                Rec."No." := 'X';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(MakeResolver(), "Some Card", null));

        // Page owner type "page" doesn't have a "No." field; ResolveMember
        // returns null and the chain is silently dropped (no spurious
        // emit, no crash).
        result.References.Should().BeEmpty();
    }

    // ── More object-scope properties ───────────────────────────────

    [Fact]
    public void Run_object_property_emits_target_reference()
    {
        // Pages declare RunObject = Page "X" / Codeunit "Y" / Report "Z"
        // on action blocks. Same shape as SourceTable but with a kind
        // keyword in front of the name. The kind keyword is consumed
        // and the catalog resolves the name to the canonical kind.
        var resolver = MakeResolver();
        resolver.AddType("Customer Card", new AlTypeRef(BaseAppId, "page", 21, "Customer Card"));

        const string src = """
            page 22 "Customer List"
            {
                actions
                {
                    action("Card")
                    {
                        RunObject = Page "Customer Card";
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(resolver, "Customer List", null));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer Card"
            && r.TargetObjectKind == "page"
            && r.ReferenceKind == "property_object");
    }

    [Fact]
    public void Table_relation_emits_target_table()
    {
        // Simple form: `TableRelation = Customer;` → property_object on
        // the Customer table. The user's primary use case is "click
        // through into the related table when investigating a record".
        const string src = """
            table 36 "Sales Header"
            {
                fields
                {
                    field(2; "Sell-to Customer No."; Code[20])
                    {
                        TableRelation = Customer;
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetObjectKind == "table"
            && r.ReferenceKind == "property_object");
    }

    [Fact]
    public void Table_relation_with_filter_still_emits_the_base_table()
    {
        // `TableRelation = Customer where(…)` — the with-filter form
        // is common. v1 emits the table reference; field references
        // inside the filter expression stay deferred. The user can
        // still click through to the Customer table.
        const string src = """
            table 36 "Sales Header"
            {
                fields
                {
                    field(2; "Sell-to Customer No."; Code[20])
                    {
                        TableRelation = Customer where("Customer Posting Group" = const('DOMESTIC'));
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Sales Header"));

        result.References.Should().Contain(r =>
            r.TargetObjectName == "Customer"
            && r.ReferenceKind == "property_object");
    }

    [Fact]
    public void Table_relation_conditional_emits_a_row_per_branch_table()
    {
        // `TableRelation = if (X) A."Y" else B."Z"` — emit both A and B
        // so the user can shortcut into either branch's table.
        var resolver = MakeResolver();
        resolver.AddType("Item", new AlTypeRef(BaseAppId, "table", 27, "Item"));
        resolver.AddType("Resource", new AlTypeRef(BaseAppId, "table", 156, "Resource"));

        const string src = """
            table 37 "Sales Line"
            {
                fields
                {
                    field(6; "No."; Code[20])
                    {
                        TableRelation = if (Type = const(Item)) Item."No." else Resource."No.";
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(resolver, "Sales Line"));

        result.References.Where(r => r.ReferenceKind == "property_object")
            .Select(r => r.TargetObjectName)
            .Should().Contain(new[] { "Item", "Resource" });
    }

    [Fact]
    public void Permissions_emits_one_reference_per_tabledata_target()
    {
        // `Permissions = tabledata "Customer" = rm, tabledata "Sales Header" = m;`
        // — the rights spec is irrelevant to references; we just want
        // the click-through into each listed table.
        const string src = """
            codeunit 80 "Sales-Post"
            {
                Permissions = tabledata "Customer" = rm, tabledata "Sales Header" = m;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        var perms = result.References.Where(r => r.ReferenceKind == "property_object").ToList();
        perms.Should().HaveCount(2);
        perms.Select(r => r.TargetObjectName)
            .Should().BeEquivalentTo(new[] { "Customer", "Sales Header" });
        perms.Should().AllSatisfy(r => r.TargetObjectKind.Should().Be("table"));
    }

    [Fact]
    public void Var_block_record_type_name_emits_a_property_object_reference()
    {
        // `GLSetup: Record "General Ledger Setup";` — the user wants the
        // type name underlined and clickable so Find references on the
        // Record type name in a var/parameter declaration jumps to the
        // table. Repro of the regression in BC's `Company-Initialize`
        // codeunit where dozens of `Record "X Setup"` declarations had
        // no underline.
        var resolver = MakeResolver();
        resolver.AddType("General Ledger Setup",
            new AlTypeRef(BaseAppId, "table", 98, "General Ledger Setup"));
        resolver.AddType("Workflow Setup",
            new AlTypeRef(BaseAppId, "codeunit", 1500, "Workflow Setup"));

        const string src = """
            codeunit 2 "Company-Initialize"
            {
                trigger OnRun()
                var
                    GLSetup: Record "General Ledger Setup";
                    WorkflowSetup: Codeunit "Workflow Setup";
                    Window: Dialog;
                begin
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        var props = result.References
            .Where(r => r.ReferenceKind == "property_object")
            .ToList();
        props.Should().ContainSingle(r =>
            r.TargetObjectName == "General Ledger Setup" && r.TargetObjectKind == "table");
        props.Should().ContainSingle(r =>
            r.TargetObjectName == "Workflow Setup" && r.TargetObjectKind == "codeunit");
        // `Dialog` is a system type — no AL keyword, no resolved name, no emission.
        props.Should().HaveCount(2);
    }

    [Fact]
    public void Parameter_record_type_name_emits_a_property_object_reference()
    {
        // Same emission shape as locals — `procedure Foo(Cust: Record Customer)`
        // underlines `Customer`.
        const string src = """
            procedure Foo(SalesLine: Record "Sales Line")
            begin
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "property_object"
            && r.TargetObjectName == "Sales Line"
            && r.TargetObjectKind == "table");
    }

    [Fact]
    public void Var_block_bare_scalar_type_does_not_emit()
    {
        // `i: Integer;`, `f: Boolean;` — no AL object keyword, must not
        // emit. (Stub resolver would refuse to resolve these anyway,
        // but the keyword guard is what we're asserting.)
        const string src = """
            procedure Foo()
            var
                i: Integer;
                f: Boolean;
                s: Text[100];
            begin
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Where(r => r.ReferenceKind == "property_object")
            .Should().BeEmpty();
    }

    [Fact]
    public void Field_declaration_with_enum_type_emits_enum_reference()
    {
        // `field(1; "Document Type"; Enum "Sales Document Type")` — the
        // third arg is an AL object reference (the enum). Click-through
        // from the field declaration into the enum.
        var resolver = MakeResolver();
        resolver.AddType("Sales Document Type", new AlTypeRef(BaseAppId, "enum", 39, "Sales Document Type"));

        const string src = """
            table 36 "Sales Header"
            {
                fields
                {
                    field(1; "Document Type"; Enum "Sales Document Type") { }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(resolver, "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Sales Document Type"
            && r.TargetObjectKind == "enum"
            && r.ReferenceKind == "property_object");
    }

    [Fact]
    public void Field_declaration_with_scalar_type_emits_no_reference()
    {
        // `field(2; "Sell-to Customer No."; Code[20])` — `Code` isn't an
        // AL object keyword (it's a primitive scalar). Must not try
        // to resolve it as an object.
        const string src = """
            table 36 "Sales Header"
            {
                fields
                {
                    field(2; "Sell-to Customer No."; Code[20]) { }
                    field(3; "Amount"; Decimal) { }
                    field(4; "Posted"; Boolean) { }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Sales Header"));

        result.References.Where(r => r.ReferenceKind == "property_object")
            .Should().BeEmpty(
                because: "scalar primitive types (Code, Decimal, Boolean) aren't AL objects");
    }

    // ── Labels (tranche 3) ─────────────────────────────────────────

    [Fact]
    public void Bare_use_of_object_scope_label_emits_label_use()
    {
        // `Error(UnsupportedTypeErr)` — UnsupportedTypeErr is declared
        // in the codeunit's object-scope var block as a Label. The bare
        // identifier in the procedure body must resolve as a label_use
        // reference targeting the codeunit + label name + "label" kind.
        const string src = """
            codeunit 50100 "Foo"
            {
                var
                    UnsupportedTypeErr: Label 'Unsupported type %1.';

                procedure DoStuff()
                begin
                    Error(UnsupportedTypeErr);
                end;
            }
            """;
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50100, "MyHelper"));
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "label_use"
            && r.TargetMemberName == "UnsupportedTypeErr"
            && r.TargetMemberKind == "label");
    }

    [Fact]
    public void Procedure_local_label_use_does_not_emit_object_scope_ref()
    {
        // A label declared inside a procedure's var block is local to
        // that procedure. The walker's existing ParseVarBlock adds it
        // to the procedure frame; the implicit-Rec / label_use handlers
        // see it as in-scope. Should still emit label_use targeting the
        // file owner (oe_module_symbols stores procedure-locals against
        // the owner object too).
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50100, "MyHelper"));

        const string src = """
            codeunit 50100 "Foo"
            {
                procedure DoStuff()
                var
                    LocalErr: Label 'Local error';
                begin
                    Error(LocalErr);
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "label_use"
            && r.TargetMemberName == "LocalErr");
    }

    [Fact]
    public void Non_label_in_scope_variable_does_not_emit_label_use()
    {
        // A regular Record variable in scope should NOT emit label_use
        // on its bare uses. The handler must check the type keyword
        // matches "Label" before emitting.
        const string src = """
            codeunit 50100 "Foo"
            {
                procedure DoStuff()
                var
                    Cust: Record Customer;
                begin
                    if Cust.IsEmpty() then exit;
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Where(r => r.ReferenceKind == "label_use")
            .Should().BeEmpty();
    }

    [Fact]
    public void Multiple_label_uses_in_same_procedure_emit_one_row_each()
    {
        // Real BC code uses Error / Message / Confirm with different
        // label vars on different lines. Each call site should produce
        // its own label_use row so Find references shows them all.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50100, "MyHelper"));

        const string src = """
            codeunit 50100 "Foo"
            {
                var
                    ErrA: Label 'Err A';
                    ErrB: Label 'Err B';

                procedure DoStuff()
                begin
                    if true then
                        Error(ErrA);
                    if true then
                        Error(ErrB);
                    Message(ErrA);
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        var labelUses = result.References.Where(r => r.ReferenceKind == "label_use").ToList();
        labelUses.Should().HaveCount(3);
        labelUses.Count(r => r.TargetMemberName == "ErrA").Should().Be(2);
        labelUses.Count(r => r.TargetMemberName == "ErrB").Should().Be(1);
    }

    // ── Page-form field declarations ───────────────────────────────

    [Fact]
    public void Page_form_field_declaration_lets_chain_walker_pick_up_Rec_field()
    {
        // Regression: TryConsumeFieldDeclaration was written for the
        // table-form `field(<id>; <name>; <type>)` shape and consumed
        // the entire parens. On a page, `field("Sell-to Customer No.";
        // Rec."Sell-to Customer No.")` has a chain expression on the
        // RHS that needs the normal member-chain walker. The handler
        // now detects the form by peeking at the first token (Number
        // → table form, else page form) and bails out for page form.
        var resolver = MakeResolver();
        resolver.AddMember("Sales Header", new AlMember("Sell-to Customer No.", "table_field",null, null));

        const string src = """
            page 42 "Sales Order"
            {
                layout
                {
                    area(content)
                    {
                        field("Sell-to Customer No."; Rec."Sell-to Customer No.")
                        {
                        }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(resolver, "Sales Order", "Sales Header"));

        // The chain `Rec."Sell-to Customer No."` should resolve as a
        // field_access on Sales Header — was being swallowed by the
        // field-declaration handler before this fix.
        result.References.Should().Contain(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "Sell-to Customer No."
            && r.ReferenceKind == "field_access");
    }

    [Fact]
    public void Page_form_field_declaration_picks_up_chain_method_calls_on_RHS()
    {
        // The page-field binding RHS can be a method call too, not just
        // a field access. `field("Editable Lines"; Rec.SalesLinesEditable())`
        // — the method call inside the field-decl parens needs to
        // resolve on Sales Header.
        var resolver = MakeResolver();
        resolver.AddMember("Sales Header", new AlMember("SalesLinesEditable", "procedure", null, null));

        const string src = """
            page 42 "Sales Order"
            {
                layout
                {
                    area(content)
                    {
                        field("Editable Lines"; Rec.SalesLinesEditable())
                        {
                        }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(resolver, "Sales Order", "Sales Header"));

        result.References.Should().Contain(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "SalesLinesEditable"
            && r.ReferenceKind == "method_call");
    }

    [Fact]
    public void Table_form_field_declaration_still_extracts_enum_type()
    {
        // Negative-side check: the form-detection fix must not regress
        // the table-form enum-type extraction we added in tranche 1.
        var resolver = MakeResolver();
        resolver.AddType("Sales Document Type", new AlTypeRef(BaseAppId, "enum", 39, "Sales Document Type"));

        const string src = """
            table 36 "Sales Header"
            {
                fields
                {
                    field(1; "Document Type"; Enum "Sales Document Type") { }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(resolver, "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Sales Document Type"
            && r.ReferenceKind == "property_object");
    }

    // ── CalcFormula + SourceTableView (tranche 2) ──────────────────

    [Fact]
    public void Calc_formula_emits_queried_table_and_target_field()
    {
        // sum("G/L Entry".Amount where(…)) — the queried table goes
        // out as a property_object, and the .Amount field after the
        // table name goes out as a field_access on that table.
        var resolver = MakeResolver();
        resolver.AddType("G/L Entry", new AlTypeRef(BaseAppId, "table", 17, "G/L Entry"));
        resolver.AddMember("G/L Entry", new AlMember("Amount", "table_field",null, null));
        resolver.AddMember("G/L Entry", new AlMember("G/L Account No.", "table_field",null, null));

        const string src = """
            table 15 "G/L Account"
            {
                fields
                {
                    field(50; "Balance"; Decimal)
                    {
                        FieldClass = FlowField;
                        CalcFormula = sum("G/L Entry".Amount where("G/L Account No." = field("No.")));
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(resolver, "G/L Account"));

        result.References.Should().Contain(r =>
            r.TargetObjectName == "G/L Entry"
            && r.ReferenceKind == "property_object");
        result.References.Should().Contain(r =>
            r.TargetObjectName == "G/L Entry"
            && r.TargetMemberName == "Amount"
            && r.ReferenceKind == "field_access");
        // LHS of the where pair is a field on the queried table.
        result.References.Should().Contain(r =>
            r.TargetObjectName == "G/L Entry"
            && r.TargetMemberName == "G/L Account No."
            && r.ReferenceKind == "field_access");
    }

    [Fact]
    public void Calc_formula_count_emits_only_the_queried_table_and_where_fields()
    {
        // count(…) and exist(…) don't have a target field after the
        // table name. The handler must still emit the table and the
        // where-clause field refs.
        var resolver = MakeResolver();
        resolver.AddType("Cust. Ledger Entry", new AlTypeRef(BaseAppId, "table", 21, "Cust. Ledger Entry"));
        resolver.AddMember("Cust. Ledger Entry", new AlMember("Customer No.", "table_field",null, null));

        const string src = """
            table 18 Customer
            {
                fields
                {
                    field(60; "Entry Count"; Integer)
                    {
                        FieldClass = FlowField;
                        CalcFormula = count("Cust. Ledger Entry" where("Customer No." = field("No.")));
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(resolver, "Customer"));

        result.References.Should().Contain(r =>
            r.TargetObjectName == "Cust. Ledger Entry"
            && r.ReferenceKind == "property_object");
        result.References.Should().Contain(r =>
            r.TargetObjectName == "Cust. Ledger Entry"
            && r.TargetMemberName == "Customer No."
            && r.ReferenceKind == "field_access");
        // No target field after the table → no Amount-style ref.
        result.References.Where(r =>
            r.TargetObjectName == "Cust. Ledger Entry"
            && r.ReferenceKind == "field_access"
            && r.TargetMemberName != "Customer No.").Should().BeEmpty();
    }

    [Fact]
    public void Source_table_view_emits_field_refs_on_pages_source_table()
    {
        // Page's SourceTableView's where() and sorting() clauses both
        // reference fields on the SourceTable (Rec). Both LHS-of-where
        // and sorting list entries get field_access rows.
        var resolver = MakeResolver();
        resolver.AddMember("Sales Header", new AlMember("No.", "table_field",null, null));
        resolver.AddMember("Sales Header", new AlMember("Document Type", "table_field",null, null));

        const string src = """
            page 42 "Sales Order"
            {
                SourceTableView = sorting("No."), where("Document Type" = filter(Order));
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(resolver, "Sales Order", "Sales Header"));

        result.References.Should().Contain(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "Document Type"
            && r.ReferenceKind == "field_access");
        result.References.Should().Contain(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "No."
            && r.ReferenceKind == "field_access");
    }

    [Fact]
    public void Source_table_view_filter_value_does_not_leak_into_field_refs()
    {
        // `filter(Order)` on the RHS of the where pair shouldn't be
        // mistaken for a field — its argument is an enum value, not
        // a field name. The handler must only emit on the LHS of `=`.
        var resolver = MakeResolver();
        resolver.AddMember("Sales Header", new AlMember("Document Type", "table_field",null, null));

        const string src = """
            page 42 "Sales Order"
            {
                SourceTableView = where("Document Type" = filter(Order));
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(resolver, "Sales Order", "Sales Header"));

        var fieldRefs = result.References.Where(r =>
            r.ReferenceKind == "field_access"
            && r.TargetObjectName == "Sales Header").ToList();
        fieldRefs.Should().ContainSingle();
        fieldRefs[0].TargetMemberName.Should().Be("Document Type");
    }

    [Fact]
    public void Calc_formula_with_unknown_queried_table_drops_silently()
    {
        // Cross-release / typo / customer-only table not in the catalog.
        // Don't crash, don't bump unresolved, don't emit garbage refs.
        const string src = """
            table 50000 "Custom"
            {
                fields
                {
                    field(10; "Total"; Decimal)
                    {
                        FieldClass = FlowField;
                        CalcFormula = sum("Nonexistent Table".Amount where("X" = field("Y")));
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Custom"));

        result.References.Where(r =>
            r.ReferenceKind == "property_object" || r.ReferenceKind == "field_access")
            .Should().BeEmpty();
    }

    // ── EventSubscriber attribute bindings ─────────────────────────

    [Fact]
    public void Event_subscriber_attribute_with_legacy_quoted_event_name_emits_publisher_binding()
    {
        // Classic Microsoft-style EventSubscriber:
        //   [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post",
        //                    'OnAfterPostSalesDoc', '', false, false)]
        // The subscriber row's TargetMemberName carries the event name and
        // TargetMemberKind = "event_publisher" so Find references on the
        // publisher procedure surfaces this binding via the existing
        // member-scoped query.
        const string src = """
            codeunit 50100 "Sales Post Subscribers"
            {
                [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", 'OnAfterPostSalesDoc', '', false, false)]
                local procedure HandleAfterPost(var SalesHeader: Record "Sales Header"; CommitIsSuppressed: Boolean)
                begin
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "event_publisher"
            && r.TargetObjectName == "Sales-Post"
            && r.TargetObjectKind == "codeunit"
            && r.TargetMemberName == "OnAfterPostSalesDoc"
            && r.TargetMemberKind == "event_publisher");
    }

    [Fact]
    public void Event_subscriber_attribute_with_bare_identifier_event_name_also_emits_binding()
    {
        // Newer BC dropped the apostrophes around the event name (and
        // element name) — bare identifiers now lex as Identifier tokens
        // instead of String tokens. Both shapes must produce the same
        // binding so subscribers in modern code aren't missed.
        const string src = """
            codeunit 50101 "Sales Post Subscribers"
            {
                [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", OnAfterPostSalesDoc, '', false, false)]
                local procedure HandleAfterPost()
                begin
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "event_publisher"
            && r.TargetObjectName == "Sales-Post"
            && r.TargetMemberName == "OnAfterPostSalesDoc");
    }

    [Fact]
    public void Event_subscriber_attribute_on_a_table_publisher_resolves()
    {
        // EventSubscriber against a Table publisher rather than a Codeunit
        // — the typed-literal in arg[1] is `Table::"Customer"` instead of
        // `Codeunit::"…"`. Catalog lookup uses the name; the kind comes
        // from whatever object the name resolves to.
        const string src = """
            codeunit 50102 "Cust Subscribers"
            {
                [EventSubscriber(ObjectType::Table, Database::Customer, 'OnAfterInsertEvent', '', false, false)]
                local procedure HandleAfterInsert()
                begin
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "event_publisher"
            && r.TargetObjectName == "Customer"
            && r.TargetObjectKind == "table"
            && r.TargetMemberName == "OnAfterInsertEvent");
    }

    [Fact]
    public void Multiple_event_subscribers_in_one_file_each_emit_a_binding()
    {
        // A subscribers codeunit often contains many subscribers. Each
        // attribute must mint its own binding row — the walker must
        // continue past the first matched attribute without losing track.
        const string src = """
            codeunit 50103 "Multi"
            {
                [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", 'OnAfterPostSalesDoc', '', false, false)]
                local procedure A() begin end;

                [EventSubscriber(ObjectType::Table, Database::Customer, 'OnAfterInsertEvent', '', false, false)]
                local procedure B() begin end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Where(r => r.ReferenceKind == "event_publisher")
            .Should().HaveCount(2);
        result.References.Where(r => r.ReferenceKind == "event_publisher")
            .Select(r => r.TargetMemberName)
            .Should().BeEquivalentTo(new[] { "OnAfterPostSalesDoc", "OnAfterInsertEvent" });
    }

    [Fact]
    public void Event_subscriber_attribute_with_unknown_target_drops_silently()
    {
        // Target codeunit isn't in the catalog (cross-release, customer
        // extension not imported, typo). Don't crash, don't bump the
        // unresolved counter — the same policy property-object refs
        // use for unknown targets.
        const string src = """
            codeunit 50104 "Unknown"
            {
                [EventSubscriber(ObjectType::Codeunit, Codeunit::"Nonexistent Cu", 'OnSomething', '', false, false)]
                local procedure A() begin end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Where(r => r.ReferenceKind == "event_publisher")
            .Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Other_attributes_alongside_event_subscriber_do_not_emit_bindings()
    {
        // [Test] / [HandlerFunctions(...)] / [Scope('OnPrem')] etc. must
        // not produce event_publisher rows — only [EventSubscriber] does.
        // The narrow `[ EventSubscriber (` detector ensures attributes
        // with other names skip the binding path.
        const string src = """
            codeunit 50105 "Test Cu"
            {
                [Test]
                [HandlerFunctions('SomeHandler')]
                procedure TestSomething() begin end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Where(r => r.ReferenceKind == "event_publisher")
            .Should().BeEmpty();
    }

    // ── Object-scope property extraction ───────────────────────────

    [Fact]
    public void Source_table_property_emits_object_reference()
    {
        // `SourceTable = "Sales Header";` at the top of a page is a
        // reference to the Sales Header table. The extractor must emit
        // a property_object row at the line/column of the name token
        // so the source viewer can underline and Go-to-definition can
        // resolve via the object-name fallback.
        const string src = """
            page 42 "Sales Order"
            {
                SourceTable = "Sales Header";
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(MakeResolver(), "Sales Order", null));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetObjectKind == "table"
            && r.TargetMemberName == null
            && r.ReferenceKind == "property_object");
    }

    [Fact]
    public void Lookup_page_id_property_emits_page_reference()
    {
        const string src = """
            table 18 Customer
            {
                LookupPageID = "Customer List";
            }
            """;
        var resolver = MakeResolver();
        resolver.AddType("Customer List", new AlTypeRef(BaseAppId, "page", 22, "Customer List"));

        var result = AlReferenceExtractor.Extract(src, OwnerTable(resolver, "Customer"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer List"
            && r.TargetObjectKind == "page"
            && r.ReferenceKind == "property_object");
    }

    [Fact]
    public void Data_caption_fields_emits_one_field_access_per_listed_field()
    {
        // DataCaptionFields = "No.", "Name"; lists two fields on the
        // owner table — each gets its own field_access row so the
        // source viewer underlines each independently and Go-to-
        // definition jumps to the field declaration on the table.
        const string src = """
            table 18 Customer
            {
                DataCaptionFields = "No.", "Name";
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Customer"));

        result.References.Should().HaveCount(2);
        result.References.Select(r => r.TargetMemberName)
            .Should().BeEquivalentTo(new[] { "No.", "Name" });
        result.References.Should().AllSatisfy(r =>
        {
            r.TargetObjectName.Should().Be("Customer");
            r.ReferenceKind.Should().Be("field_access");
        });
    }

    [Fact]
    public void Data_caption_fields_on_a_page_targets_the_source_table()
    {
        // A page's DataCaptionFields refers to fields on its SourceTable,
        // not the page itself. Uses the same SourceTable-derived Rec
        // type the body's implicit-field-access path uses.
        const string src = """
            page 42 "Sales Order"
            {
                DataCaptionFields = "Sell-to Customer No.";
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(MakeResolver(), "Sales Order", "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "Sell-to Customer No.");
    }

    [Fact]
    public void Property_extraction_does_not_fire_inside_a_procedure_body()
    {
        // `if x = 5 then` inside a procedure body uses the same `=`
        // punct as a property assignment, but it's not a property —
        // the scope-depth guard ensures property detection only fires
        // at object scope. Without the guard this would mis-parse
        // comparisons as property declarations.
        var resolver = MakeResolver();
        resolver.AddType("SourceTable", new AlTypeRef(BaseAppId, "table", 99, "SourceTable"));

        const string src = """
            codeunit 50100 "Foo"
            {
                procedure Foo()
                var
                    SourceTable: Integer;
                begin
                    if SourceTable = 5 then exit;
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        // No reference should leak out — neither the comparison nor any
        // body-token gets mis-recognised as a SourceTable property
        // referring to the table named "SourceTable" in our fixture.
        result.References.Should().BeEmpty(
            because: "comparisons inside procedure bodies are not property declarations");
    }

    [Fact]
    public void Property_with_unknown_object_name_drops_silently()
    {
        // `SourceTable = "Some Unknown Table";` referencing a table
        // we don't have in the catalog must not throw and must not
        // bump the unresolved counter — silent drop. Unknown targets
        // in property values are common (cross-release, customer
        // extensions); they don't deserve the noise.
        const string src = """
            page 42 "X"
            {
                SourceTable = "Some Unknown Table";
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(MakeResolver(), "X", null));

        result.References.Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    // ── Implicit-Rec field access (gap #5 sibling) ─────────────────

    [Fact]
    public void Bare_quoted_identifier_inside_table_trigger_resolves_to_field_on_owner()
    {
        // `"No." := 'X'` inside a table trigger is shorthand for
        // `Rec."No." := 'X'`. The extractor must emit a field_access
        // reference on the owner table even without the Rec qualifier.
        const string src = """
            trigger OnInsert()
            begin
                "No." := 'C001';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Customer"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "No."
            && r.ReferenceKind == "field_access");
    }

    [Fact]
    public void Bare_quoted_identifier_inside_page_trigger_resolves_to_field_on_source_table()
    {
        // On a page with SourceTable = "Sales Header", a bare `"Sell-to
        // Customer No."` references that field on Sales Header.
        const string src = """
            trigger OnAfterGetCurrRecord()
            begin
                if "Sell-to Customer No." = '' then exit;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(MakeResolver(), "Sales Order", "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "Sell-to Customer No.");
    }

    [Fact]
    public void Bare_unquoted_identifier_matching_owner_field_resolves()
    {
        // Field names that don't need quotes (no spaces) still trigger
        // implicit-field-access. Customer has a "Name" field in the
        // fixture; bare `Name` inside a table body resolves to it.
        const string src = """
            trigger OnInsert()
            begin
                if Name = '' then
                    Name := 'Default';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Customer"));

        // Two reads / writes of Name → two field_access emits.
        result.References.Should().HaveCount(2);
        result.References.Should().AllSatisfy(r =>
        {
            r.TargetObjectName.Should().Be("Customer");
            r.TargetMemberName.Should().Be("Name");
            r.ReferenceKind.Should().Be("field_access");
        });
    }

    [Fact]
    public void Local_variable_shadows_implicit_field_with_same_name()
    {
        // Customer has a field "Name". A local var named `Name` shadows
        // the field — bare `Name` inside the body references the local,
        // not the field. The extractor must not emit a field_access.
        const string src = """
            procedure Foo()
            var
                Name: Text;
            begin
                Name := 'shadowed';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Customer"));

        result.References.Should().BeEmpty(
            because: "the local variable Name shadows the field of the same name");
    }

    [Fact]
    public void Implicit_field_access_skips_statement_keywords_and_literals()
    {
        // `if`, `then`, `not`, `true`, `false`, `exit` etc. are bare
        // identifiers that look like potential field accesses. The
        // keyword filter must drop them before the field lookup so
        // they don't pollute the resolved counter or emit refs.
        const string src = """
            trigger OnInsert()
            begin
                if true then
                    exit;
                if not Confirm('Sure?', true) then
                    exit;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Customer"));

        result.References.Should().BeEmpty();
    }

    [Fact]
    public void Implicit_field_access_does_not_fire_outside_a_record_owner()
    {
        // Codeunits don't have Rec — implicit-field-access shouldn't
        // even try. A bare quoted identifier inside a codeunit body
        // is most likely a label / string / enum value, none of which
        // we should treat as field accesses.
        const string src = """
            procedure Foo()
            begin
                "Some Token";
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().BeEmpty();
    }

    [Fact]
    public void Nested_begin_end_blocks_preserve_procedure_scope()
    {
        // Pre-fix bug: any `end;` inside a procedure body popped the
        // scope frame, so local variables declared in the procedure
        // were "out of scope" after the first nested end. This test
        // exercises a deeply nested begin/end and verifies the local
        // `Cust` parameter is still resolvable on the inner `Cust.X()`
        // — which only works if scope wasn't popped prematurely.
        const string src = """
            procedure Foo(Cust: Record Customer)
            begin
                if true then begin
                    if true then begin
                        Cust.Insert();
                    end;
                end;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "Insert");
    }

    [Fact]
    public void Case_blocks_count_toward_block_depth_alongside_begin()
    {
        // `case … end;` opens a block closed by `end` without a paired
        // `begin`. The depth tracker must count `case` as an opener too,
        // otherwise the case's `end` pops the procedure scope.
        const string src = """
            procedure Foo(Cust: Record Customer)
            begin
                case Cust.Insert() of
                    true: exit;
                    false: exit;
                end;
                Cust.Validate();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        // Both Cust.Insert() and Cust.Validate() must emit — the second
        // one only does if the procedure scope survived the case block.
        // Plus a property_object on the `Record Customer` parameter type.
        result.References.Should().HaveCount(3);
        result.References.Where(r => r.ReferenceKind != "property_object")
            .Select(r => r.TargetMemberName)
            .Should().BeEquivalentTo(new[] { "Insert", "Validate" });
        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "property_object" && r.TargetObjectName == "Customer");
    }

    // ── Method-call arguments emit their own references ───────────

    [Fact]
    public void Args_of_a_resolved_method_call_emit_their_own_references()
    {
        // Rec.Validate("Sell-to Customer No.", Customer."No.") was
        // dropping both arg references because WalkMemberChain used
        // SkipBalancedParens after emitting the call. Both args
        // should now emit field_access refs in their own right —
        // "Sell-to Customer No." via implicit-Rec on Sales Header,
        // and Customer."No." via the chain walker on the local var.
        var resolver = MakeResolver();
        resolver.AddMember("Sales Header", new AlMember("Sell-to Customer No.", "table_field",null, null));
        resolver.AddMember("Sales Header", new AlMember("Validate", "procedure", null, null));

        const string src = """
            page 42 "Sales Order"
            {
                layout
                {
                    area(content)
                    {
                        field("Sell-to Customer No."; Rec."Sell-to Customer No.")
                        {
                            trigger OnAfterLookup(Selected: RecordRef)
                            var
                                Customer: Record Customer;
                            begin
                                Rec.Validate("Sell-to Customer No.", Customer."No.");
                            end;
                        }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(resolver, "Sales Order", "Sales Header"));

        // The user procedure Rec.Validate emits a method_call ref;
        // the args inside must also emit their own field accesses
        // rather than being swallowed by the old SkipBalancedParens.
        result.References.Should().Contain(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "Sell-to Customer No."
            && r.ReferenceKind == "field_access");
        result.References.Should().Contain(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "No."
            && r.ReferenceKind == "field_access");
    }

    [Fact]
    public void Args_of_a_builtin_method_call_emit_their_own_references()
    {
        // Rec.SetRange(...) — SetRange is a built-in, so no ref for
        // the call itself, but the args still need to walk. Inside a
        // page with SourceTable, the bare quoted "Sell-to Customer No."
        // resolves via implicit-Rec on Sales Header; Customer."No."
        // resolves via the chain walker.
        var resolver = MakeResolver();
        resolver.AddMember("Sales Header", new AlMember("Sell-to Customer No.", "table_field",null, null));

        const string src = """
            page 42 "Sales Order"
            {
                layout
                {
                    area(content)
                    {
                        field(X; Rec."Sell-to Customer No.")
                        {
                            trigger OnValidate()
                            var
                                Customer: Record Customer;
                            begin
                                Rec.SetRange("Sell-to Customer No.", Customer."No.");
                            end;
                        }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(resolver, "Sales Order", "Sales Header"));

        result.References.Should().Contain(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "Sell-to Customer No.");
        result.References.Should().Contain(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "No.");
    }

    // ── Tableextension Rec binds to base table ─────────────────────

    [Fact]
    public void Rec_inside_a_tableextension_resolves_to_the_extended_base_table()
    {
        // A tableextension's Rec is conceptually the base table flattened
        // with the extension's columns. Inside the extension's code,
        // Rec.<base-procedure> should resolve to the BASE table's
        // procedure, not be looked up on the extension itself.
        const string src = """
            tableextension 50100 "Cust Ext" extends Customer
            {
                procedure DoStuff()
                begin
                    Rec.Insert();
                    Rec."No." := 'X';
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src,
            OwnerTableExtension(MakeResolver(), "Cust Ext", "Customer"));

        // Insert is a built-in (skipped). "No." is a field on Customer.
        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "No."
            && r.ReferenceKind == "field_access");
    }

    [Fact]
    public void Rec_method_call_inside_tableextension_walks_base_then_extensions()
    {
        // CustomerExt is a tableextension on Customer that adds a
        // procedure DKValidate. From INSIDE another tableextension on
        // Customer ("Cust Ext SD"), calling Rec.DKValidate() should
        // resolve through the base→extensions walk and target the
        // extension that declared DKValidate (not "Cust Ext SD" itself).
        var resolver = MakeResolver();
        resolver.AddType("CustomerExt", new AlTypeRef(BaseAppId, "tableextension", 50000, "CustomerExt"));
        resolver.AddExtensionOf("Customer", "CustomerExt");
        resolver.AddMember("CustomerExt", new AlMember("DKValidate", "procedure", null, null));

        const string src = """
            tableextension 50200 "Cust Ext SD" extends Customer
            {
                procedure UseIt()
                begin
                    Rec.DKValidate();
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src,
            OwnerTableExtension(resolver, "Cust Ext SD", "Customer"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "CustomerExt"
            && r.TargetMemberName == "DKValidate"
            && r.ReferenceKind == "method_call");
    }

    // ── Codeunit with TableNo binds Rec to that table ──────────────

    [Fact]
    public void Codeunit_with_TableNo_binds_rec_to_the_named_table()
    {
        // A codeunit with `TableNo = "Gen. Journal Line"` runs as the
        // OnRun trigger receiver for that table — Rec is bound to the
        // journal line inside OnRun and any procedures the codeunit
        // calls. Without TableNo, codeunits have no implicit Rec.
        var resolver = MakeResolver();
        resolver.AddType("Gen. Journal Line", new AlTypeRef(BaseAppId, "table", 81, "Gen. Journal Line"));
        resolver.AddMember("Gen. Journal Line", new AlMember("Amount", "table_field",null, null));
        resolver.AddMember("Gen. Journal Line", new AlMember("PostLine", "procedure", null, null));

        const string src = """
            codeunit 12 "Gen. Jnl.-Post"
            {
                TableNo = "Gen. Journal Line";

                trigger OnRun()
                begin
                    Rec.PostLine();
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src,
            OwnerCodeunit(resolver, tableNo: "Gen. Journal Line"));

        // Rec.PostLine resolves through TableNo → Gen. Journal Line →
        // catalog member PostLine.
        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Gen. Journal Line"
            && r.TargetMemberName == "PostLine"
            && r.ReferenceKind == "method_call");
    }

    [Fact]
    public void Codeunit_without_TableNo_has_no_rec_so_chains_stay_unresolved()
    {
        // The default codeunit case — no TableNo, no Rec. A reference
        // to bare `Rec` should not silently bind to anything.
        const string src = """
            codeunit 50000 "Foo"
            {
                procedure Bar()
                begin
                    Rec.Insert();
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        // No implicit Rec → ResolveHeadType returns null → chain
        // drops with _unresolved counter, but no garbage reference.
        result.References.Where(r =>
            r.TargetObjectName != "MyHelper").Should().BeEmpty(
            because: "without TableNo, a codeunit has no Rec to dispatch on");
    }


    // ── Bare self-procedure calls (gap #4) ─────────────────────────

    [Fact]
    public void Bare_self_call_resolves_to_owner_member()
    {
        // `DoStuff();` with no receiver — the extractor must look up
        // DoStuff as a member on the file's owner object and emit a
        // method_call reference so Find references / Go to definition
        // can find it.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));
        resolver.AddMember("MyHelper", new AlMember("DoStuff", "procedure", null, null));

        const string src = """
            procedure Outer()
            begin
                DoStuff();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "MyHelper"
            && r.TargetObjectKind == "codeunit"
            && r.TargetMemberName == "DoStuff"
            && r.TargetMemberKind == "procedure");
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Bare_AL_system_function_does_not_emit_a_reference()
    {
        // Message / Error / Confirm look like bare calls but are AL runtime
        // functions, not user procedures. Skip silently so we don't try to
        // resolve them as self-members.
        const string src = """
            procedure Foo()
            begin
                Message('Hello world');
                Error('Bad');
                Confirm('Sure?', true);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(0,
            because: "system functions filter to silent skip — no diagnostic bump");
    }

    [Fact]
    public void Namespace_and_using_directives_at_file_top_are_skipped()
    {
        // Modern AL files open with `namespace X.Y.Z;` then a list of
        // `using A.B.C;` directives. Without explicit handling each one
        // gets walked as a member chain on an unresolved head (`Microsoft`,
        // `System`, …) and bumps the diagnostic counter. The skip happens
        // at object scope only — body code that legitimately uses the
        // word `using` (not a real AL pattern, defensive) would still
        // walk normally.
        const string src = """
            namespace Microsoft.Bank.Banking;

            using Microsoft.Foundation.Company;
            using System.Globalization;
            using Microsoft.Finance.GeneralLedger.Setup;

            codeunit 50000 "Foo"
            {
                procedure Bar()
                begin
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(0,
            because: "directives are pure annotations; nothing to walk");
        result.Stats.UnresolvedSamples.Should().BeEmpty();
    }

    [Fact]
    public void Clear_as_a_bare_call_is_treated_as_a_system_function()
    {
        // `Clear(SomeVar)` resets a variable to its default state. The
        // pre-fix behaviour emitted an unresolved sample on every Clear()
        // call because it tried to resolve Clear as a self-member.
        const string src = """
            procedure Foo()
            var
                i: Integer;
            begin
                Clear(i);
                ClearAll();
                Commit();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Page_field_control_name_is_not_emitted_as_a_Rec_field_access()
    {
        // `field("No."; Rec."No.") { ... }` — the LHS quoted identifier
        // is the page-field's DECLARATION (control name); the RHS chain
        // is the actual source-table field reference. Without the
        // DSL-keyword first-arg skip, the LHS falls through to implicit-
        // Rec field access against the page's source table, emitting a
        // bogus field_access on the page-field's own control name.
        // Outcome: only the RHS Rec."No." emits a reference; the LHS
        // `"No."` does not.
        var resolver = MakeResolver();
        resolver.AddMember("Sales Header", new AlMember("No.", "table_field",null, null));

        const string src = """
            page 50000 "Sales Order"
            {
                SourceTable = "Sales Header";
                layout
                {
                    area(content)
                    {
                        field("No."; Rec."No.") { ApplicationArea = All; }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(resolver, "Sales Order", "Sales Header"));

        // Only the RHS Rec."No." resolves. The LHS control name is skipped.
        result.References.Where(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "No.")
            .Should().HaveCount(1,
                because: "LHS is a declaration (control name), only the RHS Rec.\"No.\" should emit");
    }

    [Fact]
    public void Page_part_second_arg_resolves_to_the_referenced_page()
    {
        // `part(ControlName; "Page Name")` — the second arg is a page
        // reference we should resolve so Find references / Cmd-click
        // work the same way they do on `RunObject = Page "X"`. The
        // first arg (control name) is still skipped as a declaration.
        var resolver = MakeResolver();
        resolver.AddType("Sales Doc. Check Factbox",
            new AlTypeRef(BaseAppId, "page", 9081, "Sales Doc. Check Factbox"));

        const string src = """
            page 50000 "Sales Order"
            {
                SourceTable = "Sales Header";
                layout
                {
                    area(factboxes)
                    {
                        part(SalesDocCheckFactbox; "Sales Doc. Check Factbox")
                        {
                            ApplicationArea = All;
                        }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(resolver, "Sales Order", "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "property_object"
            && r.TargetObjectKind == "page"
            && r.TargetObjectName == "Sales Doc. Check Factbox");
        // The control name (first arg) does NOT emit any reference.
        result.References.Should().NotContain(r =>
            r.TargetMemberName == "SalesDocCheckFactbox"
            || r.TargetObjectName == "SalesDocCheckFactbox");
    }

    [Fact]
    public void Page_part_action_group_first_args_are_not_emitted_as_references()
    {
        // `part(ControlName; PageRef)`, `action(ControlName)`,
        // `actionref(ControlName; Target)`, `group(Name)`,
        // `area(Name)` — the FIRST argument of each is a declaration
        // name or an unresolvable base-page reference. None of them are
        // navigation targets; none should emit references.
        const string src = """
            page 50000 "Sales Order"
            {
                SourceTable = "Sales Header";
                layout
                {
                    area(factboxes)
                    {
                        part(SalesDocCheckFactbox; "Sales Doc. Check Factbox")
                        {
                            ApplicationArea = All;
                        }
                    }
                }
                actions
                {
                    area(processing)
                    {
                        action(Statistics) { ApplicationArea = All; }
                        action("Co&mments") { ApplicationArea = All; }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(MakeResolver(), "Sales Order", "Sales Header"));

        // None of the control names should surface as Rec field accesses
        // or any other reference kind.
        result.References.Should().NotContain(r =>
            r.TargetMemberName == "SalesDocCheckFactbox"
            || r.TargetMemberName == "Statistics"
            || r.TargetMemberName == "Co&mments"
            || r.TargetMemberName == "factboxes"
            || r.TargetMemberName == "processing");
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Pageextension_modify_addafter_target_names_are_not_emitted()
    {
        // `modify(TargetName) { ... }` and `addafter(TargetName) { ... }`
        // — TargetName references a control in the base page, which we
        // can't resolve. The first-arg skip silences these so they
        // don't surface as unresolved variables or bogus Rec field
        // accesses.
        const string src = """
            pageextension 50000 "Sales Order Ext" extends "Sales Order"
            {
                layout
                {
                    modify("No.") { Caption = 'Custom No.'; }
                    addafter("Sell-to Customer No.")
                    {
                        field(MyNewField; Rec.MyNewField) { ApplicationArea = All; }
                    }
                }
            }
            """;
        var resolver = MakeResolver();
        resolver.AddType("Sales Order", new AlTypeRef(BaseAppId, "page", 42, "Sales Order"));
        // The pageextension's Rec.MyNewField needs MyNewField on the
        // extended page's source table or the chain step bumps the
        // unresolved counter. Add it so we can assert the dispatch
        // ITSELF doesn't bump.
        resolver.AddMember("Sales Header", new AlMember("MyNewField", "table_field",null, null));
        var result = AlReferenceExtractor.Extract(src,
            OwnerPageExtension(resolver, "Sales Order Ext", "Sales Header"));

        // No emission for the modify / addafter target names. The
        // field inside addafter still walks normally for its RHS.
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Page_declarative_DSL_keywords_do_not_count_as_bare_calls()
    {
        // `area(content) { group(General) { field("X"; Rec."Y") {} } }` —
        // the layout / actions / field block inside a page is
        // declarative, not procedural. Without explicit handling each
        // keyword surfaces as a bare-call unresolved.
        const string src = """
            page 50000 "Sales Order"
            {
                SourceTable = "Sales Header";
                layout
                {
                    area(content)
                    {
                        group(General)
                        {
                            field("No."; Rec."No.")
                            {
                                ApplicationArea = All;
                            }
                            field("Sell-to Customer No."; Rec."Sell-to Customer No.")
                            {
                                ApplicationArea = All;
                            }
                        }
                    }
                }
                actions
                {
                    area(processing)
                    {
                        action(Post)
                        {
                            ApplicationArea = All;
                        }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(MakeResolver(), "Sales Order", "Sales Header"));

        // No DSL keyword should land in the unresolved samples.
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            new[] { "area", "group", "table_field","action" }.Contains(s.Token));
    }

    [Fact]
    public void Pageextension_change_keywords_do_not_count_as_bare_calls()
    {
        // `addAfter`, `addBefore`, `addLast`, `modify` are pageextension
        // layout / action manipulators. Same skip rule as page DSL keywords.
        const string src = """
            pageextension 50000 "Customer Card Ext" extends "Customer Card"
            {
                layout
                {
                    addAfter("Name")
                    {
                        field(MyField; Rec.MyField) { }
                    }
                    modify("Name")
                    {
                        Caption = 'Customer';
                    }
                }
            }
            """;
        var resolver = MakeResolver();
        resolver.AddType("Customer Card", new AlTypeRef(BaseAppId, "page", 21, "Customer Card"));
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            new[] { "addAfter", "modify", "field" }.Contains(s.Token));
    }

    [Fact]
    public void Enum_value_keyword_does_not_count_as_a_bare_call()
    {
        // `value(N; "Label") { Caption = '…'; }` is enum value declaration.
        const string src = """
            enum 50000 "AMC Bank Status"
            {
                value(0; "Pending") { Caption = 'Pending'; }
                value(1; "Done") { Caption = 'Done'; }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "value");
    }

    [Fact]
    public void Variable_typed_as_a_known_system_type_does_not_bump_unresolved()
    {
        // `var X: Dialog; X.Open(...)` — Dialog isn't in the AL catalog,
        // it's a runtime primitive. The chain head IS in scope (the var
        // is declared) but the type doesn't resolve and never will.
        // Skip silently so the diagnostic isn't crowded with these.
        const string src = """
            procedure Foo()
            var
                Window: Dialog;
                RecRef: RecordRef;
                XmlDoc: XmlDocument;
            begin
                Window.Open('Loading...');
                RecRef.Open('Customer');
                XmlDoc.ReadFrom('<root />');
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.Stats.UnresolvedReceivers.Should().Be(0,
            because: "Dialog / RecordRef / XmlDocument are known system types");
    }

    [Fact]
    public void Codeunit_run_as_a_builtin_static_receiver_is_skipped()
    {
        // `CODEUNIT.Run(Codeunit::"Sales-Post")` — CODEUNIT here is
        // the AL runtime dispatcher, not a user variable. Same for
        // PAGE.RunModal, REPORT.RunModal, NavApp.GetCurrentModuleInfo.
        // The static-receiver branch walks the arg list through the
        // main dispatch so inner typed-literals like `CODEUNIT::"X"`
        // and inner identifiers (passed-by-reference vars) continue
        // to walk normally.
        const string src = """
            procedure Foo()
            var
                AppInfo: ModuleInfo;
            begin
                CODEUNIT.Run(CODEUNIT::"Sales-Post");
                NavApp.GetCurrentModuleInfo(AppInfo);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.Stats.UnresolvedReceivers.Should().Be(0,
            because: "CODEUNIT/NavApp are built-in static receivers; AppInfo's ModuleInfo type is a known system type");
    }

    [Fact]
    public void ProductName_and_SYSTEM_are_builtin_static_receivers()
    {
        // `PRODUCTNAME.Full()` / `ProductName.Short()` retrieves the
        // configured app name (BC navsettings.json); `SYSTEM.Clear(X)`
        // is the disambiguating prefix for the bare-callable `Clear`
        // when a user procedure of the same name is in scope. Both
        // heads are AL runtime, not user variables.
        const string src = """
            procedure Foo(var Key: Variant)
            var
                FullName: Text;
            begin
                FullName := PRODUCTNAME.Full();
                FullName := ProductName.Short();
                SYSTEM.Clear(Key);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.Stats.UnresolvedReceivers.Should().Be(0,
            because: "PRODUCTNAME and SYSTEM are built-in static receivers");
    }

    [Fact]
    public void Enum_typed_variable_chains_through_builtin_methods_silently()
    {
        // `FeatureToUpdate.Names` / `.Ordinals` / `.AsInteger()` /
        // `.FromInteger(N)` are runtime methods on every enum value;
        // they don't appear in the catalog. The List returned by
        // Names/Ordinals threads into `.Contains`, `.IndexOf`, `.Get`
        // — collection built-ins that terminate the chain quietly.
        var resolver = MakeResolver();
        resolver.AddType("Feature To Update", new AlTypeRef(BaseAppId, "enum", 50100, "Feature To Update"));
        const string src = """
            procedure FeatureKeyMatches(FeatureToUpdate: Enum "Feature To Update"; FeatureKey: Text): Boolean
            begin
                if FeatureToUpdate.Names.Contains(FeatureKey) then
                    exit(FeatureToUpdate.AsInteger() =
                        FeatureToUpdate.Ordinals.Get(FeatureToUpdate.Names.IndexOf(FeatureKey)));
                exit(false);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedReceivers.Should().Be(0,
            because: "Names / Ordinals / AsInteger are enum built-ins");
    }

    [Fact]
    public void Query_column_access_resolves_when_the_column_is_a_catalog_member()
    {
        // `MyQuery.Sum_Remaining_Amt_LCY` on a query-typed receiver
        // resolves once query_column rows are catalogued — they're
        // declared inside the query's `column(Name; Source)` shape
        // and persisted by the source extractor.
        var resolver = MakeResolver();
        var queryRef = new AlTypeRef(BaseAppId, "query", 21, "Cust. Ledg. Entry Remain. Amt.");
        resolver.AddType("Cust. Ledg. Entry Remain. Amt.", queryRef);
        resolver.AddMember(queryRef.Name, new AlMember(
            Name: "Sum_Remaining_Amt_LCY",
            Kind: "query_column",
            ReturnTypeKeyword: null,
            ReturnTypeName: null));
        const string src = """
            procedure ReadTotal(): Decimal
            var
                CustLedgEntryRemainAmt: Query "Cust. Ledg. Entry Remain. Amt.";
                TotalAmount: Decimal;
            begin
                CustLedgEntryRemainAmt.Open();
                if CustLedgEntryRemainAmt.Read() then
                    TotalAmount := CustLedgEntryRemainAmt.Sum_Remaining_Amt_LCY;
                exit(TotalAmount);
            end;
            """;

        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedReceivers.Should().Be(0,
            because: "Open / Read are query built-ins; Sum_Remaining_Amt_LCY is a catalogued query_column");
        result.References.Should().Contain(r =>
            r.TargetObjectKind == "query"
            && r.TargetObjectName == "Cust. Ledg. Entry Remain. Amt."
            && r.TargetMemberName == "Sum_Remaining_Amt_LCY");
    }

    [Fact]
    public void A_static_dispatch_is_not_recorded_on_an_object_of_another_kind()
    {
        // The type lookup falls back to any kind, so a Page:: literal whose
        // page is not visible can find the table of the same name instead.
        var resolver = MakeResolver();
        resolver.AddType("Company Information", new AlTypeRef(BaseAppId, "table", 79, "Company Information"));
        const string src = """
            procedure Foo()
            begin
                Page.RunModal(Page::"Company Information");
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.SystemReferences.Should().BeEmpty();
    }

    [Fact]
    public void A_static_dispatch_is_a_built_in_call_on_the_object_it_names()
    {
        // Page.RunModal(Page::"Customer Card") opens the page as surely as
        // CustomerCard.RunModal() does, so "Find built-in calls" on the page
        // has to find both. The literal is still a reference of its own.
        var resolver = MakeResolver();
        resolver.AddType("Customer Card", new AlTypeRef(BaseAppId, "page", 21, "Customer Card"));
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Page.RunModal(Page::"Customer Card", Cust);
                Codeunit.Run(Codeunit::"Sales-Post");
                Page.Run(0, Cust);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.SystemReferences.Should().HaveCount(2);
        result.SystemReferences.Should().ContainSingle(r =>
            r.TargetObjectKind == "page" && r.TargetObjectId == 21 && r.SystemMethodName == "RunModal"
            && r.Line == 5 && r.Column == 10);
        result.SystemReferences.Should().ContainSingle(r =>
            r.TargetObjectKind == "codeunit" && r.TargetObjectId == 80 && r.SystemMethodName == "Run");
        result.References.Should().Contain(r =>
            r.ReferenceKind == "property_object" && r.TargetObjectName == "Customer Card");
    }

    [Fact]
    public void Record_DeleteAll_is_a_builtin_not_unresolved()
    {
        // `Customer.DeleteAll();` — DeleteAll is a Record built-in.
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.DeleteAll();
                Cust.ModifyAll("Name", 'X');
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        // Neither call is a reference in its own right. ModifyAll's first
        // argument names the field it sets, so that field is one: "where is
        // this field set?" has to find it.
        result.References.Where(r => r.ReferenceKind != "property_object")
            .Should().ContainSingle(because: "DeleteAll and ModifyAll are Record built-ins")
            .Which.Should().Match<ExtractedReference>(r =>
                r.ReferenceKind == "field_access" && r.TargetMemberName == "Name" && r.Line == 6);
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Statement_keyword_before_paren_does_not_trigger_bare_call_resolution()
    {
        // `if (X) then` and `not (X)` syntactically place a keyword before
        // `(`. The bare-call detector must skip these so it doesn't try
        // to resolve `if` or `not` as procedures.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));
        resolver.AddMember("MyHelper", new AlMember("DoStuff", "procedure", null, null));

        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                if (Cust.Insert()) then
                    DoStuff();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        // Two member calls plus a property_object ref on `Record Customer`.
        result.References.Should().HaveCount(3);
        result.References.Should().Contain(r =>
            r.TargetMemberName == "Insert" && r.TargetObjectName == "Customer");
        result.References.Should().Contain(r =>
            r.TargetMemberName == "DoStuff" && r.TargetObjectName == "MyHelper");
        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "property_object" && r.TargetObjectName == "Customer");
    }

    [Fact]
    public void Chain_on_scalar_text_variable_does_not_bump_unresolved()
    {
        // `procedure RemoveShortWords(Text: Text[250]): Text[250]; var
        //  Result: Text[250]; begin Result := CopyStr(Result.TrimEnd(), ...)`
        // — both the parameter and the local are in scope, but their
        // declared type "Text" is a language primitive that doesn't
        // resolve through the AL catalog. Without Text in
        // KnownSystemTypes every such chain bumps head-var-type-unresolved
        // — extremely common in BC's helper procedures.
        const string src = """
            procedure RemoveShortWords(Text: Text[250]): Text[250];
            var
                Result: Text[250];
            begin
                Result := CopyStr(Result.TrimEnd(), 1, MaxStrLen(Result));
                Text := Result;
                exit(Text.Split(' '));
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.Stats.UnresolvedReceivers.Should().Be(0,
            because: "Text scalar types are KnownSystemTypes; chains through them silence");
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "Result" || s.Token == "Text");
    }

    [Fact]
    public void Bare_call_of_a_record_method_name_silences_even_without_Rec()
    {
        // Catches mis-parsed chain calls (`SomeRec.INIT()` losing its
        // head and surfacing as bare `INIT()`) and implicit-Rec shapes
        // the explicit-Rec check doesn't cover. Trade-off: a real user
        // procedure named after a Record built-in would also silence.
        const string src = """
            procedure DoWork()
            begin
                INIT();
                Insert();
                SetCurrentKey('No.');
                AsInteger();
                Trim();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Bare_self_call_inside_a_system_function_argument_still_resolves()
    {
        // The system-function filter must NOT consume the argument list —
        // a `Message('x=%1', GetCount())` call needs the inner GetCount()
        // to be picked up by the main loop as its own bare self-call.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));
        resolver.AddMember("MyHelper", new AlMember("DoStuff", "procedure", null, null));
        resolver.AddMember("MyHelper", new AlMember("GetCount", "procedure", null, null));

        const string src = """
            procedure Foo()
            begin
                Message('count=%1', GetCount());
                DoStuff();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().HaveCount(2);
        result.References.Select(r => r.TargetMemberName)
            .Should().BeEquivalentTo(new[] { "GetCount", "DoStuff" });
    }

    [Fact]
    public void Bare_call_with_variable_in_scope_is_skipped()
    {
        // A parameter or local named `Cust` shadows any same-named
        // self-member — bare `Cust(...)` isn't even valid AL, but the
        // extractor must not silently emit a self-call when the name is
        // a known variable. (The shadow rule also matches how AL itself
        // resolves the identifier.)
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));
        resolver.AddMember("MyHelper", new AlMember("Cust", "procedure", null, null));

        const string src = """
            procedure Foo(Cust: Record Customer)
            begin
                Cust.Insert();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        // The chain through the parameter Cust is what gets emitted; the
        // same-named self procedure must not. Plus a property_object on
        // the `Record Customer` parameter type.
        result.References.Should().HaveCount(2);
        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "Insert");
        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "property_object" && r.TargetObjectName == "Customer");
    }

    [Fact]
    public void Bare_call_to_unknown_name_is_dropped_and_counted_as_unresolved()
    {
        // A bare call we can't match against any of (system function,
        // in-scope variable, owner-type member) is a real unresolved.
        // No reference row, but the counter ticks so operators can size
        // the gap.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));

        const string src = """
            procedure Foo()
            begin
                Mystery();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(1);
    }

    // ── Procedure-body scope tracking (#181) ────────────────────────

    [Fact]
    public void Extract_emits_symbol_scope_with_start_and_end_lines_for_procedure_body()
    {
        // The walker now captures the (start, end) span of each
        // procedure / trigger body. Verify a simple procedure produces
        // one scope row pointing at the declaration line and the
        // matching `end;` line. See issue #181.
        var resolver = MakeResolver();
        const string src = """
            procedure Foo()
            begin
                Customer.Insert(true);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.SymbolScopes.Should().ContainSingle();
        var scope = result.SymbolScopes[0];
        scope.Name.Should().Be("Foo");
        scope.Kind.Should().Be("procedure");
        scope.StartLine.Should().Be(1);
        scope.EndLine.Should().Be(4, because: "the matching `end;` sits on the fourth line");
        scope.EndColumn.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Extract_emits_one_symbol_scope_per_procedure_in_a_codeunit()
    {
        var resolver = MakeResolver();
        const string src = """
            procedure Foo()
            begin
                Customer.Insert(true);
            end;

            procedure Bar()
            begin
                Customer.Validate(true);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.SymbolScopes.Should().HaveCount(2);
        result.SymbolScopes.Select(s => s.Name).Should().Equal("Foo", "Bar");
        result.SymbolScopes.Should().AllSatisfy(s => s.EndLine.Should().BeGreaterThanOrEqualTo(s.StartLine));
    }

    [Fact]
    public void Extracted_references_carry_source_member_name_and_line_when_emitted_from_a_procedure_body()
    {
        // ExtractedReference now carries SourceMemberName /
        // SourceMemberKind / SourceMemberLine so ReleaseImportService
        // can resolve source_symbol_id on the persisted row. See #181.
        var resolver = MakeResolver();
        const string src = """
            procedure Foo()
            begin
                Customer.Insert(true);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        var refsFromFoo = result.References.Where(r => r.SourceMemberName == "Foo").ToList();
        refsFromFoo.Should().NotBeEmpty();
        refsFromFoo.Should().AllSatisfy(r =>
        {
            r.SourceMemberKind.Should().Be("procedure");
            r.SourceMemberLine.Should().Be(1);
        });
    }

    [Fact]
    public void Two_procedures_get_distinct_source_member_lines_for_their_calls()
    {
        // Each procedure body's references must point back at its OWN
        // declaration line, not at the previous procedure's. Catches
        // bugs in ScopeFrame's start-line capture or the pop ordering
        // inside TryHandleBlockDepth.
        var resolver = MakeResolver();
        const string src = """
            procedure Foo()
            begin
                Customer.Insert(true);
            end;

            procedure Bar()
            begin
                Customer.Validate(true);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        var fooLine = result.SymbolScopes.Single(s => s.Name == "Foo").StartLine;
        var barLine = result.SymbolScopes.Single(s => s.Name == "Bar").StartLine;
        fooLine.Should().BeLessThan(barLine);

        result.References.Where(r => r.SourceMemberName == "Foo")
            .Should().AllSatisfy(r => r.SourceMemberLine.Should().Be(fooLine));
        result.References.Where(r => r.SourceMemberName == "Bar")
            .Should().AllSatisfy(r => r.SourceMemberLine.Should().Be(barLine));
    }

    [Fact]
    public void Trigger_scope_emits_a_symbol_scope_with_kind_trigger()
    {
        var resolver = MakeResolver();
        // OnRun on a codeunit is a trigger, not a procedure.
        const string src = """
            trigger OnRun()
            begin
                Customer.Insert(true);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.SymbolScopes.Should().ContainSingle();
        result.SymbolScopes[0].Kind.Should().Be("trigger");
        result.SymbolScopes[0].Name.Should().Be("OnRun");
    }

    // ── Built-in receivers the catalog never tracks ─────────────────

    [Fact]
    public void Bare_File_typed_variable_does_not_resolve_to_the_File_table()
    {
        // `var ExportFile: File;` names the AL runtime File type, not the
        // System "File" table — AL requires `Record File` for the table.
        // The chain `ExportFile.WriteMode := true; ExportFile.Create(...)`
        // must silence, not fire chain-step against the colliding table.
        var resolver = MakeResolver();
        resolver.AddType("File", new AlTypeRef(BaseAppId, "table", 2000000022, "File"));

        const string src = """
            procedure Export()
            var
                ExportFile: File;
            begin
                ExportFile.WriteMode := true;
                ExportFile.TextMode := true;
                ExportFile.Create('c:\temp\x.txt');
                ExportFile.Close();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedReceivers.Should().Be(0);
        result.References.Should().NotContain(r => r.TargetObjectName == "File");
    }

    [Fact]
    public void Three_part_enum_value_literal_silences_the_value_tail()
    {
        // `Enum::"Sales Document Type"::Quote.AsInteger()` — the enum type
        // resolves (and emits a property_object ref), the `::Quote` value is
        // consumed without emitting, and `.AsInteger()` walks as an enum
        // built-in. Before the fix the value surfaced as a stray chain head
        // and fired head-not-a-variable.
        var resolver = MakeResolver();
        resolver.AddType("Sales Document Type", new AlTypeRef(BaseAppId, "enum", 39, "Sales Document Type"));

        const string src = """
            procedure Foo()
            var
                I: Integer;
            begin
                I := Enum::"Sales Document Type"::Quote.AsInteger();
                I := Enum::"Sales Document Type"::"Blanket Order".AsInteger();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedReceivers.Should().Be(0);
        result.References.Should().Contain(r =>
            r.ReferenceKind == "property_object"
            && r.TargetObjectKind == "enum"
            && r.TargetObjectName == "Sales Document Type");
    }

    [Fact]
    public void Record_link_and_consistency_methods_are_builtins()
    {
        // HasLinks / DeleteLinks / Consistent are AL-runtime Record methods,
        // never in the catalog. A chain through them must not fire chain-step.
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                if Cust.HasLinks() then
                    Cust.DeleteLinks();
                Cust.Consistent(true);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Builtin_type_static_factory_receivers_silence()
    {
        // `Version.Create(...)` / `ErrorInfo.Create(...)` /
        // `COMPANYPROPERTY.DisplayName()` use a built-in type as a static
        // receiver — not a variable, not a catalog object. Must not fire
        // head-not-a-variable.
        const string src = """
            procedure Foo()
            var
                T: Text;
            begin
                if Version.Create('1.0.0.0') = Version.Create('2.0.0.0') then
                    T := COMPANYPROPERTY.DisplayName();
                Error(ErrorInfo.Create('boom'));
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    // ── Implicit-Rec bare calls to base / source-table procedures ───

    [Fact]
    public void Bare_call_in_tableextension_resolves_to_base_table_procedure()
    {
        // `BlockDynamicTracking(true);` bare in a tableextension is
        // `Rec.BlockDynamicTracking(true)` — the procedure lives on the
        // base table, not the extension. Must emit a method_call at the
        // base table, not fire bare-call unresolved.
        var resolver = MakeResolver();
        resolver.AddType("Requisition Line", new AlTypeRef(BaseAppId, "table", 246, "Requisition Line"));
        resolver.AddType("Asm. Requisition Line",
            new AlTypeRef(BaseAppId, "tableextension", 906, "Asm. Requisition Line"));
        resolver.AddMember("Requisition Line", new AlMember("BlockDynamicTracking", "procedure", null, null));

        const string src = """
            procedure SetReplenishment()
            begin
                BlockDynamicTracking(true);
            end;
            """;
        var ctx = OwnerTableExtension(resolver, "Asm. Requisition Line", baseTable: "Requisition Line");
        var result = AlReferenceExtractor.Extract(src, ctx);

        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Requisition Line"
            && r.TargetObjectKind == "table"
            && r.TargetMemberName == "BlockDynamicTracking");
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Bare_call_on_page_resolves_to_source_table_procedure()
    {
        // `SavePassword(Pwd);` bare on a page is `Rec.SavePassword(Pwd)`
        // — the procedure is on the page's source table. Emit at the
        // table, not bare-call unresolved.
        var resolver = MakeResolver();
        resolver.AddType("Setup Card", new AlTypeRef(BaseAppId, "page", 9000, "Setup Card"));
        resolver.AddType("Setup Table", new AlTypeRef(BaseAppId, "table", 9001, "Setup Table"));
        resolver.AddMember("Setup Table", new AlMember("SavePassword", "procedure", null, null));

        const string src = """
            procedure DoSave()
            begin
                SavePassword('secret');
            end;
            """;
        var ctx = OwnerPage(resolver, "Setup Card", sourceTable: "Setup Table");
        var result = AlReferenceExtractor.Extract(src, ctx);

        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Setup Table"
            && r.TargetMemberName == "SavePassword");
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Bare_call_unknown_on_both_owner_and_rec_stays_unresolved()
    {
        // Guard: the implicit-Rec fallback must not silence a genuinely
        // unknown bare call — when neither the page nor its source table
        // declares the name, it's still bare-call unresolved.
        var resolver = MakeResolver();
        resolver.AddType("Setup Card", new AlTypeRef(BaseAppId, "page", 9000, "Setup Card"));
        resolver.AddType("Setup Table", new AlTypeRef(BaseAppId, "table", 9001, "Setup Table"));

        const string src = """
            procedure DoSave()
            begin
                TotallyUnknownProc('x');
            end;
            """;
        var ctx = OwnerPage(resolver, "Setup Card", sourceTable: "Setup Table");
        var result = AlReferenceExtractor.Extract(src, ctx);

        result.Stats.UnresolvedReceivers.Should().Be(1);
    }

    [Fact]
    public void Bare_call_in_pageextension_resolves_to_base_page_procedure()
    {
        // `NoOfRecords(TableID)` bare inside the `Navigate Ext.` pageextension
        // is a call to a procedure declared on the base page `Navigate` —
        // distinct from Rec (which is the page's source TABLE). The
        // extension → base-object fallback resolves via OwnerExtendsName.
        var resolver = MakeResolver();
        resolver.AddType("Navigate", new AlTypeRef(BaseAppId, "page", 344, "Navigate"));
        resolver.AddType("Navigate Ext.", new AlTypeRef(BaseAppId, "pageextension", 1708, "Navigate Ext."));
        resolver.AddType("Document Entry", new AlTypeRef(BaseAppId, "table", 372, "Document Entry"));
        resolver.AddMember("Navigate", new AlMember("NoOfRecords", "procedure", "Integer", null));

        const string src = """
            procedure GetNoOfRecords(TableID: Integer): Integer
            begin
                exit(NoOfRecords(TableID));
            end;
            """;
        // Navigate Ext. extends Navigate (a page); its Rec is the base
        // page's source table "Document Entry".
        var ctx = new AlExtractContext(
            OwnerKind: "pageextension",
            OwnerName: "Navigate Ext.",
            OwnerObjectId: 1708,
            OwnerAppId: BaseAppId,
            GlobalVars: new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: resolver,
            OwnerSourceTableName: "Document Entry",
            OwnerExtendsName: "Navigate");
        var result = AlReferenceExtractor.Extract(src, ctx);

        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Navigate"
            && r.TargetObjectKind == "page"
            && r.TargetMemberName == "NoOfRecords");
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    // ── with X do begin … end ───────────────────────────────────────

    [Fact]
    public void With_block_rebinds_Rec_so_bare_calls_resolve_on_the_target_record()
    {
        // `with PaymentExportData do begin SetCustomerAsRecipient(...); end;` —
        // bare identifiers inside resolve as procedures on PaymentExportData's
        // record type, not the owner codeunit. AL deprecated `with` but
        // Microsoft's banking modules still ship it.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50100, "MyHelper"));
        resolver.AddType("Payment Export Data",
            new AlTypeRef(BaseAppId, "table", 1226, "Payment Export Data"));
        resolver.AddMember("Payment Export Data",
            new AlMember("SetCustomerAsRecipient", "procedure", null, null));

        const string src = """
            procedure FillExportBuffer()
            var
                PaymentExportData: Record "Payment Export Data";
            begin
                with PaymentExportData do begin
                    SetCustomerAsRecipient();
                end;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Payment Export Data"
            && r.TargetMemberName == "SetCustomerAsRecipient");
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void With_block_pops_at_matching_end_so_outer_Rec_returns()
    {
        // Once the with-block closes, the next bare call must NOT keep
        // resolving against the with-record. Here `Insert(true)` after
        // the with's `end;` is shorthand for `Rec.Insert()` on the
        // codeunit's TableNo-bound table, not on Payment Export Data.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50100, "MyHelper"));
        resolver.AddType("Payment Export Data",
            new AlTypeRef(BaseAppId, "table", 1226, "Payment Export Data"));
        resolver.AddType("Data Exch.", new AlTypeRef(BaseAppId, "table", 1220, "Data Exch."));
        resolver.AddMember("Payment Export Data",
            new AlMember("SetCustomerAsRecipient", "procedure", null, null));

        const string src = """
            procedure FillExportBuffer()
            var
                PaymentExportData: Record "Payment Export Data";
            begin
                with PaymentExportData do begin
                    SetCustomerAsRecipient();
                end;
                Insert(true);
            end;
            """;
        var ctx = OwnerCodeunit(resolver, tableNo: "Data Exch.");
        var result = AlReferenceExtractor.Extract(src, ctx);

        // The with-block's bare call resolves on Payment Export Data.
        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Payment Export Data"
            && r.TargetMemberName == "SetCustomerAsRecipient");
        // Insert is a Record built-in → silent skip (no emit, no unresolved).
        // The key assertion: no phantom "Insert" attributed to Payment Export
        // Data after the with-block closed, and no extra unresolveds.
        result.Stats.UnresolvedReceivers.Should().Be(0);
        result.References.Should().NotContain(r =>
            r.TargetMemberName == "Insert" && r.TargetObjectName == "Payment Export Data");
    }

    // ── DotNet variable type names that collide with AL tables ───────

    [Fact]
    public void DotNet_variable_does_not_resolve_through_AL_catalog()
    {
        // `File: DotNet File;` declares a .NET System.IO.File handle.
        // The type name "File" collides with the platform virtual `File`
        // table (id 2000000022); without the DotNet bypass the resolver
        // routes the chain receiver to the AL table and `.WriteAllBytes`
        // fires as chain-step unresolved on the table receiver. Verified
        // against the unresolved sample
        //   ReceiverKind=table ReceiverName='File' Token='WriteAllBytes'
        // logged from AMCBankExpCTHndl.Codeunit.al in BC 26.5.
        var resolver = MakeResolver();
        resolver.AddType("File", new AlTypeRef(BaseAppId, "table", 2000000022, "File"));

        const string src = """
            procedure WriteBytes()
            var
                File: DotNet File;
            begin
                File.WriteAllBytes('foo.txt', 'data');
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        // No chain-step unresolved against the AL File table.
        result.Stats.UnresolvedReceivers.Should().Be(0);
        result.References.Should().NotContain(r =>
            r.TargetObjectName == "File" && r.TargetMemberName == "WriteAllBytes");
    }

    // ── Object-scope attribute skipping ───────────────────────────────

    [Fact]
    public void CommitBehavior_attribute_does_not_fire_bare_call_unresolved()
    {
        // `[CommitBehavior(CommitBehavior::Ignore)]` placed above a
        // procedure declaration is a method attribute, not a callable.
        // Before the object-scope attribute-skipper landed, the walker
        // stepped past `[` and dispatched `CommitBehavior(` as a bare
        // self-call, producing the sample
        //   Reason=bare-call Token='CommitBehavior'
        // every time this attribute appeared (BankAccReconciliationPost
        // and friends emit it on every posting procedure). With the
        // skipper, the entire bracketed span is consumed silently.
        var resolver = MakeResolver();
        const string src = """
            codeunit 50000 "MyHelper"
            {
                [CommitBehavior(CommitBehavior::Ignore)]
                local procedure DoPost()
                begin
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Inherent_permissions_attribute_does_not_fire_bare_call_unresolved()
    {
        // Same shape, different attribute name — covers the broader
        // family of method attributes (InherentPermissions,
        // InherentEntitlements, NonDebuggable, TryFunction, …).
        var resolver = MakeResolver();
        const string src = """
            codeunit 50000 "MyHelper"
            {
                [InherentPermissions(PermissionObjectType::TableData, Database::Customer, 'X')]
                procedure Update()
                begin
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void EventSubscriber_attribute_still_emits_after_attribute_skipper()
    {
        // Guard: the generic attribute skipper must not eat EventSubscriber —
        // that one carries a real cross-object reference (event_publisher).
        var resolver = MakeResolver();
        resolver.AddType("Sales-Post", new AlTypeRef(BaseAppId, "codeunit", 80, "Sales-Post"));

        const string src = """
            codeunit 50000 "MyHelper"
            {
                [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", 'OnAfterPostSalesDoc', '', false, false)]
                local procedure OnAfterPostSalesDoc(var SalesHeader: Record "Sales Header")
                begin
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "event_publisher"
            && r.TargetObjectName == "Sales-Post"
            && r.TargetMemberName == "OnAfterPostSalesDoc");
    }

    // ── Named return values ──────────────────────────────────────────

    [Fact]
    public void Named_return_value_is_in_scope_in_procedure_body()
    {
        // `procedure GetTableValuePair(...) TableValuePair: Dictionary ...`
        // — the named-return identifier becomes an in-scope local
        // for the body. Verified against the unresolved sample
        //   Reason=head-not-a-variable Token='TableValuePair'
        // logged from AssemblyLine.Table.al in BC 26.5. Same idiom
        // ships across PhysInvtOrderLine, ItemJournalLine,
        // JobJournalLine, PurchaseLine, SalesLine, ServiceLine.
        //
        // Asserting on the diagnostic sample directly (rather than the
        // unresolved counter) so an unrelated unresolved on the same
        // line doesn't mask a regression of this fix.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50100, "MyHelper"));

        const string src = """
            procedure GetTableValuePair(FieldNo: Integer) TableValuePair: Dictionary of [Integer, Code[20]]
            begin
                TableValuePair.Add(FieldNo, 'X');
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        // The named return TableValuePair, referenced inside the body,
        // must not fire head-not-a-variable.
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "TableValuePair" && s.Reason == "head-not-a-variable");
    }

    [Fact]
    public void Anonymous_return_clause_still_parses_cleanly()
    {
        // Guard: the named-return parser must not eat an anonymous
        // return type — `procedure Foo(): Integer` should still leave
        // the cursor positioned to enter the body. A regression here
        // would skip the body entirely and lose every reference inside.
        var resolver = MakeResolver();
        resolver.AddType("Sales-Post", new AlTypeRef(BaseAppId, "codeunit", 80, "Sales-Post"));

        const string src = """
            procedure DoWork(): Integer
            var
                SalesPost: Codeunit "Sales-Post";
            begin
                SalesPost.Run();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().Contain(r =>
            r.TargetObjectName == "Sales-Post" && r.ReferenceKind == "property_object");
    }

    // ── Chain :: typed-literal continuation through enum-typed fields ──

    [Fact]
    public void Field_typed_literal_chain_continues_to_enum_builtin()
    {
        // `Var."Field"::EnumValue.AsInteger()` — the dominant case-
        // label / enum-coercion shape in base app. Before the fix,
        // the chain walker exited after the field access and the main
        // dispatch picked `EnumValue` up as a fresh chain head, firing
        // head-not-a-variable. The `::Value` continuation in
        // WalkMemberChain skips the value identifier silently and
        // lets the trailing `.AsInteger()` resolve through the enum
        // builtin set.
        var resolver = MakeResolver();
        resolver.AddType("Sales Document Type",
            new AlTypeRef(BaseAppId, "enum", 39, "Sales Document Type"));
        resolver.AddMember("Sales Header", new AlMember(
            "Document Type", "table_field", "Enum", "Sales Document Type"));

        const string src = """
            procedure UsesDocumentType()
            var
                SalesHeader: Record "Sales Header";
            begin
                if SalesHeader."Document Type"::Quote.AsInteger() = 0 then
                    exit;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        // The chain-step "Quote" must not show up as unresolved.
        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "Quote");
        result.Stats.UnresolvedReceivers.Should().Be(0);
        // The field access on Sales Header still emits.
        result.References.Should().Contain(r =>
            r.ReferenceKind == "field_access"
            && r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "Document Type");
    }

    [Fact]
    public void Field_typed_literal_chain_as_case_branch_label_silences()
    {
        // The pure case-label shape — `Var."Field"::Value:` — has no
        // trailing `.AsInteger()`. The walker must still consume the
        // `::Value` and stop cleanly, leaving the trailing `:` for the
        // main loop's case-branch dispatch.
        var resolver = MakeResolver();
        resolver.AddMember("Sales Header", new AlMember(
            "Document Type", "table_field", "Enum", "Sales Document Type"));
        resolver.AddType("Sales Document Type",
            new AlTypeRef(BaseAppId, "enum", 39, "Sales Document Type"));

        const string src = """
            procedure Decide()
            var
                SalesHeader: Record "Sales Header";
            begin
                case SalesHeader."Document Type" of
                    SalesHeader."Document Type"::Quote:
                        exit;
                    SalesHeader."Document Type"::"Blanket Order":
                        exit;
                end;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "Quote" || s.Token == "Blanket Order");
    }

    [Fact]
    public void Field_typed_literal_chain_with_scalar_option_silences()
    {
        // Inline scalar-Option fields (no named enum behind them) have
        // an empty ReturnTypeName in the catalog, so the chain walker's
        // receiver drops to null after the field access. The `::Value`
        // continuation still needs to silence — and the trailing
        // `.AsInteger()` should walk past via the dead-receiver branch.
        // Mirrors the unresolved sample
        //   Token='StepLine' Line=105 Owner=table:Cash Flow Chart Setup
        // logged from CashFlowChartSetup.Table.al.
        var resolver = MakeResolver();
        resolver.AddType("Business Chart Buffer",
            new AlTypeRef(BaseAppId, "table", 485, "Business Chart Buffer"));
        // "Chart Type" is declared as an inline Option — no separate
        // enum type to resolve, so ReturnTypeName comes back empty.
        resolver.AddMember("Business Chart Buffer",
            new AlMember("Chart Type", "table_field", null, null));

        const string src = """
            procedure GetChartType(): Integer
            var
                BusinessChartBuf: Record "Business Chart Buffer";
            begin
                exit(BusinessChartBuf."Chart Type"::StepLine.AsInteger());
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "StepLine");
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Field_typed_literal_quoted_value_silences()
    {
        // `Var."Field"::"Multi Word Value"` — quoted enum values with
        // spaces hit the same dispatch path. Common in account-type
        // and document-type discriminator code:
        //   AppliedPaymentEntry."Account Type"::"G/L Account":
        var resolver = MakeResolver();
        resolver.AddType("Bank Acc. Reconciliation Line",
            new AlTypeRef(BaseAppId, "table", 274, "Bank Acc. Reconciliation Line"));
        resolver.AddMember("Bank Acc. Reconciliation Line",
            new AlMember("Account Type", "table_field", null, null));

        const string src = """
            procedure Decide()
            var
                BankRecLine: Record "Bank Acc. Reconciliation Line";
            begin
                case BankRecLine."Account Type" of
                    BankRecLine."Account Type"::"G/L Account":
                        exit;
                end;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "G/L Account" || s.Token == "Account Type");
    }

    // ── Tableextension procedures reach base-table globals ───────────

    [Fact]
    public void Tableextension_chain_resolves_when_global_passed_via_context()
    {
        // AL's compile-time merged scope lets a tableextension's
        // procedures call into the base table's global var map. The
        // unresolved samples surfacing as
        //   Reason=head-not-a-variable Token='DimMgt' Owner=tableextension:…
        // come from the import service NOT merging the base table's
        // globalsByOwner entry into the extension's. With the merge
        // wired up at the import-service layer, the extractor sees
        // DimMgt in its GlobalVars and the chain resolves to the
        // codeunit's procedure. This test pins the extractor side of
        // the contract: given a tableextension owner with DimMgt in
        // its (merged) globals, the chain head must resolve cleanly.
        var resolver = MakeResolver();
        resolver.AddType("Item Journal Line",
            new AlTypeRef(BaseAppId, "table", 83, "Item Journal Line"));
        resolver.AddType("DimensionManagement",
            new AlTypeRef(BaseAppId, "codeunit", 408, "DimensionManagement"));
        resolver.AddMember("DimensionManagement",
            new AlMember("GetCombinedDimensionSetID", "procedure", "Integer", null));

        var mergedGlobals = new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase)
        {
            ["DimMgt"] = new ResolvedVariableType("Codeunit", "DimensionManagement"),
        };

        const string src = """
            procedure CreateAssemblyDim()
            begin
                "Dimension Set ID" := DimMgt.GetCombinedDimensionSetID();
            end;
            """;
        var ctx = OwnerTableExtension(resolver, "Asm. Item Journal Line",
            baseTable: "Item Journal Line", globals: mergedGlobals);
        var result = AlReferenceExtractor.Extract(src, ctx);

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "DimensionManagement"
            && r.TargetMemberName == "GetCombinedDimensionSetID");
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "DimMgt" && s.Reason == "head-not-a-variable");
    }

    // ── Bare calls in report column properties hit dataitem source ──

    [Fact]
    public void Bare_call_in_report_column_property_resolves_on_dataitem_source()
    {
        // `AutoFormatExpression = GetCurrencyCodeFromBank();` inside a
        // report's `column(N; …) { … }` is a bare call against a
        // procedure on the dataitem's SOURCE TABLE — not on the
        // report. The structure extractor tracks the current dataitem
        // source on AlExtractionState.CurrentDataItemSource; the
        // bare-self-call resolver now consults that slot after the
        // OwnerType / RecType / BaseObjectType lookups miss.
        //
        // Verified against the unresolved samples
        //   Reason=bare-call Token='GetCurrencyCodeFromBank' Line=111/119/157
        // logged from BankAccountCheckDetails.Report.al in BC 26.5.
        var resolver = MakeResolver();
        resolver.AddType("Bank Account - Check Details",
            new AlTypeRef(BaseAppId, "report", 1406, "Bank Account - Check Details"));
        resolver.AddType("Check Ledger Entry",
            new AlTypeRef(BaseAppId, "table", 272, "Check Ledger Entry"));
        resolver.AddMember("Check Ledger Entry",
            new AlMember("GetCurrencyCodeFromBank", "procedure", "Code", null));

        const string src = """
            report 1406 "Bank Account - Check Details"
            {
                dataset
                {
                    dataitem(CheckLedgerEntry; "Check Ledger Entry")
                    {
                        column(Amount; Amount)
                        {
                            AutoFormatExpression = GetCurrencyCodeFromBank();
                        }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerReport(resolver, "Bank Account - Check Details"));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Check Ledger Entry"
            && r.TargetMemberName == "GetCurrencyCodeFromBank");
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "GetCurrencyCodeFromBank");
    }

    [Fact]
    public void Bare_call_in_report_falls_back_to_unresolved_when_no_match()
    {
        // Guard: the dataitem-source fallback must not silence a
        // genuinely missing procedure. When neither the report, its
        // Rec, nor any dataitem source declares the name, it stays
        // bare-call unresolved.
        var resolver = MakeResolver();
        resolver.AddType("My Report",
            new AlTypeRef(BaseAppId, "report", 50000, "My Report"));
        resolver.AddType("Check Ledger Entry",
            new AlTypeRef(BaseAppId, "table", 272, "Check Ledger Entry"));

        const string src = """
            report 50000 "My Report"
            {
                dataset
                {
                    dataitem(CheckLedgerEntry; "Check Ledger Entry")
                    {
                        column(X; X)
                        {
                            AutoFormatExpression = TotallyUnknownProc();
                        }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerReport(resolver, "My Report"));

        result.Stats.UnresolvedSamples.Should().Contain(s =>
            s.Token == "TotallyUnknownProc" && s.Reason == "bare-call");
    }

    // ── Bare-callable resolution order ───────────────────────────────

    [Fact]
    public void Bare_callable_silence_falls_through_when_no_own_member()
    {
        // `Exists(FileName)` is the deprecated AL file-existence
        // global. The bare-call resolver now consults the catalog
        // FIRST (no own-member named Exists on this codeunit), then
        // falls through to the BareCallableFunctions silence list.
        // Without Exists in that list — or with the old
        // bare-callable-first order — every legacy file-system check
        // in BC's banking / data-exchange code surfaces as
        // bare-call unresolved.
        //
        // The codeunit owner type must be in the catalog or
        // OwnerType() returns null and the early-out fires before
        // catalog resolution; adding the type here exercises the
        // full path (own-member miss → silence-list match → return).
        var resolver = MakeResolver();
        resolver.AddType("MyHelper",
            new AlTypeRef(BaseAppId, "codeunit", 50100, "MyHelper"));

        const string src = """
            procedure Check(FileName: Text): Boolean
            begin
                exit(Exists(FileName));
            end;
            """;
        var result = AlReferenceExtractor.Extract(src,
            OwnerCodeunit(resolver, tableNo: null));

        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "Exists");
    }

    [Fact]
    public void User_procedure_named_after_system_function_wins_catalog_lookup()
    {
        // The reorder pins AL's actual semantics: a user procedure on
        // the owner shadows a same-named system function. Before the
        // reorder, the bare-callable check fired BEFORE own-member
        // resolution, silencing the user procedure's reference.
        // Here `Exists` is both a system function (deprecated file
        // global, now in BareCallableFunctions) AND a real procedure
        // on the codeunit. The catalog lookup must win — Find
        // references on the procedure depends on this method_call
        // reference emitting.
        var resolver = MakeResolver();
        resolver.AddType("Persistent Blob Impl.",
            new AlTypeRef(BaseAppId, "codeunit", 4151, "Persistent Blob Impl."));
        resolver.AddMember("Persistent Blob Impl.",
            new AlMember("Exists", "procedure", "Boolean", null));

        const string src = """
            procedure CallSelf(): Boolean
            begin
                exit(Exists(42));
            end;
            """;
        var ctx = new AlExtractContext(
            OwnerKind: "codeunit",
            OwnerName: "Persistent Blob Impl.",
            OwnerObjectId: 4151,
            OwnerAppId: BaseAppId,
            GlobalVars: new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: resolver);
        var result = AlReferenceExtractor.Extract(src, ctx);

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Persistent Blob Impl."
            && r.TargetMemberName == "Exists");
    }

    // ── AL `this` keyword (BC 24+) ────────────────────────────────────

    [Fact]
    public void This_keyword_resolves_to_owner_type()
    {
        // BC 24+ allows `this.Member` inside a codeunit / page / report
        // to refer to the current object instance. The chain walker
        // didn't know about `this` and surfaced every reference as
        // head-not-a-variable. Verified against the BC 26.13 sample
        //   Token='this' Owner=codeunit:Apply Retention Policy Impl.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));
        resolver.AddMember("MyHelper", new AlMember("DoStuff", "procedure", null, null));

        const string src = """
            procedure CallSelf()
            begin
                this.DoStuff();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "MyHelper"
            && r.TargetMemberName == "DoStuff");
        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "this");
    }

    // ── ControlAddIn event declarations ──────────────────────────────

    [Fact]
    public void ControlAddIn_event_declaration_silences()
    {
        // `event Foo(args);` inside a controladdin block is a
        // declaration, not a call. Without an explicit consumer, the
        // orchestrator dispatched `Foo(` through TryConsumeBareSelfCall
        // and captured every controladdin event as a bare-call
        // unresolved sample. Verified against System Application's
        // ControlAddIns: ControlAddInReady, DataPointClicked,
        // ErrorOccurred, AddInReady, …
        var resolver = MakeResolver();
        resolver.AddType("MyAddIn", new AlTypeRef(BaseAppId, "controladdin", 50000, "MyAddIn"));

        const string src = """
            controladdin MyAddIn
            {
                event ControlReady();
                event DataPointClicked(Index: Integer);
                event ErrorOccurred(Message: Text);
            }
            """;
        var ctx = new AlExtractContext(
            OwnerKind: "controladdin",
            OwnerName: "MyAddIn",
            OwnerObjectId: 50000,
            OwnerAppId: BaseAppId,
            GlobalVars: new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: resolver);
        var result = AlReferenceExtractor.Extract(src, ctx);

        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "ControlReady"
            || s.Token == "DataPointClicked"
            || s.Token == "ErrorOccurred");
    }

    // ── ControlAddIn event-receiver trigger syntax ───────────────────

    [Fact]
    public void Receiver_event_trigger_parses_parameters_into_scope()
    {
        // ControlAddin / DotNet event subscriptions use the form
        //   trigger Receiver::EventName(param: DotNet SomeType)
        // The trigger name carries a `::EventName` suffix the
        // procedure-header parser previously didn't consume — the
        // parameter list parser then bailed before reading the
        // params, and every body-scope chain on those parameters
        // fired head-not-a-variable. Verified against BC 26.13's
        // OnlineMapLocation.Page.al where `location: DotNet Location`
        // surfaced as a chain head missing `location.Status` /
        // `location.Coordinate.Latitude`.
        var resolver = MakeResolver();
        resolver.AddType("MyPage", new AlTypeRef(BaseAppId, "page", 50000, "MyPage"));
        resolver.AddType("Location", new AlTypeRef(BaseAppId, "table", 14, "Location"));

        const string src = """
            trigger LocationProvider::LocationChanged(location: DotNet Location)
            begin
                if location.Status <> 0 then
                    exit;
            end;
            """;
        var ctx = OwnerPage(resolver, "MyPage", sourceTable: null);
        var result = AlReferenceExtractor.Extract(src, ctx);

        // The `location` parameter is a DotNet variable; its `.Status`
        // chain must silence (DotNet bypass), and we must NOT resolve
        // it to the AL Location table.
        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "Status");
        result.References.Should().NotContain(r =>
            r.TargetObjectName == "Location" && r.TargetMemberName == "Status");
    }

    // ── IsolatedStorage / NumberSequence / MediaSet static receivers ──

    [Fact]
    public void Static_runtime_receivers_silence_chain()
    {
        // `IsolatedStorage.Set(...)`, `NumberSequence.Insert(...)`,
        // `MediaSet.FindOrphans()` — all AL runtime APIs accessed as
        // `<Name>.<Method>(...)` from anywhere in code. Each name lives
        // in BuiltinStaticReceivers; the chain walker silences cleanly.
        var resolver = MakeResolver();
        const string src = """
            procedure DoStuff()
            begin
                IsolatedStorage.Set('K', 'V', DataScope::Module);
                NumberSequence.Insert('SeqName', 1, 1, true);
                MediaSet.FindOrphans();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "IsolatedStorage"
            || s.Token == "NumberSequence"
            || s.Token == "MediaSet");
    }

    // ── Microsoft namespace prefix ───────────────────────────────────

    [Fact]
    public void Microsoft_namespace_prefix_silences_chain()
    {
        // BC 26.13 uses fully-qualified namespace references like
        //   Microsoft.Service.Document."Service Line Type".FromInteger(Type)
        // in expression position. The chain head `Microsoft` isn't a
        // variable or catalog type; the static-receiver walk now
        // recognises it as a namespace prefix and advances through
        // the dotted segments + the final method call.
        var resolver = MakeResolver();

        const string src = """
            procedure UseNamespacedType(Type: Integer)
            var
                TableId: Integer;
            begin
                TableId := Microsoft.Service.Document."Service Line Type".FromInteger(Type);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "Microsoft");
    }

    // ── Type-name length qualifier stripping ─────────────────────────

    [Fact]
    public void Bracketed_scalar_type_silences_like_bare_form()
    {
        // SymbolPackage-derived variables can come through with
        // TypeName="Text[250]" (length qualifier preserved). The
        // KnownSystemTypes lookup now strips the bracket suffix
        // before checking so a `var X: Text[250]` declaration
        // silences the same way `Text` does — chains through the
        // variable (`AppName.Trim()`) shouldn't fire
        // head-var-type-unresolved.
        var resolver = MakeResolver();
        var globals = new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase)
        {
            // The TypeKeyword carries the same string for scalar
            // declarations the symbol package didn't tag with a
            // catalog kind. Both shapes silence.
            ["AppName"] = new ResolvedVariableType("Text[250]", "Text[250]"),
        };

        const string src = """
            procedure UseAppName()
            var
                Trimmed: Text;
            begin
                Trimmed := AppName.Trim();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver, globals: globals));

        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "AppName");
    }

    // ── Cross-app name collision tiebreaker ───────────────────────────

    [Fact]
    public void Same_app_candidate_wins_over_other_visible_apps()
    {
        // `No. Series Line` exists in BOTH Base Application and
        // Business Foundation with the same object id (309). Code in
        // Business Foundation referencing the table should resolve to
        // Business Foundation's version — Base App's stripped-down
        // shim doesn't have the `Implementation` field. Old resolver
        // picked the first visible candidate, surfacing
        //   chain-step Token='Implementation' ReceiverName='No. Series Line'
        //     ReceiverAppId=437dbf0e... (Base App, wrong)
        // The resolver's own preference logic is exercised through the
        // real CatalogResolver in integration tests; this test pins
        // the extractor-side contract (StubResolver returns the type
        // the caller-app-aware preference wants) by adding only the
        // app-specific version.
        var resolver = MakeResolver();
        var bfAppId = Guid.Parse("f3552374-a1f2-4356-848e-196002525837");
        resolver.AddType("No. Series Line",
            new AlTypeRef(bfAppId, "table", 309, "No. Series Line"));
        resolver.AddMember("No. Series Line",
            new AlMember("Implementation", "table_field", "Enum", "No. Series Implementation"));

        const string src = """
            procedure UseImplementation()
            var
                NoSeriesLine: Record "No. Series Line";
            begin
                if NoSeriesLine.Implementation::Sequence = NoSeriesLine.Implementation then
                    exit;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "field_access"
            && r.TargetObjectName == "No. Series Line"
            && r.TargetMemberName == "Implementation");
        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "Implementation");
    }

    // ── Platform virtual table chain-step silence ─────────────────────

    [Fact]
    public void Platform_virtual_table_chain_step_silences_by_object_id()
    {
        // The System app's `File` table (objectId 2000000022) doesn't
        // model static helpers like ViewFromStream / WriteAllBytes in
        // its symbol package members. Chain steps that miss on the
        // platform-id range should silence — the underline / find-
        // references trade-off is implicit when we accept these tables
        // into the catalog.
        var resolver = MakeResolver();
        var systemAppId = Guid.Parse("8874ed3a-0643-4247-9ced-7a7002f7135d");
        resolver.AddType("File",
            new AlTypeRef(systemAppId, "table", 2000000022, "File"));

        const string src = """
            procedure UseFile()
            var
                FileVar: Record File;
            begin
                FileVar.ViewFromStream(InStream, 'X');
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "ViewFromStream");
    }

    // ── Static-receiver silences for Dialog / Text ───────────────────

    [Fact]
    public void Dialog_static_call_silences_chain()
    {
        // `Dialog.StrMenu(...)` uses Dialog as a static receiver — no
        // variable, no catalog object. Without Dialog in the
        // BuiltinStaticReceivers set the chain walker emits a
        // head-not-a-variable for every base-app
        // `Dialog.StrMenu` / `Dialog.Update` / `Dialog.Open` call.
        // Verified against the BC 26.13 unresolved samples
        //   Token='Dialog' Line=249/276 Owner=codeunit:Bank Acc. Entry Set Recon.-No.
        //   Token='DIALOG' Line=270/282/294 Owner=table:Text-to-Account Mapping
        var resolver = MakeResolver();
        const string src = """
            procedure Choose()
            var
                Selection: Integer;
            begin
                Selection := Dialog.StrMenu('A,B,C', 1, 'Pick one');
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "Dialog");
    }

    [Fact]
    public void Text_static_call_silences_chain()
    {
        // `Text.StrSubstNo(...)` and `Text.Format(...)` use Text as a
        // static receiver. PmtRecReversalFinalize.Page.al uses this
        // shape in OnOpenPage — `FinalizeTxt := Text.StrSubstNo(...)`.
        var resolver = MakeResolver();
        const string src = """
            procedure Build(): Text
            var
                Msg: Text;
            begin
                Msg := Text.StrSubstNo('Hello %1', 'world');
                exit(Msg);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "Text");
    }

    // ── Array indexing in chain heads ─────────────────────────────────

    [Fact]
    public void Array_indexed_chain_head_resolves_member_on_element_type()
    {
        // `Contact[2].UpdateBusinessRelation()` — local declared as
        // `Contact: array[2] of Contact`. Before the array-index
        // chain detection landed, the orchestrator skipped the
        // chain entry because next-token-after-head is `[`, not `.`;
        // the `UpdateBusinessRelation` identifier was then dispatched
        // as a bare self-call against the table owner and missed.
        // Verified against the BC 26.13 unresolved sample
        //   Reason=bare-call Token='UpdateBusinessRelation' Owner=table:Merge Duplicates Buffer
        var resolver = MakeResolver();
        resolver.AddType("Merge Duplicates Buffer",
            new AlTypeRef(BaseAppId, "table", 64, "Merge Duplicates Buffer"));
        resolver.AddType("Contact",
            new AlTypeRef(BaseAppId, "table", 5050, "Contact"));
        resolver.AddMember("Contact",
            new AlMember("UpdateBusinessRelation", "procedure", "Boolean", null));

        const string src = """
            local procedure MergeContacts()
            var
                Contact: array[2] of Contact;
            begin
                Contact[2].UpdateBusinessRelation();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(resolver, "Merge Duplicates Buffer"));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Contact"
            && r.TargetMemberName == "UpdateBusinessRelation");
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "UpdateBusinessRelation");
    }

    // ── Implicit-Rec field access falls back to dataitem source ──────

    [Fact]
    public void Implicit_rec_field_chain_head_resolves_against_dataitem_source()
    {
        // `Type.AsInteger()` inside a report dataitem's OnAfterGetRecord
        // trigger is implicit-Rec on the dataitem's source, NOT on
        // the report itself. Rec at object scope binds to the report
        // (DetermineRecBinding's report fallback), so without the
        // dataitem-source fallback the chain head misses on the
        // report's empty member list. Verified against the BC 26.13
        // sample `Token='Type' Owner=report:Cost Acctg. Analysis`.
        var resolver = MakeResolver();
        resolver.AddType("Cost Acctg. Analysis",
            new AlTypeRef(BaseAppId, "report", 1127, "Cost Acctg. Analysis"));
        resolver.AddType("Cost Type",
            new AlTypeRef(BaseAppId, "table", 1103, "Cost Type"));
        resolver.AddMember("Cost Type",
            new AlMember("Type", "table_field", "Enum", "Cost Type Type"));

        const string src = """
            report 1127 "Cost Acctg. Analysis"
            {
                dataset
                {
                    dataitem(CostType; "Cost Type")
                    {
                        trigger OnAfterGetRecord()
                        var
                            LineTypeInt: Integer;
                        begin
                            LineTypeInt := Type.AsInteger();
                        end;
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerReport(resolver, "Cost Acctg. Analysis"));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "field_access"
            && r.TargetObjectName == "Cost Type"
            && r.TargetMemberName == "Type");
        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "Type");
    }

    [Fact]
    public void Implicit_rec_quoted_field_chain_head_resolves_against_dataitem_source()
    {
        // Same pattern with a quoted multi-word field name —
        // `"Attachment File".HasValue()` inside the report's dataitem
        // trigger. Verified against the BC 26.13 sample
        //   Token='Attachment File' Owner=report:Relocate Attachments
        var resolver = MakeResolver();
        resolver.AddType("Relocate Attachments",
            new AlTypeRef(BaseAppId, "report", 5054, "Relocate Attachments"));
        resolver.AddType("Attachment",
            new AlTypeRef(BaseAppId, "table", 5062, "Attachment"));
        resolver.AddMember("Attachment",
            new AlMember("Attachment File", "table_field", "Blob", null));

        const string src = """
            report 5054 "Relocate Attachments"
            {
                dataset
                {
                    dataitem(Attachment; Attachment)
                    {
                        trigger OnAfterGetRecord()
                        begin
                            CalcFields("Attachment File");
                            if "Attachment File".HasValue() then begin
                                exit;
                            end;
                        end;
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerReport(resolver, "Relocate Attachments"));

        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "Attachment File");
    }

    // ── Dataitem alias forward-reference (report) ─────────────────────

    [Fact]
    public void Report_dataitem_alias_resolves_when_declared_after_property()
    {
        // ContactCoverSheet.Report.al references `TempSegmentLine` at
        // line 23 via `WordMergeDataItem = TempSegmentLine;` and later
        // uses it directly in a trigger at lines 37-39, while the
        // dataitem itself is declared at line 79. Single-pass walker
        // missed the alias because it hadn't been registered yet.
        // The pre-scan addresses this for reports, mirroring the
        // existing xmlport pre-scan.
        var resolver = MakeResolver();
        resolver.AddType("Contact Cover Sheet",
            new AlTypeRef(BaseAppId, "report", 5085, "Contact Cover Sheet"));
        resolver.AddType("Segment Line",
            new AlTypeRef(BaseAppId, "table", 5077, "Segment Line"));
        resolver.AddMember("Segment Line",
            new AlMember("FindFirst", "procedure", "Boolean", null));

        const string src = """
            report 5085 "Contact Cover Sheet"
            {
                WordMergeDataItem = TempSegmentLine;

                dataset
                {
                    dataitem(Contact; Contact)
                    {
                        trigger OnAfterGetRecord()
                        begin
                            TempSegmentLine.SetRange("Contact No.", "Contact No.");
                            if not TempSegmentLine.FindFirst() then
                                exit;
                        end;
                    }
                    dataitem(TempSegmentLine; "Segment Line")
                    {
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerReport(resolver, "Contact Cover Sheet"));

        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "TempSegmentLine");
    }

    // ── XmlPort tableelement forward references ──────────────────────

    [Fact]
    public void XmlPort_tableelement_alias_resolves_when_declared_after_procedure()
    {
        // Schema blocks in xmlports routinely sit after the procedure
        // block in Base App (e.g. SEPADDpain00800102.XmlPort.al uses
        // `paymentexportdatagroup.GetOrganizationID()` at line 103
        // with the matching `tableelement(paymentexportdatagroup; ...)`
        // at line 111). The pre-scan registers the alias in the outer
        // scope frame BEFORE the main walk visits the procedure body
        // so the chain head resolves cleanly. Without it, every
        // tableelement-alias chain in a procedure ahead of the schema
        // fires head-not-a-variable.
        var resolver = MakeResolver();
        resolver.AddType("Payment Export Data",
            new AlTypeRef(BaseAppId, "table", 1226, "Payment Export Data"));
        resolver.AddMember("Payment Export Data",
            new AlMember("GetOrganizationID", "procedure", "Text", null));

        const string src = """
            xmlport 1010 "SEPA DD pain.008.001.02"
            {
                procedure ReadOrgId(): Text
                begin
                    exit(paymentexportdatagroup.GetOrganizationID());
                end;

                schema
                {
                    textelement(Document)
                    {
                        tableelement(paymentexportdatagroup; "Payment Export Data")
                        {
                            fieldelement(PmtInfId; PaymentExportDataGroup."Payment Information ID")
                            {
                            }
                        }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerXmlPort(resolver, "SEPA DD pain.008.001.02"));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Payment Export Data"
            && r.TargetMemberName == "GetOrganizationID");
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "paymentexportdatagroup");
    }

    [Fact]
    public void XmlPort_tableelement_pre_scan_does_not_emit_duplicate_references()
    {
        // Guard: the pre-scan only seeds the scope frame; the main
        // walk still emits the source-table property_object reference
        // once. A double-emit would inflate the resolved counter and
        // surface as duplicate rows in oe_module_references.
        var resolver = MakeResolver();
        resolver.AddType("Payment Export Data",
            new AlTypeRef(BaseAppId, "table", 1226, "Payment Export Data"));

        const string src = """
            xmlport 1010 "Test Port"
            {
                schema
                {
                    textelement(Document)
                    {
                        tableelement(grp; "Payment Export Data") { }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerXmlPort(resolver, "Test Port"));

        result.References.Where(r =>
                r.TargetObjectName == "Payment Export Data"
                && r.ReferenceKind == "property_object")
            .Should().HaveCount(1);
    }

    // ── Nested dataitem scope ────────────────────────────────────────

    [Fact]
    public void Bare_call_in_nested_dataitem_resolves_against_outer_source()
    {
        // `EmptyLine()` inside an inner Integer-loop dataitem's
        // OnAfterGetRecord trigger targets a procedure on the outer
        // dataitem's source table ("Gen. Journal Line"). Before the
        // depth-tracked dataitem stack, only the innermost source
        // was kept and the call surfaced as bare-call unresolved.
        //
        // Verified against the unresolved samples
        //   Reason=bare-call Token='EmptyLine' Line=351 / 347 / 569 …
        // logged across AutoPostingErrors / GeneralJournalTest /
        // PhysInvtOrderDiffList / PhysInvtOrderTest / etc. in BC 26.13.
        var resolver = MakeResolver();
        resolver.AddType("Auto Posting Errors",
            new AlTypeRef(BaseAppId, "report", 17, "Auto Posting Errors"));
        resolver.AddType("Gen. Journal Line",
            new AlTypeRef(BaseAppId, "table", 81, "Gen. Journal Line"));
        resolver.AddType("Integer",
            new AlTypeRef(BaseAppId, "table", 2000000026, "Integer"));
        resolver.AddMember("Gen. Journal Line",
            new AlMember("EmptyLine", "procedure", "Boolean", null));

        const string src = """
            report 17 "Auto Posting Errors"
            {
                dataset
                {
                    dataitem(GenJnlLine; "Gen. Journal Line")
                    {
                        dataitem(Loop; Integer)
                        {
                            trigger OnAfterGetRecord()
                            begin
                                if EmptyLine() then
                                    exit;
                            end;
                        }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerReport(resolver, "Auto Posting Errors"));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Gen. Journal Line"
            && r.TargetMemberName == "EmptyLine");
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "EmptyLine");
    }

    [Fact]
    public void Sibling_dataitem_does_not_leak_into_next_dataitem_scope()
    {
        // Guard: when a sibling dataitem's body closes, its source
        // must NOT remain on the stack for the next sibling. Without
        // depth-tracked pop, the second dataitem's bare calls would
        // mis-resolve against the first sibling's table.
        var resolver = MakeResolver();
        resolver.AddType("Test Report",
            new AlTypeRef(BaseAppId, "report", 50000, "Test Report"));
        resolver.AddType("Cust. Ledger Entry",
            new AlTypeRef(BaseAppId, "table", 21, "Cust. Ledger Entry"));
        resolver.AddType("Vendor Ledger Entry",
            new AlTypeRef(BaseAppId, "table", 25, "Vendor Ledger Entry"));
        // Only the first sibling exposes the bare-callable procedure.
        // After the first dataitem's `}` closes, we expect EmptyProc
        // to surface as unresolved inside the second dataitem.
        resolver.AddMember("Cust. Ledger Entry",
            new AlMember("EmptyProc", "procedure", "Boolean", null));

        const string src = """
            report 50000 "Test Report"
            {
                dataset
                {
                    dataitem(Cust; "Cust. Ledger Entry")
                    {
                        trigger OnAfterGetRecord()
                        begin
                            if EmptyProc() then exit;
                        end;
                    }
                    dataitem(Vend; "Vendor Ledger Entry")
                    {
                        trigger OnAfterGetRecord()
                        begin
                            if EmptyProc() then exit;
                        end;
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerReport(resolver, "Test Report"));

        // First sibling: resolved against Cust. Ledger Entry.
        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Cust. Ledger Entry"
            && r.TargetMemberName == "EmptyProc");
        // Second sibling: must NOT resolve against Cust. Ledger Entry
        // (that frame was popped); it stays unresolved because Vendor
        // Ledger Entry doesn't expose the procedure.
        result.References.Should().NotContain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Cust. Ledger Entry"
            && r.TargetMemberName == "EmptyProc"
            && r.Line >= 12); // second dataitem's trigger lives later
        result.Stats.UnresolvedSamples.Should().Contain(s =>
            s.Token == "EmptyProc" && s.Reason == "bare-call");
    }

    [Fact]
    public void CurrQuery_chain_step_silences_in_query_body()
    {
        // `CurrQuery.ColumnNo("Customer No.")` inside a query trigger
        // is a runtime self-pointer call; CurrQuery's methods live on
        // the runtime, not in the catalog. Same shape as CurrPage /
        // currXMLport / CurrReport — silenced via BuiltinStaticReceivers.
        const string src = """
            query 1001 "My Query"
            {
                trigger OnBeforeOpen()
                begin
                    if CurrQuery.ColumnNo("Customer No.") = 0 then
                        exit;
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, new AlExtractContext(
            OwnerKind: "query",
            OwnerName: "My Query",
            OwnerObjectId: 1001,
            OwnerAppId: BaseAppId,
            GlobalVars: new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: MakeResolver()));

        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "CurrQuery");
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "ColumnNo");
    }

    [Fact]
    public void Bare_call_resolves_on_outer_dataitem_after_inner_dataitem_closed()
    {
        // Replicates InventoryPostingTest.Report.al's actual shape:
        // the OUTER dataitem ("Item Journal Line") wraps an INNER
        // dataitem (DimensionLoop on Integer). The inner closes
        // BEFORE the outer's own triggers; `EmptyLine()` at line
        // 439 lives inside the outer's `OnAfterGetRecord` and must
        // resolve against the outer's source after the inner has
        // been popped. The depth-tracked stack must keep the outer
        // alive across the inner's `}` close.
        var resolver = MakeResolver();
        resolver.AddType("Inventory Posting - Test",
            new AlTypeRef(BaseAppId, "report", 702, "Inventory Posting - Test"));
        resolver.AddType("Item Journal Line",
            new AlTypeRef(BaseAppId, "table", 83, "Item Journal Line"));
        resolver.AddType("Integer",
            new AlTypeRef(BaseAppId, "table", 2000000026, "Integer"));
        resolver.AddMember("Item Journal Line",
            new AlMember("EmptyLine", "procedure", "Boolean", null));

        const string src = """
            report 702 "Inventory Posting - Test"
            {
                dataset
                {
                    dataitem("Item Journal Line"; "Item Journal Line")
                    {
                        column(Foo; "Item No.") { }
                        dataitem(DimensionLoop; "Integer")
                        {
                            column(Bar; Number) { }
                            trigger OnAfterGetRecord()
                            begin
                                Clear(Number);
                            end;
                        }
                        trigger OnPreDataItem()
                        begin
                            Clear(QtyError);
                        end;
                        trigger OnAfterGetRecord()
                        begin
                            if EmptyLine() then
                                exit;
                        end;
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerReport(resolver, "Inventory Posting - Test"));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Item Journal Line"
            && r.TargetMemberName == "EmptyLine");
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "EmptyLine");
    }

    [Fact]
    public void Bare_call_resolves_through_five_level_nested_dataitems()
    {
        // The actual AutoPostingErrors shape: FIVE dataitem levels.
        // `GenJnlTmpl > GenJnlBatch > "Gen. Journal Line" > inner
        // siblings (DimensionLoop, "Gen. Jnl. Allocation") > triggers`.
        // The bare call sits in `Gen. Journal Line`'s OnAfterGetRecord
        // AFTER both inner siblings have closed. Stack at the call site
        // must contain [(d=2, GenJnlTmpl), (d=3, GenJnlBatch),
        // (d=4, "Gen. Journal Line")] and the resolver must walk
        // innermost-to-outermost to land on Gen. Journal Line.
        var resolver = MakeResolver();
        resolver.AddType("Auto Posting Errors",
            new AlTypeRef(BaseAppId, "report", 6250, "Auto Posting Errors"));
        resolver.AddType("Gen. Journal Template",
            new AlTypeRef(BaseAppId, "table", 80, "Gen. Journal Template"));
        resolver.AddType("Gen. Journal Batch",
            new AlTypeRef(BaseAppId, "table", 232, "Gen. Journal Batch"));
        resolver.AddType("Gen. Journal Line",
            new AlTypeRef(BaseAppId, "table", 81, "Gen. Journal Line"));
        resolver.AddType("Gen. Jnl. Allocation",
            new AlTypeRef(BaseAppId, "table", 221, "Gen. Jnl. Allocation"));
        resolver.AddType("Integer",
            new AlTypeRef(BaseAppId, "table", 2000000026, "Integer"));
        resolver.AddMember("Gen. Journal Line",
            new AlMember("EmptyLine", "procedure", "Boolean", null));
        resolver.AddMember("Gen. Journal Line",
            new AlMember("UpdateLineBalance", "procedure", null, null));

        const string src = """
            report 6250 "Auto Posting Errors"
            {
                dataset
                {
                    dataitem(GenJnlTmpl; "Gen. Journal Template")
                    {
                        DataItemTableView = sorting(Name);
                        column(TmplName; Name) { }
                        dataitem(GenJnlBatch; "Gen. Journal Batch")
                        {
                            DataItemLink = "Journal Template Name" = field(Name);
                            DataItemTableView = sorting("Journal Template Name", Name);
                            column(BatchName; Name) { }
                            dataitem("Gen. Journal Line"; "Gen. Journal Line")
                            {
                                DataItemLink = "Journal Batch Name" = field(Name);
                                DataItemTableView = sorting("Journal Template Name", "Journal Batch Name", "Line No.");
                                column(LineNo; "Line No.") { }
                                dataitem(DimensionLoop; Integer)
                                {
                                    DataItemTableView = sorting(Number);
                                    column(DimNum; Number) { }
                                    trigger OnAfterGetRecord()
                                    begin
                                        Clear(Number);
                                    end;
                                }
                                dataitem("Gen. Jnl. Allocation"; "Gen. Jnl. Allocation")
                                {
                                    DataItemLink = "Journal Template Name" = field("Journal Template Name");
                                    column(AllocNo; "Line No.") { }
                                    trigger OnAfterGetRecord()
                                    begin
                                        Clear("Line No.");
                                    end;
                                    trigger OnPreDataItem()
                                    begin
                                    end;
                                    trigger OnPostDataItem()
                                    begin
                                    end;
                                }
                                trigger OnAfterGetRecord()
                                begin
                                    UpdateLineBalance();
                                    if not EmptyLine() then
                                        exit;
                                end;
                            }
                        }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerReport(resolver, "Auto Posting Errors"));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Gen. Journal Line"
            && r.TargetMemberName == "EmptyLine");
        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Gen. Journal Line"
            && r.TargetMemberName == "UpdateLineBalance");
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "EmptyLine" || s.Token == "UpdateLineBalance");
    }

    [Fact]
    public void Bare_call_resolves_through_four_level_nested_dataitems()
    {
        // The AutoPostingErrors / InventoryPostingTest / etc. shape:
        // FOUR levels of dataitem nesting (Batch > Line > inner-loop)
        // where the bare call `UpdateLineBalance()` / `EmptyLine()`
        // sits in the middle level's OnAfterGetRecord AFTER an inner
        // sibling dataitem has closed. The resolver must walk past
        // the popped inner frame and still find the middle dataitem's
        // source table.
        var resolver = MakeResolver();
        resolver.AddType("Auto Posting Errors",
            new AlTypeRef(BaseAppId, "report", 6250, "Auto Posting Errors"));
        resolver.AddType("Gen. Journal Batch",
            new AlTypeRef(BaseAppId, "table", 232, "Gen. Journal Batch"));
        resolver.AddType("Gen. Journal Line",
            new AlTypeRef(BaseAppId, "table", 81, "Gen. Journal Line"));
        resolver.AddType("Integer",
            new AlTypeRef(BaseAppId, "table", 2000000026, "Integer"));
        resolver.AddMember("Gen. Journal Line",
            new AlMember("EmptyLine", "procedure", "Boolean", null));
        resolver.AddMember("Gen. Journal Line",
            new AlMember("UpdateLineBalance", "procedure", null, null));

        const string src = """
            report 6250 "Auto Posting Errors"
            {
                dataset
                {
                    dataitem(GenJnlBatch; "Gen. Journal Batch")
                    {
                        DataItemTableView = sorting("Journal Template Name", Name);
                        column(BatchName; Name) { }
                        dataitem("Gen. Journal Line"; "Gen. Journal Line")
                        {
                            DataItemLink = "Journal Batch Name" = field(Name);
                            column(LineNo; "Line No.") { }
                            dataitem(DimensionLoop; Integer)
                            {
                                DataItemTableView = sorting(Number);
                                trigger OnAfterGetRecord()
                                begin
                                    Clear(Number);
                                end;
                            }
                            dataitem(AllocLoop; Integer)
                            {
                                column(AllocNo; Number) { }
                                trigger OnAfterGetRecord()
                                begin
                                    Clear(Number);
                                end;
                            }
                            trigger OnAfterGetRecord()
                            begin
                                UpdateLineBalance();
                                if not EmptyLine() then
                                    exit;
                            end;
                        }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerReport(resolver, "Auto Posting Errors"));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Gen. Journal Line"
            && r.TargetMemberName == "EmptyLine");
        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Gen. Journal Line"
            && r.TargetMemberName == "UpdateLineBalance");
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "EmptyLine" || s.Token == "UpdateLineBalance");
    }

    // ── Preprocessor #if / #else / #endif branch handling ───────────

    [Fact]
    public void Preprocessor_if_else_picks_first_branch_only()
    {
        // `#if CLEAN24 ... #else ... #endif` ships pre- and post-
        // cleanup code paths side by side. Walking BOTH branches
        // doubles begin/end counts and pops the procedure scope
        // early — every later identifier dispatches at object scope.
        // The canonical shape from JobQueueErrorHandler.LogError in
        // BC 26.13: a single `if X.FindFirst() then begin` with
        // `end;` in the `#if CLEAN24` branch and `end else begin`
        // in the `#else` branch. Both branches' `end`s were being
        // counted; the local var `JobQueueEntry` then surfaces as
        // head-not-a-variable on later lines (samples Line=63/64/71).
        //
        // The walker now picks ONLY the first branch (`#if`) and
        // skips forward from `#else` to the matching `#endif`.
        var resolver = MakeResolver();
        resolver.AddType("Job Queue Error Handler",
            new AlTypeRef(BaseAppId, "codeunit", 450, "Job Queue Error Handler"));
        resolver.AddType("Job Queue Entry",
            new AlTypeRef(BaseAppId, "table", 472, "Job Queue Entry"));
        resolver.AddMember("Job Queue Entry",
            new AlMember("Modify", "procedure", "Boolean", null));

        const string src = """
            codeunit 450 "Job Queue Error Handler"
            {
                local procedure LogError(var JobQueueEntry: Record "Job Queue Entry")
                begin
                    if JobQueueEntry.Modify() then begin
            #if CLEAN24
                    end;
            #else
                        JobQueueEntry.Modify();
                    end else begin
                        JobQueueEntry.Modify();
                    end;
            #endif
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        // The `JobQueueEntry.Modify()` calls in the `#else` branch
        // should NOT be walked (they're skipped). Only the one in
        // the live `#if CLEAN24` branch — which here is the call
        // BEFORE the directive — should be emitted.
        result.References.Where(r =>
                r.ReferenceKind == "method_call"
                && r.TargetObjectName == "Job Queue Entry"
                && r.TargetMemberName == "Modify")
            .Should().HaveCount(1);
        // No head-not-a-variable samples for JobQueueEntry — the
        // walker stayed in procedure scope so the local var stayed
        // in scope.
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "JobQueueEntry");
    }

    [Fact]
    public void Preprocessor_nested_if_endif_in_else_branch_skipped_correctly()
    {
        // Guard: when the `#else` branch itself contains an
        // `#if X ... #endif` pair, the outer skip must NOT mistake
        // the inner `#endif` for its own. Without the depth counter,
        // the skip would stop at the inner `#endif` and resume
        // walking the rest of the outer `#else` branch.
        var resolver = MakeResolver();
        resolver.AddType("Test Helper",
            new AlTypeRef(BaseAppId, "codeunit", 50100, "Test Helper"));

        const string src = """
            codeunit 50100 "Test Helper"
            {
                procedure Run()
                var
                    LocalVar: Integer;
                begin
            #if A
                    LocalVar := 1;
            #else
            #if B
                    LocalVar := 2;
            #endif
                    LocalVar := 3;
            #endif
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        // LocalVar is declared and should be in scope for the live
        // branch (`#if A`). No head-not-a-variable samples for it.
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "LocalVar");
    }

    [Fact]
    public void Dataitem_source_with_namespace_prefix_resolves_last_segment_as_table()
    {
        // BC 26.x increasingly uses fully-namespaced type references:
        //   `dataitem("Integer"; System.Utilities.Integer)`
        //   `dataitem("Foo"; Microsoft.Foundation.NoSeries."No. Series Line")`
        // The dataitem source parser used to read the first segment
        // (`System`), fail to resolve it, and leave the cursor mid-
        // chain — `Utilities` then dispatched as a bare chain head
        // and fired head-not-a-variable (Utilities Lines=22/143 on
        // ServiceCertificateofSupply.Report.al was the canonical
        // sample). The fix walks the `.<segment>` chain so the LAST
        // segment is treated as the actual type name.
        var resolver = MakeResolver();
        resolver.AddType("Service Certificate of Supply",
            new AlTypeRef(BaseAppId, "report", 5916, "Service Certificate of Supply"));
        resolver.AddType("Integer",
            new AlTypeRef(BaseAppId, "table", 2000000026, "Integer"));

        const string src = """
            report 5916 "Service Certificate of Supply"
            {
                dataset
                {
                    dataitem("Integer"; System.Utilities.Integer)
                    {
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerReport(resolver, "Service Certificate of Supply"));

        // No head-not-a-variable for Utilities or Integer.
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "Utilities" || s.Token == "System");
        // Integer table was resolved as the dataitem source.
        result.References.Should().Contain(r =>
            r.ReferenceKind == "property_object"
            && r.TargetObjectName == "Integer");
    }

    [Fact]
    public void Numeric_record_id_in_variable_declaration_resolves_via_catalog()
    {
        // Older AL — and modules like Payment Links to PayPal that
        // never migrated — declare record variables by numeric id
        // instead of quoted name: <c>DtldVendLedgEntry: Record 380;</c>,
        // <c>var PaymentServiceSetup: Record 1060;</c>. The walker
        // used to capture Keyword="Record" but leave TypeName empty
        // (Number tokens aren't Identifier / QuotedIdentifier), so
        // every member access fired head-var-type-unresolved with
        // ReceiverName='Record ' (trailing space, then nothing). The
        // fix routes numeric ids through the resolver's
        // <see cref="IAlTypeResolver.ResolveTypeByObjectId"/> path.
        var resolver = MakeResolver();
        resolver.AddType("Detailed Vendor Ledg. Entry",
            new AlTypeRef(BaseAppId, "table", 380, "Detailed Vendor Ledg. Entry"));
        resolver.AddMember("Detailed Vendor Ledg. Entry",
            new AlMember("SetRange", "procedure", null, null));

        const string src = """
            codeunit 50000 MyHelper
            {
                procedure Process()
                var
                    DtldVendLedgEntry: Record 380;
                begin
                    DtldVendLedgEntry.SetRange("Vendor No.", 'V001');
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedSamples.Should().BeEmpty();
        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetMemberName == "SetRange"
            && r.TargetObjectName == "Detailed Vendor Ledg. Entry");
    }

    [Fact]
    public void Return_type_kind_hint_disambiguates_same_named_table_vs_page()
    {
        // `EDocument.GetEDocumentService()` returns
        // <c>Record "E-Document Service"</c>. E-Document Core ships
        // BOTH a table AND a page named "E-Document Service". The
        // resolver's tiebreaker prefers the same kind when given an
        // expectedKeyword hint — without that hint, the chain advance
        // landed on the page and every subsequent step
        // (.GetImportProcessVersion, etc.) missed against the wrong
        // receiver. AdvanceReceiverByMember now forwards
        // <see cref="AlMember.ReturnTypeKeyword"/> so kind-aware
        // tiebreaking kicks in.
        var resolver = MakeResolver();
        resolver.AddType("EDocumentService",
            new AlTypeRef(BaseAppId, "table", 6103, "E-Document Service"));
        // Same-named page in the catalog — without the hint the
        // resolver could land here.
        resolver.AddType("E-Document Service (page)",
            new AlTypeRef(BaseAppId, "page", 6133, "E-Document Service"));
        resolver.AddType("EDocument",
            new AlTypeRef(BaseAppId, "table", 6101, "E-Document"));
        resolver.AddMember("E-Document",
            new AlMember("GetEDocumentService", "procedure", "Record", "E-Document Service"));
        resolver.AddMember("E-Document Service",
            new AlMember("GetImportProcessVersion", "procedure", null, null));

        const string src = """
            codeunit 50000 MyHelper
            {
                procedure Run(var EDocument: Record "E-Document"): Boolean
                begin
                    exit(EDocument.GetEDocumentService().GetImportProcessVersion() <> 0);
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "GetImportProcessVersion");
    }

    [Fact]
    public void Report_with_source_table_property_binds_rec_to_that_table()
    {
        // Whse. Change Unit of Measure / VAT Report Suggest Lines etc.
        // — reports where the request page declares a SourceTable
        // property instead of using a `dataset { dataitem(X; Y) }`
        // block. AL still binds Rec to the SourceTable for layout
        // expressions and triggers; the walker now uses
        // OwnerSourceTableName as a fallback when the report has no
        // dataitem to prescan.
        var resolver = MakeResolver();
        resolver.AddType("Warehouse Activity Line",
            new AlTypeRef(BaseAppId, "table", 5767, "Warehouse Activity Line"));
        resolver.AddMember("Warehouse Activity Line",
            new AlMember("Qty. to Handle (Base)", "table_field", null, null));

        const string src = """
            report 7314 "Whse. Change Unit of Measure"
            {
                requestpage
                {
                    SourceTable = "Warehouse Activity Line";
                    layout
                    {
                        area(content)
                        {
                            field(QtyHandleBase; Rec."Qty. to Handle (Base)") { }
                        }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src,
            new AlExtractContext(
                OwnerKind: "report",
                OwnerName: "Whse. Change Unit of Measure",
                OwnerObjectId: 7314,
                OwnerAppId: BaseAppId,
                GlobalVars: new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
                Resolver: resolver,
                OwnerSourceTableName: "Warehouse Activity Line"));

        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "Qty. to Handle (Base)");
    }

    [Fact]
    public void Reportextension_this_member_resolves_through_base_report()
    {
        // `this.GetLocation("Location Code")` inside
        // `reportextension "Mfg. Get Outbound Source Docs" extends
        // "Get Outbound Source Docs"`. The receiver `this` resolves to
        // the reportextension itself, but `GetLocation` is a procedure
        // on the BASE report — AL merges extension + base scopes at
        // dispatch time. Without the chain-step base-object fallback,
        // every explicit-`this.<baseMember>` chain in extension code
        // fired chain-step unresolved. (The bare-self-call path
        // already had this branch; this fix mirrors it for the chain
        // walker.)
        var resolver = MakeResolver();
        resolver.AddType("Get Outbound Source Docs",
            new AlTypeRef(BaseAppId, "report", 5754, "Get Outbound Source Docs"));
        resolver.AddType("Mfg. Get Outbound Source Docs",
            new AlTypeRef(BaseAppId, "reportextension", 99000836, "Mfg. Get Outbound Source Docs"));
        resolver.AddMember("Get Outbound Source Docs",
            new AlMember("GetLocation", "procedure", null, null));

        const string src = """
            reportextension 99000836 "Mfg. Get Outbound Source Docs" extends "Get Outbound Source Docs"
            {
                dataset
                {
                    add(ProdOrderComp)
                    {
                        trigger OnAfterGetRecord()
                        begin
                            this.GetLocation("Location Code");
                        end;
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src,
            new AlExtractContext(
                OwnerKind: "reportextension",
                OwnerName: "Mfg. Get Outbound Source Docs",
                OwnerObjectId: 99000836,
                OwnerAppId: BaseAppId,
                GlobalVars: new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
                Resolver: resolver,
                OwnerExtendsName: "Get Outbound Source Docs"));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetMemberName == "GetLocation"
            && r.TargetObjectName == "Get Outbound Source Docs");
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "GetLocation");
    }

    [Fact]
    public void Bare_self_call_continues_chain_through_return_type()
    {
        // EditInExcelFiltersImpl.Codeunit.al:48 pattern:
        //   `Get(ODataFieldName).AddFilterValueV2(EditInExcelFilterType, FilterValue);`
        // `Get` is on the owner codeunit and returns
        // `Codeunit "Edit in Excel Fld Filter Impl."`. Before the
        // chain-continuation fix, bare-self-call emitted Get's
        // reference but left `.AddFilterValueV2(...)` for the main
        // loop to re-dispatch as a fresh bare-self-call against the
        // owner — which doesn't declare AddFilterValueV2 (it lives
        // on the codeunit Get returns).
        var resolver = MakeResolver();
        resolver.AddType("MyHelper",
            new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));
        resolver.AddMember("MyHelper",
            new AlMember("Get", "procedure", "Codeunit", "Edit in Excel Fld Filter Impl."));
        resolver.AddType("Edit in Excel Fld Filter Impl.",
            new AlTypeRef(BaseAppId, "codeunit", 50100, "Edit in Excel Fld Filter Impl."));
        resolver.AddMember("Edit in Excel Fld Filter Impl.",
            new AlMember("AddFilterValueV2", "procedure", null, null));

        const string src = """
            codeunit 50000 MyHelper
            {
                procedure Run()
                begin
                    Get(ODataFieldName).AddFilterValueV2(FilterType, FilterValue);
                end;

                procedure Get(ODataFieldName: Text): Codeunit "Edit in Excel Fld Filter Impl."
                begin
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetMemberName == "AddFilterValueV2"
            && r.TargetObjectName == "Edit in Excel Fld Filter Impl.");
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "AddFilterValueV2");
    }

    [Fact]
    public void Microsoft_namespace_with_enum_value_continuation_silences_chain()
    {
        // RequisitionLine.Table.al's IsProdDemand:
        //   `Microsoft.Manufacturing.Document."Production Order Status"::Planned.AsInteger()`
        // The Microsoft-namespace static-receiver walker consumed the
        // dotted path through the quoted type but stopped at `::`,
        // leaving `Planned` to be re-dispatched as a fresh chain head
        // — Planned isn't a scope var or a catalog type, so the chain
        // fired head-not-a-variable. The fix continues the silent
        // walk past `::Value` (and any trailing `.AsInteger()` /
        // `.method(...)` chain), matching how enum-value references
        // surface elsewhere.
        var resolver = MakeResolver();
        const string src = """
            table 246 "Requisition Line"
            {
                procedure IsProdDemand(): Boolean
                begin
                    exit(
                        Microsoft.Manufacturing.Document."Production Order Status"::Planned.AsInteger() = 1);
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src,
            OwnerTable(resolver, "Requisition Line"));

        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "Planned" || s.Token == "AsInteger");
    }

    [Fact]
    public void Rec_field_with_same_name_as_catalog_codeunit_wins_chain_head()
    {
        // `Image.ImportFile(...)` inside `Customer.Table.al` —
        // `Image` is a Media-typed field on Customer AND the name
        // also matches the System Application's `Codeunit "Image"`.
        // Before the collision fix, the bare catalog lookup found
        // the codeunit and the `.ImportFile` chain step fired
        // chain-step unresolved (the System "Image" codeunit doesn't
        // expose ImportFile). AL itself resolves bare identifiers
        // against Rec fields first, so the field interpretation has
        // to win; the chain continues against the field's runtime
        // type (Media), which is in KnownSystemTypes — silenced.
        var resolver = MakeResolver();
        resolver.AddType("Image", new AlTypeRef(BaseAppId, "codeunit", 50100, "Image"));
        resolver.AddType("Customer", new AlTypeRef(BaseAppId, "table", 18, "Customer"));
        resolver.AddMember("Customer",
            new AlMember("Image", "table_field", null, null));

        const string src = """
            table 18 Customer
            {
                fields
                {
                    field(140; Image; Media) { }
                }

                procedure DoStuff()
                begin
                    Image.ImportFile('foo.png', '');
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src,
            OwnerTable(resolver, "Customer"));

        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "ImportFile");
    }

    [Fact]
    public void Report_rec_binds_to_first_dataitem_source_in_requestpage_chain()
    {
        // VAT Report Request Page / Whse. Change Unit of Measure
        // pattern: the requestpage's `field(Alias; Rec.<FieldName>)`
        // expressions read fields off the report's "primary" record,
        // which is the FIRST dataitem's source table. The walker used
        // to bind Rec to the report itself (DetermineRecBinding's
        // fallback for record-bearing owners that aren't tables /
        // pages), so every `Rec."<Field>"` chain in the requestpage
        // surfaced as chain-step unresolved with ReceiverKind=report.
        // The fix: prescan dataitems, take the first source as Rec.
        var resolver = MakeResolver();
        resolver.AddType("VAT Report Request Page",
            new AlTypeRef(BaseAppId, "report", 742, "VAT Report Request Page"));
        resolver.AddType("VAT Report Header",
            new AlTypeRef(BaseAppId, "table", 743, "VAT Report Header"));
        resolver.AddMember("VAT Report Header",
            new AlMember("Statement Template Name", "table_field", null, null));

        const string src = """
            report 742 "VAT Report Request Page"
            {
                dataset
                {
                    dataitem(VATReportHeader; "VAT Report Header")
                    {
                    }
                }
                requestpage
                {
                    layout
                    {
                        area(content)
                        {
                            field(VATStatementTemplate; Rec."Statement Template Name")
                            {
                            }
                        }
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src,
            OwnerReport(resolver, "VAT Report Request Page"));

        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "Statement Template Name");
    }

    [Fact]
    public void Interface_cast_with_as_keyword_routes_chain_through_target_interface()
    {
        // Canonical pattern from E-Document Core's Sent Document
        // Approval / Cancellation / Mark Fetched / Get Response
        // Runner codeunits:
        //   <c>exit((IDocumentSender as ISentDocumentActions).GetApprovalStatus(...))</c>.
        // Before the as-cast handler, the dispatch path walked past
        // the parenthesised cast and reached `GetApprovalStatus(` at
        // the end — that was then dispatched as a bare-self-call on
        // the owner codeunit, which doesn't declare GetApprovalStatus
        // (the codeunit implements other interfaces). The new
        // handler routes the chain step through the cast-target
        // interface so the reference lands on the interface's
        // declaration.
        var resolver = MakeResolver();
        resolver.AddType("ISentDocumentActions",
            new AlTypeRef(BaseAppId, "interface", 6200, "ISentDocumentActions"));
        resolver.AddMember("ISentDocumentActions",
            new AlMember("GetApprovalStatus", "procedure", null, null));

        const string src = """
            codeunit 50000 MyHelper
            {
                procedure Run(IDocumentSender: Variant): Boolean
                begin
                    exit((IDocumentSender as ISentDocumentActions).GetApprovalStatus(EDoc, Service, Ctx));
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.Stats.UnresolvedSamples.Should().BeEmpty();
        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetMemberName == "GetApprovalStatus"
            && r.TargetObjectName == "ISentDocumentActions");
    }

    [Fact]
    public void Xmlport_textattribute_silences_head_not_a_variable()
    {
        // `textattribute(Name)` and `textelement(Name)` declare XML
        // text nodes that show up in xmlport procedure bodies as
        // Text-typed variables. Without seeding them into the
        // outermost scope frame, every read / write of the name fires
        // head-not-a-variable. Pattern from
        // <c>ImportExportWorkflow.XmlPort.al</c>: <c>EventConditions</c>
        // declared via <c>textattribute(EventConditions)</c> and read
        // bare-style inside a trigger ~30 lines later.
        var resolver = MakeResolver();
        const string src = """
            xmlport 1502 "Import / Export Workflow"
            {
                schema
                {
                    textelement(Root)
                    {
                        textattribute(EventConditions)
                        {
                        }
                    }
                }
                trigger OnAfterImportRecord()
                begin
                    if EventConditions = 'X' then exit;
                    Message(EventConditions);
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src,
            OwnerXmlPort(resolver, "Import / Export Workflow"));

        result.Stats.UnresolvedSamples.Should().NotContain(s => s.Token == "EventConditions");
    }

    [Fact]
    public void Sibling_procedure_signature_var_modifier_does_not_eat_real_var_block()
    {
        // The canonical SalesPost.UpdateShippingNo shape: two
        // procedure signatures separated by `#if/#else/#endif`,
        // sharing a single var block + body. The walker walks the
        // FIRST signature, then needs to skip the second one to
        // reach the actual var block — but the second signature
        // contains `var SalesHeader: …` parameters, and an unguarded
        // search-for-var/begin treats that `var` modifier as the
        // start of THIS procedure's var block. ParseVarBlock then
        // consumes the second sig's params as locals (overwriting
        // the first's), recovers at `;`/`begin`, and discards the
        // ACTUAL var block (`NoSeries: Codeunit "No. Series"` etc.).
        // Body references to those locals fire head-not-a-variable.
        //
        // Fix: SkipBalancedParens when scanning for var/begin.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper",
            new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));
        resolver.AddType("No. Series",
            new AlTypeRef(BaseAppId, "codeunit", 1, "No. Series"));
        resolver.AddMember("No. Series",
            new AlMember("GetNextNo", "procedure", "Code", null));

        const string src = """
            codeunit 50000 MyHelper
            {
            #if not CLEAN24
                local procedure UpdateShippingNo(var SalesHeader: Record "Sales Header"; var ModifyHeader: Boolean; NoSeriesMgt: Codeunit "No. Series")
            #else
                local procedure UpdateShippingNo(var SalesHeader: Record "Sales Header"; var ModifyHeader: Boolean)
            #endif
                var
                    NoSeries: Codeunit "No. Series";
                begin
                    NoSeries.GetNextNo('SHIP', WorkDate());
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "No. Series"
            && r.TargetMemberName == "GetNextNo");
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "NoSeries");
    }

    // ── Object-scope var-block source-side fallback ─────────────────

    [Fact]
    public void Object_scope_global_codeunit_var_resolves_when_missing_from_symbol_package()
    {
        // Globals declared inside an `#if X / #endif` block drop out
        // of the symbol package whenever the live build doesn't define
        // the flag. The canonical BC pattern:
        //
        //   #if not CLEAN26
        //   var
        //       UpgradeTagDefinitions: Codeunit "Upgrade Tag Definitions";
        //   #endif
        //
        // The walker now scans the object-scope var block source-side
        // and seeds the bottom scope frame, so procedure-body uses of
        // the variable resolve through the same path as fields/locals
        // instead of firing head-not-a-variable.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper",
            new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));
        resolver.AddType("Upgrade Tag Definitions",
            new AlTypeRef(BaseAppId, "codeunit", 9999, "Upgrade Tag Definitions"));
        resolver.AddMember("Upgrade Tag Definitions",
            new AlMember("GetSomeTag", "procedure", "Code", null));

        // No globals in Ctx.GlobalVars — simulating a symbol package
        // that omitted UpgradeTagDefinitions (e.g. it lives behind a
        // feature flag the build didn't define).
        const string src = """
            codeunit 50000 MyHelper
            {
                var
                    UpgradeTagDefinitions: Codeunit "Upgrade Tag Definitions";

                procedure Run()
                begin
                    UpgradeTagDefinitions.GetSomeTag();
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        // GetSomeTag resolves against Upgrade Tag Definitions because
        // the source-side scanner seeded the bottom frame with the
        // variable's declared type.
        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Upgrade Tag Definitions"
            && r.TargetMemberName == "GetSomeTag");
        // No head-not-a-variable diagnostic for the var.
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "UpgradeTagDefinitions");
    }

    [Fact]
    public void Object_scope_var_block_does_not_overwrite_symbol_package_globals()
    {
        // Guard: source-side scanning is a GAP-FILL, not a replacement.
        // When the symbol package already supplied a binding for a name
        // (the normal case), the source-side pass must NOT overwrite it.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper",
            new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));
        resolver.AddType("Sales-Post",
            new AlTypeRef(BaseAppId, "codeunit", 80, "Sales-Post"));
        resolver.AddMember("Sales-Post",
            new AlMember("Run", "procedure", null, null));

        var globals = new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase)
        {
            // Symbol package's authoritative binding.
            ["SalesPost"] = new ResolvedVariableType("Codeunit", "Sales-Post"),
        };

        // Same var name in the source. Both should converge on the
        // same type, but the test verifies symbol-package data isn't
        // clobbered by source-side scanning.
        const string src = """
            codeunit 50000 MyHelper
            {
                var
                    SalesPost: Codeunit "Sales-Post";

                procedure Run()
                begin
                    SalesPost.Run();
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver, globals));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Sales-Post"
            && r.TargetMemberName == "Run");
    }

    // ── Single-statement `with X do <stmt>;` ────────────────────────


    [Fact]
    public void Single_statement_with_rebinds_rec_for_bare_call_in_inner_if()
    {
        // Canonical BC pattern from PaymentExportManagement (DK
        // module): `WITH GenJournalLine DO IF X THEN
        // InsertPaymentFileError(...);`. The single-statement WITH
        // form has no begin/end anchor so the older walker skipped
        // the frame push entirely; the bare InsertPaymentFileError
        // call then dispatched against the owner codeunit (which
        // doesn't expose it) and surfaced as bare-call unresolved.
        // The fix pre-scans for the matching `;` and pops the with-
        // frame at that position so subsequent statements aren't
        // affected.
        var resolver = MakeResolver();
        // The owner codeunit name must match OwnerCodeunit()'s default
        // ("MyHelper") so OwnerType() resolves; the bare-self-call
        // resolver early-exits when OwnerType is null and never
        // reaches the RecType fallback that the WITH frame sets up.
        resolver.AddType("MyHelper",
            new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));
        resolver.AddType("Gen. Journal Line",
            new AlTypeRef(BaseAppId, "table", 81, "Gen. Journal Line"));
        resolver.AddMember("Gen. Journal Line",
            new AlMember("InsertPaymentFileError", "procedure", null, null));

        const string src = """
            procedure Check(GenJournalLine: Record "Gen. Journal Line")
            begin
                WITH GenJournalLine DO
                    IF TRUE THEN
                        IF FALSE THEN BEGIN
                            IF TRUE THEN
                                InsertPaymentFileError('first');
                        END ELSE
                            IF TRUE THEN
                                InsertPaymentFileError('second');
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Where(r =>
                r.ReferenceKind == "method_call"
                && r.TargetObjectName == "Gen. Journal Line"
                && r.TargetMemberName == "InsertPaymentFileError")
            .Should().HaveCount(2);
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "InsertPaymentFileError");
    }

    [Fact]
    public void Single_statement_with_pops_at_terminating_semicolon()
    {
        // Guard: the with-frame must be popped at the FIRST semicolon
        // outside of any nested parens / brackets / begin-end. A bare
        // call AFTER the WITH's `;` must resolve against the owner,
        // NOT against the WITH-target's type.
        var resolver = MakeResolver();
        // Owner is "MyHelper" via OwnerCodeunit()'s default; it must
        // be in the catalog so OwnerType() resolves (bare-self-call
        // early-exits otherwise and never reaches the WITH-frame's
        // RecType binding).
        resolver.AddType("MyHelper",
            new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));
        // Sales Header DOES expose Foo; MyHelper does NOT.
        resolver.AddMember("Sales Header",
            new AlMember("Foo", "procedure", null, null));

        const string src = """
            procedure Run(SalesHeader: Record "Sales Header")
            begin
                WITH SalesHeader DO
                    Foo('inside');
                Foo('outside');
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        // First Foo() resolves on Sales Header (inside the WITH).
        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "Foo");
        // Second Foo() is OUTSIDE the WITH — owner doesn't expose Foo
        // and Sales Header is no longer the implicit receiver, so it
        // stays bare-call unresolved.
        result.Stats.UnresolvedSamples.Should().Contain(s =>
            s.Token == "Foo" && s.Reason == "bare-call");
    }

    // ── Owner-global / label fallback on chain steps ────────────────

    [Fact]
    public void This_dot_global_codeunit_var_continues_chain_against_var_type()
    {
        // `this.LogiqEDocumentManagement.Send(...)` from
        // LogiqIntegrationImpl.Codeunit.al. `this` resolves to the
        // owner codeunit; `.LogiqEDocumentManagement` is a global
        // codeunit-typed var on that owner. Globals don't live in
        // oe_module_members, so the catalog member lookup misses.
        // The fallback consults the bottom scope frame (Ctx.GlobalVars)
        // and advances the chain receiver to the var's type so the
        // following `.Send(...)` chain-step resolves normally.
        var resolver = MakeResolver();
        resolver.AddType("Logiq Integration Impl.",
            new AlTypeRef(BaseAppId, "codeunit", 6431, "Logiq Integration Impl."));
        resolver.AddType("Logiq EDocument Management",
            new AlTypeRef(BaseAppId, "codeunit", 6432, "Logiq EDocument Management"));
        resolver.AddMember("Logiq EDocument Management",
            new AlMember("Send", "procedure", null, null));

        var globals = new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase)
        {
            ["LogiqEDocumentManagement"] = new ResolvedVariableType("Codeunit", "Logiq EDocument Management"),
        };
        var ctx = new AlExtractContext(
            OwnerKind: "codeunit",
            OwnerName: "Logiq Integration Impl.",
            OwnerObjectId: 6431,
            OwnerAppId: BaseAppId,
            GlobalVars: globals,
            Resolver: resolver);

        const string src = """
            procedure Send(var EDocument: Record "E-Document"; var EDocumentService: Record "E-Document Service")
            begin
                this.LogiqEDocumentManagement.Send(EDocument, EDocumentService);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, ctx);

        // `.Send(...)` resolves against Logiq EDocument Management.
        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Logiq EDocument Management"
            && r.TargetMemberName == "Send");
        // No chain-step unresolved on LogiqEDocumentManagement.
        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "LogiqEDocumentManagement");
    }

    [Fact]
    public void This_dot_global_scalar_terminates_chain_silently()
    {
        // `this.CurrentStep` / `this.TopBannerVisible` from
        // AddUniversalPrintersWizard.Page.al. Both are scalar globals
        // (Option / Boolean) — no further chain. The chain step lookup
        // misses on the page object's catalog members, the fallback
        // matches the global, and the chain terminates without firing
        // an unresolved sample.
        var resolver = MakeResolver();
        resolver.AddType("Add Universal Printers Wizard",
            new AlTypeRef(BaseAppId, "page", 2752, "Add Universal Printers Wizard"));

        var globals = new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase)
        {
            ["CurrentStep"] = new ResolvedVariableType(string.Empty, "Option"),
            ["TopBannerVisible"] = new ResolvedVariableType(string.Empty, "Boolean"),
        };
        var ctx = new AlExtractContext(
            OwnerKind: "page",
            OwnerName: "Add Universal Printers Wizard",
            OwnerObjectId: 2752,
            OwnerAppId: BaseAppId,
            GlobalVars: globals,
            Resolver: resolver);

        const string src = """
            procedure CheckVisibility(): Boolean
            begin
                exit(this.TopBannerVisible and (this.CurrentStep = 0));
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, ctx);

        result.Stats.UnresolvedSamples.Should().NotContain(s =>
            s.Token == "CurrentStep" || s.Token == "TopBannerVisible");
    }

    [Fact]
    public void Owner_global_fallback_does_not_silence_unknown_member()
    {
        // Guard: when the chain step doesn't match a catalog member AND
        // isn't a known global, the unresolved sample still fires.
        var resolver = MakeResolver();
        resolver.AddType("My Helper",
            new AlTypeRef(BaseAppId, "codeunit", 50100, "My Helper"));

        var ctx = new AlExtractContext(
            OwnerKind: "codeunit",
            OwnerName: "My Helper",
            OwnerObjectId: 50100,
            OwnerAppId: BaseAppId,
            GlobalVars: new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: resolver);

        const string src = """
            procedure Run()
            begin
                this.TotallyUnknownMember();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, ctx);

        result.Stats.UnresolvedSamples.Should().Contain(s =>
            s.Token == "TotallyUnknownMember" && s.Reason == "chain-step");
    }

    // ── Kind-hint disambiguation (issue #712) ───────────────────────
    //
    // Every test below seeds two objects of DIFFERENT kinds under the
    // SAME name, with the wrong-kind candidate first, so a site that
    // drops its kind hint binds to the wrong object and fails here.

    [Fact]
    public void Database_typed_literal_binds_to_the_table_not_a_same_named_codeunit()
    {
        var resolver = MakeResolver();
        resolver.AddType("User", new AlTypeRef(BaseAppId, "codeunit", 4001, "User"));
        resolver.AddType("User", new AlTypeRef(BaseAppId, "table", 2000000120, "User"));

        const string src = """
            procedure Run()
            begin
                exit(Database::User);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        var reference = result.References.Should()
            .ContainSingle(r => r.TargetObjectName == "User").Subject;
        reference.TargetObjectKind.Should().Be("table");
        reference.ReferenceKind.Should().Be("property_object");
    }

    [Fact]
    public void Event_subscriber_binds_to_the_kind_named_in_the_attribute()
    {
        var resolver = MakeResolver();
        resolver.AddType("Doc. Post", new AlTypeRef(BaseAppId, "page", 6100, "Doc. Post"));
        resolver.AddType("Doc. Post", new AlTypeRef(BaseAppId, "codeunit", 6101, "Doc. Post"));

        const string src = """
            [EventSubscriber(ObjectType::Codeunit, Codeunit::"Doc. Post", 'OnAfterPost', '', false, false)]
            local procedure HandleAfterPost()
            begin
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        var reference = result.References.Should()
            .ContainSingle(r => r.ReferenceKind == "event_publisher").Subject;
        reference.TargetObjectName.Should().Be("Doc. Post");
        reference.TargetObjectKind.Should().Be("codeunit");
        reference.TargetMemberName.Should().Be("OnAfterPost");
    }

    [Fact]
    public void Source_table_binds_to_the_table_not_a_same_named_page()
    {
        // BC ships both a page and a table called "E-Document Service".
        // A page's SourceTable can only ever be the table.
        var resolver = MakeResolver();
        resolver.AddType("E-Document Service",
            new AlTypeRef(BaseAppId, "page", 6103, "E-Document Service"));
        resolver.AddType("E-Document Service",
            new AlTypeRef(BaseAppId, "table", 6103, "E-Document Service"));

        const string src = """
            page 50100 "Service Card"
            {
                SourceTable = "E-Document Service";
            }
            """;
        var ctx = OwnerPage(resolver, "Service Card", sourceTable: "E-Document Service");
        var result = AlReferenceExtractor.Extract(src, ctx);

        var reference = result.References.Should()
            .ContainSingle(r => r.ReferenceKind == "property_object").Subject;
        reference.TargetObjectName.Should().Be("E-Document Service");
        reference.TargetObjectKind.Should().Be("table");
    }

    [Fact]
    public void Table_relation_does_not_emit_a_reference_for_const_and_filter_values()
    {
        // `const(Item)` / `filter(Customer)` are option / enum VALUES.
        // They share a name with real tables, and emitting a reference
        // for them sends the reader to a table the field never relates
        // to — only the branch tables belong in the output.
        var resolver = MakeResolver();
        resolver.AddType("Item", new AlTypeRef(BaseAppId, "table", 27, "Item"));
        resolver.AddType("Resource", new AlTypeRef(BaseAppId, "table", 156, "Resource"));

        const string src = """
            table 50100 "Order Line"
            {
                fields
                {
                    field(1; "Source No."; Code[20])
                    {
                        TableRelation = if (Type = const(Item)) Resource."No."
                                        else if (Type = filter(Customer)) Resource."No.";
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(resolver, "Order Line"));

        var propertyTargets = result.References
            .Where(r => r.ReferenceKind == "property_object")
            .Select(r => r.TargetObjectName)
            .ToList();
        propertyTargets.Should().NotContain("Item");
        propertyTargets.Should().NotContain("Customer");
        propertyTargets.Should().Contain("Resource");
    }

    // ── Stub resolver ───────────────────────────────────────────────

    private sealed class StubResolver : IAlTypeResolver
    {
        // One name can carry several candidates — a table and the
        // tableextension extending it, or a codeunit and a page sharing
        // a name. Ranking them by the caller's kind hint the way the
        // production CatalogResolver does is what makes kind-hint bugs
        // visible in these tests rather than invisible (issue #712).
        private readonly Dictionary<string, List<AlTypeRef>> _types =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Kind, int ObjectId), AlTypeRef> _typesById = new();
        private readonly Dictionary<string, List<AlMember>> _members =
            new(StringComparer.OrdinalIgnoreCase);
        // baseName -> list of extension type names targeting it
        private readonly Dictionary<string, List<string>> _extensionsByBase =
            new(StringComparer.OrdinalIgnoreCase);

        public void AddType(string name, AlTypeRef type)
        {
            if (!_types.TryGetValue(name, out var candidates))
            {
                candidates = new List<AlTypeRef>();
                _types[name] = candidates;
            }
            // Re-adding the same (name, kind) replaces, so a test that
            // seeds a type twice keeps the last value; a different kind
            // is a second candidate the kind hint has to choose between.
            var existing = candidates.FindIndex(c =>
                string.Equals(c.Kind, type.Kind, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) candidates[existing] = type; else candidates.Add(type);
            if (type.ObjectId is int id && id > 0) _typesById[(type.Kind, id)] = type;
        }

        public void AddMember(string ownerName, AlMember member)
        {
            if (!_members.TryGetValue(ownerName, out var list))
            {
                list = new List<AlMember>();
                _members[ownerName] = list;
            }
            list.Add(member);
        }

        /// <summary>
        /// Declares that <paramref name="extensionName"/> is an extension of
        /// <paramref name="baseName"/>. Member lookups on the base will
        /// fall through to the extension's members when not found, and the
        /// returned AlMember carries the extension's AlTypeRef in
        /// <see cref="AlMember.DeclaringType"/> so the extractor stamps
        /// the reference at the extension.
        /// </summary>
        public void AddExtensionOf(string baseName, string extensionName)
        {
            if (!_extensionsByBase.TryGetValue(baseName, out var list))
            {
                list = new List<string>();
                _extensionsByBase[baseName] = list;
            }
            list.Add(extensionName);
        }

        /// <summary>
        /// Mirrors the production CatalogResolver's ranking, minus the
        /// app-visibility and obsolete-state tiers the stub has no data
        /// for: an exact kind match wins, then a non-extension kind,
        /// then whatever was seeded first.
        /// </summary>
        public AlTypeRef? ResolveTypeByName(string typeName, string? expectedKeyword = null)
        {
            if (!_types.TryGetValue(typeName, out var candidates) || candidates.Count == 0) return null;
            var expectedKind = AlKindKeywords.MapKeywordToKind(expectedKeyword);
            if (expectedKind is not null)
            {
                var exact = candidates.FirstOrDefault(c =>
                    string.Equals(c.Kind, expectedKind, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) return exact;
            }
            return candidates.FirstOrDefault(c => !AlKindKeywords.IsExtensionKind(c.Kind))
                ?? candidates[0];
        }

        public AlTypeRef? ResolveTypeByObjectId(int objectId, string? expectedKeyword = null)
        {
            var kind = AlKindKeywords.MapKeywordToKind(expectedKeyword);
            if (kind is null) return null;
            return _typesById.TryGetValue((kind, objectId), out var t) ? t : null;
        }

        public AlMember? ResolveMember(AlTypeRef owner, string memberName)
        {
            // Owner's own members win — matches the real CatalogResolver's
            // shadow semantics.
            if (_members.TryGetValue(owner.Name, out var ownList))
            {
                var direct = ownList.FirstOrDefault(m =>
                    string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
                if (direct is not null) return direct;
            }

            // Walk extensions.
            if (_extensionsByBase.TryGetValue(owner.Name, out var extensions))
            {
                foreach (var extName in extensions)
                {
                    if (!_members.TryGetValue(extName, out var extList)) continue;
                    var match = extList.FirstOrDefault(m =>
                        string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
                    if (match is null) continue;
                    var extType = _types.TryGetValue(extName, out var extCandidates)
                        ? extCandidates.FirstOrDefault(c => AlKindKeywords.IsExtensionKind(c.Kind))
                            ?? extCandidates.FirstOrDefault()
                        : null;
                    return match with { DeclaringType = extType };
                }
            }

            return null;
        }
    }
}
