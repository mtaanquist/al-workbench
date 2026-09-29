using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Data.Configurations;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Services.Account;

/// <summary>The identity claims we consume from a validated Entra id_token.</summary>
/// <param name="TenantId">The <c>tid</c> claim — the Entra tenant the account lives in.</param>
/// <param name="ObjectId">The <c>oid</c> claim — the stable per-tenant account id we key links on.</param>
/// <param name="Email">The <c>preferred_username</c>/<c>email</c> claim. Display + matching only, never the link key.</param>
/// <param name="DisplayName">The <c>name</c> claim.</param>
/// <param name="EmailVerified">
/// The <c>xms_edov</c> claim ("email domain owner verified") — true only when
/// Microsoft has confirmed the tenant owns the email's domain. Without it the
/// address is attacker-influenceable (a B2B guest's UPN is their own external
/// address), so <see cref="EntraSignInService.CompleteAsync"/> refuses to bind
/// the token to an existing local account on the strength of the email alone.
/// See the "unverified email claim" rule in <c>.design/auth-and-audit.md</c>.
/// </param>
public sealed record EntraTokenIdentity(
    string TenantId, string ObjectId, string? Email, string? DisplayName, bool EmailVerified = false);

/// <summary>Resolved app-registration credentials for one challenge.</summary>
/// <param name="OrganizationId">The org the sign-in is routed to.</param>
/// <param name="ClientId">The client id to challenge with (org's own, else deployment-wide).</param>
/// <param name="ConfigSource"><c>"org"</c> or <c>"system"</c> — which row holds the secret.</param>
public sealed record EntraChallengeConfig(int OrganizationId, string ClientId, string ConfigSource);

public enum EntraCompletionOutcome
{
    /// <summary>Signed in; <see cref="EntraCompletionResult.User"/> is set.</summary>
    Success,
    /// <summary>New account created, waiting for an org admin's approval.</summary>
    PendingApproval,
    /// <summary>The token's tenant isn't on any org's allow-list (or the org opted out).</summary>
    TenantNotAllowed,
    /// <summary>More than one org allows the tenant and the email domain doesn't decide it.</summary>
    Ambiguous,
    /// <summary>The token carried no usable email and no existing link matched.</summary>
    EmailMissing,
    /// <summary>A local account with this email exists in a different organisation.</summary>
    EmailTakenElsewhere,
    /// <summary>
    /// A local account with this email exists in the resolved organisation, but
    /// nothing proves the Entra tenant owns the address (no <c>xms_edov</c>, and
    /// the domain isn't one the organisation has verified). Binding on an
    /// unverified claim would be account takeover, so the sign-in is refused.
    /// </summary>
    EmailNotVerified,
    /// <summary>
    /// The Entra identity was linked to a different local account between this
    /// sign-in's lookup and its save. The winner keeps the identity; this
    /// sign-in is refused rather than 500ing (issue #754).
    /// </summary>
    IdentityTakenElsewhere,
    /// <summary>Matched an account that is still pending approval.</summary>
    AccountPending,
    /// <summary>Matched an account that has been disabled.</summary>
    AccountDisabled,
}

public sealed record EntraCompletionResult(EntraCompletionOutcome Outcome, User? User = null);

/// <summary>
/// Microsoft (Entra ID) sign-in: challenge routing, post-callback account
/// resolution, and link/JIT provisioning. See issue #552 for the design.
///
/// <para><b>Tenant-isolation note.</b> The service has two halves and they
/// sit on opposite sides of the fence. The <i>sign-in</i> half
/// (<see cref="IsSignInAvailableAsync"/>, <see cref="GetLoginSurfaceAsync"/>,
/// <see cref="ResolveChallengeAsync"/>, <see cref="GetClientSecretAsync"/>,
/// <see cref="CompleteAsync"/>) runs <b>pre-auth</b> — the login page and the
/// OIDC callback both execute before any cookie exists, so there is no current
/// organisation to filter by and these reads call <c>IgnoreQueryFilters()</c>
/// deliberately, the same sanctioned category as login/signup/password-reset
/// in <c>.design/auth-and-audit.md</c>. Every one of them exists to route one
/// sign-in to exactly one organisation; none returns another org's data to a
/// caller.</para>
///
/// <para>The <i>account-linking</i> half
/// (<see cref="ResolveChallengeForCurrentOrgAsync"/>,
/// <see cref="ListLinksAsync"/>, <see cref="LinkAsync"/>,
/// <see cref="UnlinkAsync"/>) runs under an authenticated request from
/// <c>/account</c>, so it stays <b>inside</b> the query filter: a signed-in
/// user can only ever see and change links belonging to their own
/// organisation. The single exception is documented at its call site in
/// <see cref="LinkAsync"/>.</para>
///
/// <para><b>The tid check is the security boundary.</b> With a multi-tenant
/// app registration, any Microsoft work account in the world produces a
/// cryptographically valid token. <see cref="CompleteAsync"/> therefore only
/// proceeds when the token's tenant id appears on the resolved organisation's
/// <c>entra_allowed_tenant_ids</c> — that list, not the signature, is what
/// keeps strangers out.</para>
/// </summary>
public sealed class EntraSignInService
{
    /// <summary>Provider discriminator stamped on <see cref="UserExternalLogin"/> rows.</summary>
    public const string ProviderName = "entra";

    /// <summary>
    /// The refusal shown when a Microsoft identity is already connected to
    /// someone else. Both paths in <see cref="LinkAsync"/> raise it - the
    /// pre-check and the unique-index backstop below it - so it lives here
    /// rather than being written out twice and drifting.
    /// </summary>
    private const string IdentityTakenMessage = "That Microsoft account is already connected to a different user.";

    private readonly AppDbContext _db;
    private readonly AuthService _auth;
    private readonly IOrganizationContext _orgContext;
    private readonly IDataProtector _orgSecretProtector;
    private readonly IDataProtector _systemSecretProtector;
    private readonly TimeProvider _clock;
    private readonly ILogger<EntraSignInService> _logger;

    public EntraSignInService(
        AppDbContext db,
        AuthService auth,
        IOrganizationContext orgContext,
        IDataProtectionProvider protectionProvider,
        TimeProvider clock,
        ILogger<EntraSignInService> logger)
    {
        _db = db;
        _auth = auth;
        _orgContext = orgContext;
        _orgSecretProtector = protectionProvider.CreateProtector(OrganizationAdminService.EntraClientSecretProtectionPurpose);
        _systemSecretProtector = protectionProvider.CreateProtector(SystemSettingsService.EntraClientSecretProtectionPurpose);
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Whether the login page should offer "Sign in with Microsoft" at all:
    /// true when at least one organisation has it enabled. Pre-auth: the
    /// login page has no organisation yet, and "any org" is the question.
    /// </summary>
    public Task<bool> IsSignInAvailableAsync(CancellationToken ct = default) =>
        // Fence category 1 (pre-auth routing): deployment-wide question asked by the
        // anonymous login page.
        _db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(s => s.EntraEnabled, ct);

    /// <summary>
    /// What the login page should render. Password entry stays offered as
    /// long as any organisation still allows it; when every org is
    /// Microsoft-only the password form collapses behind a disclosure
    /// (SiteAdmin break-glass still needs it to exist). Pre-auth: rendered
    /// for an anonymous visitor, so the counts are deployment-wide.
    /// </summary>
    public async Task<(bool EntraAvailable, bool PasswordPrimary)> GetLoginSurfaceAsync(CancellationToken ct = default)
    {
        var entra = await IsSignInAvailableAsync(ct);
        if (!entra) return (false, true);
        // organizations carries no query filter (it is the tenant root), so
        // this read needs no IgnoreQueryFilters.
        var orgCount = await _db.Organizations.AsNoTracking().CountAsync(ct);
        // Fence category 1 (pre-auth routing): deployment-wide count for the anonymous login page.
        var entraOnlyCount = await _db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(s => s.LocalLoginPolicy == Domain.ValueObjects.LocalLoginPolicy.EntraOnly, ct);
        return (true, orgCount > entraOnlyCount);
    }

    /// <summary>
    /// Picks the organisation (and app registration) to challenge with.
    /// With an email we route via the claimed-domain table; without one we
    /// can only proceed when exactly one org has Entra enabled (the
    /// single-tenant shape). Returns a login-page error code otherwise:
    /// <c>entra-email-needed</c>, <c>entra-not-configured</c>. Pre-auth:
    /// picking the organisation is the whole job, so there isn't one to
    /// filter by yet.
    /// </summary>
    public async Task<(EntraChallengeConfig? Config, string? ErrorCode)> ResolveChallengeAsync(
        string? email, CancellationToken ct = default)
    {
        int? orgId = null;
        var domain = ExtractDomain(email);
        if (domain is not null)
        {
            // Fence category 1 (pre-auth routing): routes one sign-in to one org; pinned to the
            // typed email's domain.
            orgId = await _db.OrganizationEmailDomains.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.Domain == domain)
                .Select(d => (int?)d.OrganizationId)
                .FirstOrDefaultAsync(ct);
        }

        if (orgId is null)
        {
            // Fence category 1 (pre-auth routing): with no email, the challenge is only offered
            // when exactly one org has Entra enabled.
            var enabledOrgIds = await _db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.EntraEnabled)
                .Select(s => s.OrganizationId)
                .Take(2)
                .ToListAsync(ct);
            if (enabledOrgIds.Count == 1) orgId = enabledOrgIds[0];
            else if (enabledOrgIds.Count > 1) return (null, "entra-email-needed");
            else return (null, "entra-not-configured");
        }

        // Fence category 1 (pre-auth routing): pinned to the org resolved above.
        var settings = await _db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == orgId && s.EntraEnabled)
            .Select(s => new { s.OrganizationId, s.EntraClientId })
            .FirstOrDefaultAsync(ct);
        if (settings is null) return (null, "entra-not-configured");

        if (settings.EntraClientId is not null)
        {
            return (new EntraChallengeConfig(settings.OrganizationId, settings.EntraClientId, "org"), null);
        }
        var systemClientId = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.EntraClientId)
            .FirstOrDefaultAsync(ct);
        return systemClientId is null
            ? (null, "entra-not-configured")
            : (new EntraChallengeConfig(settings.OrganizationId, systemClientId, "system"), null);
    }

    /// <summary>
    /// Decrypts the client secret for the code exchange. Called from the
    /// OIDC handler's token event so the secret never travels through the
    /// correlation cookie. Null when none is stored (PKCE-only registration).
    /// Pre-auth: the code exchange happens inside the OIDC handler, before
    /// any cookie exists; the org id comes from the signed challenge
    /// properties, so the read is already pinned to one organisation.
    /// </summary>
    public async Task<string?> GetClientSecretAsync(int organizationId, string configSource, CancellationToken ct = default)
    {
        string? ciphertext;
        IDataProtector protector;
        if (configSource == "org")
        {
            // Fence category 1 (pre-auth routing): inside the OIDC handler; pinned to
            // s.OrganizationId == organizationId from the signed challenge properties.
            ciphertext = await _db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.OrganizationId == organizationId)
                .Select(s => s.EntraClientSecretEncrypted)
                .FirstOrDefaultAsync(ct);
            protector = _orgSecretProtector;
        }
        else
        {
            ciphertext = await _db.SystemSettings.AsNoTracking()
                .Where(s => s.Id == 1)
                .Select(s => s.EntraClientSecretEncrypted)
                .FirstOrDefaultAsync(ct);
            protector = _systemSecretProtector;
        }
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try
        {
            return protector.Unprotect(ciphertext);
        }
        catch (CryptographicException ex)
        {
            // Loud: a lost key ring silently breaks sign-in otherwise.
            _logger.LogError(ex, "Failed to decrypt Entra client secret (source={Source}, org={OrgId}).", configSource, organizationId);
            return null;
        }
    }

    /// <summary>
    /// Post-callback resolution: existing link, then email match, then JIT
    /// provisioning through the same approval machinery as verified signup.
    /// Every attempt (success or refusal) lands in <c>login_attempts</c> so
    /// the audit trail covers federated logins too. Pre-auth: this runs in
    /// the OIDC callback before any cookie is minted, so every read below
    /// bypasses the filter to route the sign-in to exactly one organisation.
    /// </summary>
    public async Task<EntraCompletionResult> CompleteAsync(EntraTokenIdentity token, string ip, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var tid = token.TenantId.Trim().ToLowerInvariant();
        var email = AuthService.NormaliseEmail(token.Email ?? string.Empty);

        // 1. Existing link — the fast path for every returning user.
        var link = await FindLinkAsync(tid, token.ObjectId, ct);
        if (link is not null)
        {
            // Fence category 1 (pre-auth routing): pinned to the linked user's own org.
            var linkedOrgAllows = await _db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(s => s.OrganizationId == link.User!.OrganizationId
                    && s.EntraEnabled && s.EntraAllowedTenantIds.Contains(tid), ct);
            if (!linkedOrgAllows)
            {
                return await RefuseAsync(EntraCompletionOutcome.TenantNotAllowed, email, ip, now,
                    $"linked org no longer allows tenant {tid}", ct);
            }
            return await FinishStatusCheckedAsync(link.User!, link, email, ip, now, ct);
        }

        // 2. No link yet: which orgs would accept this tenant at all?
        // Fence category 1 (pre-auth routing): which orgs allow this Entra tenant at all —
        // the routing decision itself, made before any cookie exists.
        var candidates = await _db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.EntraEnabled && s.EntraAllowedTenantIds.Contains(tid))
            .Select(s => new { s.OrganizationId, s.AutoJoinVerifiedDomainUsers })
            .ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return await RefuseAsync(EntraCompletionOutcome.TenantNotAllowed, email, ip, now,
                $"tenant {tid} on no org's allow-list", ct);
        }

        if (email.Length == 0)
        {
            return await RefuseAsync(EntraCompletionOutcome.EmailMissing, email, ip, now,
                $"token from tenant {tid} carried no email", ct);
        }

        // Email-domain routing decides when several orgs share the tenant.
        var domain = ExtractDomain(email);
        // Fence category 1 (pre-auth routing): email-domain tiebreak, pinned to the domain.
        var domainOrgId = domain is null ? null : await _db.OrganizationEmailDomains.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.Domain == domain)
            .Select(d => (int?)d.OrganizationId)
            .FirstOrDefaultAsync(ct);
        var resolved = candidates.FirstOrDefault(c => c.OrganizationId == domainOrgId)
            ?? (candidates.Count == 1 ? candidates[0] : null);
        if (resolved is null)
        {
            return await RefuseAsync(EntraCompletionOutcome.Ambiguous, email, ip, now,
                $"tenant {tid} allowed by {candidates.Count} orgs and the email domain doesn't decide", ct);
        }

        // 3. Match an existing local account by verified email, in-org only.
        // Fence category 1 (pre-auth routing): pinned to the token's verified email; the
        // org match is enforced on the next line.
        var user = await _db.Users.IgnoreQueryFilters()
            .Include(u => u.Organization)
            .FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is not null && user.OrganizationId != resolved.OrganizationId)
        {
            return await RefuseAsync(EntraCompletionOutcome.EmailTakenElsewhere, email, ip, now,
                $"email registered in org {user.OrganizationId}, sign-in routed to org {resolved.OrganizationId}", ct);
        }
        if (user is not null)
        {
            // The email alone is not proof of anything: in an allow-listed
            // partner or customer tenant an admin controls what lands in
            // preferred_username/mail, and a B2B guest's UPN is their own
            // external address. Bind only on positive evidence that the
            // tenant owns the address — Microsoft's own xms_edov claim, or a
            // domain this organisation has verified.
            var domainVerifiedByOrg = domainOrgId is not null && domainOrgId == resolved.OrganizationId;
            if (!token.EmailVerified && !domainVerifiedByOrg)
            {
                _logger.LogWarning(
                    "Refused to link Entra tenant {Tid} to an existing account: the email domain {Domain} is unverified for that tenant (no xms_edov, domain not claimed by org {OrgId}).",
                    tid, domain ?? "(none)", resolved.OrganizationId);
                return await RefuseAsync(EntraCompletionOutcome.EmailNotVerified, email, ip, now,
                    $"tenant {tid} did not prove ownership of domain {domain ?? "(none)"}", ct);
            }
            // Saved on its own, ahead of FinishStatusCheckedAsync's bookkeeping,
            // so a lost race is settled while the link insert is the only thing
            // pending — otherwise the failed save takes the login attempt with it
            // and re-entering would record it twice (issue #754).
            var inserted = AddLink(user.Id, tid, token.ObjectId, email, now);
            var settled = await SaveLinkSettlingRaceAsync(inserted, user.Id, tid, token.ObjectId, ct);
            if (settled is null)
            {
                return await RefuseAsync(EntraCompletionOutcome.IdentityTakenElsewhere, email, ip, now,
                    $"identity {tid}/{token.ObjectId} was claimed by another account mid-sign-in", ct);
            }
            if (ReferenceEquals(settled, inserted))
            {
                // Only when this request is the one that made the link; the
                // adopted-winner case is already logged by the settler.
                _logger.LogInformation("Linked Entra identity {Tid}/{Oid} to existing user {Email} on first sign-in.", tid, token.ObjectId, email);
            }
            return await FinishStatusCheckedAsync(user, settled, email, ip, now, ct);
        }

        // 4. JIT provisioning, through the same approval machinery as the
        // verified signup flow (auto-join only when the email domain is
        // claimed by the resolved org and it opted in).
        var autoActive = resolved.OrganizationId == domainOrgId && resolved.AutoJoinVerifiedDomainUsers;
        user = new User
        {
            OrganizationId = resolved.OrganizationId,
            Email = email,
            // Entra-only account: no usable password. A random BCrypt hash
            // satisfies the NOT NULL column and can never verify.
            PasswordHash = _auth.HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            DisplayName = string.IsNullOrWhiteSpace(token.DisplayName) ? email : token.DisplayName.Trim(),
            Role = UserRole.User,
            Status = autoActive ? UserStatus.Active : UserStatus.Pending,
            CreatedAt = now,
            LastLoginAt = autoActive ? now : null,
        };
        // Load the nav so a Success return can feed BuildIdentity, which
        // stamps org-name / MCP / tool claims from the Organization row.
        user.Organization = await _db.Organizations
            .FirstAsync(o => o.Id == resolved.OrganizationId, ct);
        _db.Users.Add(user);
        // The user, its signup request and its link go in together. Saving the
        // user first left an Entra-only account with neither behind whenever the
        // link lost a race, and the next sign-in then treated that orphan as the
        // "existing local account" of step 3 (issue #754). Both rows reference
        // the user through the navigation property rather than user.Id, which is
        // still 0 until this save assigns it.
        var signup = new SignupRequest
        {
            OrganizationId = resolved.OrganizationId,
            User = user,
            Email = email,
            RequestedAt = now,
            Decision = autoActive ? SignupDecision.Approved : SignupDecision.Pending,
            DecidedAt = autoActive ? now : null,
        };
        _db.SignupRequests.Add(signup);
        var jitLink = AddLink(user.Id, tid, token.ObjectId, email, now, lastLogin: autoActive ? now : null);
        jitLink.User = user;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (DbErrors.IsUniqueViolation(ex, UserExternalLoginConfiguration.IdentityIndexName))
        {
            // Nothing committed: all three inserts were one transaction. Detach
            // them so they are not retried by the refusal's own save. The winner
            // is always a different account here, because this user does not
            // exist yet.
            foreach (var orphan in new object[] { jitLink, signup, user })
            {
                _db.Entry(orphan).State = EntityState.Detached;
            }
            _logger.LogWarning(ex,
                "Entra identity {Tid}/{Oid} was claimed by another account while provisioning {Email}; no account was created.",
                tid, token.ObjectId, email);
            return await RefuseAsync(EntraCompletionOutcome.IdentityTakenElsewhere, email, ip, now,
                $"identity {tid}/{token.ObjectId} was claimed by another account mid-provisioning", ct);
        }
        // Self-approval is recorded after the fact: decided_by_user_id is a plain
        // column with no navigation, so it cannot be fixed up by the save above.
        if (autoActive)
        {
            signup.DecidedByUserId = user.Id;
        }
        RecordAttempt(email, ip, autoActive, now);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("JIT-provisioned {Email} into org {OrgId} via Entra tenant {Tid} (active={Active}).",
            email, resolved.OrganizationId, tid, autoActive);
        return autoActive
            ? new EntraCompletionResult(EntraCompletionOutcome.Success, user)
            : new EntraCompletionResult(EntraCompletionOutcome.PendingApproval, user);
    }

    /// <summary>
    /// Challenge config for the self-service "Connect Microsoft account" flow
    /// on /account. Runs under an authenticated request, so the organisation
    /// is simply the caller's own and every read stays inside the query
    /// filter. Null when the org hasn't enabled Entra or no registration is
    /// usable.
    /// </summary>
    public async Task<EntraChallengeConfig?> ResolveChallengeForCurrentOrgAsync(CancellationToken ct = default)
    {
        var organizationId = RequireOrganizationId();
        var settings = await _db.OrganizationSettings.AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && s.EntraEnabled)
            .Select(s => new { s.EntraClientId })
            .FirstOrDefaultAsync(ct);
        if (settings is null) return null;
        if (settings.EntraClientId is not null)
        {
            return new EntraChallengeConfig(organizationId, settings.EntraClientId, "org");
        }
        var systemClientId = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Id == 1).Select(s => s.EntraClientId).FirstOrDefaultAsync(ct);
        return systemClientId is null ? null : new EntraChallengeConfig(organizationId, systemClientId, "system");
    }

    /// <summary>
    /// The user's linked Microsoft identities, for the /account page.
    /// Filtered: <c>user_external_logins</c> is org-scoped through its User
    /// principal, so this can only return links from the caller's own org.
    /// The provider predicate matters since issue #621 put GitHub account links
    /// in the same table: those are authorisation, not a way to sign in, and
    /// must never appear as a Microsoft account.
    /// </summary>
    public Task<List<UserExternalLogin>> ListLinksAsync(int userId, CancellationToken ct = default) =>
        _db.UserExternalLogins.AsNoTracking()
            .Where(l => l.UserId == userId && l.Provider == ProviderName)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Whether <paramref name="token"/> is the Microsoft identity already
    /// linked to <paramref name="userId"/>, from a tenant the user's
    /// organisation still allows. The step-up callback uses it to make sure a
    /// forced re-sign-in came back as the same person, not as any account
    /// the tenant would accept, and that the tenant allow-list (the actual
    /// security boundary for Microsoft sign-in) applies to step-up as it does
    /// to sign-in. Filtered: the caller is signed in.
    /// </summary>
    public async Task<bool> IsLinkedAsync(int userId, EntraTokenIdentity token, CancellationToken ct = default)
    {
        var tid = token.TenantId.Trim().ToLowerInvariant();
        var linked = await _db.UserExternalLogins.AsNoTracking().AnyAsync(
            l => l.Provider == ProviderName && l.Issuer == tid
                && l.Subject == token.ObjectId && l.UserId == userId, ct);
        if (!linked) return false;
        return await _db.OrganizationSettings.AsNoTracking()
            .AnyAsync(s => s.EntraEnabled && s.EntraAllowedTenantIds.Contains(tid), ct);
    }

    /// <summary>
    /// Links a Microsoft identity to an already-signed-in user (the
    /// "Connect Microsoft account" flow). The token's tenant must be on the
    /// user's org allow-list — same boundary as sign-in — and the identity
    /// must not already belong to someone else. Field-keyed errors surface
    /// on /account.
    /// </summary>
    public async Task LinkAsync(int userId, EntraTokenIdentity token, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var tid = token.TenantId.Trim().ToLowerInvariant();
        // Filtered: the caller is signed in, so the only user they can reach
        // is one in their own organisation.
        var user = await _db.Users.FirstAsync(u => u.Id == userId, ct);

        var allowed = await _db.OrganizationSettings.AsNoTracking()
            .AnyAsync(s => s.OrganizationId == user.OrganizationId
                && s.EntraEnabled && s.EntraAllowedTenantIds.Contains(tid), ct);
        if (!allowed)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["EntraLink"] = "That Microsoft account's organisation isn't on your organisation's allowed list. Ask your admin to add the tenant on the Identity page.",
            });
        }

        if (await _db.UserExternalLogins.AnyAsync(
                l => l.Provider == ProviderName && l.Issuer == tid
                    && l.Subject == token.ObjectId && l.UserId == userId, ct))
        {
            return; // already linked - saving again is free
        }

        // (provider, issuer, subject) is unique across the whole deployment,
        // so a Microsoft identity already claimed by a user in another
        // organisation can't be linked here either. This is the one read in
        // the linking half that has to see past the filter, and it is
        // deliberately existence-only: it projects a bool, never a row, so
        // nothing about the other organisation reaches the caller. It is the
        // fast, specific refusal - the catch on the save below is what
        // actually guarantees it, for the identity that gets claimed
        // elsewhere between this read and that save (issue #736).
        var takenElsewhere = await _db.UserExternalLogins.IgnoreQueryFilters()
            .AnyAsync(l => l.Provider == ProviderName && l.Issuer == tid && l.Subject == token.ObjectId, ct);
        if (takenElsewhere)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["EntraLink"] = IdentityTakenMessage,
            });
        }

        var link = AddLink(userId, tid, token.ObjectId,
            AuthService.NormaliseEmail(token.Email ?? string.Empty), now, lastLogin: null);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (DbErrors.IsUniqueViolation(ex, UserExternalLoginConfiguration.IdentityIndexName))
        {
            // Lost the race with a link made elsewhere after the check above.
            // The row is always a fresh insert here, so forgetting it is
            // enough to leave the context usable for the rest of the request.
            // The message stays the pre-check's: that the stranger is in
            // another organisation is not something to tell this user.
            _db.Entry(link).State = EntityState.Detached;
            _logger.LogWarning(ex,
                "User {UserId} tried to connect Entra identity {Tid}/{Oid}, which was claimed by another account before the link could be saved.",
                userId, tid, token.ObjectId);
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["EntraLink"] = IdentityTakenMessage,
            });
        }
        _logger.LogInformation("User {UserId} connected Entra identity {Tid}/{Oid} from /account.", userId, tid, token.ObjectId);
    }

    /// <summary>
    /// Removes one of the user's own links. Refused when it's the last link
    /// and the org is Microsoft-only — disconnecting would lock the user out
    /// (SiteAdmins keep password break-glass, so they may).
    /// </summary>
    public async Task UnlinkAsync(int userId, int linkId, CancellationToken ct = default)
    {
        // Filtered: a signed-in user can only reach a link whose owner is in
        // their own organisation, and the UserId predicate narrows that to
        // themselves. A link id from another org simply doesn't resolve.
        var user = await _db.Users.FirstAsync(u => u.Id == userId, ct);
        var link = await _db.UserExternalLogins
            .FirstOrDefaultAsync(l => l.Id == linkId && l.UserId == userId && l.Provider == ProviderName, ct);
        if (link is null) return;

        var linkCount = await _db.UserExternalLogins
            .CountAsync(l => l.UserId == userId && l.Provider == ProviderName, ct);
        if (linkCount == 1 && await _auth.IsLocalLoginDisabledAsync(user, ct))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["EntraLink"] = "Your organisation signs in with Microsoft only, so disconnecting your last Microsoft account would lock you out.",
            });
        }

        _db.UserExternalLogins.Remove(link);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("User {UserId} disconnected Entra identity {Tid}/{Oid}.", userId, link.Issuer, link.Subject);
    }

    /// <summary>
    /// The acting user's organisation, for the account-linking half. Throws
    /// rather than silently filtering to org 0 when called off-request.
    /// </summary>
    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException(
            "No organisation in scope; the Microsoft account-linking flow only runs under an authenticated request.");

    /// <summary>
    /// The one read that resolves an Entra identity to its local link. Shared by
    /// the returning-user fast path and by the race settlement below so the
    /// tenant-fence bypass stays a single reviewable call site.
    /// </summary>
    /// <remarks>
    /// Fence category 1 (pre-auth routing): OIDC callback, no cookie yet; pinned to the
    /// token's (provider, issuer, subject).
    /// </remarks>
    private Task<UserExternalLogin?> FindLinkAsync(string tid, string objectId, CancellationToken ct) =>
        _db.UserExternalLogins.IgnoreQueryFilters()
            .Include(l => l.User!).ThenInclude(u => u.Organization)
            .FirstOrDefaultAsync(l => l.Provider == ProviderName && l.Issuer == tid && l.Subject == objectId, ct);

    /// <summary>
    /// Saves a freshly added link and settles the race where the same Entra
    /// identity is linked by another request in between (issue #754). Returns the
    /// link to carry on with: the one just inserted, or the winner when it turns
    /// out to belong to <paramref name="userId"/> anyway, which is what a
    /// double-submitted callback looks like. Returns null when the identity now
    /// belongs to somebody else, which the caller refuses on.
    /// </summary>
    private async Task<UserExternalLogin?> SaveLinkSettlingRaceAsync(
        UserExternalLogin link, int userId, string tid, string objectId, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return link;
        }
        catch (DbUpdateException ex)
            when (DbErrors.IsUniqueViolation(ex, UserExternalLoginConfiguration.IdentityIndexName))
        {
            // Same shape as LinkAsync's catch (#736): the row is always a fresh
            // insert here, so detaching it leaves the context usable for the
            // reload and for whatever the caller does next.
            _db.Entry(link).State = EntityState.Detached;
            var winner = await FindLinkAsync(tid, objectId, ct);
            if (winner is not null && winner.UserId == userId)
            {
                _logger.LogInformation(
                    "Entra identity {Tid}/{Oid} was linked to user {UserId} by a concurrent request; continuing with that link.",
                    tid, objectId, userId);
                return winner;
            }
            _logger.LogWarning(ex,
                "Entra identity {Tid}/{Oid} was claimed by another account before user {UserId} could link it.",
                tid, objectId, userId);
            return null;
        }
    }

    private UserExternalLogin AddLink(int userId, string tid, string objectId, string email, DateTime now, DateTime? lastLogin = null)
    {
        var link = new UserExternalLogin
        {
            UserId = userId,
            Provider = ProviderName,
            Issuer = tid,
            Subject = objectId,
            DisplayIdentity = email,
            CreatedAt = now,
            LastLoginAt = lastLogin,
        };
        _db.UserExternalLogins.Add(link);
        return link;
    }

    private async Task<EntraCompletionResult> FinishStatusCheckedAsync(
        User user, UserExternalLogin link, string email, string ip, DateTime now, CancellationToken ct)
    {
        if (user.Status == UserStatus.Pending)
        {
            RecordAttempt(email, ip, succeeded: false, now);
            await _db.SaveChangesAsync(ct);
            return new EntraCompletionResult(EntraCompletionOutcome.AccountPending, user);
        }
        if (user.Status == UserStatus.Disabled)
        {
            RecordAttempt(email, ip, succeeded: false, now);
            await _db.SaveChangesAsync(ct);
            return new EntraCompletionResult(EntraCompletionOutcome.AccountDisabled, user);
        }
        user.LastLoginAt = now;
        link.LastLoginAt = now;
        RecordAttempt(email, ip, succeeded: true, now);
        await _db.SaveChangesAsync(ct);
        return new EntraCompletionResult(EntraCompletionOutcome.Success, user);
    }

    private async Task<EntraCompletionResult> RefuseAsync(
        EntraCompletionOutcome outcome, string email, string ip, DateTime now,
        string reason, CancellationToken ct)
    {
        RecordAttempt(email, ip, succeeded: false, now);
        await _db.SaveChangesAsync(ct);
        _logger.LogWarning("Entra sign-in refused ({Outcome}): {Reason}", outcome, reason);
        return new EntraCompletionResult(outcome);
    }

    private void RecordAttempt(string email, string ip, bool succeeded, DateTime now) =>
        _db.LoginAttempts.Add(new LoginAttempt
        {
            Email = email,
            Ip = ip,
            Succeeded = succeeded,
            Timestamp = now,
        });

    private static string? ExtractDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1) return null;
        return email[(at + 1)..].Trim().ToLowerInvariant();
    }
}
