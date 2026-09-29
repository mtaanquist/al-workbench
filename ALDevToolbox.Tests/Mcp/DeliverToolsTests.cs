using System.Reflection;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// The read-only Deliver tools (issue #912), through the tool methods over the real
/// service graph. The seed is three solutions: two Public ones the caller can see, and a
/// Private one they are not on that has something in every table these tools read - so
/// each test can check that it is absent rather than locked. Nothing here reaches a
/// customer's tenant: the Business Central clients are the unreachable doubles, and a
/// tool that tried to use one would fail the test.
/// </summary>
public sealed class DeliverToolsTests : IDisposable
{
    private const int ViewerId = 9801;
    private const int OutsiderId = 9802;
    private const int AnneId = 9803;
    private const int SecretOwnerId = 9804;

    private static readonly Guid PayrollAppId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid OurAppId = Guid.Parse("66666666-7777-8888-9999-000000000000");

    private readonly TestDb _db = new();
    private readonly Seeded _seed;

    public DeliverToolsTests()
    {
        _seed = SeedAsync().GetAwaiter().GetResult();
        ActAs(ViewerId);
    }

    public void Dispose() => _db.Dispose();

    private void ActAs(int userId)
    {
        _db.OrgContext.CurrentUserId = userId;
        _db.OrgContext.IsSiteAdmin = false;
    }

    private DeliverTools NewTools(AppDbContext ctx)
    {
        var access = new ProjectAccess(ctx, _db.OrgContext);
        var connections = new ProjectConnectionService(
            ctx, _db.OrgContext, access,
            new BcTokenService(new UnreachableHttpClientFactory(), NullLogger<BcTokenService>.Instance),
            new UnreachableAdminClient(), new UnreachableAppManagementClient(),
            _db.DataProtectionProvider, new BcPanelCache(TimeProvider.System), TimeProvider.System,
            NullLogger<ProjectConnectionService>.Instance);
        var discovery = new ProjectDiscoveryService(ctx, _db.OrgContext, access, new ProjectDiscoveryQueue(),
            NullLogger<ProjectDiscoveryService>.Instance);
        return new DeliverTools(
            new ProjectService(ctx, _db.OrgContext, access, discovery, NullLogger<ProjectService>.Instance),
            new ProjectCustomerInfoService(ctx, _db.OrgContext, access, NullLogger<ProjectCustomerInfoService>.Instance),
            connections,
            new UpgradeFleetService(ctx, _db.OrgContext, access, new EnvironmentRefreshQueue(),
                NullLogger<UpgradeFleetService>.Instance),
            new UpgradeActionService(ctx, _db.OrgContext, access, connections, TimeProvider.System,
                NullLogger<UpgradeActionService>.Instance),
            new EnvironmentUpgradeService(ctx, _db.OrgContext, access,
                new UpgradeFleetService(ctx, _db.OrgContext, access, new EnvironmentRefreshQueue(),
                    NullLogger<UpgradeFleetService>.Instance),
                TimeProvider.System, NullLogger<EnvironmentUpgradeService>.Instance),
            new CustomerModuleService(ctx, _db.OrgContext, access, NullLogger<CustomerModuleService>.Instance),
            new DeliveryFeedService(ctx, access),
            access,
            _db.OrgContext,
            NullLogger<DeliverTools>.Instance);
    }

    // ── The class stays read-only ───────────────────────────────────────

    [Fact]
    public void Every_tool_in_the_class_is_read_only()
    {
        var methods = typeof(DeliverTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        // Every public method is a tool - a helper made public would be a tool-shaped
        // hole the walk below could not see.
        methods.Should().OnlyContain(m => m.GetCustomAttribute<McpServerToolAttribute>() != null,
            "every public method on DeliverTools is an MCP tool");

        var tools = methods.Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!).ToList();
        tools.Should().HaveCount(12);
        tools.Should().OnlyContain(t => t.ReadOnly,
            "the Deliver tools only read; a write belongs in DeliveryTools behind its own gate");
    }

    // ── get_solution ────────────────────────────────────────────────────

    [Fact]
    public async Task Get_solution_returns_the_basics_and_the_environments()
    {
        await using var ctx = _db.NewContext();
        var solution = await NewTools(ctx).GetSolutionAsync("CRONUS Denmark");

        solution.SolutionId.Should().Be(_seed.Denmark);
        solution.Visibility.Should().Be("Public");
        solution.OnPremises.Should().BeFalse();
        solution.Version.Should().Be("BC 25 (typed)");
        solution.VersionFromBusinessCentral.Should().BeFalse("the solution has no connection, so the typed version stands");
        solution.Connection.Should().NotBeNull();
        solution.Connection!.Configured.Should().BeFalse();
        solution.Environments.Select(e => e.Name).Should().BeEquivalentTo(["Production", "Sandbox"]);
        solution.Environments.Should().OnlyContain(e => e.ReadAt != null);
    }

    [Fact]
    public async Task Get_solution_answers_a_private_solution_as_not_found()
    {
        await using var ctx = _db.NewContext();
        var byName = () => NewTools(ctx).GetSolutionAsync("CRONUS Secret");
        var byId = () => NewTools(ctx).GetSolutionAsync(_seed.Secret.ToString());

        (await byName.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("not found");
        (await byId.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("does not exist");
    }

    // ── list_environments ───────────────────────────────────────────────

    [Fact]
    public async Task List_environments_lists_every_visible_environment_with_its_freshness()
    {
        await using var ctx = _db.NewContext();
        var rows = await NewTools(ctx).ListEnvironmentsAsync();

        rows.Select(r => (r.SolutionName, r.Name)).Should().BeEquivalentTo(new[]
        {
            ("CRONUS Denmark", "Production"), ("CRONUS Denmark", "Sandbox"), ("CRONUS Sweden", "Production"),
        });

        var production = rows.Single(r => r.EnvironmentId == _seed.DenmarkProduction);
        production.Version.Should().Be("25.3.1.0");
        production.TenantStorageUse.Should().Be(0.95);
        production.UpdateWindow.Should().Be(new TimeWindow("22:00", "06:00", "Europe/Copenhagen"));
        production.NextUpdate!.Version.Should().Be("26.0");
        production.EnvironmentReadAt.Should().NotBeNull();
        production.NextUpdateReadAt.Should().NotBeNull();
        production.UpdateWindowReadAt.Should().NotBeNull();
    }

    [Fact]
    public async Task List_environments_filters_by_type_status_version_storage_and_next_update()
    {
        await using var ctx = _db.NewContext();
        var tools = NewTools(ctx);

        (await tools.ListEnvironmentsAsync(type: "sandbox")).Select(r => r.EnvironmentId)
            .Should().Equal(_seed.DenmarkSandbox);
        (await tools.ListEnvironmentsAsync(status: "Upgrading")).Select(r => r.EnvironmentId)
            .Should().Equal(_seed.SwedenProduction);
        (await tools.ListEnvironmentsAsync(version: "25")).Select(r => r.EnvironmentId)
            .Should().Equal(_seed.DenmarkProduction);
        (await tools.ListEnvironmentsAsync(version: "26.1")).Select(r => r.EnvironmentId)
            .Should().Equal(_seed.DenmarkSandbox);
        (await tools.ListEnvironmentsAsync(storageOver: 0.8)).Select(r => r.EnvironmentId)
            .Should().BeEquivalentTo([_seed.DenmarkProduction, _seed.DenmarkSandbox],
                "the quota is the tenant's, so both of its environments are over it");
        (await tools.ListEnvironmentsAsync(nextUpdateBefore: "2026-11-01")).Select(r => r.EnvironmentId)
            .Should().Equal(_seed.DenmarkProduction);
        (await tools.ListEnvironmentsAsync(solution: "CRONUS Sweden")).Select(r => r.EnvironmentId)
            .Should().Equal(_seed.SwedenProduction);
    }

    [Fact]
    public async Task List_environments_refuses_a_date_it_cannot_read()
    {
        await using var ctx = _db.NewContext();
        var act = () => NewTools(ctx).ListEnvironmentsAsync(nextUpdateBefore: "next week");

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("2026-10-01");
    }

    // ── get_environment and its history ─────────────────────────────────

    [Fact]
    public async Task Get_environment_carries_the_installed_apps_and_recent_history()
    {
        await using var ctx = _db.NewContext();
        var env = await NewTools(ctx).GetEnvironmentAsync(_seed.DenmarkProduction);

        env.Environment.Name.Should().Be("Production");
        env.CountryCode.Should().Be("DK");
        env.InstalledApps.Should().HaveCount(2);
        env.InstalledApps.Single(a => a.AppId == OurAppId).DeliveredFromWorkbench.Should().BeTrue();
        env.InstalledApps.Single(a => a.AppId == PayrollAppId).DeliveredFromWorkbench.Should().BeFalse();
        env.InstalledAppsReadAt.Should().NotBeNull();
        env.RecentHistory.Should().HaveCount(3);
    }

    [Fact]
    public async Task List_environment_history_is_newest_first()
    {
        await using var ctx = _db.NewContext();
        var history = await NewTools(ctx).ListEnvironmentHistoryAsync(_seed.DenmarkProduction);

        history.Select(h => h.Action).Should().Equal("RunNow", "PushDateToLatest", "PushDateToLatest");
        history[0].RequestedBy.Should().Be("Viewer");
        history[2].Status.Should().Be("Pending");
        history[2].IsBooking.Should().BeTrue();
    }

    [Fact]
    public async Task An_environment_of_a_private_solution_is_not_found()
    {
        await using var ctx = _db.NewContext();
        var tools = NewTools(ctx);

        (await ((Func<Task>)(() => tools.GetEnvironmentAsync(_seed.SecretProduction))).Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("not found");
        (await ((Func<Task>)(() => tools.ListEnvironmentHistoryAsync(_seed.SecretProduction))).Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("not found");
    }

    // ── list_upgrades ───────────────────────────────────────────────────

    [Fact]
    public async Task List_upgrades_groups_by_solution_and_says_where_the_caller_may_act()
    {
        await using var ctx = _db.NewContext();
        var groups = await NewTools(ctx).ListUpgradesAsync();

        groups.Select(g => g.SolutionName).Should().Equal("CRONUS Denmark", "CRONUS Sweden");
        var denmark = groups[0];
        denmark.Environments.Select(e => e.Name).Should().Equal("Production", "Sandbox");
        denmark.Environments.Should().OnlyContain(e => e.CanChangeDate);
        denmark.Environments[0].Booked.Should().ContainSingle(b => b.Action == "PushDateToLatest");
        groups[1].Environments.Should().OnlyContain(e => !e.CanChangeDate);
    }

    [Fact]
    public async Task List_upgrades_needs_the_environment_updates_permission()
    {
        ActAs(OutsiderId);
        await using var ctx = _db.NewContext();
        var act = () => NewTools(ctx).ListUpgradesAsync();

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("environment updates");
    }

    // ── Planned upgrades ────────────────────────────────────────────────

    [Fact]
    public async Task Get_upgrade_returns_the_header_and_each_visible_environment_with_its_state()
    {
        var id = await SeedUpgradeAsync("26.0 in October 2026", "26.0", closed: false,
            _seed.DenmarkProduction, _seed.SwedenProduction, _seed.SecretProduction);
        await using (var ctx = _db.NewContext())
        {
            // A start sent to Denmark from the upgrade, and Sweden's check assigned to Anne.
            var started = NewAction(_seed.Denmark, _seed.DenmarkProduction,
                UpgradeActionKind.RunNow, UpgradeActionStatus.Sent, DateTime.UtcNow, DateTime.UtcNow);
            started.UpgradeId = id;
            ctx.OeEnvironmentUpgradeActions.Add(started);
            var sweden = ctx.OeEnvironmentUpgradeLines.Single(l => l.UpgradeId == id && l.EnvironmentId == _seed.SwedenProduction);
            sweden.AssigneeUserId = AnneId;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var upgrade = await NewTools(read).GetUpgradeAsync(id);

        upgrade.Upgrade.Name.Should().Be("26.0 in October 2026");
        upgrade.Upgrade.TargetVersion.Should().Be("26.0");
        upgrade.Upgrade.CreatedBy.Should().Be("Viewer", "the email is dropped from the stored actor");
        upgrade.Upgrade.Status.Should().Be("InProgress");
        upgrade.Upgrade.EnvironmentCount.Should().Be(2, "the Private solution's environment is left out");
        upgrade.Upgrade.Counts.Should().Be(new UpgradeStateCounts(0, 0, 1, 1, 0, 0, 0));

        upgrade.Environments.Select(e => e.SolutionName).Should().Equal("CRONUS Denmark", "CRONUS Sweden");
        var denmark = upgrade.Environments[0];
        denmark.State.Should().Be("Booked");
        denmark.LastAction!.Action.Should().Be("RunNow");
        denmark.NextUpdate!.Version.Should().Be("26.0");
        denmark.CurrentVersion.Should().Be("25.3.1.0");
        var swedenLine = upgrade.Environments[1];
        swedenLine.State.Should().Be("Running", "Business Central reports it upgrading");
        swedenLine.AssignedTo.Should().Be("Anne Hansen");
        swedenLine.LastAction.Should().BeNull();
        upgrade.Environments.Should().NotContain(e => e.SolutionId == _seed.Secret);
    }

    [Fact]
    public async Task Get_upgrade_answers_an_unknown_id_as_not_found()
    {
        await using var ctx = _db.NewContext();
        var act = () => NewTools(ctx).GetUpgradeAsync(987_654);

        (await act.Should().ThrowAsync<McpException>()).Which.Message
            .Should().Contain("does not exist").And.Contain("list_planned_upgrades");
    }

    [Fact]
    public async Task List_planned_upgrades_puts_open_first_and_finds_the_archive_by_search()
    {
        var open = await SeedUpgradeAsync("26.0 in October 2026", "26.0", closed: false, _seed.DenmarkProduction);
        var done = await SeedUpgradeAsync("25.5 in June 2026", "25.5", closed: true, _seed.DenmarkSandbox);

        await using var ctx = _db.NewContext();
        var tools = NewTools(ctx);

        (await tools.ListPlannedUpgradesAsync()).Select(u => u.UpgradeId).Should().Equal(open);
        var all = await tools.ListPlannedUpgradesAsync(includeArchived: true);
        all.Select(u => u.UpgradeId).Should().Equal(open, done);
        all[1].Status.Should().Be("Done");
        all[1].ClosedBy.Should().Be("Viewer");
        all[0].Counts.Planned.Should().Be(1);

        (await tools.ListPlannedUpgradesAsync(includeArchived: true, search: "june")).Select(u => u.UpgradeId)
            .Should().Equal(done);
        (await tools.ListPlannedUpgradesAsync(includeArchived: true, search: "26.0")).Select(u => u.UpgradeId)
            .Should().Equal(open);
    }

    [Fact]
    public async Task Planned_upgrades_need_the_environment_updates_permission()
    {
        var id = await SeedUpgradeAsync("26.0 in October 2026", "26.0", closed: false, _seed.DenmarkProduction);
        ActAs(OutsiderId);
        await using var ctx = _db.NewContext();
        var tools = NewTools(ctx);

        (await ((Func<Task>)(() => tools.ListPlannedUpgradesAsync())).Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("environment updates");
        (await ((Func<Task>)(() => tools.GetUpgradeAsync(id))).Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("environment updates");
    }

    // ── list_recent_deliveries ──────────────────────────────────────────

    [Fact]
    public async Task List_recent_deliveries_spans_solutions_and_explains_failures()
    {
        await using var ctx = _db.NewContext();
        var tools = NewTools(ctx);

        var all = await tools.ListRecentDeliveriesAsync();
        all.Select(d => d.SolutionName).Should().Equal("CRONUS Sweden", "CRONUS Denmark");
        all.Single(d => d.Status == ProjectDeliveryStatus.Deployed).Apps.Should().BeEmpty();

        var failed = await tools.ListRecentDeliveriesAsync(status: "failed");
        failed.Should().ContainSingle();
        failed[0].FailureMessage.Should().Be("The app did not install.");
        failed[0].Apps.Should().ContainSingle(a => a.Status == ProjectDeliveryResultStatus.Failed);

        (await tools.ListRecentDeliveriesAsync(since: "2099-01-01")).Should().BeEmpty();
    }

    [Fact]
    public async Task List_recent_deliveries_refuses_a_status_that_does_not_exist()
    {
        await using var ctx = _db.NewContext();
        var act = () => NewTools(ctx).ListRecentDeliveriesAsync(status: "exploded");

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("handed_off");
    }

    // ── Customer information ────────────────────────────────────────────

    [Fact]
    public async Task List_customer_contacts_returns_how_to_reach_them()
    {
        await using var ctx = _db.NewContext();
        var result = await NewTools(ctx).ListCustomerContactsAsync("CRONUS Denmark");

        result.Contacts.Should().ContainSingle();
        result.Contacts[0].Should().Be(new ContactRow("Customer", "Lars Nielsen", "CRONUS Denmark A/S",
            "lars@cronus.example", "+45 12 34 56 78"));
    }

    [Fact]
    public async Task Get_customer_access_returns_the_getting_in_notes_and_integrations()
    {
        await using var ctx = _db.NewContext();
        var access = await NewTools(ctx).GetCustomerAccessAsync(_seed.Denmark.ToString());

        access.GettingIn.Should().Be("VPN, then ask for the support account.");
        access.HostingNotes.Should().Be("Hosted by Microsoft.");
        access.Integrations.Should().ContainSingle(i => i.Name == "Webshop" && i.Direction == "Both");
    }

    [Fact]
    public async Task List_customer_knowledge_answers_both_directions()
    {
        await using var ctx = _db.NewContext();
        var tools = NewTools(ctx);

        var bySolution = await tools.ListCustomerKnowledgeAsync(solution: "CRONUS Denmark");
        bySolution.People.Should().ContainSingle(p => p.PersonName == "Anne Hansen" && p.Role == "Consultant");
        bySolution.Notes.Should().Be("Month-end is busy.");

        var byPerson = await tools.ListCustomerKnowledgeAsync(person: "anne");
        byPerson.People.Select(p => p.SolutionName).Should().Equal("CRONUS Denmark", "CRONUS Sweden");
        byPerson.Notes.Should().BeNull();

        var byEmail = await tools.ListCustomerKnowledgeAsync(person: "ANNE@example.com");
        byEmail.People.Should().HaveCount(2);
    }

    [Fact]
    public async Task List_customer_knowledge_wants_exactly_one_question()
    {
        await using var ctx = _db.NewContext();
        var tools = NewTools(ctx);

        await ((Func<Task>)(() => tools.ListCustomerKnowledgeAsync())).Should().ThrowAsync<McpException>();
        await ((Func<Task>)(() => tools.ListCustomerKnowledgeAsync("CRONUS Denmark", "anne"))).Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task List_customer_modules_answers_the_catalogue_a_module_and_a_solution()
    {
        await using var ctx = _db.NewContext();
        var tools = NewTools(ctx);

        var catalogue = await tools.ListCustomerModulesAsync();
        catalogue.Should().ContainSingle(m => m.Module == "CRONUS Payroll" && m.SolutionId == null);

        var holders = await tools.ListCustomerModulesAsync(module: "cronus payroll");
        holders.Should().ContainSingle();
        holders[0].SolutionName.Should().Be("CRONUS Denmark");
        holders[0].Version.Should().Be("3.1.0.0");
        holders[0].Source.Should().Be("business_central");
        holders[0].ReadAt.Should().NotBeNull();

        var denmark = await tools.ListCustomerModulesAsync(solution: "CRONUS Denmark");
        denmark.Should().ContainSingle(m => m.Module == "CRONUS Payroll" && m.EnvironmentName == "Production");
    }

    // ── The Private solution is absent everywhere ───────────────────────

    [Fact]
    public async Task A_private_solution_the_caller_is_not_on_is_absent_from_every_list()
    {
        await using var ctx = _db.NewContext();
        var tools = NewTools(ctx);

        (await tools.ListEnvironmentsAsync(includeDeleted: true)).Should().NotContain(r => r.SolutionId == _seed.Secret);
        (await tools.ListUpgradesAsync()).Should().NotContain(g => g.SolutionId == _seed.Secret);
        (await tools.ListRecentDeliveriesAsync()).Should().NotContain(d => d.SolutionId == _seed.Secret);
        (await tools.ListCustomerKnowledgeAsync(person: "anne")).People.Should().NotContain(p => p.SolutionId == _seed.Secret);
        (await tools.ListCustomerModulesAsync(module: "CRONUS Payroll")).Should().NotContain(m => m.SolutionId == _seed.Secret);

        foreach (var read in new Func<Task>[]
                 {
                     () => tools.ListCustomerContactsAsync("CRONUS Secret"),
                     () => tools.GetCustomerAccessAsync("CRONUS Secret"),
                     () => tools.ListCustomerKnowledgeAsync(solution: "CRONUS Secret"),
                     () => tools.ListCustomerModulesAsync(solution: "CRONUS Secret"),
                     () => tools.ListEnvironmentsAsync(solution: "CRONUS Secret"),
                 })
        {
            await read.Should().ThrowAsync<McpException>();
        }
    }

    [Fact]
    public async Task The_private_solutions_owner_does_see_it()
    {
        ActAs(SecretOwnerId);
        await using var ctx = _db.NewContext();
        var tools = NewTools(ctx);

        (await tools.ListEnvironmentsAsync()).Should().Contain(r => r.SolutionId == _seed.Secret);
        (await tools.ListRecentDeliveriesAsync()).Should().Contain(d => d.SolutionId == _seed.Secret);
        (await tools.ListCustomerContactsAsync("CRONUS Secret")).Contacts.Should().ContainSingle();
    }

    // ── Seeding ─────────────────────────────────────────────────────────

    private sealed record Seeded(
        int Denmark, int Sweden, int Secret,
        int DenmarkProduction, int DenmarkSandbox, int SwedenProduction, int SecretProduction);

    private async Task<Seeded> SeedAsync()
    {
        var now = DateTime.UtcNow;
        await using var ctx = _db.NewContext();

        ctx.Users.AddRange(
            NewUser(ViewerId, "viewer@example.com", "Viewer"),
            NewUser(OutsiderId, "outsider@example.com", "Outsider"),
            NewUser(AnneId, "anne@example.com", "Anne Hansen"),
            NewUser(SecretOwnerId, "owner@example.com", "Secret Owner"));
        await ctx.SaveChangesAsync();

        var denmark = NewProject("CRONUS Denmark", ProjectVisibility.Public, null, 1_000_000);
        denmark.BcVersion = "BC 25 (typed)";
        denmark.BcTimeZone = "Europe/Copenhagen";
        denmark.AccessDescription = "VPN, then ask for the support account.";
        denmark.HostingNotes = "Hosted by Microsoft.";
        denmark.KnowledgeNotes = "Month-end is busy.";
        var sweden = NewProject("CRONUS Sweden", ProjectVisibility.Public, null, 1_000_000);
        var secret = NewProject("CRONUS Secret", ProjectVisibility.Private, SecretOwnerId, 1_000_000);
        ctx.OeProjects.AddRange(denmark, sweden, secret);
        await ctx.SaveChangesAsync();

        var dkProd = NewEnvironment(denmark.Id, "Production", "Production", "Active", "25.3.1.0", 900_000);
        dkProd.CountryCode = "DK";
        dkProd.BcNextUpdateVersion = "26.0";
        dkProd.BcNextUpdateDate = new DateTime(2026, 10, 10, 20, 0, 0, DateTimeKind.Utc);
        dkProd.BcNextUpdateLatestDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        dkProd.BcNextUpdateFetchedAt = now;
        dkProd.BcUpdateWindowStart = new TimeOnly(22, 0);
        dkProd.BcUpdateWindowEnd = new TimeOnly(6, 0);
        dkProd.BcUpdateWindowTimeZoneIana = "Europe/Copenhagen";
        dkProd.BcUpdateWindowFetchedAt = now;
        var dkSandbox = NewEnvironment(denmark.Id, "Sandbox", "Sandbox", "Active", "26.1.0.0", 50_000);
        var seProd = NewEnvironment(sweden.Id, "Production", "Production", "Upgrading", "26.2.0.0", 100_000);
        seProd.BcNextUpdateVersion = "27.0";
        seProd.BcNextUpdateDate = new DateTime(2026, 12, 1, 20, 0, 0, DateTimeKind.Utc);
        seProd.BcNextUpdateFetchedAt = now;
        var secretProd = NewEnvironment(secret.Id, "Production", "Production", "Active", "25.1.0.0", 10_000);
        ctx.OeProjectEnvironments.AddRange(dkProd, dkSandbox, seProd, secretProd);
        await ctx.SaveChangesAsync();

        // Installed apps: the catalogue module on Denmark and on the Secret solution, and
        // one of our own on Denmark that a delivery below carried there.
        ctx.CustomerModules.Add(new CustomerModule
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Payroll", Publisher = "CRONUS Partner",
            AppId = PayrollAppId, CreatedAt = now,
        });
        ctx.OeEnvironmentApps.AddRange(
            NewApp(dkProd.Id, PayrollAppId, "CRONUS Payroll", "CRONUS Partner", "3.1.0.0"),
            NewApp(dkProd.Id, OurAppId, "CRONUS Core", "Our House", "1.0.0.0"),
            NewApp(secretProd.Id, PayrollAppId, "CRONUS Payroll", "CRONUS Partner", "2.0.0.0"));

        // The update team: the viewer holds the flag, and the team is on Denmark only.
        var team = new Team { OrganizationId = TestDb.DefaultOrgId, Name = "Upgrades", CreatedAt = now, UpdatedAt = now };
        ctx.Teams.Add(team);
        await ctx.SaveChangesAsync();
        ctx.TeamMembers.Add(new TeamMember
        {
            OrganizationId = TestDb.DefaultOrgId, TeamId = team.Id, UserId = ViewerId, ManagesUpdates = true, CreatedAt = now,
        });
        ctx.OeProjectTeams.Add(new OeProjectTeam
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = denmark.Id, TeamId = team.Id, CreatedAt = now,
        });

        // Workbench history on Denmark's production: an older move, a newer start, and
        // (added below) a booking still waiting for its slot.
        ctx.OeEnvironmentUpgradeActions.AddRange(
            NewAction(denmark.Id, dkProd.Id, UpgradeActionKind.PushDateToLatest, UpgradeActionStatus.Sent, now.AddDays(-2), now.AddDays(-2)),
            NewAction(denmark.Id, dkProd.Id, UpgradeActionKind.RunNow, UpgradeActionStatus.Sent, now.AddDays(-1), now.AddDays(-1)),
            NewAction(secret.Id, secretProd.Id, UpgradeActionKind.RunNow, UpgradeActionStatus.Sent, now, now));
        await ctx.SaveChangesAsync();
        ctx.OeEnvironmentUpgradeActions.Add(
            NewAction(denmark.Id, dkProd.Id, UpgradeActionKind.PushDateToLatest, UpgradeActionStatus.Pending, now.AddDays(-3), now.AddDays(5)));

        // Customer tab rows.
        ctx.OeProjectContacts.AddRange(
            new OeProjectContact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = denmark.Id, Type = ProjectContactType.Customer,
                Name = "Lars Nielsen", Company = "CRONUS Denmark A/S", Email = "lars@cronus.example",
                Phone = "+45 12 34 56 78", CreatedAt = now,
            },
            new OeProjectContact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = secret.Id, Type = ProjectContactType.Customer,
                Name = "Hidden Person", Email = "hidden@cronus.example", CreatedAt = now,
            });
        ctx.OeProjectPeople.AddRange(
            new OeProjectPerson { OrganizationId = TestDb.DefaultOrgId, ProjectId = denmark.Id, UserId = AnneId, Role = ProjectPersonRole.Consultant, Areas = "finance", CreatedAt = now },
            new OeProjectPerson { OrganizationId = TestDb.DefaultOrgId, ProjectId = sweden.Id, UserId = AnneId, Role = ProjectPersonRole.Developer, CreatedAt = now },
            new OeProjectPerson { OrganizationId = TestDb.DefaultOrgId, ProjectId = secret.Id, UserId = AnneId, Role = ProjectPersonRole.Architect, CreatedAt = now });
        ctx.OeProjectIntegrations.Add(new OeProjectIntegration
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = denmark.Id, Name = "Webshop",
            Direction = ProjectIntegrationDirection.Both, CreatedAt = now,
        });
        await ctx.SaveChangesAsync();

        // Deliveries: Denmark's failed, Sweden's deployed (newer), the Secret one failed.
        await AddDeliveryAsync(ctx, denmark.Id, dkProd.Id, ProjectDeliveryStatus.Failed, now.AddHours(-3),
            "The app did not install.", OurAppId);
        await AddDeliveryAsync(ctx, sweden.Id, seProd.Id, ProjectDeliveryStatus.Deployed, now.AddHours(-2), null, OurAppId);
        await AddDeliveryAsync(ctx, secret.Id, secretProd.Id, ProjectDeliveryStatus.Failed, now.AddHours(-1), "Secret failure.", OurAppId);

        return new Seeded(denmark.Id, sweden.Id, secret.Id, dkProd.Id, dkSandbox.Id, seProd.Id, secretProd.Id);
    }

    /// <summary>A planned upgrade with a line per environment, written straight to the tables as the viewer made it.</summary>
    private async Task<int> SeedUpgradeAsync(string name, string target, bool closed, params int[] environmentIds)
    {
        var now = DateTime.UtcNow;
        await using var ctx = _db.NewContext();
        var projects = ctx.OeProjectEnvironments.Where(e => environmentIds.Contains(e.Id))
            .ToDictionary(e => e.Id, e => e.ProjectId);
        var upgrade = new OeEnvironmentUpgrade
        {
            OrganizationId = TestDb.DefaultOrgId, Name = name, TargetVersion = target,
            CreatedByUserId = ViewerId, CreatedBy = "Viewer <viewer@example.com>", CreatedAt = now, UpdatedAt = now,
            ClosedAt = closed ? now : null, ClosedByUserId = closed ? ViewerId : null,
            ClosedBy = closed ? "Viewer <viewer@example.com>" : null,
            Lines = environmentIds.Select(e => new OeEnvironmentUpgradeLine
            {
                OrganizationId = TestDb.DefaultOrgId, EnvironmentId = e, ProjectId = projects[e],
                IsOpen = !closed, AddedAt = now,
            }).ToList(),
        };
        ctx.OeEnvironmentUpgrades.Add(upgrade);
        await ctx.SaveChangesAsync();
        return upgrade.Id;
    }

    private static User NewUser(int id, string email, string name) => new()
    {
        Id = id, OrganizationId = TestDb.DefaultOrgId, Email = email, PasswordHash = "x", DisplayName = name,
        Role = UserRole.User, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
    };

    private static OeProject NewProject(string name, ProjectVisibility visibility, int? ownerId, long quotaKb) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, Name = name, Visibility = visibility, CreatedByUserId = ownerId,
        BcStorageQuotaKb = quotaKb, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static OeProjectEnvironment NewEnvironment(int projectId, string name, string type, string status, string version, long kb) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = name, Type = type, Status = status,
        Version = version, BcDatabaseKb = kb, FetchedAt = DateTime.UtcNow,
    };

    private static OeEnvironmentApp NewApp(int environmentId, Guid appId, string name, string publisher, string version) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, EnvironmentId = environmentId, AppId = appId, Name = name,
        Publisher = publisher, Version = version, FetchedAt = DateTime.UtcNow,
    };

    private static OeEnvironmentUpgradeAction NewAction(
        int projectId, int environmentId, UpgradeActionKind kind, UpgradeActionStatus status, DateTime requestedAt, DateTime executeAfter) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, EnvironmentId = environmentId, Kind = kind,
        Status = status, RequestedByUserId = ViewerId, RequestedBy = "Viewer <viewer@example.com>",
        RequestedAt = requestedAt, ExecuteAfter = executeAfter,
        SentAt = status == UpgradeActionStatus.Sent ? executeAfter : null,
    };

    private static async Task AddDeliveryAsync(
        AppDbContext ctx, int projectId, int environmentId, string status, DateTime createdAt, string? failure, Guid appId)
    {
        var pipeline = new OePipeline { OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Build", CreatedAt = createdAt, UpdatedAt = createdAt };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        var releasePipeline = new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "To production",
            BuildPipelineId = pipeline.Id, ProjectEnvironmentId = environmentId,
            DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
            CreatedAt = createdAt, UpdatedAt = createdAt,
        };
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipeline.Id,
            Status = ProjectBuildStatus.Ready, StartedAt = createdAt, FinishedAt = createdAt,
        };
        ctx.OeReleasePipelines.Add(releasePipeline);
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();

        var delivery = new OeProjectDelivery
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, ReleasePipelineId = releasePipeline.Id,
            ProjectBuildId = build.Id, TriggeredByUserId = ViewerId, EnvironmentName = "Production",
            ScheduledFor = createdAt, Status = status, FailureMessage = failure,
            StartedAt = createdAt, FinishedAt = createdAt.AddMinutes(5), CreatedAt = createdAt, UpdatedAt = createdAt,
        };
        delivery.Results.Add(new OeProjectDeliveryResult
        {
            OrganizationId = TestDb.DefaultOrgId, Ordering = 0, AppId = appId.ToString(), AppName = "CRONUS Core",
            AppVersion = "1.0.0.0",
            Status = status == ProjectDeliveryStatus.Failed ? ProjectDeliveryResultStatus.Failed : ProjectDeliveryResultStatus.Completed,
            Message = failure, CreatedAt = createdAt, UpdatedAt = createdAt,
        });
        ctx.OeProjectDeliveries.Add(delivery);
        await ctx.SaveChangesAsync();
    }
}
