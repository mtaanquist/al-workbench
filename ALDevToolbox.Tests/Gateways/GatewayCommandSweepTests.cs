using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// <see cref="GatewayCommandSweep"/> as the app wires it: one organisation at a time in
/// its ambient scope, the command pass, and the online/offline notices to Admins, each
/// change told once. See <c>.design/on-prem-gateway.md</c>, "Online and offline".
/// </summary>
public sealed class GatewayCommandSweepTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private const string Origin = "https://workbench.cronus.example";

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly CapturingEmailService _email = new();

    public void Dispose() => _db.Dispose();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddScoped<IOrganizationContext, HttpOrganizationContext>();
        services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(_db.ConnectionString));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(new GatewayCommandSignal());
        services.AddScoped<GatewayCommandService>();
        services.AddSingleton(_db.NewContextFactory());
        services.AddSingleton<IEmailService>(_email);
        services.AddSingleton<EmailRenderer>();
        services.AddSingleton(new PublicOrigin(Origin));
        services.AddScoped<NotificationPreferenceService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<GatewayNotifier>();
        return services.BuildServiceProvider();
    }

    private static GatewayCommandSweep NewSweep(IServiceProvider provider) =>
        new(provider, NullLogger<GatewayCommandSweep>.Instance, new WorkerHeartbeatRegistry());

    private async Task SetLastSeenAsync(int gatewayId, DateTime? at)
    {
        await using var ctx = _db.NewContext();
        await ctx.Gateways.Where(g => g.Id == gatewayId).ExecuteUpdateAsync(s => s.SetProperty(g => g.LastSeenAt, at));
    }

    private async Task<List<UserNotification>> NotificationsAsync()
    {
        await using var ctx = _db.NewContext();
        return await ctx.UserNotifications.AsNoTracking().OrderBy(n => n.Id).ToListAsync();
    }

    [Fact]
    public async Task Admins_hear_once_when_a_gateway_goes_offline_and_once_when_it_comes_back()
    {
        var admin = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Admin, "admin@cronus.example");
        await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Editor, "editor@cronus.example");
        using var key = GatewayTestKit.NewKey();
        var gateway = (await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, admin.Id, key, approve: true)).GatewayId;
        await using var provider = BuildProvider();
        var sweep = NewSweep(provider);

        // Never heard from: still being set up, nothing to say.
        await sweep.SweepAsync(CancellationToken.None);
        (await NotificationsAsync()).Should().BeEmpty();

        // First connection: recorded, not announced.
        await SetLastSeenAsync(gateway, Now);
        await sweep.SweepAsync(CancellationToken.None);
        (await NotificationsAsync()).Should().BeEmpty();

        // Three missed heartbeats: offline, told once however often the sweep runs.
        _clock.Advance(TimeSpan.FromMinutes(4));
        await sweep.SweepAsync(CancellationToken.None);
        await sweep.SweepAsync(CancellationToken.None);
        var afterOffline = await NotificationsAsync();
        afterOffline.Should().ContainSingle();
        afterOffline[0].UserId.Should().Be(admin.Id, "only Admins hear about gateways");
        afterOffline[0].Category.Should().Be(NotificationCategory.Gateways);
        afterOffline[0].Title.Should().Be("Gateway offline: CRONUS-BC01");
        afterOffline[0].Path.Should().Be(GatewayNotifier.GatewaysPath);

        // Back: told once.
        await SetLastSeenAsync(gateway, Now);
        await sweep.SweepAsync(CancellationToken.None);
        await sweep.SweepAsync(CancellationToken.None);
        var all = await NotificationsAsync();
        all.Should().HaveCount(2);
        all[1].Title.Should().Be("Gateway back online: CRONUS-BC01");

        _email.Sent.Should().HaveCount(2);
        _email.Sent.Should().OnlyContain(m => m.To == "admin@cronus.example" && m.Purpose == EmailPurpose.GatewayNotification);
    }

    [Fact]
    public async Task The_sweep_expires_uncollected_commands_in_every_organisation()
    {
        var admin = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Admin, "admin@cronus.example");
        var otherAdmin = await GatewayTestKit.SeedUserAsync(_db, TestDb.OtherOrgId, UserRole.Admin, "admin@other.example");
        using var key = GatewayTestKit.NewKey();
        using var otherKey = GatewayTestKit.NewKey();
        var mine = (await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, admin.Id, key, approve: true)).GatewayId;
        var theirs = (await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.OtherOrgId, otherAdmin.Id, otherKey, approve: true)).GatewayId;
        var signal = new GatewayCommandSignal();
        foreach (var (org, gateway) in new[] { (TestDb.DefaultOrgId, mine), (TestDb.OtherOrgId, theirs) })
        {
            var scope = new AmbientOrganizationContext { CurrentOrganizationId = org };
            await using var ctx = _db.NewContext(scope);
            await new GatewayCommandService(ctx, scope, signal, _clock, NullLogger<GatewayCommandService>.Instance)
                .EnqueueAsync(new GatewayCommandRequest(gateway, GatewayVerb.RefreshInventory));
        }
        _clock.Advance(TimeSpan.FromMinutes(16));
        await using var provider = BuildProvider();

        await NewSweep(provider).SweepAsync(CancellationToken.None);

        foreach (var org in new[] { TestDb.DefaultOrgId, TestDb.OtherOrgId })
        {
            await using var ctx = _db.NewContext(new AmbientOrganizationContext { CurrentOrganizationId = org });
            var row = await ctx.GatewayCommands.SingleAsync();
            row.Status.Should().Be(GatewayCommandStatus.Expired);
            row.FailureMessage.Should().Be(GatewayCommandService.OfflineMessage);
        }
    }
}
