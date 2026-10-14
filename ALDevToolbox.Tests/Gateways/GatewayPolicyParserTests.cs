using ALWorkbench.Gateway.Core.Policy;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// Reading <c>gateway.toml</c>: the design's own example, the strictness that keeps a
/// typo from silently widening or narrowing access (unknown keys and sections, bad
/// values, references to tiers that are not listed, each with its line number), and
/// the effective permissions per tier (own value, else the kind's default, else
/// read-only; replaced whole, never merged). See <c>.design/on-prem-gateway.md</c>,
/// "The local policy file".
/// </summary>
public sealed class GatewayPolicyParserTests
{
    private const string Local = "CRONUS-BC01";

    /// <summary>The example from the design document, as customer IT would write it.</summary>
    internal const string DesignExample = """
        # Defaults per kind of environment; a tier inherits them unless it sets its own.
        [defaults.production]
        allow = ["inventory", "publish", "restart"]
        change_hours = "20:00-06:00"

        [defaults.test]
        allow = ["inventory", "publish", "unpublish", "restart", "service_control", "licence"]

        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC140"
        environment = "production"
        allow = ["inventory", "publish", "unpublish", "restart", "licence"]
        tenants = ["default"]
        publishers = ["CRONUS Partner"]
        allow_unsigned = true
        scope = "tenant"

        [[instance]]
        machine = "CRONUS-BC02"
        name = "BC140"
        environment = "production"
        allow = ["inventory", "restart"]

        [[database]]
        name = "CRONUS-SQL/BC140"
        publish_on = { machine = "CRONUS-BC01", instance = "BC140" }
        restart = [
          { machine = "CRONUS-BC01", instance = "BC140" },
          { machine = "CRONUS-BC02", instance = "BC140" },
        ]
        restart_mode = "stop-all-then-start-all"
        shared_restart = false
        licence_target = "NavDatabase"
        before = 'C:\ALWorkbench\before-deploy.ps1'
        after = 'C:\ALWorkbench\after-restart.ps1'

        [[machine]]
        name = "CRONUS-BC02"
        method = "service-manager"
        """;

    private static GatewayPolicy Valid(string toml)
    {
        var result = GatewayPolicyParser.Parse(toml, Local);
        result.Errors.Should().BeEmpty();
        result.IsValid.Should().BeTrue();
        return result.Policy!;
    }

    private static IReadOnlyList<string> Errors(string toml)
    {
        var result = GatewayPolicyParser.Parse(toml, Local);
        result.Policy.Should().BeNull("a file with mistakes is never used as written");
        result.Errors.Should().NotBeEmpty();
        return result.Errors;
    }

    [Fact]
    public void The_design_example_reads_as_written()
    {
        var policy = Valid(DesignExample);

        policy.Instances.Should().HaveCount(2);
        var bc01 = policy.Find(new TierRef("cronus-bc01", "bc140"))!;
        bc01.Environment.Should().Be(PolicyEnvironment.Production);
        bc01.Allow.Should().BeEquivalentTo([PolicyPermission.Inventory, PolicyPermission.Publish, PolicyPermission.Unpublish, PolicyPermission.Restart, PolicyPermission.Licence]);
        bc01.ChangeHours.Should().Be(new ChangeWindow(new TimeOnly(20, 0), new TimeOnly(6, 0)), "it sets no hours of its own, so the production default applies");
        bc01.Tenants.Should().Equal("default");
        bc01.Publishers.Should().Equal("CRONUS Partner");
        bc01.AllowUnsigned.Should().BeTrue();
        bc01.Scope.Should().Be(PublishScope.Tenant);

        var database = policy.Databases.Should().ContainSingle().Subject;
        database.PublishOn.Should().Be(new TierRef("CRONUS-BC01", "BC140"));
        database.Restart.Should().Equal(new TierRef("CRONUS-BC01", "BC140"), new TierRef("CRONUS-BC02", "BC140"));
        database.LicenceTarget.Should().Be(LicenceTarget.NavDatabase);
        database.Before.Should().Be(@"C:\ALWorkbench\before-deploy.ps1");
        policy.DatabaseOf(new TierRef("CRONUS-BC02", "BC140")).Should().BeSameAs(database);
        policy.Machines.Should().ContainSingle().Which.Method.Should().Be(MachineMethod.ServiceManager);
        policy.Scripts.Should().HaveCount(2);
    }

    [Fact]
    public void The_example_file_shipped_with_the_gateway_reads_without_mistakes_and_is_plain_ascii()
    {
        var path = ALDevToolbox.Tests.Infrastructure.RepoRoot.Combine("ALWorkbench.Gateway", "gateway.example.toml");
        var text = File.ReadAllText(path);

        var policy = Valid(text);

        policy.Instances.Should().HaveCount(3);
        policy.Find(new TierRef(Local, "BC140TEST"))!.Allow.Should().Contain(PolicyPermission.ServiceControl, "the test tier takes [defaults.test]");
        text.Should().MatchRegex(@"^[\x09\x0A\x0D\x20-\x7E]*$", "customer IT may open it in any editor");
        text.Should().NotContain("Acme");
    }

    [Fact]
    public void An_empty_file_is_valid_and_allows_nothing()
    {
        Valid("# nothing yet\n").Instances.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- effective permissions

    [Fact]
    public void A_tier_with_no_allow_and_no_default_is_read_only_with_no_hours_limit()
    {
        var tier = Valid("""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"
            """).Instances.Single();

        tier.Environment.Should().Be(PolicyEnvironment.Production, "production is the safe default");
        tier.Allow.Should().Equal(PolicyPermission.Inventory);
        tier.ChangeHours.Should().BeNull();
        tier.Tenants.Should().Equal("default");
        tier.Publishers.Should().BeNull();
        tier.AllowUnsigned.Should().BeFalse();
    }

    [Fact]
    public void A_tiers_own_allow_replaces_the_default_whole_and_its_hours_still_come_from_the_default()
    {
        var tier = Valid("""
            [defaults.production]
            allow = ["inventory", "publish", "restart"]
            change_hours = "20:00-06:00"

            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"
            allow = ["licence"]
            """).Instances.Single();

        tier.Allow.Should().Equal([PolicyPermission.Licence], "the tier's list replaces the default, it is not added to it");
        tier.ChangeHours.Should().Be(new ChangeWindow(new TimeOnly(20, 0), new TimeOnly(6, 0)));
    }

    [Fact]
    public void A_tiers_own_change_hours_replace_the_default_and_its_allow_comes_from_the_default()
    {
        var tier = Valid("""
            [defaults.production]
            allow = ["inventory", "publish"]
            change_hours = "20:00-06:00"

            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"
            change_hours = "22:00-23:00"
            """).Instances.Single();

        tier.Allow.Should().BeEquivalentTo([PolicyPermission.Inventory, PolicyPermission.Publish]);
        tier.ChangeHours.Should().Be(new ChangeWindow(new TimeOnly(22, 0), new TimeOnly(23, 0)));
    }

    [Fact]
    public void Defaults_apply_only_to_their_own_kind_of_environment()
    {
        var policy = Valid("""
            [defaults.production]
            allow = ["inventory", "publish"]
            change_hours = "20:00-06:00"

            [defaults.development]
            allow = ["inventory", "service_control"]

            [[instance]]
            machine = "CRONUS-BC01"
            name = "TEST"
            environment = "test"

            [[instance]]
            machine = "CRONUS-BC01"
            name = "DEV"
            environment = "development"
            """);

        var test = policy.Find(new TierRef(Local, "TEST"))!;
        test.Allow.Should().Equal([PolicyPermission.Inventory], "there is no [defaults.test], so a test tier is read-only");
        test.ChangeHours.Should().BeNull();
        policy.Find(new TierRef(Local, "DEV"))!.Allow.Should().BeEquivalentTo([PolicyPermission.Inventory, PolicyPermission.ServiceControl]);
    }

    [Fact]
    public void An_empty_allow_list_allows_nothing_not_even_inventory()
    {
        Valid("""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"
            allow = []
            """).Instances.Single().Allow.Should().BeEmpty();
    }

    [Fact]
    public void A_database_without_restart_restarts_only_its_publishing_tier()
    {
        Valid("""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"

            [[database]]
            name = "CRONUS-SQL/BC140"
            publish_on = { machine = "CRONUS-BC01", instance = "BC140" }
            """).Databases.Single().Restart.Should().Equal(new TierRef(Local, "BC140"));
    }

    // ---------------------------------------------------------------- strictness

    [Fact]
    public void An_unknown_key_is_a_mistake_with_its_line_number()
    {
        Errors("""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"
            allow_unsinged = true
            """).Should().ContainSingle().Which.Should().StartWith("Line 4: 'allow_unsinged' is not a setting of [[instance]]");
    }

    [Fact]
    public void An_unknown_section_a_single_bracket_instance_and_a_setting_outside_any_section_are_mistakes()
    {
        var errors = Errors("""
            change_hours = "20:00-06:00"

            [gateway]
            change_hours = "20:00-06:00"

            [instance]
            machine = "CRONUS-BC01"
            """);

        errors.Should().HaveCount(3);
        errors[0].Should().StartWith("Line 1: 'change_hours' is outside any section");
        errors[1].Should().StartWith("Line 3: [gateway] is not a section this file knows");
        errors[2].Should().StartWith("Line 6: Write [[instance]] with double brackets");
    }

    [Fact]
    public void A_dotted_key_is_a_mistake_rather_than_a_hidden_nested_setting()
    {
        Errors("""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"
            allow.extra = ["publish"]
            """).Should().ContainSingle().Which.Should().StartWith("Line 4: 'allow.extra'");
    }

    [Fact]
    public void A_database_that_names_a_tier_without_an_instance_section_is_a_mistake()
    {
        Errors("""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"

            [[database]]
            name = "CRONUS-SQL/BC140"
            publish_on = { machine = "CRONUS-BC01", instance = "BC140" }
            restart = [{ machine = "CRONUS-BC01", instance = "BC140" }, { machine = "CRONUS-BC01", instance = "BC150" }]
            """).Should().ContainSingle().Which.Should().Contain("CRONUS-BC01 / BC150, which has no [[instance]] section");
    }

    [Fact]
    public void Malformed_toml_is_reported_with_its_line_and_nothing_else_is_guessed()
    {
        var errors = Errors("""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140
            allow = ["inventory"]
            """);

        // The unclosed quote on line 3 is noticed where the next line no longer makes sense.
        errors.Should().OnlyContain(e => e.StartsWith("Line 3:", StringComparison.Ordinal) || e.StartsWith("Line 4:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_key_given_twice_is_a_mistake()
    {
        Errors("""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"
            allow = ["inventory"]
            allow = ["publish"]
            """).Should().ContainSingle().Which.Should().StartWith("Line 5:");
    }

    [Theory]
    [InlineData("environment = \"prod\"", "environment = \"prod\" is not one of the choices. Use \"production\", \"test\" or \"development\"")]
    [InlineData("allow = [\"publsh\"]", "\"publsh\" is not a permission")]
    [InlineData("scope = \"Tenant\"", "scope = \"Tenant\" is not one of the choices")]
    [InlineData("powershell = \"6\"", "powershell = \"6\" is not one of the choices. Use \"5.1\" or \"7\"")]
    [InlineData("allow_unsigned = \"yes\"", "allow_unsigned must be true or false")]
    [InlineData("tenants = []", "tenants is an empty list")]
    [InlineData("tenants = \"default\"", "tenants must be a list")]
    [InlineData("change_hours = \"25:00-06:00\"", "has a time that does not exist")]
    [InlineData("change_hours = \"20:00-20:00\"", "starts and ends at the same time")]
    [InlineData("change_hours = \"8-17\"", "is not a time window")]
    public void A_bad_instance_value_is_a_mistake_naming_the_choices(string line, string message)
    {
        Errors($"""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"
            {line}
            """).Should().ContainSingle().Which.Should().StartWith("Line 4:").And.Contain(message);
    }

    [Theory]
    [InlineData("restart_mode = \"rolling\"", "restart_mode = \"rolling\" is not one of the choices")]
    [InlineData("licence_target = \"navdatabase\"", "licence_target = \"navdatabase\" is not one of the choices")]
    [InlineData("before = 'before-deploy.ps1'", "before must be the full path of a .ps1 file on this computer")]
    [InlineData("after = '\\\\fileserver\\share\\after.ps1'", "after must be the full path of a .ps1 file on this computer")]
    [InlineData("before = 'C:\\ALWorkbench\\before-deploy.cmd'", "before must be the full path of a .ps1 file")]
    public void A_bad_database_value_is_a_mistake(string line, string message)
    {
        Errors($$"""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"

            [[database]]
            name = "CRONUS-SQL/BC140"
            publish_on = { machine = "CRONUS-BC01", instance = "BC140" }
            {{line}}
            """).Should().ContainSingle().Which.Should().StartWith("Line 8:").And.Contain(message);
    }

    [Fact]
    public void An_unknown_kind_of_default_and_a_bad_machine_method_are_mistakes()
    {
        var errors = Errors("""
            [defaults.staging]
            allow = ["inventory"]

            [[machine]]
            name = "CRONUS-BC02"
            method = "winrm"
            """);

        errors.Should().HaveCount(2);
        errors[0].Should().StartWith("Line 1: [defaults.staging] is not a kind of environment");
        errors[1].Should().StartWith("Line 6: method = \"winrm\" is not one of the choices. Use \"service-manager\" or \"powershell-remoting\"");
    }

    [Fact]
    public void A_tier_on_another_machine_needs_a_machine_section_and_this_computer_needs_none()
    {
        var errors = Errors("""
            [[instance]]
            machine = "CRONUS-BC02"
            name = "BC140"

            [[machine]]
            name = "cronus-bc01"
            method = "service-manager"
            """);

        errors.Should().HaveCount(2);
        errors.Should().Contain(e => e.StartsWith("Line 1:") && e.Contains("Add a [[machine]] section for CRONUS-BC02"));
        errors.Should().Contain(e => e.StartsWith("Line 5:") && e.Contains("is this computer"));
    }

    [Fact]
    public void The_same_tier_twice_and_a_tier_in_two_databases_are_mistakes()
    {
        var errors = Errors("""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"

            [[instance]]
            machine = "cronus-bc01"
            name = "bc140"

            [[database]]
            name = "A"
            publish_on = { machine = "CRONUS-BC01", instance = "BC140" }

            [[database]]
            name = "B"
            publish_on = { machine = "CRONUS-BC01", instance = "BC140" }
            """);

        errors.Should().Contain(e => e.StartsWith("Line 5:") && e.Contains("is listed twice"));
        errors.Should().Contain(e => e.StartsWith("Line 13:") && e.Contains("is in both \"A\" and \"B\""));
    }

    [Fact]
    public void Missing_required_settings_are_each_reported()
    {
        var errors = Errors("""
            [[instance]]
            name = "BC140"

            [[database]]
            name = "CRONUS-SQL/BC140"

            [[machine]]
            name = "CRONUS-BC02"
            """);

        errors.Should().Equal(
            "Line 1: This [[instance]] has no machine.",
            "Line 4: This [[database]] has no publish_on. Name the tier that publishes for it, for example publish_on = { machine = \"CRONUS-BC01\", instance = \"BC140\" }.",
            "Line 7: This [[machine]] has no method. Say how to reach the machine: method = \"service-manager\" or \"powershell-remoting\".");
    }

    [Fact]
    public void A_tier_reference_with_an_extra_or_missing_key_is_a_mistake()
    {
        var errors = Errors("""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"

            [[database]]
            name = "CRONUS-SQL/BC140"
            publish_on = { machine = "CRONUS-BC01", name = "BC140" }
            """);

        errors.Should().Contain(e => e.StartsWith("Line 7:") && e.Contains("'name' does not belong in publish_on"));
        errors.Should().Contain(e => e.StartsWith("Line 7:") && e.Contains("must give both machine and instance"));
    }
}
