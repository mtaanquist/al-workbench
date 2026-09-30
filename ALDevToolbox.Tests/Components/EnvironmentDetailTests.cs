using ALDevToolbox.Components.Pages.Environments;
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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// One environment's own page (PageEnvironmentDetail.dc.html, issue #809). The named
/// user is an ops engineer who would otherwise open the customer's admin centre.
///
/// <para>Two readings share the page and the tests keep them apart. The head, the meta
/// row and the Updates card are our own mirror; Apps, Operations, Sessions and the
/// settings' values are live, with the customer's credentials. Both halves are reads,
/// so both follow the solution's visibility, and what acts on the customer's tenant
/// needs managing it. The colleague in these tests reads a <b>Read-only</b> solution,
/// which is the level where those two answers differ: a Public solution is managed by
/// everyone in the organisation, so nobody is a reader-only on one.
/// The live half is served here from the panel cache, so Business Central is never
/// reached - the doubles throw if it is.</para>
/// </summary>
public sealed class EnvironmentDetailTests : IAsyncDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly BcPanelCache _panels = new(TimeProvider.System);
    private readonly SessionsAdminClient _admin = new();
    private const int OwnerUserId = 9870;
    private const int ColleagueUserId = 9871;
    private static readonly Guid TenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public EnvironmentDetailTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("owner@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<UpgradeFleetService>();
        _ctx.Services.AddScoped<UpgradeActionService>();
        _ctx.Services.AddScoped<ProjectConnectionService>();
        _ctx.Services.AddSingleton<IBcAdminClient>(_admin);
        _ctx.Services.AddSingleton<IBcAppManagementClient>(new UnreachableAppManagementClient());
        // A token the Sessions tests can spend. Every other tab is stopped before this by
        // a solution with no client id, so the doubles still stand in for a live tenant.
        _ctx.Services.AddSingleton(new BcTokenService(
            new TokenFactory(), NullLogger<BcTokenService>.Instance));
        _ctx.Services.AddSingleton(_db.DataProtectionProvider);
        _ctx.Services.AddSingleton(_panels);
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddSingleton(new EnvironmentRefreshQueue());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        // The page reads Business Central only once it is live; see the prerender test.
        _ctx.SetRendererInfo(new RendererInfo("Server", isInteractive: true));

        using var seed = _db.NewContext();
        seed.Users.AddRange(
            NewUser(OwnerUserId, "owner@example.com"),
            NewUser(ColleagueUserId, "colleague@example.com"));
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public async ValueTask DisposeAsync()
    {
        // Teardown has raced the page three times (#895, #905, #924), and the three
        // symptoms had one cause: bunit's DisposeComponentsAsync clears its list of root
        // components right after posting the detach to the renderer's dispatcher, so
        // when the dispatcher is busy with one of the page's loads at that moment - it
        // usually is, straight after an assertion - the posted work finds nothing to
        // detach, and the page lives on: its timer ticks into a disposed context
        // ("connection is in state Connecting"), its list is iterated under the clear
        // ("Collection was modified"), or its next query runs after the database has
        // been dropped ('database "aldt_test_..." does not exist'). The settle and the
        // sleeps that were here only shortened the odds.
        //
        // So the components are not disposed through that call. The renderer's own
        // DisposeAsync walks every component on the dispatcher, behind whatever the
        // page is doing, and waits for the page's DisposeAsync - which cancels its
        // reads and waits for the last call to let go of the DbContext. Only then
        // is the service provider disposed and the database dropped. The settle is
        // a second line, not the first: it covers a command a dying call is still
        // disposing. Leaving_the_page_cancels_the_read_under_way_and_waits_for_it holds
        // the page and the renderer to the first part of that.
        await _ctx.Renderer.DisposeAsync();
        _db.WaitForQueriesToSettle();
        await _ctx.DisposeAsync();
        _db.Dispose();
    }

    /// <summary>
    /// The Admin Center as the Sessions tab uses it, and nothing else: every other call
    /// still throws, so a tab that reached for the customer's tenant would say so.
    /// </summary>
    private sealed class SessionsAdminClient : UnreachableAdminClient
    {
        public Func<IReadOnlyList<BcSession>> OnSessions { get; set; } = Array.Empty<BcSession>;

        /// <summary>Every read, so a test can show that arriving reads and a tick reads again.</summary>
        public int Reads;

        /// <summary>Set to make one read fail, as a tenant that stops answering would.</summary>
        public BcApiException? SessionsThrows;

        public List<int> Cancelled { get; } = new();

        /// <summary>Set to make every read wait until the page gives up on it.</summary>
        public bool HangUntilCancelled;

        /// <summary>Reads the page gave up on, so a test can show that leaving does.</summary>
        public int CancelledReads;

        public override async Task<IReadOnlyList<BcSession>> ListSessionsAsync(
            string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            if (SessionsThrows is { } refusal) throw refusal;
            if (HangUntilCancelled)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref CancelledReads);
                    throw;
                }
            }
            return OnSessions();
        }

        public override Task CancelSessionAsync(
            string accessToken, string? applicationFamily, string environmentName, int sessionId,
            CancellationToken ct = default)
        {
            Cancelled.Add(sessionId);
            return Task.CompletedTask;
        }
    }

    /// <summary>A login that answers with a token, so the sessions read gets as far as the client.</summary>
    private sealed class TokenFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(), disposeHandler: false);

        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"tok","expires_in":3600}"""),
                });
        }
    }

    private static User NewUser(int id, string email) => new()
    {
        Id = id,
        OrganizationId = TestDb.DefaultOrgId,
        Email = email,
        PasswordHash = "x",
        DisplayName = email,
        Role = UserRole.Editor,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };

    private async Task<(int ProjectId, int EnvironmentId)> SeedAsync(
        ProjectVisibility visibility = ProjectVisibility.Public)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS Denmark",
            Visibility = visibility,
            BcTenantId = TenantId,
            BcTimeZone = "Europe/Copenhagen",
            CreatedByUserId = OwnerUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Name = "Production",
            Type = "Production",
            ApplicationFamily = "BusinessCentral",
            Status = "Active",
            Version = "28.2.41125.0",
            CountryCode = "DK",
            LocationName = "West Europe",
            FetchedAt = DateTime.UtcNow,
            UpdateWindowStart = new TimeOnly(22, 0),
            UpdateWindowEnd = new TimeOnly(4, 0),
            BcNextUpdateVersion = "28.3",
            BcNextUpdateDate = new DateTime(2026, 10, 12, 2, 0, 0, DateTimeKind.Utc),
            BcNextUpdateLatestDate = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc),
            BcNextUpdateFetchedAt = DateTime.UtcNow,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        return (project.Id, env.Id);
    }

    private static readonly Guid CoreId = Guid.NewGuid();

    private static BcEnvironmentPanel Panel() => new(
        "Production",
        new HashSet<Guid>(),
        [
            new BcInstalledApp(Guid.NewGuid(), "CRONUS Sales Extension", "CRONUS International", "1.4.0.882", "Installed", "tenant", true, null, string.Empty),
            new BcInstalledApp(CoreId, "Continia Core", "Continia Software", "28.4.1.359421", "Installed", "global", true, null, string.Empty),
            new BcInstalledApp(Guid.NewGuid(), "Payables Agent", "Microsoft", "28.2.50931.53916", "Installed", "global", false, null, string.Empty),
        ],
        null,
        [
            // Blocked first on purpose: the page has to put the ready one above it.
            new BcAvailableAppUpdate(Guid.NewGuid(), "Continia Connector App", "Continia Software", "28.5.0.360827",
                [new BcAppUpdateRequirement(CoreId, "Continia Core", "Continia Software", "28.5.0.363410", "update")]),
            new BcAvailableAppUpdate(CoreId, "Continia Core", "Continia Software", "28.5.0.363410", []),
        ],
        null,
        [],
        null,
        [
            new BcEnvironmentUpdate("28.3", true, true, "scheduled", "GA", new DateTimeOffset(2026, 10, 12, 2, 0, 0, TimeSpan.Zero), null, false, "Active", null, null),
            new BcEnvironmentUpdate("28.4", true, false, "available", "GA", null, null, false, "Active", null, null),
        ],
        null,
        DateTime.UtcNow);

    private IRenderedComponent<EnvironmentDetail> Render(int environmentId, string? tab = null)
    {
        var cut = _ctx.Render<EnvironmentDetail>(p => p.Add(c => c.EnvironmentId, environmentId).Add(c => c.OpenTab, tab));
        cut.WaitForAssertion(() => cut.FindAll(".loading-block").Should().BeEmpty());
        return cut;
    }

    /// <summary>
    /// The page tells the command palette what it is about (#887). It says so on
    /// every tab, since the palette's context rows land on all five.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("history")]
    public async Task The_page_tells_the_palette_which_environment_it_is(string? tab)
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());

        var cut = Render(envId, tab);

        var marker = cut.Find("[data-palette-context]");
        marker.GetAttribute("data-palette-context").Should().Be($"environment:{envId}");
        marker.GetAttribute("data-palette-href").Should().Be($"/environments/{envId}");
    }

    [Fact]
    public async Task The_head_meta_row_and_updates_card_come_from_our_own_mirror()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());

        var cut = Render(envId);

        cut.Find("h1.detail-head__title").TextContent.Should().Be("Production");
        cut.Find(".detail-head__title-row .status-pill").ClassList.Should().Contain("status-pill--success");
        cut.Find(".detail-head__title-row .tag").TextContent.Should().Be("Production");
        cut.Find(".page-head__sub").TextContent.Trim().Should()
            .Be("Business Central environment in CRONUS Denmark - BC version 28.2 - West Europe");
        cut.FindAll(".page-head__crumbs a").Select(a => a.GetAttribute("href")).Should()
            .Equal("/solutions", $"/solutions/{projectId}", "/environments");

        cut.FindAll(".meta-row .meta-item__label").Select(l => l.TextContent).Should()
            .Equal("Type", "BC version", "Country", "Delivery window", "Next update", "Database", "Apps installed");
        var meta = cut.FindAll(".meta-row .meta-item__value").Select(v => v.TextContent.Trim()).ToList();
        meta[1].Should().Be("28.2.41125.0");
        meta[2].Should().Be("Denmark (DK)");
        meta[3].Should().Be("22:00-04:00 (Copenhagen)");
        meta[4].Should().Be("12 Oct 2026");
        meta[5].Should().Be("—", "storage has not been read for this one");
        meta[6].Should().Be("3");

        cut.FindAll(".kv-grid .meta-item__label").Select(l => l.TextContent).Should().Equal(
            "Current version", "Next update version", "Scheduled for", "Latest possible date",
            "Our delivery window", "BC update window");

        var open = cut.Find(".page-head__actions a");
        open.GetAttribute("href").Should().Be($"https://businesscentral.dynamics.com/{TenantId:D}/Production");
    }

    /// <summary>Marks the seeded environment as one the customer deleted.</summary>
    private async Task SoftDeleteAsync(int environmentId, DateTime? goneForGood)
    {
        await using var ctx = _db.NewContext();
        var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == environmentId);
        env.Status = "SoftDeleted";
        env.SoftDeletedOn = new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc);
        env.HardDeletePendingOn = goneForGood;
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// A deleted environment's page leads with the fact and the deadline, because both
    /// change what everything below them means - the version, the windows and the app
    /// lists are all the state it was in on the day it was deleted.
    /// </summary>
    [Fact]
    public async Task A_deleted_environment_says_so_at_the_top_with_the_date_it_goes_for_good()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());
        await SoftDeleteAsync(envId, new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc));

        var cut = Render(envId);

        var alert = cut.FindAll(".alert--danger").Should().ContainSingle().Subject;
        alert.TextContent.Should().Contain("Production was deleted on 20 Sep 2026.");
        alert.TextContent.Should().Contain("It is gone for good on 04 Oct 2026");
        alert.QuerySelector("button")!.TextContent.Should().Contain("Recover this environment");
    }

    [Fact]
    public async Task A_deleted_environment_with_no_deadline_from_microsoft_says_how_long_it_normally_keeps_one()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());
        await SoftDeleteAsync(envId, goneForGood: null);

        var cut = Render(envId);

        cut.Find(".alert--danger").TextContent.Should()
            .Contain("usually keeps a deleted environment for about 14 days, but hasn't said when this one goes for good");
    }

    /// <summary>A live environment gets none of it - the alert is not a permanent fixture.</summary>
    [Fact]
    public async Task A_live_environment_gets_no_deleted_alert()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());

        var cut = Render(envId);

        cut.FindAll(".alert--danger").Should().BeEmpty();
        cut.Markup.Should().NotContain("Recover this environment");
    }

    /// <summary>
    /// The confirm names the environment, its customer and says out loud that it is a
    /// production one, before anything reaches the customer's tenant.
    /// </summary>
    [Fact]
    public async Task Recovering_asks_first_and_names_the_environment()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());
        await SoftDeleteAsync(envId, new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc));

        var cut = Render(envId);

        cut.WaitForAssertion(() =>
            cut.FindAll(".alert--danger button").Single(b => b.TextContent.Contains("Recover this environment")).Click());

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Recover Production, a production environment?");
            cut.Markup.Should().Contain("CRONUS Denmark");
        });
    }

    // ── Copying the environment ───────────────────────────────────────────

    /// <summary>
    /// The commonest thing this page's reader would otherwise open the admin centre for,
    /// so it sits in the head beside the other two. An outline button: it writes to the
    /// customer's tenant, and nothing on this page is the one thing to do.
    /// </summary>
    [Fact]
    public async Task Someone_who_manages_the_solution_is_offered_a_copy_from_the_head()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());

        var cut = Render(envId);

        var copy = cut.FindAll(".page-head__actions button")
            .Should().ContainSingle(b => b.TextContent.Contains("Copy this environment...")).Subject;
        copy.ClassList.Should().NotContain("btn--primary");
    }

    [Fact]
    public async Task Someone_who_can_see_the_solution_but_not_manage_it_is_not_offered_a_copy()
    {
        var (_, envId) = await SeedAsync(ProjectVisibility.ReadOnly);
        _db.OrgContext.CurrentUserId = ColleagueUserId;

        var cut = Render(envId);

        cut.Markup.Should().NotContain("Copy this environment...");
    }

    /// <summary>A deleted environment has nothing to copy; the alert offers the one thing left to do.</summary>
    [Fact]
    public async Task A_deleted_environment_is_not_offered_a_copy()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());
        await SoftDeleteAsync(envId, new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc));

        var cut = Render(envId);

        cut.Markup.Should().NotContain("Copy this environment...");
        cut.Find(".alert--danger button").TextContent.Should().Contain("Recover this environment");
    }

    [Fact]
    public async Task The_copy_dialog_names_the_environment_and_offers_a_name_for_the_new_one()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());

        var cut = Render(envId);

        cut.WaitForAssertion(() =>
            cut.FindAll(".page-head__actions button")
                .Single(b => b.TextContent.Contains("Copy this environment...")).Click());

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Copy Production?");
            cut.Find("#copy-env-name").GetAttribute("value").Should().Be("Production-Copy");
            // Production into a sandbox: the sandbox holds the customer's real data.
            cut.Find(".note--warn").TextContent.Should().Contain("real data");
        });
    }

    [Fact]
    public async Task Waiting_updates_put_the_ready_ones_first_and_name_what_the_rest_wait_for()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());

        var cut = Render(envId, "apps");

        var rows = cut.FindAll("table.u-compact tbody tr");
        rows.Should().HaveCount(2);
        rows[0].ClassList.Should().Contain("is-published");
        rows[0].QuerySelector(".status-pill")!.TextContent.Should().Be("Ready");
        rows[0].Children[1].TextContent.Should().Be("Continia Core");
        rows[1].ClassList.Should().Contain("is-queued");
        rows[1].QuerySelector(".status-pill")!.TextContent.Should().Be("Waits for 1");
        rows[1].QuerySelectorAll(".tag").Select(t => t.TextContent).Should().Equal("Continia Core");

        // Both rows can be updated; the waiting one says up front that it moves others.
        rows[0].QuerySelector(".data-table__actions button")!.GetAttribute("aria-label")
            .Should().Be("Update Continia Core to 28.5.0.363410");
        rows[1].QuerySelector(".data-table__actions button")!.GetAttribute("aria-label")
            .Should().EndWith("along with the 1 it waits for");
    }

    [Fact]
    public async Task Updating_a_waiting_app_lists_what_moves_with_it_before_anything_is_sent()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());
        var cut = Render(envId, "apps");

        // The page is still settling its own reads when it first renders, and a click
        // on an element found before a re-render lands on a handler that is gone.
        cut.WaitForAssertion(() =>
            cut.FindAll("table.u-compact tbody tr")[1].QuerySelector(".data-table__actions button")!.Click());

        cut.WaitForAssertion(() =>
            cut.FindAll(".env-detail__alongside-list li").Select(li => li.Children[0].TextContent)
                .Should().Equal("Continia Core"));
        cut.Markup.Should().Contain("and 1 other app it waits for");
    }

    [Fact]
    public async Task An_app_from_another_company_can_be_uploaded_but_not_before_a_file_is_chosen()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());
        var cut = Render(envId, "apps");

        cut.WaitForAssertion(() =>
            cut.FindAll("button").Single(b => b.TextContent.Trim() == "Upload apps").Click());

        cut.WaitForAssertion(() => cut.Find("#upload-app-file").GetAttribute("accept").Should().Be(".app"));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Upload and install")
            .HasAttribute("disabled").Should().BeTrue();
    }

    /// <summary>
    /// The seeded environment has a delivery window, so that is the first choice and
    /// Microsoft's window is not offered beside it; picking a time reveals the field and
    /// says the slot back with the zone named, which is the sentence that keeps 12:32 on
    /// the person's clock from becoming 12:32 UTC.
    /// </summary>
    [Fact]
    public async Task The_upload_dialog_offers_the_delivery_window_first_and_says_a_picked_time_back_with_its_zone()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());
        var cut = Render(envId, "apps");

        cut.WaitForAssertion(() =>
            cut.FindAll("button").Single(b => b.TextContent.Trim() == "Upload apps").Click());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("In the delivery window, 22:00-04:00 (Copenhagen)"));
        cut.Markup.Should().NotContain("In the BC update window", "the customer's own window stands in for Microsoft's");
        cut.Markup.Should().Contain("At a time I pick (UTC time)", "the option names the zone it is read in - the test organisation shows times in UTC").And.Contain("anyone working in Business Central may be interrupted");
        cut.FindAll("input[type=datetime-local]").Should().BeEmpty("the field only appears once that option is chosen");

        cut.FindAll(".upload-app__when-opt input[type=radio]")[1].Change(true);

        cut.WaitForAssertion(() => cut.FindAll("input[type=datetime-local]").Should().HaveCount(1));
        cut.Find(".upload-app__echo").TextContent.Should().MatchRegex(
            @"^Installs at \d\d:\d\d on \d+ \w+, UTC time - \d\d:\d\d for the customer \(Copenhagen\)\.$",
            "the customer's clock differs from the page's, so both are said");
    }

    [Fact]
    public async Task Without_a_delivery_window_the_upload_dialog_falls_back_to_microsofts_window_and_says_so()
    {
        var (projectId, envId) = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == envId);
            env.UpdateWindowStart = null;
            env.UpdateWindowEnd = null;
            await ctx.SaveChangesAsync();
        }
        _panels.Set(projectId, envId, Panel());
        var cut = Render(envId, "apps");

        cut.WaitForAssertion(() =>
            cut.FindAll("button").Single(b => b.TextContent.Trim() == "Upload apps").Click());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("In the BC update window"));
        cut.Markup.Should().Contain("has no delivery window").And.NotContain("In the delivery window");
        // Microsoft's window has not been read on the seeded row, so that option cannot be booked yet.
        cut.Markup.Should().Contain("hasn't been read yet");
        cut.FindAll(".upload-app__when-opt input[type=radio]")[0].HasAttribute("disabled").Should().BeTrue();
    }

    /// <summary>
    /// An upload booked here is one of the environment's scheduled installs to the person
    /// asking what is going to install, so it sits in that list beside Business Central's
    /// own, named, timed, and with the two things that can be done to it.
    /// </summary>
    [Fact]
    public async Task A_booked_upload_is_listed_among_the_scheduled_installs_with_install_now_and_cancel()
    {
        var (projectId, envId) = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            ctx.OeEnvironmentUpgradeActions.Add(new OeEnvironmentUpgradeAction
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = projectId,
                EnvironmentId = envId,
                Kind = UpgradeActionKind.UploadApp,
                Status = UpgradeActionStatus.Pending,
                RequestedByUserId = OwnerUserId,
                RequestedBy = "owner@example.com",
                RequestedAt = DateTime.UtcNow,
                ExecuteAfter = DateTime.UtcNow.AddHours(6),
                PackageFileName = "Partner_Thing_1.0.0.0.app",
                PackageContent = new byte[] { 1, 2, 3 },
            });
            await ctx.SaveChangesAsync();
        }
        _panels.Set(projectId, envId, Panel());
        var cut = Render(envId, "apps");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Partner_Thing_1.0.0.0.app"));
        cut.Markup.Should().Contain("Uploaded here").And.Contain("Booked for").And.NotContain("Nothing waiting to install");
        cut.Markup.Should().NotContain(" of 1", "a single upload is not shown as a batch");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Install now")
            .And.Contain(b => b.TextContent.Trim() == "Cancel install");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel install").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Call off this install?"));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Call off the install").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("The install of Partner_Thing_1.0.0.0.app was cancelled."));
        await using var verify = _db.NewContext();
        var row = await verify.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.EnvironmentId == envId);
        row.Status.Should().Be(UpgradeActionStatus.Cancelled);
        row.PackageContent.Should().BeNull("a cancelled booking keeps nothing but the record");
    }

    [Fact]
    public async Task The_delivery_window_is_set_from_the_environment_it_belongs_to()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());
        var cut = Render(envId);

        cut.WaitForAssertion(() =>
            cut.FindAll("button").Single(b => b.TextContent.Trim() == "Change the delivery window").Click());
        cut.WaitForAssertion(() => cut.FindAll(".env-detail__window input[type=time]")[0].Change("22:00"));
        cut.WaitForAssertion(() => cut.FindAll(".env-detail__window input[type=time]")[1].Change("04:00"));
        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save window").Click());

        cut.WaitForAssertion(() => cut.FindAll(".env-detail__window").Should().BeEmpty());
        await using var ctx = _db.NewContext();
        var saved = await ctx.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId);
        saved.UpdateWindowStart.Should().Be(new TimeOnly(22, 0));
        saved.UpdateWindowEnd.Should().Be(new TimeOnly(4, 0));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("22:00-04:00"));
    }

    [Fact]
    public async Task Instants_follow_the_organisations_zone_while_the_windows_stay_on_the_customers_clock()
    {
        // The solution is in Copenhagen; the organisation reads times in Tokyo (issue #942).
        await _db.NewOrganizationAdminService(_db.NewContext()).SetDisplayTimeZoneAsync("Asia/Tokyo");
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());
        var cut = Render(envId);

        cut.WaitForAssertion(() =>
        {
            // 02:00 UTC on 12 Oct is 11:00 in Tokyo; the same instant on the customer's
            // clock sits under it, since the windows beside it are on that clock.
            var scheduled = cut.FindAll(".kv-grid .meta-item")
                .Single(m => m.QuerySelector(".meta-item__label")!.TextContent == "Scheduled for");
            scheduled.QuerySelector("time")!.TextContent.Should().Be("12 Oct 2026, 11:00");
            scheduled.QuerySelector("time")!.GetAttribute("title").Should().Be("2026-10-12 02:00:00 UTC");
            scheduled.QuerySelector(".env-detail__utc")!.TextContent.Should().Be("Copenhagen time · 12 Oct, 04:00");
            cut.FindAll(".meta-row .meta-item__value")[3].TextContent.Trim().Should()
                .Be("22:00-04:00 (Copenhagen)", "a delivery window is a time of day on the customer's clock, never converted");
        });

        cut.WaitForAssertion(() =>
            cut.FindAll("button").Single(b => b.TextContent.Trim() == "Change the delivery window").Click());
        cut.WaitForAssertion(() => cut.Find(".env-detail__window .field__hint").TextContent.Should()
            .StartWith("Times are in the solution's own time zone, Copenhagen."));
    }

    [Fact]
    public async Task The_prerender_draws_our_half_and_a_loader_without_waiting_for_business_central()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());
        _ctx.SetRendererInfo(new RendererInfo("Static", isInteractive: false));

        var cut = _ctx.Render<EnvironmentDetail>(p => p.Add(c => c.EnvironmentId, envId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Version and update dates",
            "the head and the Updates card come from our own mirror"));
        cut.FindAll(".loading-block").Should().NotBeEmpty("the live half is a loader until the page is live");
        cut.FindAll("table.u-compact").Should().BeEmpty(
            "even a cached panel is left for the live pass: the prerender must never reach for the customer's tenant");
    }

    [Fact]
    public async Task An_app_business_central_is_already_updating_says_so_and_cannot_be_asked_for_again()
    {
        var (projectId, envId) = await SeedAsync();
        var panel = Panel();
        // Business Central's own word on the installed app is what survives a reload.
        var updating = panel.InstalledApps.Select(a => a.AppId == CoreId ? a with { State = "Updating" } : a).ToList();
        _panels.Set(projectId, envId, panel with { InstalledApps = updating });

        var cut = Render(envId, "apps");

        var core = cut.FindAll("table.u-compact tbody tr").Single(r => r.Children[1].TextContent == "Continia Core");
        core.QuerySelector(".status-pill")!.TextContent.Should().Be("Updating");
        var button = core.QuerySelector(".data-table__actions button")!;
        button.TextContent.Trim().Should().Be("Updating...");
        button.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Installed_apps_say_where_each_came_from_and_the_filter_narrows_them()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());

        var cut = Render(envId, "apps");

        string[] Sources() => cut.FindAll("table.data-table:not(.u-compact) tbody tr")
            .Select(r => r.Children[0].TextContent.Trim() + " / " + r.Children[3].TextContent.Trim()).ToArray();

        Sources().Should().Equal(
            "CRONUS Sales Extension / Per-tenant", "Continia Core / AppSource", "Payables Agent / Microsoft");

        cut.Find("input[aria-label='Filter installed apps']").Input("continia");

        // The filter re-renders on its own turn, so the narrowed list is waited for.
        cut.WaitForAssertion(() => Sources().Should().Equal("Continia Core / AppSource"));
    }

    [Fact]
    public async Task The_page_is_five_tabs_and_each_shows_only_its_own_part()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());

        var cut = Render(envId);

        cut.FindAll(".header-tab").Select(t => t.TextContent).Should().Equal(
            "Overview", "Apps", "Operations", "Sessions", "Workbench history");
        cut.Find(".header-tab.is-active").TextContent.Should().Be("Overview");
        cut.FindAll(".header-tab").Select(t => t.GetAttribute("href")).Should().Equal(
            $"/environments/{envId}", $"/environments/{envId}/apps",
            $"/environments/{envId}/operations", $"/environments/{envId}/sessions",
            $"/environments/{envId}/history");
        _admin.Reads.Should().Be(0, "another tab never reads who is signed in");
        cut.FindAll(".setting-list").Should().NotBeEmpty("the settings live on Overview, beside the dates they move");
        cut.FindAll("table.u-compact").Should().BeEmpty("the app lists have their own tab");
        cut.Markup.Should().Contain("Version and update dates", "the numbers above the tabs are on every tab");
    }

    [Theory]
    [InlineData("whatever")]
    [InlineData("overview")]
    [InlineData("2")]
    public async Task A_segment_that_names_no_tab_is_stripped_back_to_the_overview(string segment)
    {
        var (_, envId) = await SeedAsync();

        _ctx.Render<EnvironmentDetail>(p => p.Add(c => c.EnvironmentId, envId).Add(c => c.OpenTab, segment));

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.Uri.Should().EndWith($"/environments/{envId}");
    }

    [Fact]
    public async Task History_is_our_own_record_and_asks_business_central_nothing()
    {
        // No panel cached and the doubles throw: if this tab reached for the tenant it would say so.
        var (_, envId) = await SeedAsync();

        var cut = Render(envId, "history");

        cut.Find(".header-tab.is-active").TextContent.Should().Be("Workbench history");
        cut.FindAll(".setting-list, table.u-compact").Should().BeEmpty();
        cut.Markup.Should().NotContain("Couldn't read");
    }

    [Fact]
    public async Task Operations_that_cannot_be_read_say_why_and_point_at_the_connection()
    {
        // The seeded solution has a tenant and no credentials, so the read is refused.
        var (projectId, envId) = await SeedAsync();

        var cut = Render(envId, "operations");

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Couldn't read the operations from Business Central"));
        cut.Find(".empty a.btn, .empty-state a.btn, a.btn[href$='tab=bc']").GetAttribute("href")
            .Should().Be($"/solutions/{projectId}?tab=bc");
    }

    /// <summary>
    /// What Business Central has been doing to the environment is a read, so a colleague
    /// who cannot manage the solution gets it too - they reach the same connection error
    /// the owner does rather than a card telling them to join a team. The way out of that error is
    /// the solution's Business Central tab, which is a manager's, so they are told who to
    /// ask instead of handed a button that goes nowhere they can follow.
    /// </summary>
    [Fact]
    public async Task A_colleague_reads_the_operations_of_a_solution_they_cannot_manage()
    {
        var (_, envId) = await SeedAsync(ProjectVisibility.ReadOnly);
        _db.OrgContext.CurrentUserId = ColleagueUserId;

        var cut = Render(envId, "operations");

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Couldn't read the operations from Business Central"));
        cut.Markup.Should().NotContain("add you to one of its teams");
        cut.FindAll("a.btn[href$='tab=bc']").Should().BeEmpty();
        cut.Find(".empty-state__text").TextContent.Should()
            .Contain("Ask the solution's owner or an administrator to check its Business Central connection.");
    }

    // ── Sessions ──────────────────────────────────────────────────────────

    private static BcSession Session(
        int id, string user, string clientType = "WebClient", TimeSpan? running = null,
        string currentObject = "") => new(
        id, user, clientType, new DateTimeOffset(2026, 9, 20, 6, 30, 0, TimeSpan.Zero),
        "OnRun", "Sales Order", "42", "Page",
        currentObject, currentObject.Length > 0 ? 82 : null, currentObject.Length > 0 ? "CodeUnit" : string.Empty,
        running);

    /// <summary>
    /// Gives the solution a connection the Sessions tab can spend. Everything else on the
    /// page is deliberately left without one, so a tab that reads the tenant stands out.
    /// </summary>
    private async Task SeedCredentialsAsync(int projectId)
    {
        await using var ctx = _db.NewContext();
        var project = await ctx.OeProjects.SingleAsync(p => p.Id == projectId);
        project.BcClientId = "client-abc";
        project.BcClientSecretEncrypted = _db.DataProtectionProvider
            .CreateProtector(ProjectConnectionService.SecretProtectionPurpose).Protect("secret");
        project.BcClientSecretExpiresAt = DateTime.UtcNow.AddYears(1);
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// The tab reads the moment it is opened. A Sessions tab that waits for a button is a
    /// tab that shows the wrong answer, so there is no first-run state to press.
    /// </summary>
    [Fact]
    public async Task Opening_the_sessions_tab_reads_who_is_signed_in_straight_away()
    {
        var (projectId, envId) = await SeedAsync();
        await SeedCredentialsAsync(projectId);
        _admin.OnSessions = () =>
        [
            Session(47, "ola@cronus.example", running: TimeSpan.FromMinutes(62), currentObject: "Post Sales Documents"),
        ];

        var cut = Render(envId, "sessions");

        cut.WaitForAssertion(() =>
        {
            cut.Find(".header-tab.is-active").TextContent.Should().Be("Sessions");
            cut.Find("tbody tr").TextContent.Should().Contain("ola@cronus.example");
        }, TimeSpan.FromSeconds(5));
        _admin.Reads.Should().Be(1);
        cut.Markup.Should().Contain("Sessions read from Business Central",
            "the freshness strip says which half of the page this is");
        cut.Find(".freshness button").HasAttribute("data-page-refresh").Should().BeTrue(
            "the command palette offers Refresh only on a page whose Refresh button carries the mark");
    }

    /// <summary>
    /// A sessions list that is minutes old is wrong in a way an operations list is not,
    /// so coming back to the tab reads again rather than showing what was there before.
    /// </summary>
    [Fact]
    public async Task Coming_back_to_the_tab_reads_again_rather_than_showing_the_old_list()
    {
        var (projectId, envId) = await SeedAsync();
        await SeedCredentialsAsync(projectId);
        _admin.OnSessions = () => [Session(47, "ola@cronus.example")];

        var cut = Render(envId, "sessions");
        cut.WaitForAssertion(() => _admin.Reads.Should().Be(1), TimeSpan.FromSeconds(5));

        // Workbench history asks Business Central nothing, so this leaves the tab without
        // starting a second read that the next assertion would have to tell apart.
        cut.Render(p => p.Add(c => c.EnvironmentId, envId).Add(c => c.OpenTab, "history"));
        cut.WaitForAssertion(() =>
        {
            cut.Find(".header-tab.is-active").TextContent.Should().Be("Workbench history");
            cut.Markup.Should().NotContain("ola@cronus.example", "leaving the tab forgets the list");
        });

        cut.Render(p => p.Add(c => c.EnvironmentId, envId).Add(c => c.OpenTab, "sessions"));
        cut.WaitForAssertion(() => _admin.Reads.Should().Be(2), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// It keeps itself current while it is open, on the renderer's synchronisation
    /// context - the same mechanism the Upgrades page polls with.
    /// </summary>
    [Fact]
    public async Task While_the_tab_is_open_it_re_reads_by_itself()
    {
        var (projectId, envId) = await SeedAsync();
        await SeedCredentialsAsync(projectId);
        _admin.OnSessions = () => [Session(47, "ola@cronus.example")];

        var cut = _ctx.Render<EnvironmentDetail>(p => p
            .Add(c => c.EnvironmentId, envId).Add(c => c.OpenTab, "sessions")
            .Add(c => c.SessionsLiveEvery, TimeSpan.FromMilliseconds(60))
            .Add(c => c.SessionsLiveFor, TimeSpan.FromSeconds(30)));

        cut.WaitForAssertion(() => _admin.Reads.Should().BeGreaterThan(2), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// A browser tab left open overnight must not read a customer's tenant all night, so
    /// it gives up - and says so, with the one button that starts it again.
    /// </summary>
    [Fact]
    public async Task It_stops_by_itself_and_refresh_starts_it_again()
    {
        var (projectId, envId) = await SeedAsync();
        await SeedCredentialsAsync(projectId);
        _admin.OnSessions = () => [Session(47, "ola@cronus.example")];

        var cut = _ctx.Render<EnvironmentDetail>(p => p
            .Add(c => c.EnvironmentId, envId).Add(c => c.OpenTab, "sessions")
            .Add(c => c.SessionsLiveEvery, TimeSpan.FromMilliseconds(60))
            .Add(c => c.SessionsLiveFor, TimeSpan.FromMilliseconds(30)));

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("Stopped updating after"),
            TimeSpan.FromSeconds(5));
        var stoppedAfter = _admin.Reads;

        // Widen the window before restarting, or the restarted loop would stop again
        // within a tick and the assertion below would be racing it rather than the code.
        cut.Render(p => p
            .Add(c => c.EnvironmentId, envId).Add(c => c.OpenTab, "sessions")
            .Add(c => c.SessionsLiveEvery, TimeSpan.FromMilliseconds(60))
            .Add(c => c.SessionsLiveFor, TimeSpan.FromSeconds(30)));

        cut.WaitForAssertion(() => cut.FindAll(".env-sessions__note button")
            .Single(b => b.TextContent.Contains("Keep updating")).Click());
        cut.WaitForAssertion(
            () =>
            {
                _admin.Reads.Should().BeGreaterThan(stoppedAfter);
                cut.Markup.Should().NotContain("Stopped updating after");
            },
            TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The list somebody is reading out to a customer must not be replaced by an error
    /// card because one automatic read missed.
    /// </summary>
    [Fact]
    public async Task A_failed_automatic_read_keeps_the_list_and_says_it_is_not_current()
    {
        var (projectId, envId) = await SeedAsync();
        await SeedCredentialsAsync(projectId);
        _admin.OnSessions = () => [Session(47, "ola@cronus.example")];

        var cut = _ctx.Render<EnvironmentDetail>(p => p
            .Add(c => c.EnvironmentId, envId).Add(c => c.OpenTab, "sessions")
            .Add(c => c.SessionsLiveEvery, TimeSpan.FromMilliseconds(60))
            .Add(c => c.SessionsLiveFor, TimeSpan.FromSeconds(30)));

        cut.WaitForAssertion(() => cut.Find("tbody tr").TextContent.Should().Contain("ola@cronus.example"));
        _admin.SessionsThrows = new BcApiException(System.Net.HttpStatusCode.BadGateway, "Business Central didn't answer.");

        cut.WaitForAssertion(
            () =>
            {
                cut.Markup.Should().Contain("last one we could read");
                cut.Find("tbody tr").TextContent.Should().Contain("ola@cronus.example");
            },
            TimeSpan.FromSeconds(5));
    }

    /// <summary>A first read that fails has no list to keep, so it is the unreadable state.</summary>
    [Fact]
    public async Task A_first_read_that_fails_says_why_and_points_at_the_connection()
    {
        var (projectId, envId) = await SeedAsync();

        var cut = Render(envId, "sessions");

        cut.WaitForAssertion(() =>
            cut.Find(".empty-state__title").TextContent.Should()
                .Be("Couldn't read who is signed in to this environment"));
        cut.Find(".empty-state__action a").GetAttribute("href").Should().Be($"/solutions/{projectId}?tab=bc");
    }

    /// <summary>
    /// The prerender has nobody to redraw for and waits for everything it does, so a read
    /// of the customer's tenant there is the click into the page hanging on Microsoft.
    /// </summary>
    [Fact]
    public async Task The_prerender_of_the_sessions_tab_draws_a_loader_and_reads_nothing()
    {
        var (projectId, envId) = await SeedAsync();
        await SeedCredentialsAsync(projectId);
        _admin.OnSessions = () => [Session(47, "ola@cronus.example")];
        _ctx.SetRendererInfo(new RendererInfo("Static", isInteractive: false));

        var cut = _ctx.Render<EnvironmentDetail>(p => p.Add(c => c.EnvironmentId, envId).Add(c => c.OpenTab, "sessions"));

        cut.FindAll(".loading-block").Should().NotBeEmpty();
        cut.FindAll("tbody tr").Should().BeEmpty();
        _admin.Reads.Should().Be(0, "the prerender must never reach for the customer's tenant");
    }

    /// <summary>
    /// Who is signed in is a read, so a colleague who only reads the solution sees it.
    /// Ending one of those sessions signs somebody out of the customer's system, so the
    /// column of buttons is not there for them.
    /// </summary>
    [Fact]
    public async Task A_colleague_sees_who_is_signed_in_but_gets_no_way_to_end_a_session()
    {
        var (projectId, envId) = await SeedAsync(ProjectVisibility.ReadOnly);
        await SeedCredentialsAsync(projectId);
        _db.OrgContext.CurrentUserId = ColleagueUserId;
        _admin.OnSessions = () => [Session(47, "ola@cronus.example")];

        var cut = Render(envId, "sessions");

        cut.WaitForAssertion(() => cut.Find("tbody tr").TextContent.Should().Contain("ola@cronus.example"));
        cut.FindAll("tbody .data-table__actions").Should().BeEmpty("ending a session is a write");
        cut.FindAll("button.btn--danger").Should().BeEmpty();
    }

    /// <summary>
    /// The tab stays - it is on every environment - but there is nothing to read: Business
    /// Central ended every session when the environment was deleted.
    /// </summary>
    [Fact]
    public async Task A_deleted_environment_has_the_tab_but_nobody_can_be_signed_in_to_it()
    {
        var (projectId, envId) = await SeedAsync();
        await SeedCredentialsAsync(projectId);
        await using (var ctx = _db.NewContext())
        {
            var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == envId);
            env.SoftDeletedOn = DateTime.UtcNow.AddDays(-2);
            env.Status = "SoftDeleted";
            await ctx.SaveChangesAsync();
        }

        var cut = Render(envId, "sessions");

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".header-tab").Select(t => t.TextContent).Should().Contain("Sessions");
            cut.Markup.Should().Contain("Nobody can be signed in to a deleted environment");
        });
        _admin.Reads.Should().Be(0);
    }

    [Fact]
    public async Task Nobody_signed_in_is_a_plain_empty_state_and_not_a_failure()
    {
        var (projectId, envId) = await SeedAsync();
        await SeedCredentialsAsync(projectId);

        var cut = Render(envId, "sessions");

        cut.WaitForAssertion(() => cut.Find(".empty-state__title").TextContent.Should()
            .Be("Nobody is signed in to Production"));
        cut.Markup.Should().NotContain("Couldn't read");
    }

    /// <summary>
    /// The confirm has to name the person, what their session is running, the environment
    /// and that it is a production one, and say plainly what it costs them - it is the one
    /// write here whose consequence lands on somebody who is not in the room. And the row
    /// must not move underneath them while they read it.
    /// </summary>
    [Fact]
    public async Task Ending_a_session_is_confirmed_by_name_and_nothing_moves_while_it_is_asked()
    {
        var (projectId, envId) = await SeedAsync();
        await SeedCredentialsAsync(projectId);
        _admin.OnSessions = () =>
        [
            Session(47, "ola@cronus.example", running: TimeSpan.FromMinutes(62), currentObject: "Post Sales Documents"),
        ];

        var cut = _ctx.Render<EnvironmentDetail>(p => p
            .Add(c => c.EnvironmentId, envId).Add(c => c.OpenTab, "sessions")
            .Add(c => c.SessionsLiveEvery, TimeSpan.FromMilliseconds(60))
            .Add(c => c.SessionsLiveFor, TimeSpan.FromSeconds(30)));

        cut.WaitForAssertion(() => cut.Find(".data-table__actions button").Click());

        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__title").TextContent.Should()
                .Be("End ola@cronus.example's session on Production, a production environment?");
            var body = cut.Find(".confirm-dialog__body").TextContent;
            body.Should().Contain("Production (Production) in CRONUS Denmark")
                .And.Contain("through the web client")
                .And.Contain("Post Sales Documents (code unit 82)")
                .And.Contain("anything they have not saved is lost");
        });

        // While the question is on screen the list is frozen: the row named in it must
        // still be the row the button ends.
        var readsWhileAsking = _admin.Reads;
        await Task.Delay(300);
        _admin.Reads.Should().Be(readsWhileAsking, "a tick must not move the row somebody is about to end");

        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions button")
            .Single(b => b.TextContent.Contains("End the session")).Click());
        cut.WaitForAssertion(() =>
        {
            _admin.Cancelled.Should().ContainSingle().Which.Should().Be(47);
            cut.Find(".alert--success").TextContent.Should().Contain("ola@cronus.example's session on Production was ended");
        });
    }

    [Fact]
    public async Task Leaving_the_page_cancels_the_read_under_way_and_waits_for_it()
    {
        var (projectId, envId) = await SeedAsync();
        await SeedCredentialsAsync(projectId);
        _admin.HangUntilCancelled = true;

        var cut = _ctx.Render<EnvironmentDetail>(p => p.Add(c => c.EnvironmentId, envId).Add(c => c.OpenTab, "sessions"));
        // Not WaitForAssertion: it re-checks on renders, and a read that hangs never
        // renders again once it has started.
        SpinWait.SpinUntil(() => Volatile.Read(ref _admin.Reads) == 1, TimeSpan.FromSeconds(5)).Should().BeTrue();

        // A read is still on its way to Business Central when the page goes. Leaving
        // has to end it, and has to come back only once it has ended: that is what lets
        // this class's teardown drop the database the moment the renderer is gone.
        await _ctx.Renderer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        _admin.CancelledReads.Should().Be(1);
        _db.CommandTracker.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task Declining_the_confirm_ends_nothing()
    {
        var (projectId, envId) = await SeedAsync();
        await SeedCredentialsAsync(projectId);
        _admin.OnSessions = () => [Session(47, "ola@cronus.example")];

        var cut = Render(envId, "sessions");
        cut.WaitForAssertion(() => cut.Find(".data-table__actions button").Click());
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions button")
            .First(b => b.TextContent.Trim() == "Cancel").Click());

        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog").Should().BeEmpty());
        _admin.Cancelled.Should().BeEmpty();
    }

    [Fact]
    public async Task The_three_settings_are_there_and_none_of_them_is_a_primary_button()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());

        var cut = Render(envId);

        cut.FindAll(".setting-list .setting__name").Select(n => n.TextContent).Should().Equal(
            "AppSource apps update cadence", "Access with Microsoft 365 licences", "Next Business Central update");
        cut.Find(".setting--danger .setting__lock").TextContent.Should().Contain("Writes to the customer's tenant");
        cut.FindAll("#env-version option").Select(o => o.TextContent).Should()
            .Equal("Choose a version...", "28.3 (already next)", "28.4");
        cut.Find(".alert--warn").TextContent.Should().Contain("These change Production in the customer's tenant");
        cut.FindAll(".btn--primary").Should().BeEmpty(
            "nothing on this page is the one thing to do; every action is an outline button");
    }

    /// <summary>
    /// A colleague who only reads the solution reads everything the owner reads, and changes
    /// none of it: the settings are values rather than controls, the warning that they
    /// write to the customer's tenant is for the people who can, the version row is a
    /// control and nothing else so it goes, and Refresh - the one read that makes the
    /// tenant answer again - is not offered.
    /// </summary>
    [Fact]
    public async Task A_colleague_reads_the_live_half_and_gets_no_control_over_it()
    {
        var (projectId, envId) = await SeedAsync(ProjectVisibility.ReadOnly);
        _panels.Set(projectId, envId, Panel());
        _db.OrgContext.CurrentUserId = ColleagueUserId;

        var cut = Render(envId);

        cut.Find("h1.detail-head__title").TextContent.Should().Be("Production");
        cut.FindAll(".setting-list .setting__name").Select(n => n.TextContent).Should().Equal(
            "AppSource apps update cadence", "Access with Microsoft 365 licences");
        cut.FindAll(".setting-list select").Should().BeEmpty("a colleague reads the settings, not writes them");
        cut.FindAll(".setting--danger").Should().BeEmpty();
        cut.FindAll(".alert--warn").Should().BeEmpty();
        cut.FindAll(".freshness button").Should().BeEmpty(
            "a forced re-read spends the customer's connection, which this person may not do");
        cut.FindAll("[data-page-refresh]").Should().BeEmpty(
            "with no button to press, the command palette must not offer Refresh either");
    }

    /// <summary>
    /// The Updates card says "None announced" when nothing is coming, which is a fact.
    /// When Business Central refused to say what is coming, it is a guess - and it is the
    /// guess somebody reads out to a colleague. A manager meets that refusal on the
    /// setting row the card links down to; a reader has no such row, so the card says it.
    /// </summary>
    [Fact]
    public async Task A_colleague_is_told_when_business_central_would_not_name_the_versions()
    {
        var (projectId, envId) = await SeedAsync(ProjectVisibility.ReadOnly);
        _panels.Set(projectId, envId, Panel() with
        {
            EnvironmentUpdates = [],
            EnvironmentUpdatesError = "Business Central refused: no admin access.",
        });
        _db.OrgContext.CurrentUserId = ColleagueUserId;

        var cut = Render(envId);

        cut.Find(".kv-grid").Should().NotBeNull();
        cut.Markup.Should().Contain("Couldn't read the versions this environment is being offered");
        cut.Markup.Should().Contain("Business Central refused: no admin access.");
    }

    /// <summary>Apps is a read like the rest; the buttons that act on them are not.</summary>
    [Fact]
    public async Task A_colleague_reads_the_apps_tab_without_its_actions()
    {
        var (projectId, envId) = await SeedAsync(ProjectVisibility.ReadOnly);
        _panels.Set(projectId, envId, Panel());
        _db.OrgContext.CurrentUserId = ColleagueUserId;

        var cut = Render(envId, "apps");

        cut.Markup.Should().Contain("CRONUS Sales Extension");
        cut.Markup.Should().Contain("Continia Core");
        cut.FindAll(".data-table__actions").Should().BeEmpty("every action on an app writes to the customer's tenant");
        cut.Markup.Should().NotContain("Upload apps");
    }

    [Fact]
    public async Task A_solution_with_no_connection_says_so_and_points_at_the_tab_that_fixes_it()
    {
        var (projectId, envId) = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            var project = await ctx.OeProjects.SingleAsync(p => p.Id == projectId);
            project.BcTenantId = null;
            await ctx.SaveChangesAsync();
        }

        var cut = Render(envId);

        cut.Find(".empty-state__title").TextContent.Should().Be("Couldn't read apps and settings from Business Central");
        cut.Find(".empty-state__action a").GetAttribute("href").Should().Be($"/solutions/{projectId}?tab=bc");
        cut.FindAll(".kv-grid").Should().HaveCount(1, "the mirror does not need the connection");
    }

    [Fact]
    public async Task An_environment_the_person_cannot_see_reads_exactly_like_one_that_does_not_exist()
    {
        var (_, hidden) = await SeedAsync(ProjectVisibility.Private);
        _db.OrgContext.CurrentUserId = ColleagueUserId;

        foreach (var id in new[] { hidden, hidden + 1000 })
        {
            var cut = Render(id);

            cut.Find("h1.empty-state__title").TextContent.Should().Be("We couldn't find that environment");
            cut.FindAll(".detail-head").Should().BeEmpty();
            cut.Markup.Should().NotContain("CRONUS Denmark");
        }
    }
}
