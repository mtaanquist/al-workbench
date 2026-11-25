using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Components.Layout;
using ALDevToolbox.Tests.Infrastructure;
using ALDevToolbox.Endpoints;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The card a chat app shows for a pasted link (#1501). The fetch has no
/// cookie, so everything in it is public: the title may name the kind of page
/// but never anything read from the database or echoed from the URL.
/// </summary>
public sealed class LinkPreviewTests
{
    [Theory]
    [InlineData("/solutions/cronus", "Solution in AL Workbench")]
    [InlineData("/solutions/42/repositories", "Solution in AL Workbench")]
    [InlineData("/solutions", "Solutions in AL Workbench")]
    [InlineData("/pipelines/7", "Pipeline in AL Workbench")]
    [InlineData("/pipelines/deployments/3", "Deployment in AL Workbench")]
    [InlineData("/pipelines/deployments", "Deployments in AL Workbench")]
    [InlineData("/environments/cronus/production", "Environment in AL Workbench")]
    [InlineData("/SOLUTIONS/cronus", "Solution in AL Workbench")]
    [InlineData("/", "AL Workbench")]
    [InlineData("/solutionsx", "AL Workbench")]
    [InlineData("/admin/users", "AL Workbench")]
    public void A_page_is_named_by_its_kind_only(string path, string title)
    {
        LinkPreview.Describe(path, null).Title.Should().Be(title);
    }

    [Theory]
    [InlineData("/solutions/cronus?tab=people", "Solution in AL Workbench")]
    [InlineData("/s/abcdefghijklmnop", "A secure link from AL Workbench")]
    [InlineData("//evil.example/solutions/x", "AL Workbench")]
    [InlineData("/\\evil.example/solutions/x", "AL Workbench")]
    [InlineData("https://evil.example/solutions/x", "AL Workbench")]
    [InlineData("", "AL Workbench")]
    public void The_sign_in_page_describes_where_the_link_was_going(string returnUrl, string title)
    {
        LinkPreview.Describe("/login", returnUrl).Title.Should().Be(title);
    }

    [Fact]
    public void The_routers_return_value_wins_over_the_cookie_challenges()
    {
        // LinkPreview reads ?return= first; Describe gets whichever won, so
        // check the rule at the component's own seam.
        LinkPreview.PickReturnUrl("/pipelines/1", "/solutions/x").Should().Be("/pipelines/1");
        LinkPreview.PickReturnUrl(null, "/solutions/x").Should().Be("/solutions/x");
    }

    [Fact]
    public void A_return_url_only_counts_on_the_sign_in_page()
    {
        LinkPreview.Describe("/docs/mcp", "/solutions/cronus").Title.Should().Be("AL Workbench");
    }

    [Fact]
    public void A_sealed_link_gets_a_generic_card()
    {
        var preview = LinkPreview.Describe("/s/abcdefghijklmnop", null);
        preview.Title.Should().Be("A secure link from AL Workbench");
        preview.Description.Should().NotContainEquivalentOf("organisation");
    }
}

/// <summary>The same card through the real pipeline, as a chat app fetches it.</summary>
public sealed class LinkPreviewEndpointTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public LinkPreviewEndpointTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task An_anonymous_fetch_of_a_solution_link_gets_a_card_that_names_nothing()
    {
        var client = _factory.CreateClient(handleCookies: false);

        using var gated = await client.GetAsync("/solutions/cronus-secret-customer");
        gated.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var login = gated.Headers.Location!;
        login.AbsolutePath.Should().Be("/login");

        using var page = await client.GetAsync(login.PathAndQuery);
        var html = await page.Content.ReadAsStringAsync();

        Meta(html, "og:title").Should().Be("Solution in AL Workbench");
        Meta(html, "og:image").Should().MatchRegex("^https?://[^/]+/link-preview\\.png$");
        Meta(html, "twitter:card").Should().Be("summary_large_image");
        Regex.Matches(html, "<meta[^>]*cronus-secret-customer").Should().BeEmpty(
            "the slug is in the URL already; the card must not repeat it");
    }

    [Fact]
    public async Task The_preview_image_is_served_without_signing_in()
    {
        using var image = await _factory.CreateClient(handleCookies: false).GetAsync(LinkPreview.ImagePath);

        image.StatusCode.Should().Be(HttpStatusCode.OK);
        image.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
    }

    [Fact]
    public async Task The_image_url_uses_the_configured_public_address()
    {
        using var factory = new EndpointFactory(_db, services =>
            services.AddSingleton(new PublicOrigin("https://tools.cronus.example")));

        using var page = await factory.CreateClient(handleCookies: false).GetAsync("/login");

        Meta(await page.Content.ReadAsStringAsync(), "og:image")
            .Should().Be("https://tools.cronus.example/link-preview.png");
    }

    private static string Meta(string html, string name)
    {
        var match = Regex.Match(html, $"""<meta (?:property|name)="{Regex.Escape(name)}" content="([^"]*)" """.TrimEnd());
        match.Success.Should().BeTrue($"the page should carry a {name} tag");
        return match.Groups[1].Value;
    }
}
