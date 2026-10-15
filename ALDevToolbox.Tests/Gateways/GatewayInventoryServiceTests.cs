using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Inventory;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// <see cref="GatewayInventoryService"/>: the stable upsert of what a gateway reports,
/// rows marked missing and back again, the limits, the licence, the Admin's refresh
/// and the organisation fence. See <c>.design/on-prem-gateway.md</c>, "Inventory, as built".
/// </summary>
public sealed class GatewayInventoryServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(Start);
    private User _admin = null!;

    public void Dispose() => _db.Dispose();

    private GatewayInventoryService Service(AppDbContext ctx, IOrganizationContext? org = null)
    {
        var context = org ?? _db.OrgContext;
        return new(ctx, context, new GatewayCommandService(ctx, context, new GatewayCommandSignal(), _clock, NullLogger<GatewayCommandService>.Instance),
            _clock, NullLogger<GatewayInventoryService>.Instance);
    }

    private async Task<int> GatewayAsync(bool approve = true, int organizationId = TestDb.DefaultOrgId)
    {
        var admin = await GatewayTestKit.SeedUserAsync(_db, organizationId, UserRole.Admin, $"admin{Guid.NewGuid():N}@cronus.example");
        if (organizationId == TestDb.DefaultOrgId) _admin ??= admin;
        using var key = GatewayTestKit.NewKey();
        return (await GatewayTestKit.RegisterAsync(_db, _clock, organizationId, admin.Id, key, approve)).GatewayId;
    }

    private async Task<bool> ApplyAsync(int gatewayId, GatewayInventoryReport report, IOrganizationContext? org = null)
    {
        await using var ctx = org is null ? _db.NewContext() : _db.NewContext(org);
        return await Service(ctx, org).ApplyReportAsync(gatewayId, report);
    }

    private static GatewayInventoryReport Report(params GatewayInstanceInventory[] instances) => new()
    {
        Roster = instances.Select(i => new GatewayPolicyTier { Machine = i.Machine, Instance = i.Name }).ToList(),
        Instances = instances,
    };

    private static GatewayAppInventory App(string id, string version = "1.0.0.0", string name = "CRONUS Freight Extension") =>
        new() { AppId = id, Name = name, Publisher = "CRONUS Partner A/S", Version = version, IsInstalled = true };

    private static GatewayInstanceInventory Tier(string name, params GatewayTenantInventory[] tenants) => new()
    {
        Machine = "CRONUS-BC01",
        Name = name,
        Environment = "production",
        State = "Running",
        Version = "25.4.29661.29927",
        Configuration = new GatewayInstanceConfiguration { DatabaseServer = "CRONUS-SQL", DatabaseName = name + " App", Multitenant = tenants.Length > 1 },
        Tenants = tenants,
        SessionCount = 2,
    };

    private static GatewayTenantInventory Tenant(string id, params GatewayAppInventory[] apps) => new() { Id = id, State = "Operational", Apps = apps };

    private const string AppA = "8c1a6b1e-4a35-4b5a-9a8f-0c7d2e3f4a51";
    private const string AppB = "437dbf0e-84ff-417a-965d-ed2bb9650972";

    private async Task<List<GatewayInstance>> RowsAsync()
    {
        await using var ctx = _db.NewContext();
        return await ctx.GatewayInstances.AsNoTracking()
            .Include(i => i.Tenants).ThenInclude(t => t.Apps)
            .Include(i => i.Licence).ThenInclude(l => l!.Ranges)
            .OrderBy(i => i.Name)
            .ToListAsync();
    }

    // ---------------------------------------------------------------- upsert

    [Fact]
    public async Task Rows_keep_their_ids_from_one_report_to_the_next()
    {
        var gateway = await GatewayAsync();
        var report = Report(Tier("BC250", Tenant("cronus-dk", App(AppA), App(AppB)), Tenant("cronus-se", App(AppA))));

        (await ApplyAsync(gateway, report)).Should().BeTrue();
        var first = await RowsAsync();
        _clock.Advance(TimeSpan.FromMinutes(15));
        // Spelled differently: Windows names compare without regard to case.
        await ApplyAsync(gateway, Report(Tier("bc250", Tenant("CRONUS-DK", App(AppA.ToUpperInvariant()), App(AppB)), Tenant("cronus-se", App(AppA)))
            with { Machine = "cronus-bc01" }));
        var second = await RowsAsync();

        var instance = second.Should().ContainSingle().Subject;
        instance.Id.Should().Be(first[0].Id);
        instance.FirstReportedAt.Should().Be(Start.UtcDateTime);
        instance.ReadAt.Should().Be(Start.UtcDateTime.AddMinutes(15));
        instance.Tenants.Select(t => t.Id).Should().BeEquivalentTo(first[0].Tenants.Select(t => t.Id));
        instance.Tenants.SelectMany(t => t.Apps).Select(a => a.Id)
            .Should().BeEquivalentTo(first[0].Tenants.SelectMany(t => t.Apps).Select(a => a.Id));
        instance.Tenants.SelectMany(t => t.Apps).Should().OnlyContain(a => a.MissingSince == null);
        (instance.Machine, instance.Name).Should().Be(("cronus-bc01", "bc250"), "the latest spelling is shown");
    }

    [Fact]
    public async Task A_tier_the_roster_no_longer_lists_is_marked_missing_and_comes_back_as_itself()
    {
        var gateway = await GatewayAsync();
        await ApplyAsync(gateway, Report(Tier("BC140", Tenant("default")), Tier("BC250", Tenant("default"))));
        var bc140 = (await RowsAsync())[0].Id;

        _clock.Advance(TimeSpan.FromMinutes(15));
        await ApplyAsync(gateway, Report(Tier("BC250", Tenant("default"))));
        var rows = await RowsAsync();
        rows.Single(r => r.Name == "BC140").MissingSince.Should().Be(Start.UtcDateTime.AddMinutes(15));
        rows.Single(r => r.Name == "BC250").MissingSince.Should().BeNull();

        _clock.Advance(TimeSpan.FromMinutes(15));
        await ApplyAsync(gateway, Report(Tier("BC140", Tenant("default")), Tier("BC250", Tenant("default"))));
        var back = (await RowsAsync()).Single(r => r.Name == "BC140");
        back.Id.Should().Be(bc140);
        back.MissingSince.Should().BeNull();
    }

    [Fact]
    public async Task A_report_of_one_tier_without_a_roster_leaves_the_others_alone()
    {
        var gateway = await GatewayAsync();
        await ApplyAsync(gateway, Report(Tier("BC140"), Tier("BC250")));

        await ApplyAsync(gateway, new GatewayInventoryReport { Instances = [Tier("BC250")] });

        (await RowsAsync()).Should().OnlyContain(r => r.MissingSince == null);
    }

    [Fact]
    public async Task A_tenant_or_app_that_disappears_is_marked_missing_and_cleared_when_it_returns()
    {
        var gateway = await GatewayAsync();
        await ApplyAsync(gateway, Report(Tier("BC250", Tenant("cronus-dk", App(AppA), App(AppB)), Tenant("cronus-se"))));

        _clock.Advance(TimeSpan.FromMinutes(15));
        // cronus-se was removed; AppB uninstalled and unpublished from cronus-dk; AppA upgraded.
        await ApplyAsync(gateway, Report(Tier("BC250", Tenant("cronus-dk", App(AppA, "1.1.0.0")))));
        var instance = (await RowsAsync()).Single();
        var missingAt = Start.UtcDateTime.AddMinutes(15);
        instance.Tenants.Single(t => t.TenantId == "cronus-se").MissingSince.Should().Be(missingAt);
        var dk = instance.Tenants.Single(t => t.TenantId == "cronus-dk");
        dk.MissingSince.Should().BeNull();
        dk.Apps.Select(a => (a.AppId, a.Version, a.MissingSince)).Should().BeEquivalentTo(new[]
        {
            (AppA, "1.0.0.0", (DateTime?)missingAt),
            (AppB, "1.0.0.0", (DateTime?)missingAt),
            (AppA, "1.1.0.0", (DateTime?)null),
        });

        _clock.Advance(TimeSpan.FromMinutes(15));
        await ApplyAsync(gateway, Report(Tier("BC250", Tenant("cronus-dk", App(AppA, "1.1.0.0"), App(AppB)), Tenant("cronus-se"))));
        instance = (await RowsAsync()).Single();
        instance.Tenants.Should().OnlyContain(t => t.MissingSince == null);
        instance.Tenants.Single(t => t.TenantId == "cronus-dk").Apps.Where(a => a.MissingSince == null)
            .Select(a => (a.AppId, a.Version)).Should().BeEquivalentTo(new[] { (AppB, "1.0.0.0"), (AppA, "1.1.0.0") });
    }

    [Fact]
    public async Task A_list_the_gateway_cut_to_size_marks_nothing_missing()
    {
        var gateway = await GatewayAsync();
        await ApplyAsync(gateway, Report(Tier("BC250", Tenant("cronus-dk", App(AppA), App(AppB)), Tenant("cronus-se"))));

        await ApplyAsync(gateway, Report(Tier("BC250", Tenant("cronus-dk", App(AppA)) with { AppsTruncated = true }) with { TenantsTruncated = true }));

        var instance = (await RowsAsync()).Single();
        instance.TenantsTruncated.Should().BeTrue();
        instance.Tenants.Should().OnlyContain(t => t.MissingSince == null);
        instance.Tenants.SelectMany(t => t.Apps).Should().OnlyContain(a => a.MissingSince == null);
    }

    [Fact]
    public async Task A_tier_that_could_not_be_read_keeps_what_was_known_and_says_why()
    {
        var gateway = await GatewayAsync();
        await ApplyAsync(gateway, Report(Tier("BC250", Tenant("cronus-dk", App(AppA)))));

        _clock.Advance(TimeSpan.FromMinutes(15));
        await ApplyAsync(gateway, Report(new GatewayInstanceInventory { Machine = "CRONUS-BC01", Name = "BC250", Error = "Windows PowerShell did not answer." }));

        var instance = (await RowsAsync()).Single();
        instance.ReadError.Should().Be("Windows PowerShell did not answer.");
        instance.ReadAt.Should().Be(Start.UtcDateTime, "it was last read at the first report");
        instance.ReportedAt.Should().Be(Start.UtcDateTime.AddMinutes(15));
        instance.State.Should().Be("Running");
        instance.Tenants.Should().ContainSingle().Which.Apps.Should().ContainSingle().Which.MissingSince.Should().BeNull();
    }

    [Fact]
    public async Task Parts_the_tier_could_not_read_keep_their_last_values()
    {
        var gateway = await GatewayAsync();
        await ApplyAsync(gateway, Report(Tier("BC250", Tenant("cronus-dk", App(AppA)))));

        await ApplyAsync(gateway, Report(Tier("BC250") with
        {
            State = "Stopped",
            Configuration = null,
            Tenants = null,
            SessionCount = null,
            Unavailable = new Dictionary<string, string> { ["tenants"] = "The service tier is not running." },
        }));

        var instance = (await RowsAsync()).Single();
        instance.State.Should().Be("Stopped");
        instance.SessionCount.Should().BeNull("a count from before would be wrong now");
        instance.DatabaseName.Should().Be("BC250 App");
        instance.Tenants.Should().ContainSingle().Which.MissingSince.Should().BeNull();
        instance.UnavailableJson.Should().Contain("not running");
    }

    // ---------------------------------------------------------------- the licence

    [Fact]
    public async Task The_licence_is_read_into_its_expiry_and_ranges_and_replaced_only_when_it_changes()
    {
        var gateway = await GatewayAsync();
        var bc25 = GatewayInventoryParsingTests.Sample("bc25-multitenant.json")["licence"]!["text"]!.GetValue<string>();
        var bc14 = GatewayInventoryParsingTests.Sample("bc14-hybrid.json")["licence"]!["text"]!.GetValue<string>();
        await ApplyAsync(gateway, Report(Tier("BC250") with { Licence = new GatewayLicenceInventory { Text = bc25 } }));
        var first = (await RowsAsync()).Single().Licence!;
        first.ExpiresOn.Should().Be(new DateOnly(2026, 12, 31));
        first.Licensee.Should().Be("CRONUS Danmark A/S");
        first.Ranges.Should().HaveCount(7);

        _clock.Advance(TimeSpan.FromMinutes(15));
        await ApplyAsync(gateway, Report(Tier("BC250") with { Licence = new GatewayLicenceInventory { Text = bc25 } }));
        var same = (await RowsAsync()).Single().Licence!;
        same.Ranges.Select(r => r.Id).Should().BeEquivalentTo(first.Ranges.Select(r => r.Id));
        same.ReadAt.Should().Be(Start.UtcDateTime.AddMinutes(15));

        await ApplyAsync(gateway, Report(Tier("BC250") with { Licence = new GatewayLicenceInventory { Text = bc14 } }));
        var replaced = (await RowsAsync()).Single().Licence!;
        replaced.Id.Should().Be(first.Id);
        replaced.ExpiresOn.Should().BeNull();
        replaced.Ranges.Should().HaveCount(8).And.Contain(r => r.ObjectType == "MenuSuite" && r.FromId == 1050);
        await using var ctx = _db.NewContext();
        (await ctx.GatewayInstanceLicenceRanges.CountAsync()).Should().Be(8, "the old ranges were deleted, not left behind");
    }

    [Fact]
    public async Task The_bc14_sample_read_by_the_gateway_is_stored_whole()
    {
        var gateway = await GatewayAsync();
        var inventory = InventoryNormaliser.FromScriptOutput(GatewayInventoryParsingTests.Sample("bc14-hybrid.json"), GatewayInventoryParsingTests.Tier("BC140"));

        await ApplyAsync(gateway, Report(inventory));

        var instance = (await RowsAsync()).Single();
        instance.Version.Should().Be("14.0.47786.0");
        instance.SessionUsersJson.Should().BeNull("BC140's policy allows inventory but not session users");
        instance.Tenants.Single().Apps.Should().HaveCount(4);
        instance.UnavailableJson.Should().Contain("apps.tenantSpecificProperties");
        instance.Licence!.Ranges.Should().HaveCount(8);
    }

    // ---------------------------------------------------------------- limits

    [Fact]
    public async Task A_report_over_a_limit_is_refused_whole()
    {
        var gateway = await GatewayAsync();
        var apps = Enumerable.Range(0, GatewayInventoryLimits.MaxAppsPerTenant + 1).Select(i => App(Guid.NewGuid().ToString())).ToArray();

        var tooManyApps = () => ApplyAsync(gateway, Report(Tier("BC250", Tenant("cronus-dk", apps))));
        var tooManyTiers = () => ApplyAsync(gateway, Report(Enumerable.Range(0, GatewayInventoryLimits.MaxInstancesPerReport + 1).Select(i => Tier($"BC{i}")).ToArray()));
        var newerSchema = () => ApplyAsync(gateway, new GatewayInventoryReport { SchemaVersion = 2, Instances = [] });

        (await tooManyApps.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("apps");
        (await tooManyTiers.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKeys("instances", "roster");
        (await newerSchema.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("schemaVersion");
        (await RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_revoked_gateways_report_is_not_stored()
    {
        var gateway = await GatewayAsync();
        _db.OrgContext.CurrentUserId = _admin.Id;
        await using (var ctx = _db.NewContext())
        {
            await GatewayTestKit.NewService(ctx, _db.OrgContext, _clock).RevokeAsync(gateway);
        }

        (await ApplyAsync(gateway, Report(Tier("BC250")))).Should().BeFalse();
        (await RowsAsync()).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- the page and the refresh

    [Fact]
    public async Task The_page_lists_tiers_with_counts_of_what_is_there_now()
    {
        var gateway = await GatewayAsync();
        await ApplyAsync(gateway, Report(Tier("BC250", Tenant("cronus-dk", App(AppA), App(AppB)), Tenant("cronus-se", App(AppA)))));
        await ApplyAsync(gateway, Report(Tier("BC250", Tenant("cronus-dk", App(AppA)))));
        _db.OrgContext.CurrentUserId = _admin.Id;

        await using var ctx = _db.NewContext();
        var view = await Service(ctx).GetViewAsync(gateway);

        var row = view!.Instances.Should().ContainSingle().Subject;
        (row.TenantCount, row.AppCount).Should().Be((1, 1));
        row.Database.Should().Be("CRONUS-SQL / BC250 App");
        view.RefreshRequestedAt.Should().BeNull();
    }

    [Fact]
    public async Task Only_an_admin_can_see_the_page_or_ask_for_a_refresh()
    {
        var gateway = await GatewayAsync();
        var editor = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Editor, "editor@cronus.example");
        _db.OrgContext.CurrentUserId = editor.Id;

        await using var ctx = _db.NewContext();
        var view = () => Service(ctx).GetViewAsync(gateway);
        var refresh = () => Service(ctx).RequestRefreshAsync(gateway);

        await view.Should().ThrowAsync<UnauthorizedAccessException>();
        await refresh.Should().ThrowAsync<UnauthorizedAccessException>();
        (await ctx.GatewayCommands.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_refresh_is_queued_once_for_an_approved_gateway()
    {
        var gateway = await GatewayAsync();
        _db.OrgContext.CurrentUserId = _admin.Id;

        long first, second;
        await using (var ctx = _db.NewContext()) first = await Service(ctx).RequestRefreshAsync(gateway);
        await using (var ctx = _db.NewContext()) second = await Service(ctx).RequestRefreshAsync(gateway);

        second.Should().Be(first, "a refresh already waiting is not queued twice");
        await using var read = _db.NewContext();
        var command = await read.GatewayCommands.AsNoTracking().SingleAsync();
        command.Verb.Should().Be(GatewayVerb.RefreshInventory);
        command.RequestedByUserId.Should().Be(_admin.Id);
        (await Service(read).GetViewAsync(gateway))!.RefreshRequestedAt.Should().Be(Start.UtcDateTime);
    }

    [Fact]
    public async Task A_gateway_waiting_for_approval_cannot_be_asked_for_a_refresh()
    {
        var gateway = await GatewayAsync(approve: false);
        _db.OrgContext.CurrentUserId = _admin.Id;

        await using var ctx = _db.NewContext();
        var refresh = () => Service(ctx).RequestRefreshAsync(gateway);

        (await refresh.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("gateway");
    }

    // ---------------------------------------------------------------- the organisation fence

    [Fact]
    public async Task One_organisation_never_sees_or_writes_another_organisations_inventory()
    {
        var ours = await GatewayAsync();
        var foreign = await GatewayAsync(organizationId: TestDb.OtherOrgId);
        var other = new AmbientOrganizationContext { CurrentOrganizationId = TestDb.OtherOrgId };
        await ApplyAsync(foreign, Report(Tier("BC140", Tenant("default", App(AppA)))), other);

        // A report sent under our organisation for their gateway finds no gateway.
        (await ApplyAsync(foreign, Report(Tier("BC250")))).Should().BeFalse();
        await ApplyAsync(ours, Report(Tier("BC250")));
        _db.OrgContext.CurrentUserId = _admin.Id;

        await using var ctx = _db.NewContext();
        (await ctx.GatewayInstances.AsNoTracking().Select(i => i.Name).ToListAsync()).Should().Equal("BC250");
        (await ctx.GatewayInstanceApps.AsNoTracking().CountAsync()).Should().Be(0);
        (await Service(ctx).GetViewAsync(foreign)).Should().BeNull();
        var refresh = () => Service(ctx).RequestRefreshAsync(foreign);
        await refresh.Should().ThrowAsync<PlanValidationException>();

        await using var theirs = _db.NewContext(other);
        (await theirs.GatewayInstances.AsNoTracking().Select(i => i.Name).ToListAsync()).Should().Equal("BC140");
        (await theirs.GatewayInstances.AsNoTracking().Select(i => i.OrganizationId).SingleAsync()).Should().Be(TestDb.OtherOrgId);
    }
}
