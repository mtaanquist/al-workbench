using Microsoft.AspNetCore.Components;
using ALDevToolbox.Components.Pages.Upgrades;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
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
/// "Change the next version" run end to end from the Upgrades page (issue #960): the
/// picked version reaches Business Central (a fake) for the rows the preview said would
/// change, the rows it passed over are reported without being sent, and the environment's
/// history names the version. Kept apart from <see cref="UpgradesPageTests"/> because it
/// needs a customer with a working connection, which the upgrade-action fixture seeds
/// the way the app would.
/// </summary>
public sealed class UpgradesPageSelectVersionRunTests : IDisposable
{
    private readonly UpgradeActionTestFixture _f = new();
    private readonly BunitContext _ctx = new();

    public UpgradesPageSelectVersionRunTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("upgrade@example.com");
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _ctx.Services.AddSingleton<IOrganizationContext>(_f.Db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_f.Db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_f.Db.ConnectionString).AddInterceptors(_f.Db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<UpgradeFleetService>();
        _ctx.Services.AddScoped<UpgradeActionService>();
        _ctx.Services.AddScoped<EnvironmentUpgradeService>();
        _ctx.Services.AddScoped<ProjectConnectionService>();
        _ctx.Services.AddSingleton<IBcAdminClient>(_f.Admin);
        _ctx.Services.AddSingleton<IBcAppManagementClient>(new UnreachableAppManagementClient());
        _ctx.Services.AddSingleton(_f.TokenService());
        _ctx.Services.AddSingleton(_f.Db.DataProtectionProvider);
        _ctx.Services.AddSingleton(new BcPanelCache(TimeProvider.System));
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddSingleton(new EnvironmentRefreshQueue());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));

        // 27.6 is chosen and 29.2 on offer; after the write, 29.2 is the chosen one, as
        // Business Central would report it on the re-read that proves the change landed.
        _f.Admin.OnUpdates = () =>
        {
            var selected = _f.Admin.SelectedTargetVersion ?? "27.6";
            return new[] { Offer("27.6", selected == "27.6"), Offer("29.2", selected == "29.2") };
        };
    }

    /// <summary>
    /// The page on its Fleet view. A bare /upgrades opens on the planned upgrades now
    /// (#984), and this class is about the fleet table, so every render names the view -
    /// on top of whatever address the test has already set.
    /// </summary>
    private IRenderedComponent<UpgradesPage> RenderFleet()
    {
        var nav = _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("view", "fleet"));
        return _ctx.Render<UpgradesPage>();
    }

    /// <summary>"Change the next version..." - the first entry of the bar's More menu.</summary>
    private static AngleSharp.Dom.IElement ChangeVersionCommand(IRenderedComponent<UpgradesPage> cut) =>
        cut.FindAll(".cmdbar .ra__menu .menu__item").First(i => i.TextContent.Trim() == "Change the next version...");

    public void Dispose()
    {
        _f.Db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _f.Dispose();
    }

    private static BcEnvironmentUpdate Offer(string version, bool selected) =>
        new(version, true, selected, "scheduled", "GA", selected ? UpgradeActionTestFixture.ScheduledDate : null,
            UpgradeActionTestFixture.LatestDate, false, "Active", null, null);

    [Fact]
    public async Task A_run_changes_the_rows_the_preview_named_and_reports_the_rest()
    {
        var (_, changeId) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (_, chosenId) = await _f.SeedCustomerAsync("Fabrikam Norway");
        await MirrorAsync(changeId, next: "27.6", offered: ["29.2", "27.6"]);
        await MirrorAsync(chosenId, next: "29.2", offered: ["29.2"]);

        var cut = RenderFleet();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(2));

        cut.WaitForAssertion(() =>
        {
            cut.Find("thead .data-table__col-check input").Change(true);
            ChangeVersionCommand(cut).HasAttribute("disabled").Should().BeFalse();
        });
        cut.WaitForAssertion(() =>
        {
            ChangeVersionCommand(cut).Click();
            cut.FindAll(".upg-version__opts input[type=radio]").Select(r => r.GetAttribute("value"))
                .Should().Equal("29.2", "27.6");
        });
        cut.WaitForAssertion(() =>
        {
            Pick(cut, "29.2");
            cut.FindAll(".upg-preview__head").Select(h => h.TextContent.Trim())
                .Should().Equal("Will change (1)", "Already chosen (1)");
        });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__gate input").Input("update");
            cut.FindAll(".confirm-dialog__actions .btn").Last().Click();
        });

        cut.WaitForAssertion(() =>
        {
            cut.Find(".upg-notice").TextContent.Should().Contain("Set the next version to 29.2 on 1 environment, 1 skipped.");
            cut.FindAll(".confirm-dialog").Should().BeEmpty();
        });

        // Business Central was asked once, for the row that needed it, and nothing about a date.
        _f.Admin.Writes.Should().Be(1);
        _f.Admin.SelectedTargetVersion.Should().Be("29.2");
        _f.Admin.SelectedDateTime.Should().BeNull();

        await using var ctx = _f.Db.NewContext();
        var actions = await ctx.OeEnvironmentUpgradeActions.AsNoTracking().ToListAsync();
        actions.Should().ContainSingle();
        actions[0].EnvironmentId.Should().Be(changeId);
        actions[0].Kind.Should().Be(UpgradeActionKind.SelectVersion);
        actions[0].TargetVersion.Should().Be("29.2");
        actions[0].Status.Should().Be(UpgradeActionStatus.Sent);
    }

    [Fact]
    public async Task The_history_names_the_version_that_was_set()
    {
        var (_, envId) = await _f.SeedCustomerAsync("CRONUS Denmark");
        await MirrorAsync(envId, next: "27.6", offered: ["29.2", "27.6"]);

        var cut = RenderFleet();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(1));

        // From the row's own menu this time: the one environment, same dialog.
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody .ra__menu .menu__item").First(i => i.TextContent.Trim() == "Change the next version...").Click();
            cut.Find(".confirm-dialog__title").TextContent.Should().Be("Change this environment's next version?");
        });
        cut.WaitForAssertion(() =>
        {
            Pick(cut, "29.2");
            cut.Find(".upg-preview__head").TextContent.Trim().Should().Be("Will change (1)");
        });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__gate input").Input("update");
            cut.FindAll(".confirm-dialog__actions .btn").Last().Click();
        });
        cut.WaitForAssertion(() => cut.Find(".upg-notice").TextContent.Should().Contain("Set the next version to 29.2"));

        // The history opens once: a second click on the same entry closes it again.
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".ra__menu .menu__item").First(i => i.TextContent.Trim() == "Update history").Click();
            cut.FindAll("tr.is-subrow .upg-feed").Should().NotBeEmpty();
        });
        cut.WaitForAssertion(() =>
            cut.Find(".eaf__what b").TextContent.Should().Be("Set the next version to 29.2"));
    }

    private static void Pick(IRenderedComponent<UpgradesPage> cut, string version) =>
        cut.FindAll(".upg-version__opts input[type=radio]")
            .First(r => r.GetAttribute("value") == version)
            .Change(version);

    /// <summary>Writes the next-update mirror the nightly sweep would have written.</summary>
    private async Task MirrorAsync(int environmentId, string next, List<string> offered)
    {
        await using var ctx = _f.Db.NewContext();
        var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == environmentId);
        env.BcNextUpdateVersion = next;
        env.BcNextUpdateStatus = "scheduled";
        env.BcOfferedVersions = offered;
        env.BcNextUpdateFetchedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
    }
}
