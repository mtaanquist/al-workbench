using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// Linking a service tier an on-premises gateway reports to a Solution (#1366): who may,
/// what may not be linked twice, what a linked row carries and how the inventory keeps it
/// in step, and the readers that must see it (production facts, modules, MCP) or must
/// not (Upgrades). See <c>.design/on-prem-gateway.md</c>, "An on-premises environment is
/// an OeProjectEnvironment row".
/// </summary>
public sealed class OnPremisesEnvironmentServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private const string AppA = "8c1a6b1e-4a35-4b5a-9a8f-0c7d2e3f4a51";
    private const string AppB = "437dbf0e-84ff-417a-965d-ed2bb9650972";

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(Start);
    private User _admin = null!;
    private User _editor = null!;
    private int _gateway;

    public OnPremisesEnvironmentServiceTests()
    {
        SetUpAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _db.Dispose();

    private async Task SetUpAsync()
    {
        _admin = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Admin, "admin@cronus.example");
        _editor = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Editor, "consultant@cronus.example");
        using var key = GatewayTestKit.NewKey();
        _gateway = (await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key, approve: true)).GatewayId;
        ActAs(_admin);
    }

    private void ActAs(User user)
    {
        _db.OrgContext.CurrentUserId = user.Id;
        _db.OrgContext.IsSiteAdmin = false;
    }

    // ---------------------------------------------------------------- building blocks

    private OnPremisesEnvironmentService Service(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _clock, NullLogger<OnPremisesEnvironmentService>.Instance);

    private Task ReportAsync(params GatewayInstanceInventory[] instances) => ReportToAsync(_gateway, instances);

    private async Task ReportToAsync(int gatewayId, params GatewayInstanceInventory[] instances)
    {
        await using var ctx = _db.NewContext();
        var service = new GatewayInventoryService(ctx, _db.OrgContext,
            new GatewayCommandService(ctx, _db.OrgContext, new GatewayCommandSignal(), _clock, NullLogger<GatewayCommandService>.Instance),
            _clock, NullLogger<GatewayInventoryService>.Instance);
        var report = new GatewayInventoryReport
        {
            Roster = instances.Select(i => new GatewayPolicyTier { Machine = i.Machine, Instance = i.Name }).ToList(),
            Instances = instances,
        };
        (await service.ApplyReportAsync(gatewayId, report)).Should().BeTrue();
    }

    private static GatewayInstanceInventory Tier(string name, string environment, params GatewayTenantInventory[] tenants) => new()
    {
        Machine = "CRONUS-BC01",
        Name = name,
        Environment = environment,
        State = "Running",
        Version = "25.4.29661.29927",
        Configuration = new GatewayInstanceConfiguration { DatabaseServer = "CRONUS-SQL", DatabaseName = name, Multitenant = tenants.Length > 1 },
        Tenants = tenants,
    };

    private static GatewayTenantInventory Tenant(string id, params GatewayAppInventory[] apps) =>
        new() { Id = id, State = "Operational", Apps = apps };

    private static GatewayAppInventory App(string id, string name, string version = "1.0.0.0", string publisher = "CRONUS Partner A/S") =>
        new() { AppId = id, Name = name, Publisher = publisher, Version = version, IsInstalled = true };

    private async Task<int> SolutionAsync(string name, ProjectHostingType? hosting = ProjectHostingType.OurCloud,
        ProjectVisibility visibility = ProjectVisibility.Public, string? bcVersion = null)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = name, HostingType = hosting, Visibility = visibility,
            BcVersion = bcVersion, CreatedByUserId = _admin.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private async Task<int> InstanceIdAsync(string name, int? gatewayId = null)
    {
        await using var ctx = _db.NewContext();
        return await ctx.GatewayInstances.AsNoTracking()
            .Where(i => i.Name == name && i.MissingSince == null && (gatewayId == null || i.GatewayId == gatewayId))
            .Select(i => i.Id).SingleAsync();
    }

    private async Task<int> LinkAsync(int projectId, string instance, string name, string tenant = "default")
    {
        await using var ctx = _db.NewContext();
        return await Service(ctx).LinkAsync(projectId, new LinkTierInput(await InstanceIdAsync(instance), tenant, name));
    }

    private async Task<OeProjectEnvironment> RowAsync(int environmentId)
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == environmentId);
    }

    private async Task<List<OeEnvironmentApp>> AppsAsync(int environmentId)
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeEnvironmentApps.AsNoTracking().Where(a => a.EnvironmentId == environmentId).OrderBy(a => a.Name).ToListAsync();
    }

    private UpgradeFleetService Fleet(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), new EnvironmentRefreshQueue(), NullLogger<UpgradeFleetService>.Instance);

    // ---------------------------------------------------------------- linking

    [Fact]
    public async Task Linking_makes_an_on_premises_environment_carrying_what_the_gateway_read()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("default", App(AppA, "CRONUS Freight"), App(AppB, "Base Application", "25.4.0.0", "Microsoft"))));
        var solution = await SolutionAsync("CRONUS Danmark");

        var id = await LinkAsync(solution, "BC250", " Production ");

        var row = await RowAsync(id);
        row.Kind.Should().Be(OeEnvironmentKind.OnPremises);
        row.GatewayInstanceId.Should().Be(await InstanceIdAsync("BC250"));
        row.TenantId.Should().Be("default");
        row.Name.Should().Be("Production");
        row.Type.Should().Be("Production");
        row.Version.Should().Be("25.4.29661.29927");
        row.Status.Should().Be(BcEnvironmentStatus.Active, "a running tier reads as the status the existing checks know");
        row.MissingSince.Should().BeNull();
        (await AppsAsync(id)).Select(a => (a.Name, a.Version)).Should().Equal(("Base Application", "25.4.0.0"), ("CRONUS Freight", "1.0.0.0"));
    }

    [Fact]
    public async Task Only_an_admin_can_list_link_or_unlink_service_tiers()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("default")));
        var solution = await SolutionAsync("CRONUS Danmark");
        var linked = await LinkAsync(solution, "BC250", "Production");
        var instance = await InstanceIdAsync("BC250");
        ActAs(_editor);

        await using var ctx = _db.NewContext();
        var service = Service(ctx);
        (await service.CanLinkAsync()).Should().BeFalse();
        await FluentActions.Invoking(() => service.ListLinkableTiersAsync(solution)).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Invoking(() => service.LinkAsync(solution, new LinkTierInput(instance, "default", "Again"))).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Invoking(() => service.UnlinkAsync(solution, linked)).Should().ThrowAsync<UnauthorizedAccessException>();
        (await RowAsync(linked)).MissingSince.Should().BeNull();
    }

    [Fact]
    public async Task One_tier_and_tenant_cannot_be_linked_to_two_solutions()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("cronus-dk"), Tenant("cronus-se")));
        var denmark = await SolutionAsync("CRONUS Danmark");
        var sweden = await SolutionAsync("CRONUS Sverige");
        await LinkAsync(denmark, "BC250", "Production", tenant: "cronus-dk");

        var twice = () => LinkAsync(sweden, "BC250", "Production", tenant: "CRONUS-DK");
        var again = () => LinkAsync(denmark, "BC250", "Second", tenant: "cronus-dk");

        (await twice.Should().ThrowAsync<PlanValidationException>()).Which.Errors["InstanceId"].Should().Contain("CRONUS Danmark");
        (await again.Should().ThrowAsync<PlanValidationException>()).Which.Errors["InstanceId"].Should().Contain("already linked to this Solution");
        // The other tenant on the same server is free.
        (await LinkAsync(sweden, "BC250", "Production", tenant: "cronus-se")).Should().BePositive();

        await using var ctx = _db.NewContext();
        var tiers = await Service(ctx).ListLinkableTiersAsync(sweden);
        tiers.Should().HaveCount(2);
        tiers.Single(t => t.TenantId == "cronus-dk").LinkedToProjectName.Should().Be("CRONUS Danmark");
        tiers.Single(t => t.TenantId == "cronus-se").LinkedToThisSolution.Should().BeTrue();
    }

    [Fact]
    public async Task The_database_refuses_a_second_live_link_even_past_the_service()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("default")));
        var denmark = await SolutionAsync("CRONUS Danmark");
        var sweden = await SolutionAsync("CRONUS Sverige");
        await LinkAsync(denmark, "BC250", "Production");
        var instance = await InstanceIdAsync("BC250");

        await using var ctx = _db.NewContext();
        ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = sweden, Name = "Production", Kind = OeEnvironmentKind.OnPremises,
            GatewayInstanceId = instance, TenantId = "default", FetchedAt = DateTime.UtcNow,
        });

        await FluentActions.Invoking(() => ctx.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Linking_validates_the_name_the_tier_and_the_hosting()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("cronus-dk"), Tenant("cronus-se")));
        var onPremises = await SolutionAsync("CRONUS Danmark");
        var cloud = await SolutionAsync("CRONUS Online", ProjectHostingType.MicrosoftCloud);
        var instance = await InstanceIdAsync("BC250");

        await using var ctx = _db.NewContext();
        var service = Service(ctx);
        var noName = () => service.LinkAsync(onPremises, new LinkTierInput(instance, "cronus-dk", " "));
        var longName = () => service.LinkAsync(onPremises, new LinkTierInput(instance, "cronus-dk", new string('x', OnPremisesEnvironmentService.MaxNameLength + 1)));
        var noTier = () => service.LinkAsync(onPremises, new LinkTierInput(null, null, "Production"));
        var noSuchTenant = () => service.LinkAsync(onPremises, new LinkTierInput(instance, "cronus-no", "Production"));
        var inTheCloud = () => service.LinkAsync(cloud, new LinkTierInput(instance, "cronus-dk", "Production"));

        (await noName.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
        (await longName.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
        (await noTier.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("InstanceId");
        (await noSuchTenant.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("TenantId");
        (await inTheCloud.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("HostingType");
        (await ctx.OeProjectEnvironments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Two_tiers_in_one_solution_cannot_share_a_name()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("default")), Tier("BC250TEST", "test", Tenant("default")));
        var solution = await SolutionAsync("CRONUS Danmark");
        await LinkAsync(solution, "BC250", "Production");

        var same = () => LinkAsync(solution, "BC250TEST", "production");

        (await same.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task Unlinking_marks_the_row_no_longer_linked_drops_its_apps_and_linking_again_revives_it()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("default", App(AppA, "CRONUS Freight"))));
        var denmark = await SolutionAsync("CRONUS Danmark");
        var sweden = await SolutionAsync("CRONUS Sverige");
        var id = await LinkAsync(denmark, "BC250", "Production");

        _clock.Advance(TimeSpan.FromMinutes(5));
        await using (var ctx = _db.NewContext()) await Service(ctx).UnlinkAsync(denmark, id);

        (await RowAsync(id)).MissingSince.Should().Be(Start.UtcDateTime.AddMinutes(5));
        (await AppsAsync(id)).Should().BeEmpty();

        // While unlinked, the tier is free for another solution...
        await using (var ctx = _db.NewContext())
        {
            (await Service(ctx).ListLinkableTiersAsync(sweden)).Single().IsFree.Should().BeTrue();
        }

        // ...and linking it back here brings the same row, and its apps, back.
        var again = await LinkAsync(denmark, "BC250", "Live");
        again.Should().Be(id);
        var row = await RowAsync(id);
        (row.MissingSince, row.Name).Should().Be((null, "Live"));
        (await AppsAsync(id)).Should().ContainSingle().Which.Name.Should().Be("CRONUS Freight");
    }

    // ---------------------------------------------------------------- an unlinked row gives its name up (#1398)

    [Fact]
    public async Task A_rebuilt_gateways_tier_can_be_linked_as_production_again()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("default")));
        var solution = await SolutionAsync("CRONUS Danmark");
        var old = await LinkAsync(solution, "BC250", "Production");
        await using (var ctx = _db.NewContext()) await Service(ctx).UnlinkAsync(solution, old);

        // The customer's IT rebuilds the gateway: a new registration, reporting the same
        // server as a tier AL Workbench has never seen.
        using var key = GatewayTestKit.NewKey();
        var rebuilt = (await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key, approve: true)).GatewayId;
        await ReportToAsync(rebuilt, Tier("BC250", "production", Tenant("default")));
        ActAs(_admin);

        int linked;
        await using (var ctx = _db.NewContext())
            linked = await Service(ctx).LinkAsync(solution, new LinkTierInput(await InstanceIdAsync("BC250", rebuilt), "default", "Production"));

        linked.Should().NotBe(old, "it is a different tier, so it is a different row");
        var row = await RowAsync(linked);
        (row.Name, row.MissingSince).Should().Be(("Production", (DateTime?)null));
        (await RowAsync(old)).MissingSince.Should().NotBeNull("the old row stays unlinked for its pipelines' history");
    }

    [Fact]
    public async Task Relinking_under_a_name_another_live_row_has_taken_is_refused()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("default")), Tier("BC250NEW", "production", Tenant("default")));
        var solution = await SolutionAsync("CRONUS Danmark");
        var old = await LinkAsync(solution, "BC250", "Production");
        await using (var ctx = _db.NewContext()) await Service(ctx).UnlinkAsync(solution, old);
        await LinkAsync(solution, "BC250NEW", "Production");

        // The old tier comes back under the name the new one now holds: refused, rather
        // than reviving a second "Production" or renaming either behind the user's back.
        var same = () => LinkAsync(solution, "BC250", "production");
        (await same.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
        (await RowAsync(old)).MissingSince.Should().NotBeNull();

        // Under a name of its own it revives its own row.
        (await LinkAsync(solution, "BC250", "Old production")).Should().Be(old);
        (await RowAsync(old)).Name.Should().Be("Old production");
    }

    [Fact]
    public async Task A_failed_link_leaves_the_context_usable_for_the_next_one()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("default")), Tier("BC250TEST", "test", Tenant("default")));
        var denmark = await SolutionAsync("CRONUS Danmark");
        var sweden = await SolutionAsync("CRONUS Sverige");
        var production = await InstanceIdAsync("BC250");
        var test = await InstanceIdAsync("BC250TEST");

        // Another Admin links the same tier to another Solution between this link's
        // checks and its save, so the database refuses it.
        var race = new RaceInterceptor(async () =>
        {
            await using var other = _db.NewContext();
            other.OeProjectEnvironments.Add(new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = sweden, Name = "Production", Type = "Production",
                Kind = OeEnvironmentKind.OnPremises, GatewayInstanceId = production, TenantId = "default", FetchedAt = DateTime.UtcNow,
            });
            await other.SaveChangesAsync();
        });
        await using var ctx = _db.NewContext(race);
        var service = Service(ctx);

        var lost = () => service.LinkAsync(denmark, new LinkTierInput(production, "default", "Production"));
        await lost.Should().ThrowAsync<PlanValidationException>();

        // The same context (a Blazor circuit's) links the next tier without dragging the
        // failed row along.
        var next = await service.LinkAsync(denmark, new LinkTierInput(test, "default", "Test"));
        (await RowAsync(next)).Name.Should().Be("Test");
        await using var read = _db.NewContext();
        (await read.OeProjectEnvironments.AsNoTracking().CountAsync(e => e.ProjectId == denmark)).Should().Be(1);
    }

    private sealed class RaceInterceptor(Func<Task> once) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private Func<Task>? _once = once;

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_once is { } run)
            {
                _once = null;
                await run();
            }
            return result;
        }
    }

    [Fact]
    public async Task Another_organisations_service_tier_cannot_be_linked()
    {
        var otherAdmin = await GatewayTestKit.SeedUserAsync(_db, TestDb.OtherOrgId, UserRole.Admin, "admin@cronus-other.example");
        using var key = GatewayTestKit.NewKey();
        var theirs = (await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.OtherOrgId, otherAdmin.Id, key, approve: true)).GatewayId;
        int theirTier;
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        _db.OrgContext.CurrentUserId = otherAdmin.Id;
        try
        {
            await ReportToAsync(theirs, Tier("BC250", "production", Tenant("default")));
            theirTier = await InstanceIdAsync("BC250", theirs);
        }
        finally
        {
            _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
            ActAs(_admin);
        }
        var solution = await SolutionAsync("CRONUS Danmark");

        await using var ctx = _db.NewContext();
        var link = () => Service(ctx).LinkAsync(solution, new LinkTierInput(theirTier, "default", "Production"));

        (await link.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("InstanceId");
        (await ctx.OeProjectEnvironments.CountAsync()).Should().Be(0);
        (await Service(ctx).ListLinkableTiersAsync(solution)).Should().BeEmpty("another organisation's gateway is not listed either");
    }

    [Fact]
    public async Task A_server_reporting_two_tenants_is_multitenant_even_without_the_flag()
    {
        // The gateway could not read the server's configuration, so the flag is unknown;
        // two tenants say it all the same.
        await ReportAsync(Tier("BC250", "production", Tenant("cronus-dk"), Tenant("cronus-se")) with
        {
            Configuration = new GatewayInstanceConfiguration { DatabaseServer = "CRONUS-SQL", DatabaseName = "BC250", Multitenant = null },
        });
        var solution = await SolutionAsync("CRONUS Danmark");

        await using (var ctx = _db.NewContext())
        {
            var tiers = await Service(ctx).ListLinkableTiersAsync(solution);
            tiers.Select(t => (t.TenantId, t.Multitenant)).Should().BeEquivalentTo([("cronus-dk", true), ("cronus-se", true)]);
            var noTenant = () => Service(ctx).LinkAsync(solution, new LinkTierInput(tiers[0].InstanceId, null, "Production"));
            (await noTenant.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("TenantId");
        }

        var id = await LinkAsync(solution, "BC250", "Production", tenant: "cronus-se");
        (await RowAsync(id)).TenantId.Should().Be("cronus-se");
    }

    // ---------------------------------------------------------------- what the gateway stops reporting

    [Fact]
    public async Task A_failed_read_keeps_the_time_of_the_last_good_one()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("default")));
        var solution = await SolutionAsync("CRONUS Danmark");
        var id = await LinkAsync(solution, "BC250", "Production");
        var first = (await RowAsync(id)).FetchedAt;

        _clock.Advance(TimeSpan.FromMinutes(15));
        await ReportAsync(Tier("BC250", "production", Tenant("default")) with { Error = "Access is denied." });

        (await RowAsync(id)).FetchedAt.Should().Be(first, "nothing was read, so the facts are as old as they were");
    }

    [Fact]
    public async Task A_tenant_the_gateway_stops_reporting_keeps_its_facts_as_no_longer_reported()
    {
        await ReportAsync(Tier("BC250", "production",
            Tenant("cronus-dk", App(AppA, "Document Capture", "25.1.0.0", "Continia Software")), Tenant("cronus-se")));
        var solution = await SolutionAsync("CRONUS Danmark", bcVersion: "BC 25 (typed)");
        await using (var ctx = _db.NewContext())
            await Modules(ctx).SaveCatalogModuleAsync(null, new CatalogModuleInput("Continia Document Capture", "Continia Software", AppA));
        var id = await LinkAsync(solution, "BC250", "Production", tenant: "cronus-dk");
        (await BasicsAsync(solution)).FromBusinessCentral.Should().BeTrue();

        // The customer's IT removes the tenant and upgrades the server.
        _clock.Advance(TimeSpan.FromMinutes(15));
        await ReportAsync(Tier("BC250", "production", Tenant("cronus-se")) with { Version = "26.0.1.2" });

        var row = await RowAsync(id);
        row.Version.Should().Be("25.4.29661.29927", "the server's new version is not the version of a tenant that is gone");
        (await AppsAsync(id)).Should().ContainSingle();

        await using var read = _db.NewContext();
        var fleetRow = (await Fleet(read).GetEnvironmentAsync(id))!.Fleet;
        fleetRow.OnPremises!.NoLongerReportedSince.Should().Be(Start.UtcDateTime.AddMinutes(15));
        ALDevToolbox.Components.Shared.FleetRowState.IsOutOfTouch(fleetRow, _clock.GetUtcNow().UtcDateTime).Should().BeTrue();
        ALDevToolbox.Components.Shared.FleetRowState.StatusWord(fleetRow, _clock.GetUtcNow().UtcDateTime).Should().Be("Last seen running");

        (await BasicsAsync(solution)).EffectiveVersion.Should().Be("BC 25 (typed)", "a tenant that is gone is not the customer's current version");
        (await Modules(read).GetSolutionModulesAsync(solution)).ReadFromEnvironment.Should().BeFalse();
        var capture = (await Modules(read).ListCatalogAsync()).Single().Id;
        (await Modules(read).ListProjectIdsWithModuleAsync(capture)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_module_filter_matches_the_card_for_a_linked_tier()
    {
        // Production has Document Capture; only the test tier has the sales app. The
        // card reads Production, so the Solutions filter must find the first and not the
        // second.
        await ReportAsync(
            Tier("BC250", "production", Tenant("default", App(AppA, "Document Capture", "25.1.0.0", "Continia Software"))),
            Tier("BC250TEST", "test", Tenant("default", App(AppB, "CRONUS Sales", "2.0.0.0"))));
        var solution = await SolutionAsync("CRONUS Danmark");
        await LinkAsync(solution, "BC250", "Production");
        await LinkAsync(solution, "BC250TEST", "Test");

        await using var ctx = _db.NewContext();
        var modules = Modules(ctx);
        await modules.SaveCatalogModuleAsync(null, new CatalogModuleInput("Continia Document Capture", "Continia Software", AppA));
        await modules.SaveCatalogModuleAsync(null, new CatalogModuleInput("CRONUS Sales", "CRONUS Partner A/S", AppB));
        var catalog = await modules.ListCatalogAsync();
        var capture = catalog.Single(m => m.AppId == Guid.Parse(AppA)).Id;
        var sales = catalog.Single(m => m.AppId == Guid.Parse(AppB)).Id;

        (await modules.ListProjectIdsWithModuleAsync(capture)).Should().Equal(solution);
        (await modules.ListProjectIdsWithModuleAsync(sales)).Should().BeEmpty("the card shows Production's apps, not the test tier's");
        (await modules.GetSolutionModulesAsync(solution)).Modules.Select(m => m.Name).Should().Equal("Continia Document Capture");
    }

    [Fact]
    public async Task Linking_and_unlinking_are_audited()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("default")));
        var solution = await SolutionAsync("CRONUS Danmark");
        var instance = await InstanceIdAsync("BC250");

        int id;
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
            id = await Service(ctx).LinkAsync(solution, new LinkTierInput(instance, "default", "Production"));
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
            await Service(ctx).UnlinkAsync(solution, id);

        await using var read = _db.NewContext();
        var rows = await read.AuditLog.AsNoTracking()
            .Where(a => a.EntityType == AuditEntityType.ProjectEnvironment && a.EntityId == id)
            .OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
        rows.Should().Equal(AuditAction.Created, AuditAction.Updated);
    }

    // ---------------------------------------------------------------- the inventory keeps it in step

    [Fact]
    public async Task The_next_inventory_rewrites_type_version_status_and_apps()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("default", App(AppA, "CRONUS Freight"))));
        var solution = await SolutionAsync("CRONUS Danmark");
        var id = await LinkAsync(solution, "BC250", "Main");

        // The customer's IT re-labels the tier as a test server, upgrades it, stops it,
        // uninstalls one app and installs another.
        _clock.Advance(TimeSpan.FromMinutes(15));
        await ReportAsync(Tier("BC250", "test", Tenant("default", App(AppB, "CRONUS Sales", "2.0.0.0"))) with
        {
            Version = "26.0.1.2",
            State = "Stopped",
        });

        var row = await RowAsync(id);
        row.Type.Should().Be("Sandbox", "test and development are Sandbox; production is Production");
        row.Version.Should().Be("26.0.1.2");
        row.Status.Should().Be("Stopped");
        row.FetchedAt.Should().Be(Start.UtcDateTime.AddMinutes(15));
        (await AppsAsync(id)).Select(a => (a.Name, a.Version)).Should().Equal(("CRONUS Sales", "2.0.0.0"));
    }

    [Fact]
    public async Task An_unlinked_row_is_left_alone_by_the_inventory()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("default", App(AppA, "CRONUS Freight"))));
        var solution = await SolutionAsync("CRONUS Danmark");
        var id = await LinkAsync(solution, "BC250", "Production");
        await using (var ctx = _db.NewContext()) await Service(ctx).UnlinkAsync(solution, id);

        await ReportAsync(Tier("BC250", "production", Tenant("default", App(AppA, "CRONUS Freight", "9.0.0.0"))) with { Version = "26.0.0.0" });

        (await RowAsync(id)).Version.Should().Be("25.4.29661.29927");
        (await AppsAsync(id)).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- who sees it

    [Fact]
    public async Task Visibility_follows_the_solutions_access()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("default")), Tier("BC140", "production", Tenant("default")));
        var open = await SolutionAsync("CRONUS Danmark");
        var secret = await SolutionAsync("CRONUS Secret", visibility: ProjectVisibility.Private);
        var openTier = await LinkAsync(open, "BC250", "Production");
        var secretTier = await LinkAsync(secret, "BC140", "Production");
        ActAs(_editor);

        await using var ctx = _db.NewContext();
        var fleet = await Fleet(ctx).ListFleetAsync(includeOnPremises: true);
        fleet.Select(r => r.EnvironmentId).Should().Equal(openTier);
        fleet.Single().IsOnPremises.Should().BeTrue();
        fleet.Single().OnPremises!.TierName.Should().Be("CRONUS-BC01 / BC250");
        (await Fleet(ctx).GetEnvironmentAsync(secretTier)).Should().BeNull("a Private solution the caller is not on is not there at all");
        (await Fleet(ctx).GetEnvironmentAsync(openTier)).Should().NotBeNull();
    }

    [Fact]
    public async Task On_premises_tiers_are_absent_from_upgrades()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("default")));
        var solution = await SolutionAsync("CRONUS Danmark");
        var tier = await LinkAsync(solution, "BC250", "Production");

        await using var ctx = _db.NewContext();
        var fleet = Fleet(ctx);
        (await fleet.ListFleetAsync()).Should().BeEmpty("the Upgrades fleet is Business Central online's");
        var upgrades = new EnvironmentUpgradeService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), fleet,
            _clock, NullLogger<EnvironmentUpgradeService>.Instance);
        var upgrade = await upgrades.CreateAsync("Wave 26.0", "26.0", null, null);

        var result = await upgrades.AddLinesAsync(upgrade, [tier]);

        result.Added.Should().BeEmpty();
        result.Refused.Should().ContainKey(tier.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---------------------------------------------------------------- production facts and modules

    [Fact]
    public async Task A_linked_production_tier_gives_the_version_and_modules_and_typed_values_return_on_unlink()
    {
        await ReportAsync(
            Tier("BC250", "production", Tenant("default", App(AppA, "Document Capture", "25.1.0.0", "Continia Software"))),
            Tier("BC250TEST", "test", Tenant("default", App(AppA, "Document Capture", "26.0.0.0", "Continia Software"))));
        var solution = await SolutionAsync("CRONUS Danmark", bcVersion: "BC 25 (typed)");
        int captureId;
        await using (var ctx = _db.NewContext())
        {
            var modules = Modules(ctx);
            await modules.SaveCatalogModuleAsync(null, new CatalogModuleInput("Continia Document Capture", "Continia Software", AppA));
            captureId = (await modules.ListCatalogAsync()).Single().Id;
            await modules.SaveSolutionModuleAsync(solution, null, new SolutionModuleInput(captureId, "24.0 (typed)", null));
        }

        // A test tier alone changes nothing: production is what the customer runs on.
        await LinkAsync(solution, "BC250TEST", "Test");
        (await BasicsAsync(solution)).EffectiveVersion.Should().Be("BC 25 (typed)");

        var production = await LinkAsync(solution, "BC250", "Production");
        var basics = await BasicsAsync(solution);
        basics.FromBusinessCentral.Should().BeTrue();
        basics.EffectiveVersion.Should().Be("25.4.29661.29927");
        await using (var ctx = _db.NewContext())
        {
            var read = await Modules(ctx).GetSolutionModulesAsync(solution);
            read.ReadFromEnvironment.Should().BeTrue();
            read.FromOnPremisesTier.Should().BeTrue();
            read.EnvironmentId.Should().Be(production);
            read.Modules.Select(m => (m.Name, m.Version)).Should().Equal(("Continia Document Capture", "25.1.0.0"));
            var typing = () => Modules(ctx).SaveSolutionModuleAsync(solution, null, new SolutionModuleInput(captureId, "1.0", null));
            await typing.Should().ThrowAsync<PlanValidationException>("the linked tier says which version is installed");
        }

        await using (var ctx = _db.NewContext()) await Service(ctx).UnlinkAsync(solution, production);
        (await BasicsAsync(solution)).EffectiveVersion.Should().Be("BC 25 (typed)");
        await using (var ctx = _db.NewContext())
        {
            var read = await Modules(ctx).GetSolutionModulesAsync(solution);
            read.ReadFromEnvironment.Should().BeFalse();
            read.Modules.Select(m => (m.Name, m.Version)).Should().Equal(("Continia Document Capture", "24.0 (typed)"));
        }
    }

    private CustomerModuleService Modules(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), NullLogger<CustomerModuleService>.Instance);

    private ProjectCustomerInfoService CustomerInfo(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), NullLogger<ProjectCustomerInfoService>.Instance);

    private async Task<CustomerBasics> BasicsAsync(int solution)
    {
        await using var ctx = _db.NewContext();
        return (await CustomerInfo(ctx).GetBasicsAsync(solution))!;
    }

    // ---------------------------------------------------------------- the hosting rules, both ways

    [Fact]
    public async Task A_solution_with_a_linked_tier_cannot_move_to_microsofts_cloud()
    {
        await ReportAsync(Tier("BC250", "production", Tenant("default")));
        var solution = await SolutionAsync("CRONUS Danmark");
        var id = await LinkAsync(solution, "BC250", "Production");

        await using (var ctx = _db.NewContext())
        {
            var move = () => CustomerInfo(ctx).SaveBasicsAsync(solution, new CustomerBasicsInput(ProjectHostingType.MicrosoftCloud, null, null, null, null, null));
            (await move.Should().ThrowAsync<PlanValidationException>()).Which.Errors["HostingType"].Should().Contain("unlink");
            // Staying on-premises, with a linked tier, is fine.
            await CustomerInfo(ctx).SaveBasicsAsync(solution, new CustomerBasicsInput(ProjectHostingType.CustomerHardware, "BC 25", null, null, null, null));
        }

        await using (var ctx = _db.NewContext()) await Service(ctx).UnlinkAsync(solution, id);
        await using (var ctx = _db.NewContext())
            await CustomerInfo(ctx).SaveBasicsAsync(solution, new CustomerBasicsInput(ProjectHostingType.MicrosoftCloud, null, null, null, null, null));
    }

    [Fact]
    public async Task On_premises_hosting_is_refused_only_while_an_online_environment_exists()
    {
        var solution = await SolutionAsync("CRONUS Danmark", ProjectHostingType.MicrosoftCloud);
        await using (var seed = _db.NewContext())
        {
            seed.OeProjectEnvironments.Add(new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = solution, Name = "Production", Type = "Production", FetchedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var move = () => CustomerInfo(ctx).SaveBasicsAsync(solution, new CustomerBasicsInput(ProjectHostingType.OurCloud, null, null, null, null, null));
        (await move.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("HostingType");

        await ctx.OeProjectEnvironments.Where(e => e.ProjectId == solution).ExecuteDeleteAsync();
        await CustomerInfo(ctx).SaveBasicsAsync(solution, new CustomerBasicsInput(ProjectHostingType.OurCloud, null, null, null, null, null));
    }

    // ---------------------------------------------------------------- MCP

    [Fact]
    public async Task The_mcp_tools_return_linked_tiers_flagged_on_premises()
    {
        await ReportAsync(Tier("BC250", "test", Tenant("default", App(AppA, "CRONUS Freight"))));
        var solution = await SolutionAsync("CRONUS Danmark");
        var id = await LinkAsync(solution, "BC250", "Test");
        ActAs(_editor);

        await using var ctx = _db.NewContext();
        var tools = Tools(ctx);

        var listed = (await tools.ListEnvironmentsAsync()).Should().ContainSingle().Subject;
        listed.EnvironmentId.Should().Be(id);
        listed.OnPremises.Should().BeTrue();
        listed.Type.Should().Be("Sandbox");
        listed.OnPremisesTier!.Environment.Should().Be("Test");
        (listed.OnPremisesTier.Server, listed.OnPremisesTier.Instance, listed.OnPremisesTier.Tenant).Should().Be(("CRONUS-BC01", "BC250", "default"));
        listed.NextUpdate.Should().BeNull();

        var detail = await tools.GetEnvironmentAsync(id);
        detail.Environment.OnPremises.Should().BeTrue();
        detail.InstalledApps.Should().ContainSingle().Which.Name.Should().Be("CRONUS Freight");

        var brief = (await tools.GetSolutionAsync("CRONUS Danmark")).Environments.Should().ContainSingle().Subject;
        brief.OnPremises.Should().BeTrue();
        brief.GatewayOnline.Should().NotBeNull("an on-premises row says whether its gateway is online");
    }

    private DeliverTools Tools(AppDbContext ctx)
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
            new ProjectService(ctx, _db.OrgContext, access, discovery, _db.NewOfflineGitHubAccessService(ctx), _db.NewRepositoryReadProbe(ctx), NullLogger<ProjectService>.Instance),
            CustomerInfo(ctx),
            connections,
            Fleet(ctx),
            new UpgradeActionService(ctx, _db.OrgContext, access, connections, TimeProvider.System, NullLogger<UpgradeActionService>.Instance),
            new EnvironmentUpgradeService(ctx, _db.OrgContext, access, Fleet(ctx), TimeProvider.System, NullLogger<EnvironmentUpgradeService>.Instance),
            Modules(ctx),
            new DeliveryFeedService(ctx, access),
            access,
            _db.OrgContext,
            NullLogger<DeliverTools>.Instance);
    }
}
