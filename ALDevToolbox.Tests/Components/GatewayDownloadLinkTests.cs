using ALDevToolbox.Components.Pages.Admin.Administration;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Gateways;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The gateway download on the Gateways tab (#1363): a release links the gateway
/// published with it and shows its SHA-256; an unsigned build says it is a preview;
/// a local or staging AL Workbench says plainly that there is no download instead
/// of linking to a release that does not exist.
/// </summary>
public sealed class GatewayDownloadLinkTests : IDisposable
{
    private static readonly string Hash = "0123456789abcdef".PadRight(64, 'e');

    private readonly BunitContext _ctx = new();

    public GatewayDownloadLinkTests() =>
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<GatewayDownloadLink> Render(GatewayBuild build)
    {
        _ctx.Services.AddSingleton(build);
        return _ctx.Render<GatewayDownloadLink>(p => p.Add(c => c.IdPrefix, "gw-test"));
    }

    [Fact]
    public void A_signed_release_links_its_own_gateway_with_the_hash_to_check()
    {
        var cut = Render(new GatewayBuild("12.1.0", Hash, signed: true));

        var link = cut.Find("a.btn");
        link.GetAttribute("href").Should().Be("https://github.com/mtaanquist/al-workbench/releases/download/v12.1.0/ALWorkbench.Gateway.exe");
        link.ClassList.Should().NotContain("btn--primary", "Add gateway stays the page's one primary action");
        link.TextContent.Should().Contain("Download ALWorkbench.Gateway.exe");
        cut.Markup.Should().Contain("Version 12.1.0");
        cut.Find("#gw-test-sha256").TextContent.Should().Be(Hash.ToUpperInvariant());
        cut.Find("[data-copy-target]").GetAttribute("data-copy-target").Should().Be("#gw-test-sha256");
        cut.Markup.Should().Contain("Get-FileHash").And.NotContain("not code-signed");
    }

    [Fact]
    public void An_unsigned_release_says_it_is_not_code_signed_without_calling_it_a_preview()
    {
        var cut = Render(new GatewayBuild("12.1.0", Hash, signed: false));

        cut.Find(".gw-download__note").TextContent.Should().Contain("not code-signed yet").And.NotContain("preview");
        cut.Find("#gw-test-sha256").TextContent.Should().Be(Hash.ToUpperInvariant());
    }

    [Fact]
    public void A_release_without_a_stamped_hash_points_at_the_published_checksum()
    {
        var cut = Render(new GatewayBuild("12.1.0", null, signed: true));

        cut.FindAll("#gw-test-sha256").Should().BeEmpty();
        cut.FindAll("a").Select(a => a.GetAttribute("href")).Should()
            .Contain("https://github.com/mtaanquist/al-workbench/releases/download/v12.1.0/ALWorkbench.Gateway.exe.sha256");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0.0.0-staging.ga1b2c3d")]
    public void A_local_or_staging_workbench_says_there_is_no_download_and_links_nothing(string? version)
    {
        var cut = Render(new GatewayBuild(version, Hash, signed: true));

        cut.FindAll("a").Should().BeEmpty();
        cut.Markup.Should().Contain("There is no gateway download for this AL Workbench").And.Contain("not a released");
    }
}
