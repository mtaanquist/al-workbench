using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Verifies email + password pairs and owns the rate-limit / lockout / login-
/// attempt accounting that backs both <see cref="ALDevToolbox.Services.AccountService"/>
/// (the catch-all account surface) and <see cref="PasswordResetService"/> (the
/// magic-link flow). Carved out of the original AccountService in #88 so the
/// security-sensitive part of the auth surface can be reviewed in isolation.
/// </summary>
public sealed class AuthService
{
    public const int MaxAttemptsPerEmail = 10;
    public const int MaxAttemptsPerIp = 30;
    public const int LockoutThreshold = 5;
    /// <summary>
    /// Wrong second-factor guesses (TOTP, recovery code or email code) allowed
    /// within the lockout window before the challenge endpoints refuse, so the
    /// 6-digit space can't be walked inside the renewable MfaPending window,
    /// however many email codes are sent. See #477 and #1432.
    /// </summary>
    public const int MaxMfaAttempts = 5;
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);
    public const int MinPasswordLength = 12;
    public const int BcryptWorkFactor = 12;

    // Used to pay the BCrypt cost when the supplied email doesn't match a
    // user, so the response-time profile doesn't leak whether the email is
    // registered (timing oracle). Computed once per process.
    private static readonly Lazy<string> DummyPasswordHash = new(() =>
        BCrypt.Net.BCrypt.HashPassword("not-a-real-password", BcryptWorkFactor));

    private readonly AppDbContext _db;
    private readonly ILogger<AuthService> _logger;
    private readonly TimeProvider _clock;

    public AuthService(AppDbContext db, ILogger<AuthService> logger, TimeProvider clock)
    {
        _db = db;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Verifies the email + password pair and returns the matching user when
    /// successful, along with the discriminated outcome. Records a row in
    /// <c>login_attempts</c> for both successes and failures so the rate
    /// limit and lockout windows have raw material.
    /// </summary>
    public async Task<(LoginOutcome Outcome, User? User)> TryLoginAsync(
        string email, string password, string ip, CancellationToken ct = default)
    {
        var normalised = NormaliseEmail(email);
        var now = _clock.GetUtcNow().UtcDateTime;

        if (await IsRateLimitedAsync(normalised, ip, now, ct, failuresOnlyPerIp: true))
        {
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            _logger.LogInformation("Login throttled for {Email} from {Ip}.", normalised, ip);
            return (LoginOutcome.RateLimited, null);
        }

        // Lockout is checked before password verification so that an attacker
        // hammering the same email with wrong passwords also gets locked out,
        // not just somebody who finally guesses the right one. The current
        // attempt is not yet recorded, so IsLockedOutAsync counts strictly
        // prior attempts (LockoutThreshold consecutive failures → next
        // attempt locked).
        if (await IsLockedOutAsync(normalised, now, ct))
        {
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return (LoginOutcome.LockedOut, null);
        }

        var user = await _db.Users
            // Fence category 1 (pre-auth routing): password login, before any cookie exists;
            // pinned to the typed email.
            .IgnoreQueryFilters()
            .Include(u => u.Organization)
            .FirstOrDefaultAsync(u => u.Email == normalised, ct);

        if (user is null)
        {
            // Pay the BCrypt cost against a dummy hash so the response time
            // doesn't leak whether the email is registered (timing oracle).
            _ = VerifyPassword(password, DummyPasswordHash.Value);
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return (LoginOutcome.InvalidCredentials, null);
        }

        if (!VerifyPassword(password, user.PasswordHash))
        {
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return (LoginOutcome.InvalidCredentials, null);
        }

        if (user.Status == UserStatus.Pending)
        {
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return (LoginOutcome.Pending, null);
        }

        if (user.Status == UserStatus.Disabled)
        {
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return (LoginOutcome.Disabled, null);
        }

        // The org may have switched to Microsoft-only sign-in. Checked after
        // password verification so this path keeps the same timing and
        // attempt-recording shape as a normal refusal, and after the status
        // checks so a disabled account still reads "disabled".
        if (await IsLocalLoginDisabledAsync(user, ct))
        {
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return (LoginOutcome.LocalLoginDisabled, null);
        }

        // Password is right and the account is in good standing. If the user
        // has 2FA enrolled, stop here: the caller redirects to /login/challenge
        // with a short-lived signed cookie carrying the user id. LastLoginAt
        // and the login_attempts success row are stamped by CompleteMfaAsync
        // once the second factor verifies.
        if (user.TotpEnabled || user.EmailMfaEnabled)
        {
            return (LoginOutcome.MfaRequired, user);
        }

        user.LastLoginAt = now;
        await _db.SaveChangesAsync(ct);
        await RecordAttemptAsync(normalised, ip, succeeded: true, now, ct);

        return (LoginOutcome.Success, user);
    }

    /// <summary>
    /// Finalises a multi-factor login after the second factor (TOTP / email
    /// code / recovery code) has been verified. Stamps <c>LastLoginAt</c> and
    /// records the success in <c>login_attempts</c>. The caller sets the auth
    /// cookie.
    /// <para>
    /// The account is checked again, since up to ten minutes can pass after
    /// the password leg (#1614): the result carries a login error code
    /// instead of the user when the account is no longer active, its
    /// organisation has moved to Microsoft-only sign-in, or a credential
    /// changed after <paramref name="pendingIssuedAt"/> (a password reset, an
    /// email change). Without the last check, a half-finished sign-in from
    /// before a reset could finish after it with a session newer than the
    /// reset's cut-off.
    /// </para>
    /// </summary>
    public async Task<(User? User, string? RefusedWith)> CompleteMfaAsync(
        int userId, DateTime pendingIssuedAt, string ip, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        // Fence category 1 (pre-auth routing): finishes a half-authenticated MFA login;
        // pinned to u.Id == userId from the signed MFA state.
        var user = await _db.Users.IgnoreQueryFilters()
            .Include(u => u.Organization)
            .FirstAsync(u => u.Id == userId, ct);
        if (user.Status != UserStatus.Active) return (null, "disabled");
        if (await IsLocalLoginDisabledAsync(user, ct)) return (null, "entra-only");
        if (user.CredentialsChangedAt is { } changedAt && changedAt > pendingIssuedAt) return (null, "invalid");
        user.LastLoginAt = now;
        await _db.SaveChangesAsync(ct);
        await RecordAttemptAsync(user.Email, ip, succeeded: true, now, ct);
        return (user, null);
    }

    /// <summary>
    /// True when the user's organisation is Microsoft-sign-in-only
    /// (<see cref="Domain.ValueObjects.LocalLoginPolicy.EntraOnly"/>) and the
    /// user isn't a SiteAdmin. SiteAdmin password login always survives as
    /// break-glass — mirroring the <c>/site-admin/users/{id}/reset-mfa</c>
    /// precedent — so a broken Entra config can't lock the operator out.
    /// Passkey login is deliberately NOT gated by the policy: passkeys are
    /// phishing-resistant and were approved at registration time (issue #552).
    /// </summary>
    public async Task<bool> IsLocalLoginDisabledAsync(User user, CancellationToken ct = default)
    {
        if (user.IsSiteAdmin) return false;
        return await IsMicrosoftOnlyOrganizationAsync(user.OrganizationId, ct);
    }

    /// <summary>
    /// True when the organisation refuses password, reset and magic-link
    /// sign-in for its members. The SiteAdmin exception lives in
    /// <see cref="IsLocalLoginDisabledAsync"/>; this is the organisation's
    /// rule alone, for an invite whose account does not exist yet (#1336).
    /// </summary>
    public async Task<bool> IsMicrosoftOnlyOrganizationAsync(int organizationId, CancellationToken ct = default) =>
        // Fence category 1 (pre-auth routing): every caller but one is a login /
        // password-reset / magic-link / invite path running before a cookie exists,
        // so there is no current organisation. Pinned to s.OrganizationId == organizationId,
        // the subject's own org (or the org that issued the invite).
        await _db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(s => s.OrganizationId == organizationId
                && s.LocalLoginPolicy == Domain.ValueObjects.LocalLoginPolicy.EntraOnly, ct);

    /// <summary>
    /// True when the user has at least one strong-auth method enrolled:
    /// confirmed TOTP, email-MFA, or any non-revoked passkey credential.
    /// The org-level <c>RequireStrongAuth</c> gate consults this, and so does
    /// the admin toggle that turns the requirement on.
    ///
    /// <para>Every caller runs under an authenticated request asking about a
    /// user in its own organisation, so all three reads stay inside the query
    /// filter — a user id from another org simply doesn't resolve.</para>
    /// </summary>
    public async Task<bool> HasStrongAuthAsync(int userId, CancellationToken ct = default)
    {
        var totpOrEmail = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.TotpEnabled || u.EmailMfaEnabled)
            .FirstOrDefaultAsync(ct);
        if (totpOrEmail) return true;
        if (await _db.UserPasskeys.AnyAsync(p => p.UserId == userId, ct)) return true;
        // A linked Microsoft account counts: MFA is the Entra tenant's job
        // there, and without this a RequireStrongAuth org would trap its
        // federated users on /account with nothing to enrol. See issue #552.
        // Entra only - a GitHub account link (issue #621) shares this table but
        // signs nobody in, so it is no evidence of strong authentication.
        return await _db.UserExternalLogins
            .AnyAsync(l => l.UserId == userId && l.Provider == EntraSignInService.ProviderName, ct);
    }

    public string HashPassword(string password) => BCrypt.Net.BCrypt.HashPassword(password, BcryptWorkFactor);

    public bool VerifyPassword(string candidate, string hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        try
        {
            return BCrypt.Net.BCrypt.Verify(candidate, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }

    public static void ValidatePassword(string? value, Dictionary<string, string> errors, string fieldName = "Password")
    {
        if (string.IsNullOrEmpty(value) || value.Length < MinPasswordLength)
        {
            errors[fieldName] = $"Password must be at least {MinPasswordLength} characters.";
        }
    }

    public static string NormaliseEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>
    /// True when the email or the source IP has used up its attempts in the
    /// rate window. <paramref name="failuresOnlyPerIp"/> is what password
    /// sign-in passes: there the IP cap counts only refusals, because a whole
    /// office signs in from one address and thirty good sign-ins on a Monday
    /// morning must not lock the thirty-first person out (#1259). The flows
    /// that send mail (reset links, magic links, signups) leave it off, so
    /// the IP cap still bounds how much mail one address can make the site
    /// send to valid accounts, whose rows are recorded as successes.
    /// </summary>
    public async Task<bool> IsRateLimitedAsync(string email, string ip, DateTime now, CancellationToken ct, bool failuresOnlyPerIp = false)
    {
        var window = now - RateWindow;
        // login_attempts is org-less rate-limit data with no tenant query filter,
        // so these pre-auth reads have nothing to bypass.
        var perEmail = await _db.LoginAttempts
            .CountAsync(a => a.Email == email && a.Timestamp >= window, ct);
        if (perEmail >= MaxAttemptsPerEmail) return true;
        if (string.IsNullOrEmpty(ip)) return false;
        var perIp = await _db.LoginAttempts
            .CountAsync(a => a.Ip == ip && a.Timestamp >= window && (!failuresOnlyPerIp || !a.Succeeded), ct);
        return perIp >= MaxAttemptsPerIp;
    }

    /// <summary>
    /// True when the email has had <c>LockoutThreshold</c> consecutive
    /// failures within the lockout window with no intervening success.
    /// </summary>
    private async Task<bool> IsLockedOutAsync(string email, DateTime now, CancellationToken ct)
    {
        var window = now - LockoutWindow;
        var recent = await _db.LoginAttempts
            .Where(a => a.Email == email && a.Timestamp >= window)
            .OrderByDescending(a => a.Timestamp)
            .Take(LockoutThreshold)
            .ToListAsync(ct);
        return recent.Count >= LockoutThreshold && recent.All(a => !a.Succeeded);
    }

    /// <summary>
    /// Takes one second-factor attempt for the user before the code is
    /// checked. Gates the TOTP, recovery-code and email-code challenges so their short
    /// code space can't be brute-forced inside the renewable MfaPending
    /// window: after <see cref="MaxMfaAttempts"/> failed attempts for the
    /// user's email within the lockout window and since their last completed
    /// sign-in, further attempts are refused. See #477.
    ///
    /// <para>The attempt is written to <c>login_attempts</c> as a failure
    /// first and the throttle is then checked against every other attempt,
    /// which is what makes the cap hold under parallel guesses: each request
    /// sees the ones already in flight, where check-then-write let them all
    /// read the same count (#1208). Returns the attempt's id, or
    /// <see langword="null"/> when throttled (the attempt is then removed
    /// again, so a refused request doesn't extend the lockout). On a right
    /// code the caller hands the id to <see cref="ReleaseMfaAttemptAsync"/>;
    /// on a wrong one the recorded failure stands, and also feeds the
    /// per-email and per-IP login limits.</para>
    /// </summary>
    public async Task<int?> TryReserveMfaAttemptAsync(int userId, string ip, CancellationToken ct)
    {
        // Fence category 1 (pre-auth routing): MFA throttle between the two login legs;
        // pinned to u.Id == userId and then to that user's email.
        var user = await _db.Users.IgnoreQueryFilters()
            .Where(u => u.Id == userId).Select(u => new { u.Email, u.LastLoginAt }).FirstOrDefaultAsync(ct);
        if (user is null) return null;
        var email = user.Email;
        var now = _clock.GetUtcNow().UtcDateTime;
        var attempt = new LoginAttempt { Email = email, Ip = ip ?? string.Empty, Succeeded = false, Timestamp = now };
        _db.LoginAttempts.Add(attempt);
        await _db.SaveChangesAsync(ct);

        // The run of failures ends at a completed sign-in, read from the
        // user's LastLoginAt, and not at any success row: issuing a reset or
        // magic link writes a success row too, and letting that end the run
        // let someone holding the password lift the cap at will (#1259).
        var since = now - LockoutWindow;
        if (user.LastLoginAt is { } lastLogin && lastLogin >= since) since = lastLogin.AddTicks(1);
        var failures = await _db.LoginAttempts
            .CountAsync(a => a.Email == email && a.Timestamp >= since && !a.Succeeded && a.Id != attempt.Id, ct);
        if (failures >= MaxMfaAttempts)
        {
            await ReleaseMfaAttemptAsync(attempt.Id, ct);
            return null;
        }
        return attempt.Id;
    }

    /// <summary>
    /// Withdraws an attempt <see cref="TryReserveMfaAttemptAsync"/> took, once
    /// the code proved right: a correct second factor is not a failure.
    /// </summary>
    public async Task ReleaseMfaAttemptAsync(int attemptId, CancellationToken ct)
    {
        await _db.LoginAttempts.Where(a => a.Id == attemptId).ExecuteDeleteAsync(ct);
    }

    /// <summary>What the signed-in account pages are told for a refused current password while the email is locked out.</summary>
    public const string CurrentPasswordLockedOutMessage =
        "Too many wrong passwords. Wait 15 minutes, then try again.";

    /// <summary>
    /// Checks the current password a signed-in account action asks for
    /// (changing the password, turning two-step off, new recovery codes, the
    /// passkey fallback). A wrong one is recorded as a failed sign-in attempt
    /// for the account's email and the action is refused once the email is
    /// locked out, exactly as at sign-in: without it a session that lacks the
    /// password (a stolen cookie, an unattended browser) could run a breach
    /// list against these forms with no limit (#1614). An empty password is
    /// refused without counting, since it guesses nothing.
    /// </summary>
    public async Task<CurrentPasswordCheck> CheckCurrentPasswordAsync(
        string email, string passwordHash, string? password, string ip, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var normalised = NormaliseEmail(email);
        if (await IsLockedOutAsync(normalised, now, ct)) return CurrentPasswordCheck.LockedOut;
        if (string.IsNullOrEmpty(password)) return CurrentPasswordCheck.Wrong;
        if (VerifyPassword(password, passwordHash)) return CurrentPasswordCheck.Correct;
        await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
        return CurrentPasswordCheck.Wrong;
    }

    public async Task RecordAttemptAsync(string email, string ip, bool succeeded, DateTime timestamp, CancellationToken ct)
    {
        _db.LoginAttempts.Add(new LoginAttempt
        {
            Email = email,
            Ip = ip ?? string.Empty,
            Succeeded = succeeded,
            Timestamp = timestamp,
        });
        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>The answer of <see cref="AuthService.CheckCurrentPasswordAsync"/>.</summary>
public enum CurrentPasswordCheck
{
    Correct,
    Wrong,
    /// <summary>Too many wrong passwords for this email lately; refused without checking.</summary>
    LockedOut,
}
