using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Issues + redeems single-use credential-recovery tokens: password reset
/// links and magic-link sign-in. Both kinds land in <c>password_reset_tokens</c>
/// discriminated by <see cref="TokenPurpose"/>; the table name predates the
/// magic-link addition (P4.19) and we keep it for migration continuity.
/// Carved out of the original AccountService in #88.
/// </summary>
public sealed class PasswordResetService
{
    public static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan MagicLinkTokenLifetime = TimeSpan.FromMinutes(15);

    private readonly AppDbContext _db;
    private readonly AuthService _auth;
    private readonly TimeProvider _clock;
    private readonly IOpenIddictTokenManager _oauthTokens;

    public PasswordResetService(AppDbContext db, AuthService auth, TimeProvider clock, IOpenIddictTokenManager oauthTokens)
    {
        _db = db;
        _auth = auth;
        _clock = clock;
        _oauthTokens = oauthTokens;
    }

    /// <summary>
    /// Generates a single-use password reset token for the user with the
    /// given email. Returns the plaintext token — the caller emails it.
    /// Returns <c>null</c> for unknown / disabled / Microsoft-only users and
    /// for a throttled request, so <c>/forgot-password</c> can render the same
    /// opaque "if that email exists" response regardless of outcome (no
    /// email-enumeration leak).
    ///
    /// <para>
    /// Rate limited on the same per-email / per-IP windows as password sign-in
    /// and magic links (10 per email, 30 per IP, per 15 minutes), because
    /// without one this endpoint is an unauthenticated way to make the site
    /// send mail to an address of the caller's choosing, as often as they like.
    /// <c>.design/auth-and-audit.md</c> described that limit long before there
    /// was one; issue #790's outbox is what made the gap matter, since every
    /// request now also writes a durable row.
    /// </para>
    ///
    /// <para>
    /// Every outcome is recorded in <c>login_attempts</c>, issued or not, so
    /// the counter is honest — an attacker cycling unknown addresses has to
    /// pay for them. Note what <c>succeeded: true</c> means on a row written
    /// here: a reset link was issued, not that anyone signed in. It is
    /// deliberate that issuing records a success rather than a failure, and
    /// <see cref="CreateMagicLoginTokenAsync"/> does the same: five *failures*
    /// in the window lock an account out of sign-in, so recording a failure
    /// for a valid address would hand anyone who knows an email a way to lock
    /// its owner out by asking for a reset five times.
    /// </para>
    /// </summary>
    public async Task<string?> CreatePasswordResetTokenAsync(
        string email, string ip, CancellationToken ct = default)
    {
        var normalised = AuthService.NormaliseEmail(email);
        var now = _clock.GetUtcNow().UtcDateTime;

        if (await _auth.IsRateLimitedAsync(normalised, ip, now, ct))
        {
            await _auth.RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return null;
        }

        // Fence category 1 (pre-auth routing): password reset, before any cookie exists;
        // pinned to the typed email.
        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == normalised, ct);
        if (user is null || user.Status == UserStatus.Disabled)
        {
            await _auth.RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return null;
        }
        // Microsoft-only orgs have no password to reset (issue #552). The
        // response stays the generic "check your email" either way. SiteAdmins
        // are exempt from that policy (break-glass), so this branch cannot
        // write failures against the one account that must stay reachable.
        if (await _auth.IsLocalLoginDisabledAsync(user, ct))
        {
            await _auth.RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return null;
        }

        var (raw, hash) = TokenIssuer.Issue();
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = hash,
            Purpose = TokenPurpose.PasswordReset,
            CreatedAt = now,
            ExpiresAt = now + ResetTokenLifetime,
        });
        await _db.SaveChangesAsync(ct);
        await _auth.RecordAttemptAsync(normalised, ip, succeeded: true, now, ct);
        return raw;
    }

    /// <summary>
    /// Consumes a reset token and applies the new password atomically. The
    /// row is stamped with <c>ConsumedAt</c> on success so it can't be
    /// reused. Throws <see cref="PlanValidationException"/> for expired,
    /// missing or already-consumed tokens.
    /// </summary>
    public async Task ConsumePasswordResetTokenAsync(string token, string newPassword, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        AuthService.ValidatePassword(newPassword, errors, fieldName: "Password");
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var hash = TokenIssuer.Sha256Hex(token);
        var now = _clock.GetUtcNow().UtcDateTime;
        // Fence category 1 (pre-auth routing): pinned to the reset token's hash.
        var row = await _db.PasswordResetTokens.IgnoreQueryFilters()
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == TokenPurpose.PasswordReset, ct);
        if (row is null || row.ConsumedAt is not null || row.ExpiresAt <= now || row.User is null)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "This reset link is no longer valid. Request a new one." });
        }
        if (row.User.Status != UserStatus.Active)
        {
            // Defensive, mirroring the magic-link consume: a token issued before
            // the account was disabled (or a status change mid-flow) must not let
            // a non-Active user complete a reset. Login already blocks Disabled,
            // but the two consume paths should be consistent. #410
            throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "This reset link is no longer valid. Request a new one." });
        }
        // The org may have gone Microsoft-only between issue and consume, as
        // the magic-link consume also checks: a password set now would come
        // back to life if the policy were relaxed later.
        if (await _auth.IsLocalLoginDisabledAsync(row.User, ct))
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "Your organisation signs in with Microsoft. Use \"Sign in with Microsoft\" on the login page." });
        }
        row.ConsumedAt = now;
        row.User.PasswordHash = _auth.HashPassword(newPassword);
        // Reset is the documented recovery path, so it has to actually recover:
        // stamp the credential change (which invalidates cookies and OAuth
        // access tokens issued before it) and revoke the personal access
        // tokens and OAuth tokens an intruder may have minted. #675
        row.User.CredentialsChangedAt = now;
        await AccountService.CommitCredentialChangeAsync(_db, _oauthTokens, row.User.Id, now, ct);
    }

    /// <summary>
    /// Generates a single-use magic-link token for the user with the given
    /// email. Returns the plaintext token — the caller emails it. Returns
    /// <c>null</c> for unknown / disabled / pending users so the
    /// <c>/login/magic</c> page can render the same opaque "if that email
    /// exists" response regardless of outcome (no email-enumeration leak).
    /// 15-minute expiry, with the same per-email / per-IP rate-limit as
    /// password sign-in (10 per email, 30 per IP, per 15-minute window)
    /// per <c>.design/milestones.md</c> P4.19. Records every attempt — issued
    /// or not — in <c>login_attempts</c> so the rate counter is honest.
    /// </summary>
    public async Task<string?> CreateMagicLoginTokenAsync(string email, string ip, CancellationToken ct = default)
    {
        var normalised = AuthService.NormaliseEmail(email);
        var now = _clock.GetUtcNow().UtcDateTime;

        if (await _auth.IsRateLimitedAsync(normalised, ip, now, ct))
        {
            await _auth.RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return null;
        }

        // Fence category 1 (pre-auth routing): magic-link request; pinned to the typed email.
        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == normalised, ct);
        if (user is null || user.Status != UserStatus.Active)
        {
            await _auth.RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return null;
        }
        // Microsoft-only orgs don't get magic links either — a magic link is
        // a local credential in email form (issue #552). Generic response.
        if (await _auth.IsLocalLoginDisabledAsync(user, ct))
        {
            await _auth.RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return null;
        }

        var (raw, hash) = TokenIssuer.Issue();
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = hash,
            Purpose = TokenPurpose.MagicLogin,
            CreatedAt = now,
            ExpiresAt = now + MagicLinkTokenLifetime,
        });
        await _db.SaveChangesAsync(ct);
        await _auth.RecordAttemptAsync(normalised, ip, succeeded: true, now, ct);
        return raw;
    }

    /// <summary>
    /// Consumes a magic-link token and returns the signed-in user. The token
    /// row is stamped with <c>ConsumedAt</c> on success so it can't be
    /// reused. Throws <see cref="PlanValidationException"/> for expired,
    /// missing, wrong-purpose or already-consumed tokens. The user's
    /// <c>LastLoginAt</c> is stamped so the magic-link path doesn't
    /// look like a stale account in <c>/admin/users</c>.
    /// </summary>
    public async Task<User> ConsumeMagicLoginTokenAsync(string token, CancellationToken ct = default)
    {
        var hash = TokenIssuer.Sha256Hex(token);
        var now = _clock.GetUtcNow().UtcDateTime;
        // Fence category 1 (pre-auth routing): pinned to the magic-link token's hash.
        var tokenRows = _db.PasswordResetTokens.IgnoreQueryFilters()
            .Where(t => t.TokenHash == hash && t.Purpose == TokenPurpose.MagicLogin);
        var row = await tokenRows
            .Include(t => t.User)
                .ThenInclude(u => u!.Organization)
            .FirstOrDefaultAsync(ct);
        if (row is null || row.ConsumedAt is not null || row.ExpiresAt <= now || row.User is null)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "This sign-in link is no longer valid. Request a new one." });
        }
        if (row.User.Status != UserStatus.Active)
        {
            // Defensive: status may have changed between issue and consume.
            throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "This sign-in link is no longer valid. Request a new one." });
        }
        // The org may have gone Microsoft-only between issue and consume.
        if (await _auth.IsLocalLoginDisabledAsync(row.User, ct))
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "Your organisation signs in with Microsoft. Use \"Sign in with Microsoft\" on the login page." });
        }
        // Single use, claimed with a conditional update: of two requests
        // carrying the same link, one signs in. #1208
        var claimed = await tokenRows
            .Where(t => t.Id == row.Id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now), ct);
        if (claimed == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "This sign-in link is no longer valid. Request a new one." });
        }
        row.ConsumedAt = now;
        row.User.LastLoginAt = now;
        await _db.SaveChangesAsync(ct);
        return row.User;
    }
}
