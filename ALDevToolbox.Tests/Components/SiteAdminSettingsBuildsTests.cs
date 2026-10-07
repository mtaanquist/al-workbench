using ALDevToolbox.Components.Pages.SiteAdmin;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Builds tab (#1164), rendered: the number box mirrors the server's 1 to 16
/// rule, empty says what the default is, and the recommendation names what this
/// server has.
/// </summary>
public sealed class SiteAdminSettingsBuildsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly ProjectBuildQueue _queue = new(defaultLimit: 2);

    public SiteAdminSettingsBuildsTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("siteadmin@cronus.example");
        auth.SetRoles("SiteAdmin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddDataProtection();
        _ctx.Services.AddSingleton(_queue);
        _ctx.Services.AddScoped<SystemSettingsService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void An_empty_box_says_what_the_default_is_and_the_input_mirrors_the_range()
    {
        var cut = _ctx.Render<SiteAdminSettingsBuilds>();

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input[name=BuildConcurrency]");
            input.GetAttribute("type").Should().Be("number");
            input.GetAttribute("min").Should().Be("1");
            input.GetAttribute("max").Should().Be("16");
            input.GetAttribute("value").Should().BeNullOrEmpty();
            cut.Markup.Should().Contain("Builds that run at once");
            cut.Markup.Should().Contain("Leave empty to use the default (currently 2).");
            cut.Markup.Should().Contain("up to <strong>2 builds</strong> run at once");
        });
    }

    [Fact]
    public void The_recommendation_names_what_this_server_has()
    {
        var capacity = BuildConcurrencyAdvice.ForThisServer();

        var cut = _ctx.Render<SiteAdminSettingsBuilds>();

        cut.WaitForAssertion(() =>
        {
            var text = System.Text.RegularExpressions.Regex.Replace(cut.Find(".setting__hint").TextContent, @"\s+", " ");
            text.Should().Contain($"{capacity.MemoryGb} GB of memory.");
            text.Should().Contain($"Recommended: {capacity.Recommended}.");
            text.Should().Contain("raising this past the recommendation makes builds slower, not faster.");
        });
    }

    [Fact]
    public async Task A_saved_value_shows_in_the_box()
    {
        await using (var ctx = _db.NewContext())
        {
            var row = await ctx.SystemSettings.SingleAsync();
            row.BuildConcurrency = 5;
            await ctx.SaveChangesAsync();
        }

        var cut = _ctx.Render<SiteAdminSettingsBuilds>();

        cut.WaitForAssertion(() =>
            cut.Find("input[name=BuildConcurrency]").GetAttribute("value").Should().Be("5"));
    }
}
