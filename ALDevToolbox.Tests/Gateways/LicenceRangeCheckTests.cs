using ALDevToolbox.Services.Gateways;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// The licensed object range rule on its own (#1370): which objects of a build the
/// licence has to cover, against which ranges, and which ranges count. See
/// <c>.design/on-prem-gateway.md</c>, "Licensed object ranges, as built".
/// </summary>
public sealed class LicenceRangeCheckTests
{
    private static readonly Version Bc25 = new(25, 0, 12345, 0);
    private static readonly Version Bc14 = new(14, 0, 29530, 0);

    private static readonly LicenceRange[] Licence =
    [
        new("Table", 50000, 50099, "Direct"),
        new("TableData", 50000, 50199, "Direct Direct Direct Direct"),
        new("Page", 50000, 50149, null),
        new("Report", 50000, 50049, "Direct"),
        new("Codeunit", 50000, 50099, "Direct"),
        new("XMLport", 50000, 50009, "Direct"),
        new("Query", 50000, 50009, "Direct"),
    ];

    private static LicensedObject O(string kind, int id, string? name = null) => new(kind, id, name ?? $"{kind} {id}");

    [Fact]
    public void A_full_object_inside_its_range_is_not_flagged()
    {
        var (outside, checkedCount) = LicenceRangeCheck.Evaluate(
            [O("table", 50000), O("table", 50099), O("page", 50120), O("codeunit", 50050)], Licence, Bc25);

        outside.Should().BeEmpty();
        checkedCount.Should().Be(4);
    }

    [Fact]
    public void A_full_object_outside_its_range_is_flagged_with_the_ranges_it_missed()
    {
        var (outside, _) = LicenceRangeCheck.Evaluate([O("table", 50120, "CRONUS Rating")], Licence, Bc25);

        outside.Should().ContainSingle();
        outside[0].ObjectType.Should().Be("Table");
        outside[0].ObjectId.Should().Be(50120);
        outside[0].ObjectName.Should().Be("CRONUS Rating");
        outside[0].Message.Should().Be("Table 50120 \"CRONUS Rating\" is not in the licensed table range 50000-50099");
    }

    [Fact]
    public void Each_type_is_checked_against_its_own_ranges_only()
    {
        // 50120 is a licensed page id and a licensed table *data* id, but not a licensed
        // table: only the Table range covers a table object.
        var (outside, checkedCount) = LicenceRangeCheck.Evaluate(
            [O("page", 50120), O("table", 50120), O("report", 50060), O("query", 50005), O("xmlport", 50010), O("codeunit", 50000)],
            Licence, Bc25);

        checkedCount.Should().Be(6);
        outside.Select(f => (f.ObjectType, f.ObjectId)).Should().Equal(
            ("Table", 50120), ("Report", 50060), ("XMLport", 50010));
        outside.Single(f => f.ObjectType == "XMLport").Message.Should().Be("XMLport 50010 \"xmlport 50010\" is not in the licensed XMLport range 50000-50009");
    }

    [Fact]
    public void A_type_the_licence_has_no_range_for_says_so()
    {
        var ranges = new LicenceRange[] { new("Table", 50000, 50099, null) };

        var (outside, _) = LicenceRangeCheck.Evaluate([O("query", 50000, "Open Orders")], ranges, Bc25);

        outside.Single().Message.Should().Be("Query 50000 \"Open Orders\" is not in the licence, which has no query ranges");
    }

    [Fact]
    public void Enums_interfaces_and_extension_objects_are_never_flagged_on_a_current_version()
    {
        var (outside, checkedCount) = LicenceRangeCheck.Evaluate(
            [
                O("enum", 70000), O("interface", 70001), O("tableextension", 70002), O("pageextension", 70003),
                O("reportextension", 70004), O("enumextension", 70005), O("permissionsetextension", 70006),
                O("controladdin", 70007), O("profile", 70008),
            ],
            Licence, Bc25);

        outside.Should().BeEmpty();
        checkedCount.Should().Be(0);
    }

    [Fact]
    public void On_bc14_an_enum_outside_the_table_ranges_is_flagged_and_one_inside_is_not()
    {
        var (outside, checkedCount) = LicenceRangeCheck.Evaluate(
            [O("enum", 50010, "Rating Level"), O("enum", 50130, "Rating Source"), O("interface", 50131)], Licence, Bc14);

        checkedCount.Should().Be(2, "both enums, and not the interface");
        outside.Should().ContainSingle();
        outside[0].ObjectType.Should().Be("Enum");
        outside[0].Message.Should().Be("Enum 50130 \"Rating Source\" is not in the licensed table range 50000-50099, which BC 14 checks enums against");
    }

    [Fact]
    public void An_enum_is_not_checked_against_an_enum_range_on_a_current_version()
    {
        var (outside, _) = LicenceRangeCheck.Evaluate([O("enum", 90000)], [new("Enum", 50000, 50099, null)], Bc25);

        outside.Should().BeEmpty();
    }

    [Fact]
    public void Permission_sets_are_checked_only_when_the_licence_lists_permission_set_ranges()
    {
        var withoutRanges = LicenceRangeCheck.Evaluate([O("permissionset", 50100)], Licence, Bc25);
        withoutRanges.Outside.Should().BeEmpty();
        withoutRanges.Checked.Should().Be(0);

        var withRanges = LicenceRangeCheck.Evaluate(
            [O("permissionset", 50100), O("permissionset", 50001)], [.. Licence, new("PermissionSet", 50000, 50009, null)], Bc25);
        withRanges.Outside.Select(f => f.ObjectId).Should().Equal(50100);
        withRanges.Outside[0].Message.Should().StartWith("Permission set 50100");
    }

    [Fact]
    public void An_object_reported_twice_is_one_finding()
    {
        var (outside, checkedCount) = LicenceRangeCheck.Evaluate([O("table", 60000), O("TABLE", 60000)], Licence, Bc25);

        outside.Should().ContainSingle();
        checkedCount.Should().Be(1);
    }

    [Fact]
    public void A_long_list_of_ranges_is_shortened_in_the_message()
    {
        var ranges = Enumerable.Range(0, 6).Select(i => new LicenceRange("Table", 50000 + i * 100, 50009 + i * 100, null)).ToList();

        var (outside, _) = LicenceRangeCheck.Evaluate([O("table", 59999, "Far")], ranges, Bc25);

        outside.Single().Message.Should().Be("Table 59999 \"Far\" is not in the licensed table ranges 50000-50009, 50100-50109, 50200-50209, 50300-50309 and 2 more");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Direct", true)]
    [InlineData("Direct Direct Direct Direct", true)]
    [InlineData("RIMDX", true)]
    [InlineData("R----", true)]
    [InlineData("Indirect", true)]
    [InlineData("-----", false)]
    [InlineData("None", false)]
    [InlineData("None None None None None", false)]
    [InlineData("- - - - -", false)]
    [InlineData("Execute: No", false)]
    [InlineData("Read: No, Execute: No", false)]
    [InlineData("Read: No, Execute: Yes", true)]
    [InlineData("X: -", false)]
    public void A_range_counts_when_it_grants_any_permission_or_says_none_at_all(string? permissions, bool counts)
    {
        LicenceRangeCheck.Grants(permissions).Should().Be(counts);
    }

    [Fact]
    public void A_range_that_grants_nothing_does_not_cover_its_ids()
    {
        var ranges = new LicenceRange[] { new("Table", 50000, 50099, "None"), new("Table", 50100, 50199, "Direct") };

        var (outside, _) = LicenceRangeCheck.Evaluate([O("table", 50010), O("table", 50110)], ranges, Bc25);

        outside.Select(f => f.ObjectId).Should().Equal(50010);
        outside[0].Message.Should().EndWith("range 50100-50199", "only the ranges that count are named");
    }

    [Fact]
    public void Stored_checks_round_trip_and_cut_their_findings()
    {
        var findings = Enumerable.Range(0, LicenceRangeCheck.MaxStoredFindings + 5)
            .Select(i => new LicenceCheckFinding("Table", 60000 + i, null, $"Table {60000 + i} is outside the licence (50000-50099)"))
            .ToList();
        var check = new LicenceCheck(7, "Production", LicenceCheckOutcome.OutsideLicence, null, findings, findings.Count, 300, "25.0.1.0", DateTime.UtcNow);

        var read = LicenceRangeCheck.Parse(LicenceRangeCheck.Serialize([check]));

        read.Should().ContainSingle();
        read[0].IsOutside.Should().BeTrue();
        read[0].Findings.Should().HaveCount(LicenceRangeCheck.MaxStoredFindings);
        read[0].OutsideCount.Should().Be(findings.Count, "the count stays whole");
        LicenceRangeCheck.Serialize([]).Should().BeNull();
        LicenceRangeCheck.Parse("not json").Should().BeEmpty();
    }

    [Fact]
    public void A_bc14_enum_with_no_table_ranges_says_so()
    {
        var (outside, _) = LicenceRangeCheck.Evaluate([O("enum", 50130, "Source")], [new("Page", 50000, 50099, null)], Bc14);

        outside.Single().Message.Should().Be(
            "Enum 50130 \"Source\" is not in the licence, which has no table ranges for BC 14 to check enums against");
    }

    [Fact]
    public void The_refusal_names_the_objects_and_what_to_do()
    {
        var check = new LicenceCheck(7, "Production", LicenceCheckOutcome.OutsideLicence, null,
            [
                new("Table", 50120, "Rating", "Table 50120 \"Rating\" is not in the licensed table range 50000-50099"),
                new("Report", 50060, "List", "Report 50060 \"List\" is not in the licensed report range 50000-50049"),
            ], 2, 10, "25.0.1.0", DateTime.UtcNow);

        check.RefusalFor(42).Should().Be(
            "Build #42 can't be deployed to Production: 2 objects are outside the customer's licence there, so nothing was sent. "
            + "Table 50120 \"Rating\" is not in the licensed table range 50000-50099; Report 50060 \"List\" is not in the licensed report range 50000-50049. "
            + "Ask the customer to add these objects to their licence, or renumber them to object IDs the licence covers, then deploy again.");
    }

    [Fact]
    public void The_refusal_names_the_first_200_objects_and_counts_the_rest()
    {
        var findings = Enumerable.Range(0, 250)
            .Select(i => new LicenceCheckFinding("Table", 60000 + i, null, $"Table {60000 + i} is not in the licensed table range 50000-50099"))
            .ToList();
        var check = new LicenceCheck(7, "Production", LicenceCheckOutcome.OutsideLicence, null, findings, 250, 250, "25.0.1.0", DateTime.UtcNow);

        var refusal = check.RefusalFor(42);

        refusal.Should().Contain("Table 60199 is not in the licensed").And.NotContain("Table 60200 ")
            .And.Contain("Table 60199 is not in the licensed table range 50000-50099; and 50 more. Ask the customer");
    }

    [Fact]
    public void A_list_row_carries_the_outcome_and_summary_without_the_objects()
    {
        var check = new LicenceCheck(7, "Production", LicenceCheckOutcome.OutsideLicence, null,
            [new("Table", 50120, "Rating", "Table 50120 \"Rating\" is not in the licensed table range 50000-50099")], 1, 4, "25.0.1.0",
            new DateTime(2026, 10, 15, 9, 10, 0, DateTimeKind.Utc));

        var row = LicenceCheckRow.From(check);

        row.Should().Be(new LicenceCheckRow(7, "Production", LicenceCheckOutcome.OutsideLicence, 1,
            "Can't be deployed to Production: 1 object is outside the customer's licence.", check.CheckedAt));
    }

    [Fact]
    public void A_licence_with_a_line_too_long_to_read_or_too_many_ranges_has_incomplete_ranges()
    {
        const string Head = "Licensed to : CRONUS A/S\nTable 50000 50099 Direct\n";
        GatewayLicenceParser.Parse(Head).RangesComplete.Should().BeTrue();
        GatewayLicenceParser.Parse(Head + new string('x', GatewayLicenceParser.MaxLineChars + 1)).RangesComplete.Should().BeFalse();

        var many = string.Concat(Enumerable.Range(0, GatewayLicenceParser.MaxRanges + 1).Select(i => $"Page {i} {i} Direct\n"));
        GatewayLicenceParser.Parse(Head + many).RangesComplete.Should().BeFalse();
    }
}
