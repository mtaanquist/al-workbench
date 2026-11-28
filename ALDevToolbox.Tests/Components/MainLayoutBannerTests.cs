using ALDevToolbox.Components.Layout;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The site banner a SiteAdmin types at /site-admin/settings/general has to
/// actually reach the page (issue #698 — it was written, audited and never
/// read). Rendered signed-out so the layout takes its NotAuthorized branch:
/// that is also the state a visitor on the sign-in page is in, which is
/// precisely who a "maintenance at 21:00" notice needs to reach.
/// </summary>
public sealed class MainLayoutBannerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly ALDevToolbox.Services.SingleTenant.SingleTenantModeState _singleTenant = new(false);

    public MainLayoutBannerTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.AddAuthorization();

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton<ALDevToolbox.Services.SingleTenant.ISingleTenantMode>(_singleTenant);
        _ctx.Services.AddSingleton<IToolAvailability>(new AllToolsOn());
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(
            typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    private sealed class AllToolsOn : IToolAvailability
    {
        public bool IsSiteEnabled(ALDevToolbox.Domain.Tools.ToolKey key) => true;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private async Task SetBannerAsync(string? text)
    {
        await using var ctx = _db.NewContext();
        var row = await ctx.SystemSettings.FirstOrDefaultAsync(r => r.Id == 1);
        if (row is null)
        {
            row = new SystemSettings { Id = 1 };
            ctx.SystemSettings.Add(row);
        }
        row.BannerText = text;
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Banner_text_is_rendered_at_the_top_of_the_layout()
    {
        await SetBannerAsync("Maintenance tonight 21:00-23:00 UTC.");

        var cut = _ctx.Render<MainLayout>();

        cut.WaitForAssertion(() =>
            cut.Find(".site-banner").TextContent.Trim()
                .Should().Be("Maintenance tonight 21:00-23:00 UTC."));
    }

    [Fact]
    public void No_banner_element_when_none_is_set()
    {
        var cut = _ctx.Render<MainLayout>();

        cut.WaitForAssertion(() => cut.Find(".app__content").Should().NotBeNull());
        cut.FindAll(".site-banner").Should().BeEmpty();
    }
}
