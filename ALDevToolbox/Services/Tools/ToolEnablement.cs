using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Tools;

/// <summary>
/// "Is this tool switched on for the organisation acting right now?" — the one
/// question every surface that behaves differently for a disabled tool asks,
/// answered the same way the sidebar and the route gate answer it: a tool the
/// SiteAdmin switched off site-wide is off for everyone, and an organisation
/// can narrow that further by switching one off for itself.
///
/// <para>The sidebar (<c>NavMenu.razor</c>) and <see cref="ToolAccessGate"/>
/// read the <c>org_disabled_tools</c> claim inline because they run per render
/// and per request and must not touch the DB. This service exists for the
/// callers that are not a rendered page — chiefly the MCP tools, whose PAT and
/// OAuth principals carry no such claim (see
/// <c>PatAuthenticationHandler</c> and <c>OAuthClaimsTransformer</c>, which
/// mount the org and role claims but not this one). When the claim is there it
/// wins; otherwise the acting organisation's own
/// <see cref="Domain.Entities.Organization.DisabledTools"/> is read once and
/// held for the rest of the scope.</para>
/// </summary>
public sealed class ToolEnablement
{
    private readonly IToolAvailability _availability;
    private readonly IHttpContextAccessor _http;
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;

    private readonly TimeProvider _clock;

    // Resolved at most once per scope: a request that asks about two tools
    // should not read the organisation row twice.
    private HashSet<ToolKey>? _orgDisabled;
    private HashSet<ToolKey>? _orgStepUp;
    private TimeSpan? _orgStepUpWindow;

    public ToolEnablement(
        IToolAvailability availability,
        IHttpContextAccessor http,
        AppDbContext db,
        IOrganizationContext orgContext,
        TimeProvider clock)
    {
        _availability = availability;
        _http = http;
        _db = db;
        _orgContext = orgContext;
        _clock = clock;
    }

    // ---- Step-up: "does this tool want a recent second factor?" -------------
    // The second per-org question about a tool, answered from the same row.
    // See ".design/auth-and-audit.md", "Step-up for sensitive tools".

    /// <summary>The tools the acting organisation has marked for step-up, read once per scope.</summary>
    public async Task<HashSet<ToolKey>> StepUpToolsAsync(CancellationToken ct = default)
    {
        await LoadStepUpAsync(ct);
        return _orgStepUp!;
    }

    /// <summary>How long a confirmation keeps a session fresh in the acting organisation.</summary>
    public async Task<TimeSpan> StepUpWindowAsync(CancellationToken ct = default)
    {
        await LoadStepUpAsync(ct);
        return _orgStepUpWindow!.Value;
    }

    private async Task LoadStepUpAsync(CancellationToken ct)
    {
        if (_orgStepUp is not null) return;
        if (_orgContext.CurrentOrganizationId is not { } orgId)
        {
            _orgStepUp = new HashSet<ToolKey>();
            _orgStepUpWindow = Services.Account.StepUpAuth.DefaultWindow;
            return;
        }
        // Always the row, never the claim: the claim refreshes with the cookie
        // and this answer decides whether a credential gets spent.
        var row = await _db.Organizations.AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => new { o.StepUpTools, o.StepUpWindowMinutes })
            .FirstOrDefaultAsync(ct);
        _orgStepUp = ToolCatalog.ParseKeys(row?.StepUpTools);
        _orgStepUpWindow = Services.Account.StepUpAuth.WindowOf(row?.StepUpWindowMinutes);
    }

    /// <summary>True when the acting organisation wants a recent second factor before <paramref name="key"/> is used.</summary>
    public async Task<bool> RequiresStepUpAsync(ToolKey key, CancellationToken ct = default) =>
        (await StepUpToolsAsync(ct)).Contains(key);

    /// <summary>True when the acting organisation has marked any tool at all.</summary>
    public async Task<bool> AnyStepUpAsync(CancellationToken ct = default) =>
        (await StepUpToolsAsync(ct)).Count > 0;

    /// <summary>Whether the principal's last second factor is within the acting organisation's window.</summary>
    public async Task<bool> IsFreshAsync(ClaimsPrincipal? user, CancellationToken ct = default) =>
        Services.Account.StepUpAuth.IsFresh(user, _clock.GetUtcNow().UtcDateTime, await StepUpWindowAsync(ct));

    /// <summary>
    /// The action-level check for the code paths that spend a stored
    /// credential from inside a page a route gate cannot see (a dialog on a
    /// different tool's page, an admin page): refuses with
    /// <see cref="StepUpRequiredException"/> when the acting organisation
    /// marked <paramref name="key"/> and the request's cookie session is not
    /// fresh. Bearer sessions are the MCP filter's business (a PAT is refused
    /// there, an OAuth session was checked at consent), and a call with no
    /// HTTP request at all (a worker) passes: there is nobody to ask.
    /// </summary>
    public async Task EnsureStepUpAsync(ToolKey key, CancellationToken ct = default)
    {
        var user = _http.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return;
        if (user.HasClaim(c => c.Type == "pat_id") || user.FindFirst(EndpointHelpers.DisabledToolsClaim) is null) return;
        if (!await RequiresStepUpAsync(key, ct)) return;
        if (await IsFreshAsync(user, ct)) return;
        throw new StepUpRequiredException(key);
    }

    /// <summary>
    /// True when <paramref name="key"/> is available to the organisation
    /// <paramref name="user"/> belongs to. The overload for a rendered page,
    /// which has the signed-in principal from the cascading auth state and so
    /// needs neither an <c>HttpContext</c> nor a query - the same two lines the
    /// sidebar runs.
    /// </summary>
    public bool IsEnabled(ToolKey key, ClaimsPrincipal? user) =>
        _availability.IsSiteEnabled(key) && !EndpointHelpers.ReadDisabledTools(user).Contains(key);

    /// <summary>
    /// True when <paramref name="key"/> is available to the acting
    /// organisation. Site state wins; the per-org opt-out only narrows it.
    /// </summary>
    public async Task<bool> IsEnabledAsync(ToolKey key, CancellationToken ct = default)
    {
        if (!_availability.IsSiteEnabled(key)) return false;
        return !(await OrgDisabledAsync(ct)).Contains(key);
    }

    private async Task<HashSet<ToolKey>> OrgDisabledAsync(CancellationToken ct)
    {
        if (_orgDisabled is not null) return _orgDisabled;

        // A cookie principal carries the claim, so the browser path costs
        // nothing. The claim may legitimately be empty (nothing switched off),
        // hence the presence check rather than a check on the parsed set.
        var user = _http.HttpContext?.User;
        if (user?.FindFirst(EndpointHelpers.DisabledToolsClaim) is not null)
        {
            return _orgDisabled = EndpointHelpers.ReadDisabledTools(user);
        }

        if (_orgContext.CurrentOrganizationId is not { } orgId)
        {
            return _orgDisabled = new HashSet<ToolKey>();
        }

        // Organization carries no query filter to escape (see the note in
        // AppDbContext.OnModelCreating), so the row is pinned by id here - and
        // that id comes from the authenticated principal, never from input.
        var row = await _db.Organizations
            .AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => new { o.DisabledTools, o.McpEnabled })
            .FirstOrDefaultAsync(ct);
        if (row is null) return _orgDisabled = new HashSet<ToolKey>();

        var disabled = ToolCatalog.ParseDisabled(row.DisabledTools);
        // MCP keeps its own flag on the organisation; folding it in here means
        // callers ask one question for every tool, as the claim lets them.
        if (!row.McpEnabled) disabled.Add(ToolKey.Mcp);
        return _orgDisabled = disabled;
    }
}

/// <summary>
/// Raised by <see cref="ToolEnablement.EnsureStepUpAsync"/>. A
/// <see cref="PlanValidationException"/> so the dialogs and pages that
/// already render field-keyed errors show it inline, with a message that
/// says what to do: confirm on the step-up page and come back.
/// </summary>
public sealed class StepUpRequiredException : PlanValidationException
{
    public ToolKey Tool { get; }

    public StepUpRequiredException(ToolKey tool)
        : base(new Dictionary<string, string>
        {
            ["StepUp"] = $"Confirm it's you before using {ToolCatalog.Describe(tool).Name}: open {Endpoints.StepUpEndpoints.Path} in a new tab, confirm, then reload this page and try again.",
        })
    {
        Tool = tool;
    }
}
