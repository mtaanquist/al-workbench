using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.SingleTenant;

/// <summary>
/// First-run seeding for a single-tenant deployment. Because
/// <c>SINGLE_TENANT_MODE</c> blocks self-service org creation, the one
/// organisation must come up already configured — named after the company,
/// claiming its email domain(s), and (when domains are claimed) auto-joining
/// verified domain users. This runs once, in the same fresh-database window
/// as the bootstrap admin (<see cref="Endpoints.StartupTasks"/>), writing
/// rows directly via <see cref="AppDbContext"/> because there is no
/// authenticated request scope at startup to satisfy
/// <c>OrganizationAdminService.RequireOrganizationId()</c>.
///
/// <para>
/// The single tenant <em>is</em> the system org (see <c>.design/deployment.md</c>),
/// so the org is resolved by <see cref="Organization.IsSystem"/>. Email
/// domains are globally unique; an already-claimed domain is skipped with a
/// warning rather than throwing. Auto-join only affects the verified
/// (SMTP) signup path — see <see cref="OrganizationSettings.AutoJoinVerifiedDomainUsers"/>.
/// </para>
/// </summary>
public static class SingleTenantSeeder
{
    // Mirrors OrganizationAdminService.DomainRegex so seeded domains match
    // what the admin UI would accept.
    private static readonly Regex DomainRegex =
        new(@"^[a-z0-9]([a-z0-9\-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9\-]*[a-z0-9])?)+$", RegexOptions.Compiled);

    /// <summary>
    /// Applies the supplied identity and email domains to the system org and
    /// saves. Null/blank <paramref name="orgName"/> or <paramref name="orgSlug"/>
    /// leave the existing values untouched. Returns silently if there's no
    /// system org (a malformed database that startup will have already flagged).
    /// </summary>
    public static async Task SeedAsync(
        AppDbContext db,
        string? orgName,
        string? orgSlug,
        string? emailDomainsCsv,
        DateTime now,
        ILogger logger,
        CancellationToken ct)
    {
        var org = await db.Organizations
            .FirstOrDefaultAsync(o => o.IsSystem, ct);
        if (org is null) return;

        var changed = false;

        var name = orgName?.Trim();
        if (!string.IsNullOrEmpty(name) && org.Name != name)
        {
            org.Name = name;
            changed = true;
        }

        var slug = orgSlug?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(slug) && org.Slug != slug)
        {
            org.Slug = slug;
            changed = true;
        }

        var domains = ParseDomains(emailDomainsCsv);
        var seededAnyDomain = false;
        foreach (var domain in domains)
        {
            if (!DomainRegex.IsMatch(domain) || domain.Length > 253)
            {
                logger.LogWarning("SINGLE_TENANT_EMAIL_DOMAINS: skipping invalid domain {Domain}.", domain);
                continue;
            }
            // Domains are globally unique. On a fresh database nothing is
            // claimed yet, but stay defensive in case the seed list overlaps.
            // Fence category 3 (startup seeder): existence-only, pinned to d.Domain == domain.
            var taken = await db.OrganizationEmailDomains.IgnoreQueryFilters()
                .AnyAsync(d => d.Domain == domain, ct);
            if (taken)
            {
                logger.LogWarning("SINGLE_TENANT_EMAIL_DOMAINS: domain {Domain} is already claimed; skipping.", domain);
                continue;
            }
            db.OrganizationEmailDomains.Add(new OrganizationEmailDomain
            {
                OrganizationId = org.Id,
                Domain = domain,
                CreatedAt = now,
            });
            seededAnyDomain = true;
            changed = true;
        }

        // Claiming a domain only helps onboarding if verified domain users
        // join straight through; flip the org setting on so the maintainer
        // doesn't have to. (Verified/SMTP path only — see the entity doc.)
        if (seededAnyDomain)
        {
            // Fence category 3 (startup seeder): pinned to s.OrganizationId == org.Id.
            var settings = await db.OrganizationSettings.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.OrganizationId == org.Id, ct);
            if (settings is null)
            {
                settings = new OrganizationSettings { OrganizationId = org.Id, UpdatedAt = now };
                db.OrganizationSettings.Add(settings);
            }
            if (!settings.AutoJoinVerifiedDomainUsers)
            {
                settings.AutoJoinVerifiedDomainUsers = true;
                settings.UpdatedAt = now;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Single-tenant seed applied to org {OrgId}: name={Name}, slug={Slug}, domains-seeded={DomainCount}.",
                org.Id, org.Name, org.Slug, domains.Count(d => DomainRegex.IsMatch(d)));
        }
    }

    /// <summary>Splits the env var on commas, whitespace and semicolons; normalises like the admin UI (lowercase, strip leading <c>@</c>).</summary>
    private static IReadOnlyList<string> ParseDomains(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
        return csv
            .Split(new[] { ',', ';', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.ToLowerInvariant().TrimStart('@'))
            .Distinct()
            .ToList();
    }
}
