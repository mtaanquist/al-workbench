using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// An open interactive page keeps the principal its circuit connected with,
/// so the circuit handler re-checks it against the user's row before each
/// event and sends a stale page to the refresh endpoint instead of running
/// the event (#1534).
/// </summary>
public sealed class SessionRevalidationCircuitHandlerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingNavigationManager _navigation = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_current_admin_event_runs()
    {
        var adminId = await SeedAsync(UserRole.Admin);
        var handler = NewHandler(PrincipalFor(adminId, UserRole.Admin));

        (await DispatchAsync(handler)).Should().BeTrue();
        _navigation.Navigations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_demoted_admin_event_is_dropped_and_the_page_reloads_through_the_refresh()
    {
        var adminId = await SeedAsync(UserRole.Admin);
        var handler = NewHandler(PrincipalFor(adminId, UserRole.Admin));
        await UpdateAsync(adminId, u => u.Role = UserRole.User);

        (await DispatchAsync(handler)).Should().BeFalse("the promote-myself-back click must not run");
        _navigation.Navigations.Should().ContainSingle().Which.Should().Be(
            ("https://workbench.cronus.example/auth/session/refresh?return=%2Fadmin%2Fadministration%2Fusers", true));

        (await DispatchAsync(handler)).Should().BeFalse("a stale circuit stays stale");
        _navigation.Navigations.Should().ContainSingle("the browser is sent once");
    }

    [Fact]
    public async Task A_disabled_user_event_is_dropped()
    {
        var userId = await SeedAsync(UserRole.User);
        var handler = NewHandler(PrincipalFor(userId, UserRole.User));
        await UpdateAsync(userId, u => u.Status = UserStatus.Disabled);

        (await DispatchAsync(handler)).Should().BeFalse();
    }

    [Fact]
    public async Task A_site_admin_flag_taken_away_is_caught()
    {
        var operatorId = await SeedAsync(UserRole.Admin, siteAdmin: true);
        var handler = NewHandler(PrincipalFor(operatorId, UserRole.Admin, siteAdmin: true));
        await UpdateAsync(operatorId, u => u.IsSiteAdmin = false);

        (await DispatchAsync(handler)).Should().BeFalse();
    }

    [Fact]
    public async Task A_credential_change_after_the_page_opened_is_caught()
    {
        var userId = await SeedAsync(UserRole.User);
        var handler = NewHandler(PrincipalFor(userId, UserRole.User));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await UpdateAsync(userId, u => u.CredentialsChangedAt = _clock.GetUtcNow().UtcDateTime);

        (await DispatchAsync(handler)).Should().BeFalse();
    }

    [Fact]
    public async Task The_row_is_read_at_most_once_per_interval()
    {
        var adminId = await SeedAsync(UserRole.Admin);
        var handler = NewHandler(PrincipalFor(adminId, UserRole.Admin));
        (await DispatchAsync(handler)).Should().BeTrue();
        await UpdateAsync(adminId, u => u.Role = UserRole.User);

        (await DispatchAsync(handler)).Should().BeTrue("inside the interval the last check stands");

        _clock.Advance(SessionRevalidationCircuitHandler.CheckInterval);
        (await DispatchAsync(handler)).Should().BeFalse();
    }

    [Fact]
    public async Task Maintenance_mode_leaves_the_page_alone()
    {
        var adminId = await SeedAsync(UserRole.Admin);
        var maintenance = new MaintenanceModeState();
        maintenance.Enter("Restoring a backup");
        var handler = NewHandler(PrincipalFor(adminId, UserRole.Admin), maintenance);
        await UpdateAsync(adminId, u => u.Status = UserStatus.Disabled);

        (await DispatchAsync(handler)).Should().BeTrue("a restore empties the users table; that is not a deleted account");
    }

    [Fact]
    public async Task An_anonymous_page_is_left_alone()
    {
        var handler = NewHandler(new ClaimsPrincipal(new ClaimsIdentity()));

        (await DispatchAsync(handler)).Should().BeTrue();
    }

    [Fact]
    public void A_matching_principal_matches_and_an_extra_org_role_does_not()
    {
        var opened = _clock.GetUtcNow().UtcDateTime;
        var row = new SessionRevalidationCircuitHandler.UserSnapshot(UserStatus.Active, UserRole.Editor, false, opened.AddDays(-1));

        SessionRevalidationCircuitHandler.StillMatches(PrincipalFor(7, UserRole.Editor), row, opened).Should().BeTrue();
        SessionRevalidationCircuitHandler.StillMatches(PrincipalFor(7, UserRole.Admin), row, opened).Should().BeFalse();
        SessionRevalidationCircuitHandler.StillMatches(PrincipalFor(7, UserRole.Editor, siteAdmin: true), row, opened).Should().BeFalse();
        SessionRevalidationCircuitHandler.StillMatches(PrincipalFor(7, UserRole.Editor), null, opened).Should().BeFalse();
    }

    private async Task<bool> DispatchAsync(SessionRevalidationCircuitHandler handler)
    {
        var ran = false;
        var pipeline = handler.CreateInboundActivityHandler(_ => { ran = true; return Task.CompletedTask; });
        await pipeline(null!);
        return ran;
    }

    private SessionRevalidationCircuitHandler NewHandler(ClaimsPrincipal principal, MaintenanceModeState? maintenance = null) =>
        new(new FixedAuthState(principal), new FixtureContextFactory(_db), _db.OrgContext, _navigation, _clock,
            maintenance ?? new MaintenanceModeState(), NullLogger<SessionRevalidationCircuitHandler>.Instance);

    private static ClaimsPrincipal PrincipalFor(int userId, UserRole role, bool siteAdmin = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Role, role.ToString()),
            new(HttpOrganizationContext.UserIdClaim, userId.ToString()),
            new(HttpOrganizationContext.OrganizationIdClaim, TestDb.DefaultOrgId.ToString()),
        };
        if (siteAdmin)
        {
            claims.Add(new Claim(HttpOrganizationContext.SiteAdminClaim, "true"));
            claims.Add(new Claim(ClaimTypes.Role, HttpOrganizationContext.SiteAdminRole));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private async Task<int> SeedAsync(UserRole role, bool siteAdmin = false)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"{Guid.NewGuid():N}@cronus.example",
            DisplayName = "Sam Berg",
            PasswordHash = "x",
            Role = role,
            IsSiteAdmin = siteAdmin,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime.AddDays(-30),
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        _db.OrgContext.CurrentUserId = user.Id;
        return user.Id;
    }

    private async Task UpdateAsync(int userId, Action<User> change)
    {
        await using var ctx = _db.NewContext();
        change(await ctx.Users.SingleAsync(u => u.Id == userId));
        await ctx.SaveChangesAsync();
    }

    private sealed class FixedAuthState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class FixtureContextFactory(TestDb db) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => db.NewContext();
    }

    private sealed class RecordingNavigationManager : NavigationManager
    {
        public List<(string Uri, bool ForceLoad)> Navigations { get; } = new();

        public RecordingNavigationManager() =>
            Initialize("https://workbench.cronus.example/", "https://workbench.cronus.example/admin/administration/users");

        protected override void NavigateToCore(string uri, NavigationOptions options) =>
            Navigations.Add((ToAbsoluteUri(uri).ToString(), options.ForceLoad));
    }
}
