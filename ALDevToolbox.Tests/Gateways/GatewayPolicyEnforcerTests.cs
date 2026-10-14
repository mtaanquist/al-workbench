using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Policy;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// The local policy check every command passes before anything is done for it: a
/// tier the file does not list is refused whatever AL Workbench sent, each verb needs
/// its permission on every tier it touches, publishers, unsigned apps and tenants are
/// checked, AI assistants cannot change production, and changes keep to
/// <c>change_hours</c> on the gateway's own wall clock, overnight windows and
/// daylight-saving changes included.
/// </summary>
public sealed class GatewayPolicyEnforcerTests
{
    /// <summary>A Monday at 12:00 UTC, which is 14:00 in Copenhagen.</summary>
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo Copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

    private const string Policy = """
        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC140"
        environment = "test"
        allow = ["inventory", "publish", "unpublish", "restart", "service_control", "licence", "scripts"]
        tenants = ["default", "cronus"]
        publishers = ["CRONUS Partner"]
        allow_unsigned = true

        [[instance]]
        machine = "CRONUS-BC02"
        name = "BC140"
        environment = "test"
        allow = ["inventory", "restart", "service_control"]

        [[instance]]
        machine = "CRONUS-BC01"
        name = "PROD"
        allow = ["inventory", "publish", "restart", "service_control"]
        change_hours = "20:00-06:00"

        [[instance]]
        machine = "CRONUS-BC01"
        name = "SIGNED"
        environment = "development"
        allow = ["inventory", "publish", "restart"]

        [[database]]
        name = "CRONUS-SQL/BC140"
        publish_on = { machine = "CRONUS-BC01", instance = "BC140" }
        restart = [
          { machine = "CRONUS-BC01", instance = "BC140" },
          { machine = "CRONUS-BC02", instance = "BC140" },
        ]
        licence_target = "NavDatabase"

        [[machine]]
        name = "CRONUS-BC02"
        method = "service-manager"
        """;

    private static readonly GatewayPolicy Parsed = GatewayPolicyParser.Parse(Policy, "CRONUS-BC01").Policy!;

    private static GatewayCommandMessage Command(GatewayVerb verb, string? machine, string? instance, bool byAgent = false, Action<JsonObject>? more = null)
    {
        var parameters = new JsonObject();
        if (machine is not null) parameters[GatewayCommandParameters.Machine] = machine;
        if (instance is not null) parameters[GatewayCommandParameters.Instance] = instance;
        more?.Invoke(parameters);
        return new GatewayCommandMessage { Id = 1, Verb = verb, Parameters = parameters, SecondsLeft = 600, StartedByAgent = byAgent };
    }

    private static PolicyDecision Check(GatewayCommandMessage command, DateTimeOffset? at = null, GatewayPolicy? policy = null)
    {
        var request = PolicyRequest.FromCommand(command, out var problem);
        if (request is null) return PolicyDecision.Refuse(problem!);
        return PolicyEnforcer.Check(policy ?? Parsed, request, at ?? Noon, Copenhagen);
    }

    private static JsonArray Apps(params (string Publisher, bool? Signed)[] apps) => new(apps.Select(a =>
    {
        var app = new JsonObject { [GatewayCommandParameters.AppPublisher] = a.Publisher };
        if (a.Signed is not null) app[GatewayCommandParameters.AppSigned] = a.Signed;
        return (JsonNode)app;
    }).ToArray());

    private static Action<JsonObject> WithApps(params (string Publisher, bool? Signed)[] apps) => p => p[GatewayCommandParameters.Apps] = Apps(apps);

    // ---------------------------------------------------------------- tiers not listed

    [Theory]
    [InlineData(GatewayVerb.RefreshInventory)]
    [InlineData(GatewayVerb.PublishInstall)]
    [InlineData(GatewayVerb.RestartGroup)]
    [InlineData(GatewayVerb.RestartInstance)]
    [InlineData(GatewayVerb.StopInstance)]
    [InlineData(GatewayVerb.StartInstance)]
    [InlineData(GatewayVerb.ImportLicence)]
    public void A_command_for_a_tier_the_file_does_not_list_is_refused_whatever_the_server_sent(GatewayVerb verb)
    {
        var decision = Check(Command(verb, "CRONUS-BC09", "BC140"));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be("CRONUS-BC09 / BC140 is not listed in the customer's gateway settings (gateway.toml), so the gateway does not touch it.");
    }

    [Fact]
    public void Tier_names_are_compared_without_regard_to_case_as_Windows_does()
    {
        Check(Command(GatewayVerb.RestartInstance, "cronus-bc01", "bc140")).Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_change_that_names_no_tier_is_refused_and_an_inventory_that_names_none_reads_the_allowed_tiers()
    {
        Check(Command(GatewayVerb.RestartInstance, null, null)).Reason.Should().Contain("does not say which service tier");
        Check(Command(GatewayVerb.RefreshInventory, null, null)).Allowed.Should().BeTrue();
        Check(Command(GatewayVerb.RefreshInventory, null, null), policy: GatewayPolicy.Empty).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Parameters_of_the_wrong_shape_refuse_the_command_rather_than_being_read_as_absent()
    {
        Check(Command(GatewayVerb.RestartInstance, null, null, more: p =>
        {
            p[GatewayCommandParameters.Machine] = 42;
            p[GatewayCommandParameters.Instance] = "BC140";
        })).Reason.Should().Contain("'machine' is not text");
        Check(Command(GatewayVerb.PublishInstall, "CRONUS-BC01", "BC140", more: p => p[GatewayCommandParameters.Apps] = "all"))
            .Reason.Should().Contain("not a list");
        Check(Command(GatewayVerb.PublishInstall, "CRONUS-BC01", "BC140", more: p => p[GatewayCommandParameters.UnpublishPrevious] = "yes"))
            .Allowed.Should().BeFalse();
        Check(Command(GatewayVerb.RestartInstance, "CRONUS-BC01", null)).Reason.Should().Contain("not both");
    }

    // ---------------------------------------------------------------- permissions per verb

    [Theory]
    [InlineData(GatewayVerb.RefreshInventory, "inventory")]
    [InlineData(GatewayVerb.PublishInstall, "publish")]
    [InlineData(GatewayVerb.RestartInstance, "service_control")]
    [InlineData(GatewayVerb.StopInstance, "service_control")]
    [InlineData(GatewayVerb.StartInstance, "service_control")]
    [InlineData(GatewayVerb.RestartGroup, "service_control")]
    public void Each_verb_is_refused_without_its_permission(GatewayVerb verb, string permission)
    {
        var policy = GatewayPolicyParser.Parse("""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"
            allow = []
            """, "CRONUS-BC01").Policy!;

        var decision = Check(Command(verb, "CRONUS-BC01", "BC140", more: WithApps(("CRONUS Partner", true))), policy: policy);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be($"Not allowed by the customer's gateway settings: CRONUS-BC01 / BC140 does not allow '{permission}'.");
    }

    [Fact]
    public void Each_verb_runs_with_its_permission()
    {
        foreach (var verb in Enum.GetValues<GatewayVerb>())
        {
            Check(Command(verb, "CRONUS-BC01", "BC140", more: WithApps(("CRONUS Partner", null))))
                .Should().Be(PolicyDecision.Allow, verb.ToString());
        }
    }

    [Fact]
    public void Import_licence_needs_licence_on_the_publishing_tier_and_a_licence_target()
    {
        var policy = GatewayPolicyParser.Parse("""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"
            allow = ["inventory", "restart"]

            [[instance]]
            machine = "CRONUS-BC01"
            name = "NOTARGET"
            allow = ["inventory", "licence", "restart"]

            [[database]]
            name = "CRONUS-SQL/BC140"
            publish_on = { machine = "CRONUS-BC01", instance = "BC140" }
            licence_target = "NavDatabase"
            """, "CRONUS-BC01").Policy!;

        Check(Command(GatewayVerb.ImportLicence, "CRONUS-BC01", "BC140"), policy: policy).Reason.Should().Contain("does not allow 'licence'");
        Check(Command(GatewayVerb.ImportLicence, "CRONUS-BC01", "NOTARGET"), policy: policy).Reason.Should().Contain("licence_target");
    }

    [Fact]
    public void A_deployment_needs_restart_on_every_tier_of_the_restart_group()
    {
        var policy = GatewayPolicyParser.Parse(Policy.Replace("""
            name = "BC140"
            environment = "test"
            allow = ["inventory", "restart", "service_control"]
            """, """
            name = "BC140"
            environment = "test"
            allow = ["inventory", "service_control"]
            """, StringComparison.Ordinal), "CRONUS-BC01").Policy!;

        Check(Command(GatewayVerb.PublishInstall, "CRONUS-BC01", "BC140", more: WithApps(("CRONUS Partner", null))), policy: policy)
            .Reason.Should().Be("Not allowed by the customer's gateway settings: CRONUS-BC02 / BC140 does not allow 'restart'.");
    }

    [Fact]
    public void A_deployment_goes_through_the_databases_publishing_tier_only()
    {
        Check(Command(GatewayVerb.PublishInstall, "CRONUS-BC02", "BC140", more: WithApps(("CRONUS Partner", null))))
            .Reason.Should().Be("Not allowed by the customer's gateway settings: Apps for the database \"CRONUS-SQL/BC140\" go through CRONUS-BC01 / BC140, not CRONUS-BC02 / BC140.");
    }

    [Fact]
    public void Unpublishing_previous_versions_needs_unpublish_and_named_scripts_need_scripts()
    {
        Check(Command(GatewayVerb.PublishInstall, "CRONUS-BC01", "PROD", more: p => p[GatewayCommandParameters.UnpublishPrevious] = true), at: Noon.AddHours(10))
            .Reason.Should().Contain("does not allow 'unpublish'");

        var withScripts = GatewayPolicyParser.Parse("""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"
            allow = ["publish", "restart"]

            [[database]]
            name = "CRONUS-SQL/BC140"
            publish_on = { machine = "CRONUS-BC01", instance = "BC140" }
            before = 'C:\ALWorkbench\before-deploy.ps1'
            """, "CRONUS-BC01").Policy!;
        Check(Command(GatewayVerb.PublishInstall, "CRONUS-BC01", "BC140"), policy: withScripts)
            .Reason.Should().Contain("does not allow 'scripts'");
    }

    // ---------------------------------------------------------------- publishers, signing, tenants

    [Fact]
    public void An_app_from_a_publisher_the_tier_does_not_list_is_refused()
    {
        Check(Command(GatewayVerb.PublishInstall, "CRONUS-BC01", "BC140", more: WithApps(("CRONUS Partner", null), ("Other Partner", null))))
            .Reason.Should().Be("Not allowed by the customer's gateway settings: CRONUS-BC01 / BC140 does not take apps from the publisher 'Other Partner'.");
        Check(Command(GatewayVerb.PublishInstall, "CRONUS-BC01", "BC140", more: WithApps(("cronus partner ", null))))
            .Allowed.Should().BeTrue("the comparison ignores case and surrounding spaces");
    }

    [Fact]
    public void An_unsigned_app_is_refused_unless_the_tier_allows_unsigned_apps()
    {
        Check(Command(GatewayVerb.PublishInstall, "CRONUS-BC01", "SIGNED", more: WithApps(("CRONUS Partner", null))))
            .Reason.Should().Be("Not allowed by the customer's gateway settings: CRONUS-BC01 / SIGNED does not take unsigned apps.");
        Check(Command(GatewayVerb.PublishInstall, "CRONUS-BC01", "SIGNED", more: WithApps(("CRONUS Partner", true))))
            .Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_tenant_the_tier_does_not_list_is_refused_and_no_tenant_means_default()
    {
        Check(Command(GatewayVerb.PublishInstall, "CRONUS-BC01", "BC140", more: p => p[GatewayCommandParameters.Tenant] = "fabrikam"))
            .Reason.Should().Be("Not allowed by the customer's gateway settings: CRONUS-BC01 / BC140 may not act on tenant 'fabrikam'.");
        Check(Command(GatewayVerb.PublishInstall, "CRONUS-BC01", "BC140", more: p => p[GatewayCommandParameters.Tenant] = "CRONUS"))
            .Allowed.Should().BeTrue();
        Check(Command(GatewayVerb.PublishInstall, "CRONUS-BC01", "SIGNED", more: WithApps(("Anyone", true))))
            .Allowed.Should().BeTrue("a tier without a tenants line takes tenant 'default', and without publishers any publisher");
    }

    // ---------------------------------------------------------------- AI assistants

    [Fact]
    public void An_AI_assistant_cannot_change_a_production_tier_but_may_read_it_and_change_test()
    {
        var night = Noon.AddHours(10);
        Check(Command(GatewayVerb.RestartInstance, "CRONUS-BC01", "PROD", byAgent: true), night)
            .Reason.Should().Be("CRONUS-BC01 / PROD is a production service tier, and AI assistants cannot change on-premises production. Start this from AL Workbench in the browser.");
        Check(Command(GatewayVerb.RestartInstance, "CRONUS-BC01", "PROD", byAgent: false), night).Allowed.Should().BeTrue();
        Check(Command(GatewayVerb.RefreshInventory, "CRONUS-BC01", "PROD", byAgent: true), night).Allowed.Should().BeTrue();
        Check(Command(GatewayVerb.RestartInstance, "CRONUS-BC01", "BC140", byAgent: true)).Allowed.Should().BeTrue();
    }

    // ---------------------------------------------------------------- change hours

    [Fact]
    public void A_change_outside_the_hours_is_refused_and_a_read_is_not()
    {
        // 14:00 in Copenhagen; the window is 20:00-06:00.
        Check(Command(GatewayVerb.RestartInstance, "CRONUS-BC01", "PROD"))
            .Reason.Should().Be("Outside the hours set on the customer's gateway: CRONUS-BC01 / PROD allows changes only from 20:00 to 06:00, local time.");
        Check(Command(GatewayVerb.RefreshInventory, "CRONUS-BC01", "PROD")).Allowed.Should().BeTrue();
    }

    [Theory]
    // 2026-10-12 is CEST, UTC+2.
    [InlineData(17, 59, false)] // 19:59 local
    [InlineData(18, 0, true)]   // 20:00 local: the start is inside
    [InlineData(21, 30, true)]  // 23:30 local
    [InlineData(22, 0, true)]   // 00:00 local, the next day
    [InlineData(3, 59, true)]   // 05:59 local
    [InlineData(4, 0, false)]   // 06:00 local: the end is outside
    public void An_overnight_window_spans_midnight_with_the_start_inside_and_the_end_outside(int utcHour, int utcMinute, bool inside)
    {
        var at = new DateTimeOffset(2026, 10, utcHour >= 12 ? 12 : 13, utcHour, utcMinute, 0, TimeSpan.Zero);
        Check(Command(GatewayVerb.RestartInstance, "CRONUS-BC01", "PROD"), at).Allowed.Should().Be(inside);
    }

    [Fact]
    public void A_daytime_window_does_not_run_overnight()
    {
        var window = new ChangeWindow(new TimeOnly(8, 0), new TimeOnly(17, 0));
        window.Contains(new TimeOnly(8, 0)).Should().BeTrue();
        window.Contains(new TimeOnly(16, 59)).Should().BeTrue();
        window.Contains(new TimeOnly(17, 0)).Should().BeFalse();
        window.Contains(new TimeOnly(2, 0)).Should().BeFalse();
    }

    [Fact]
    public void The_window_follows_the_wall_clock_across_daylight_saving_changes()
    {
        var window = new ChangeWindow(new TimeOnly(2, 0), new TimeOnly(3, 0));

        // 25 October 2026: clocks go back at 03:00 CEST to 02:00 CET, so 02:30 happens twice.
        window.Contains(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), Copenhagen).Should().BeTrue("02:30 CEST");
        window.Contains(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), Copenhagen).Should().BeTrue("02:30 CET, the second time");
        window.Contains(new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.Zero), Copenhagen).Should().BeFalse("03:00 CET");

        // 29 March 2026: clocks go forward at 02:00 CET to 03:00 CEST; the hour never comes.
        window.Contains(new DateTimeOffset(2026, 3, 29, 0, 59, 0, TimeSpan.Zero), Copenhagen).Should().BeFalse("01:59 CET");
        window.Contains(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), Copenhagen).Should().BeFalse("03:00 CEST");

        // The same moment in summer and in winter: the window is local, not UTC.
        var summer = new ChangeWindow(new TimeOnly(20, 0), new TimeOnly(21, 0));
        summer.Contains(new DateTimeOffset(2026, 7, 1, 18, 30, 0, TimeSpan.Zero), Copenhagen).Should().BeTrue("20:30 CEST");
        summer.Contains(new DateTimeOffset(2026, 1, 1, 18, 30, 0, TimeSpan.Zero), Copenhagen).Should().BeFalse("19:30 CET");
    }

    [Fact]
    public void Every_tier_a_deployment_restarts_must_be_inside_its_own_hours()
    {
        var policy = GatewayPolicyParser.Parse("""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140"
            environment = "test"
            allow = ["publish", "restart"]
            allow_unsigned = true

            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC140B"
            environment = "test"
            allow = ["restart"]
            change_hours = "20:00-06:00"

            [[database]]
            name = "CRONUS-SQL/BC140"
            publish_on = { machine = "CRONUS-BC01", instance = "BC140" }
            restart = [{ machine = "CRONUS-BC01", instance = "BC140" }, { machine = "CRONUS-BC01", instance = "BC140B" }]
            """, "CRONUS-BC01").Policy!;

        Check(Command(GatewayVerb.PublishInstall, "CRONUS-BC01", "BC140"), policy: policy)
            .Reason.Should().StartWith("Outside the hours set on the customer's gateway: CRONUS-BC01 / BC140B");
    }

    // ---------------------------------------------------------------- read-only after a mistake

    [Fact]
    public void The_read_only_form_of_a_policy_keeps_inventory_and_nothing_else()
    {
        var readOnly = Parsed.ReadOnly();

        readOnly.Instances.Should().OnlyContain(i => i.Allow.SetEquals(new[] { PolicyPermission.Inventory }));
        Check(Command(GatewayVerb.RefreshInventory, "CRONUS-BC01", "BC140"), policy: readOnly).Allowed.Should().BeTrue();
        Check(Command(GatewayVerb.RestartInstance, "CRONUS-BC01", "BC140"), policy: readOnly).Allowed.Should().BeFalse();
    }
}
