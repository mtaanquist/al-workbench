using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using ALDevToolbox.Components.Pages.Upgrades;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Upgrades fleet table. The named user is a member of the BC upgrade team who
/// schedules platform updates for around a hundred solutions and reads this table
/// across, row by row.
///
/// <para>The page follows the design's actionable-list sheet (PageUpgrades.dc.html):
/// one command bar whose selection commands are disabled until a row is ticked, a
/// glyph for state, and the row's commands - history first - in one trailing menu
/// rather than as buttons inside data cells, which is what #805 got wrong.</para>
/// </summary>
public sealed class UpgradesPageTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private const int AdminUserId = 9850;
    private static readonly Guid TenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public UpgradesPageTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("upgrades@example.com");
        // The search filters straight away here; the one test about the wait sets its own.
        UpgradesPage.SearchDebounce = TimeSpan.Zero;

        // The page asks the browser for the last view picked; by default it has none.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<UpgradeFleetService>();
        _ctx.Services.AddScoped<UpgradeActionService>();
        _ctx.Services.AddScoped<EnvironmentUpgradeService>();
        _ctx.Services.AddScoped<ProjectConnectionService>();
        _ctx.Services.AddSingleton<IBcAdminClient>(new UnreachableAdminClient());
        _ctx.Services.AddSingleton<IBcAppManagementClient>(new UnreachableAppManagementClient());
        _ctx.Services.AddSingleton(new BcTokenService(
            new UnreachableHttpClientFactory(), NullLogger<BcTokenService>.Instance));
        _ctx.Services.AddSingleton(_db.DataProtectionProvider);
        _ctx.Services.AddSingleton(new BcPanelCache(TimeProvider.System));
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddSingleton(new EnvironmentRefreshQueue());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));

        using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            Id = AdminUserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "upgrades@example.com",
            PasswordHash = "x",
            DisplayName = "Anna Jensen",
            // An org admin holds the environment-ops grant outright, which is the
            // shortest way to a page that renders its table at all.
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = AdminUserId;
    }

    public void Dispose()
    {
        // A test that set a real wait must not hand it to a page in another test class.
        UpgradesPage.SearchDebounce = TimeSpan.Zero;
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private async Task SeedOneEnvironmentAsync()
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS Denmark",
            BcTenantId = TenantId,
            CreatedByUserId = AdminUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Name = "Production",
            Type = "Production",
            ApplicationFamily = "BusinessCentral",
            Status = "Active",
            Version = "27.5.12345.0",
            FetchedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
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

    private IRenderedComponent<UpgradesPage> RenderWithOneRow()
    {
        var cut = RenderFleet();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(1));
        return cut;
    }

    /// <summary>
    /// The page exists to move update dates, and a deleted environment's date cannot be
    /// moved: Business Central offers it no update at all. It is dropped in the fleet
    /// query rather than in the page, so the counts, the checkbox selection and the two
    /// bulk actions all agree without each having to remember.
    /// </summary>
    [Fact]
    public async Task A_deleted_environment_is_not_on_the_upgrades_table_at_all()
    {
        await SeedOneEnvironmentAsync();
        await using (var ctx = _db.NewContext())
        {
            var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Name == "Production");
            env.Status = "SoftDeleted";
            env.SoftDeletedOn = DateTime.UtcNow.AddDays(-2);
            env.HardDeletePendingOn = DateTime.UtcNow.AddDays(12);
            await ctx.SaveChangesAsync();
        }

        var cut = RenderFleet();

        cut.WaitForAssertion(() => cut.FindAll(".empty-state").Should().NotBeEmpty());
        cut.FindAll(".data-table tbody tr").Should().BeEmpty();
        cut.Markup.Should().NotContain("Production");
    }

    [Fact]
    public async Task The_first_column_names_the_record_the_way_the_rest_of_the_app_does()
    {
        await SeedOneEnvironmentAsync();

        var cut = RenderWithOneRow();

        var headers = cut.FindAll(".data-table thead th").Select(h => h.TextContent.Trim()).ToList();
        // "Customer" here and "Solution" one click away on Environments named the same
        // record twice (#807).
        headers.Should().Contain("Solution");
        headers.Should().NotContain("Customer");
    }

    [Fact]
    public async Task The_environment_leads_the_row_and_the_first_link_opens_it()
    {
        await SeedOneEnvironmentAsync();

        var cut = RenderWithOneRow();

        var headers = cut.FindAll(".data-table thead th").Select(h => h.TextContent.Trim()).Where(h => h.Length > 0).ToList();
        headers.IndexOf("Environment").Should().BeLessThan(headers.IndexOf("Solution"));
        cut.Find(".data-table tbody tr a").GetAttribute("href").Should().StartWith("/environments/",
            "a row in a list of environments opens the environment first");
    }

    [Fact]
    public async Task A_bare_visit_opens_on_the_view_this_browser_picked_last()
    {
        await SeedOneEnvironmentAsync();
        _ctx.JSInterop.Setup<string?>("sessionStorage.getItem", "aldt-upgrades-view").SetResult("0Production");
        var nav = _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        var cut = RenderFleet();

        cut.WaitForAssertion(() => nav.Uri.Should().EndWith("/upgrades?view=fleet&type=Production"));
    }

    [Fact]
    public async Task An_address_that_names_a_filter_outranks_the_remembered_view()
    {
        await SeedOneEnvironmentAsync();
        _ctx.JSInterop.Setup<string?>("sessionStorage.getItem", "aldt-upgrades-view").SetResult("0Production");
        var nav = _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("view", "fleet"));
        nav.NavigateTo(nav.GetUriWithQueryParameter("waiting", "1"));
        var before = nav.Uri;

        var cut = RenderFleet();
        cut.WaitForAssertion(() => cut.FindAll(".data-table").Should().NotBeEmpty());

        nav.Uri.Should().Be(before, "a link somebody sent shows what they meant it to show");
    }

    [Fact]
    public async Task History_opens_from_the_row_menu_and_no_data_cell_holds_a_button()
    {
        await SeedOneEnvironmentAsync();

        var cut = RenderWithOneRow();

        var headers = cut.FindAll(".data-table thead th").Select(h => h.TextContent.Trim()).ToList();
        headers.Last().Should().Be("Actions");

        // An action in a data cell made every row three lines tall (#805). The sheet
        // puts the row's commands in one kebab in the trailing cell, history first
        // because it is the entry every row has and the only one that changes nothing.
        var cells = cut.FindAll(".data-table tbody tr td");
        var items = cells.Last().QuerySelectorAll(".ra__menu .menu__item")
            .Select(i => i.TextContent.Trim()).ToList();
        items.Should().Equal(
            "Update history", "Move this date to the latest", "Start this update...",
            "Change the next version...", "Open environment", "Open in Business Central");
        // The tenant comes from the solution's own connection; the name is a path segment.
        cells.Last().QuerySelector("a.menu__item[target=_blank]")!.GetAttribute("href").Should().Be(
            "https://businesscentral.dynamics.com/11111111-2222-3333-4444-555555555555/Production");
        cells.Take(cells.Count - 1).SelectMany(c => c.QuerySelectorAll("button")).Should().BeEmpty();

        cells.Last().QuerySelector(".menu__item")!.Click();
        cut.WaitForAssertion(() => cut.Find("tr.is-subrow .upg-feed__title").TextContent
            .Should().Be("Update history - CRONUS Denmark, Production"));
    }

    [Fact]
    public async Task Commands_that_need_a_selection_are_disabled_until_a_row_is_ticked()
    {
        await SeedOneEnvironmentAsync();

        var cut = RenderWithOneRow();

        // One bar, not a filter row over a selection row: search, view, commands - the
        // order Solutions and Environments use (#966).
        cut.Find(".cmdbar .cmdbar__row > .search.cmdbar__search:first-child input[type=search]").Should().NotBeNull();
        cut.Find(".cmdbar .cmdbar__row > .search + .cmdbar__group select").Should().NotBeNull();
        cut.FindAll(".filter-bar").Should().BeEmpty();

        // The embedded bar (PageUpgrades.dc.html, embedded): the version change is the
        // first entry of More, so the row stays one line; Refresh is an icon.
        var commands = cut.FindAll(".cmdbar .cmdbar__group:last-child > button");
        commands.Select(c => c.TextContent.Trim()).Should().Equal(
            "Move dates...", "Start update...", "Add to upgrade...", "Refresh");
        commands[0].HasAttribute("disabled").Should().BeTrue();
        commands[1].HasAttribute("disabled").Should().BeTrue();
        commands[2].HasAttribute("disabled").Should().BeTrue();
        commands[3].HasAttribute("disabled").Should().BeFalse();
        var more = cut.FindAll(".cmdbar .ra__menu .menu__item");
        more.Select(i => i.TextContent.Trim()).First().Should().Be("Change the next version...");
        more[0].HasAttribute("disabled").Should().BeTrue();
        // The page's one primary is New upgrade, in the head, on every view.
        cut.FindAll(".btn--primary").Should().ContainSingle().Which.TextContent.Trim().Should().Be("New upgrade");

        cut.Find("tbody .data-table__col-check input").Change(true);

        cut.WaitForAssertion(() =>
        {
            cut.Find("tbody tr").ClassList.Should().Contain("is-selected");
            cut.FindAll(".cmdbar .cmdbar__group:last-child button")[0].HasAttribute("disabled").Should().BeFalse();
        });
    }

    [Fact]
    public async Task A_rows_result_gets_a_line_of_its_own_under_the_row()
    {
        await SeedOneEnvironmentAsync();
        var cut = RenderWithOneRow();

        cut.Find("tbody .data-table__col-check input").Change(true);
        cut.WaitForAssertion(() =>
            cut.FindAll(".cmdbar .cmdbar__group:last-child button")[0].HasAttribute("disabled").Should().BeFalse());
        cut.FindAll(".cmdbar .cmdbar__group:last-child button")[0].Click();
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions .btn")
            .Should().Contain(b => b.TextContent.Trim() == "Move the dates"));
        cut.FindAll(".confirm-dialog__actions .btn").First(b => b.TextContent.Trim() == "Move the dates").Click();

        // Inside the Next update cell a long refusal from Business Central widened that
        // column and squeezed the others; on its own row it cannot size a column.
        cut.WaitForAssertion(() =>
        {
            cut.Find("tr.upg-result-row .upg-result .upg-note").TextContent.Should().Contain("Skipped");
            cut.FindAll("tbody tr:not(.is-subrow) .upg-note--muted").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task State_is_a_named_glyph_and_the_type_sits_under_the_environment()
    {
        await SeedOneEnvironmentAsync();

        var cut = RenderWithOneRow();

        cut.Find("tbody tr").ClassList.Should().Contain("is-published");
        cut.Find("tbody .data-table__col-state [role=img]").GetAttribute("aria-label").Should().Be("Running");
        cut.FindAll("tbody .status-pill").Should().BeEmpty();
        cut.Find("tbody .cell-stack__main").TextContent.Should().Be("Production");
        cut.Find("tbody .cell-stack__sub").TextContent.Should().Be("Production");
        cut.Find(".pager__count").TextContent.Should().Be("Showing 1 of 1 environment");
    }

    [Fact]
    public async Task Typing_in_the_search_box_filters_the_rows_already_loaded()
    {
        await SeedOneEnvironmentAsync();

        var cut = RenderWithOneRow();

        cut.Find(".cmdbar__search input").Input("nothing like it");

        // Waited for, not read straight off: the table is drawn by the list frame, and
        // under a loaded test run the redraw has been seen to land a beat later.
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".data-table tbody tr").Should().BeEmpty();
            cut.Find(".empty-state__title").TextContent.Should().Be("No environments match these filters");
        });
    }

    // ── Typing fast (#981) ───────────────────────────────────────────────

    /// <summary>
    /// The box used to be trimmed as it was typed and the trimmed value written back,
    /// so the space between two words vanished and "CRONUS Denmark" could not be typed.
    /// </summary>
    [Fact]
    public async Task A_trailing_space_stays_in_the_box_and_the_search_still_finds_the_row()
    {
        await SeedFleetEnvironmentAsync("CRONUS Denmark", "Production");
        await SeedFleetEnvironmentAsync("Fabrikam Norway", "Production");

        var cut = RenderFleet();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(2));

        cut.Find(".cmdbar__search input").Input("CRONUS ");

        cut.WaitForAssertion(() =>
        {
            cut.Find(".cmdbar__search input").GetAttribute("value").Should().Be("CRONUS ");
            var rows = cut.FindAll(".data-table tbody tr");
            rows.Should().ContainSingle();
            rows[0].QuerySelector("td.upg-customer a")!.TextContent.Should().Be("CRONUS Denmark");
        });
    }

    /// <summary>
    /// The table waits for typing to pause instead of redrawing per keystroke, and the
    /// box keeps the latest text throughout.
    /// </summary>
    [Fact]
    public async Task The_table_filters_once_typing_pauses_and_the_box_keeps_every_keystroke()
    {
        await SeedFleetEnvironmentAsync("CRONUS Denmark", "Production");
        await SeedFleetEnvironmentAsync("Fabrikam Norway", "Production");
        UpgradesPage.SearchDebounce = TimeSpan.FromMilliseconds(300);

        var cut = RenderFleet();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(2));

        cut.Find(".cmdbar__search input").Input("f");
        cut.Find(".cmdbar__search input").Input("fa");
        cut.Find(".cmdbar__search input").Input("fab");

        cut.WaitForAssertion(() =>
            cut.Find(".cmdbar__search input").GetAttribute("value").Should().Be("fab"));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".data-table tbody tr");
            rows.Should().ContainSingle();
            rows[0].QuerySelector("td.upg-customer a")!.TextContent.Should().Be("Fabrikam Norway");
        }, TimeSpan.FromSeconds(5));
        cut.Find(".cmdbar__search input").GetAttribute("value").Should().Be("fab");
    }

    // ── The solution's short name (#966) ───────────────────────────────

    [Fact]
    public async Task The_short_name_shows_after_the_solution_and_the_search_finds_it()
    {
        await SeedFleetEnvironmentAsync("CRONUS Denmark", "Production", shortName: "CRD");
        await SeedFleetEnvironmentAsync("Fabrikam Norway", "Production");

        var cut = RenderFleet();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(2));

        var shortName = cut.Find("td.upg-customer .sol-list__short");
        shortName.TextContent.Should().Be("CRD");
        shortName.GetAttribute("title").Should().Be("Short name");

        // Colleagues say "CRD" aloud; a search for it in any case finds the customer.
        cut.WaitForAssertion(() =>
        {
            cut.Find(".cmdbar__search input").Input("crd");
            var rows = cut.FindAll(".data-table tbody tr");
            rows.Should().ContainSingle();
            rows[0].QuerySelector("td.upg-customer a")!.TextContent.Should().Be("CRONUS Denmark");
        });
    }

    // ── The selection survives the search (#985) ───────────────────────

    private static string PickedCount(IRenderedComponent<UpgradesPage> cut) =>
        cut.Find(".cmdbar .upg-picked__count").TextContent.Trim();

    private static AngleSharp.Dom.IElement BarButton(IRenderedComponent<UpgradesPage> cut, string label) =>
        cut.FindAll(".cmdbar button").Single(b => b.TextContent.Trim() == label);

    /// <summary>
    /// The named user builds an evening's batch by finding each customer by short name
    /// in turn. A search must not untick the ones already found; the bar says how many
    /// are off screen; the header box only reaches the rows under it; and the command
    /// acts on - and its preview names - every tick, hidden or not.
    /// </summary>
    [Fact]
    public async Task A_search_keeps_the_ticks_and_the_preview_names_the_hidden_ones()
    {
        await SeedFleetEnvironmentAsync("CRONUS Denmark", "Production", shortName: "CRD");
        await SeedFleetEnvironmentAsync("Fabrikam Norway", "Production", shortName: "FAB");

        var cut = RenderFleet();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(2));
        cut.FindAll(".upg-picked__count").Should().BeEmpty("nothing is ticked yet");

        cut.Find(".cmdbar__search input").Input("crd");
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().ContainSingle());
        cut.Find("tbody .data-table__col-check input").Change(true);

        cut.Find(".cmdbar__search input").Input("fab");
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".data-table tbody tr").Should().ContainSingle()
                .Which.TextContent.Should().Contain("Fabrikam Norway");
            PickedCount(cut).Should().Be("1 selected, 0 shown");
            BarButton(cut, "Move dates...").HasAttribute("disabled").Should().BeFalse(
                "the tick the search hid is still a selection to act on");
        });

        // The header box ticks the row shown, then unticks only that one.
        cut.Find("thead .data-table__col-check input").Change(true);
        cut.WaitForAssertion(() => PickedCount(cut).Should().Be("2 selected, 1 shown"));
        cut.Find("thead .data-table__col-check input").Change(false);
        cut.WaitForAssertion(() => PickedCount(cut).Should().Be("1 selected, 0 shown"));

        // The command names the hidden customer before it sends anything.
        BarButton(cut, "Move dates...").Click();
        cut.WaitForAssertion(() =>
            cut.FindAll(".confirm-dialog .upg-preview__who").Select(w => w.TextContent.Trim())
                .Should().ContainSingle().Which.Should().StartWith("CRONUS Denmark - Production"));
        cut.FindAll(".confirm-dialog__actions .btn").First(b => b.TextContent.Trim() == "Cancel").Click();

        // Show selected brings it back into view, whatever the search says.
        cut.WaitForAssertion(() => BarButton(cut, "Show selected").Click());
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".data-table tbody tr").Should().ContainSingle()
                .Which.TextContent.Should().Contain("CRONUS Denmark");
            BarButton(cut, "Show selected").GetAttribute("aria-pressed").Should().Be("true");
            PickedCount(cut).Should().Be("1 selected");
        });

        cut.WaitForAssertion(() => BarButton(cut, "Clear selection").Click());
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".upg-picked__count").Should().BeEmpty();
            cut.FindAll(".data-table tbody tr").Should().ContainSingle()
                .Which.TextContent.Should().Contain("Fabrikam Norway", "the search is still in the box");
            BarButton(cut, "Move dates...").HasAttribute("disabled").Should().BeTrue();
        });
    }

    /// <summary>
    /// Only a re-read takes a tick away, and only for a row that is no longer there.
    /// </summary>
    [Fact]
    public async Task A_reload_drops_the_tick_on_an_environment_that_has_gone()
    {
        await SeedFleetEnvironmentAsync("CRONUS Denmark", "Production");
        await SeedFleetEnvironmentAsync("Fabrikam Norway", "Production");

        var cut = RenderFleet();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(2));
        cut.Find("thead .data-table__col-check input").Change(true);
        cut.WaitForAssertion(() => PickedCount(cut).Should().Be("2 selected"));

        await using (var ctx = _db.NewContext())
        {
            var gone = await ctx.OeProjectEnvironments
                .SingleAsync(e => e.Project!.Name == "Fabrikam Norway");
            ctx.OeProjectEnvironments.Remove(gone);
            await ctx.SaveChangesAsync();
        }

        cut.WaitForAssertion(() => BarButton(cut, "Refresh").Click());
        // Reload now sits on the notice under the bar, not in it.
        cut.WaitForAssertion(() =>
            cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reload now").Click());
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".data-table tbody tr").Should().ContainSingle();
            PickedCount(cut).Should().Be("1 selected");
        });
    }

    // ── Change the next version (#960) ──────────────────────────────────

    /// <summary>
    /// One environment per preview group the page can reach from a selection of rows it
    /// may act on: 27.5 moving forward, 30.0 moving back, one already on a later version,
    /// one already set to 29.2, one not offered it, one with an update running.
    /// </summary>
    private async Task SeedVersionFleetAsync()
    {
        await SeedFleetEnvironmentAsync("CRONUS Denmark", "Production",
            nextVersion: "27.5", offered: ["29.2", "27.5"]);
        await SeedFleetEnvironmentAsync("CRONUS Denmark", "Sandbox", type: "Sandbox",
            nextVersion: "30.0", offered: ["30.0", "29.2"]);
        await SeedFleetEnvironmentAsync("Fabrikam Norway", "Production",
            version: "29.3.1.0", nextVersion: "30.0", offered: ["30.0"]);
        await SeedFleetEnvironmentAsync("Litware Sweden", "Production",
            nextVersion: "29.2", offered: ["29.2"]);
        await SeedFleetEnvironmentAsync("Northwind Finland", "Production",
            nextVersion: "28.1", offered: ["28.1"]);
        await SeedFleetEnvironmentAsync("Tailspin Iceland", "Production",
            nextVersion: "29.2", offered: ["29.2"], nextStatus: "Running");
    }

    /// <summary>"Change the next version..." - the first entry of the bar's More menu.</summary>
    private static AngleSharp.Dom.IElement ChangeVersionCommand(IRenderedComponent<UpgradesPage> cut) =>
        cut.FindAll(".cmdbar .ra__menu .menu__item").First(i => i.TextContent.Trim() == "Change the next version...");

    private IRenderedComponent<UpgradesPage> OpenVersionDialog(int rows)
    {
        var cut = RenderFleet();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(rows));

        cut.WaitForAssertion(() =>
        {
            cut.Find("thead .data-table__col-check input").Change(true);
            ChangeVersionCommand(cut).HasAttribute("disabled").Should().BeFalse();
        });
        cut.WaitForAssertion(() =>
        {
            ChangeVersionCommand(cut).Click();
            cut.Find(".confirm-dialog__title").TextContent.Should().Be("Change the next version?");
        });
        return cut;
    }

    private static void Pick(IRenderedComponent<UpgradesPage> cut, string version) =>
        cut.FindAll(".upg-version__opts input[type=radio]")
            .First(r => r.GetAttribute("value") == version)
            .Change(version);

    [Fact]
    public async Task The_picker_lists_every_version_on_offer_newest_first_with_how_many_it_is_for()
    {
        await SeedVersionFleetAsync();

        var cut = OpenVersionDialog(rows: 6);

        cut.WaitForAssertion(() =>
        {
            var options = cut.FindAll(".upg-version__opts .upg-when__opt")
                .Select(o => o.QuerySelector(".upg-version__name")!.TextContent + " "
                             + o.QuerySelector(".upg-version__count")!.TextContent)
                .ToList();
            options.Should().Equal(
                "30.0 for 2 of the selected",
                "29.2 for 4 of the selected",
                "28.1 for 1 of the selected",
                "27.5 for 1 of the selected");
            // Nothing is picked for the person, so there is nothing to preview or confirm yet.
            cut.FindAll(".upg-preview").Should().BeEmpty();
            cut.FindAll(".confirm-dialog__actions .btn").Last().HasAttribute("disabled").Should().BeTrue();
        });
    }

    [Fact]
    public async Task Picking_a_version_sorts_every_selected_environment_into_its_group()
    {
        await SeedVersionFleetAsync();
        var cut = OpenVersionDialog(rows: 6);

        cut.WaitForAssertion(() =>
        {
            Pick(cut, "29.2");
            var heads = cut.FindAll(".upg-preview__head").Select(h => h.TextContent.Trim()).ToList();
            heads.Should().Equal(
                "Will change (2)", "Already on it (1)", "Already chosen (1)", "Not offered (1)", "Update under way (1)");
        });

        // Every ticked row lands in exactly one group, and every group heading has its
        // rows under it in the same group - none dropped, none counted twice.
        cut.WaitForAssertion(() =>
        {
            var groups = cut.FindAll(".upg-version__group");
            groups.Should().HaveCount(5);
            foreach (var group in groups)
            {
                var count = int.Parse(System.Text.RegularExpressions.Regex.Match(
                    group.QuerySelector(".upg-preview__head")!.TextContent, @"\((\d+)\)").Groups[1].Value);
                group.QuerySelectorAll(".upg-preview li").Length.Should().Be(count);
            }
            var who = cut.FindAll(".upg-version__group .upg-preview__who")
                .Select(w => System.Text.RegularExpressions.Regex.Replace(w.TextContent, @"\s+", " ").Trim())
                .ToList();
            who.Should().HaveCount(6).And.OnlyHaveUniqueItems();
        });

        cut.WaitForAssertion(() =>
        {
            var lines = cut.FindAll(".upg-preview li")
                .Select(li => li.QuerySelector(".upg-preview__what")!.TextContent.Trim())
                .ToList();
            lines.Should().Contain("27.5 to 29.2");
            lines.Should().Contain("30.0 back to 29.2");
            lines.Should().Contain("Already on 29.3, past 29.2");
            lines.Should().Contain("Already set to 29.2");
            lines.Should().Contain("Business Central does not offer 29.2 to this environment yet");
            lines.Should().Contain("An update is already running, and Microsoft finishes it");

            cut.Find(".upg-version__date").TextContent.Trim()
                .Should().Be("Business Central sets the date; use Move dates afterwards to push it out.");
            cut.Find(".confirm-dialog .upg-preview__count").TextContent
                .Should().Be("2 environments, including 1 production.");
        });
    }

    [Fact]
    public async Task The_change_waits_for_the_typed_word_like_the_other_production_writes()
    {
        await SeedVersionFleetAsync();
        var cut = OpenVersionDialog(rows: 6);

        cut.WaitForAssertion(() =>
        {
            Pick(cut, "29.2");
            cut.Find(".confirm-dialog__gate-label").TextContent.Should().Contain("update");
            var confirm = cut.FindAll(".confirm-dialog__actions .btn").Last();
            confirm.TextContent.Trim().Should().Be("Change to 29.2");
            confirm.HasAttribute("disabled").Should().BeTrue();
        });

        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__gate input").Input("update");
            cut.FindAll(".confirm-dialog__actions .btn").Last().HasAttribute("disabled").Should().BeFalse();
        });
    }

    [Fact]
    public async Task A_version_nobody_in_the_selection_can_move_to_holds_the_confirm()
    {
        await SeedVersionFleetAsync();
        var cut = OpenVersionDialog(rows: 6);

        // 28.1 is offered only to the environment already set to it.
        cut.WaitForAssertion(() =>
        {
            Pick(cut, "28.1");
            cut.FindAll(".upg-preview__head").Select(h => h.TextContent.Trim())
                .Should().NotContain(h => h.StartsWith("Will change"));
            cut.Find(".confirm-dialog .upg-preview__count").TextContent.Should().Be("Nothing to change for this version.");
        });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__gate input").Input("update");
            cut.FindAll(".confirm-dialog__actions .btn").Last().HasAttribute("disabled").Should().BeTrue();
        });
    }

    private async Task SeedFleetEnvironmentAsync(
        string projectName, string environmentName, string type = "Production", string? shortName = null,
        string version = "27.4.12345.0", string? nextVersion = null, List<string>? offered = null,
        string? nextStatus = "Scheduled")
    {
        await using var ctx = _db.NewContext();
        var project = await ctx.OeProjects.FirstOrDefaultAsync(p => p.Name == projectName);
        if (project is null)
        {
            project = new OeProject
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = projectName,
                ShortName = shortName,
                BcTenantId = TenantId,
                CreatedByUserId = AdminUserId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.OeProjects.Add(project);
            await ctx.SaveChangesAsync();
        }

        ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Name = environmentName,
            Type = type,
            ApplicationFamily = "BusinessCentral",
            Status = "Active",
            Version = version,
            FetchedAt = DateTime.UtcNow,
            BcNextUpdateVersion = nextVersion,
            BcNextUpdateStatus = nextVersion is null ? null : nextStatus,
            BcOfferedVersions = offered,
            BcNextUpdateFetchedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    // ── Watching an update through to its end (#982) ───────────────────

    /// <summary>
    /// An Admin Center that answers the three calls a started-and-watched update makes:
    /// the updates list, the date write, and the environment by name. Everything else
    /// still refuses, so a watch that reached for more would fail the test.
    /// </summary>
    private sealed class WatchAdminClient : UnreachableAdminClient
    {
        public List<string> Reads { get; } = new();
        public string Status = "Active";
        public string Version = "27.5.12345.0";
        public List<BcEnvironmentUpdate> Updates = [NextUpdate("Scheduled")];

        public override Task<BcEnvironment?> GetEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        {
            Reads.Add("GetEnvironment:" + environmentName);
            return Task.FromResult<BcEnvironment?>(new BcEnvironment(environmentName, "Production")
            {
                ApplicationFamily = "BusinessCentral",
                Status = Status,
                Version = Version,
            });
        }

        public override Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        {
            Reads.Add("ListEnvironmentUpdates:" + environmentName);
            return Task.FromResult<IReadOnlyList<BcEnvironmentUpdate>>(Updates.ToList());
        }

        public override Task SelectTargetVersionAsync(string accessToken, string? applicationFamily, string environmentName, string targetVersion, string? targetVersionType, DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static BcEnvironmentUpdate NextUpdate(string status) =>
        new("27.6", true, true, status, "GA", DateTimeOffset.UtcNow.AddMinutes(-6),
            DateTimeOffset.UtcNow.AddDays(20), true, "Active", null, null);

    /// <summary>Points the page's connection service at <paramref name="admin"/> with a token that is always granted.</summary>
    private void UseBusinessCentral(WatchAdminClient admin)
    {
        _ctx.Services.AddSingleton<IBcAdminClient>(admin);
        _ctx.Services.AddSingleton(new BcTokenService(
            new ALDevToolbox.Tests.ObjectExplorer.StubHttpClientFactory(new Dictionary<string, string>
            {
                ["oauth2"] = "{\"access_token\":\"tok\",\"expires_in\":3600}",
            }),
            NullLogger<BcTokenService>.Instance));
    }

    /// <summary>A connected solution with <paramref name="count"/> environments in the given state.</summary>
    private async Task<List<int>> SeedConnectedAsync(
        string status = "Active", string? nextStatus = "Scheduled", int count = 1)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS Denmark",
            BcTenantId = TenantId,
            BcClientId = "client-abc",
            BcClientSecretEncrypted = _db.DataProtectionProvider
                .CreateProtector(ProjectConnectionService.SecretProtectionPurpose).Protect("s3cr3t"),
            BcClientSecretExpiresAt = DateTime.UtcNow.AddYears(1),
            CreatedByUserId = AdminUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        var rows = Enumerable.Range(0, count).Select(i => new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Name = count == 1 ? "Production" : $"Sandbox {i + 1:00}",
            Type = count == 1 ? "Production" : "Sandbox",
            ApplicationFamily = "BusinessCentral",
            Status = status,
            Version = "27.5.12345.0",
            FetchedAt = DateTime.UtcNow,
            BcNextUpdateVersion = "27.6",
            BcNextUpdateStatus = nextStatus,
            BcNextUpdateDate = DateTime.UtcNow.AddMinutes(-6),
            BcNextUpdateFetchedAt = DateTime.UtcNow,
        }).ToList();
        ctx.OeProjectEnvironments.AddRange(rows);
        await ctx.SaveChangesAsync();
        return rows.Select(r => r.Id).ToList();
    }

    /// <summary>
    /// The named user has just started a customer's update and is staying on the page to
    /// see it finish. The row must say it is being watched without them pressing anything.
    /// </summary>
    [Fact]
    public async Task Starting_an_update_now_puts_its_row_on_the_watch()
    {
        var envId = (await SeedConnectedAsync()).Single();
        var admin = new WatchAdminClient();
        UseBusinessCentral(admin);
        var cut = RenderWithOneRow();
        cut.Instance.WatchedEnvironmentIds.Should().BeEmpty("nothing is updating yet");

        cut.Find("tbody .data-table__col-check input").Change(true);
        cut.WaitForAssertion(() =>
            cut.FindAll(".cmdbar .cmdbar__group:last-child button")[1].HasAttribute("disabled").Should().BeFalse());
        cut.FindAll(".cmdbar .cmdbar__group:last-child button")[1].Click();
        cut.WaitForAssertion(() => cut.Find(".confirm-dialog__gate input").Input("update"));
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions .btn")
            .First(b => b.TextContent.Trim() == "Start the updates").Click());
        // The grace period's own way past the wait.
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions .btn")
            .First(b => b.TextContent.Trim() == "Start now").Click());

        cut.WaitForAssertion(() =>
        {
            cut.Instance.WatchedEnvironmentIds.Should().Equal(envId);
            cut.Find("td.upg-next .upg-note--busy").TextContent.Trim().Should().StartWith("Updating... started");
        });
    }

    /// <summary>
    /// A slot the worker fired at 20:00 is under way when somebody opens the page, and it
    /// deserves the same watch as one started from here. A row that is simply waiting for
    /// its date is not watched: nothing is happening to it.
    /// </summary>
    [Theory]
    [InlineData("Upgrading", "Scheduled", true)]
    [InlineData("Active", "Running", true)]
    [InlineData("Active", "Scheduled", false)]
    public async Task A_row_already_updating_is_watched_from_the_moment_the_page_opens(
        string status, string nextStatus, bool watched)
    {
        var envId = (await SeedConnectedAsync(status, nextStatus)).Single();
        UseBusinessCentral(new WatchAdminClient());

        var cut = RenderWithOneRow();

        if (watched)
        {
            cut.Instance.WatchedEnvironmentIds.Should().Equal(envId);
            cut.Find("td.upg-next .upg-note--busy").TextContent.Should().Contain("started");
        }
        else
        {
            cut.Instance.WatchedEnvironmentIds.Should().BeEmpty();
            cut.FindAll("td.upg-next .upg-note--busy").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task A_re_read_that_is_still_updating_keeps_the_watch()
    {
        var envId = (await SeedConnectedAsync("Upgrading")).Single();
        var admin = new WatchAdminClient { Status = "Upgrading", Updates = [NextUpdate("Running")] };
        UseBusinessCentral(admin);
        var cut = RenderWithOneRow();

        await cut.InvokeAsync(() => cut.Instance.WatchTickAsync());

        admin.Reads.Should().Equal("GetEnvironment:Production", "ListEnvironmentUpdates:Production");
        cut.Instance.WatchedEnvironmentIds.Should().Equal(envId);
    }

    /// <summary>
    /// The update is over when the environment is running again and nothing is under way.
    /// On the new version it went through, and the row says so in words, not API ones.
    /// </summary>
    [Fact]
    public async Task A_running_re_read_on_the_new_version_ends_the_watch_as_updated()
    {
        await SeedConnectedAsync("Upgrading");
        var admin = new WatchAdminClient { Status = "Active", Version = "27.6.20001.0", Updates = [] };
        UseBusinessCentral(admin);
        var cut = RenderWithOneRow();

        await cut.InvokeAsync(() => cut.Instance.WatchTickAsync());

        cut.Instance.WatchedEnvironmentIds.Should().BeEmpty();
        cut.WaitForAssertion(() =>
        {
            cut.Find("td.upg-next .upg-note--ok").TextContent.Trim().Should().EndWith("Updated to 27.6");
            cut.Find("td.u-num").TextContent.Should().Be("27.6", "the row shows what Business Central now says");
        });
    }

    [Fact]
    public async Task A_running_re_read_still_on_the_old_version_ends_the_watch_as_failed()
    {
        var envId = (await SeedConnectedAsync("Upgrading")).Single();
        var admin = new WatchAdminClient { Status = "Active", Updates = [NextUpdate("Failed")] };
        UseBusinessCentral(admin);
        var cut = RenderWithOneRow();

        await cut.InvokeAsync(() => cut.Instance.WatchTickAsync());

        cut.Instance.WatchedEnvironmentIds.Should().BeEmpty();
        cut.WaitForAssertion(() =>
        {
            var note = cut.Find("td.upg-next .upg-note--bad");
            note.TextContent.Should().Contain("Update failed");
            note.QuerySelector("a.upg-note__link")!.GetAttribute("href")
                .Should().Be($"/environments/{envId}/operations");
        });
    }

    /// <summary>
    /// Fifty updates started at once must not become six hundred requests a minute
    /// against Microsoft: one tick reads ten, two requests each.
    /// </summary>
    [Fact]
    public async Task One_tick_reads_at_most_ten_environments()
    {
        await SeedConnectedAsync("Upgrading", count: 12);
        var admin = new WatchAdminClient { Status = "Upgrading", Updates = [NextUpdate("Running")] };
        UseBusinessCentral(admin);
        var cut = RenderFleet();
        cut.WaitForAssertion(() => cut.Instance.WatchedEnvironmentIds.Should().HaveCount(12));

        await cut.InvokeAsync(() => cut.Instance.WatchTickAsync());

        admin.Reads.Should().HaveCount(2 * UpdateWatch.PerTick);
        admin.Reads.Where(r => r.StartsWith("GetEnvironment:")).Should().OnlyHaveUniqueItems();
        cut.Instance.WatchedEnvironmentIds.Should().HaveCount(12);
    }

    [Fact]
    public void A_row_without_a_tenant_has_no_Business_Central_address_and_a_name_is_escaped()
    {
        var row = new UpgradeFleetRow(1, "CRONUS Denmark", null, 2, "UAT 2", "Sandbox", "Active", null,
            null, null, null, null, null, null, null, CanAct: true);

        row.BusinessCentralUrl.Should().BeNull();
        (row with { TenantId = TenantId }).BusinessCentralUrl.Should().EndWith("/UAT%202");
    }
}

/// <summary>
/// When a watched update counts as over, and how it went - the rule UpdateWatch, the
/// Upgrades page's and an upgrade's own page's watch, applies to each re-read (#982). Pure.
/// </summary>
public sealed class UpgradeWatchVerdictTests
{
    private static UpgradeFleetRow Row(string? status, string? version, string? nextStatus = null) =>
        new(1, "CRONUS Denmark", null, 2, "Production", "Production", status, version,
            "27.6", "GA", nextStatus, null, null, null, null, CanAct: true);

    [Theory]
    [InlineData("Upgrading", null, true)]
    [InlineData("Preparing", null, true)]
    [InlineData("NotReady", null, true)]
    [InlineData("Recovering", null, true)]
    [InlineData("Active", "Running", true)]
    [InlineData("active", "running", true)]
    [InlineData("Active", "Scheduled", false)]
    [InlineData("Active", null, false)]
    [InlineData(null, null, false)]
    public void An_update_is_under_way_when_the_state_is_busy_or_the_update_is_running(
        string? status, string? nextStatus, bool expected)
    {
        UpdateWatch.IsUpdating(Row(status, "27.5.1.0", nextStatus)).Should().Be(expected);
    }

    [Fact]
    public void Busy_is_still_updating()
    {
        UpdateWatch.Judge(Row("Upgrading", "27.5.1.0"), "27.6", seenBusy: true)
            .Should().Be(UpdateWatch.Verdict.StillUpdating);
    }

    [Fact]
    public void Running_with_the_update_still_running_is_still_updating()
    {
        UpdateWatch.Judge(Row("Active", "27.5.1.0", "Running"), "27.6", seenBusy: true)
            .Should().Be(UpdateWatch.Verdict.StillUpdating);
    }

    [Theory]
    [InlineData("27.6.20001.0")]
    [InlineData("28.0.1.0")]
    public void Running_on_the_target_version_or_later_is_updated(string version)
    {
        UpdateWatch.Judge(Row("Active", version), "27.6", seenBusy: false)
            .Should().Be(UpdateWatch.Verdict.Updated);
    }

    [Fact]
    public void Running_on_the_old_version_after_being_busy_is_failed()
    {
        UpdateWatch.Judge(Row("Active", "27.5.1.0", "Failed"), "27.6", seenBusy: true)
            .Should().Be(UpdateWatch.Verdict.Failed);
    }

    /// <summary>
    /// Right after Start update, Business Central has not picked the update up yet: the
    /// environment is running, on the old version. That is not a failure.
    /// </summary>
    [Fact]
    public void Running_on_the_old_version_before_ever_being_busy_is_not_picked_up_yet()
    {
        UpdateWatch.Judge(Row("Active", "27.5.1.0", "Scheduled"), "27.6", seenBusy: false)
            .Should().Be(UpdateWatch.Verdict.StillUpdating);
    }

    [Fact]
    public void A_failed_state_is_failed_whatever_else_the_row_says()
    {
        UpdateWatch.Judge(Row("UpgradingFailed", "27.5.1.0", "Running"), "27.6", seenBusy: false)
            .Should().Be(UpdateWatch.Verdict.Failed);
    }

    [Fact]
    public void A_reading_replaces_what_business_central_said_and_keeps_the_rest()
    {
        var row = Row("Upgrading", "27.5.1.0", "Running") with { TenantId = Guid.NewGuid(), ProjectShortName = "CRD" };
        var at = new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);

        var after = new BcEnvironmentReading("Active", "27.6.1.0", at, null, null, null, null, null, null, [], at)
            .ApplyTo(row);

        after.Status.Should().Be("Active");
        after.Version.Should().Be("27.6.1.0");
        after.EnvironmentFetchedAt.Should().Be(at);
        after.FetchedAt.Should().Be(at);
        after.HasUpdate.Should().BeFalse();
        after.OfferedVersions.Should().BeEmpty();
        after.CanAct.Should().BeTrue();
        after.TenantId.Should().Be(row.TenantId);
        after.ProjectShortName.Should().Be("CRD");
    }
}
