using ALDevToolbox.Components.Pages.Environments;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The cross-solution Environments list (design archetype 2a). The named user is a
/// BC consultant checking every customer environment they can reach in one table.
///
/// <para>The rule most worth pinning is which timestamp "Last checked" reports.
/// A row carries two, stamped by different reads: the environment itself, and the
/// next-update mirror. A tenant can answer one and refuse the other, so reading the
/// wrong one reports an environment as never checked when only its updates were
/// unreadable.</para>
/// </summary>
public sealed class EnvironmentsListTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private const int OwnerUserId = 9800;

    public EnvironmentsListTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("owner@example.com");
        // The search filters straight away here; the one test about the wait sets its own.
        EnvironmentsList.SearchDebounce = TimeSpan.Zero;

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<UpgradeFleetService>();
        _ctx.Services.AddSingleton(new EnvironmentRefreshQueue());
        // The row menu's upload goes through the connection service.
        _ctx.Services.AddScoped<ProjectConnectionService>();
        _ctx.Services.AddSingleton<IBcAdminClient>(new UnreachableAdminClient());
        _ctx.Services.AddSingleton<IBcAppManagementClient>(new UnreachableAppManagementClient());
        _ctx.Services.AddSingleton(new BcTokenService(
            new UnreachableHttpClientFactory(), NullLogger<BcTokenService>.Instance));
        _ctx.Services.AddSingleton(_db.DataProtectionProvider);
        _ctx.Services.AddSingleton(new BcPanelCache(TimeProvider.System));
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));

        using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            Id = OwnerUserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "owner@example.com",
            PasswordHash = "x",
            DisplayName = "Owner",
            Role = UserRole.Editor,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose()
    {
        // A test that set a real wait must not hand it to a page in another test class.
        EnvironmentsList.SearchDebounce = TimeSpan.Zero;
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private async Task<int> SeedSolutionAsync(string name, ProjectVisibility visibility = ProjectVisibility.Public)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            Visibility = visibility,
            CreatedByUserId = OwnerUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private async Task SeedEnvironmentAsync(
        int projectId, string name, string type, string? status,
        DateTime environmentFetchedAt, DateTime? updatesFetchedAt)
    {
        await using var ctx = _db.NewContext();
        ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name,
            Type = type,
            Status = status,
            Version = "28.2",
            FetchedAt = environmentFetchedAt,
            BcNextUpdateFetchedAt = updatesFetchedAt,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task An_org_with_no_connected_solution_gets_the_first_run_empty_state()
    {
        await SeedSolutionAsync("CRONUS Denmark");

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() =>
            cut.Find(".empty-state__title").TextContent.Trim()
                .Should().Be("No environments to show yet"));
        // The empty state has to say how to get out of it, not just that it is empty.
        cut.Find(".empty-state__text").TextContent
            .Should().Contain("Business Central connection");
        cut.Find(".empty-state__action a").TextContent.Trim().Should().Be("Go to solutions");
    }

    [Fact]
    public async Task Last_checked_reports_the_environment_read_not_the_update_mirror()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        // Read half an hour ago; its updates have never been readable at all.
        await SeedEnvironmentAsync(id, "Production", "Production", "Active",
            environmentFetchedAt: DateTime.UtcNow.AddMinutes(-30),
            updatesFetchedAt: null);

        var cut = _ctx.Render<EnvironmentsList>();

        // Inside the wait: the time is a Timestamp, which renders once the organisation's
        // zone has been read, a render after the row itself.
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".data-table tbody tr").Should().HaveCount(1);
            var lastChecked = cut.FindAll(".data-table tbody tr td")
                .Last(c => !c.ClassList.Contains("data-table__actions")).TextContent.Trim();
            lastChecked.Should().NotBe("never",
                "the environment was read half an hour ago - only its updates were unreadable");
            lastChecked.Should().Contain("minutes ago");
        });
        cut.Find(".freshness [data-page-refresh]").TextContent.Should().Contain("Refresh",
            "the command palette's Refresh presses the button carrying this mark");
    }

    [Fact]
    public async Task A_row_offers_to_upload_an_app_and_the_dialog_names_that_environment()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);

        var cut = _ctx.Render<EnvironmentsList>();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(1));

        cut.WaitForAssertion(() =>
            cut.FindAll("button.menu__item").Single(b => b.TextContent.Trim() == "Upload an app...").Click());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Upload apps to Production, a production environment?"));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Upload and install")
            .HasAttribute("disabled").Should().BeTrue("nothing has been chosen yet");
    }

    // ── Copying an environment from the row menu ──────────────────────────

    /// <summary>
    /// Copy asks for a name and two decisions before it does anything, so unlike the
    /// one-click entries beside it, it is offered only to somebody who may go through
    /// with it - taking all that back with "you may not" is a worse answer than never
    /// asking.
    /// </summary>
    [Fact]
    public async Task A_row_offers_a_copy_and_the_dialog_names_that_environment_and_its_customer()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);

        var cut = _ctx.Render<EnvironmentsList>();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(1));

        cut.WaitForAssertion(() =>
            cut.FindAll("button.menu__item").Single(b => b.TextContent.Trim() == "Copy this environment...").Click());

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Copy Production?");
            cut.Markup.Should().Contain("CRONUS Denmark");
            cut.Find("#copy-env-name").GetAttribute("value").Should().Be("Production-Copy");
        });
    }

    [Fact]
    public async Task Someone_who_can_see_a_solution_but_not_manage_it_is_not_offered_a_copy()
    {
        // Read-only, because that is the level where seeing and managing part company:
        // a Public solution is managed by everyone in the organisation.
        var id = await SeedSolutionAsync("CRONUS Denmark", ProjectVisibility.ReadOnly);
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);

        // A colleague who can see this solution but neither owns it nor is on a
        // team assigned to it.
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = 9801, OrganizationId = TestDb.DefaultOrgId, Email = "colleague@example.com",
                PasswordHash = "x", DisplayName = "Colleague", Role = UserRole.Editor,
                Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        _db.OrgContext.CurrentUserId = 9801;

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(1));
        cut.FindAll("button.menu__item").Select(b => b.TextContent.Trim())
            .Should().NotContain("Copy this environment...");
    }

    [Fact]
    public async Task An_environment_part_way_through_an_update_does_not_need_attention()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Updating", now, now);
        await SeedEnvironmentAsync(id, "UAT", "Sandbox", "Suspended", now, now);
        await SeedEnvironmentAsync(id, "Test", "Sandbox", "Active", now, now);

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(3));
        var tabs = cut.FindAll(".pill-tab").Select(t => t.TextContent.Trim()).ToList();
        tabs.Should().Contain(t => t.StartsWith("Needs attention"));
        // Suspended counts; mid-update is the system working, so it must not.
        tabs.First(t => t.StartsWith("Needs attention")).Should().EndWith("1");
    }

    /// <summary>
    /// The design gives a table row no status column: the state is the edge bar plus a
    /// glyph. Four states share two glyphs and a title does not exist on touch, so the
    /// word must still be on screen for anything that is not plainly running - under
    /// Next update, where the designed sheet puts it.
    /// </summary>
    [Fact]
    public async Task State_is_a_named_glyph_and_the_word_stays_on_screen_unless_running()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);
        await SeedEnvironmentAsync(id, "UAT", "Sandbox", "Suspended", now, now);

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(2));
        var rows = cut.FindAll(".data-table tbody tr");
        var running = rows.Single(r => r.TextContent.Contains("Production"));
        var suspended = rows.Single(r => r.TextContent.Contains("UAT"));

        foreach (var row in rows)
        {
            var glyph = row.QuerySelector("td.data-table__col-state > .data-table__state--icon")!;
            glyph.GetAttribute("role").Should().Be("img");
            glyph.GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace().And.Be(glyph.GetAttribute("title"));
            glyph.TextContent.Trim().Should().BeEmpty("the state cell is a glyph, not a label column");
        }

        suspended.QuerySelectorAll(".cell-stack__sub").Last().TextContent.Should().Be("Suspended by Microsoft");
        running.QuerySelectorAll(".cell-stack__sub").Last().TextContent.Should().Be("Nothing scheduled");
        running.TextContent.Should().NotContain("Running", "a healthy row spends no words on its state");
    }

    // ── Deleted environments live in a view of their own ──────────────────

    /// <summary>Marks a seeded environment as one the customer deleted.</summary>
    private async Task SoftDeleteAsync(string name, DateTime? goneForGood)
    {
        await using var ctx = _db.NewContext();
        var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Name == name);
        env.Status = "SoftDeleted";
        env.SoftDeletedOn = DateTime.UtcNow.AddDays(-2);
        env.HardDeletePendingOn = goneForGood;
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// A deleted environment cannot be published to or updated, so listing it beside the
    /// live ones makes the fleet look both bigger and sicker than it is. In particular it
    /// must not count under Needs attention, which is a list of things to go and do.
    /// </summary>
    [Fact]
    public async Task A_deleted_environment_is_out_of_the_working_views_and_out_of_needs_attention()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);
        await SeedEnvironmentAsync(id, "JLE-260911110359", "Sandbox", "Active", now, now);
        await SoftDeleteAsync("JLE-260911110359", new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc));

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(1));
        cut.Find(".data-table tbody tr").TextContent.Should().Contain("Production");
        cut.Markup.Should().NotContain("JLE-260911110359", "the default view is the fleet you can work with");

        var tabs = cut.FindAll(".pill-tab").Select(t => t.TextContent.Trim()).ToList();
        tabs.First(t => t.StartsWith("All")).Should().EndWith("1");
        tabs.First(t => t.StartsWith("Needs attention")).Should()
            .EndWith("0", "a deleted environment is not something somebody has to go and fix");
        tabs.Should().Contain(t => t.StartsWith("Deleted"));
    }

    /// <summary>
    /// Nothing deleted is the normal state, and a view that is always empty is one people
    /// learn to ignore - which is the view that has to be noticed on the fortnight it
    /// isn't empty.
    /// </summary>
    [Fact]
    public async Task The_deleted_view_is_not_offered_when_nothing_has_been_deleted()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(1));
        cut.FindAll(".pill-tab").Select(t => t.TextContent.Trim())
            .Should().NotContain(t => t.StartsWith("Deleted"));
    }

    [Fact]
    public async Task The_deleted_view_says_when_each_one_goes_for_good_and_offers_to_bring_it_back()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "JLE-260911110359", "Sandbox", "Active", now, now);
        await SoftDeleteAsync("JLE-260911110359", new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc));

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() =>
            cut.FindAll(".pill-tab").Single(t => t.TextContent.Trim().StartsWith("Deleted")).Click());

        cut.WaitForAssertion(() =>
        {
            var row = cut.FindAll(".data-table tbody tr").Should().ContainSingle().Subject;
            row.TextContent.Should().Contain("JLE-260911110359");
            // The deadline takes the bold line, not the grey one under a dash.
            row.QuerySelectorAll(".cell-stack__main").Last().TextContent
                .Should().Be("Gone for good on 04 Oct 2026");
            row.QuerySelectorAll(".cell-stack__sub").Last().TextContent
                .Should().EndWith("to bring it back", "a date alone leaves the reader doing the arithmetic");
        });

        // The column header says what the column now holds.
        cut.FindAll(".data-table thead th").Select(h => h.TextContent.Trim())
            .Should().Contain("Gone for good").And.NotContain("Next update");

        // And the view itself says these can be brought back - not only the pill's tooltip.
        System.Text.RegularExpressions.Regex.Replace(cut.Find(".note--info").TextContent, @"\s+", " ")
            .Should().Contain("Each one can be brought back until the date below");

        var menu = cut.FindAll("button.menu__item, a.menu__item").Select(b => b.TextContent.Trim()).ToList();
        menu.Should().Contain("Recover this environment...")
            .And.NotContain("Upload an app...", "nothing can be installed on a deleted environment")
            .And.NotContain("Copy this environment...", "and there is nothing to copy until it is back");
        menu[0].Should().Be("Recover this environment...", "it is the only thing left to do, and the only one with a deadline");
    }

    /// <summary>
    /// Microsoft does not always give the date. An unknown one is said plainly rather
    /// than left as a dash the reader would have to take as "no deadline".
    /// </summary>
    [Fact]
    public async Task A_deleted_environment_with_no_deadline_from_microsoft_says_so()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "JLE", "Sandbox", "Active", now, now);
        await SoftDeleteAsync("JLE", goneForGood: null);

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() =>
            cut.FindAll(".pill-tab").Single(t => t.TextContent.Trim().StartsWith("Deleted")).Click());

        cut.WaitForAssertion(() =>
            cut.FindAll(".data-table tbody tr .cell-stack__main").Last().TextContent
                .Should().Be("Business Central hasn't said when it goes for good"));
    }

    /// <summary>
    /// The confirm names the environment and its customer before a write reaches the
    /// tenant, and says out loud when the environment is a production one.
    /// </summary>
    [Fact]
    public async Task Recovering_asks_first_and_names_the_environment_and_its_customer()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "JLE-260911110359", "Production", "Active", now, now);
        await SoftDeleteAsync("JLE-260911110359", new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc));

        var cut = _ctx.Render<EnvironmentsList>();
        cut.WaitForAssertion(() =>
            cut.FindAll(".pill-tab").Single(t => t.TextContent.Trim().StartsWith("Deleted")).Click());

        cut.WaitForAssertion(() =>
            cut.FindAll("button.menu__item").Single(b => b.TextContent.Trim() == "Recover this environment...").Click());

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Recover JLE-260911110359, a production environment?");
            cut.Markup.Should().Contain("CRONUS Denmark");
        });
    }

    // ── Storage: the environment's size, the customer's tenant against its allowance ──

    private async Task SetStorageAsync(int projectId, long quotaKb, params (string Environment, long Kb)[] sizes)
    {
        await using var ctx = _db.NewContext();
        var project = await ctx.OeProjects.SingleAsync(p => p.Id == projectId);
        project.BcStorageQuotaKb = quotaKb;
        project.BcStorageFetchedAt = DateTime.UtcNow;
        foreach (var (environment, kb) in sizes)
        {
            (await ctx.OeProjectEnvironments.SingleAsync(e => e.ProjectId == projectId && e.Name == environment)).BcDatabaseKb = kb;
        }
        await ctx.SaveChangesAsync();
    }

    private const long Gb = 1024 * 1024;

    [Fact]
    public async Task Each_row_shows_its_own_size_and_the_customers_tenant_against_its_allowance()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", DateTime.UtcNow, DateTime.UtcNow);
        await SeedEnvironmentAsync(id, "Sandbox", "Sandbox", "Active", DateTime.UtcNow, DateTime.UtcNow);
        await SetStorageAsync(id, 80 * Gb, ("Production", 50 * Gb), ("Sandbox", 18 * Gb));

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() =>
        {
            var cells = cut.FindAll(".env-storage");
            cells.Select(c => c.QuerySelector(".cell-stack__main")!.TextContent).Should().BeEquivalentTo(["50.0 GB", "18.0 GB"]);
            cells.Should().AllSatisfy(c =>
            {
                c.QuerySelector(".cell-stack__sub")!.TextContent.Should().Be("Customer at 85% of 80.0 GB",
                    "the allowance is the tenant's, so both rows carry the same 68 of 80");
                c.QuerySelector("progress")!.ClassList.Should().Contain("env-storage__bar--warn");
            });
        });
    }

    [Fact]
    public async Task A_customer_over_their_allowance_gets_a_full_red_bar_and_is_told_so_in_words()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", DateTime.UtcNow, DateTime.UtcNow);
        await SetStorageAsync(id, 80 * Gb, ("Production", 92 * Gb));

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() =>
        {
            var cell = cut.Find(".env-storage");
            cell.QuerySelector(".cell-stack__sub")!.TextContent.Should().Be("Customer at 115% of 80.0 GB - over its allowance");
            var bar = cell.QuerySelector("progress")!;
            bar.ClassList.Should().Contain("env-storage__bar--danger");
            bar.GetAttribute("value").Should().Be("1", "a bar cannot be more than full; the words carry the rest");
        });
        cut.FindAll(".pill-tab, .seg__btn, [role=tab]").Single(t => t.TextContent.Contains("Needs attention"))
            .TextContent.Should().Contain("1", "over the allowance is a conversation somebody has to have");
    }

    [Fact]
    public async Task Storage_nobody_has_read_yet_is_a_dash_and_a_roomy_tenant_gets_no_colour()
    {
        var unread = await SeedSolutionAsync("CRONUS Norway");
        await SeedEnvironmentAsync(unread, "Production", "Production", "Active", DateTime.UtcNow, DateTime.UtcNow);
        var roomy = await SeedSolutionAsync("CRONUS Sweden");
        await SeedEnvironmentAsync(roomy, "Production", "Production", "Active", DateTime.UtcNow, DateTime.UtcNow);
        await SetStorageAsync(roomy, 80 * Gb, ("Production", 800 * 1024));

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() =>
        {
            var cell = cut.FindAll(".env-storage").Should().ContainSingle().Subject;
            cell.QuerySelector(".cell-stack__main")!.TextContent.Should().Be("800 MB");
            cell.QuerySelector("progress")!.ClassList.Should().NotContain(c => c.StartsWith("env-storage__bar--"));
        });
    }

    // ── Selection and the bulk delivery window (#961) ─────────────────────

    private async Task SetWindowAsync(string name, TimeOnly? start, TimeOnly? end)
    {
        await using var ctx = _db.NewContext();
        var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Name == name);
        env.UpdateWindowStart = start;
        env.UpdateWindowEnd = end;
        await ctx.SaveChangesAsync();
    }

    private static IEnumerable<AngleSharp.Dom.IElement> RowChecks(IRenderedComponent<EnvironmentsList> cut) =>
        cut.FindAll(".data-table tbody td.data-table__col-check input[type=checkbox]");

    private static string Summary(IRenderedComponent<EnvironmentsList> cut) =>
        cut.Find(".bulk-bar__summary").TextContent.Trim();

    private static AngleSharp.Dom.IElement BarButton(IRenderedComponent<EnvironmentsList> cut, string label) =>
        cut.FindAll(".bulk-bar button").Single(b => b.TextContent.Trim() == label);

    /// <summary>
    /// Ticking rows brings up the bar with the count and the one action; the header box
    /// ticks every row on screen; and a tab that takes the ticked rows off the screen
    /// leaves them ticked (#985) - the bar stays over the empty view, says they are
    /// there but not shown, and Show selected brings them back.
    /// </summary>
    [Fact]
    public async Task Ticked_rows_bring_up_the_bulk_bar_and_stay_ticked_under_a_tab_that_hides_them()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);
        await SeedEnvironmentAsync(id, "Test", "Sandbox", "Active", now, now);

        var cut = _ctx.Render<EnvironmentsList>();
        cut.WaitForAssertion(() => RowChecks(cut).Should().HaveCount(2));
        cut.FindAll(".bulk-bar").Should().BeEmpty("nothing is ticked yet");

        cut.WaitForAssertion(() => RowChecks(cut).First().Change(true));
        cut.WaitForAssertion(() =>
        {
            Summary(cut).Should().Be("1 selected");
            cut.Find(".bulk-bar").TextContent.Should().Contain("Set delivery window...");
        });

        cut.WaitForAssertion(() => cut.Find("th.data-table__col-check input[type=checkbox]").Change(true));
        cut.WaitForAssertion(() => Summary(cut).Should().Be("2 selected"));

        // Nothing needs attention here, so that view shows none of the ticked rows.
        cut.WaitForAssertion(() =>
            cut.FindAll(".pill-tab").Single(t => t.TextContent.Trim().StartsWith("Needs attention")).Click());
        cut.WaitForAssertion(() => Summary(cut).Should().Be("2 selected, 0 shown"));

        // The view is empty, and the bar over its empty state is the way back to the ticks.
        cut.WaitForAssertion(() => BarButton(cut, "Show selected").Click());
        cut.WaitForAssertion(() =>
        {
            RowChecks(cut).Should().HaveCount(2).And.OnlyContain(c => c.HasAttribute("checked"));
            Summary(cut).Should().Be("2 selected");
        });
    }

    /// <summary>
    /// The upgrade team finds each customer by short name in turn. A search must not
    /// throw away the customers already found, the header box must only reach the rows
    /// under it, and Show selected brings the whole selection back into view (#985).
    /// </summary>
    [Fact]
    public async Task A_search_keeps_the_ticks_and_show_selected_brings_them_back()
    {
        var cronus = await SeedSolutionAsync("CRONUS Denmark");
        var fabrikam = await SeedSolutionAsync("Fabrikam");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(cronus, "Production", "Production", "Active", now, now);
        await SeedEnvironmentAsync(fabrikam, "Live", "Production", "Active", now, now);
        await SeedEnvironmentAsync(fabrikam, "Test", "Sandbox", "Active", now, now);

        var cut = _ctx.Render<EnvironmentsList>();
        cut.WaitForAssertion(() => RowChecks(cut).Should().HaveCount(3));

        cut.WaitForAssertion(() => cut.Find("input[type=search]").Input("cronus"));
        cut.WaitForAssertion(() => RowChecks(cut).Should().ContainSingle());
        RowChecks(cut).Single().Change(true);

        cut.WaitForAssertion(() => cut.Find("input[type=search]").Input("fabrikam"));
        cut.WaitForAssertion(() =>
        {
            RowChecks(cut).Should().HaveCount(2);
            Summary(cut).Should().Be("1 selected, 0 shown");
            cut.Find("th.data-table__col-check label").ClassList.Should().NotContain("is-indeterminate",
                "the tick off screen is not one of the rows under the box");
        });

        // The header box ticks the two rows shown and leaves the hidden one alone...
        cut.Find("th.data-table__col-check input[type=checkbox]").Change(true);
        cut.WaitForAssertion(() => Summary(cut).Should().Be("3 selected, 2 shown"));

        // ...and unticking it takes only those two back off.
        cut.Find("th.data-table__col-check input[type=checkbox]").Change(false);
        cut.WaitForAssertion(() => Summary(cut).Should().Be("1 selected, 0 shown"));

        cut.WaitForAssertion(() => BarButton(cut, "Show selected").Click());
        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".data-table tbody tr");
            rows.Should().ContainSingle().Which.TextContent.Should().Contain("CRONUS Denmark");
            BarButton(cut, "Show selected").GetAttribute("aria-pressed").Should().Be("true");
            Summary(cut).Should().Be("1 selected");
        });

        // The bulk action acts on every tick and names each one before it writes.
        cut.WaitForAssertion(() => BarButton(cut, "Set delivery window...").Click());
        cut.WaitForAssertion(() =>
            cut.Find("#bdw-title").TextContent.Trim().Should().Be("Set the delivery window on 1 environment"));
        cut.FindAll(".confirm-dialog__actions button").First(b => b.TextContent.Trim() == "Cancel").Click();

        cut.WaitForAssertion(() => BarButton(cut, "Clear selection").Click());
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".bulk-bar").Should().BeEmpty();
            // The search is still in the box, so the table goes back to what it finds.
            RowChecks(cut).Should().HaveCount(2);
        });
    }

    /// <summary>
    /// Show selected is a view of its own: typing a new search or picking a filter is
    /// asking for something else, so it ends and every tick is kept.
    /// </summary>
    [Fact]
    public async Task Changing_a_filter_under_show_selected_goes_back_to_the_filtered_table()
    {
        var cronus = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(cronus, "Production", "Production", "Active", now, now);
        await SeedEnvironmentAsync(cronus, "Test", "Sandbox", "Active", now, now);

        var cut = _ctx.Render<EnvironmentsList>();
        cut.WaitForAssertion(() => RowChecks(cut).Should().HaveCount(2));
        RowChecks(cut).First().Change(true);
        cut.WaitForAssertion(() => BarButton(cut, "Show selected").Click());
        cut.WaitForAssertion(() => RowChecks(cut).Should().ContainSingle());

        cut.Find("select[aria-label='Filter by environment type']").Change("Sandbox");
        cut.WaitForAssertion(() =>
        {
            RowChecks(cut).Should().ContainSingle();
            cut.Find(".data-table tbody tr").TextContent.Should().Contain("Test");
            BarButton(cut, "Show selected").GetAttribute("aria-pressed").Should().Be("false");
            Summary(cut).Should().Be("1 selected, 0 shown");
        });
    }

    [Fact]
    public async Task A_deleted_environment_cannot_be_ticked()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "JLE-260911110359", "Sandbox", "Active", now, now);
        await SoftDeleteAsync("JLE-260911110359", new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc));

        var cut = _ctx.Render<EnvironmentsList>();
        cut.WaitForAssertion(() =>
            cut.FindAll(".pill-tab").Single(t => t.TextContent.Trim().StartsWith("Deleted")).Click());
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".data-table tbody tr").Should().HaveCount(1);
            RowChecks(cut).Should().BeEmpty();
            cut.FindAll("th.data-table__col-check input").Should().BeEmpty();
        });
    }

    /// <summary>
    /// The dialog previews before it writes: which rows change and from what, which
    /// already have that window, whose clock the times are on - and the confirm counts
    /// only the rows that will change. After the run it says what happened to each.
    /// </summary>
    [Fact]
    public async Task The_window_dialog_groups_the_selection_and_reports_each_row_after_setting_it()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);
        await SeedEnvironmentAsync(id, "Test", "Sandbox", "Active", now, now);
        await SetWindowAsync("Production", new TimeOnly(1, 0), new TimeOnly(5, 0));
        await SetWindowAsync("Test", new TimeOnly(22, 0), new TimeOnly(6, 0));

        var cut = _ctx.Render<EnvironmentsList>();
        cut.WaitForAssertion(() => cut.Find("th.data-table__col-check input[type=checkbox]").Change(true));
        cut.WaitForAssertion(() =>
            cut.FindAll(".bulk-bar button").Single(b => b.TextContent.Trim() == "Set delivery window...").Click());

        cut.WaitForAssertion(() =>
        {
            cut.Find("#bdw-title").TextContent.Trim().Should().Be("Set the delivery window on 2 environments");
            cut.Find(".bdw-zone").TextContent.Should().Contain("each customer's own Business Central time zone");
            cut.FindAll(".bdw-row").Should().BeEmpty("nothing is previewed until a window is chosen");
            cut.FindAll(".confirm-dialog__actions button").Last().TextContent.Trim().Should().Be("Set window");
        });

        cut.WaitForAssertion(() => cut.Find("input[aria-label='Window start']").Change("22:00"));
        cut.WaitForAssertion(() => cut.Find("input[aria-label='Window end']").Change("06:00"));

        cut.WaitForAssertion(() =>
        {
            var heads = cut.FindAll(".bdw-group__head").Select(h => h.TextContent.Trim()).ToList();
            heads.Should().Equal(new[] { "Will change (1)", "Already set (1)" }, cut.Find(".confirm-dialog").OuterHtml);
            var rows = cut.FindAll(".bdw-row");
            rows[0].TextContent.Should().Contain("CRONUS Denmark - Production").And.Contain("01:00-05:00 → 22:00-06:00");
            rows[1].TextContent.Should().Contain("Already 22:00-06:00");
            cut.FindAll(".confirm-dialog__actions button").Last().TextContent.Trim()
                .Should().Be("Set window on 1 environment");
        });

        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions button").Last().Click());
        cut.WaitForAssertion(() =>
        {
            cut.Find("#bdw-title").TextContent.Trim().Should().Be("Delivery window set");
            cut.Find(".bdw-lead").TextContent.Should().Be("Set 22:00-06:00 on 1 environment. 1 skipped.");
            cut.FindAll(".bulk-bar").Should().BeEmpty("the ticks have done their job");
        });

        await using var verify = _db.NewContext();
        var production = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Name == "Production");
        production.UpdateWindowStart.Should().Be(new TimeOnly(22, 0));
        production.UpdateWindowEnd.Should().Be(new TimeOnly(6, 0));
    }

    /// <summary>Many customers are only ever called by their short name (#966).</summary>
    [Fact]
    public async Task The_search_finds_a_solution_by_its_short_name_and_the_row_shows_it()
    {
        var cronus = await SeedSolutionAsync("CRONUS Denmark");
        var other = await SeedSolutionAsync("Fabrikam");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(cronus, "Production", "Production", "Active", now, now);
        await SeedEnvironmentAsync(other, "Live", "Production", "Active", now, now);
        await using (var ctx = _db.NewContext())
        {
            (await ctx.OeProjects.SingleAsync(p => p.Id == cronus)).ShortName = "CRN";
            await ctx.SaveChangesAsync();
        }

        var cut = _ctx.Render<EnvironmentsList>();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(2));
        cut.Find(".sol-list__short").TextContent.Should().Be("CRN");
        cut.FindAll("select[aria-label='Filter by solution'] option").Select(o => o.TextContent.Trim())
            .Should().Contain("CRONUS Denmark (CRN)");

        cut.WaitForAssertion(() => cut.Find("input[type=search]").Input("crn"));
        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".data-table tbody tr");
            rows.Should().ContainSingle();
            rows[0].TextContent.Should().Contain("CRONUS Denmark");
        });
    }

    // ── Auto-refresh (#983) ──────────────────────────────────────────────
    //
    // The named user here is an ops engineer with the list open on a second screen
    // during a release week. The tick is driven directly; the timer only calls it.

    private async Task MakeOwnerAnAdminAsync()
    {
        await using var ctx = _db.NewContext();
        (await ctx.Users.SingleAsync(u => u.Id == OwnerUserId)).Role = UserRole.Admin;
        await ctx.SaveChangesAsync();
    }

    private async Task StampEnvironmentsReadAsync(int projectId, DateTime at)
    {
        await using var ctx = _db.NewContext();
        (await ctx.OeProjects.SingleAsync(p => p.Id == projectId)).BcEnvironmentsFetchedAt = at;
        await ctx.SaveChangesAsync();
    }

    private static AngleSharp.Dom.IElement AutoBox(IRenderedComponent<EnvironmentsList> cut) =>
        cut.Find(".freshness input[data-auto-refresh]");

    [Fact]
    public async Task The_auto_refresh_switch_sits_in_the_freshness_strip_and_starts_off()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".freshness .freshness__auto").TextContent.Trim().Should().Be("Refresh every 5 minutes");
            AutoBox(cut).HasAttribute("checked").Should().BeFalse("nothing remembers it between visits");
        });
    }

    [Fact]
    public async Task Auto_in_the_address_starts_the_page_with_the_switch_on()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);
        _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/environments?auto=1");

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() => AutoBox(cut).HasAttribute("checked").Should().BeTrue());
    }

    [Fact]
    public async Task A_tick_asks_Business_Central_about_a_solution_whose_read_has_gone_stale()
    {
        await MakeOwnerAnAdminAsync();
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);
        await StampEnvironmentsReadAsync(id, now.AddMinutes(-10));

        var cut = _ctx.Render<EnvironmentsList>();
        cut.WaitForAssertion(() => AutoBox(cut));
        // Ticking the box runs the first round straight away.
        await AutoBox(cut).ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        cut.WaitForAssertion(() =>
            cut.Find(".note--info .note__body").TextContent.Should().Contain("Asking Business Central about 1 solution"));
        _ctx.Services.GetRequiredService<EnvironmentRefreshQueue>().IsInFlight(id).Should().BeTrue();
    }

    [Fact]
    public async Task A_tick_leaves_a_freshly_read_solution_alone_and_says_so()
    {
        await MakeOwnerAnAdminAsync();
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);
        await StampEnvironmentsReadAsync(id, now.AddMinutes(-1));

        var cut = _ctx.Render<EnvironmentsList>();
        cut.WaitForAssertion(() => AutoBox(cut));
        await AutoBox(cut).ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        await cut.InvokeAsync(() => cut.Instance.AutoRefreshTickAsync());

        cut.WaitForAssertion(() =>
            cut.Find(".note--info .note__body").TextContent.Should().StartWith("Already up to date"));
        _ctx.Services.GetRequiredService<EnvironmentRefreshQueue>().IsInFlight(id).Should().BeFalse(
            "another open page, or the sweep, read it a minute ago");
    }

    [Fact]
    public async Task A_tick_does_nothing_while_the_switch_is_off()
    {
        await MakeOwnerAnAdminAsync();
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);

        var cut = _ctx.Render<EnvironmentsList>();
        cut.WaitForAssertion(() => AutoBox(cut));
        await cut.InvokeAsync(() => cut.Instance.AutoRefreshTickAsync());

        _ctx.Services.GetRequiredService<EnvironmentRefreshQueue>().IsInFlight(id).Should().BeFalse();
        cut.FindAll(".note--info").Should().BeEmpty();
    }

    [Fact]
    public async Task The_auto_refresh_turns_itself_off_when_its_time_is_up()
    {
        await MakeOwnerAnAdminAsync();
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);

        var saved = EnvironmentsList.AutoRefreshFor;
        EnvironmentsList.AutoRefreshFor = TimeSpan.Zero;
        try
        {
            var cut = _ctx.Render<EnvironmentsList>();
            cut.WaitForAssertion(() => AutoBox(cut));
            await AutoBox(cut).ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

            cut.WaitForAssertion(() =>
            {
                cut.Find(".note--info .note__body").TextContent.Should().StartWith("Stopped refreshing every 5 minutes");
                AutoBox(cut).HasAttribute("checked").Should().BeFalse();
            });
            _ctx.Services.GetRequiredService<EnvironmentRefreshQueue>().IsInFlight(id).Should().BeFalse();
        }
        finally
        {
            EnvironmentsList.AutoRefreshFor = saved;
        }
    }

    /// <summary>
    /// Typing fast used to lose characters: every keystroke redrew the page and wrote
    /// the value of an older keystroke back into the box (#981). The box is bound now,
    /// and the table follows once typing pauses.
    /// </summary>
    [Fact]
    public async Task The_table_filters_once_typing_pauses_and_the_box_keeps_every_keystroke()
    {
        var cronus = await SeedSolutionAsync("CRONUS Denmark");
        var other = await SeedSolutionAsync("Fabrikam");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(cronus, "Production", "Production", "Active", now, now);
        await SeedEnvironmentAsync(other, "Live", "Production", "Active", now, now);
        EnvironmentsList.SearchDebounce = TimeSpan.FromMilliseconds(300);

        var cut = _ctx.Render<EnvironmentsList>();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(2));

        cut.Find("input[type=search]").Input("CRONUS");
        cut.Find("input[type=search]").Input("CRONUS ");
        cut.Find("input[type=search]").Input("CRONUS D");

        cut.WaitForAssertion(() =>
            cut.Find("input[type=search]").GetAttribute("value").Should().Be("CRONUS D"));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".data-table tbody tr");
            rows.Should().ContainSingle();
            rows[0].TextContent.Should().Contain("CRONUS Denmark");
        }, TimeSpan.FromSeconds(5));
        cut.Find("input[type=search]").GetAttribute("value").Should().Be("CRONUS D");
    }
}
