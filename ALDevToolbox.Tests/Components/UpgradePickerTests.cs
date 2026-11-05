using ALDevToolbox.Components.Pages.Upgrades;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using ALDevToolbox.Tests.ObjectExplorer;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Add environments picker on an upgrade's page: it opens on the environments the
/// upgrade can still do something for, and the whole fleet is one choice away.
/// </summary>
public sealed class UpgradePickerTests : IDisposable
{
    private readonly UpgradeActionTestFixture _f = new();
    private readonly BunitContext _ctx = new();

    public UpgradePickerTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("upgrade@example.com");
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _ctx.Services.AddSingleton<IOrganizationContext>(_f.Db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_f.Db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_f.Db.ConnectionString).AddInterceptors(_f.Db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddSingleton(new EnvironmentRefreshQueue());
        _ctx.Services.AddScoped<UpgradeFleetService>();
        _ctx.Services.AddScoped<EnvironmentUpgradeService>();
        _ctx.Services.AddSingleton<TimeProvider>(_f.Clock);
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));
    }

    public void Dispose()
    {
        _f.Db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _f.Dispose();
    }

    [Fact]
    public async Task It_opens_on_the_eligible_environments_and_All_environments_shows_the_rest()
    {
        // Seeded on 27.5; one customer is then moved to the target and one past it.
        await _f.SeedCustomerAsync("CRONUS Denmark");
        var (_, onTargetId) = await _f.SeedCustomerAsync("CRONUS Norway");
        var (_, pastTargetId) = await _f.SeedCustomerAsync("CRONUS Sweden");
        await using (var ctx = _f.Db.NewContext())
        {
            (await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == onTargetId)).Version = "28.5.40000.0";
            (await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == pastTargetId)).Version = "29.0.41000.0";
            await ctx.SaveChangesAsync();
        }
        int upgradeId;
        await using (var ctx = _f.Db.NewContext())
        {
            upgradeId = await _f.Upgrades(ctx).CreateAsync("28.5 in November 2026", "28.5", null, null);
        }

        var cut = _ctx.Render<UpgradePicker>();
        await cut.InvokeAsync(() => { _ = cut.Instance.OpenAsync(upgradeId, "28.5 in November 2026", "28.5", []); });

        cut.WaitForAssertion(() =>
        {
            cut.Find("select[aria-label=View]").GetAttribute("value").Should().Be("eligible");
            Solutions(cut).Should().Equal("CRONUS Denmark");
        });

        cut.Find("select[aria-label=View]").Change("active");

        cut.WaitForAssertion(() =>
            Solutions(cut).Should().BeEquivalentTo("CRONUS Denmark", "CRONUS Norway", "CRONUS Sweden"));
    }

    [Fact]
    public async Task With_every_environment_on_the_target_it_says_so_and_where_to_find_them()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        await using (var ctx = _f.Db.NewContext())
        {
            (await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == envId)).Version = "28.5.40000.0";
            await ctx.SaveChangesAsync();
        }
        int upgradeId;
        await using (var ctx = _f.Db.NewContext())
        {
            upgradeId = await _f.Upgrades(ctx).CreateAsync("28.5 in November 2026", "28.5", null, null);
        }

        var cut = _ctx.Render<UpgradePicker>();
        await cut.InvokeAsync(() => { _ = cut.Instance.OpenAsync(upgradeId, "28.5 in November 2026", "28.5", []); });

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".upk-table").Should().BeEmpty();
            cut.Find(".upk-scroll").TextContent.Should().Contain("already on 28.5 or later")
                .And.Contain("All environments");
        });
    }

    private static List<string> Solutions(IRenderedComponent<UpgradePicker> cut) =>
        cut.FindAll(".upk-sol").Select(e => e.TextContent.Trim()).ToList();
}
