using ALDevToolbox.Components.Pages.Upgrades;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using ALDevToolbox.Tests.ObjectExplorer;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// An upgrade's own page (#984, sub-issues D and E). The named user is a member of the
/// upgrade team the morning after the wave, going down eight customers and ticking off
/// each one they have checked. These pin what the sheet (PageUpgrade.dc.html) and the
/// engine's rules make of that: the four states, the one primary that follows the wave,
/// when a line can be ticked, the Mine switch, a selection that outlives the search, the
/// Mark done confirm, and the leftovers waiting for the upgrade to be done.
/// </summary>
public sealed class UpgradeDetailTests : IAsyncDisposable
{
    private readonly UpgradeActionTestFixture _f = new();
    private readonly BunitContext _ctx = new();

    public UpgradeDetailTests()
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
        _ctx.Services.AddScoped<ProjectCustomerInfoService>();
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
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
    }

    public async ValueTask DisposeAsync()
    {
        // The renderer's own dispose walks the page on the dispatcher, behind whatever
        // load it is in the middle of; bunit's DisposeComponents can miss it (#924).
        await _ctx.Renderer.DisposeAsync();
        _f.Db.WaitForQueriesToSettle();
        await _ctx.DisposeAsync();
        _f.Dispose();
    }

    // ── The four states ─────────────────────────────────────────────────

    [Fact]
    public async Task While_it_loads_the_page_shows_the_skeleton_and_no_head()
    {
        await _f.SeedCustomerAsync();
        var id = await UpgradeAsync();

        var cut = Render(id);

        // Straight after the first render the page is still reading the upgrade: the frame's
        // skeleton head, the page's skeleton rows, no heading yet.
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Loading the upgrade and each environment...");
            cut.Markup.Should().NotContain("from Business Central", "the page reads the mirror, not Business Central");
            cut.FindAll("h1").Should().BeEmpty();
            cut.FindAll(".detail-head .skeleton").Should().NotBeEmpty();
            cut.Find(".page-head__crumbs a").GetAttribute("href").Should().Be("/upgrades");
        });
        cut.WaitForAssertion(() => cut.Find("h1.detail-head__title").TextContent.Should().Be("28.5 in November 2026"));
    }

    [Fact]
    public async Task An_upgrade_that_is_not_there_says_so_and_points_at_the_open_list_and_the_archive()
    {
        // A customer, for the team that carries the environment-updates grant.
        await _f.SeedCustomerAsync();
        var cut = Render(424242);

        cut.WaitForAssertion(() =>
        {
            cut.Find("h1").TextContent.Should().Be("This upgrade does not exist");
            var links = cut.FindAll(".empty-state__action a");
            links.Select(a => a.TextContent.Trim()).Should().Equal("Open upgrades", "Archive");
            links[0].GetAttribute("href").Should().Be("/upgrades");
            links[1].GetAttribute("href").Should().Be(UpgradeDetail.ArchiveHref);
        });
    }

    [Fact]
    public async Task A_fresh_upgrade_leads_with_Add_environments_and_holds_the_rest_until_there_are_lines()
    {
        await _f.SeedCustomerAsync();
        var id = await UpgradeAsync();

        var cut = Render(id);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".detail-head .status-pill").TextContent.Trim().Should().Be("Planned");
            cut.Find(".empty-state__title").TextContent.Should().Be("No environments yet");
            // The empty state holds the one primary; the head's copy of the same command
            // steps down to outline, so there is still one primary on the page.
            cut.FindAll(".btn--primary").Should().ContainSingle()
                .Which.TextContent.Trim().Should().Be("Add environments...");
            cut.Find(".empty-state__action .btn--primary").Should().NotBeNull();
            HeadButton(cut, "Add environments...").HasAttribute("disabled").Should().BeFalse();
            foreach (var label in new[] { "Move dates...", "Start update...", "Change the next version..." })
            {
                var button = HeadButton(cut, label);
                button.HasAttribute("disabled").Should().BeTrue(label);
                button.GetAttribute("title").Should().Be("Add environments first");
            }
            cut.FindAll(".upd-table").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task An_upgrade_under_way_leads_with_Start_update_over_the_lines_not_sent_yet()
    {
        var (_, updated) = await _f.SeedCustomerAsync("CRONUS Danmark");
        var (_, waiting) = await _f.SeedCustomerAsync("CRONUS UK");
        await SetVersionAsync(updated, "28.5.1.0");
        var id = await UpgradeAsync(updated, waiting);

        var cut = Render(id);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".detail-head .status-pill").TextContent.Trim().Should().Be("In progress");
            var primary = HeadPrimary(cut);
            primary.TextContent.Trim().Should().Be("Start update...");
            primary.GetAttribute("title").Should().Be("Starts the update on the environments not started yet: 1 environment");
            Meta(cut, "Progress").Should().Be("1 of 2 updated, 0 checked");
            Meta(cut, "Target").Should().Be("28.5");
            Meta(cut, "Environments").Should().Be("2");
            cut.FindAll(".upd-table tbody tr").Should().HaveCount(2);
        });
    }

    [Fact]
    public async Task Once_every_line_has_an_answer_Mark_done_is_the_primary()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        await SetVersionAsync(envId, "28.5.1.0");
        var id = await UpgradeAsync(envId);

        var cut = Render(id);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".detail-head .status-pill").TextContent.Trim().Should().Be("Updated");
            HeadPrimary(cut).TextContent.Trim().Should().Be("Mark done...");
            cut.FindAll(".page-head__actions .btn--primary").Should().ContainSingle("one primary on the page");
            OverflowItems(cut).Select(i => i.TextContent.Trim()).Should().NotContain("Mark done...",
                "the primary already offers it");
        });
    }

    [Fact]
    public async Task A_done_upgrade_is_a_read_only_record_with_Reopen_and_the_leftovers_in_the_overflow()
    {
        var (_, checkedEnv) = await _f.SeedCustomerAsync("CRONUS Danmark");
        var (_, failedEnv) = await _f.SeedCustomerAsync("CRONUS UK");
        await SetVersionAsync(checkedEnv, "28.5.1.0");
        var id = await UpgradeAsync(checkedEnv, failedEnv);
        await using (var ctx = _f.Db.NewContext())
        {
            var svc = _f.Upgrades(ctx);
            var detail = await svc.GetAsync(id);
            await svc.SetCheckedAsync(detail!.Lines.Single(l => l.Environment.EnvironmentId == checkedEnv).LineId, true, "Posting OK");
            await svc.CloseAsync(id);
        }

        var cut = Render(id);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".detail-head .status-pill").TextContent.Trim().Should().Be("Done");
            cut.FindAll(".page-head__actions > button").Should().BeEmpty("a done upgrade has no ribbon");
            OverflowItems(cut).Select(i => i.TextContent.Trim()).Should()
                .Equal("Reopen", "New upgrade from the leftovers");
            cut.FindAll(".upd-table input[type=checkbox]").Should().BeEmpty("the controls are removed, not dimmed");
            cut.FindAll(".upd-table .ra").Should().BeEmpty();
            cut.FindAll(".upd-table .upd-mark.upd-ok").Should().ContainSingle();
            cut.FindAll(".upd-table .upd-mark.upd-bad").Should().ContainSingle();
            cut.Find(".upd-table").TextContent.Should().Contain("Posting OK", "the note survives on a done upgrade");
            Meta(cut, "Closed").Should().Contain("Anna Jensen");
        });
    }

    // ── Lines ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_line_can_be_ticked_once_it_is_updated_and_not_before()
    {
        var (_, updated) = await _f.SeedCustomerAsync("CRONUS Danmark");
        var (_, waiting) = await _f.SeedCustomerAsync("CRONUS UK");
        await SetVersionAsync(updated, "28.5.1.0");
        var id = await UpgradeAsync(updated, waiting);

        var cut = Render(id);

        cut.WaitForAssertion(() =>
        {
            var before = CheckBox(cut, "CRONUS UK");
            before.HasAttribute("disabled").Should().BeTrue("a line that has not been updated has nothing to check yet");
            Row(cut, "CRONUS UK").TextContent.Should().Contain("After the update");
            CheckBox(cut, "CRONUS Danmark").HasAttribute("disabled").Should().BeFalse();
            Row(cut, "CRONUS Danmark").TextContent.Should().Contain("Tick when checked");
        });

        cut.WaitForAssertion(() => CheckBox(cut, "CRONUS Danmark").Change(true));

        cut.WaitForAssertion(() =>
        {
            var row = Row(cut, "CRONUS Danmark");
            row.QuerySelector(".status-pill")!.TextContent.Trim().Should().Be("Checked");
            row.TextContent.Should().Contain("Anna Jensen");
            row.TextContent.Should().Contain("Add a note");
            Meta(cut, "Progress").Should().Be("1 of 2 updated, 1 checked");
        });

        await using var read = _f.Db.NewContext();
        (await read.OeEnvironmentUpgradeLines.SingleAsync(l => l.EnvironmentId == updated)).CheckedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Mine_shows_only_the_lines_assigned_to_me()
    {
        var (_, mine) = await _f.SeedCustomerAsync("CRONUS Danmark");
        var (_, theirs) = await _f.SeedCustomerAsync("CRONUS UK");
        var id = await UpgradeAsync(mine, theirs);
        await using (var ctx = _f.Db.NewContext())
        {
            var svc = _f.Upgrades(ctx);
            var detail = await svc.GetAsync(id);
            await svc.AssignAsync(detail!.Lines.Single(l => l.Environment.EnvironmentId == mine).LineId, UpgradeActionTestFixture.FlagUserId);
            await svc.AssignAsync(detail.Lines.Single(l => l.Environment.EnvironmentId == theirs).LineId, UpgradeActionTestFixture.PlainTeamUserId);
        }

        var cut = Render(id);
        cut.WaitForAssertion(() => cut.FindAll(".upd-table tbody tr").Should().HaveCount(2));

        cut.WaitForAssertion(() => cut.Find(".upd-bar .switch input").Change(true));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".upd-table tbody tr");
            rows.Should().ContainSingle();
            rows[0].TextContent.Should().Contain("CRONUS Danmark").And.Contain("Anna Jensen");
        });
    }

    [Fact]
    public async Task A_tick_outlives_a_search_that_hides_it()
    {
        var (_, first) = await _f.SeedCustomerAsync("CRONUS Danmark");
        var (_, second) = await _f.SeedCustomerAsync("CRONUS UK");
        var id = await UpgradeAsync(first, second);

        var cut = Render(id);
        cut.WaitForAssertion(() => PickBox(cut, "CRONUS Danmark").Change(true));
        cut.WaitForAssertion(() => cut.Find(".upd-bar__count").TextContent.Should().Be("1 selected"));

        cut.WaitForAssertion(() => cut.Find(".upd-search input").Input("UK"));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".upd-table tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("CRONUS UK");
            cut.Find(".upd-bar__count").TextContent.Should().Be("1 selected, 0 shown");
        });

        cut.WaitForAssertion(() => cut.Find(".upd-search input").Input(""));

        cut.WaitForAssertion(() =>
            PickBox(cut, "CRONUS Danmark").HasAttribute("checked").Should().BeTrue("searching never unticks a line"));
    }

    [Fact]
    public async Task Mark_done_names_the_lines_still_unchecked_and_says_how_many()
    {
        var (_, checkedEnv) = await _f.SeedCustomerAsync("CRONUS Danmark");
        var (_, updated) = await _f.SeedCustomerAsync("CRONUS UK");
        var (_, waiting) = await _f.SeedCustomerAsync("CRONUS Sverige");
        await SetVersionAsync(checkedEnv, "28.5.1.0");
        await SetVersionAsync(updated, "28.5.1.0");
        var id = await UpgradeAsync(checkedEnv, updated, waiting);
        await using (var ctx = _f.Db.NewContext())
        {
            var svc = _f.Upgrades(ctx);
            var detail = await svc.GetAsync(id);
            await svc.SetCheckedAsync(detail!.Lines.Single(l => l.Environment.EnvironmentId == checkedEnv).LineId, true, null);
        }

        var cut = Render(id);
        cut.WaitForAssertion(() => OverflowItems(cut).Single(i => i.TextContent.Trim() == "Mark done...").Click());

        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__title").TextContent.Should().Be("2 of 3 are not checked yet. Mark done anyway?");
            var named = cut.FindAll(".upd-unchecked__row");
            named.Select(r => r.QuerySelector(".cell-stack__main")!.TextContent).Should().BeEquivalentTo("CRONUS UK", "CRONUS Sverige");
            named.Select(r => r.QuerySelector(".status-pill")!.TextContent.Trim()).Should().BeEquivalentTo("Updated", "Planned");
            cut.FindAll(".upd-unchecked__head").Select(h => h.TextContent.Trim()).Should()
                .Equal("Failed or never started (1)", "The rest, not checked yet (1)");
            cut.Find(".upd-unchecked__head + .upd-unchecked .cell-stack__main").TextContent.Should().Be("CRONUS Sverige",
                "the leftovers lead, under their own heading");
            cut.Find(".upd-unchecked__after").TextContent.Should().Contain("the one that failed or never started");
        });

        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions .btn").Last().Click());

        cut.WaitForAssertion(() => cut.Find(".detail-head .status-pill").TextContent.Trim().Should().Be("Done"));
        await using var read = _f.Db.NewContext();
        (await read.OeEnvironmentUpgrades.SingleAsync(u => u.Id == id)).ClosedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task On_an_open_upgrade_the_leftovers_wait_for_it_to_be_marked_done()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await UpgradeAsync(envId);

        var cut = Render(id);

        cut.WaitForAssertion(() =>
        {
            var leftovers = OverflowItems(cut).Single(i => i.TextContent.Trim() == "New upgrade from the leftovers");
            leftovers.HasAttribute("disabled").Should().BeTrue();
            leftovers.GetAttribute("title").Should().StartWith("Mark this upgrade done first.");
            OverflowItems(cut).Select(i => i.TextContent.Trim()).Should().NotContain("Reopen",
                "only a done upgrade can be reopened, and the page hides what it cannot do rather than dimming it");
        });
    }

    [Fact]
    public async Task A_refusal_from_the_engine_is_shown_as_an_alert_not_an_exception()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await UpgradeAsync(envId);

        var cut = Render(id);
        cut.WaitForAssertion(() => cut.FindAll(".upd-table tbody tr").Should().HaveCount(1));

        // Somebody else marks it done while this page is open.
        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).CloseAsync(id);
        }

        cut.WaitForAssertion(() => OverflowItems(cut).Single(i => i.TextContent.Trim() == "Mark done...").Click());
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions .btn").Last().Click());

        cut.WaitForAssertion(() =>
            cut.Find(".upd-alerts .alert--danger").TextContent.Should().Contain("This upgrade is already marked done."));
    }

    [Fact]
    public async Task The_contact_column_calls_the_customer()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        await using (var ctx = _f.Db.NewContext())
        {
            ctx.OeProjectContacts.Add(new OeProjectContact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Type = ProjectContactType.Customer,
                Name = "Anne Holm", Phone = "+45 31 22 40 18",
            });
            await ctx.SaveChangesAsync();
        }
        var id = await UpgradeAsync(envId);

        var cut = Render(id);

        cut.WaitForAssertion(() =>
        {
            var call = cut.Find(".upd-table a[href^='tel:']");
            call.GetAttribute("href").Should().Be("tel:+4531224018");
            call.TextContent.Should().Be("+45 31 22 40 18");
            Row(cut, "CRONUS").TextContent.Should().Contain("Anne Holm");
        });
    }

    [Fact]
    public async Task A_line_the_person_can_see_but_not_manage_has_a_padlock_and_no_controls()
    {
        var (_, mine) = await _f.SeedCustomerAsync("CRONUS Danmark");
        var id = await UpgradeAsync(mine);
        var theirs = await SeedUnmanagedAsync("CRONUS Norge");
        await SetVersionAsync(theirs.EnvironmentId, "28.5.1.0");
        await PutOnUpgradeAsync(id, theirs);

        var cut = Render(id);

        cut.WaitForAssertion(() =>
        {
            var row = Row(cut, "CRONUS Norge");
            row.QuerySelector("[role=img][aria-label^='Locked']").Should().NotBeNull();
            cut.FindAll("label[aria-label='Select CRONUS Norge Production']").Should().BeEmpty();
            CheckBox(cut, "CRONUS Norge").HasAttribute("disabled").Should().BeTrue(
                "the line is updated, but ticking it needs the environment-updates grant");
            row.QuerySelector(".ra").Should().BeNull("no row menu, and no people menu");
            cut.Find(".upd-legend").TextContent.Should().Contain("padlock");
            // The line this person manages keeps its controls.
            cut.FindAll("label[aria-label='Select CRONUS Danmark Production']").Should().ContainSingle();
        });
    }

    [Fact]
    public async Task Delete_waits_until_nothing_has_been_done_from_the_upgrade()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var id = await UpgradeAsync(envId);
        await ActionFromUpgradeAsync(id, projectId, envId);

        var cut = Render(id);

        cut.WaitForAssertion(() =>
        {
            var delete = OverflowItems(cut).Single(i => i.TextContent.Trim() == "Delete");
            delete.HasAttribute("disabled").Should().BeTrue();
            delete.GetAttribute("title").Should().StartWith("Something has already been done from this upgrade");
        });
    }

    [Fact]
    public async Task An_upgrade_nothing_was_done_from_can_be_deleted()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await UpgradeAsync(envId);

        var cut = Render(id);
        cut.WaitForAssertion(() => OverflowItems(cut).Single(i => i.TextContent.Trim() == "Delete").Click());
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions .btn").Last().Click());

        cut.WaitForAssertion(() =>
            _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri.Should().EndWith("/upgrades"));
        await using var read = _f.Db.NewContext();
        (await read.OeEnvironmentUpgrades.AnyAsync(u => u.Id == id)).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_waits_until_nothing_has_been_done_to_that_environment()
    {
        var (projectId, acted) = await _f.SeedCustomerAsync("CRONUS Danmark");
        var (_, untouched) = await _f.SeedCustomerAsync("CRONUS UK");
        var id = await UpgradeAsync(acted, untouched);
        await ActionFromUpgradeAsync(id, projectId, acted);

        var cut = Render(id);

        cut.WaitForAssertion(() =>
        {
            var remove = RowMenuItem(cut, "CRONUS Danmark", "Remove from upgrade");
            remove.HasAttribute("disabled").Should().BeTrue();
            remove.GetAttribute("title").Should().StartWith("Something has already been done to this environment");
            RowMenuItem(cut, "CRONUS UK", "Remove from upgrade").HasAttribute("disabled").Should().BeFalse();
        });

        cut.WaitForAssertion(() => RowMenuItem(cut, "CRONUS UK", "Remove from upgrade").Click());
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions .btn").Last().Click());

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".upd-table tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("CRONUS Danmark");
            cut.Find(".upd-alerts").TextContent.Should().Contain("Took 1 environment off the upgrade.");
        });
        await using var read = _f.Db.NewContext();
        (await read.OeEnvironmentUpgradeLines.CountAsync(l => l.UpgradeId == id)).Should().Be(1);
    }

    [Fact]
    public async Task A_done_upgrade_can_be_reopened()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await UpgradeAsync(envId);
        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).CloseAsync(id);
        }

        var cut = Render(id);
        cut.WaitForAssertion(() => OverflowItems(cut).Single(i => i.TextContent.Trim() == "Reopen").Click());

        cut.WaitForAssertion(() =>
        {
            cut.Find(".detail-head .status-pill").TextContent.Trim().Should().Be("Planned");
            cut.Find(".upd-alerts").TextContent.Should().Contain("Reopened.");
            cut.FindAll(".upd-table input[type=checkbox]").Should().NotBeEmpty("the controls are back");
        });
        await using var read = _f.Db.NewContext();
        (await read.OeEnvironmentUpgrades.SingleAsync(u => u.Id == id)).ClosedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_note_added_on_the_page_keeps_the_stamp_of_whoever_ticked_the_line()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        await SetVersionAsync(envId, "28.5.1.0");
        var id = await UpgradeAsync(envId);
        // A colleague on the update team did the check.
        await using (var ctx = _f.Db.NewContext())
        {
            (await ctx.TeamMembers.SingleAsync(m => m.UserId == UpgradeActionTestFixture.PlainTeamUserId)).ManagesUpdates = true;
            await ctx.SaveChangesAsync();
        }
        _f.ActAs(UpgradeActionTestFixture.PlainTeamUserId);
        await using (var ctx = _f.Db.NewContext())
        {
            var svc = _f.Upgrades(ctx);
            await svc.SetCheckedAsync((await svc.GetAsync(id))!.Lines.Single().LineId, true);
        }
        _f.ActAs(UpgradeActionTestFixture.FlagUserId);

        var cut = Render(id);
        cut.WaitForAssertion(() => cut.Find(".upd-table .upd-note-button").Click());
        cut.WaitForAssertion(() => cut.Find(".confirm-dialog textarea").Input("reports OK"));
        cut.WaitForAssertion(() => cut.Find(".confirm-dialog button[type=submit]").Click());

        cut.WaitForAssertion(() =>
        {
            var row = Row(cut, "CRONUS");
            row.TextContent.Should().Contain("reports OK").And.Contain("colleague@example.com");
        });
        await using var read = _f.Db.NewContext();
        var line = await read.OeEnvironmentUpgradeLines.SingleAsync(l => l.UpgradeId == id);
        line.Note.Should().Be("reports OK");
        line.CheckedByUserId.Should().Be(UpgradeActionTestFixture.PlainTeamUserId, "writing a note is not doing the check");
    }

    // ── The watch and the page's writes ─────────────────────────────────

    [Fact]
    public async Task A_watch_tick_skips_while_the_page_holds_its_gate_and_reads_once_it_is_free()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var row = new UpgradeFleetRow(projectId, "CRONUS Denmark", "Europe/Copenhagen", envId, "Production", "Production",
            "Upgrading", "28.4.1.0", "28.5", "GA", "Running", null, null, null, null, CanAct: true);
        var gate = new SemaphoreSlim(1, 1);
        var afterTicks = 0;
        await using var ctx = _f.Db.NewContext();
        using var watch = new UpdateWatch(_f.Connections(ctx), NullLogger.Instance, f => f(), () => false,
            _ => row, _ => { }, _ => { afterTicks++; return Task.CompletedTask; }, gate: gate);
        watch.Begin(row, DateTime.UtcNow, seenBusy: true);

        await gate.WaitAsync();
        await watch.TickAsync();

        watch.Watching[envId].LastReadUtc.Should().Be(DateTime.MinValue, "a write of the page's own holds the context");
        afterTicks.Should().Be(0);
        watch.IsTicking.Should().BeFalse();

        gate.Release();
        await watch.TickAsync();

        afterTicks.Should().Be(1);
        gate.CurrentCount.Should().Be(1, "the tick gives the gate back, its re-read included");
        watch.IsTicking.Should().BeFalse();
    }

    // ── The picker ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_picker_locks_what_is_taken_and_fills_only_what_is_free_and_behind_the_target()
    {
        var (_, onThis) = await _f.SeedCustomerAsync("CRONUS Danmark");
        var (_, onOther) = await _f.SeedCustomerAsync("CRONUS UK");
        var (_, behind) = await _f.SeedCustomerAsync("CRONUS Sverige");
        var (_, alreadyOn) = await _f.SeedCustomerAsync("CRONUS Norge");
        await SetVersionAsync(alreadyOn, "28.5.2.0");
        var id = await UpgradeAsync(onThis);
        await using (var ctx = _f.Db.NewContext())
        {
            var svc = _f.Upgrades(ctx);
            var other = await svc.CreateAsync("28.5 in October", "28.5", null, null);
            await svc.AddLinesAsync(other, [onOther]);
        }

        var cut = Render(id);
        cut.WaitForAssertion(() => HeadButton(cut, "Add environments...").Click());

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".upk-table tbody tr").Should().HaveCount(4);
            var mine = PickerBox(cut, "CRONUS Danmark");
            mine.HasAttribute("checked").Should().BeTrue();
            mine.HasAttribute("disabled").Should().BeTrue();
            PickerRow(cut, "CRONUS Danmark").TextContent.Should().Contain("Already on this upgrade");
            var taken = PickerBox(cut, "CRONUS UK");
            taken.HasAttribute("checked").Should().BeFalse();
            taken.HasAttribute("disabled").Should().BeTrue();
            PickerRow(cut, "CRONUS UK").QuerySelector(".upk-why--other")!.TextContent.Should().Be("On 28.5 in October");
            cut.Find(".upk-foot__selected").TextContent.Should().Be("None selected");
            var add = cut.FindAll(".upk-foot .btn--primary").Single();
            add.TextContent.Trim().Should().Be("Add environments");
            add.HasAttribute("disabled").Should().BeTrue();
        });

        cut.WaitForAssertion(() => cut.FindAll(".upk-fill .btn").First(b => b.TextContent.StartsWith("Production")).Click());

        cut.WaitForAssertion(() =>
        {
            // Only the one that is free and still behind 28.5.
            PickerBox(cut, "CRONUS Sverige").HasAttribute("checked").Should().BeTrue();
            PickerBox(cut, "CRONUS Norge").HasAttribute("checked").Should().BeFalse("it is on 28.5 already");
            cut.Find(".upk-foot__selected").TextContent.Should().Be("1 selected");
            cut.FindAll(".upk-foot .btn--primary").Single().TextContent.Trim().Should().Be("Add 1 environment");
        });

        cut.WaitForAssertion(() => cut.FindAll(".upk-foot .btn--primary").Single().Click());

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".upk-dialog").Should().BeEmpty();
            cut.Find(".upd-alerts").TextContent.Should().Contain("Added 1 environment.");
            cut.FindAll(".upd-table tbody tr").Should().HaveCount(2);
        });
        await using var read = _f.Db.NewContext();
        (await read.OeEnvironmentUpgradeLines.CountAsync(l => l.UpgradeId == id)).Should().Be(2);
    }

    [Fact]
    public async Task The_picker_locks_a_solution_the_person_cannot_manage_and_the_fill_skips_it()
    {
        var (_, envId) = await _f.SeedCustomerAsync("CRONUS Danmark");
        await SeedUnmanagedAsync("CRONUS Norge");
        var id = await UpgradeAsync();

        var cut = Render(id);
        cut.WaitForAssertion(() => HeadButton(cut, "Add environments...").Click());
        cut.WaitForAssertion(() =>
        {
            PickerBox(cut, "CRONUS Norge").HasAttribute("disabled").Should().BeTrue();
            PickerRow(cut, "CRONUS Norge").QuerySelector(".upk-why")!.TextContent.Should().Be("You can't manage this solution's updates");
        });

        cut.WaitForAssertion(() => cut.FindAll(".upk-fill .btn").First(b => b.TextContent.StartsWith("Production")).Click());

        cut.WaitForAssertion(() =>
        {
            PickerBox(cut, "CRONUS Danmark").HasAttribute("checked").Should().BeTrue();
            PickerBox(cut, "CRONUS Norge").HasAttribute("checked").Should().BeFalse();
            cut.Find(".upk-foot__selected").TextContent.Should().Be("1 selected");
        });
        _ = envId;
    }

    [Fact]
    public async Task The_fill_adds_to_what_was_ticked_by_hand_and_the_search_keeps_it()
    {
        var (_, handPicked) = await _f.SeedCustomerAsync("CRONUS Danmark");
        var (_, filled) = await _f.SeedCustomerAsync("CRONUS UK");
        await using (var ctx = _f.Db.NewContext())
        {
            // A sandbox: the Production fill must not reach it.
            (await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == handPicked)).Type = "Sandbox";
            await ctx.SaveChangesAsync();
        }
        var id = await UpgradeAsync();

        var cut = Render(id);
        cut.WaitForAssertion(() => HeadButton(cut, "Add environments...").Click());
        cut.WaitForAssertion(() => PickerBox(cut, "CRONUS Danmark").Change(true));
        cut.WaitForAssertion(() => cut.Find(".upk-search input").Input("UK"));
        cut.WaitForAssertion(() => cut.FindAll(".upk-table tbody tr").Should().ContainSingle());
        cut.WaitForAssertion(() => cut.FindAll(".upk-fill .btn").First(b => b.TextContent.StartsWith("Production")).Click());

        cut.WaitForAssertion(() =>
        {
            cut.Find(".upk-foot__selected").TextContent.Should().Be("2 selected, 1 shown");
            cut.FindAll(".upk-foot .btn--primary").Single().TextContent.Trim().Should().Be("Add 2 environments");
        });
        _ = filled;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private IRenderedComponent<UpgradeDetail> Render(int id) =>
        _ctx.Render<UpgradeDetail>(p => p.Add(c => c.Id, id));

    private async Task<int> UpgradeAsync(params int[] environmentIds)
    {
        await using var ctx = _f.Db.NewContext();
        var svc = _f.Upgrades(ctx);
        var id = await svc.CreateAsync("28.5 in November 2026", "28.5", null, "Agreed with the customers on the October call.");
        if (environmentIds.Length > 0) await svc.AddLinesAsync(id, environmentIds);
        return id;
    }

    private async Task SetVersionAsync(int environmentId, string version)
    {
        await using var ctx = _f.Db.NewContext();
        (await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == environmentId)).Version = version;
        await ctx.SaveChangesAsync();
    }

    /// <summary>A solution with a Production environment and no team: visible to everyone (Public), manageable by nobody here.</summary>
    private async Task<(int ProjectId, int EnvironmentId)> SeedUnmanagedAsync(string name)
    {
        await using var ctx = _f.Db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = name, CreatedByUserId = UpgradeActionTestFixture.OwnerUserId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production",
            ApplicationFamily = "BusinessCentral", Status = "Active", Version = "27.5.12345.0", FetchedAt = DateTime.UtcNow,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        return (project.Id, env.Id);
    }

    /// <summary>
    /// Puts a line on the upgrade directly - the engine would refuse, because this person
    /// may not manage it, but somebody who may did.
    /// </summary>
    private async Task PutOnUpgradeAsync(int upgradeId, (int ProjectId, int EnvironmentId) env)
    {
        await using var ctx = _f.Db.NewContext();
        ctx.OeEnvironmentUpgradeLines.Add(new OeEnvironmentUpgradeLine
        {
            OrganizationId = TestDb.DefaultOrgId, UpgradeId = upgradeId, EnvironmentId = env.EnvironmentId,
            ProjectId = env.ProjectId, IsOpen = true, AddedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>A date move sent from the upgrade: after it, the line and the upgrade are part of the record.</summary>
    private async Task ActionFromUpgradeAsync(int upgradeId, int projectId, int environmentId)
    {
        await using var ctx = _f.Db.NewContext();
        ctx.OeEnvironmentUpgradeActions.Add(new OeEnvironmentUpgradeAction
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, EnvironmentId = environmentId,
            Kind = UpgradeActionKind.PushDateToLatest, Status = UpgradeActionStatus.Sent,
            RequestedBy = "Anna Jensen <upgrade@example.com>", RequestedAt = DateTime.UtcNow.AddHours(-1),
            ExecuteAfter = DateTime.UtcNow.AddHours(-1), SentAt = DateTime.UtcNow.AddHours(-1), UpgradeId = upgradeId,
        });
        await ctx.SaveChangesAsync();
    }

    private static AngleSharp.Dom.IElement RowMenuItem(IRenderedComponent<UpgradeDetail> cut, string solution, string label) =>
        Row(cut, solution).QuerySelectorAll(".ra__menu .menu__item").Single(i => i.TextContent.Trim() == label);

    private static AngleSharp.Dom.IElement HeadPrimary(IRenderedComponent<UpgradeDetail> cut) =>
        cut.FindAll(".page-head__actions > .btn--primary").Single();

    private static AngleSharp.Dom.IElement HeadButton(IRenderedComponent<UpgradeDetail> cut, string label) =>
        cut.FindAll(".page-head__actions > button").Single(b => b.TextContent.Trim() == label);

    /// <summary>The head's overflow entries a desktop shows (the phone-only copies of the ribbon left out).</summary>
    private static IEnumerable<AngleSharp.Dom.IElement> OverflowItems(IRenderedComponent<UpgradeDetail> cut) =>
        cut.FindAll(".page-head__actions .ra__menu .menu__item").Where(i => !i.ClassList.Contains("upd-phone-only"));

    private static string Meta(IRenderedComponent<UpgradeDetail> cut, string label) =>
        cut.FindAll(".meta-item").Single(m => m.QuerySelector(".meta-item__label")!.TextContent == label)
            .QuerySelector(".meta-item__value")!.TextContent.Trim();

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<UpgradeDetail> cut, string solution) =>
        cut.FindAll(".upd-table tbody tr").First(r => r.QuerySelector(".upd-sol")?.TextContent.Contains(solution) == true);

    private static AngleSharp.Dom.IElement CheckBox(IRenderedComponent<UpgradeDetail> cut, string solution) =>
        Row(cut, solution).QuerySelector(".upd-check__box input")!;

    private static AngleSharp.Dom.IElement PickBox(IRenderedComponent<UpgradeDetail> cut, string solution) =>
        cut.Find($"label[aria-label='Select {solution} Production'] input");

    private static AngleSharp.Dom.IElement PickerRow(IRenderedComponent<UpgradeDetail> cut, string solution) =>
        cut.FindAll(".upk-table tbody tr").First(r => r.QuerySelector(".upk-sol")!.TextContent == solution);

    private static AngleSharp.Dom.IElement PickerBox(IRenderedComponent<UpgradeDetail> cut, string solution) =>
        PickerRow(cut, solution).QuerySelector("input[type=checkbox]")!;
}
