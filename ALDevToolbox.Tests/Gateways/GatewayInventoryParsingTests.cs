using System.Text.Json;
using System.Text.Json.Nodes;
using ALDevToolbox.Services.Gateways;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Inventory;
using ALWorkbench.Gateway.Core.Policy;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// Reading what the inventory script prints (<see cref="InventoryNormaliser"/>) and the
/// licence text (<see cref="GatewayLicenceParser"/>), against the samples in
/// <c>InventorySamples/</c>. Those are synthetic until replaced with captures from our
/// own servers; see the README there.
/// </summary>
public sealed class GatewayInventoryParsingTests
{
    internal const string PolicyText = """
        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC140"
        environment = "production"
        allow = ["inventory"]

        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC250"
        environment = "test"
        allow = ["inventory", "session_users"]

        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC140TEST"
        environment = "development"
        allow = ["inventory"]

        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC260"
        allow = []
        """;

    internal static readonly GatewayPolicy Policy = GatewayPolicyParser.Parse(PolicyText, "CRONUS-BC01").Policy!;

    internal static EffectiveInstance Tier(string name) => Policy.Find(new TierRef("CRONUS-BC01", name))!;

    internal static JsonNode Sample(string file) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Gateways", "InventorySamples", file)))!;

    // ---------------------------------------------------------------- the script's output

    [Fact]
    public void A_bc14_tier_read_with_windows_powershell_is_read_with_its_unavailable_parts()
    {
        var inventory = InventoryNormaliser.FromScriptOutput(Sample("bc14-hybrid.json"), Tier("BC140"));

        inventory.Error.Should().BeNull();
        inventory.Machine.Should().Be("CRONUS-BC01");
        inventory.Name.Should().Be("BC140");
        inventory.Environment.Should().Be("production");
        inventory.PowerShell.Should().Be("5.1");
        inventory.State.Should().Be("Running");
        inventory.Version.Should().Be("14.0.47786.0");
        inventory.Service!.StartMode.Should().Be("Auto");
        inventory.Configuration!.DatabaseServer.Should().Be("CRONUS-SQL");
        inventory.Configuration.DatabaseInstance.Should().Be("BCDEMO");
        inventory.Configuration.DatabaseName.Should().Be("Demo Database BC (14-0)");
        inventory.Configuration.Multitenant.Should().BeFalse();
        inventory.Configuration.ClientServicesPort.Should().Be(7046);
        inventory.Configuration.DeveloperServicesPort.Should().Be(7049);
        inventory.Configuration.PublicWebBaseUrl.Should().BeNull();

        // The tenant list arrived wrapped as {"value": [...], "Count": 1}.
        var tenant = inventory.Tenants.Should().ContainSingle().Subject;
        tenant.Id.Should().Be("default");
        tenant.SessionCount.Should().Be(3);
        tenant.Apps.Should().HaveCount(4);
        tenant.Apps!.Should().OnlyContain(a => a.DataVersion == null && a.SyncState == null);
        tenant.Apps!.Where(a => a.AppId == "8c1a6b1e-4a35-4b5a-9a8f-0c7d2e3f4a51").Select(a => (a.Version, a.IsInstalled))
            .Should().Equal(("2.3.0.12", true), ("2.4.0.3", false));
        inventory.Unavailable.Should().ContainKey(GatewayInventorySections.AppTenantProperties);
        inventory.SessionCount.Should().Be(3);
        inventory.Licence!.Text.Should().Contain("Object Assignment");
    }

    [Fact]
    public void A_current_multitenant_tier_is_read_with_data_versions_and_sync_states()
    {
        var inventory = InventoryNormaliser.FromScriptOutput(Sample("bc25-multitenant.json"), Tier("BC250"));

        inventory.PowerShell.Should().Be("7");
        inventory.Environment.Should().Be("test");
        inventory.Configuration!.Multitenant.Should().BeTrue();
        inventory.Configuration.PublicWebBaseUrl.Should().Be("https://bc.cronus.example/BC250");
        inventory.Tenants!.Select(t => t.Id).Should().Equal("cronus-dk", "cronus-se");
        var freight = inventory.Tenants![1].Apps!.Single(a => a.Name == "CRONUS Freight Extension");
        (freight.Version, freight.DataVersion, freight.Scope, freight.SyncState, freight.IsPublished)
            .Should().Be(("3.1.0.7", "3.0.0.2", "Tenant", "Synced", (bool?)true));
        inventory.Unavailable.Should().BeEmpty();
    }

    [Fact]
    public void A_stopped_tier_keeps_what_it_could_read_and_says_why_the_rest_is_missing()
    {
        var inventory = InventoryNormaliser.FromScriptOutput(Sample("bc14-stopped.json"), Tier("BC140TEST"));

        inventory.Error.Should().BeNull();
        inventory.State.Should().Be("Stopped");
        inventory.Configuration!.DatabaseName.Should().Be("Test Database BC (14-0)");
        inventory.Tenants.Should().BeNull();
        inventory.Licence.Should().BeNull();
        inventory.SessionCount.Should().BeNull();
        inventory.Unavailable.Keys.Should().BeEquivalentTo(["tenants", "apps", "sessions", "licence"]);
    }

    [Fact]
    public void Session_user_names_are_kept_only_where_the_policy_allows_session_users()
    {
        var sample = Sample("bc14-hybrid.json");

        InventoryNormaliser.FromScriptOutput(sample, Tier("BC140")).SessionUsers
            .Should().BeNull("BC140 allows inventory only, whatever the script returned");
        InventoryNormaliser.FromScriptOutput(sample, Tier("BC250")).SessionUsers
            .Should().Equal("CRONUS\\ANNA", "CRONUS\\BO", "CRONUS\\SVEN");
    }

    [Fact]
    public void The_script_is_asked_for_session_users_only_where_the_policy_allows_them()
    {
        InventoryScript.Parameters("BC140", @"C:\BC\140\Service", Tier("BC140").Allows(PolicyPermission.SessionUsers))["includeSessionUsers"]!
            .GetValue<bool>().Should().BeFalse();
        InventoryScript.Parameters("BC250", @"C:\BC\250\Service", Tier("BC250").Allows(PolicyPermission.SessionUsers))["includeSessionUsers"]!
            .GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Lists_over_the_limits_are_cut_and_flagged()
    {
        var apps = new JsonArray(Enumerable.Range(0, GatewayInventoryLimits.MaxAppsPerTenant + 5)
            .Select(i => (JsonNode)new JsonObject { ["appId"] = Guid.NewGuid().ToString(), ["name"] = $"App {i}", ["publisher"] = "CRONUS", ["version"] = "1.0.0.0" })
            .ToArray());
        var tenants = new JsonArray(Enumerable.Range(0, GatewayInventoryLimits.MaxTenantsPerInstance + 2)
            .Select(i => (JsonNode)new JsonObject { ["id"] = $"tenant{i}", ["apps"] = i == 0 ? apps : null })
            .ToArray());
        var output = new JsonObject
        {
            ["schema"] = 1,
            ["state"] = "Running",
            ["tenants"] = tenants,
            ["licence"] = new JsonObject { ["text"] = new string('x', GatewayInventoryLimits.MaxLicenceTextChars + 10), ["truncated"] = false },
        };

        var inventory = InventoryNormaliser.FromScriptOutput(output, Tier("BC250"));

        inventory.Tenants.Should().HaveCount(GatewayInventoryLimits.MaxTenantsPerInstance);
        inventory.TenantsTruncated.Should().BeTrue();
        inventory.Tenants![0].Apps.Should().HaveCount(GatewayInventoryLimits.MaxAppsPerTenant);
        inventory.Tenants[0].AppsTruncated.Should().BeTrue();
        inventory.Tenants[1].Apps.Should().BeNull("a tenant whose apps were not read is not an empty list");
        inventory.Licence!.Text.Should().HaveLength(GatewayInventoryLimits.MaxLicenceTextChars);
        inventory.Licence.Truncated.Should().BeTrue();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"not an object\"")]
    [InlineData("{\"schema\": 2, \"state\": \"Running\"}")]
    public void Output_the_gateway_cannot_read_becomes_a_tier_that_was_not_read(string json)
    {
        var inventory = InventoryNormaliser.FromScriptOutput(JsonNode.Parse(json), Tier("BC140"));

        inventory.Error.Should().NotBeNullOrEmpty();
        inventory.Name.Should().Be("BC140");
        inventory.State.Should().BeNull();
    }

    // ---------------------------------------------------------------- the licence

    [Fact]
    public void A_bc14_licence_is_read_into_ranges_by_object_type()
    {
        var text = Sample("bc14-hybrid.json")["licence"]!["text"]!.GetValue<string>();

        var licence = GatewayLicenceParser.Parse(text);

        licence.Licensee.Should().Be("CRONUS International Ltd.");
        licence.SerialNumber.Should().Be("6312345");
        licence.ExpiresOn.Should().BeNull("this licence states no expiry date");
        licence.Problems.Should().BeEmpty();
        licence.Ranges.Select(r => (r.ObjectType, r.FromId, r.ToId)).Should().Equal(
            ("TableData", 50000, 50099),
            ("Table", 50000, 50099),
            ("Page", 50000, 50099),
            ("Report", 50000, 50049),
            ("Codeunit", 50000, 50099),
            ("XMLport", 50000, 50009),
            ("Query", 50000, 50009),
            ("MenuSuite", 1050, 1059));
        licence.Ranges[0].Permissions.Should().Contain("Direct");
    }

    [Fact]
    public void A_current_licence_is_read_with_its_expiry_and_dotted_ranges()
    {
        var text = Sample("bc25-multitenant.json")["licence"]!["text"]!.GetValue<string>();

        var licence = GatewayLicenceParser.Parse(text);

        licence.Licensee.Should().Be("CRONUS Danmark A/S");
        licence.SerialNumber.Should().Be("7654321");
        licence.ExpiresOn.Should().Be(new DateOnly(2026, 12, 31));
        licence.Ranges.Should().Contain(new LicenceRange("Codeunit", 50000, 50199, null));
        licence.Ranges.Select(r => r.ObjectType).Should().OnlyHaveUniqueItems();
        licence.Problems.Should().BeEmpty();
    }

    [Theory]
    [InlineData("2026-12-31", 2026, 12, 31)]
    [InlineData("31-12-2026", 2026, 12, 31)]
    [InlineData("31.12.2026", 2026, 12, 31)]
    [InlineData("31/12/2026", 2026, 12, 31)]
    [InlineData("12/31/2026", 2026, 12, 31)]
    [InlineData("31-12-2026 (in 30 days)", 2026, 12, 31)]
    public void Expiry_dates_are_read_in_the_layouts_licences_use(string text, int year, int month, int day)
    {
        GatewayLicenceParser.ParseDate(text, out var problem).Should().Be(new DateOnly(year, month, day));
        problem.Should().BeNull();
    }

    [Fact]
    public void A_date_that_reads_two_ways_is_reported_rather_than_guessed()
    {
        GatewayLicenceParser.ParseDate("03/04/2026", out var problem).Should().BeNull();
        problem.Should().Contain("day/month or month/day");
        GatewayLicenceParser.ParseDate("Never", out var none).Should().BeNull();
        none.Should().BeNull();
    }

    [Fact]
    public void Licence_text_that_cannot_be_read_says_so_and_does_not_throw()
    {
        var licence = GatewayLicenceParser.Parse("Nothing useful here.\nExpiration Date : sometime");

        licence.Licensee.Should().BeNull();
        licence.Ranges.Should().BeEmpty();
        licence.Problems.Should().HaveCount(3);
    }

    [Fact]
    public void A_very_long_line_is_skipped_quickly_instead_of_holding_the_request()
    {
        var text = "Licensed to : CRONUS Danmark A/S\nTable 1" + new string(' ', 20_000) + "\nTable 50000..50099\n";
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var licence = GatewayLicenceParser.Parse(text);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
        licence.Licensee.Should().Be("CRONUS Danmark A/S");
        licence.Ranges.Should().ContainSingle().Which.Should().Be(new LicenceRange("Table", 50000, 50099, null));
        licence.Problems.Should().ContainSingle().Which.Should().Contain("too long");
    }

    [Theory]
    [InlineData("01/13/10000")]
    [InlineData("13/01/99999")]
    [InlineData("0001-01-01")]
    [InlineData("01.01.1800")]
    public void A_year_no_licence_states_is_reported_rather_than_failing(string text)
    {
        var act = () => GatewayLicenceParser.ParseDate(text, out _);

        act.Should().NotThrow();
        GatewayLicenceParser.ParseDate(text, out var problem).Should().BeNull();
        problem.Should().Contain("could not be read");
        GatewayLicenceParser.Parse("Licensed to : CRONUS\nExpiration Date : " + text).Problems.Should().Contain(p => p.Contains("could not be read"));
    }

    [Fact]
    public void Tenants_the_script_stopped_at_the_limit_are_flagged_as_cut()
    {
        var output = new JsonObject
        {
            ["schema"] = 1,
            ["state"] = "Running",
            ["tenants"] = new JsonArray(new JsonObject { ["id"] = "cronus-dk" }, new JsonObject { ["id"] = "cronus-se" }),
            ["tenantsTruncated"] = true,
        };

        var inventory = InventoryNormaliser.FromScriptOutput(output, Tier("BC250"));

        inventory.Tenants.Should().HaveCount(2);
        inventory.TenantsTruncated.Should().BeTrue("a list the script cut must not have the rest marked missing");
        output["tenantsTruncated"] = false;
        InventoryNormaliser.FromScriptOutput(output, Tier("BC250")).TenantsTruncated.Should().BeFalse();
    }

    // ---------------------------------------------------------------- a round that AL Workbench refuses in part

    private static Func<GatewayInventoryReport, CancellationToken, Task> Refusing(string instance, Exception refusal, List<GatewayInventoryReport> taken) =>
        (report, _) =>
        {
            if (report.Instances.Any(i => i.Name == instance)) throw refusal;
            taken.Add(report);
            return Task.CompletedTask;
        };

    public static TheoryData<string> PerReportRefusals => ["400", "413", "500"];

    [Theory]
    [MemberData(nameof(PerReportRefusals))]
    public async Task A_tier_AL_Workbench_refuses_is_skipped_and_the_others_are_still_sent(string status)
    {
        Exception refusal = status == "500"
            ? new ALWorkbench.Gateway.Core.WorkbenchUnreachableException("AL Workbench answered 500: boom", status: System.Net.HttpStatusCode.InternalServerError)
            : new ALWorkbench.Gateway.Core.WorkbenchRefusedException((System.Net.HttpStatusCode)int.Parse(status, System.Globalization.CultureInfo.InvariantCulture), "No.");
        var taken = new List<GatewayInventoryReport>();

        var summary = await new GatewayInventory(new FakeInventoryReader()).RunAsync(Policy, null, Refusing("BC250", refusal, taken), null, CancellationToken.None);

        taken.SelectMany(r => r.Instances).Select(i => i.Name).Should().Equal("BC140", "BC140TEST");
        summary.Posts.Should().Be(2);
        summary.Refused.Should().ContainSingle().Which.Should().StartWith("CRONUS-BC01 / BC250");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable)]
    [InlineData(System.Net.HttpStatusCode.BadGateway)]
    public async Task An_AL_Workbench_that_cannot_be_reached_ends_the_round_for_a_retry_soon(System.Net.HttpStatusCode? status)
    {
        var down = new ALWorkbench.Gateway.Core.WorkbenchUnreachableException("Could not reach AL Workbench.", status: status);
        var taken = new List<GatewayInventoryReport>();

        var act = () => new GatewayInventory(new FakeInventoryReader()).RunAsync(Policy, null, Refusing("BC250", down, taken), null, CancellationToken.None);

        await act.Should().ThrowAsync<ALWorkbench.Gateway.Core.WorkbenchUnreachableException>();
        taken.Should().ContainSingle("the tier before was sent; the round stops at the first it cannot send");
    }

    [Fact]
    public async Task A_conflict_is_sent_again_and_taken()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        var inventory = new GatewayInventory(new FakeInventoryReader(), (wait, _) => { waits.Add(wait); return Task.CompletedTask; });

        var summary = await inventory.RunAsync(Policy, new TierRef("CRONUS-BC01", "BC250"), (_, _) =>
            ++attempts < 3 ? throw new ALWorkbench.Gateway.Core.CommandClosedException("Send this one again.") : Task.CompletedTask,
            null, CancellationToken.None);

        attempts.Should().Be(3);
        waits.Should().HaveCount(2);
        summary.Should().Be(new InventoryRunSummary(1, 0, 1));
    }

    [Fact]
    public async Task A_conflict_that_does_not_clear_is_reported_as_refused()
    {
        var inventory = new GatewayInventory(new FakeInventoryReader(), (_, _) => Task.CompletedTask);

        var summary = await inventory.RunAsync(Policy, new TierRef("CRONUS-BC01", "BC250"),
            (_, _) => throw new ALWorkbench.Gateway.Core.CommandClosedException("Send this one again."), null, CancellationToken.None);

        summary.Posts.Should().Be(0);
        summary.Refused.Should().ContainSingle();
    }

    // ---------------------------------------------------------------- splitting into posts

    [Fact]
    public void A_large_tier_is_split_so_each_tenants_apps_travel_once()
    {
        var tenants = Enumerable.Range(0, 40).Select(t => new GatewayTenantInventory
        {
            Id = $"tenant{t}",
            Apps = Enumerable.Range(0, 300).Select(a => new GatewayAppInventory
            {
                AppId = Guid.NewGuid().ToString(), Name = $"CRONUS app number {a} with a long name", Publisher = "CRONUS Partner A/S", Version = "25.0.0.0",
            }).ToList(),
        }).ToList();
        var inventory = new GatewayInstanceInventory { Machine = "CRONUS-BC01", Name = "BC250", Tenants = tenants };
        const int target = 256 * 1024;

        var parts = InventoryChunker.Split(inventory, target);

        parts.Count.Should().BeGreaterThan(1);
        parts.Should().OnlyContain(p => JsonSerializer.SerializeToUtf8Bytes(p, (JsonSerializerOptions?)null).Length <= target * 2);
        parts.Should().OnlyContain(p => p.Tenants!.Count == 40, "every part carries the whole tenant list");
        tenants.Select(t => parts.Count(p => p.Tenants!.Single(x => x.Id == t.Id).Apps is not null))
            .Should().OnlyContain(n => n == 1);
        InventoryChunker.Split(inventory with { Tenants = tenants.Take(1).ToList() }, target).Should().ContainSingle();
    }

    // ---------------------------------------------------------------- one round

    [Fact]
    public async Task A_round_reads_only_the_tiers_that_allow_inventory_and_sends_each_with_the_roster()
    {
        var reader = new FakeInventoryReader();
        var posts = new List<GatewayInventoryReport>();

        var summary = await new GatewayInventory(reader).RunAsync(Policy, null, (r, _) => { posts.Add(r); return Task.CompletedTask; }, null, CancellationToken.None);

        reader.Read.Should().Equal("CRONUS-BC01 / BC140", "CRONUS-BC01 / BC250", "CRONUS-BC01 / BC140TEST");
        summary.Should().Be(new InventoryRunSummary(3, 0, 3));
        posts.Should().OnlyContain(p => p.Roster!.Count == 3 && p.Instances.Count == 1);
        posts[0].Roster!.Select(t => t.Instance).Should().NotContain("BC260");
    }

    [Fact]
    public async Task A_refresh_of_one_tier_sends_no_roster_so_nothing_else_is_marked_missing()
    {
        var reader = new FakeInventoryReader();
        var posts = new List<GatewayInventoryReport>();

        await new GatewayInventory(reader).RunAsync(Policy, new TierRef("cronus-bc01", "bc250"), (r, _) => { posts.Add(r); return Task.CompletedTask; }, null, CancellationToken.None);

        reader.Read.Should().Equal("CRONUS-BC01 / BC250");
        posts.Should().ContainSingle().Which.Roster.Should().BeNull();
    }

    [Fact]
    public async Task With_no_readable_tier_one_report_says_so_with_an_empty_roster()
    {
        var posts = new List<GatewayInventoryReport>();

        await new GatewayInventory(new FakeInventoryReader()).RunAsync(GatewayPolicy.Empty, null, (r, _) => { posts.Add(r); return Task.CompletedTask; }, null, CancellationToken.None);

        posts.Should().ContainSingle();
        posts[0].Roster.Should().BeEmpty();
        posts[0].Instances.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tier_on_another_computer_or_without_a_service_is_reported_as_not_read()
    {
        var policy = GatewayPolicyParser.Parse("""
            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC999"
            allow = ["inventory"]

            [[instance]]
            machine = "CRONUS-BC02"
            name = "BC140"
            allow = ["inventory"]

            [[machine]]
            name = "CRONUS-BC02"
            method = "service-manager"
            """, "CRONUS-BC01").Policy!;
        var reader = new PowerShellInventoryReader(
            new ALWorkbench.Gateway.Core.PowerShell.PowerShellRunner(new NeverRunProcesses(), new("powershell.exe", "pwsh.exe"), new FakeFileAccess()),
            new NoTiers(), "CRONUS-BC01");

        var local = await reader.ReadAsync(policy.Instances[0], null, CancellationToken.None);
        var remote = await reader.ReadAsync(policy.Instances[1], null, CancellationToken.None);

        local.Error.Should().Contain("no Business Central service for BC999");
        remote.Error.Should().Contain("another computer");
    }

    // ---------------------------------------------------------------- the script itself, when pwsh is installed

    /// <summary>
    /// Stands in for a BC 14 tier's NavAdminTool.ps1: no Get-NAVTenant, no
    /// -TenantSpecificProperties, no -Tenant on Get-NAVServerSession, and a licence
    /// export that hangs. Get-CimInstance does not exist off Windows, so the service
    /// part fails as it would on a machine where WMI is broken.
    /// </summary>
    private static string Bc14AdminTool(string state) => """
        Write-Host 'Business Central Administration Shell'
        function Get-NAVServerInstance([string]$ServerInstance) {
            [pscustomobject]@{ ServerInstance = 'MicrosoftDynamicsNavServer$' + $ServerInstance; State = 'STATE'; Version = '14.0.47786.0' }
        }
        function Get-NAVServerConfiguration([string]$ServerInstance, [switch]$AsXml) {
            [xml]'<configuration><appSettings><add key="DatabaseServer" value="CRONUS-SQL" /><add key="DatabaseName" value="Demo Database BC (14-0)" /><add key="Multitenant" value="false" /><add key="ClientServicesPort" value="7046" /><add key="ServicesCertificateThumbprint" value="secret" /></appSettings></configuration>'
        }
        function Get-NAVAppInfo([string]$ServerInstance, [string]$Tenant) {
            [pscustomobject]@{ AppId = [guid]'437dbf0e-84ff-417a-965d-ed2bb9650972'; Name = 'Base Application'; Publisher = 'Microsoft'; Version = [version]'14.0.47786.0'; IsInstalled = $true }
        }
        function Get-NAVServerSession([string]$ServerInstance) {
            [pscustomobject]@{ UserID = 'CRONUS\ANNA' }
            [pscustomobject]@{ UserID = 'CRONUS\BO' }
        }
        function Export-NAVServerLicenseInformation([string]$ServerInstance) {
            Start-Sleep -Seconds 30
            'never seen'
        }
        """.Replace("STATE", state, StringComparison.Ordinal);

    private static async Task<(ALWorkbench.Gateway.Core.PowerShell.PowerShellResult Outcome, GatewayInstanceInventory Inventory)> RunScriptAsync(string state, string tierName, string? adminTool = null, int? maxTenants = null)
    {
        var pwsh = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh"))
            .FirstOrDefault(File.Exists);
        if (pwsh is null) Assert.Skip("PowerShell 7 (pwsh) is not installed here.");
        using var tier = new ScratchFolder();
        await File.WriteAllTextAsync(Path.Combine(tier.Path, "NavAdminTool.ps1"), adminTool ?? Bc14AdminTool(state));
        var runner = new ALWorkbench.Gateway.Core.PowerShell.PowerShellRunner(
            new ALWorkbench.Gateway.Core.PowerShell.SystemProcessRunner(), new(pwsh, pwsh), new FakeFileAccess());
        var parameters = InventoryScript.Parameters(tierName, tier.Path, includeSessionUsers: true);
        parameters["sectionSeconds"] = 5;
        if (maxTenants is { } max) parameters["maxTenants"] = max;

        var outcome = await runner.RunAsync(InventoryScript.Operation,
            new ALWorkbench.Gateway.Core.PowerShell.PowerShellTarget(PowerShellEdition.PowerShell7, tier.Path),
            parameters, TimeSpan.FromMinutes(2), null, CancellationToken.None);
        var output = outcome.Succeeded && outcome.Output.Count == 1 ? outcome.Output[0] : null;
        return (outcome, InventoryNormaliser.FromScriptOutput(output, Tier(tierName)));
    }

    [Fact]
    public async Task With_pwsh_installed_the_script_reads_a_bc14_shaped_tier_and_names_what_it_could_not_read()
    {
        var (outcome, inventory) = await RunScriptAsync("Running", "BC250");

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        inventory.Error.Should().BeNull();
        inventory.State.Should().Be("Running");
        inventory.Version.Should().Be("14.0.47786.0");
        inventory.Configuration!.DatabaseName.Should().Be("Demo Database BC (14-0)");
        inventory.Configuration.ClientServicesPort.Should().Be(7046);
        outcome.Output[0]!["configuration"]!.AsObject().Select(p => p.Key).Should().NotContain("ServicesCertificateThumbprint");
        var tenant = inventory.Tenants.Should().ContainSingle().Subject;
        tenant.Id.Should().Be("default", "a single-tenant tier has one tenant even without Get-NAVTenant");
        tenant.Apps.Should().ContainSingle().Which.Version.Should().Be("14.0.47786.0");
        inventory.SessionCount.Should().Be(2);
        inventory.SessionUsers.Should().Equal("CRONUS\\ANNA", "CRONUS\\BO");
        inventory.Service.Should().BeNull();
        inventory.Unavailable.Should().ContainKeys("service", "apps.tenantSpecificProperties", "licence");
        inventory.Unavailable["licence"].Should().Be("Did not answer within 5 seconds.");
        inventory.Licence.Should().BeNull();
    }

    /// <summary>A multitenant tier with three tenants, each with one app; everything else answers nothing.</summary>
    private const string MultitenantAdminTool = """
        function Get-NAVServerInstance([string]$ServerInstance) {
            [pscustomobject]@{ State = 'Running'; Version = '25.4.29661.29927' }
        }
        function Get-NAVServerConfiguration([string]$ServerInstance, [switch]$AsXml) {
            [xml]'<configuration><appSettings><add key="Multitenant" value="true" /></appSettings></configuration>'
        }
        function Get-NAVTenant([string]$ServerInstance) {
            foreach ($id in @('cronus-dk', 'cronus-se', 'cronus-no')) { [pscustomobject]@{ Id = $id; State = 'Operational'; DatabaseName = 'BC250 ' + $id } }
        }
        function Get-NAVAppInfo([string]$ServerInstance, [string]$Tenant, [switch]$TenantSpecificProperties) {
            [pscustomobject]@{ AppId = '437dbf0e-84ff-417a-965d-ed2bb9650972'; Name = 'Base Application'; Publisher = 'Microsoft'; Version = '25.4.29661.29927'; ExtensionDataVersion = '25.4.29661.29927'; SyncState = 'Synced' }
        }
        """;

    [Fact]
    public async Task With_pwsh_installed_tenants_past_the_limit_come_back_flagged_so_none_are_marked_missing()
    {
        var (outcome, inventory) = await RunScriptAsync("Running", "BC250", MultitenantAdminTool, maxTenants: 2);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.Output[0]!["tenantsTruncated"]!.GetValue<bool>().Should().BeTrue();
        inventory.Tenants!.Select(t => t.Id).Should().Equal("cronus-dk", "cronus-se");
        inventory.TenantsTruncated.Should().BeTrue();
        inventory.Tenants![0].Apps.Should().ContainSingle().Which.SyncState.Should().Be("Synced");

        var (_, all) = await RunScriptAsync("Running", "BC250", MultitenantAdminTool, maxTenants: 3);
        all.Tenants.Should().HaveCount(3);
        all.TenantsTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task With_pwsh_installed_a_stopped_tier_is_not_asked_for_what_only_a_running_one_knows()
    {
        var (outcome, inventory) = await RunScriptAsync("Stopped", "BC140TEST");

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        inventory.State.Should().Be("Stopped");
        inventory.Configuration!.DatabaseServer.Should().Be("CRONUS-SQL");
        inventory.Tenants.Should().BeNull();
        inventory.Unavailable["tenants"].Should().Be("The service tier is not running.");
    }

    private sealed class NoTiers : ALWorkbench.Gateway.Core.PowerShell.IServiceTierLocator
    {
        public ALWorkbench.Gateway.Core.PowerShell.ServiceTierInstall? Find(string instance) => null;
    }

    private sealed class NeverRunProcesses : ALWorkbench.Gateway.Core.PowerShell.IProcessRunner
    {
        public Task<ALWorkbench.Gateway.Core.PowerShell.ProcessOutcome> RunAsync(ALWorkbench.Gateway.Core.PowerShell.ProcessSpec spec, CancellationToken ct) =>
            throw new InvalidOperationException("Nothing should run.");
    }
}
