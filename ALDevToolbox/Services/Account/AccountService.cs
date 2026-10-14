using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.SingleTenant;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Result of a /login attempt. Discriminates the failure modes the UI cares
/// about so the page can render a specific (but never enumerating) message.
/// </summary>
public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    Pending,
    Disabled,
    LockedOut,
    RateLimited,
    /// <summary>
    /// Password verified but the user has 2FA enrolled — caller must redirect
    /// to <c>/login/challenge</c>. <c>LastLoginAt</c> is <em>not</em> stamped
    /// at this stage; <c>AuthService.CompleteMfaAsync</c> finalises the login
    /// once the second factor checks out.
    /// </summary>
    MfaRequired,
    /// <summary>
    /// Credentials were valid but the user's organisation requires Microsoft
    /// sign-in (<c>local_login_policy = EntraOnly</c>). SiteAdmins are exempt
    /// as break-glass access. See issue #552.
    /// </summary>
    LocalLoginDisabled,
}

/// <summary>
/// Outcome of a /signup attempt.
/// </summary>
public enum SignupOutcome
{
    PendingApproval,
    EmailAlreadyTaken,
    OrganizationProvisioned,
    /// <summary>
    /// A verified email at an org's claimed domain joined that org as an Active
    /// member because the org has <c>AutoJoinVerifiedDomainUsers</c> on. The
    /// caller signs the user in (like <see cref="OrganizationProvisioned"/>)
    /// rather than queuing them for approval.
    /// </summary>
    JoinedActive,
    Invalid,
}

/// <summary>
/// User-initiated lifecycle on the account: signup (pending or auto-provision)
/// and the signed-in self-service surface (change password, change display
/// name, delete account). Slimmed in #88: login + lockout live in
/// <see cref="AuthService"/>, admin user management in
/// <see cref="UserAdministrationService"/>, credential-recovery tokens in
/// <see cref="PasswordResetService"/>.
/// </summary>
/// <remarks>
/// Depends on <see cref="AuthService"/> for password hashing /
/// verification so the BCrypt work factor and the password policy live in
/// exactly one place.
/// </remarks>
public sealed class AccountService
{
    private static readonly Regex SlugRegex = new("^[a-z0-9-]+$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly AuthService _auth;
    private readonly SystemSettingsService _settings;
    private readonly ISingleTenantMode _singleTenant;
    private readonly ILogger<AccountService> _logger;
    private readonly TimeProvider _clock;
    private readonly IOpenIddictTokenManager _oauthTokens;

    public AccountService(AppDbContext db, AuthService auth, SystemSettingsService settings, ISingleTenantMode singleTenant, ILogger<AccountService> logger, TimeProvider clock, IOpenIddictTokenManager oauthTokens)
    {
        _db = db;
        _auth = auth;
        _settings = settings;
        _singleTenant = singleTenant;
        _logger = logger;
        _clock = clock;
        _oauthTokens = oauthTokens;
    }

    /// <summary>
    /// Refuses self-service organisation creation when the deployment runs in
    /// single-tenant mode. Joining a claimed domain or an existing org by slug
    /// stays open — onboarding goes through admin invites or domain routing.
    /// </summary>
    private void ThrowIfNewOrgBlocked()
    {
        if (!_singleTenant.IsEnabled) return;
        throw new PlanValidationException(new Dictionary<string, string>
        {
            ["Email"] = "This site doesn't allow creating new organisations. Ask an administrator to invite you.",
        });
    }

    /// <summary>
    /// Creates a pending user (and signup request) for an existing or
    /// brand-new organisation. Resolution order:
    /// <list type="number">
    ///   <item>If the email matches a domain claimed by an org via
    ///         <see cref="OrganizationAdminService.AddEmailDomainAsync"/>,
    ///         the user is routed to that org as Pending — typed slug and
    ///         org name are ignored (the admin-claimed domain is
    ///         authoritative, so a domain squatter can't spin up a fake
    ///         org).</item>
    ///   <item>Otherwise, if <paramref name="organizationSlug"/> matches an
    ///         existing org, the user is attached as Pending.</item>
    ///   <item>Otherwise a brand-new org is created with the supplied
    ///         <paramref name="organizationName"/>; the slug is derived from
    ///         that name (not the user's display name). The new admin is
    ///         auto-approved because no in-org admin exists to do the
    ///         approving (see <c>.design/auth-and-audit.md</c>).</item>
    /// </list>
    /// </summary>
    public async Task<(SignupOutcome Outcome, User? User, Organization? Organization)> SignupAsync(
        string email, string displayName, string password, string? organizationSlug,
        string? organizationName, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        ValidateEmail(email, errors);
        ValidateDisplayName(displayName, errors);
        AuthService.ValidatePassword(password, errors);
        var slug = (organizationSlug ?? string.Empty).Trim().ToLowerInvariant();
        if (slug.Length > 0 && !SlugRegex.IsMatch(slug))
        {
            errors["OrganizationSlug"] = "Slug must use lowercase letters, digits and hyphens only.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var normalised = AuthService.NormaliseEmail(email);

        // Site-wide email-domain allow-list. When the SiteAdmin has filled it
        // in, every signup route (new-org, slug-join, claimed-domain) must
        // come from a listed domain. Empty/null list = feature off. Run after
        // shape validation so users see "valid email" before "wrong domain".
        var allowed = await _settings.GetSignupAllowedDomainsAsync(ct);
        if (allowed is not null)
        {
            var domain = EmailAddress.DomainOf(normalised) ?? string.Empty;
            if (!allowed.Contains(domain))
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["Email"] = "Sign-ups from this email domain are not allowed on this site. Contact the site administrator if you believe this is a mistake.",
                });
            }
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        // Hashed before the lock below so it is not held across BCrypt.
        var passwordHash = _auth.HashPassword(password);

        // A double-clicked Sign up sends two posts for one address. Without
        // a lock both pass the probe below and each creates an organisation
        // before one loses on the email index. The transaction-scoped lock on
        // the address makes the second wait, then find the first's account.
        // The organisation and the user commit together, so a refusal or a
        // fault never leaves an empty organisation behind. See issue #1215.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({SignupLockClass}, hashtext({normalised}))", ct);

        var existingUser = await _db.Users
            // Fence category 1 (pre-auth routing): signup runs before any cookie exists, so
            // there is no org to filter by; the probe is pinned to the typed email.
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == normalised, ct);
        if (existingUser is not null)
        {
            return (SignupOutcome.EmailAlreadyTaken, null, null);
        }

        Organization org;
        bool createdNewOrg = false;

        var domainMatch = await ResolveOrganizationByEmailDomainAsync(normalised, ct);
        if (domainMatch is not null)
        {
            org = domainMatch;
        }
        else if (slug.Length == 0)
        {
            ThrowIfNewOrgBlocked();
            ValidateOrganizationName(organizationName, errors);
            if (errors.Count > 0) throw new PlanValidationException(errors);
            var trimmedName = organizationName!.Trim();
            org = await CreatePendingOrganizationAsync(Slugify(trimmedName), trimmedName, now, ct);
            createdNewOrg = true;
        }
        else
        {
            // Resolves which org a signup joins; no cookie exists yet. organizations
            // is the tenant table and carries no query filter, so nothing is bypassed.
            var match = await _db.Organizations
                .FirstOrDefaultAsync(o => o.Slug == slug, ct);
            if (match is null)
            {
                ThrowIfNewOrgBlocked();
                ValidateOrganizationName(organizationName, errors);
                if (errors.Count > 0) throw new PlanValidationException(errors);
                var trimmedName = organizationName!.Trim();
                org = await CreatePendingOrganizationAsync(slug, trimmedName, now, ct);
                createdNewOrg = true;
            }
            else
            {
                org = match;
            }
        }

        var user = new User
        {
            OrganizationId = org.Id,
            Email = normalised,
            PasswordHash = passwordHash,
            DisplayName = displayName.Trim(),
            // New orgs auto-approve their first signup: there's no admin
            // in-org to do the approving and we deliberately have no superuser
            // (see .design/auth-and-audit.md). Existing-org signups still
            // wait on a same-org admin via /admin/users.
            Role = createdNewOrg ? UserRole.Admin : UserRole.User,
            Status = createdNewOrg ? UserStatus.Active : UserStatus.Pending,
            CreatedAt = now,
        };
        _db.Users.Add(user);
        if (!await TrySaveNewUserAsync(ct))
        {
            return (SignupOutcome.EmailAlreadyTaken, null, null);
        }

        var request = new SignupRequest
        {
            OrganizationId = org.Id,
            UserId = user.Id,
            Email = normalised,
            RequestedAt = now,
            // Auto-approval still writes a SignupRequest so /admin/users keeps
            // a complete history; the decided fields point back at the user
            // themselves so the audit log is unambiguous.
            Decision = createdNewOrg ? SignupDecision.Approved : SignupDecision.Pending,
            DecidedAt = createdNewOrg ? now : null,
            DecidedByUserId = createdNewOrg ? user.Id : null,
        };
        _db.SignupRequests.Add(request);
        if (createdNewOrg)
        {
            org.IsPending = false;
        }
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("Signup recorded for {Email} into {OrgSlug} (new={New}).",
            normalised, org.Slug, createdNewOrg);

        return (createdNewOrg ? SignupOutcome.OrganizationProvisioned : SignupOutcome.PendingApproval,
                user, org);
    }

    /// <summary>
    /// Completes the email-first signup flow for a verified
    /// <see cref="PendingSignup"/>. Branches on whether the verified email's
    /// domain is claimed by an org:
    /// <list type="bullet">
    ///   <item>Claimed domain → join that org. Active <see cref="UserRole.User"/>
    ///         when the org has <see cref="OrganizationSettings.AutoJoinVerifiedDomainUsers"/>
    ///         on, otherwise Pending awaiting admin approval.</item>
    ///   <item>No claimed domain → create a brand-new org and activate the
    ///         visitor as its <see cref="UserRole.Admin"/>, exactly like the
    ///         new-org path of <see cref="SignupAsync"/>. There is no
    ///         "join an arbitrary org by slug" path here by design.</item>
    /// </list>
    /// The supplied <paramref name="pending"/> row must already be verified; the
    /// caller (the completion endpoint) re-checks that via
    /// <c>PendingSignupService.FindVerifiedAsync</c>. We stamp its
    /// <see cref="PendingSignup.CompletedAt"/> in the same save as the new user
    /// so the link/code can't be reused.
    /// </summary>
    public async Task<(SignupOutcome Outcome, User? User, Organization? Organization)> CompleteVerifiedSignupAsync(
        PendingSignup pending, string displayName, string password,
        string? organizationName, string? organizationSlug, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        ValidateDisplayName(displayName, errors);
        AuthService.ValidatePassword(password, errors);
        var slug = (organizationSlug ?? string.Empty).Trim().ToLowerInvariant();
        if (slug.Length > 0 && !SlugRegex.IsMatch(slug))
        {
            errors["OrganizationSlug"] = "Slug must use lowercase letters, digits and hyphens only.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var normalised = pending.Email;
        var now = _clock.GetUtcNow().UtcDateTime;
        // Hashed before the claim below so the row lock is not held across BCrypt.
        var passwordHash = _auth.HashPassword(password);

        // Single-use, claimed first: a double-clicked Create sends two posts
        // that both passed FindVerifiedAsync. The conditional update lets one
        // of them through; the other waits on the row lock, then matches no
        // row and reports the account as taken. Everything below runs in the
        // same transaction, so a refusal or a fault leaves no stray
        // organisation behind and the row claimable again. See issue #1215.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var claimed = await _db.PendingSignups
            .Where(p => p.Id == pending.Id && p.CompletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CompletedAt, now), ct);
        if (claimed == 0)
        {
            return (SignupOutcome.EmailAlreadyTaken, null, null);
        }

        // Race: a user for this email could have appeared between verification
        // and completion (e.g. an admin invite was accepted in the meantime).
        // Refuse rather than duplicate — mirrors the SignupAsync guard.
        var existingUser = await _db.Users.IgnoreQueryFilters()
            .AnyAsync(u => u.Email == normalised, ct);
        if (existingUser)
        {
            return (SignupOutcome.EmailAlreadyTaken, null, null);
        }

        Organization org;
        var createdNewOrg = false;
        bool autoActive;

        var domainMatch = await ResolveOrganizationByEmailDomainAsync(normalised, ct);
        if (domainMatch is not null)
        {
            org = domainMatch;
            // Verified ownership of a claimed domain joins active immediately
            // only when the org opted in; otherwise it's the historical
            // Pending-awaiting-approval behaviour.
            autoActive = await _db.OrganizationSettings.IgnoreQueryFilters()
                .Where(s => s.OrganizationId == org.Id)
                .Select(s => (bool?)s.AutoJoinVerifiedDomainUsers)
                .FirstOrDefaultAsync(ct) ?? false;
        }
        else
        {
            // No claimed domain → the only path is a brand-new org.
            ThrowIfNewOrgBlocked();
            ValidateOrganizationName(organizationName, errors);
            if (errors.Count > 0) throw new PlanValidationException(errors);
            var trimmedName = organizationName!.Trim();
            org = await CreatePendingOrganizationAsync(slug.Length > 0 ? slug : trimmedName, trimmedName, now, ct);
            createdNewOrg = true;
            autoActive = true;
        }

        var user = new User
        {
            OrganizationId = org.Id,
            Email = normalised,
            PasswordHash = passwordHash,
            DisplayName = displayName.Trim(),
            Role = createdNewOrg ? UserRole.Admin : UserRole.User,
            Status = autoActive ? UserStatus.Active : UserStatus.Pending,
            CreatedAt = now,
        };
        _db.Users.Add(user);
        if (!await TrySaveNewUserAsync(ct))
        {
            return (SignupOutcome.EmailAlreadyTaken, null, null);
        }

        var request = new SignupRequest
        {
            OrganizationId = org.Id,
            UserId = user.Id,
            Email = normalised,
            RequestedAt = now,
            Decision = autoActive ? SignupDecision.Approved : SignupDecision.Pending,
            DecidedAt = autoActive ? now : null,
            DecidedByUserId = autoActive ? user.Id : null,
        };
        _db.SignupRequests.Add(request);
        if (createdNewOrg) org.IsPending = false;

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // The claim above went straight to the database; mirror it on the
        // caller's copy so it reads as burned too.
        pending.CompletedAt = now;

        _logger.LogInformation("Verified signup completed for {Email} into {OrgSlug} (new={New}, active={Active}).",
            normalised, org.Slug, createdNewOrg, autoActive);

        var outcome = autoActive
            ? (createdNewOrg ? SignupOutcome.OrganizationProvisioned : SignupOutcome.JoinedActive)
            : SignupOutcome.PendingApproval;
        return (outcome, user, org);
    }

    /// <summary>
    /// The signed-in person's own account row, their organisation, and whether
    /// that organisation offers Microsoft sign-in — what the Account page opens
    /// with.
    /// </summary>
    /// <remarks>
    /// Runs inside the organisation query filter. The page used to read these
    /// with <c>IgnoreQueryFilters()</c>; neither bypass was doing anything —
    /// a person's own row is in their own org, and the organisations table
    /// carries no filter to escape. See #680 / #701.
    /// </remarks>
    public async Task<AccountOverview?> GetAccountOverviewAsync(int userId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return null;

        var organization = await _db.Organizations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == user.OrganizationId, ct);
        var entraEnabled = await _db.OrganizationSettings.AsNoTracking()
            .AnyAsync(s => s.OrganizationId == user.OrganizationId && s.EntraEnabled, ct);

        return new AccountOverview(user, organization, entraEnabled);
    }

    /// <summary>Self-service password change. Verifies the current password before applying the new one.</summary>
    public async Task ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        if (!_auth.VerifyPassword(currentPassword, user.PasswordHash))
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["CurrentPassword"] = "Current password is incorrect." });
        }
        var errors = new Dictionary<string, string>();
        AuthService.ValidatePassword(newPassword, errors, fieldName: "NewPassword");
        if (errors.Count > 0) throw new PlanValidationException(errors);
        user.PasswordHash = _auth.HashPassword(newPassword);
        var now = _clock.GetUtcNow().UtcDateTime;
        user.CredentialsChangedAt = now;
        // The assistants this user approved go too, so a client someone else
        // connected while holding the old password has to be approved again.
        // Filtered: this runs in the user's own signed-in request, so the
        // query filter already scopes the read to their organisation.
        var consents = await _db.OAuthConsents
            .Where(c => c.UserId == user.Id && c.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var consent in consents) consent.RevokedAt = now;
        await CommitCredentialChangeAsync(_db, _oauthTokens, user.Id, now, ct);
    }

    /// <summary>
    /// Saves a password change together with what it is supposed to take away
    /// besides the password itself: the user's live personal access tokens and
    /// every OAuth access token, refresh token and authorization code issued to
    /// them. Cookies are handled by the <c>credentials_changed_at</c> stamp the
    /// caller writes; <see cref="Endpoints.CookieSessionRevalidation"/> drops
    /// any cookie issued before it, and <see cref="OAuth.OAuthClaimsTransformer"/>
    /// refuses any OAuth access token issued before it. #675.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Saves the caller's pending changes (the new hash, the stamp) in the same
    /// transaction as the revocations, so a failure part way cannot leave the
    /// password changed with the old tokens still live. OpenIddict's token
    /// manager shares the scoped context, so its revocations join it.
    /// </para>
    /// <para>
    /// The personal-access-token query skips the org filter because a
    /// completed password reset runs with no session at all, and the reset
    /// path already read the user the same way. It is scoped to this one
    /// user's rows by id, so it cannot reach another account's tokens.
    /// OpenIddict's tables carry no org filter; the subject is the user id
    /// (<c>OAuthEndpoints.MapAuthorizeComplete</c>), as in
    /// <see cref="UserAdministrationService.DisableUserAsync"/>.
    /// </para>
    /// </remarks>
    internal static async Task CommitCredentialChangeAsync(
        AppDbContext db, IOpenIddictTokenManager oauthTokens, int userId, DateTime now, CancellationToken ct)
    {
        // Joins the caller's transaction when there is one (the email-change
        // confirmation holds a lock on the user row across the whole swap), and
        // opens its own otherwise.
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        // Fence category 4 (own-user lookup): pinned to p.UserId == userId, the
        // signed-in user or the user a reset token was issued to.
        var tokens = await db.PersonalAccessTokens.IgnoreQueryFilters()
            .Where(p => p.UserId == userId && p.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in tokens) token.RevokedAt = now;
        await db.SaveChangesAsync(ct);
        await foreach (var token in oauthTokens.FindBySubjectAsync(userId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct))
        {
            await oauthTokens.TryRevokeAsync(token, ct);
        }
        if (tx is not null) await tx.CommitAsync(ct);
    }

    /// <summary>Self-service display-name change.</summary>
    public async Task ChangeDisplayNameAsync(int userId, string newDisplayName, CancellationToken ct = default)
    {
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        var errors = new Dictionary<string, string>();
        ValidateDisplayName(newDisplayName, errors);
        if (errors.Count > 0) throw new PlanValidationException(errors);
        user.DisplayName = newDisplayName.Trim();
        await _db.SaveChangesAsync(ct);
    }

    private static void ValidateEmail(string? value, Dictionary<string, string> errors)
    {
        if (!EmailAddress.HasValidShape(value))
        {
            errors["Email"] = "Enter a valid email address.";
        }
    }

    private static void ValidateDisplayName(string? value, Dictionary<string, string> errors)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 2 or > 80)
        {
            errors["DisplayName"] = "Full name must be 2-80 characters.";
        }
    }

    private static void ValidateOrganizationName(string? value, Dictionary<string, string> errors)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 2 or > 80)
        {
            errors["OrganizationName"] = "Organisation name must be 2-80 characters.";
        }
    }

    /// <summary>
    /// First key of the advisory lock <see cref="SignupAsync"/> takes on the
    /// address being signed up; the second is the address's hash. Distinct
    /// from the build importer's lock classes.
    /// </summary>
    private const int SignupLockClass = 0x53_49_47_4E;

    /// <summary>
    /// Saves the pending new user, or returns false when another signup for
    /// the same address committed first (an invite accepted, say). The email
    /// probe can't see a request still in flight; the unique email index can.
    /// The caller returns without committing, so its transaction rolls back
    /// whatever organisation it created on the way.
    /// </summary>
    private async Task<bool> TrySaveNewUserAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex, "IX_users_email"))
        {
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task<Organization?> ResolveOrganizationByEmailDomainAsync(string normalisedEmail, CancellationToken ct)
    {
        var domain = EmailAddress.DomainOf(normalisedEmail);
        if (domain is null) return null;
        return await _db.OrganizationEmailDomains
            // Fence category 1 (pre-auth routing): maps the signup email's domain to one org,
            // pinned to d.Domain == domain. No cookie exists yet.
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(d => d.Domain == domain)
            .Select(d => d.Organization!)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<Organization> CreatePendingOrganizationAsync(string slug, string organizationName, DateTime now, CancellationToken ct)
    {
        var safeSlug = Slugify(slug);
        var candidate = safeSlug;
        var disambiguator = 1;
        // Slugs are unique deployment-wide, and organizations carries no tenant
        // query filter, so this existence-only probe sees every org as it must.
        while (await _db.Organizations.AnyAsync(o => o.Slug == candidate, ct))
        {
            disambiguator++;
            candidate = $"{safeSlug}-{disambiguator}";
        }
        var org = new Organization
        {
            Name = string.IsNullOrWhiteSpace(organizationName) ? candidate : organizationName,
            Slug = candidate,
            IsPending = true,
            IsSystem = false,
            CreatedAt = now,
        };
        _db.Organizations.Add(org);
        await _db.SaveChangesAsync(ct);

        // Seed the platform-default workspace files (.gitignore, the shared
        // ruleset, the README stub) so the new org has a starter library in
        // place when its admins create their first template. Idempotent: the
        // helper skips any row whose path already exists for the org.
        await PlatformOrganizationFileSeeder.EnsureForOrganizationAsync(_db, org.Id, now, ct);
        await _db.SaveChangesAsync(ct);
        return org;
    }

    private static string Slugify(string value)
    {
        var lower = value.Trim().ToLowerInvariant();
        var chars = lower.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var hyphenated = new string(chars).Trim('-');
        while (hyphenated.Contains("--")) hyphenated = hyphenated.Replace("--", "-");
        return hyphenated.Length == 0 ? "org" : hyphenated;
    }
}

/// <summary>What the Account page needs about the signed-in person up front.</summary>
public sealed record AccountOverview(User User, Organization? Organization, bool OrganizationEntraEnabled);
