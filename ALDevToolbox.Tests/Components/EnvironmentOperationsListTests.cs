using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using AwesomeAssertions;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Operations tab's list: Business Central's own record of what happened on an
/// environment. The named user is an ops engineer checking whether last night's update
/// went through, who should never have to read a wire token to find out.
/// </summary>
public sealed class EnvironmentOperationsListTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private static readonly Guid CoreId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    public EnvironmentOperationsListTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddUtcDisplayTimeZone();
    }

    public void Dispose() => _ctx.Dispose();

    private static BcEnvironmentOperation Op(
        string type, string status, string? error = null, int minutes = 5, string by = "",
        params (string Key, string Value)[] parameters)
    {
        var created = new DateTimeOffset(2026, 9, 19, 22, 0, 0, TimeSpan.Zero);
        return new BcEnvironmentOperation(
            Guid.NewGuid().ToString(), type, status, created, created,
            status is "running" or "scheduled" or "queued" ? null : created.AddMinutes(minutes),
            by, error ?? string.Empty,
            parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
    }

    private IRenderedComponent<EnvironmentOperationsList> Render(params BcEnvironmentOperation[] operations) =>
        _ctx.Render<EnvironmentOperationsList>(p => p
            .Add(c => c.Operations, operations)
            .Add(c => c.EnvironmentName, "Production")
            .Add(c => c.AppName, id => id == CoreId ? "Continia Core" : null));

    [Fact]
    public void Each_operation_reads_as_a_sentence_with_a_status_and_how_long_it_took()
    {
        var cut = Render(
            Op("environmentAppUpdate", "succeeded", by: "ops@cronus.example",
                parameters: [("appId", CoreId.ToString()), ("targetAppVersion", "28.5.0.1")]),
            Op("update", "running", parameters: [("targetVersion", "28.3")]),
            Op("modify", "failed", error: "The update window is too short.", minutes: 0));

        var rows = cut.FindAll("tbody tr");
        rows[0].Children[1].TextContent.Trim().Should().Be("Updated Continia Core to 28.5.0.1");
        rows[0].QuerySelector(".status-pill")!.TextContent.Should().Be("Succeeded");
        rows[0].Children[2].TextContent.Should().Be("ops@cronus.example");
        rows[0].Children[3].TextContent.Should().Be("19 Sep 2026, 22:00", "times are in the organisation's zone, UTC when it has not picked one");
        rows[0].Children[3].QuerySelector("time")!.GetAttribute("title").Should().Be("2026-09-19 22:00:00 UTC");
        rows[0].Children[4].TextContent.Should().Be("5 min");

        rows[1].Children[1].TextContent.Trim().Should().Be("Business Central update to 28.3");
        rows[1].ClassList.Should().Contain("is-running");
        rows[1].Children[4].TextContent.Should().Be("Still running");

        rows[2].ClassList.Should().Contain("is-failed");
        rows[2].QuerySelector(".env-ops__error")!.TextContent.Should().Be("The update window is too short.");
        rows[2].Children[4].TextContent.Should().Be("Under a minute");
    }

    [Fact]
    public void An_app_nobody_can_name_and_a_type_nobody_has_seen_still_read_as_words()
    {
        var cut = Render(
            Op("environmentAppInstall", "succeeded", parameters: [("appId", Guid.NewGuid().ToString())]),
            Op("someNewThing", "skipped"));

        var rows = cut.FindAll("tbody tr");
        rows[0].Children[1].TextContent.Trim().Should().Be("Installed an app");
        rows[1].Children[1].TextContent.Trim().Should().Be("Some new thing");
        rows[1].QuerySelector(".status-pill")!.TextContent.Should().Be("Skipped");
    }

    [Fact]
    public void Nothing_recorded_says_what_will_show_up_here()
    {
        var cut = Render();

        cut.FindAll("table").Should().BeEmpty();
        cut.Markup.Should().Contain("Business Central has nothing recorded for this environment");
    }

    [Fact]
    public void A_long_list_shows_the_latest_and_offers_the_rest()
    {
        var cut = Render(Enumerable.Range(0, 30).Select(_ => Op("restart", "succeeded")).ToArray());

        cut.FindAll("tbody tr").Should().HaveCount(25);
        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Contains("Show all 30")).Click());
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(30));
    }

    [Fact]
    public void An_operation_the_page_has_named_shows_that_name_instead_of_the_client_id()
    {
        var ours = Op("restart", "succeeded", by: "103da8d4-b842-46aa-9b1a-e8317721130e");
        var theirs = Op("modify", "succeeded", by: "ops@cronus.example");

        var cut = _ctx.Render<EnvironmentOperationsList>(p => p
            .Add(c => c.Operations, new[] { ours, theirs })
            .Add(c => c.EnvironmentName, "Production")
            .Add(c => c.StartedBy, new Dictionary<string, string> { [ours.Id] = "AL Workbench (Ada Lovelace)" }));

        var rows = cut.FindAll("tbody tr");
        rows[0].Children[2].TextContent.Should().Be("AL Workbench (Ada Lovelace)");
        rows[1].Children[2].TextContent.Should().Be("ops@cronus.example");
    }
}
