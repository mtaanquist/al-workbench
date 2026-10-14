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
/// <para>
/// A SiteAdmin's account is the hosting operator's way into every
/// organisation, so its email, name, status and role can only be changed by another
/// SiteAdmin, never by an org Admin who happens to share the organisation. See
/// <see cref="RequireMayManage"/>.
/// </para>
/// </remarks>
public sealed class UserAdministrationService
{
    /// <summary>
    /// How long an email change waits after it is asked for before it can be
    /// confirmed, so the person at the current address has time to read the
    /// notice and stop it.
    /// </summary>
    public static readonly TimeSpan EmailChangeConfirmDelay = TimeSpan.FromHours(24);

    /// <summary>
    /// How long both email-change links work, counted from the request: two
    /// days to confirm once <see cref="EmailChangeConfirmDelay"/> is over,
    /// and the cancel link for the whole time the change can still be made.
    /// </summary>
    public static readonly TimeSpan EmailChangeTokenLifetime = TimeSpan.FromHours(72);

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IOpenIddictTokenManager _oauthTokens;
    private readonly IOrganizationContext _orgContext;

    public UserAdministrationService(AppDbContext db, TimeProvider clock, IOpenIddictTokenManager oauthTokens, IOrganizationContext orgContext)
    {
        _db = db;
        _clock = clock;
        _oauthTokens = oauthTokens;
        _orgContext = orgContext;
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
        // One transaction from the guard to the commit, with the org locked, so
        // two admins disabling each other at once can't both pass the guard
        // (#1260). The bulk revocations below commit inside it too, so a failed
        // save never leaves tokens revoked on an account still active.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockAdminGuardAsync(actingOrgId, ct);
        var user = await LoadUserAsync(userId, actingOrgId, ct);
        RequireMayManage(user);
        if (user.Role == UserRole.Admin && user.Status == UserStatus.Active
            && await CountActiveAdminsAsync(actingOrgId, ct) <= 1)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["LastAdmin"] = "You can't disable the last active admin in this organisation." });
        }
        // The acting SiteAdmin is an active SiteAdmin in this org, so this only
        // bites when they disable themselves with nobody else to fall back on.
        // Counted inside the query filter (the request's own org): a SiteAdmin
        // in another organisation is out of this page's sight, so the guard
        // errs towards refusing, and /site-admin is the place to hand over.
        if (user.IsSiteAdmin && user.Status == UserStatus.Active
            && !await _db.Users.AnyAsync(u => u.OrganizationId == actingOrgId
                                              && u.Id != user.Id
                                              && u.IsSiteAdmin
                                              && u.Status == UserStatus.Active, ct))
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["LastSiteAdmin"] = "You can't disable the last active site administrator." });
        }
        var now = _clock.GetUtcNow().UtcDateTime;
        user.Status = UserStatus.Disabled;
        user.CredentialsChangedAt = now;
        // Both filtered writes: LoadUserAsync pinned the user to the
        // acting org, and the admin endpoint passes the request's own org as
        // actingOrgId, so the query filter and the pin agree. Not crossing the
        // fence here is deliberate (CLAUDE.md); a caller acting for another org
        // would find these no-op, which the filter would then be right about.
        await _db.PersonalAccessTokens
            .Where(p => p.UserId == userId && p.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.RevokedAt, now), ct);
        await _db.OAuthConsents
            .Where(c => c.UserId == userId && c.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, now), ct);
        await _db.SaveChangesAsync(ct);
        // OpenIddict stores Subject = the user id (OAuthEndpoints.MapAuthorizeComplete).
        // Its manager shares this scoped context, so the revocation joins the transaction.
        // One set-based UPDATE, not one per token: an assistant that refreshes hourly
        // leaves hundreds of rows per user (#1259).
        await _oauthTokens.RevokeBySubjectAsync(userId.ToString(), ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Re-enables a disabled user without further admin approval.</summary>
    public async Task EnableUserAsync(int userId, int actingOrgId, CancellationToken ct = default)
    {
        var user = await LoadUserAsync(userId, actingOrgId, ct);
        RequireMayManage(user);
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
        RequireMayManage(user);
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
        // Guard and save under the org lock, as in DisableUserAsync (#1260).
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockAdminGuardAsync(actingOrgId, ct);
        var user = await LoadUserAsync(userId, actingOrgId, ct);
        RequireMayManage(user);
        if (user.Role == UserRole.Admin && newRole != UserRole.Admin
            && user.Status == UserStatus.Active
            && await CountActiveAdminsAsync(actingOrgId, ct) <= 1)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["LastAdmin"] = "You can't demote the last active admin in this organisation." });
        }
        user.Role = newRole;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
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

    /// <summary>
    /// Takes a row lock on the organisation for the rest of the caller's
    /// transaction. The last-admin guard counts admins and then saves; without
    /// the lock two admins demoting or disabling each other at the same time
    /// both counted two and the organisation was left with none (#1260). Every
    /// path that can take an admin away goes through here first, so the
    /// second waits for the first to commit and then counts what it left.
    /// Locking the one organisations row rather than the admin rows means an
    /// admin added in the meantime can't slip past either.
    /// </summary>
    private async Task LockAdminGuardAsync(int orgId, CancellationToken ct) =>
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM organizations WHERE id = {orgId} FOR UPDATE", ct);

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

    /// <summary>
    /// Refuses an email, name, status or role change on a SiteAdmin's account unless
    /// the actor is a SiteAdmin too. An org Admin shares the organisation with
    /// the bootstrap SiteAdmin in the usual single-org setup, and changing that
    /// account's email or status would hand them, or lock everyone out of, the
    /// cross-organisation console.
    /// </summary>
    private void RequireMayManage(User target)
    {
        if (target.IsSiteAdmin && !_orgContext.IsSiteAdmin)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["UserId"] = "Only a site administrator can change this account."
            });
        }
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
    /// Stamps a pending email change on the user row and issues two tokens,
    /// both with the <see cref="EmailChangeTokenLifetime"/>: a confirmation
    /// (purpose <see cref="TokenPurpose.EmailChangeConfirm"/>) for the
    /// <em>new</em> address, and a cancel token
    /// (<see cref="TokenPurpose.EmailChangeCancel"/>) for the <em>current</em>
    /// one. The endpoint emails both; the swap happens in
    /// <see cref="ConfirmEmailChangeAsync"/> once the new mailbox responds, no
    /// sooner than <see cref="EmailChangeConfirmDelay"/> after the request, and
    /// <see cref="CancelEmailChangeAsync"/> lets the current owner stop it.
    /// </summary>
    /// <remarks>
    /// Once confirmed, the new address is how the account signs in, so the
    /// owner has to hear about the change and have time to stop it. The
    /// endpoint refuses the change when the notice to the current address
    /// cannot be sent (calling <see cref="WithdrawEmailChangeAsync"/>), and the
    /// confirmation waits out <see cref="EmailChangeConfirmDelay"/>.
    /// </remarks>
    public async Task<EmailChangeRequest> RequestEmailChangeAsync(int targetUserId, string newEmail, int actingOrgId, int actingUserId, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var normalised = (newEmail ?? string.Empty).Trim().ToLowerInvariant();
        if (!EmailAddress.HasValidShape(normalised))
        {
            errors["NewEmail"] = "Enter a valid email address.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var user = await LoadUserAsync(targetUserId, actingOrgId, ct);
        RequireMayManage(user);
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
        // The notice to the current address names who asked. Loaded through
        // the same org-pinned helper, so it is always someone in this org.
        var actor = await LoadUserAsync(actingUserId, actingOrgId, ct);

        var (confirmRaw, confirmHash) = TokenIssuer.Issue();
        var (cancelRaw, cancelHash) = TokenIssuer.Issue();
        var now = _clock.GetUtcNow().UtcDateTime;
        // Burn any still-valid prior change tokens for this user. Without this,
        // a previous recipient's link would still validate against the newly-
        // stored pending_email and silently confirm an address they never had
        // access to.
        await BurnEmailChangeTokensAsync(user.Id, now, ct);

        user.PendingEmail = normalised;
        user.PendingEmailAt = now;
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = confirmHash,
            // RequireMayManage above means a SiteAdmin target here was asked
            // for by a SiteAdmin; the purpose records that for the confirm step.
            Purpose = user.IsSiteAdmin ? TokenPurpose.SiteAdminEmailChangeConfirm : TokenPurpose.EmailChangeConfirm,
            CreatedAt = now,
            ExpiresAt = now + EmailChangeTokenLifetime,
        });
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = cancelHash,
            Purpose = TokenPurpose.EmailChangeCancel,
            CreatedAt = now,
            // As long as the confirmation link: the change can be stopped for
            // as long as it can still be made.
            ExpiresAt = now + EmailChangeTokenLifetime,
        });
        await _db.SaveChangesAsync(ct);
        return new EmailChangeRequest(confirmRaw, cancelRaw, user.Email, normalised, user.DisplayName,
            actor.DisplayName, actor.Email, now + EmailChangeConfirmDelay, now + EmailChangeTokenLifetime);
    }

    /// <summary>
    /// Drops a pending email change and burns its links. The admin endpoint
    /// calls this when the emails about a change could not be sent, so a change
    /// its owner was not told about never sits waiting to be confirmed.
    /// </summary>
    public async Task WithdrawEmailChangeAsync(int targetUserId, int actingOrgId, CancellationToken ct = default)
    {
        var user = await LoadUserAsync(targetUserId, actingOrgId, ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        await BurnEmailChangeTokensAsync(user.Id, now, ct);
        user.PendingEmail = null;
        user.PendingEmailAt = null;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Consumes an <see cref="TokenPurpose.EmailChangeConfirm"/> token: swaps
    /// <see cref="User.Email"/> for <see cref="User.PendingEmail"/>, clears the
    /// pending fields, and returns the user with the address it had before.
    /// A token opened before <see cref="EmailChangeConfirmDelay"/> has passed
    /// is left unused and answered with when it can be used. Any other refusal
    /// (invalid, expired, used, change stopped) is one outcome, without saying
    /// which.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The swap is a credential change, since the address is part of how the
    /// account signs in. So it stamps <see cref="User.CredentialsChangedAt"/> and revokes
    /// the user's personal access tokens and OAuth tokens through the same
    /// <see cref="AccountService.CommitCredentialChangeAsync"/> a password
    /// reset uses, and every existing session has to sign in again.
    /// </para>
    /// <para>
    /// The token is claimed with a conditional update, so of two clicks one
    /// wins, and the user row is locked first, so a cancel arriving at the same
    /// moment either lands before the swap (and the swap sees no pending
    /// address) or after it (and finds nothing left to stop).
    /// </para>
    /// </remarks>
    public async Task<EmailChangeConfirmResult> ConfirmEmailChangeAsync(string rawToken, CancellationToken ct = default)
    {
        var invalid = new EmailChangeConfirmResult(EmailChangeConfirmOutcome.Invalid);
        if (string.IsNullOrWhiteSpace(rawToken)) return invalid;
        var now = _clock.GetUtcNow().UtcDateTime;
        var tokens = EmailChangeTokens(rawToken,
            [TokenPurpose.EmailChangeConfirm, TokenPurpose.SiteAdminEmailChangeConfirm]);
        var row = await tokens.AsNoTracking().Include(t => t.User).FirstOrDefaultAsync(ct);
        // A stopped or withdrawn change answers as invalid even inside the
        // wait, rather than promising a link that will never work.
        if (row is null || row.ConsumedAt is not null || row.ExpiresAt <= now
            || row.User is null || string.IsNullOrEmpty(row.User.PendingEmail))
        {
            return invalid;
        }
        var confirmableFrom = row.CreatedAt + EmailChangeConfirmDelay;
        if (now < confirmableFrom)
        {
            // Not consumed: the same link works once the wait is over.
            return new EmailChangeConfirmResult(EmailChangeConfirmOutcome.NotYet, ConfirmableFrom: confirmableFrom);
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var user = await ClaimEmailChangeTokenAsync(tokens, row, now, ct);
        if (user is null) return invalid;
        if (user.IsSiteAdmin && row.Purpose != TokenPurpose.SiteAdminEmailChangeConfirm)
        {
            // Issued without a SiteAdmin asking for it (before such requests
            // were refused, or before the account was promoted): burn it and
            // drop the pending address rather than swap a SiteAdmin's email.
            user.PendingEmail = null;
            user.PendingEmailAt = null;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return invalid;
        }
        if (string.IsNullOrEmpty(user.PendingEmail))
        {
            // Pending email was cleared (stopped from the current address,
            // withdrawn, account modified): the token is burned, nothing else
            // changes.
            await tx.CommitAsync(ct);
            return invalid;
        }
        // Race: another account may have grabbed the address since the token
        // was issued.
        // Fence category 6 (existence-only uniqueness probe): projects a bool, never a row.
        var stolen = await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == user.PendingEmail && u.Id != user.Id, ct);
        if (stolen)
        {
            user.PendingEmail = null;
            user.PendingEmailAt = null;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return invalid;
        }
        var previousEmail = user.Email;
        user.Email = user.PendingEmail;
        user.PendingEmail = null;
        user.PendingEmailAt = null;
        user.CredentialsChangedAt = now;
        // Joins the transaction above, so the swap and the revocations land together.
        await AccountService.CommitCredentialChangeAsync(_db, _oauthTokens, user.Id, now, ct);
        await tx.CommitAsync(ct);
        return new EmailChangeConfirmResult(EmailChangeConfirmOutcome.Confirmed, user, previousEmail);
    }

    /// <summary>
    /// Consumes an <see cref="TokenPurpose.EmailChangeCancel"/> token, the link
    /// in the notice sent to the current address: drops the pending change so
    /// its confirmation link stops working. Needs no sign-in, since the person
    /// it protects may not be able to get in. Returns the user's id when a
    /// pending change was stopped, and <c>null</c> for an invalid / expired /
    /// used token or when there was nothing left to stop. Claims the token and
    /// locks the user row the same way <see cref="ConfirmEmailChangeAsync"/>
    /// does, so the two can't both win.
    /// </summary>
    public async Task<int?> CancelEmailChangeAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var now = _clock.GetUtcNow().UtcDateTime;
        var tokens = EmailChangeTokens(rawToken, [TokenPurpose.EmailChangeCancel]);
        var row = await tokens.AsNoTracking().FirstOrDefaultAsync(ct);
        if (row is null || row.ConsumedAt is not null || row.ExpiresAt <= now)
        {
            return null;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var user = await ClaimEmailChangeTokenAsync(tokens, row, now, ct);
        if (user is null) return null;
        var hadPending = !string.IsNullOrEmpty(user.PendingEmail);
        // The confirmation link from the stopped change finds no pending
        // address and is burned when opened; the next request burns it too.
        user.PendingEmail = null;
        user.PendingEmailAt = null;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return hadPending ? user.Id : null;
    }

    /// <summary>
    /// Email-change tokens matching the hash of <paramref name="rawToken"/>.
    /// Shared by the confirm and cancel links, both opened from an email by
    /// someone who may be signed out, for the first read and for the claim.
    /// </summary>
    private IQueryable<PasswordResetToken> EmailChangeTokens(string rawToken, TokenPurpose[] purposes)
    {
        var hash = TokenIssuer.Sha256Hex(rawToken);
        // Fence category 1 (pre-auth routing): the confirm and cancel links are opened from
        // an email, possibly signed out; pinned to the token hash.
        return _db.PasswordResetTokens.IgnoreQueryFilters()
            .Where(t => t.TokenHash == hash && purposes.Contains(t.Purpose));
    }

    /// <summary>
    /// Inside the caller's transaction: locks the token's user row, claims the
    /// token with a conditional update (0 rows means someone else used it
    /// first), and loads the user as it is now, tracked. Returns <c>null</c>
    /// when the claim lost. The lock comes first so a confirm and a cancel on
    /// the same user run one after the other, and the user is read after it so
    /// the second sees what the first saved.
    /// </summary>
    private async Task<User?> ClaimEmailChangeTokenAsync(
        IQueryable<PasswordResetToken> tokens, PasswordResetToken row, DateTime now, CancellationToken ct)
    {
        // Raw SQL, like LockAdminGuardAsync: a row lock pinned to the id the
        // token row names, with no query filter to bypass.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM users WHERE id = {row.UserId} FOR UPDATE", ct);
        var claimed = await tokens
            .Where(t => t.Id == row.Id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now), ct);
        if (claimed == 0) return null;
        var fresh = await tokens.Where(t => t.Id == row.Id).Include(t => t.User).FirstAsync(ct);
        return fresh.User;
    }

    /// <summary>Marks every still-valid email-change link for <paramref name="userId"/> as used.</summary>
    private async Task BurnEmailChangeTokensAsync(int userId, DateTime now, CancellationToken ct)
    {
        // Fence category 4 (explicitly scoped user-id lookup): pinned to t.UserId == userId,
        // a user the caller loaded from the acting admin's own org.
        var stale = await _db.PasswordResetTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == userId
                        && (t.Purpose == TokenPurpose.EmailChangeConfirm
                            || t.Purpose == TokenPurpose.SiteAdminEmailChangeConfirm
                            || t.Purpose == TokenPurpose.EmailChangeCancel)
                        && t.ConsumedAt == null
                        && t.ExpiresAt > now)
            .ToListAsync(ct);
        foreach (var s in stale) s.ConsumedAt = now;
    }

    /// <summary>
    /// SiteAdmin-only break-glass: clears every 2FA factor for the target
    /// user (TOTP secret + recovery codes + every passkey) and flips both
    /// MFA flags off. The user can re-enroll after their next sign-in.
    /// </summary>
    public async Task ResetMfaAsync(int targetUserId, CancellationToken ct = default)
    {
        // Checked here, not only on the endpoint: the reads below cross the
        // fence, and the route's role was their only guard (#1272).
        _orgContext.RequireSiteAdmin();
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

/// <summary>
/// What <see cref="UserAdministrationService.RequestEmailChangeAsync"/> hands
/// the endpoint to email: the confirmation link's token for the new address,
/// the cancel link's token for the current one, and the names both emails show.
/// </summary>
public sealed record EmailChangeRequest(
    string ConfirmToken,
    string CancelToken,
    string CurrentEmail,
    string NewEmail,
    string DisplayName,
    string RequestedByName,
    string RequestedByEmail,
    DateTime ConfirmableFrom,
    DateTime ExpiresAt);

/// <summary>How a click on an email-change confirmation link went.</summary>
public enum EmailChangeConfirmOutcome
{
    /// <summary>The address was swapped.</summary>
    Confirmed,

    /// <summary>Opened before the waiting period was over; the link still works later.</summary>
    NotYet,

    /// <summary>Invalid, expired, used, or the change was stopped. Deliberately one outcome.</summary>
    Invalid,
}

/// <summary>
/// The result of <see cref="UserAdministrationService.ConfirmEmailChangeAsync"/>:
/// for <see cref="EmailChangeConfirmOutcome.Confirmed"/> the user as it is now
/// and the address it replaced; for <see cref="EmailChangeConfirmOutcome.NotYet"/>
/// when the link can be used.
/// </summary>
public sealed record EmailChangeConfirmResult(
    EmailChangeConfirmOutcome Outcome,
    User? User = null,
    string? PreviousEmail = null,
    DateTime? ConfirmableFrom = null);
