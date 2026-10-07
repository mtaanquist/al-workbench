using ALDevToolbox.Components.Pages.Upgrades;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Upgrades page as the home of planned upgrades (issue #984, sub-issue D): the Open,
/// Archive and Fleet views under one head (PageUpgradesList.dc.html). The named user is
/// somebody on the upgrade team planning tonight's wave: they open the page to see which
/// waves are open and how far each has got, make a new one, put environments on it from
/// the fleet, and mark it done once every environment has been checked.
///
/// <para>The Fleet view's own behaviour is <see cref="UpgradesPageTests"/>; this class
/// is the other two views, the switch between the three, and the planned upgrades' own
/// dialogs.</para>
/// </summary>
public sealed class UpgradesListPageTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private const int UserId = 9870;
    private const string Actor = "Anna Jensen <anna@example.com>";
    private static readonly Guid TenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public UpgradesListPageTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("anna@example.com");
        UpgradesPage.SearchDebounce = TimeSpan.Zero;
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
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "anna@example.com",
            PasswordHash = "x",
            DisplayName = "Anna Jensen",
            // An org admin holds the environment-ops grant outright.
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    // ── Seeding ─────────────────────────────────────────────────────────

    /// <summary>A solution with environments on the given versions, named Production, Sandbox, Test...</summary>
    private async Task<(int ProjectId, List<int> EnvironmentIds)> SeedSolutionAsync(string name, params string[] versions)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            BcTenantId = TenantId,
            CreatedByUserId = UserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        string[] names = ["Production", "Sandbox", "Test", "UAT"];
        var environments = versions.Select((version, i) => new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Name = names[i],
            Type = i == 0 ? "Production" : "Sandbox",
            ApplicationFamily = "BusinessCentral",
            Status = "Active",
            Version = version,
            BcNextUpdateVersion = "28.5.1.0",
            FetchedAt = DateTime.UtcNow,
        }).ToList();
        ctx.OeProjectEnvironments.AddRange(environments);
        await ctx.SaveChangesAsync();
        return (project.Id, environments.Select(e => e.Id).ToList());
    }

    private async Task<int> SeedUpgradeAsync(
        string name, int projectId, IEnumerable<int> environmentIds, DateTime? plannedAt = null,
        bool closed = false, IEnumerable<int>? checkedIds = null, string target = "28.5")
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var ticked = checkedIds?.ToHashSet() ?? [];
        var upgrade = new OeEnvironmentUpgrade
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            TargetVersion = target,
            PlannedAt = plannedAt,
            CreatedByUserId = UserId,
            CreatedBy = Actor,
            CreatedAt = now.AddDays(-3),
            UpdatedAt = now,
            ClosedAt = closed ? now.AddDays(-1) : null,
            ClosedByUserId = closed ? UserId : null,
            ClosedBy = closed ? Actor : null,
            Lines = environmentIds.Select(id => new OeEnvironmentUpgradeLine
            {
                OrganizationId = TestDb.DefaultOrgId,
                EnvironmentId = id,
                ProjectId = projectId,
                IsOpen = !closed,
                AddedAt = now.AddDays(-2),
                CheckedAt = ticked.Contains(id) ? now : null,
                CheckedBy = ticked.Contains(id) ? Actor : null,
            }).ToList(),
        };
        ctx.OeEnvironmentUpgrades.Add(upgrade);
        await ctx.SaveChangesAsync();
        return upgrade.Id;
    }

    /// <summary>An action from the upgrade: what makes it part of the record.</summary>
    private async Task SeedActionAsync(
        int upgradeId, int projectId, int environmentId, UpgradeActionStatus status, DateTime? executeAfter = null)
    {
        var pending = status == UpgradeActionStatus.Pending;
        await using var ctx = _db.NewContext();
        ctx.OeEnvironmentUpgradeActions.Add(new OeEnvironmentUpgradeAction
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            EnvironmentId = environmentId,
            UpgradeId = upgradeId,
            Kind = UpgradeActionKind.RunNow,
            Status = status,
            RequestedByUserId = UserId,
            RequestedBy = Actor,
            RequestedAt = DateTime.UtcNow.AddHours(-2),
            ExecuteAfter = executeAfter ?? DateTime.UtcNow.AddHours(-2),
            SentAt = pending ? null : DateTime.UtcNow.AddHours(-2),
        });
        await ctx.SaveChangesAsync();
    }

    private IRenderedComponent<UpgradesPage> Render(string? view = null)
    {
        if (view is not null)
        {
            var nav = _ctx.Services.GetRequiredService<NavigationManager>();
            nav.NavigateTo(nav.GetUriWithQueryParameter("view", view));
        }
        return _ctx.Render<UpgradesPage>();
    }

    private static List<string> Tabs(IRenderedComponent<UpgradesPage> cut) =>
        cut.FindAll(".pill-tabs .pill-tab").Select(t => System.Text.RegularExpressions.Regex.Replace(t.TextContent, @"\s+", "")).ToList();

    // ── The switch ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_bare_visit_opens_on_the_Open_view_with_three_counted_tabs_under_the_head()
    {
        var (projectId, envs) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0", "28.5.1.0", "27.5.1.0");
        await SeedUpgradeAsync("28.5 in November", projectId, envs.Take(2));

        var cut = Render();

        cut.WaitForAssertion(() => cut.FindAll(".upl-table tbody tr").Should().HaveCount(1));
        // The switch is its own row under the head, not part of the filter row, so it does
        // not move when the row under it changes with the view.
        var page = cut.Find("div.page");
        page.Children[0].ClassList.Should().Contain("page-head");
        page.Children[1].QuerySelector(".pill-tabs").Should().NotBeNull();
        // Archive is always there, even at 0; Fleet counts the fleet table's rows.
        Tabs(cut).Should().Equal("Open1", "Archive0", "Fleet3");
        cut.FindAll(".pill-tab").Select(t => t.GetAttribute("href"))
            .Should().Equal("/upgrades", "/upgrades?view=archive", "/upgrades?view=fleet");
        cut.Find(".pill-tab.is-active").TextContent.Trim().Should().StartWith("Open");
        cut.FindAll(".cmdbar").Should().BeEmpty("the fleet's command bar belongs to the Fleet view");
    }

    [Fact]
    public async Task The_Fleet_view_is_the_fleet_table_under_the_same_head_and_New_upgrade_stays_the_one_primary()
    {
        await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0");

        var cut = Render("fleet");

        cut.WaitForAssertion(() => cut.FindAll(".upg-scroll .data-table tbody tr").Should().HaveCount(1));
        cut.Find(".page-head__title").TextContent.Should().Be("Upgrades");
        cut.Find(".pill-tab.is-active").TextContent.Trim().Should().StartWith("Fleet");
        cut.FindAll(".btn--primary").Should().ContainSingle().Which.TextContent.Trim().Should().Be("New upgrade");
    }

    // ── Open ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_open_upgrade_shows_its_status_how_far_each_environment_got_and_how_many_are_checked()
    {
        var (projectId, envs) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0", "28.5.1.0", "28.5.1.0");
        // Production still on 27.5 (planned), Sandbox on 28.5 (updated), Test on 28.5 and ticked.
        var id = await SeedUpgradeAsync("28.5 in November", projectId, envs, checkedIds: [envs[2]]);

        var cut = Render();

        cut.WaitForAssertion(() => cut.FindAll(".upl-table tbody tr").Should().HaveCount(1));
        var row = cut.Find(".upl-table tbody tr");
        row.QuerySelector("a")!.GetAttribute("href").Should().Be($"/upgrades/{id}");
        row.QuerySelector(".cell-stack__sub")!.TextContent.Trim().Should().Be("to 28.5");
        row.QuerySelector(".status-pill")!.ClassList.Should().Contain("status-pill--running");
        row.QuerySelector(".status-pill")!.TextContent.Trim().Should().Be("In progress");
        // Checked lines count as updated in the breakdown, and only in the Checked column
        // as checked, so none is counted twice.
        row.QuerySelectorAll(".upl-part").Select(p => p.TextContent.Trim()).Should().Equal("2 updated", "1 planned");
        row.QuerySelector(".upl-check__text")!.TextContent.Trim().Should().Be("1 of 3");
        row.QuerySelector("progress")!.GetAttribute("value").Should().Be("33");
        cut.Find(".page-head__sub").TextContent.Should().Be("1 open, nothing booked for tonight.");
        cut.Find(".pager__count").TextContent.Trim().Should().Be("1 open upgrade");
    }

    [Fact]
    public async Task An_upgrade_with_no_environments_says_so_and_has_no_checked_bar()
    {
        await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0");
        await using (var ctx = _db.NewContext())
        {
            ctx.OeEnvironmentUpgrades.Add(new OeEnvironmentUpgrade
            {
                OrganizationId = TestDb.DefaultOrgId, Name = "28.5 in November", TargetVersion = "28.5",
                CreatedBy = Actor, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var cut = Render();

        cut.WaitForAssertion(() => cut.FindAll(".upl-table tbody tr").Should().HaveCount(1));
        var row = cut.Find(".upl-table tbody tr");
        row.TextContent.Should().Contain("No environments yet");
        row.QuerySelectorAll("progress").Should().BeEmpty();
        row.QuerySelector(".status-pill")!.TextContent.Trim().Should().Be("Planned");
    }

    [Fact]
    public async Task With_nothing_planned_the_empty_state_takes_the_one_primary_and_the_head_drops_it()
    {
        var cut = Render();

        cut.WaitForAssertion(() => cut.Find(".empty-state__title").TextContent.Should().Be("Nothing planned yet"));
        cut.FindAll(".page-head__actions").Should().BeEmpty();
        cut.FindAll(".btn--primary").Should().ContainSingle()
            .Which.TextContent.Trim().Should().Be("New upgrade");
        cut.Find(".empty-state__action .btn--primary").Should().NotBeNull();
        cut.Find(".page-head__sub").TextContent.Should().Be("Nothing planned.");
        cut.FindAll(".filter-bar").Should().BeEmpty("there is nothing to filter");
        Tabs(cut).Should().StartWith("Open0").And.Contain("Archive0");
    }

    [Fact]
    public async Task While_the_upgrades_load_the_Open_view_shows_its_columns_and_says_what_it_is_reading()
    {
        // Hold the upgrades table so the page's first read waits, the way a slow database would.
        await using var hold = new NpgsqlConnection(_db.ConnectionString);
        await hold.OpenAsync();
        await using var tx = await hold.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand("lock table oe_environment_upgrades in access exclusive mode", hold, tx))
        {
            await lockCmd.ExecuteNonQueryAsync();
        }

        var cut = Render();

        cut.WaitForAssertion(() => cut.Find(".loading-block").TextContent.Should().Contain("Loading upgrades..."));
        // The page reads its own record of the upgrades; the line must not claim Business Central.
        cut.Find(".loading-block").TextContent.Should().NotContain("Business Central");
        cut.FindAll(".upg-plan-skeleton thead th").Select(t => t.TextContent.Trim()).Should()
            .StartWith(["Upgrade", "Status", "Environments", "Checked", "Planned"]);
        cut.FindAll(".page-head__actions").Should().BeEmpty("until the read returns, the page cannot tell which button is the next step");

        await tx.RollbackAsync();
        cut.WaitForAssertion(() => cut.Find(".empty-state__title").TextContent.Should().Be("Nothing planned yet"));
    }

    [Fact]
    public async Task Open_upgrades_are_ordered_tonight_first_then_the_ones_that_ran_then_the_ones_to_come()
    {
        var (projectId, _) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0");
        var now = DateTime.UtcNow;
        await SeedUpgradeAsync("Next month", projectId, [], plannedAt: now.AddDays(30));
        await SeedUpgradeAsync("Last week", projectId, [], plannedAt: now.AddDays(-7));
        await SeedUpgradeAsync("No slot", projectId, []);
        await SeedUpgradeAsync("In three days", projectId, [], plannedAt: now.AddDays(3));
        await SeedUpgradeAsync("Two days ago", projectId, [], plannedAt: now.AddDays(-2));

        var cut = Render();

        cut.WaitForAssertion(() => cut.FindAll(".upl-table tbody tr").Should().HaveCount(5));
        cut.FindAll(".upl-table tbody tr .upl-name").Select(a => a.TextContent.Trim()).Should().Equal(
            "Two days ago", "Last week", "In three days", "Next month", "No slot");
    }

    [Fact]
    public async Task The_search_narrows_the_open_list_and_a_search_that_matches_nothing_offers_the_way_back()
    {
        var (projectId, _) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0");
        await SeedUpgradeAsync("28.5 in November", projectId, []);
        await SeedUpgradeAsync("Sandboxes first", projectId, [], target: "28.4");

        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".upl-table tbody tr").Should().HaveCount(2));

        cut.Find(".filter-bar input[type=search]").Input("28.4");
        cut.WaitForAssertion(() => cut.FindAll(".upl-table tbody tr .upl-name").Select(a => a.TextContent.Trim())
            .Should().Equal("Sandboxes first"));
        cut.Find(".pager__count").TextContent.Trim().Should().Be("Showing 1 of 2 open upgrades");

        cut.Find(".filter-bar input[type=search]").Input("nothing like it");
        cut.WaitForAssertion(() => cut.Find(".empty-state__title").TextContent.Should().Be("No open upgrades match these filters"));
        cut.FindAll(".filter-bar").Should().ContainSingle("the filter row is the way back");
        cut.FindAll(".empty-state__action button").Single().Click();
        cut.WaitForAssertion(() => cut.FindAll(".upl-table tbody tr").Should().HaveCount(2));
    }

    [Fact]
    public async Task Delete_is_held_back_once_anything_was_sent_and_says_why()
    {
        var (projectId, envs) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0", "27.5.1.0");
        var sent = await SeedUpgradeAsync("Sent", projectId, [envs[0]]);
        await SeedActionAsync(sent, projectId, envs[0], UpgradeActionStatus.Sent);
        await SeedUpgradeAsync("Untouched", projectId, [envs[1]]);

        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".upl-table tbody tr").Should().HaveCount(2));

        AngleSharp.Dom.IElement DeleteFor(string name) => cut.FindAll(".upl-table tbody tr")
            .Single(r => r.QuerySelector(".upl-name")!.TextContent.Trim() == name)
            .QuerySelectorAll(".menu__item").Single(i => i.TextContent.Trim() == "Delete");

        DeleteFor("Sent").HasAttribute("disabled").Should().BeTrue();
        DeleteFor("Sent").GetAttribute("title").Should().Contain("can only be marked done");
        DeleteFor("Untouched").HasAttribute("disabled").Should().BeFalse();
        cut.FindAll(".upl-table tbody tr").First().QuerySelectorAll(".menu__item").Select(i => i.TextContent.Trim())
            .Should().Equal("Open", "Mark done...", "Delete");

        DeleteFor("Untouched").Click();
        cut.WaitForAssertion(() => cut.Find(".confirm-dialog__title").TextContent.Should().Be("Delete \"Untouched\"?"));
        cut.FindAll(".confirm-dialog__actions .btn").Single(b => b.TextContent.Trim() == "Delete upgrade").Click();

        cut.WaitForAssertion(() => cut.FindAll(".upl-table tbody tr").Should().HaveCount(1));
        cut.Find(".alert").TextContent.Should().Contain("\"Untouched\" was deleted.");
    }

    [Fact]
    public async Task Mark_done_counts_and_names_the_unchecked_lines_before_anything_moves()
    {
        var (projectId, envs) = await SeedSolutionAsync("Contoso Retail", "27.5.1.0", "28.5.1.0", "28.5.1.0");
        await SeedUpgradeAsync("28.5 in November", projectId, envs, checkedIds: [envs[2]]);

        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".upl-table tbody tr").Should().HaveCount(1));

        cut.FindAll(".upl-table .menu__item").Single(i => i.TextContent.Trim() == "Mark done...").Click();

        cut.WaitForAssertion(() => cut.Find(".confirm-dialog__title").TextContent
            .Should().Be("2 of 3 are not checked yet. Mark done anyway?"));
        cut.Find(".confirm-dialog__body").TextContent.Should().Contain(
            "\"28.5 in November\" moves to the Archive and becomes read-only. The environments not ticked off stay that way in the record.");
        // The sheet's warning icon, since something is still unchecked (the upgrade page's too).
        cut.Find(".confirm-dialog__icon svg").GetAttribute("class").Should().Contain("triangle-alert");
        var notChecked = cut.FindAll(".upg-done-list__row");
        notChecked.Select(r => r.QuerySelector(".status-pill")!.TextContent.Trim()).Should().BeEquivalentTo(["Planned", "Updated"]);
        notChecked.Should().AllSatisfy(r => r.TextContent.Should().Contain("Contoso Retail"));
        // Only the one never started would go on a second pass, and with two listed it is
        // named rather than counted, so the hint cannot read as a miscount.
        cut.Find(".upg-done-hint").TextContent.Should().Contain("new upgrade with Contoso Retail Production from the upgrade's page");
        // "Production", not "Production - Production": the type only where the name does not say it.
        notChecked.Select(r => r.QuerySelector(".cell-stack__sub")!.TextContent.Trim()).Should().BeEquivalentTo(["Production", "Sandbox"]);

        cut.FindAll(".confirm-dialog__actions .btn").Single(b => b.TextContent.Trim() == "Mark done").Click();

        cut.WaitForAssertion(() => cut.Find(".empty-state__title").TextContent.Should().Be("Nothing planned yet"));
        cut.Find(".alert").TextContent.Should().Contain("is marked done and moved to the Archive");
        Tabs(cut).Should().Contain("Archive1");
    }

    // ── Archive ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_archive_lists_done_upgrades_read_only_with_failures_in_danger()
    {
        var (projectId, envs) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0", "28.5.1.0");
        var id = await SeedUpgradeAsync("28.5 September Production", projectId, envs, closed: true);
        await SeedActionAsync(id, projectId, envs[0], UpgradeActionStatus.Failed);

        var cut = Render("archive");

        cut.WaitForAssertion(() => cut.FindAll(".upa-table tbody tr").Should().HaveCount(1));
        cut.FindAll(".upa-table thead th").Select(t => t.TextContent.Trim()).Take(5).Should()
            .Equal("Upgrade", "Target", "Environments", "Closed", "Created by");
        var row = cut.Find(".upa-table tbody tr");
        row.QuerySelector(".upa-name")!.GetAttribute("href").Should().Be($"/upgrades/{id}");
        row.QuerySelector(".upa-failed")!.TextContent.Trim().Should().Be("1 failed");
        row.TextContent.Should().Contain("by Anna Jensen").And.NotContain("anna@example.com");
        row.QuerySelectorAll(".menu__item").Select(i => i.TextContent.Trim()).Should().Equal("Open", "Reopen");
        cut.FindAll("input[type=checkbox]").Should().BeEmpty("nothing in the archive is selected or worked on");
        cut.Find(".pager__count").TextContent.Trim().Should().Be("Showing 1 of 1 done upgrade");
        cut.Find(".filter-bar input[type=search]").GetAttribute("placeholder").Should().Be("Search by name or target version...");
    }

    [Fact]
    public async Task An_empty_archive_explains_how_an_upgrade_gets_there_and_offers_no_action()
    {
        var cut = Render("archive");

        cut.WaitForAssertion(() => cut.Find(".empty-state__title").TextContent.Should().Be("No upgrade has been marked done yet"));
        cut.FindAll(".empty-state__action").Should().BeEmpty();
        // The head keeps New upgrade: only the Open view's empty state carries it itself.
        cut.Find(".page-head__actions .btn--primary").TextContent.Trim().Should().Be("New upgrade");
    }

    [Fact]
    public async Task The_archive_search_matches_the_target_version()
    {
        var (projectId, envs) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0", "27.5.1.0");
        await SeedUpgradeAsync("August wave", projectId, [envs[0]], closed: true, target: "28.2");
        await SeedUpgradeAsync("July wave", projectId, [envs[1]], closed: true, target: "28.1");

        var cut = Render("archive");
        cut.WaitForAssertion(() => cut.FindAll(".upa-table tbody tr").Should().HaveCount(2));

        cut.Find(".filter-bar input[type=search]").Input("28.1");

        cut.WaitForAssertion(() => cut.FindAll(".upa-table .upa-name").Select(a => a.TextContent.Trim())
            .Should().Equal("July wave"));
    }

    [Fact]
    public async Task A_refused_reopen_is_a_notice_naming_the_other_upgrade_not_an_exception()
    {
        var (projectId, envs) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0");
        await SeedUpgradeAsync("September wave", projectId, envs, closed: true);
        await SeedUpgradeAsync("October wave", projectId, envs);

        var cut = Render("archive");
        cut.WaitForAssertion(() => cut.FindAll(".upa-table tbody tr").Should().HaveCount(1));

        cut.FindAll(".upa-table .menu__item").Single(i => i.TextContent.Trim() == "Reopen").Click();

        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find(".alert.alert--warn");
            alert.TextContent.Should().Contain("\"October wave\"");
        });
        cut.FindAll(".upa-table tbody tr").Should().HaveCount(1, "the refused upgrade stays in the archive");
    }

    // ── New upgrade ─────────────────────────────────────────────────────

    [Fact]
    public async Task New_upgrade_needs_a_name_and_a_whole_slot_then_opens_the_upgrade_it_made()
    {
        await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0");
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        var cut = Render("archive");
        cut.WaitForAssertion(() => cut.Find(".page-head__actions .btn--primary").Click());

        cut.WaitForAssertion(() => cut.Find("#nu-title").TextContent.Should().Be("New upgrade"));
        // The version the fleet is offered is picked already; the name is not.
        cut.FindAll("#nu-target option").Select(o => o.TextContent.Trim()).Should().Equal("28.5", "Another version...");
        cut.Find("#nu-name").HasAttribute("required").Should().BeTrue();
        cut.Find("#nu-name").GetAttribute("maxlength").Should().Be(EnvironmentUpgradeService.NameMaxLength.ToString());

        ClickCreate(cut);
        cut.WaitForAssertion(() => cut.Find(".field-error").TextContent.Should().Contain("Give the upgrade a name"));

        cut.Find("#nu-name").Input("28.5 in November 2026");
        cut.Find("input[type=date]").Input("2026-11-12");
        ClickCreate(cut);
        cut.WaitForAssertion(() => cut.Find(".field-error").TextContent.Should().Contain("Pick both a date and a time"));

        cut.Find("input[type=time]").Input("20:00");
        ClickCreate(cut);

        cut.WaitForAssertion(() => nav.Uri.Should().MatchRegex(@"/upgrades/\d+$"));
        await using var ctx = _db.NewContext();
        var made = await ctx.OeEnvironmentUpgrades.SingleAsync();
        made.Name.Should().Be("28.5 in November 2026");
        made.TargetVersion.Should().Be("28.5");
        made.PlannedAt.Should().NotBeNull();
        nav.Uri.Should().EndWith($"/upgrades/{made.Id}");
    }

    [Fact]
    public async Task New_upgrade_takes_a_version_nobody_is_offered_yet_and_says_back_a_badly_written_one()
    {
        await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0");
        var cut = Render("archive");
        cut.WaitForAssertion(() => cut.Find(".page-head__actions .btn--primary").Click());
        cut.WaitForAssertion(() => cut.Find("#nu-target").Change("__other"));

        cut.WaitForAssertion(() => cut.Find("#nu-target-other").GetAttribute("pattern").Should().Be(@"\d{1,4}\.\d{1,4}"));
        cut.Find("#nu-name").Input("Next year");
        cut.Find("#nu-target-other").Input("29");
        ClickCreate(cut);

        cut.WaitForAssertion(() => cut.Find(".field-error").TextContent.Should().Contain("major.minor"));
    }

    /// <summary>
    /// New upgrade clicked while the first load's fleet read is still out: the click reads
    /// the offered versions itself, and the dialog must open with the newest one picked
    /// even though the page has not redrawn with them yet (8006eda2).
    /// </summary>
    [Fact]
    public async Task New_upgrade_clicked_before_the_first_fleet_read_lands_still_picks_the_offered_version()
    {
        await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0");
        var gate = new FirstFleetReadGate();
        _ctx.Services.ConfigureDbContext<ALDevToolbox.Data.AppDbContext>(opts => opts.AddInterceptors(gate));
        // The time zone and the access check are read once per circuit. With both in
        // hand the page first yields on the upgrades read, after it knows its view, so
        // its first draw carries the head's New upgrade while the load goes on.
        await _ctx.Services.GetRequiredService<ALDevToolbox.Services.Organizations.DisplayTimeZone>().EnsureLoadedAsync();
        await _ctx.Services.GetRequiredService<ProjectAccess>().GetSnapshotAsync();
        try
        {
            var cut = Render("archive");
            await gate.Held.WaitAsync(TimeSpan.FromSeconds(30));

            cut.WaitForAssertion(() => cut.Find(".page-head__actions .btn--primary").Click());

            cut.WaitForAssertion(() => cut.Find("#nu-title").TextContent.Should().Be("New upgrade"));
            cut.WaitForAssertion(() => cut.FindAll("#nu-target option[selected]")
                .Should().ContainSingle().Which.TextContent.Trim().Should().Be("28.5"));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Holds the first fleet read until released; every later one runs straight through.</summary>
    private sealed class FirstFleetReadGate : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seen;

        public Task Held => _held.Task;

        public void Release() => _released.TrySetResult();

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            // Only the fleet read filters out environments Business Central stopped reporting.
            if (command.CommandText.Contains("missing_since", StringComparison.Ordinal)
                && Interlocked.Increment(ref _seen) == 1)
            {
                _held.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    /// <summary>Picks an upgrade's radio in the Add dialog.</summary>
    private static void PickUpgrade(IRenderedComponent<UpgradesPage> cut, string name) =>
        cut.FindAll(".atu-choice").Single(c => c.TextContent.Contains(name)).QuerySelector("input")!.Change(true);

    private static void ClickCreate(IRenderedComponent<UpgradesPage> cut) =>
        cut.FindAll(".confirm-dialog__actions .btn--primary").Single().Click();

    // ── Add to upgrade ──────────────────────────────────────────────────

    [Fact]
    public async Task Add_to_upgrade_leaves_out_what_another_open_upgrade_holds_and_counts_only_the_rest()
    {
        var (projectId, envs) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0", "27.5.1.0");
        await SeedUpgradeAsync("28.5 in October", projectId, [envs[0]], plannedAt: DateTime.UtcNow.AddDays(2));
        var target = await SeedUpgradeAsync("28.5 in November", projectId, [], plannedAt: DateTime.UtcNow.AddDays(20));

        var cut = Render("fleet");
        // The Fleet view draws its rows, then reads the planned upgrades for the tabs and
        // redraws; tick only once all three counts are in, so no handler is retired under us
        // and the dialog's own read does not overlap the tabs' read on the page's context.
        cut.WaitForAssertion(() => cut.FindAll(".pill-tab__count").Should().HaveCount(3));
        cut.Find("thead .data-table__col-check input").Change(true);
        cut.WaitForAssertion(() => cut.FindAll(".upg-scroll tbody tr.is-selected").Should().HaveCount(2));
        // Once, not inside the wait: a retried click opens the dialog twice, and the second
        // open can land on a handler the first one's redraw retired.
        cut.FindAll(".cmdbar button").Single(b => b.TextContent.Trim() == "Add to upgrade...").Click();

        cut.WaitForAssertion(() => cut.Find("#atu-title").TextContent.Should().Be("Add 2 environments to an upgrade"));
        cut.WaitForAssertion(() => cut.FindAll(".atu-choice__name").Select(n => n.TextContent.Trim()).Should()
            .Equal("28.5 in October", "28.5 in November", "New upgrade"));

        PickUpgrade(cut, "28.5 in November");
        cut.WaitForAssertion(() =>
        {
            var left = cut.FindAll(".atu-env--out");
            left.Should().ContainSingle();
            left[0].TextContent.Should().Contain("CRONUS Denmark - Production").And.Contain("On 28.5 in October. Left out.");
        });
        cut.Find(".confirm-dialog__actions .btn--primary").TextContent.Trim().Should().Be("Add 1 of 2");
        cut.FindAll(".atu-none").Should().BeEmpty("one of them can still go on");
        cut.Find(".confirm-dialog__actions .btn--primary").Click();

        cut.WaitForAssertion(() => cut.Find(".alert").TextContent.Should().Contain(
            "Added 1 environment to \"28.5 in November\". 1 left out: already on another open upgrade."));
        cut.Find(".alert a").GetAttribute("href").Should().Be($"/upgrades/{target}");
        await using var ctx = _db.NewContext();
        (await ctx.OeEnvironmentUpgradeLines.CountAsync(l => l.UpgradeId == target)).Should().Be(1);
    }

    [Fact]
    public async Task With_nothing_left_to_add_the_dialog_says_why_instead_of_only_greying_the_button()
    {
        var (projectId, envs) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0");
        await SeedUpgradeAsync("28.5 in October", projectId, envs);
        await SeedUpgradeAsync("28.5 in November", projectId, []);

        var cut = Render("fleet");
        // The Fleet view draws its rows, then reads the planned upgrades for the tabs and
        // redraws; tick only once all three counts are in, so no handler is retired under us.
        cut.WaitForAssertion(() => cut.FindAll(".pill-tab__count").Should().HaveCount(3));
        cut.Find("tbody .data-table__col-check input").Change(true);
        cut.WaitForAssertion(() => cut.Find(".upg-scroll tbody tr").ClassList.Should().Contain("is-selected"));
        // Once, not inside the wait: a retried click opens the dialog twice, and the second
        // open can land on a handler the first one's redraw retired.
        cut.FindAll(".cmdbar button").Single(b => b.TextContent.Trim() == "Add to upgrade...").Click();
        cut.WaitForAssertion(() => cut.Find("#atu-title").Should().NotBeNull());

        // Neither upgrade has a slot still to come, so nothing is picked and the confirm waits.
        cut.WaitForAssertion(() => cut.Find("#atu-title").Should().NotBeNull());
        cut.FindAll(".atu-choice input:checked").Should().BeEmpty();
        cut.Find(".confirm-dialog__actions .btn--primary").HasAttribute("disabled").Should().BeTrue();

        PickUpgrade(cut, "28.5 in November");
        cut.WaitForAssertion(() => cut.Find(".atu-none").TextContent.Should().Contain("each of these is on another open upgrade"));
        cut.Find(".confirm-dialog__actions .btn--primary").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".atu-env__why a").TextContent.Should().Be("28.5 in October", "the upgrade to take it off is one click away");
    }

    /// <summary>
    /// One already on the chosen upgrade is not "left out: on another open upgrade" - it is
    /// on this one. It is not counted in the confirm, is not greyed as left out, and the
    /// notice says it was on it already, from the service's own answer.
    /// </summary>
    [Fact]
    public async Task One_already_on_the_chosen_upgrade_is_counted_apart_from_the_left_out()
    {
        var (projectId, envs) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0", "27.5.1.0");
        var tonight = await SeedUpgradeAsync("Tonight", projectId, [envs[0]], plannedAt: DateTime.UtcNow.AddHours(3));

        var cut = Render("fleet");
        // The Fleet view draws its rows, then reads the planned upgrades for the tabs and
        // redraws; tick only once all three counts are in, so no handler is retired under us.
        cut.WaitForAssertion(() => cut.FindAll(".pill-tab__count").Should().HaveCount(3));
        cut.Find("thead .data-table__col-check input").Change(true);
        cut.WaitForAssertion(() => cut.FindAll(".upg-scroll tbody tr.is-selected").Should().HaveCount(2));
        // Once, not inside the wait: a retried click opens the dialog twice, and the second
        // open can land on a handler the first one's redraw retired.
        cut.FindAll(".cmdbar button").Single(b => b.TextContent.Trim() == "Add to upgrade...").Click();
        cut.WaitForAssertion(() => cut.Find("#atu-title").Should().NotBeNull());

        cut.WaitForAssertion(() => cut.Find(".confirm-dialog__actions .btn--primary").TextContent.Trim().Should().Be("Add 1 of 2"));
        cut.FindAll(".atu-env--out").Should().BeEmpty("nothing here is on another upgrade");
        cut.Find(".atu-envs").TextContent.Should().Contain("Already on this upgrade.");
        cut.Find(".confirm-dialog__actions .btn--primary").Click();

        cut.WaitForAssertion(() => cut.Find(".alert").TextContent.Should().Contain(
            "Added 1 environment to \"Tonight\". 1 was on it already."));
        cut.Find(".alert").TextContent.Should().NotContain("left out");
        await using var ctx = _db.NewContext();
        (await ctx.OeEnvironmentUpgradeLines.CountAsync(l => l.UpgradeId == tonight)).Should().Be(2);
    }

    [Fact]
    public async Task With_every_ticked_one_on_the_chosen_upgrade_the_dialog_names_it()
    {
        var (projectId, envs) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0");
        await SeedUpgradeAsync("Tonight", projectId, envs, plannedAt: DateTime.UtcNow.AddHours(3));

        var cut = Render("fleet");
        // The Fleet view draws its rows, then reads the planned upgrades for the tabs and
        // redraws; tick only once all three counts are in, so no handler is retired under us.
        cut.WaitForAssertion(() => cut.FindAll(".pill-tab__count").Should().HaveCount(3));
        cut.Find("tbody .data-table__col-check input").Change(true);
        cut.WaitForAssertion(() => cut.Find(".upg-scroll tbody tr").ClassList.Should().Contain("is-selected"));
        // Once, not inside the wait: a retried click opens the dialog twice, and the second
        // open can land on a handler the first one's redraw retired.
        cut.FindAll(".cmdbar button").Single(b => b.TextContent.Trim() == "Add to upgrade...").Click();
        cut.WaitForAssertion(() => cut.Find("#atu-title").Should().NotBeNull());

        cut.WaitForAssertion(() => cut.Find(".atu-none").TextContent.Should().Be("Nothing to add: it is on \"Tonight\" already."));
        cut.Find(".confirm-dialog__actions .btn--primary").HasAttribute("disabled").Should().BeTrue();
    }

    /// <summary>
    /// The Open view leads with last night's wave, which is still being checked; the dialog
    /// starts on the slot that comes next instead, since that is where tonight's customers go.
    /// </summary>
    [Fact]
    public async Task Add_to_upgrade_starts_on_the_next_slot_not_last_nights()
    {
        var (projectId, _) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0");
        await SeedUpgradeAsync("Last night", projectId, [], plannedAt: DateTime.UtcNow.AddHours(-12));
        await SeedUpgradeAsync("Next week", projectId, [], plannedAt: DateTime.UtcNow.AddDays(7));
        await SeedUpgradeAsync("Tomorrow", projectId, [], plannedAt: DateTime.UtcNow.AddDays(1));

        var cut = Render("fleet");
        // The Fleet view draws its rows, then reads the planned upgrades for the tabs and
        // redraws; tick only once all three counts are in, so no handler is retired under us.
        cut.WaitForAssertion(() => cut.FindAll(".pill-tab__count").Should().HaveCount(3));
        cut.Find("tbody .data-table__col-check input").Change(true);
        cut.WaitForAssertion(() => cut.Find(".upg-scroll tbody tr").ClassList.Should().Contain("is-selected"));
        // Once, not inside the wait: a retried click opens the dialog twice, and the second
        // open can land on a handler the first one's redraw retired.
        cut.FindAll(".cmdbar button").Single(b => b.TextContent.Trim() == "Add to upgrade...").Click();
        cut.WaitForAssertion(() => cut.Find("#atu-title").Should().NotBeNull());

        cut.WaitForAssertion(() => cut.Find(".atu-choice.is-picked .atu-choice__name").TextContent.Trim().Should().Be("Tomorrow"));
    }

    // ── The switch on a live page ───────────────────────────────────────

    /// <summary>
    /// The Fleet view's watch belongs to it: following another tab stops it, and coming back
    /// reads the fleet again - here, a rename made while away - and joins the watch afresh.
    /// </summary>
    [Fact]
    public async Task Leaving_the_Fleet_view_stops_its_watch_and_coming_back_reads_the_fleet_again()
    {
        var (_, envs) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0");
        await using (var ctx = _db.NewContext())
        {
            var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == envs[0]);
            env.Status = "Upgrading";
            await ctx.SaveChangesAsync();
        }
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();

        var cut = Render("fleet");
        cut.WaitForAssertion(() => cut.Instance.WatchedEnvironmentIds.Should().Equal(envs[0]));

        nav.NavigateTo(nav.GetUriWithQueryParameter("view", "open"));
        cut.WaitForAssertion(() => cut.Find(".empty-state__title").TextContent.Should().Be("Nothing planned yet"));
        cut.Instance.WatchedEnvironmentIds.Should().BeEmpty("the watch stops with the view it belongs to");

        await using (var ctx = _db.NewContext())
        {
            var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == envs[0]);
            env.Name = "PROD-DK";
            await ctx.SaveChangesAsync();
        }
        nav.NavigateTo(nav.GetUriWithQueryParameter("view", "fleet"));

        cut.WaitForAssertion(() =>
        {
            cut.Find(".upg-scroll tbody tr .upg-env a").TextContent.Should().Be("PROD-DK");
            cut.Instance.WatchedEnvironmentIds.Should().Equal(envs[0]);
        });
    }

    // ── Open filters, Archive pages, the subtitle ───────────────────────

    [Fact]
    public async Task The_status_filter_narrows_the_open_list()
    {
        var (projectId, envs) = await SeedSolutionAsync("CRONUS Denmark", "28.5.1.0");
        await SeedUpgradeAsync("Nothing on it", projectId, []);
        await SeedUpgradeAsync("All updated", projectId, envs);

        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".upl-table tbody tr").Should().HaveCount(2));

        cut.Find(".filter-bar select").Change("Updated");

        cut.WaitForAssertion(() => cut.FindAll(".upl-table .upl-name").Select(a => a.TextContent.Trim())
            .Should().Equal("All updated"));
        cut.Find(".filter-bar select").Change("Planned");
        cut.WaitForAssertion(() => cut.FindAll(".upl-table .upl-name").Select(a => a.TextContent.Trim())
            .Should().Equal("Nothing on it"));
    }

    [Fact]
    public async Task The_archive_pages_twenty_at_a_time_and_says_which_it_shows()
    {
        var (projectId, _) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0");
        for (var i = 1; i <= 22; i++)
        {
            await SeedUpgradeAsync($"Wave {i:00}", projectId, [], closed: true);
        }

        var cut = Render("archive");

        cut.WaitForAssertion(() => cut.FindAll(".upa-table tbody tr").Should().HaveCount(20));
        cut.Find(".pager__count").TextContent.Trim().Should().Be("Showing 1-20 of 22 done upgrades");
        Tabs(cut).Should().Contain("Archive22");
        var buttons = cut.FindAll(".pager__buttons .btn");
        buttons[0].HasAttribute("disabled").Should().BeTrue("there is nothing before the first page");

        buttons[1].Click();

        cut.WaitForAssertion(() => cut.FindAll(".upa-table tbody tr").Should().HaveCount(2));
        cut.Find(".pager__count").TextContent.Trim().Should().Be("Showing 21-22 of 22 done upgrades");
        cut.FindAll(".pager__buttons .btn")[1].HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task The_subtitle_counts_what_is_booked_for_a_slot_still_to_come_today()
    {
        var (projectId, envs) = await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0", "27.5.1.0");
        // The last moment of today in the display zone (UTC here), so it is later today
        // whatever time the test runs.
        var slot = DateTime.UtcNow.Date.AddDays(1).AddSeconds(-1);
        var id = await SeedUpgradeAsync("Tonight", projectId, envs, plannedAt: slot);
        await SeedActionAsync(id, projectId, envs[0], UpgradeActionStatus.Pending, executeAfter: slot);

        var cut = Render();

        cut.WaitForAssertion(() => cut.Find(".page-head__sub").TextContent.Should().Be("1 open, 1 environment booked for tonight."));
    }

    [Fact]
    public async Task Add_to_upgrade_can_make_the_upgrade_it_adds_to()
    {
        await SeedSolutionAsync("CRONUS Denmark", "27.5.1.0");

        var cut = Render("fleet");
        // The Fleet view draws its rows, then reads the planned upgrades for the tabs and
        // redraws; tick only once all three counts are in, so no handler is retired under us.
        cut.WaitForAssertion(() => cut.FindAll(".pill-tab__count").Should().HaveCount(3));
        cut.Find("tbody .data-table__col-check input").Change(true);
        cut.WaitForAssertion(() => cut.Find(".upg-scroll tbody tr").ClassList.Should().Contain("is-selected"));
        // Once, not inside the wait: a retried click opens the dialog twice, and the second
        // open can land on a handler the first one's redraw retired.
        cut.FindAll(".cmdbar button").Single(b => b.TextContent.Trim() == "Add to upgrade...").Click();
        cut.WaitForAssertion(() => cut.Find("#atu-title").Should().NotBeNull());

        // With nothing open, a new one is the only choice, and its fields are open.
        cut.WaitForAssertion(() => cut.Find("#atu-name").Should().NotBeNull());
        cut.Find(".confirm-dialog__actions .btn--primary").TextContent.Trim().Should().Be("Add 1 environment");
        cut.Find(".confirm-dialog__actions .btn--primary").Click();
        cut.WaitForAssertion(() => cut.Find(".field-error").TextContent.Should().Contain("Give the upgrade a name"));

        cut.Find("#atu-name").Input("28.5 for CRONUS in December");
        cut.Find(".confirm-dialog__actions .btn--primary").Click();

        cut.WaitForAssertion(() => cut.Find(".alert").TextContent.Should().Contain(
            "Created \"28.5 for CRONUS in December\" and added 1 environment to it."));
        await using var ctx = _db.NewContext();
        (await ctx.OeEnvironmentUpgrades.SingleAsync()).Name.Should().Be("28.5 for CRONUS in December");
        (await ctx.OeEnvironmentUpgradeLines.CountAsync()).Should().Be(1);
    }
}
