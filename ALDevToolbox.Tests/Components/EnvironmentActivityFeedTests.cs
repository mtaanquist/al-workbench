using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The environment's update history says which wave an entry belonged to (#984, sub-issue
/// E): an action run from a planned upgrade names it and links to it, so a person reading
/// one customer's history can get to the other seven customers of that evening.
/// </summary>
public sealed class EnvironmentActivityFeedTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public EnvironmentActivityFeedTests()
    {
        _ctx.Services.AddUtcDisplayTimeZone();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    private static UpgradeActionRow Entry(int id, int? upgradeId = null, string? upgradeName = null) => new(
        id, 1, 2, UpgradeActionKind.RunNow, UpgradeActionStatus.Sent, "Anna Jensen <anna@cronus.example>",
        DateTime.UtcNow.AddHours(-12), DateTime.UtcNow.AddHours(-12), DateTime.UtcNow.AddHours(-12), null,
        null, null, UpgradeId: upgradeId, UpgradeName: upgradeName);

    [Fact]
    public void An_entry_run_from_an_upgrade_names_it_and_links_to_it()
    {
        var cut = _ctx.Render<EnvironmentActivityFeed>(p => p
            .Add(f => f.Entries, [Entry(1, 42, "28.5 in November 2026"), Entry(2)]));

        var items = cut.FindAll(".eaf__item");
        var from = items[0].QuerySelector(".eaf__from")!;
        from.TextContent.Should().Be("from 28.5 in November 2026");
        from.QuerySelector("a")!.GetAttribute("href").Should().Be("/upgrades/42");
        items[1].QuerySelector(".eaf__from").Should().BeNull("an ad hoc action belongs to no upgrade");
    }
}
