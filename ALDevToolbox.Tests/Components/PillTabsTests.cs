using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The pill tabs' count (<c>.pill-tab__count</c>), which a view switch uses to say how much
/// each view holds. A count not known yet is left out rather than drawn as a zero.
/// </summary>
public sealed class PillTabsTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public PillTabsTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void A_tab_with_a_count_shows_it_after_the_label_and_one_without_shows_none()
    {
        var cut = _ctx.Render<PillTabs>(p => p.Add(c => c.Items, new PillTabs.PillTabItem[]
        {
            new("Open", "/upgrades", null, true, 5),
            new("Archive", "/upgrades?view=archive", null, false, 0),
            new("Fleet", "/upgrades?view=fleet", null, false),
        }));

        var tabs = cut.FindAll(".pill-tab");
        tabs[0].QuerySelector(".pill-tab__count")!.TextContent.Should().Be("5");
        tabs[1].QuerySelector(".pill-tab__count")!.TextContent.Should().Be("0", "a real zero is still a count");
        tabs[2].QuerySelector(".pill-tab__count").Should().BeNull();
        tabs[0].GetAttribute("aria-selected").Should().Be("true");
    }
}
