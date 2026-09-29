using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Tools;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Answers "does this request need a recent second factor for this tool?"
/// against the live organisation row. The cookie carries the same set as a
/// claim for the page-route gate (no DB hit per navigation); this service is
/// for the surfaces that authenticate with a bearer token and so carry no such
/// claim: the MCP call filter and the OAuth consent step. Scoped, like every
/// service holding the context.
/// </summary>
public sealed class StepUpPolicy
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _org;
    private readonly TimeProvider _clock;

    public StepUpPolicy(AppDbContext db, IOrganizationContext org, TimeProvider clock)
    {
        _db = db;
        _org = org;
        _clock = clock;
    }

    /// <summary>The tools the current organisation has marked for step-up. Empty outside a request.</summary>
    public async Task<HashSet<ToolKey>> StepUpToolsAsync(CancellationToken ct = default)
    {
        var orgId = _org.CurrentOrganizationId;
        if (orgId is null) return new HashSet<ToolKey>();
        var names = await _db.Organizations.AsNoTracking()
            .Where(o => o.Id == orgId.Value)
            .Select(o => o.StepUpTools)
            .FirstOrDefaultAsync(ct);
        return ToolCatalog.ParseKeys(names);
    }

    /// <summary>True when the organisation wants a recent second factor before <paramref name="tool"/> is used.</summary>
    public async Task<bool> RequiresStepUpAsync(ToolKey tool, CancellationToken ct = default) =>
        (await StepUpToolsAsync(ct)).Contains(tool);

    /// <summary>True when the organisation has marked any tool at all.</summary>
    public async Task<bool> AnyStepUpToolAsync(CancellationToken ct = default) =>
        (await StepUpToolsAsync(ct)).Count > 0;

    /// <summary>Whether the principal's last strong sign-in or step-up is within <see cref="StepUpAuth.Window"/>.</summary>
    public bool IsFresh(ClaimsPrincipal? principal) =>
        StepUpAuth.IsFresh(principal, _clock.GetUtcNow().UtcDateTime);
}
