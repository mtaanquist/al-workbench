using ALDevToolbox.Services.Tools;
using Bunit.TestDoubles;
using Bunit;
using ALDevToolbox.Components.Pages;
using ALDevToolbox.Data;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins that the teardown guard the other component fixtures rely on is
/// actually live.
///
/// <para>
/// <see cref="InFlightCommandTracker"/> only helps if EF Core really picks it
/// up - it is registered as an <c>IInterceptor</c> in bUnit's service
/// collection and discovered by <c>AddDbContext</c>, which is a convention
/// rather than something the compiler checks. If that discovery ever stops
/// working the tracker would sit at zero, every fixture's
/// <c>WaitForQueriesToSettle</c> would return instantly, and the flakiness it
/// guards against would come back looking like a fresh bUnit bug. This test
/// fails loudly instead.
/// </para>
/// </summary>
public sealed class InFlightCommandTrackerWiringTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly ALDevToolbox.Services.SingleTenant.SingleTenantModeState _singleTenant = new(false);

    public InFlightCommandTrackerWiringTests()
    {
        _db.McpAvailability.Set(true);
        _ctx.Services.AddSingleton<IToolAvailability>(new ToolAvailabilityState(_db.McpAvailability));
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<AppDbContext>(opts => opts
            .UseNpgsql(_db.ConnectionString)
            .AddInterceptors(_db.CommandTracker)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        _ctx.Services.AddScoped<DashboardService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton<ALDevToolbox.Services.SingleTenant.ISingleTenantMode>(_singleTenant);
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
    }

    [Fact]
    public void The_tracker_sees_the_queries_a_rendered_component_runs()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("user@cronus.example");
        auth.SetRoles("User");
        auth.SetClaims(new Claim("org_disabled_tools", string.Empty));

        var cut = _ctx.Render<Home>();
        cut.WaitForAssertion(() => cut.FindAll(".tool-tile").Should().NotBeEmpty());

        // Home's OnInitializedAsync reads tool counts once the user is
        // authenticated, so the interceptor must have seen at least that.
        _db.WaitForQueriesToSettle();
        _db.CommandTracker.Total.Should().BeGreaterThan(
            0, "the interceptor must reach EF or the teardown guard is a no-op");
    }

    [Fact]
    public void Waiting_leaves_no_command_holding_a_connection()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("user@cronus.example");
        auth.SetRoles("User");
        auth.SetClaims(new Claim("org_disabled_tools", string.Empty));

        var cut = _ctx.Render<Home>();
        cut.WaitForAssertion(() => cut.FindAll(".tool-tile").Should().NotBeEmpty());

        _db.WaitForQueriesToSettle().Should().BeTrue("queries should settle well inside the timeout");
        _db.CommandTracker.InFlight.Should().Be(0);
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }
}
