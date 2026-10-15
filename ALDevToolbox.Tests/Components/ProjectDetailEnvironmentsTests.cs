using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Environments tab of an on-premises solution (#1366). Named users: a BC consultant
/// checking which version a hosted customer runs, who reads it, and the org Admin who
/// links the customer's servers, who gets the one obvious "Link service tier" action.
/// </summary>
public sealed class ProjectDetailEnvironmentsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private User _admin = null!;
    private User _consultant = null!;
    private int _gateway;

    public ProjectDetailEnvironmentsTests()
    {
        _ctx.AddAuthorization().SetAuthorized("admin@cronus.example");
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddSingleton(new EnvironmentRefreshQueue());
        _ctx.Services.AddScoped<UpgradeFleetService>();
        _ctx.Services.AddScoped<OnPremisesEnvironmentService>();
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        SetUpAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private async Task SetUpAsync()
    {
        _admin = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Admin, "admin@cronus.example");
        _consultant = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Editor, "consultant@cronus.example");
        using var key = GatewayTestKit.NewKey();
        _gateway = (await GatewayTestKit.RegisterAsync(_db, TimeProvider.System, TestDb.DefaultOrgId, _admin.Id, key, approve: true)).GatewayId;
        _db.OrgContext.CurrentUserId = _admin.Id;

        await using var ctx = _db.NewContext();
        var inventory = new GatewayInventoryService(ctx, _db.OrgContext,
            new GatewayCommandService(ctx, _db.OrgContext, new GatewayCommandSignal(), TimeProvider.System, NullLogger<GatewayCommandService>.Instance),
            TimeProvider.System, NullLogger<GatewayInventoryService>.Instance);
        var tier = new GatewayInstanceInventory
        {
            Machine = "CRONUS-BC01", Name = "BC250", Environment = "production", State = "Running", Version = "25.4.29661.29927",
            Configuration = new GatewayInstanceConfiguration { DatabaseServer = "CRONUS-SQL", DatabaseName = "BC250", Multitenant = false },
            Tenants = [new GatewayTenantInventory { Id = "default", State = "Operational", Apps = [] }],
        };
        await inventory.ApplyReportAsync(_gateway, new GatewayInventoryReport
        {
            Roster = [new GatewayPolicyTier { Machine = "CRONUS-BC01", Instance = "BC250" }],
            Instances = [tier],
        });
    }

    private async Task<int> SolutionAsync()
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Danmark", HostingType = ProjectHostingType.OurCloud,
            CreatedByUserId = _admin.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private async Task LinkAsync(int solution)
    {
        await using var ctx = _db.NewContext();
        var instance = await ctx.GatewayInstances.AsNoTracking().Select(i => i.Id).SingleAsync();
        await new OnPremisesEnvironmentService(ctx, _db.OrgContext, TimeProvider.System, NullLogger<OnPremisesEnvironmentService>.Instance)
            .LinkAsync(solution, new LinkTierInput(instance, "default", "Production"));
    }

    private IRenderedComponent<ProjectDetailEnvironments> Render(int id)
    {
        var cut = _ctx.Render<ProjectDetailEnvironments>(p => p.Add(c => c.Id, id).Add(c => c.SolutionName, "CRONUS Danmark"));
        cut.WaitForAssertion(() => cut.FindAll(".loading-block").Should().BeEmpty());
        return cut;
    }

    [Fact]
    public async Task An_admin_with_nothing_linked_is_offered_the_one_next_step()
    {
        var id = await SolutionAsync();

        var cut = Render(id);

        cut.Markup.Should().Contain("No service tiers linked yet");
        var link = cut.FindAll("button").Should().ContainSingle(b => b.TextContent.Contains("Link service tier")).Subject;
        link.ClassList.Should().NotContain("btn--primary", "Generate is the only primary button");
    }

    [Fact]
    public async Task A_consultant_with_nothing_linked_is_told_who_links_and_gets_no_button()
    {
        var id = await SolutionAsync();
        _db.OrgContext.CurrentUserId = _consultant.Id;

        var cut = Render(id);

        cut.Markup.Should().Contain("An administrator links");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Link service tier"));
    }

    [Fact]
    public async Task The_link_dialog_names_gateway_server_and_tier_and_suggests_a_name()
    {
        var id = await SolutionAsync();
        var cut = Render(id);

        cut.FindAll("button").Single(b => b.TextContent.Contains("Link service tier")).Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CRONUS-BC01 / BC250"));
        cut.Markup.Should().Contain("Gateway CRONUS-BC01");
        // The only free tier is chosen, and named with the customer's own word for it.
        cut.Find("#link-tier-name").GetAttribute("value").Should().Be("Production");
    }

    [Fact]
    public async Task A_linked_tier_shows_its_server_and_version_and_only_an_admin_can_unlink_it()
    {
        var id = await SolutionAsync();
        await LinkAsync(id);

        var cut = Render(id);
        cut.Markup.Should().Contain("CRONUS-BC01 / BC250").And.Contain("25.4.29661.29927").And.Contain("Single tenant");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Unlink");

        _db.OrgContext.CurrentUserId = _consultant.Id;
        var reader = Render(id);
        reader.Markup.Should().Contain("CRONUS-BC01 / BC250");
        reader.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Unlink");
    }

    // ---------------------------------------------------------------- the environment page's head

    private async Task<UpgradeFleetRow> FleetRowAsync(int solution)
    {
        await using var ctx = _db.NewContext();
        var id = await ctx.OeProjectEnvironments.AsNoTracking().Where(e => e.ProjectId == solution).Select(e => e.Id).SingleAsync();
        var fleet = new UpgradeFleetService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), new EnvironmentRefreshQueue(),
            NullLogger<UpgradeFleetService>.Instance);
        return (await fleet.GetEnvironmentAsync(id))!.Fleet;
    }

    private IRenderedComponent<ALDevToolbox.Components.Pages.Environments.OnPremisesEnvironmentSections> RenderHead(UpgradeFleetRow row, bool admin) =>
        _ctx.Render<ALDevToolbox.Components.Pages.Environments.OnPremisesEnvironmentSections>(p => p
            .Add(c => c.Row, row).Add(c => c.Head, true).Add(c => c.CanSeeGateway, admin));

    [Fact]
    public async Task An_offline_gateways_tier_shows_only_the_warning_and_only_an_admin_gets_the_gateway_link()
    {
        var id = await SolutionAsync();
        await LinkAsync(id);
        await using (var ctx = _db.NewContext())
        {
            await ctx.Gateways.Where(g => g.Id == _gateway)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastSeenAt, DateTime.UtcNow.AddDays(-2)));
        }
        var row = await FleetRowAsync(id);

        var admin = RenderHead(row, admin: true);
        admin.FindAll(".alert").Should().ContainSingle().Which.TextContent.Should().Contain("Gateway offline since");
        admin.FindAll(".freshness").Should().BeEmpty("a freshness line beside the warning would only argue with it");
        admin.Markup.Should().NotContain("every 15 minutes");
        admin.FindAll("a").Should().Contain(a => a.TextContent == "Open the gateway");

        var consultant = RenderHead(row, admin: false);
        consultant.FindAll(".alert").Should().ContainSingle();
        consultant.FindAll("a").Should().NotContain(a => a.TextContent == "Open the gateway");
    }

    [Fact]
    public async Task A_tier_its_gateway_is_reading_shows_how_fresh_it_is_and_no_warning()
    {
        var id = await SolutionAsync();
        await LinkAsync(id);
        await using (var ctx = _db.NewContext())
        {
            await ctx.Gateways.Where(g => g.Id == _gateway)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastSeenAt, DateTime.UtcNow));
        }
        var row = await FleetRowAsync(id);

        var cut = RenderHead(row, admin: false);

        cut.FindAll(".alert").Should().BeEmpty();
        cut.Find(".freshness").TextContent.Should().Contain("every 15 minutes");
        // The header's Server is the machine; the instance is on the Overview card.
        cut.FindAll(".meta-item").Single(m => m.TextContent.StartsWith("Server")).QuerySelector(".meta-item__value")!
            .TextContent.Should().Be("CRONUS-BC01");
        FleetRowState.StatusWord(row, DateTime.UtcNow).Should().Be("Running");
    }
}
