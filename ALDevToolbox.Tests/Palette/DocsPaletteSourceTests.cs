using System.Reflection;
using System.Security.Claims;
using ALDevToolbox.Components.Pages.Docs;
using ALDevToolbox.Data;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Services.Palette.Sources;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// The shared fence harness over <see cref="DocsPaletteSource"/>. Docs belong to
/// no solution and to no organisation - there is no row to leak - so what
/// applies is that a signed-out caller gets nothing and a signed-in one finds a
/// page.
/// </summary>
public sealed class DocsPaletteSourceHarnessTests : PaletteSourceVisibilityTestBase
{
    protected override bool RowsBelongToSolutions => false;

    protected override string VisibleQuery => "assistant";

    protected override IPaletteSource CreateSource(PaletteSourceUnderTest context) => new DocsPaletteSource();

    protected override Task SeedRowAsync(AppDbContext ctx, PaletteSourceSeed seed) => Task.CompletedTask;
}

/// <summary>
/// What the Docs source finds, where its rows land, and - the reason this file
/// exists - that its hand-kept catalogue matches the pages it describes.
/// </summary>
public sealed class DocsPaletteSourceTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public DocsPaletteSourceTests()
    {
        _ctx.Services.AddSingleton<IHttpContextAccessor>(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        // The gateway setup guide shows the gateway download; a local build has none.
        _ctx.Services.AddSingleton(new GatewayBuild(null, null, false));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task Mcp_finds_the_page_about_connecting_an_assistant()
    {
        // The issue's own example: the page's title never says MCP, which is
        // why each page carries a topic line.
        var results = await SearchAsync("mcp");

        var row = results.Should().ContainSingle().Subject;
        row.Kind.Should().Be("doc");
        row.Title.Should().Be("Connect an AI assistant");
        row.Href.Should().Be("/docs/mcp");
    }

    [Fact]
    public async Task A_section_heading_lands_on_its_anchor()
    {
        var results = await SearchAsync("token");

        var row = results.Should().ContainSingle().Subject;
        row.Title.Should().Be("A personal access token");
        row.Subtitle.Should().Be("Help: Connecting an AI assistant",
            "a section says it is help, and which page it is on - the page title alone reads like a command");
        row.Href.Should().Be("/docs/mcp#mcp-token");
    }

    [Fact]
    public async Task A_section_is_found_by_its_heading_and_its_page_together()
    {
        var results = await SearchAsync("assistant set up");

        results.Select(r => r.Href).Should().Contain("/docs/mcp#mcp-setup");
    }

    [Fact]
    public async Task A_page_is_found_by_its_title()
    {
        var results = await SearchAsync("search or jump");

        results.First().Href.Should().Be("/docs/search");
    }

    [Fact]
    public async Task Nothing_comes_back_for_a_word_no_page_uses()
    {
        var results = await SearchAsync("fabrikam");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task A_signed_in_member_passes_the_gate_and_a_signed_out_caller_does_not()
    {
        var source = new DocsPaletteSource();

        (await source.IsAvailableAsync(RecipePaletteSourceTests.MemberPrincipal(), CancellationToken.None))
            .Should().BeTrue();
        (await source.IsAvailableAsync(new ClaimsPrincipal(new ClaimsIdentity()), CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public void Every_docs_page_is_in_the_catalogue_and_every_catalogued_page_exists()
    {
        var docsRoutes = RouteTemplates().Where(r => r.StartsWith("/docs/", StringComparison.Ordinal)).ToList();

        DocsPaletteSource.Pages.Select(p => p.Href).Should().BeEquivalentTo(docsRoutes,
            "a docs page the catalogue leaves out cannot be found, and one it invents is a dead end");
    }

    /// <summary>
    /// The catalogue against the rendered page, both ways: every catalogued
    /// heading is on the page at its anchor, and every section heading the page
    /// renders is catalogued. The second half is what keeps a section added to
    /// a docs page from going unfindable.
    /// </summary>
    [Theory]
    [InlineData("/docs/mcp", typeof(McpDocs))]
    [InlineData("/docs/search", typeof(SearchDocs))]
    [InlineData("/docs/extensions-whats-next", typeof(WhatsNextDocs))]
    [InlineData("/docs/gateway-setup", typeof(GatewaySetupDocs))]
    public void The_catalogue_matches_the_rendered_page(string href, Type component)
    {
        var entry = DocsPaletteSource.Pages.Should().ContainSingle(p => p.Href == href).Subject;
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(href);

        var rendered = _ctx.Render(builder =>
        {
            builder.OpenComponent(0, component);
            builder.CloseComponent();
        });

        rendered.Find("h1").TextContent.Trim().Should().Be(entry.Title,
            "the row's title is the page's own heading");

        foreach (var heading in entry.Headings)
        {
            var element = rendered.FindAll($"#{heading.Anchor}").Should().ContainSingle(
                "the catalogue sends {0} to #{1}", heading.Text, heading.Anchor).Subject;
            element.TagName.Should().BeOneOf("H2", "H3");
            element.TextContent.Trim().Should().EndWith(heading.Text);
        }

        var catalogued = entry.Headings.Select(h => h.Anchor).ToHashSet();
        foreach (var h2 in rendered.FindAll(".docs__main h2"))
        {
            catalogued.Should().Contain(h2.Id,
                "the section '{0}' on {1} has to be in the catalogue to be found", h2.TextContent.Trim(), href);
        }
    }

    private static async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(string rawQuery)
    {
        var source = new DocsPaletteSource();
        var query = PaletteQuery.Parse(rawQuery);
        var candidates = await source.SearchAsync(query, 25, CancellationToken.None);
        return PaletteRanking.Rank(query, candidates).Select(m => m.Candidate).ToList();
    }

    private static IReadOnlyList<string> RouteTemplates() =>
        typeof(HttpOrganizationContext).Assembly
            .GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>(inherit: true))
            .Select(r => r.Template)
            .ToList();
}
