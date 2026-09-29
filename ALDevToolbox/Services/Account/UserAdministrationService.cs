using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Admin-of-the-org actions on existing user accounts: approving / rejecting
/// pending signups, disabling / enabling accounts, role flips, and the bulk
/// versions of each. Carved out of the original AccountService in #88 so the
/// admin user-management surface lives in one place and the security-
/// sensitive auth code (login, tokens) doesn't have to scroll past it.
/// </summary>
/// <remarks>
/// "Last active admin" is enforced consistently across every demote / disable
/// path so an org can never be left without a way back in. Every method
/// returns either successfully or via <see cref="PlanValidationException"/>
/// with field-keyed errors the UI can render inline.
/// </remarks>
public sealed class UserAdministrationService
{
    public static readonly TimeSpan EmailChangeTokenLifetime = TimeSpan.FromHours(24);

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IOpenIddictTokenManager _oauthTokens;

    public UserAdministrationService(AppDbContext db, TimeProvider clock, IOpenIddictTokenManager oauthTokens)
    {
        _db = db;
        _clock = clock;
        _oauthTokens = oauthTokens;
    }

    /// <summary>
    /// Approves a pending signup. The user transitions to <c>Active</c>; the
    /// signup request gets stamped with <paramref name="decidedByUserId"/>.
    /// Caller's organisation must match the request's organisation.
    /// </summary>
    public async Task ApproveSignupAsync(int signupRequestId, int decidedByUserId, int actingOrgId, CancellationToken ct = default)
    {
        var req = await LoadSignupRequestAsync(signupRequestId, actingOrgId, ct);
        if (req.Decision != SignupDecision.Pending || req.UserId is null)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Decision"] = "This request has already been decided." });
        }
        // Fence category 4 (explicitly scoped lookups): the signup request was already
        // loaded against actingOrgId, so both ids below belong to the admin's own org.
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == req.UserId.Value, ct);
        user.Status = UserStatus.Active;
        req.Decision = SignupDecision.Approved;
        req.DecidedAt = _clock.GetUtcNow().UtcDateTime;
        req.DecidedByUserId = decidedByUserId;
        // The org carrying the new user becomes non-pending the moment its
        // first signup is approved. Subsequent admin login triggers the
        // first-time seed (see Program.cs bootstrap path).
        // Pinned to req.OrganizationId, which LoadSignupRequestAsync already
        // checked equals actingOrgId.
        var org = await _db.Organizations.FirstAsync(o => o.Id == req.OrganizationId, ct);
        org.IsPending = false;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Rejects a pending signup. The user row is deleted; the request keeps the audit trail.</summary>
    public async Task RejectSignupAsync(int signupRequestId, int decidedByUserId, int actingOrgId, CancellationToken ct = default)
    {
        var req = await LoadSignupRequestAsync(signupRequestId, actingOrgId, ct);
        if (req.Decision != SignupDecision.Pending)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Decision"] = "This request has already been decided." });
        }
        if (req.UserId is int userId)
        {
            // Fence category 4 (explicitly scoped user-id lookup): req.UserId belongs to the
            // signup request already checked against actingOrgId.
            var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
            // Pending users rarely have audit rows pointing at them, but
            // anonymise defensively rather than gamble on the rejection
            // failing at SaveChanges.
            await _db.AnonymiseActorAsync(user.Id, ct);
            _db.Users.Remove(user);
        }
        req.Decision = SignupDecision.Rejected;
        req.DecidedAt = _clock.GetUtcNow().UtcDateTime;
        req.DecidedByUserId = decidedByUserId;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Disables an active user (or pending one). Last admin protection enforced.
    /// Disabling is the "this person has left" action, so it also cuts every
    /// way back in: the cookie (via <see cref="User.CredentialsChangedAt"/>,
    /// which <c>CookieSessionRevalidation</c> compares against the session's
    /// start), the user's personal access tokens, their OAuth consents and the
    /// access and refresh tokens issued under them. The PAT and OAuth request
    /// paths already refuse a non-active user on every call, so this is belt
    /// and braces there; it also means re-enabling the account later does not
    /// quietly bring old tokens back to life.
    /// </summary>
    public async Task DisableUserAsync(int userId, int actingOrgId, CancellationToken ct = default)
    {
        var user = await LoadUserAsync(userId, actingOrgId, ct);
        if (user.Role == UserRole.Admin && user.Status == UserStatus.Active
            && await CountActiveAdminsAsync(actingOrgId, ct) <= 1)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["LastAdmin"] = "You can't disable the last active admin in this organisation." });
        }
        var now = _clock.GetUtcNow().UtcDateTime;
        user.Status = UserStatus.Disabled;
        user.CredentialsChangedAt = now;
        // Both filtered writes: LoadUserAsync pinned the user to the acting org,
        // and the admin endpoint passes the request's own org as actingOrgId, so
        // the query filter and the pin agree. Not crossing the fence here is
        // deliberate (CLAUDE.md); a caller acting for another org would find
        // these no-op, which the filter would then be right about.
        await _db.PersonalAccessTokens
            .Where(p => p.UserId == userId && p.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.RevokedAt, now), ct);
        await _db.OAuthConsents
            .Where(c => c.UserId == userId && c.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, now), ct);
        await _db.SaveChangesAsync(ct);
        // OpenIddict stores Subject = the user id (OAuthEndpoints.MapAuthorizeComplete).
        await foreach (var token in _oauthTokens.FindBySubjectAsync(userId.ToString(), ct))
        {
            await _oauthTokens.TryRevokeAsync(token, ct);
        }
    }

    /// <summary>Re-enables a disabled user without further admin approval.</summary>
    public async Task EnableUserAsync(int userId, int actingOrgId, CancellationToken ct = default)
    {
        var user = await LoadUserAsync(userId, actingOrgId, ct);
        if (user.Status == UserStatus.Disabled) user.Status = UserStatus.Active;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Admin correction of a user's full name (the <see cref="User.DisplayName"/>
    /// column). Scoped to the acting admin's organisation via
    /// <see cref="LoadUserAsync"/>; same 2–80 character rule as the
    /// self-service change on the Account page.
    /// </summary>
    public async Task ChangeDisplayNameAsync(int userId, string newDisplayName, int actingOrgId, CancellationToken ct = default)
    {
        var user = await LoadUserAsync(userId, actingOrgId, ct);
        var trimmed = newDisplayName?.Trim() ?? string.Empty;
        if (trimmed.Length is < 2 or > 80)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["DisplayName"] = "Full name must be 2-80 characters." });
        }
        user.DisplayName = trimmed;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Promotes / demotes a user. Last-admin protection on any demotion away from Admin (User or Editor).</summary>
    public async Task ChangeRoleAsync(int userId, UserRole newRole, int actingOrgId, CancellationToken ct = default)
    {
        var user = await LoadUserAsync(userId, actingOrgId, ct);
        if (user.Role == UserRole.Admin && newRole != UserRole.Admin
            && user.Status == UserStatus.Active
            && await CountActiveAdminsAsync(actingOrgId, ct) <= 1)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["LastAdmin"] = "You can't demote the last active admin in this organisation." });
        }
        user.Role = newRole;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Bulk variant of <see cref="DisableUserAsync"/>. Each user is processed
    /// in turn — a last-admin guard on one row surfaces as a per-row failure
    /// instead of halting the whole batch. See <c>.design/milestones.md</c>
    /// Milestone 20.
    /// </summary>
    public Task<BulkActionResult> BulkDisableUsersAsync(IReadOnlyList<int> userIds, int actingOrgId, CancellationToken ct = default) =>
        BulkAsync(userIds, id => DisableUserAsync(id, actingOrgId, ct), actingOrgId, ct);

    /// <summary>Bulk variant of <see cref="EnableUserAsync"/>.</summary>
    public Task<BulkActionResult> BulkEnableUsersAsync(IReadOnlyList<int> userIds, int actingOrgId, CancellationToken ct = default) =>
        BulkAsync(userIds, id => EnableUserAsync(id, actingOrgId, ct), actingOrgId, ct);

    /// <summary>
    /// Bulk variant of <see cref="ChangeRoleAsync"/>. Each role flip carries
    /// the same last-admin guard — failures bubble up per user so an admin can
    /// see exactly which row blocked the operation.
    /// </summary>
    public Task<BulkActionResult> BulkChangeRoleAsync(IReadOnlyList<int> userIds, UserRole newRole, int actingOrgId, CancellationToken ct = default) =>
        BulkAsync(userIds, id => ChangeRoleAsync(id, newRole, actingOrgId, ct), actingOrgId, ct);

    /// <summary>
    /// Shared shape for the bulk-action trio (#81). Iterates ids in input
    /// order (with duplicates collapsed), runs the per-user delegate, and
    /// turns a <see cref="PlanValidationException"/> into a row failure
    /// rather than halting the whole batch.
    /// </summary>
    private async Task<BulkActionResult> BulkAsync(
        IReadOnlyList<int> userIds, Func<int, Task> op, int actingOrgId, CancellationToken ct)
    {
        var succeeded = new List<int>();
        var failures = new List<BulkActionFailure>();
        foreach (var id in userIds.Distinct())
        {
            try
            {
                await op(id);
                succeeded.Add(id);
            }
            catch (PlanValidationException ex)
            {
                failures.Add(new BulkActionFailure(id, await LookupDisplayNameAsync(id, actingOrgId, ct), ex.Errors.First().Value));
            }
        }
        return new BulkActionResult(userIds.Count, succeeded, failures);
    }

    /// <summary>
    /// Resolves a user's display name for a bulk-action failure row, scoped to
    /// the acting admin's own organisation. Ids come from the org-filtered grid
    /// today, but scoping here means a cross-org id can never pair a "not found"
    /// error with another org's real display name. See #489.
    /// </summary>
    private async Task<string> LookupDisplayNameAsync(int userId, int actingOrgId, CancellationToken ct)
    {
        // Fence category 4 (explicitly scoped org-id lookup): pinned to
        // u.OrganizationId == actingOrgId (see #489).
        var name = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == userId && u.OrganizationId == actingOrgId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct);
        return name ?? $"#{userId}";
    }

    private async Task<int> CountActiveAdminsAsync(int orgId, CancellationToken ct)
    {
        // Fence category 4 (explicitly scoped org-id lookup): pinned to u.OrganizationId == orgId.
        return await _db.Users.IgnoreQueryFilters()
            .CountAsync(u => u.OrganizationId == orgId
                             && u.Role == UserRole.Admin
                             && u.Status == UserStatus.Active, ct);
    }

    private async Task<User> LoadUserAsync(int userId, int actingOrgId, CancellationToken ct)
    {
        // Fence category 4 (explicitly scoped user-id lookup): the row is refused below
        // unless its OrganizationId equals actingOrgId.
        var user = await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.OrganizationId != actingOrgId)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["UserId"] = "User not found in this organisation." });
        }
        return user;
    }

    private async Task<SignupRequest> LoadSignupRequestAsync(int id, int actingOrgId, CancellationToken ct)
    {
        var req = await _db.SignupRequests
            // Fence category 4 (explicitly scoped lookup): the row is refused below unless its
            // OrganizationId equals actingOrgId.
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (req is null || req.OrganizationId != actingOrgId)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["RequestId"] = "Request not found in this organisation." });
        }
        return req;
    }

    /// <summary>
    /// Stamps a pending email change on the user row and issues a confirmation
    /// token (purpose <see cref="TokenPurpose.EmailChangeConfirm"/>, 24-hour
    /// lifetime). Returns the plaintext token — the endpoint emails it to the
    /// <em>new</em> address. The actual swap happens in
    /// <see cref="ConfirmEmailChangeAsync"/> once that mailbox responds.
    /// </summary>
    public async Task<string> RequestEmailChangeAsync(int targetUserId, string newEmail, int actingOrgId, int actingUserId, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var normalised = (newEmail ?? string.Empty).Trim().ToLowerInvariant();
        if (!EmailAddress.HasValidShape(normalised))
        {
            errors["NewEmail"] = "Enter a valid email address.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var user = await LoadUserAsync(targetUserId, actingOrgId, ct);
        if (user.Id == actingUserId)
        {
            // Admins changing their own email via the admin route is too
            // accident-prone (they'd be racing themselves on the confirmation).
            // Self-service email change is out of scope this milestone — once
            // shipped, this user would use /account.
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["NewEmail"] = "You can't change your own email from the admin page. Ask another admin or a SiteAdmin."
            });
        }
        if (string.Equals(user.Email, normalised, StringComparison.Ordinal))
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["NewEmail"] = "That's already this user's email." });
        }
        // Fence category 6 (existence-only uniqueness probe): emails are unique
        // deployment-wide; projects a bool, never a row.
        var taken = await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == normalised && u.Id != user.Id, ct);
        if (taken)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["NewEmail"] = "Another account already uses that email." });
        }

        var (raw, hash) = TokenIssuer.Issue();
        var now = _clock.GetUtcNow().UtcDateTime;
        // Burn any still-valid prior change tokens for this user. Without this,
        // a previous recipient's link would still validate against the newly-
        // stored pending_email and silently confirm an address they never had
        // access to.
        // Fence category 4 (explicitly scoped user-id lookup): pinned to t.UserId == user.Id,
        // the user this admin just loaded from their own org.
        var stale = await _db.PasswordResetTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == user.Id
                        && t.Purpose == TokenPurpose.EmailChangeConfirm
                        && t.ConsumedAt == null
                        && t.ExpiresAt > now)
            .ToListAsync(ct);
        foreach (var s in stale) s.ConsumedAt = now;

        user.PendingEmail = normalised;
        user.PendingEmailAt = now;
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = hash,
            Purpose = TokenPurpose.EmailChangeConfirm,
            CreatedAt = now,
            ExpiresAt = now + EmailChangeTokenLifetime,
        });
        await _db.SaveChangesAsync(ct);
        return raw;
    }

    /// <summary>
    /// Consumes an <see cref="TokenPurpose.EmailChangeConfirm"/> token: swaps
    /// <see cref="User.Email"/> for <see cref="User.PendingEmail"/>, clears the
    /// pending fields, and returns the user. Returns <c>null</c> for an
    /// invalid / expired / consumed token without revealing which.
    /// </summary>
    public async Task<User?> ConfirmEmailChangeAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var hash = TokenIssuer.Sha256Hex(rawToken);
        var now = _clock.GetUtcNow().UtcDateTime;
        // Fence category 1 (pre-auth routing): the confirmation link is opened from an email,
        // possibly signed out; pinned to the token hash.
        var row = await _db.PasswordResetTokens.IgnoreQueryFilters()
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == TokenPurpose.EmailChangeConfirm, ct);
        if (row is null || row.ConsumedAt is not null || row.ExpiresAt <= now || row.User is null)
        {
            return null;
        }
        var user = row.User;
        if (string.IsNullOrEmpty(user.PendingEmail))
        {
            // Pending email was cleared (admin reverted, account modified):
            // burn the token but don't change anything.
            row.ConsumedAt = now;
            await _db.SaveChangesAsync(ct);
            return null;
        }
        // Race: another account may have grabbed the address since the token
        // was issued.
        // Fence category 6 (existence-only uniqueness probe): projects a bool, never a row.
        var stolen = await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == user.PendingEmail && u.Id != user.Id, ct);
        if (stolen)
        {
            user.PendingEmail = null;
            user.PendingEmailAt = null;
            row.ConsumedAt = now;
            await _db.SaveChangesAsync(ct);
            return null;
        }
        user.Email = user.PendingEmail;
        user.PendingEmail = null;
        user.PendingEmailAt = null;
        row.ConsumedAt = now;
        await _db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>
    /// SiteAdmin-only break-glass: clears every 2FA factor for the target
    /// user (TOTP secret + recovery codes + every passkey) and flips both
    /// MFA flags off. The user can re-enroll after their next sign-in.
    /// </summary>
    public async Task ResetMfaAsync(int targetUserId, CancellationToken ct = default)
    {
        // Fence category 2 (SiteAdmin cross-org console): break-glass MFA reset, reached only
        // from /site-admin/users; every read is pinned to targetUserId.
        var user = await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == targetUserId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["UserId"] = "User not found." });

        // Category 2, pinned to s.UserId == targetUserId.
        var totp = await _db.UserTotpSecrets.IgnoreQueryFilters()
            .Where(s => s.UserId == targetUserId).ToListAsync(ct);
        _db.UserTotpSecrets.RemoveRange(totp);
        // Category 2, pinned to c.UserId == targetUserId.
        var codes = await _db.UserRecoveryCodes.IgnoreQueryFilters()
            .Where(c => c.UserId == targetUserId).ToListAsync(ct);
        _db.UserRecoveryCodes.RemoveRange(codes);
        // Category 2, pinned to p.UserId == targetUserId.
        var passkeys = await _db.UserPasskeys.IgnoreQueryFilters()
            .Where(p => p.UserId == targetUserId).ToListAsync(ct);
        _db.UserPasskeys.RemoveRange(passkeys);
        user.TotpEnabled = false;
        user.EmailMfaEnabled = false;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Everything the Administration → Users page lists: pending signups (with
    /// the account each one created), the org's non-pending members, which of
    /// them sign in with Microsoft, and the invites still outstanding.
    /// </summary>
    /// <remarks>
    /// Reads run inside the organisation query filter — the page's own copies of
    /// these queries used <c>IgnoreQueryFilters()</c> with an explicit
    /// <c>OrganizationId == actingOrgId</c> predicate, which is what the filter
    /// already applies for the signed-in admin's own org. See #680 / #701.
    /// </remarks>
    public async Task<UserAdministrationPage> GetAdministrationPageAsync(int actingOrgId, CancellationToken ct = default)
    {
        var pendingRequests = await _db.SignupRequests.AsNoTracking()
            .Where(r => r.OrganizationId == actingOrgId && r.Decision == SignupDecision.Pending)
            .OrderBy(r => r.RequestedAt)
            .ToListAsync(ct);

        var active = await _db.Users.AsNoTracking()
            .Where(u => u.OrganizationId == actingOrgId && u.Status != UserStatus.Pending)
            .OrderBy(u => u.Status == UserStatus.Active ? 0 : 1)
            .ThenBy(u => u.DisplayName)
            .ToListAsync(ct);

        // Entra only: the "signs in with Microsoft" badge on the Users tab must
        // not light up for a GitHub account link (issue #621) in the same table.
        var entraLinked = (await _db.UserExternalLogins.AsNoTracking()
            .Where(l => l.Provider == EntraSignInService.ProviderName)
            .Select(l => l.UserId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        var now = _clock.GetUtcNow().UtcDateTime;
        var invites = await _db.Invites.AsNoTracking()
            .Where(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .OrderBy(i => i.ExpiresAt)
            .ToListAsync(ct);

        // One lookup for every user a request or an invite points at, rather than
        // the query-per-row the page used to run.
        var userIds = pendingRequests.Where(r => r.UserId is not null).Select(r => r.UserId!.Value)
            .Concat(invites.Select(i => i.InvitedByUserId))
            .Distinct()
            .ToList();
        var users = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        return new UserAdministrationPage(
            pendingRequests
                .Select(r => (r, r.UserId is int uid && users.TryGetValue(uid, out var u) ? u : null))
                .ToList(),
            active,
            entraLinked,
            invites
                .Select(i => (i, users.TryGetValue(i.InvitedByUserId, out var by) ? by : null))
                .ToList());
    }
}

/// <summary>The read model behind Administration → Users.</summary>
public sealed record UserAdministrationPage(
    List<(SignupRequest Request, User? User)> Pending,
    List<User> Active,
    HashSet<int> EntraLinkedUserIds,
    List<(Invite Invite, User? InvitedBy)> Invites);
