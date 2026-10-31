using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.Operations;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// The cookie re-check (<c>CookieSessionRevalidation</c>) for interactive
/// pages. A Blazor circuit keeps the principal it connected with for as long
/// as the websocket stays up and never runs the cookie check again, so a user
/// disabled, demoted, or stripped of SiteAdmin while a page was open kept
/// acting with their old rights on it: every interactive admin page, and the
/// services behind them, trust that principal (#1534).
/// <para>
/// This sits in front of every inbound circuit event (clicks, form input,
/// script calls) and, at most once per <see cref="CheckInterval"/>, compares
/// the circuit's principal with the user's current row. On a mismatch it
/// drops the event, so the action never runs, and sends the browser to
/// <see cref="CookieSessionRevalidation.RefreshPath"/> with a full load. That
/// request re-reads the row over HTTP: the user is signed out, or comes back
/// to the page with claims that match the row, where the page's own
/// authorisation decides what they may still see. One check covers every
/// interactive page, so no page or service has to re-read its actor itself.
/// </para>
/// <para>
/// It is also where a restore reaches interactive pages, which the
/// maintenance middleware never sees: a click on an open page is a circuit
/// event, not a request (#1571). Once maintenance mode is on, new events from
/// anyone but a SiteAdmin are dropped and the page is reloaded, which lands on
/// the maintenance page; a SiteAdmin's pages keep working. This only covers
/// events that arrive during maintenance. A click handler that started before
/// it keeps running past its first await, beyond the reach of this hook, so
/// its database work is guarded by <see cref="Data.MaintenanceDatabaseGuard"/>
/// instead, which counts each command for the restore to wait on and refuses
/// new ones once the site is closed.
/// </para>
/// </summary>
public sealed class SessionRevalidationCircuitHandler : CircuitHandler
{
    /// <summary>
    /// How long a successful check is trusted. Short enough that a demoted
    /// user cannot ride it, long enough that typing in a bound field is not a
    /// query per keystroke.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a circuit may go on without a successful check before a
    /// failed one drops its events instead of letting them through (#1573).
    /// A brief database hiccup still lets the page work; a long outage no
    /// longer lets a circuit act indefinitely on a principal nobody has
    /// re-read.
    /// </summary>
    public static readonly TimeSpan FailClosedAfter = TimeSpan.FromMinutes(1);

    private readonly AuthenticationStateProvider _authState;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IOrganizationContext _orgContext;
    private readonly NavigationManager _navigation;
    private readonly TimeProvider _clock;
    private readonly MaintenanceModeState _maintenance;
    private readonly ILogger<SessionRevalidationCircuitHandler> _logger;

    private DateTime? _lastCheckedAt;
    private readonly DateTime _openedAt;
    private volatile bool _stale;
    private int _redirected;

    public SessionRevalidationCircuitHandler(
        AuthenticationStateProvider authState,
        IDbContextFactory<AppDbContext> dbFactory,
        IOrganizationContext orgContext,
        NavigationManager navigation,
        TimeProvider clock,
        MaintenanceModeState maintenance,
        ILogger<SessionRevalidationCircuitHandler> logger,
        MaintenanceDatabaseGuard? databaseGuard = null)
    {
        _authState = authState;
        _dbFactory = dbFactory;
        _orgContext = orgContext;
        _navigation = navigation;
        _clock = clock;
        _maintenance = maintenance;
        _logger = logger;
        _openedAt = clock.GetUtcNow().UtcDateTime;
        // The guard lets a SiteAdmin's circuit use the database during a restore
        // only while it shows a SiteAdmin console page (#1571).
        if (databaseGuard is not null) databaseGuard.CircuitOnSiteAdminConsole = OnSiteAdminConsole;
    }

    private bool OnSiteAdminConsole()
    {
        try
        {
            var relative = _navigation.ToBaseRelativePath(_navigation.Uri);
            var cut = relative.IndexOfAny(['?', '#']);
            var path = new PathString("/" + (cut < 0 ? relative : relative[..cut]));
            return path.StartsWithSegments(HttpOrganizationContext.SiteAdminPathPrefix);
        }
        catch (InvalidOperationException)
        {
            // Not initialised yet: no page, so not the console.
            return false;
        }
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            switch (await CheckAsync(CancellationToken.None))
            {
                case SessionCheck.Stale:
                    SendToRefresh();
                    return;
                case SessionCheck.Unverified:
                    // Dropped without a reload: the refresh endpoint would
                    // need the same database that just failed.
                    return;
            }
            if (_maintenance.IsActive)
            {
                // The users table may be empty or gone, so the claim decides,
                // as it does in the maintenance middleware.
                var principal = (await _authState.GetAuthenticationStateAsync()).User;
                if (principal.FindFirstValue(HttpOrganizationContext.SiteAdminClaim) != "true")
                {
                    SendToMaintenancePage();
                    return;
                }
            }
            await next(context);
        };

    /// <summary>What a check concluded about the circuit's principal.</summary>
    internal enum SessionCheck
    {
        /// <summary>The principal matches the row, or there is nothing to check.</summary>
        Current,
        /// <summary>The principal has more than the row grants. Sticky until the browser leaves.</summary>
        Stale,
        /// <summary>The row could not be read and the last good check is too old to lean on.</summary>
        Unverified,
    }

    /// <summary>
    /// Compares the circuit's principal with the user's row, at most once per
    /// <see cref="CheckInterval"/>. Stale is sticky: a stale circuit stays
    /// stale until the browser leaves it.
    /// </summary>
    internal async Task<SessionCheck> CheckAsync(CancellationToken ct)
    {
        if (_stale) return SessionCheck.Stale;
        var now = _clock.GetUtcNow().UtcDateTime;
        if (_lastCheckedAt is { } last && now - last < CheckInterval) return SessionCheck.Current;
        // A restore empties the users table while it runs; reading it now
        // would mistake that for a deleted account (#1269).
        if (_maintenance.IsActive) return SessionCheck.Current;

        var principal = (await _authState.GetAuthenticationStateAsync()).User;
        // Anonymous pages have no rights to lose. Without an organisation the
        // query filter matches nothing, so no page could act either.
        if (principal.Identity?.IsAuthenticated != true
            || _orgContext.CurrentOrganizationId is null
            || !int.TryParse(principal.FindFirstValue(HttpOrganizationContext.UserIdClaim), out var userId))
        {
            _lastCheckedAt = now;
            return SessionCheck.Current;
        }

        UserSnapshot? row;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            // Filtered: the circuit's own user, inside the organisation its claims name.
            row = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new UserSnapshot(u.Status, u.Role, u.IsSiteAdmin, u.CredentialsChangedAt))
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            // A hiccup should not take every open page down with it, so the
            // last good check (or the request that opened the circuit) is
            // trusted for a while. Past that the event is dropped (#1573).
            var trustedSince = _lastCheckedAt ?? _openedAt;
            if (now - trustedSince < FailClosedAfter)
            {
                _logger.LogWarning(ex, "Could not re-check the session for user {UserId}; trying again on the next event.", userId);
                return SessionCheck.Current;
            }
            _logger.LogWarning(ex, "Could not re-check the session for user {UserId} and the last check is too old; dropping the event.", userId);
            return SessionCheck.Unverified;
        }

        _lastCheckedAt = now;
        if (!StillMatches(principal, row))
        {
            _stale = true;
            _logger.LogInformation("Session for user {UserId} no longer matches the account; reloading the page.", userId);
        }
        return _stale ? SessionCheck.Stale : SessionCheck.Current;
    }

    /// <summary>
    /// Whether the principal a circuit connected with is still covered by the
    /// account: active, an organisation role no higher than the row's, no
    /// SiteAdmin claim the row has lost, and no credential change since the
    /// session signed in. A gain of rights (a promotion, a SiteAdmin flag
    /// granted) is left for the next page load: reloading for it would drop
    /// the click and any unsaved edits of someone who still has every right
    /// they are using (#1573). A credential change (a password reset or an
    /// email change stamps one) is compared with the session's sign-in moment, mirrored on the principal, not with when the circuit
    /// opened: a page opened just after a reset, while the cookie re-check
    /// was still throttled, would otherwise keep the old session (#1567). A
    /// principal without the claim (a cookie from before it existed) counts
    /// as older than any change, so it takes one trip through the refresh
    /// endpoint, which reads the real stamp and re-issues the cookie with it.
    /// </summary>
    internal static bool StillMatches(ClaimsPrincipal principal, UserSnapshot? row)
    {
        if (row is null || row.Status != UserStatus.Active) return false;
        var orgRoles = principal.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .Where(v => v != HttpOrganizationContext.SiteAdminRole)
            .ToList();
        // UserRole is declared in rank order: User, Editor, Admin.
        if (orgRoles.Count != 1 || !Enum.TryParse<UserRole>(orgRoles[0], out var claimedRole) || claimedRole > row.Role)
        {
            return false;
        }
        var claimsSiteAdmin = principal.FindFirstValue(HttpOrganizationContext.SiteAdminClaim) == "true"
            || principal.IsInRole(HttpOrganizationContext.SiteAdminRole);
        if (claimsSiteAdmin && !row.IsSiteAdmin) return false;
        return row.CredentialsChangedAt is not { } changedAt || EndpointHelpers.SignedInAt(principal) >= changedAt;
    }

    private void SendToRefresh()
    {
        var back = "/" + _navigation.ToBaseRelativePath(_navigation.Uri);
        ForceLoad($"{CookieSessionRevalidation.RefreshPath.TrimStart('/')}?return={Uri.EscapeDataString(back)}",
            "Could not send a stale session to the refresh page.");
    }

    /// <summary>
    /// Reloads the page the user is on. While maintenance mode is on the
    /// middleware answers that load with the maintenance page, and once the
    /// restore is over the same reload brings the page back.
    /// </summary>
    private void SendToMaintenancePage() =>
        ForceLoad(_navigation.Uri, "Could not reload a page into the maintenance page.");

    private void ForceLoad(string uri, string failureMessage)
    {
        if (Interlocked.Exchange(ref _redirected, 1) == 1) return;
        try
        {
            _navigation.NavigateTo(uri, forceLoad: true);
        }
        catch (Exception ex)
        {
            // The event is still dropped. Let a later event try again, so a
            // failed send does not leave the page dead.
            Interlocked.Exchange(ref _redirected, 0);
            _logger.LogWarning(ex, failureMessage);
        }
    }

    /// <summary>The columns the comparison reads from the user's row.</summary>
    internal sealed record UserSnapshot(UserStatus Status, UserRole Role, bool IsSiteAdmin, DateTime? CredentialsChangedAt);
}
