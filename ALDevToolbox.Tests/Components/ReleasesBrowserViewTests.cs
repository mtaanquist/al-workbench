using ALDevToolbox.Components.Pages.ObjectExplorer;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// A symbols package a pipeline build pulled from the feeds with no source in it is
/// not a card on the Object Explorer releases page; it stays in the database for
/// reference resolution (#1092). A release an admin imported without source is still
/// listed, and so is a feed package that did carry source.
///
/// <para>Named user: a BC consultant opening the Third-party tab to look through a
/// vendor's code.</para>
/// </summary>
public sealed class ReleasesBrowserViewTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public ReleasesBrowserViewTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("tester@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ReferenceQueryService>();
        _ctx.Services.AddScoped<ObjectExplorerService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task An_empty_symbol_package_from_the_feeds_is_not_listed_or_counted()
    {
        await SeedAsync("Continia Banking - Import 28.6.0.395730 (symbols)", files: 0, dedupKey: "symbols:aaaa:28.6.0.395730");
        await SeedAsync("Continia Banking - PSP 28.6.0.395730 (symbols)", files: 4, dedupKey: "symbols:bbbb:28.6.0.395730");
        await SeedAsync("CRONUS Extension 1.0.0.0", files: 0, dedupKey: null);

        var cut = _ctx.Render<ReleasesBrowserView>();
        cut.WaitForAssertion(() => cut.FindAll(".pill-tab").Should().NotBeEmpty());
        cut.FindAll(".pill-tab").First(t => t.TextContent.Contains("Third-party")).Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("CRONUS Extension 1.0.0.0", "an admin imported it, source or not");
            cut.Markup.Should().Contain("Continia Banking - PSP", "this package carried source");
            cut.Markup.Should().NotContain("Continia Banking - Import");
            cut.FindAll(".pill-tab").First(t => t.TextContent.Contains("Third-party"))
                .QuerySelector(".pill-tab__count")!.TextContent.Trim().Should().Be("2");
        });
    }

    private async Task SeedAsync(string label, int files, string? dedupKey)
    {
        await using var db = _db.NewContext();
        db.OeReleases.Add(new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = label,
            Kind = "third_party",
            Status = "ready",
            Publisher = "CRONUS",
            SourceFileCount = files,
            DedupKey = dedupKey,
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
