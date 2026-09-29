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
/// <see cref="UpgradeActionRunner"/> on its own, for what only an upgrade's page (#984)
/// asks of it and the fleet page never does: the upgrade id stamped on what it sends, and
/// the version and the slot it opens with. The fleet page's own tests cover the three
/// moves as the page drives them.
/// </summary>
public sealed class UpgradeActionRunnerTests : IDisposable
{
    private readonly UpgradeActionTestFixture _f = new();
    private readonly BunitContext _ctx = new();

    public UpgradeActionRunnerTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("upgrade@example.com");
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _ctx.Services.AddSingleton<IOrganizationContext>(_f.Db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_f.Db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_f.Db.ConnectionString).AddInterceptors(_f.Db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<UpgradeActionService>();
        _ctx.Services.AddScoped<ProjectConnectionService>();
        _ctx.Services.AddSingleton<IBcAdminClient>(_f.Admin);
        _ctx.Services.AddSingleton<IBcAppManagementClient>(new UnreachableAppManagementClient());
        _ctx.Services.AddSingleton(_f.TokenService());
        _ctx.Services.AddSingleton(_f.Db.DataProtectionProvider);
        _ctx.Services.AddSingleton(new BcPanelCache(TimeProvider.System));
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));
    }

    public void Dispose()
    {
        _f.Db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _f.Dispose();
    }

    [Fact]
    public async Task A_planned_slot_opens_on_Later_in_the_display_zone_and_books_each_customer_in_theirs_for_the_upgrade()
    {
        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Db.NewOrganizationAdminService(ctx).SetDisplayTimeZoneAsync("Europe/Copenhagen");
        }
        // The first line is the far-away customer: the slot must not be read in its zone.
        var (_, aucklandId) = await _f.SeedCustomerAsync("CRONUS New Zealand", "Pacific/Auckland");
        var (_, copenhagenId) = await _f.SeedCustomerAsync("CRONUS Denmark", "Europe/Copenhagen");
        await MirrorAsync(aucklandId, next: "28.5", offered: ["28.5"]);
        await MirrorAsync(copenhagenId, next: "28.5", offered: ["28.5"]);
        var upgradeId = await UpgradeWithLinesAsync(aucklandId, copenhagenId);

        var planned = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(5).AddHours(18), DateTimeKind.Utc);
        var copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");
        var wall = TimeZoneInfo.ConvertTimeFromUtc(planned, copenhagen);

        var rows = await RowsAsync(aucklandId, copenhagenId);
        var cut = _ctx.Render<UpgradeActionRunner>(p => p
            .Add(r => r.Pending, new List<UpgradeActionRow>())
            .Add(r => r.UpgradeId, upgradeId)
            .Add(r => r.PrefillSlotUtc, planned));

        Task? run = null;
        await cut.InvokeAsync(() => { run = cut.Instance.ConfirmUpdateNowAsync(UpgradeActionTarget.ForSelection(rows)); });

        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__title").TextContent.Should().Be("Book these updates?");
            var radios = cut.FindAll("input[name=upg-when]");
            radios[0].HasAttribute("checked").Should().BeFalse("a planned slot opens on Later, not Immediately");
            radios[1].HasAttribute("checked").Should().BeTrue();
            cut.Find(".upg-when__echo").TextContent.Should()
                .StartWith($"Runs at {wall:HH:mm} on {wall:d MMM}, in each customer's own time");
        });

        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions .btn").Last().Click());
        await run!;

        await using var read = _f.Db.NewContext();
        var actions = await read.OeEnvironmentUpgradeActions.AsNoTracking().ToListAsync();
        actions.Should().HaveCount(2);
        actions.Should().OnlyContain(a => a.UpgradeId == upgradeId && a.Kind == UpgradeActionKind.RunNow);

        // One wall clock, read in each customer's own zone - the fleet page's rule.
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        actions.Single(a => a.EnvironmentId == copenhagenId).ExecuteAfter
            .Should().Be(TimeZoneInfo.ConvertTimeToUtc(unspecified, copenhagen));
        actions.Single(a => a.EnvironmentId == aucklandId).ExecuteAfter
            .Should().Be(TimeZoneInfo.ConvertTimeToUtc(unspecified, auckland));

        cut.Instance.Results.Should().HaveCount(2);
        cut.Instance.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task A_planned_slot_that_has_passed_opens_on_Immediately()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        await MirrorAsync(envId, next: "28.5", offered: ["28.5"]);
        var rows = await RowsAsync(envId);

        var cut = _ctx.Render<UpgradeActionRunner>(p => p
            .Add(r => r.Pending, new List<UpgradeActionRow>())
            .Add(r => r.PrefillSlotUtc, DateTime.UtcNow.AddHours(-2)));
        await cut.InvokeAsync(() => { _ = cut.Instance.ConfirmUpdateNowAsync(UpgradeActionTarget.ForSelection(rows)); });

        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__title").TextContent.Should().Be("Start these updates?");
            var radios = cut.FindAll("input[name=upg-when]");
            radios[0].HasAttribute("checked").Should().BeTrue();
            radios[1].HasAttribute("checked").Should().BeFalse();
            cut.FindAll(".upg-when__echo").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task A_prefilled_version_that_is_offered_is_picked()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        await MirrorAsync(envId, next: "28.4", offered: ["29.2", "28.5", "28.4"]);
        var rows = await RowsAsync(envId);

        var cut = _ctx.Render<UpgradeActionRunner>(p => p
            .Add(r => r.Pending, new List<UpgradeActionRow>())
            .Add(r => r.PrefillVersion, "28.5"));
        await cut.InvokeAsync(() => { _ = cut.Instance.ConfirmSelectVersionAsync(UpgradeActionTarget.ForSelection(rows)); });

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".upg-version__opts input[type=radio]")
                .Where(r => r.HasAttribute("checked")).Select(r => r.GetAttribute("value"))
                .Should().Equal("28.5");
            cut.FindAll(".upg-preview__head").Select(h => h.TextContent.Trim()).Should().Contain("Will change (1)");
            cut.FindAll(".confirm-dialog__actions .btn").Last().TextContent.Trim().Should().Be("Change to 28.5");
            cut.Markup.Should().NotContain("isn't offered to these environments yet");
        });
    }

    [Fact]
    public async Task A_prefilled_version_nobody_is_offered_picks_nothing_and_says_so()
    {
        // Exactly one other version on offer: without a prefill that one would be picked.
        var (_, envId) = await _f.SeedCustomerAsync();
        await MirrorAsync(envId, next: "28.3", offered: ["28.4"]);
        var rows = await RowsAsync(envId);

        var cut = _ctx.Render<UpgradeActionRunner>(p => p
            .Add(r => r.Pending, new List<UpgradeActionRow>())
            .Add(r => r.PrefillVersion, "28.5"));
        await cut.InvokeAsync(() => { _ = cut.Instance.ConfirmSelectVersionAsync(UpgradeActionTarget.ForSelection(rows)); });

        cut.WaitForAssertion(() =>
        {
            cut.Find(".upg-version__none").TextContent.Trim().Should().Be("28.5 isn't offered to these environments yet.");
            cut.FindAll(".upg-version__opts input[type=radio]").Should().ContainSingle()
                .Which.HasAttribute("checked").Should().BeFalse("28.4 is not the upgrade's version");
            cut.FindAll(".upg-preview__head").Should().BeEmpty();
            var confirm = cut.FindAll(".confirm-dialog__actions .btn").Last();
            confirm.TextContent.Trim().Should().Be("Change the version");
            confirm.HasAttribute("disabled").Should().BeTrue();
        });
    }

    private async Task<int> UpgradeWithLinesAsync(params int[] environmentIds)
    {
        await using var ctx = _f.Db.NewContext();
        var svc = _f.Upgrades(ctx);
        var id = await svc.CreateAsync("28.5 in October 2026", "28.5", null, null);
        await svc.AddLinesAsync(id, environmentIds);
        return id;
    }

    /// <summary>The fleet rows for these environments, in the order given, as a host would hand them over.</summary>
    private async Task<List<UpgradeFleetRow>> RowsAsync(params int[] environmentIds)
    {
        await using var ctx = _f.Db.NewContext();
        var fleet = new UpgradeFleetService(ctx, _f.Db.OrgContext, new ProjectAccess(ctx, _f.Db.OrgContext),
            new EnvironmentRefreshQueue(), NullLogger<UpgradeFleetService>.Instance);
        var all = await fleet.ListFleetAsync();
        return environmentIds.Select(id => all.Single(r => r.EnvironmentId == id)).ToList();
    }

    /// <summary>Writes the next-update mirror the nightly sweep would have written.</summary>
    private async Task MirrorAsync(int environmentId, string next, List<string> offered)
    {
        await using var ctx = _f.Db.NewContext();
        var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == environmentId);
        env.BcNextUpdateVersion = next;
        env.BcNextUpdateStatus = "scheduled";
        env.BcNextUpdateDate = UpgradeActionTestFixture.ScheduledDate.UtcDateTime;
        env.BcNextUpdateLatestDate = UpgradeActionTestFixture.LatestDate.UtcDateTime;
        env.BcOfferedVersions = offered;
        env.BcNextUpdateFetchedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
    }
}
