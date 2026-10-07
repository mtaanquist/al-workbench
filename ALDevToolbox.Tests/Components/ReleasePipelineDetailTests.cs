using ALDevToolbox.Components.Pages.Pipelines;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Operations;
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
/// One release pipeline's page (ReleasePipelineBody.dc.html, issue #929 and #932). The
/// named user is an ops engineer who was told "the release to CRONUS's test environment
/// failed" and needs what, when, how long and why without leaving the page. The five
/// states the sheet draws - failed and expanded, healthy, scheduled on Production,
/// empty, loading - each have a test here, over releases written straight into the
/// database the way the run leaves them. Business Central is never reached: the page
/// renders from what is stored, and the doubles throw if it tried.
/// </summary>
public sealed class ReleasePipelineDetailTests : IAsyncDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private const int OwnerUserId = 9880;

    public ReleasePipelineDetailTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("owner@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ReleasePipelineService>();
        _ctx.Services.AddScoped<PipelineService>();
        _ctx.Services.AddScoped<ArtifactService>();
        _ctx.Services.AddScoped<ProjectConnectionService>();
        _ctx.Services.AddScoped<IDeliveryTokenSource>(sp => sp.GetRequiredService<ProjectConnectionService>());
        _ctx.Services.AddScoped<DeliveryService>();
        TestDb.AddToolServices(_ctx.Services);
        _ctx.Services.AddSingleton(new DeliveryQueue());
        _ctx.Services.AddSingleton<IBcAdminClient>(new UnreachableAdminClient());
        _ctx.Services.AddSingleton<IBcAppManagementClient>(new UnreachableAppManagementClient());
        _ctx.Services.AddSingleton(new BcTokenService(new UnreachableHttpClientFactory(), NullLogger<BcTokenService>.Instance));
        _ctx.Services.AddSingleton(_db.DataProtectionProvider);
        _ctx.Services.AddSingleton(new BcPanelCache(TimeProvider.System));
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddScoped(_ => NewUnusedGitHubReleaseService());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            Id = OwnerUserId, OrganizationId = TestDb.DefaultOrgId, Email = "owner@example.com",
            PasswordHash = "x", DisplayName = "K. Jensen", Role = UserRole.Editor, Status = UserStatus.Active,
        });
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public async ValueTask DisposeAsync()
    {
        // Same order as EnvironmentDetailTests: the renderer detaches the page (which
        // stops its poll) before the provider and the database go.
        await _ctx.Renderer.DisposeAsync();
        _db.WaitForQueriesToSettle();
        await _ctx.DisposeAsync();
        _db.Dispose();
    }

    [Fact]
    public void An_app_a_stopped_deployment_left_on_its_way_reads_as_not_confirmed_rather_than_failed()
    {
        var look = ReleasePipelineDetail.AppLook(ProjectDeliveryResultStatus.Unconfirmed);

        look.Word.Should().Be("Not confirmed");
        look.Tone.Should().NotBe("failed", "Business Central may well have installed it");
    }

    [Fact]
    public async Task A_failed_release_opens_with_the_whole_failure_its_apps_in_words_and_the_log()
    {
        var seed = await SeedAsync();
        const string failure = "CRONUS Warehouse 2.3.0.118 failed: Business Central reported the install as failed (ExtensionChangeFailed). "
            + "The schema synchronization of table 50110 \"Pick Zone\" failed because field 12 \"Zone Priority\" has been removed, and this message is long enough that the old page cut it off.";
        var start = DateTime.UtcNow.AddHours(-3);
        await AddDeliveryAsync(seed, ProjectDeliveryStatus.Failed, start, d =>
        {
            d.ClaimedAt = start.AddSeconds(4);
            d.StartedAt = start.AddSeconds(5);
            d.InstallStartedAt = start.AddSeconds(70);
            d.FinishedAt = start.AddSeconds(212);
            d.FailureMessage = failure;
            d.DiagnosticsLog = "02:00:05  Publishing 3 app(s) to Test.\n02:03:31  FAILED CRONUS Warehouse 2.3.0.118: refused\n02:03:32  Delivery failed.\n";
        },
        Result(0, "CRONUS Base", ProjectDeliveryResultStatus.Completed, "2.2.0.104", start.AddSeconds(5), start.AddSeconds(53)),
        Result(1, "CRONUS Warehouse", ProjectDeliveryResultStatus.Failed, "2.2.0.104", start.AddSeconds(53), start.AddSeconds(145), "Business Central reported the install as failed."),
        Result(2, "CRONUS Reports", ProjectDeliveryResultStatus.Skipped, "2.2.0.104", null, null));

        var cut = Render(seed.ReleasePipelineId);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".rp-summary__title").TextContent.Should().Be("Last deployment failed");
            cut.Find(".rp-summary__sentence").TextContent.Should()
                .Contain("on CRONUS Warehouse. CRONUS Base 2.3.0.118 was installed before it.").And.Contain("Before this deployment the environment had CRONUS Warehouse 2.2.0.104 installed.");
            cut.FindAll(".rp-rel.is-open").Should().ContainSingle("the newest release failed, so it opens by itself");
            cut.Find(".rp-rel__title").TextContent.Should().StartWith("Deployment 1");
            cut.Find(".rp-rel__build").TextContent.Should().Be($"Build #{seed.BuildId} from main");
            // A release stored before #930 kept the long line; it reads like a new one (#930).
            cut.Find(".rp-rel__why").TextContent.Should().Contain("Business Central refused a schema change");
            cut.Find(".rp-rel__why .tag").TextContent.Should().Be("ExtensionChangeFailed");
            cut.Find(".rp-what__text").TextContent.Should()
                .Be("Business Central refused a schema change while installing CRONUS Warehouse 2.3.0.118.");
            cut.Find(".rp-what__bc-text").TextContent.Should().StartWith("The schema synchronization of table 50110")
                .And.EndWith("the old page cut it off.");
            cut.Find(".rp-next__text").TextContent.Should().Contain("Force sync");
            cut.FindAll(".rp-app__msg--failed").Select(e => e.TextContent).Should().Equal("Not installed. Still on 2.2.0.104.");
            // Every app says its state in words; the skipped one says why.
            cut.FindAll(".rp-app__end .rp-strong").Select(e => e.TextContent).Should().Equal("Completed", "Failed", "Skipped");
            cut.FindAll(".rp-app__ver").Select(e => e.TextContent).Should()
                .Equal("2.2.0.104 to 2.3.0.118", "2.2.0.104 to 2.3.0.118", "2.2.0.104, not changed");
            cut.FindAll(".rp-app__msg").Select(e => e.TextContent).Should()
                .Contain("Skipped after CRONUS Warehouse failed.");
            cut.FindAll(".rp-app__took").Select(e => e.TextContent).Should().Equal("0m 48s", "1m 32s");
            // Scheduled, claimed, uploaded, installing failed, failed.
            cut.FindAll(".rp-step__label").Select(e => e.TextContent).Should()
                .Equal("Scheduled", "Claimed", "First app uploaded", "Installing failed", "Failed");
            var log = cut.Find("details.rp-log");
            log.HasAttribute("open").Should().BeTrue("the log opens by default on a failure");
            cut.FindAll(".rp-log__line").Should().HaveCount(3);
            cut.FindAll(".rp-log__line--err").Should().HaveCount(2);
            cut.Find(".rp-log__t").TextContent.Should().Be("02:00:05");
        });
    }

    [Fact]
    public async Task A_schema_change_failure_releases_again_with_force_sync_for_that_release_only()
    {
        var seed = await SeedAsync();
        var start = DateTime.UtcNow.AddHours(-3);
        await AddDeliveryAsync(seed, ProjectDeliveryStatus.Failed, start, d =>
        {
            d.StartedAt = start.AddSeconds(5);
            d.FinishedAt = start.AddSeconds(212);
            d.FailureMessage = "Business Central refused a schema change while installing CRONUS Warehouse 2.3.0.118.";
            d.DiagnosticsLog = "02:03:31  FAILED CRONUS Warehouse 2.3.0.118: Business Central reported the install as failed (ExtensionChangeFailed). "
                + "A request to the Data Plane Admin Service failed. Http status code: BadRequest Error: "
                + "{ \"code\": \"ExtensionChangeFailed\", \"message\": \"Feltet 12 er fjernet.\" }\n";
        },
        Result(0, "CRONUS Base", ProjectDeliveryResultStatus.Completed, "2.2.0.104", start.AddSeconds(5), start.AddSeconds(53)),
        Result(1, "CRONUS Warehouse", ProjectDeliveryResultStatus.Failed, "2.2.0.104", start.AddSeconds(53), start.AddSeconds(145)),
        Result(2, "CRONUS Reports", ProjectDeliveryResultStatus.Skipped, "2.2.0.104", null, null));

        var cut = Render(seed.ReleasePipelineId);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".rp-rel__acts .btn").TextContent.Should().Be("Deploy again");
            cut.Find(".rp-next__acts .btn").TextContent.Trim().Should().Be("Deploy again with Force sync");
        });
        cut.WaitForAssertion(() => cut.Find(".rp-next__acts .btn").Click());
        cut.WaitForAssertion(() =>
        {
            cut.Find("#ra-title").TextContent.Should().Be($"Deploy build #{seed.BuildId} again?");
            cut.Find(".ra-lead").TextContent.Should().Contain("CRONUS Base is already on this version and is left alone.");
            cut.Find(".ra-check input").HasAttribute("checked").Should().BeTrue();
        });
        cut.WaitForAssertion(() => cut.Find(".check--ack input").Change(true));
        cut.WaitForAssertion(() => cut.Find(".confirm-dialog__actions .btn--primary").Click());
        // No Business Central connection here, so who is online can't be checked; the
        // dialog says so and the second press goes ahead.
        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__body [role=status]").TextContent.Trim().Should()
                .StartWith("Couldn't check who is signed in to Test.")
                .And.EndWith("Are you sure you want to deploy the build?");
            cut.Find(".confirm-dialog__actions .btn--primary").TextContent.Should().Contain("Deploy anyway");
        });
        cut.Find(".confirm-dialog__actions .btn--primary").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("#ra-title").Should().BeEmpty();
            cut.FindAll(".rp-rel").Should().HaveCount(2);
            cut.Find(".rp-rel__force").TextContent.Should().Be("Force sync, this deployment only");
        });
        await using var read = _db.NewContext();
        var again = await read.OeProjectDeliveries.AsNoTracking().OrderByDescending(d => d.Id).FirstAsync();
        again.SchemaSyncMode.Should().Be(BcSyncMode.ForceSync);
        (await read.OeReleasePipelines.AsNoTracking().SingleAsync(r => r.Id == seed.ReleasePipelineId))
            .SchemaSyncMode.Should().Be(BcSyncMode.Add);
    }

    [Fact]
    public async Task A_deleted_build_pipeline_is_named_without_a_link_and_the_page_says_what_to_do()
    {
        var seed = await SeedAsync();
        await using (var db = _db.NewContext())
        {
            await db.OePipelines.Where(p => p.Id == seed.BuildPipelineId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.DeletedAt, DateTime.UtcNow));
        }

        var cut = Render(seed.ReleasePipelineId);

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("was deleted, so no new builds will reach this deployment pipeline");
            cut.FindAll($"a[href='/pipelines/{seed.BuildPipelineId}']").Should().BeEmpty("the page behind it is gone");
        });
    }

    [Fact]
    public async Task An_app_the_environment_already_had_says_so_instead_of_blaming_a_failure()
    {
        var seed = await SeedAsync();
        var start = DateTime.UtcNow.AddHours(-1);
        await AddDeliveryAsync(seed, ProjectDeliveryStatus.Deployed, start, d =>
        {
            d.StartedAt = start.AddSeconds(5);
            d.FinishedAt = start.AddSeconds(90);
        },
        Result(0, "CRONUS Base", ProjectDeliveryResultStatus.Skipped, "2.3.0.118", null, null, "Already on 2.3.0.118."),
        Result(1, "CRONUS Warehouse", ProjectDeliveryResultStatus.Completed, "2.2.0.104", start.AddSeconds(5), start.AddSeconds(80)));

        var cut = Render(seed.ReleasePipelineId);
        cut.WaitForAssertion(() => cut.Find(".rp-rel__row").Click());

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".rp-app__msg").Select(e => e.TextContent).Should().Equal("Already on 2.3.0.118.");
            cut.FindAll(".rp-rel__acts .btn").Should().BeEmpty("only a failed release is released again");
        });
    }

    [Fact]
    public async Task A_failure_Business_Central_reported_shows_its_message_as_given_once_with_the_raw_response_behind_a_fold()
    {
        // As the run stores it since #930: one line on the release, the sentence and the
        // message on the app, Business Central's whole response on the log line.
        const string danish = "Udvidelsen \"CRONUS Core\" kunne ikke installeres, fordi feltet 12 \"Zone Priority\" i tabel 50110 \"Pick Zone\" er fjernet.";
        const string raw = "A request to the Data Plane Admin Service failed. Http status code: BadRequest Error: "
            + "{ \"code\": \"ExtensionChangeFailed\", \"message\": \"Udvidelsen \\\"CRONUS Core\\\" kunne ikke installeres, fordi feltet 12 \\\"Zone Priority\\\" i tabel 50110 \\\"Pick Zone\\\" er fjernet.\" }";
        var detail = BcFailureText.Parse(raw);
        var seed = await SeedAsync();
        var start = DateTime.UtcNow.AddHours(-1);
        await AddDeliveryAsync(seed, ProjectDeliveryStatus.Failed, start, d =>
        {
            d.ClaimedAt = start.AddSeconds(4);
            d.StartedAt = start.AddSeconds(5);
            d.FinishedAt = start.AddSeconds(100);
            d.FailureMessage = BcFailureText.WhatHappened(detail.Code, "CRONUS Core 2.3.0.118");
            d.DiagnosticsLog = "02:00:05  Publishing 1 app(s) to Test.\n"
                + $"02:01:40  FAILED CRONUS Core 2.3.0.118: {BcFailureText.ForLog(detail, raw)}\n02:01:40  Delivery failed.\n";
        },
        Result(0, "CRONUS Core", ProjectDeliveryResultStatus.Failed, "2.2.0.104", start.AddSeconds(5), start.AddSeconds(100),
            BcFailureText.AppMessage(detail)));

        var cut = Render(seed.ReleasePipelineId);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".rp-rel__why").TextContent.Should().Contain("Business Central refused a schema change")
                .And.NotContain("Data Plane", "the wrapper tells the user nothing");
            cut.Find(".rp-what__text").TextContent.Should()
                .Be("Business Central refused a schema change while installing CRONUS Core 2.3.0.118.");
            cut.Find(".rp-what__bc-label").TextContent.Should().Be("Business Central's message, in the environment's language");
            cut.Find(".rp-what__bc-text").TextContent.Should().Be(danish, "shown as given, never translated");
            cut.Find(".rp-next__text").TextContent.Should().Contain("deploy this build again with Force sync").And.Contain("\"Test\"");
            // The app says where it was left, not the failure a second time.
            cut.Find(".rp-app__msg--failed").TextContent.Should().Be("Not installed. Still on 2.2.0.104.");
            cut.Find(".rp-rel__detail").TextContent.Split(danish).Length.Should().Be(2,
                "Business Central's message is on the page once (the log and the raw response keep it escaped, as sent)");
            var fold = cut.Find("details.rp-raw");
            fold.HasAttribute("open").Should().BeFalse("the raw response is for support, closed until asked for");
            cut.Find(".rp-raw__text").TextContent.Should().Contain(raw).And.StartWith("Deployment 1 of");
            cut.Find(".rp-raw__foot .copy-btn").TextContent.Should().Contain("Copy for support");
        });
    }

    [Fact]
    public async Task A_failure_the_run_raised_itself_is_shown_as_written_with_no_code_and_no_raw_response()
    {
        var seed = await SeedAsync();
        var start = DateTime.UtcNow.AddHours(-1);
        const string ours = "CRONUS Core 2.3.0.118 failed: The build's deliverables changed under the delivery.";
        await AddDeliveryAsync(seed, ProjectDeliveryStatus.Failed, start, d =>
        {
            d.StartedAt = start.AddSeconds(5);
            d.FinishedAt = start.AddSeconds(6);
            d.FailureMessage = ours;
            d.DiagnosticsLog = "02:00:05  FAILED CRONUS Core 2.3.0.118: deliverable missing.\n02:00:06  Delivery failed.\n";
        },
        Result(0, "CRONUS Core", ProjectDeliveryResultStatus.Failed, "2.2.0.104", null, null, "The build's deliverables changed under the delivery."));

        var cut = Render(seed.ReleasePipelineId);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".rp-rel__why").TextContent.Trim().Should().Be(ours);
            cut.FindAll(".rp-rel__why .tag").Should().BeEmpty();
            cut.Find(".rp-what__text").TextContent.Should().Be(ours);
            cut.FindAll(".rp-what__bc").Should().BeEmpty("these are our words, not Business Central's");
            cut.FindAll(".rp-next").Should().BeEmpty();
            cut.FindAll("details.rp-raw").Should().BeEmpty();
            cut.FindAll(".rp-app__msg--failed").Should().BeEmpty("the reason is said once, above");
        });
    }

    [Fact]
    public async Task The_head_links_the_solution_the_source_and_the_environment()
    {
        var seed = await SeedAsync();

        var cut = Render(seed.ReleasePipelineId);

        cut.WaitForAssertion(() =>
        {
            var sub = cut.Find(".page-head__sub");
            System.Text.RegularExpressions.Regex.Replace(sub.TextContent, @"\s+", " ").Should().Contain("Installs from build pipeline \"Test\" into the Sandbox environment \"Test\"");
            sub.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).Should().Equal(
                $"/solutions/{seed.ProjectId}", $"/pipelines/{seed.BuildPipelineId}", $"/environments/{seed.EnvironmentId}");
        });
    }

    [Fact]
    public async Task A_healthy_pipeline_keeps_its_rows_closed_and_says_when_the_last_release_landed()
    {
        var seed = await SeedAsync();
        var start = DateTime.UtcNow.AddMinutes(-40);
        await AddDeliveryAsync(seed, ProjectDeliveryStatus.Deployed, start, d =>
        {
            d.ClaimedAt = start; d.StartedAt = start; d.FinishedAt = start.AddSeconds(171);
        }, Result(0, "CRONUS Base", ProjectDeliveryResultStatus.Completed, "2.2.0.104", start, start.AddSeconds(40)));

        var cut = Render(seed.ReleasePipelineId);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".rp-summary__title").TextContent.Should().Be("Healthy");
            cut.Find(".rp-summary__sentence").TextContent.Should().Be("The last deployment succeeded 37 minutes ago. Nothing is scheduled.");
            cut.FindAll(".rp-rel__detail").Should().BeEmpty();
            cut.Find(".rp-rel__took").TextContent.Should().Be("2m 51s");
            cut.FindAll(".rp-rel__cell").Select(c => c.TextContent).Should().Contain("1 of 1 installed");
            cut.FindAll(".btn--primary").Select(b => b.TextContent.Trim()).Should().Equal("Deploy");
            cut.FindAll(".rp-foot").Should().BeEmpty("there is nothing older to show");
        });

        // Opening a row shows its apps; opening it again closes it.
        cut.WaitForAssertion(() =>
        {
            cut.Find(".rp-rel__row").Click();
            cut.FindAll(".rp-app").Should().ContainSingle();
        });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".rp-rel__row").Click();
            cut.FindAll(".rp-rel__detail").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task A_scheduled_release_into_Production_warns_about_force_sync_and_offers_reschedule_and_cancel()
    {
        var seed = await SeedAsync(production: true, forceSync: true);
        await AddDeliveryAsync(seed, ProjectDeliveryStatus.Scheduled, DateTime.UtcNow.AddHours(6), d =>
        {
            d.SchemaSyncMode = BcSyncMode.ForceSync;
        }, Result(0, "CRONUS Base", ProjectDeliveryResultStatus.Pending, "1.8.2.30", null, null));

        var cut = Render(seed.ReleasePipelineId);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".detail-head .status-pill").ClassList.Should().Contain("status-pill--danger");
            cut.Find(".rp-summary__title").TextContent.Should().Be("Scheduled");
            cut.Find(".rp-force").TextContent.Should().Contain("Force sync");
            cut.Markup.Should().Contain("Force sync is on for a Production environment.");
            cut.FindAll(".rp-rel.is-open").Should().ContainSingle("a waiting release is the one someone came to check");
            cut.Markup.Should().Contain("Force sync is on: if this build removes tables or fields, their data is deleted.");
            cut.FindAll(".rp-rel__acts button").Select(b => b.TextContent).Should().Equal("Reschedule", "Cancel");
            cut.FindAll(".rp-step__label").Select(e => e.TextContent).Should().Equal("Scheduled", "Claim", "Upload", "Install");
            cut.FindAll(".rp-app__end .rp-strong").Select(e => e.TextContent).Should().Equal("Pending");
        });
    }

    [Fact]
    public async Task Times_show_in_the_organisations_zone_not_the_solutions_with_the_UTC_instant_on_hover()
    {
        // The solution is in Copenhagen; the organisation reads times in Tokyo (issue #942).
        // A deployment is an instant, so it follows the organisation.
        await _db.NewOrganizationAdminService(_db.NewContext()).SetDisplayTimeZoneAsync("Asia/Tokyo");
        var seed = await SeedAsync();
        var scheduled = new DateTime(2027, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        await AddDeliveryAsync(seed, ProjectDeliveryStatus.Scheduled, scheduled);

        var cut = Render(seed.ReleasePipelineId);

        cut.WaitForAssertion(() =>
        {
            var when = cut.Find(".rp-rel__outcome .cell-stack__sub");
            when.TextContent.Should().Be("for 15 Jan 21:00", "noon UTC is 21:00 in Tokyo, not 13:00 in Copenhagen");
            when.GetAttribute("title").Should().Be("2027-01-15 12:00:00 UTC");
            cut.Find(".rp-summary__sentence").TextContent.Should().Contain("on 15 Jan at 21:00");
            cut.Markup.Should().NotContain("Times are in", "the list no longer claims the solution's zone");
        });
    }

    [Theory]
    [InlineData("2027-03-27T10:00:00", 9)] // before Copenhagen moves its clocks: UTC+1
    [InlineData("2027-03-28T10:00:00", 8)] // after: UTC+2
    public async Task Rescheduling_reads_the_picked_time_in_the_solutions_zone_across_the_March_change(string picked, int utcHour)
    {
        // The picker is a wall clock in the customer's zone, as its hint says, whatever
        // zone the organisation reads times in - so a Tokyo display zone must not move it.
        await _db.NewOrganizationAdminService(_db.NewContext()).SetDisplayTimeZoneAsync("Asia/Tokyo");
        var seed = await SeedAsync();
        await AddDeliveryAsync(seed, ProjectDeliveryStatus.Scheduled, new DateTime(2027, 1, 15, 12, 0, 0, DateTimeKind.Utc));

        var cut = Render(seed.ReleasePipelineId);
        cut.WaitForAssertion(() => cut.FindAll(".rp-rel__acts button").Single(b => b.TextContent == "Reschedule").Click());
        cut.WaitForAssertion(() =>
        {
            cut.Find("#rs-when").Input(picked);
            cut.Find(".rs-when").TextContent.Should().Contain("At a time I pick (Copenhagen time)");
        });
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions .btn--primary").Single().Click());

        cut.WaitForAssertion(() =>
        {
            using var ctx = _db.NewContext();
            var stored = ctx.OeProjectDeliveries.AsNoTracking().Single().ScheduledFor;
            stored.Should().Be(new DateTime(2027, 3, int.Parse(picked[8..10]), utcHour, 0, 0));
        });
    }

    [Fact]
    public async Task Rescheduling_to_now_asks_who_is_online_first_and_then_runs_it()
    {
        var seed = await SeedAsync();
        await AddDeliveryAsync(seed, ProjectDeliveryStatus.Scheduled, DateTime.UtcNow.AddDays(2));

        var cut = Render(seed.ReleasePipelineId);
        cut.WaitForAssertion(() => cut.FindAll(".rp-rel__acts button").Single(b => b.TextContent == "Reschedule").Click());
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".rs-when__opt input").Single(i => i.ParentElement!.TextContent.Trim().StartsWith("Now")).Change(true);
            // The build's app isn't in the environment yet, so Business Central can't hold it back.
            cut.FindAll(".rs-when__opt input").Where(i => i.ParentElement!.TextContent.Contains("Business Central update"))
                .Should().OnlyContain(i => i.HasAttribute("disabled"));
        });

        // Business Central can't be reached here, so the check comes back unknown and asks.
        cut.WaitForAssertion(() => cut.Find(".confirm-dialog__actions .btn--primary").Click());
        cut.WaitForAssertion(() => cut.Find(".confirm-dialog__actions .btn--primary").TextContent.Should().Contain("Deploy anyway"));
        cut.Find(".confirm-dialog__actions .btn--primary").Click();

        cut.WaitForAssertion(() =>
        {
            using var ctx = _db.NewContext();
            ctx.OeProjectDeliveries.AsNoTracking().Single().ScheduledFor
                .Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        });
    }

    [Fact]
    public async Task With_no_releases_the_one_primary_moves_into_the_empty_state()
    {
        var seed = await SeedAsync();

        var cut = Render(seed.ReleasePipelineId);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".empty-state__title").TextContent.Should().Be("No deployments yet");
            cut.Find(".empty-state__text").TextContent.Should()
                .Be("Nothing has been installed by this pipeline yet. Deploy the latest build to install it now.");
            cut.FindAll(".btn--primary").Should().ContainSingle()
                .Which.Closest(".empty-state").Should().NotBeNull("the head's Release is an outline copy now");
            cut.Find(".rp-summary__title").TextContent.Should().Be("Nothing deployed yet");
        });
    }

    [Fact]
    public async Task While_the_releases_load_the_head_is_drawn_over_a_skeleton()
    {
        var seed = await SeedAsync();

        // Hold the deliveries table so the list's read has to wait: the render is then
        // certainly the loading state, not a race against the database.
        await using var blocker = new NpgsqlConnection(_db.ConnectionString);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand("LOCK TABLE oe_project_deliveries IN ACCESS EXCLUSIVE MODE", blocker, tx))
        {
            await lockCmd.ExecuteNonQueryAsync();
        }

        var cut = _ctx.Render<ReleasePipelineDetail>(p => p.Add(c => c.Id, seed.ReleasePipelineId));
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Loading deployments...");
            cut.Find(".detail-head__title").TextContent.Should().Be("Test into Test");
            cut.FindAll(".skeleton").Should().NotBeEmpty();
        });

        await tx.CommitAsync();
        cut.WaitForAssertion(() => cut.Find(".empty-state__title").TextContent.Should().Be("No deployments yet"));
    }

    [Fact]
    public async Task Show_older_releases_extends_the_list_in_place()
    {
        var seed = await SeedAsync();
        for (var i = 0; i < ReleasePipelineDetail.PageSize + 2; i++)
        {
            var at = DateTime.UtcNow.AddDays(-30 + i);
            await AddDeliveryAsync(seed, ProjectDeliveryStatus.Cancelled, at, d => { d.FinishedAt = at; d.CreatedAt = at; });
        }

        var cut = Render(seed.ReleasePipelineId);
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".rp-rel").Should().HaveCount(ReleasePipelineDetail.PageSize);
            cut.Find(".rp-rel__title").TextContent.Should().StartWith($"Deployment {ReleasePipelineDetail.PageSize + 2}");
        });

        cut.WaitForAssertion(() =>
        {
            cut.Find(".rp-foot button").Click();
            cut.FindAll(".rp-rel").Should().HaveCount(ReleasePipelineDetail.PageSize + 2);
        });
        cut.WaitForAssertion(() => cut.FindAll(".rp-foot").Should().BeEmpty());
    }

    // ── Waiting for approval (#934) ───────────────────────────────────────────

    [Fact]
    public async Task A_prepared_release_waits_in_the_band_above_the_releases_not_among_them()
    {
        var seed = await SeedAsync();
        var start = DateTime.UtcNow.AddDays(-1);
        await AddDeliveryAsync(seed, ProjectDeliveryStatus.Deployed, start, d =>
        {
            d.ClaimedAt = start; d.StartedAt = start; d.FinishedAt = start.AddMinutes(3); d.CreatedAt = start;
        }, Result(0, "CRONUS Base", ProjectDeliveryResultStatus.Completed, "2.2.0.104", start, start.AddMinutes(1)));
        await AddProposalAsync(seed);

        var cut = Render(seed.ReleasePipelineId);

        cut.WaitForAssertion(() =>
        {
            var band = cut.Find(".rp-approval");
            band.QuerySelector(".rp-approval__eyebrow")!.TextContent.Should().Be("Waiting for approval");
            band.QuerySelector(".rp-approval__title")!.TextContent.Should().Be($"Deployment 2 - build #{seed.BuildId}");
            band.QuerySelector(".rp-approval__text")!.TextContent.Should()
                .StartWith($"Build #{seed.BuildId} finished today at")
                .And.EndWith("a deployment was prepared for \"Test\". Approve it to install it right away, or dismiss it.");
            cut.FindAll(".rp-approval__acts button").Select(b => b.TextContent).Should().Equal("Approve", "Dismiss");
            cut.FindAll(".rp-rel").Should().ContainSingle("the prepared release is not a row: it has not happened");
            cut.Find(".rp-summary__title").TextContent.Should().Be("Waiting for approval");
            cut.FindAll(".btn--primary").Select(b => b.TextContent.Trim()).Should().Equal("Deploy");
        });
    }

    [Fact]
    public async Task Approving_a_prepared_release_schedules_it_and_clears_the_band()
    {
        var seed = await SeedAsync();
        var id = await AddProposalAsync(seed);

        var cut = Render(seed.ReleasePipelineId);
        // Clicked once: the confirmation opens after asking who is online.
        cut.WaitForAssertion(() => cut.FindAll(".rp-approval__acts button")[0].Click());
        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__title").TextContent.Should().Be("Approve deployment 1?");
            cut.Find(".confirm-dialog__body").TextContent.Should()
                .Contain($"This installs build #{seed.BuildId}, 3 apps (CRONUS Base, CRONUS Warehouse, CRONUS Reports), into the Sandbox environment \"Test\" right away. You can still cancel it on this page until it starts.");
        });
        cut.WaitForAssertion(() =>
        {
            // Clicked only while the dialog is up: a retry after it closed has nothing to click.
            if (cut.FindAll(".confirm-dialog__actions .btn--primary") is { Count: 1 } confirm) confirm[0].Click();
            cut.FindAll(".rp-approval").Should().BeEmpty();
            cut.Find(".rp-rel__title").TextContent.Should().StartWith("Deployment 1");
        });

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.AsNoTracking().SingleAsync(d => d.Id == id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Scheduled);
        delivery.TriggeredByUserId.Should().Be(OwnerUserId);
        delivery.DiagnosticsLog.Should().Contain("Approved by K. Jensen.");
    }

    [Fact]
    public async Task Approving_a_release_that_installs_right_away_says_who_is_online_first()
    {
        _ctx.Services.AddSingleton<IDeliveryTokenSource>(new FixedTokenSource());
        _ctx.Services.AddSingleton<IBcAdminClient>(new SessionsAdminClient(
            ("ola@cronus.com", "WebClient"), ("anna@cronus.com", "Tablet"), ("anna@cronus.com", "WebClient"),
            ("USER_E5EE0099AFAB445E8B604FE18E05FC1A", "WebClient"), ("nas@cronus.com", "Background")));
        var seed = await SeedAsync();
        var id = await AddProposalAsync(seed);

        var cut = Render(seed.ReleasePipelineId);
        cut.WaitForAssertion(() => cut.FindAll(".rp-approval__acts button")[0].Click());
        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__body").TextContent.Should().Contain(
                "You can still cancel it on this page until it starts. There are 2 end-users online and 1 delegated user online in Test. Are you sure you want to deploy the build?");
            cut.Find(".confirm-dialog__actions .btn--primary").TextContent.Should().Contain("Approve anyway");
        });
        cut.WaitForAssertion(() =>
        {
            if (cut.FindAll(".confirm-dialog__actions .btn--primary") is { Count: 1 } confirm) confirm[0].Click();
            cut.FindAll(".rp-approval").Should().BeEmpty();
        });

        (await _db.NewContext().OeProjectDeliveries.AsNoTracking().SingleAsync(d => d.Id == id)).Status
            .Should().Be(ProjectDeliveryStatus.Scheduled);
    }

    private sealed class FixedTokenSource : IDeliveryTokenSource
    {
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default) =>
            Task.FromResult(new BcDeliveryContext("token", Guid.Empty));
    }

    private sealed class SessionsAdminClient : UnreachableAdminClient
    {
        private readonly (string User, string ClientType)[] _sessions;

        public SessionsAdminClient(params (string User, string ClientType)[] sessions) => _sessions = sessions;

        public override Task<IReadOnlyList<BcSession>> ListSessionsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BcSession>>(_sessions
                .Select((s, i) => new BcSession(i + 1, s.User, s.ClientType, null, "", "", "", "", "", null, "", null))
                .ToList());
    }

    [Fact]
    public async Task Approving_into_Production_needs_the_acknowledgement()
    {
        var seed = await SeedAsync(production: true);
        var id = await AddProposalAsync(seed);

        var cut = Render(seed.ReleasePipelineId);
        cut.WaitForAssertion(() => cut.FindAll(".rp-approval__acts button")[0].Click());
        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__actions .btn--primary").HasAttribute("disabled").Should().BeTrue();
            cut.Find(".confirm-dialog .check--ack").TextContent.Should().Contain("CRONUS Denmark's live Production environment");
        });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog .check--ack input").Change(true);
            cut.Find(".confirm-dialog__actions .btn--primary").HasAttribute("disabled").Should().BeFalse();
        });
        cut.WaitForAssertion(() =>
        {
            // Clicked only while the dialog is up: a retry after it closed has nothing to click.
            if (cut.FindAll(".confirm-dialog__actions .btn--primary") is { Count: 1 } confirm) confirm[0].Click();
            cut.FindAll(".rp-approval").Should().BeEmpty();
        });

        (await _db.NewContext().OeProjectDeliveries.AsNoTracking().SingleAsync(d => d.Id == id)).Status
            .Should().Be(ProjectDeliveryStatus.Scheduled);
    }

    [Fact]
    public async Task Dismissing_a_prepared_release_keeps_who_and_why_in_the_history()
    {
        var seed = await SeedAsync();
        var id = await AddProposalAsync(seed);

        var cut = Render(seed.ReleasePipelineId);
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".rp-approval__acts button")[1].Click();
            cut.Find(".confirm-dialog__title").TextContent.Should().Be("Dismiss deployment 1?");
        });
        cut.WaitForAssertion(() =>
        {
            cut.Find("#rp-dismiss-why").Input("CRONUS asked us to wait");
            cut.Find("#rp-dismiss-why").GetAttribute("value").Should().Be("CRONUS asked us to wait");
        });
        cut.WaitForAssertion(() =>
        {
            // Clicked only while the dialog is up: a retry after it closed has nothing to click.
            if (cut.FindAll(".confirm-dialog__actions .btn--primary") is { Count: 1 } confirm) confirm[0].Click();
            cut.FindAll(".rp-approval").Should().BeEmpty();
            cut.Find(".rp-rel__word").TextContent.Should().Be("- Dismissed");
        });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".rp-rel__row").Click();
            cut.Markup.Should().Contain("Dismissed by K. Jensen: CRONUS asked us to wait. Nothing was sent.");
            cut.FindAll(".rp-step__label").Select(e => e.TextContent).Should().Equal("Prepared", "Dismissed");
            cut.Find(".rp-summary__title").TextContent.Should().Be("Nothing deployed yet", "a dismissed proposal never was a release");
        });

        var stored = await _db.NewContext().OeProjectDeliveries.AsNoTracking().SingleAsync(d => d.Id == id);
        stored.Status.Should().Be(ProjectDeliveryStatus.Dismissed);
        stored.CancelledByUserId.Should().Be(OwnerUserId);
        stored.DismissReason.Should().Be("CRONUS asked us to wait");
    }

    [Fact]
    public async Task A_replaced_proposal_says_which_build_replaced_it_from_what_is_stored()
    {
        var seed = await SeedAsync();
        var id = await AddProposalAsync(seed);
        await using (var ctx = _db.NewContext())
        {
            // The log says nothing about it: the page must read the stored facts.
            await ctx.OeProjectDeliveries.Where(d => d.Id == id).ExecuteUpdateAsync(u => u
                .SetProperty(d => d.Status, ProjectDeliveryStatus.Dismissed)
                .SetProperty(d => d.ReplacedByProjectBuildId, 4242)
                .SetProperty(d => d.DismissReason, "Replaced by build #4242")
                .SetProperty(d => d.FinishedAt, DateTime.UtcNow)
                .SetProperty(d => d.DiagnosticsLog, (string?)null));
        }

        var cut = Render(seed.ReleasePipelineId);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".rp-approval").Should().BeEmpty();
            cut.Find(".rp-rel__word").TextContent.Should().Be("- Replaced");
            cut.Find(".rp-rel__why").TextContent.Should().Be("Replaced by build #4242 before anyone approved it.");
            cut.FindAll(".rp-rel__cell").Select(c => c.TextContent).Should().Contain("None sent");
        });
    }

    /// <summary>A release the pipeline prepared from the seeded build, the way the service writes one.</summary>
    private async Task<int> AddProposalAsync(Seed seed)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var delivery = new OeProjectDelivery
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId, ReleasePipelineId = seed.ReleasePipelineId,
            ProjectBuildId = seed.BuildId, TriggeredByUserId = null, EnvironmentName = "Test",
            DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
            ScheduledFor = now, Status = ProjectDeliveryStatus.Proposed,
            DiagnosticsLog = $"{now:HH:mm:ss}  {DeliveryProposalLog.Prepared(seed.BuildId)}\n",
            CreatedAt = now, UpdatedAt = now,
        };
        foreach (var (name, i) in new[] { "CRONUS Base", "CRONUS Warehouse", "CRONUS Reports" }.Select((n, i) => (n, i)))
        {
            delivery.Results.Add(Result(i, name, ProjectDeliveryResultStatus.Pending, null, null, null));
        }
        ctx.OeProjectDeliveries.Add(delivery);
        await ctx.SaveChangesAsync();
        return delivery.Id;
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    private sealed record Seed(int ProjectId, int EnvironmentId, int BuildPipelineId, int ReleasePipelineId, int BuildId);

    private IRenderedComponent<ReleasePipelineDetail> Render(int releasePipelineId)
    {
        var cut = _ctx.Render<ReleasePipelineDetail>(p => p.Add(c => c.Id, releasePipelineId));
        cut.WaitForAssertion(() => cut.FindAll(".loading-block").Should().BeEmpty());
        return cut;
    }

    private async Task<Seed> SeedAsync(bool production = false, bool forceSync = false)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Denmark", Visibility = ProjectVisibility.Public,
            BcTimeZone = "Europe/Copenhagen", CreatedByUserId = OwnerUserId, CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        var buildPipeline = new OePipeline { OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Test", CreatedAt = now, UpdatedAt = now };
        ctx.OePipelines.Add(buildPipeline);
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id,
            Name = production ? "Production" : "Test", Type = production ? "Production" : "Sandbox",
            Status = "Active", Version = "28.2.41125.0", FetchedAt = now.AddMinutes(-4),
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();

        var rp = new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = production ? "CRONUS Production" : "Test into Test",
            BuildPipelineId = buildPipeline.Id, ProjectEnvironmentId = env.Id, CreatedByUserId = OwnerUserId,
            DeploymentSchedule = BcDeploymentSchedule.Immediate,
            SchemaSyncMode = forceSync ? BcSyncMode.ForceSync : BcSyncMode.Add,
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleasePipelines.Add(rp);
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, PipelineId = buildPipeline.Id,
            Status = ProjectBuildStatus.Ready, Branch = "main", StartedAt = now.AddHours(-5), FinishedAt = now.AddHours(-5),
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();

        foreach (var (name, i) in new[] { "CRONUS Base", "CRONUS Warehouse", "CRONUS Reports" }.Select((n, i) => (n, i)))
        {
            ctx.OeProjectBuildArtifacts.Add(new OeProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id, FileName = $"{name}_2.3.0.118.app",
                AppName = name, AppVersion = "2.3.0.118", SizeBytes = 3, Content = [1, 2, (byte)i], CreatedAt = now,
            });
        }
        await ctx.SaveChangesAsync();
        return new Seed(project.Id, env.Id, buildPipeline.Id, rp.Id, build.Id);
    }

    private static OeProjectDeliveryResult Result(int ordering, string name, string status, string? previous,
        DateTime? started, DateTime? finished, string? message = null) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, Ordering = ordering, AppName = name, AppVersion = "2.3.0.118",
        Status = status, PreviousVersion = previous, StartedAt = started, FinishedAt = finished, Message = message,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private async Task AddDeliveryAsync(Seed seed, string status, DateTime scheduledFor,
        Action<OeProjectDelivery>? shape = null, params OeProjectDeliveryResult[] results)
    {
        await using var ctx = _db.NewContext();
        var delivery = new OeProjectDelivery
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId, ReleasePipelineId = seed.ReleasePipelineId,
            ProjectBuildId = seed.BuildId, TriggeredByUserId = OwnerUserId, EnvironmentName = "Test",
            DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
            ScheduledFor = scheduledFor, Status = status,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        foreach (var r in results) delivery.Results.Add(r);
        shape?.Invoke(delivery);
        ctx.OeProjectDeliveries.Add(delivery);
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// The page injects the GitHub releases service for a pipeline that installs from
    /// GitHub. Every pipeline here installs builds, so it is constructed and never
    /// called, as in <see cref="ReleaseBuildDialogTests"/>.
    /// </summary>
    private static ALDevToolbox.Services.GitHub.GitHubReleaseService NewUnusedGitHubReleaseService()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=never-opened").Options);
        var org = new AmbientOrganizationContext();
        var protection = Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aldt-release-pipeline-tests")));
        var settings = new SystemSettingsService(db, protection, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
        var client = new ALDevToolbox.Services.GitHub.GitHubAppClient(
            new HttpClient(new UnreachableHandler()) { BaseAddress = new Uri(ALDevToolbox.Services.GitHub.GitHubAppClient.ApiBaseUrl) },
            settings, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            TimeProvider.System, NullLogger<ALDevToolbox.Services.GitHub.GitHubAppClient>.Instance);
        return new ALDevToolbox.Services.GitHub.GitHubReleaseService(
            db, client,
            new ALDevToolbox.Services.GitHub.GitHubConnectionService(
                db, org, null!, settings, null!,
                NullLogger<ALDevToolbox.Services.GitHub.GitHubConnectionService>.Instance, TimeProvider.System),
            new ProjectAccess(db, org), org,
            new ALDevToolbox.Endpoints.PublicOrigin(null), TimeProvider.System,
            new ALDevToolbox.Services.Tools.ToolEnablement(TestDb.EverythingEnabled(),
                new Microsoft.AspNetCore.Http.HttpContextAccessor(), db, org, TimeProvider.System),
            NullLogger<ALDevToolbox.Services.GitHub.GitHubReleaseService>.Instance);
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("GitHub is not reachable from this test.");
    }
}
