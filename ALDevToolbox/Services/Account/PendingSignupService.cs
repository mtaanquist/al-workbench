using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Owner of the pre-account email-verification step of the email-first signup
/// flow (see <c>.design/auth-and-audit.md</c>). A visitor enters an email at
/// <c>/signup</c>; <see cref="StartAsync"/> persists a <see cref="PendingSignup"/>
/// and returns a single-use link token plus a 6-digit code for the caller to
/// email. Verification (link or code) stamps <c>verified_at</c>; the verified
/// row then gates <c>AccountService.CompleteVerifiedSignupAsync</c>.
///
/// <para>
/// Token plumbing mirrors the rest of the auth surface:
/// <see cref="TokenIssuer"/> for the link, the
/// <see cref="EmailMfaService"/>-style 6-digit code (hashed bound to the row's
/// link-token hash, since the row id isn't known before insert), and
/// <see cref="AuthService"/> rate limiting on the send step.
/// </para>
/// </summary>
public sealed class PendingSignupService
{
    /// <summary>Verification window for both the link and the code.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>Wrong-code submissions allowed before the pending row is dead (#409).</summary>
    public const int MaxCodeAttempts = 5;

    private readonly AppDbContext _db;
    private readonly AuthService _auth;
    private readonly SystemSettingsService _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<PendingSignupService> _logger;

    public PendingSignupService(
        AppDbContext db,
        AuthService auth,
        SystemSettingsService settings,
        TimeProvider clock,
        ILogger<PendingSignupService> logger)
    {
        _db = db;
        _auth = auth;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Validates and rate-limits the email, sweeps expired rows, supersedes any
    /// in-flight attempt for the same email, and persists a fresh
    /// <see cref="PendingSignup"/>. Returns the plaintext link token + code for
    /// the caller to email, or <see langword="null"/> when nothing should be
    /// sent — invalid shape, disallowed domain, rate-limited, or the email is
    /// already a registered user. The caller shows the same generic response in
    /// every case so the flow never reveals which one occurred.
    /// </summary>
    public async Task<PendingSignupStart?> StartAsync(string email, string ip, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        if (!EmailAddress.HasValidShape(email)) return null;
        var normalised = AuthService.NormaliseEmail(email);

        // Site-wide allow-list (when the SiteAdmin set one) gates every signup,
        // exactly as AccountService.SignupAsync does for the SMTP-off flow.
        var allowed = await _settings.GetSignupAllowedDomainsAsync(ct);
        if (allowed is not null)
        {
            var domain = EmailAddress.DomainOf(normalised) ?? string.Empty;
            if (!allowed.Contains(domain)) return null;
        }

        // Throttle the (email-sending) start step on the same windows as login.
        if (await _auth.IsRateLimitedAsync(normalised, ip, now, ct))
        {
            await _auth.RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return null;
        }

        // Opportunistic cleanup + supersede in one pass: drop every expired row
        // (bounds the table) and any still-in-flight attempt for this email so
        // the newest start is canonical and the partial unique index holds.
        // pending_signups is org-less by design and carries no tenant query
        // filter, so the reads in this service have nothing to bypass.
        var stale = await _db.PendingSignups
            .Where(p => p.ExpiresAt <= now || (p.Email == normalised && p.CompletedAt == null))
            .ToListAsync(ct);
        if (stale.Count > 0) _db.PendingSignups.RemoveRange(stale);

        // Don't reveal that the email already has an account: create no row and
        // send no email, but still return null so the caller's response is
        // indistinguishable from a fresh signup (no account enumeration).
        // Fence category 1 (pre-auth routing): existence-only probe on the typed email, so
        // the response can't be used to enumerate accounts.
        var alreadyUser = await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == normalised, ct);
        if (alreadyUser)
        {
            await _db.SaveChangesAsync(ct); // persist the cleanup
            _logger.LogInformation("Signup start for an already-registered email — returning generic response.");
            return null;
        }

        var (linkToken, linkHash) = TokenIssuer.Issue();
        var code = GenerateCode();
        _db.PendingSignups.Add(new PendingSignup
        {
            Email = normalised,
            LinkTokenHash = linkHash,
            CodeHash = HashCode(code, linkHash),
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        });
        await _db.SaveChangesAsync(ct);
        await _auth.RecordAttemptAsync(normalised, ip, succeeded: true, now, ct);
        _logger.LogInformation("Pending signup started for {Email}.", normalised);
        return new PendingSignupStart(linkToken, code);
    }

    /// <summary>
    /// Verifies a signup by its plaintext link token. Stamps <c>verified_at</c>
    /// on first success and returns the row; returns <see langword="null"/> for
    /// unknown / expired / already-completed tokens.
    /// </summary>
    public async Task<PendingSignup?> VerifyByTokenAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var hash = TokenIssuer.Sha256Hex(rawToken);
        var now = _clock.GetUtcNow().UtcDateTime;
        var row = await _db.PendingSignups
            .FirstOrDefaultAsync(p => p.LinkTokenHash == hash, ct);
        if (row is null || row.CompletedAt is not null || row.ExpiresAt <= now) return null;
        if (row.VerifiedAt is null)
        {
            row.VerifiedAt = now;
            await _db.SaveChangesAsync(ct);
        }
        return row;
    }

    /// <summary>
    /// Verifies a signup by the 6-digit code the visitor typed, against the
    /// active row for <paramref name="email"/>. Stamps <c>verified_at</c> on
    /// first success and returns the row; returns <see langword="null"/> on any
    /// mismatch / expiry.
    /// </summary>
    public async Task<PendingSignup?> VerifyByCodeAsync(string email, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var trimmed = code.Replace(" ", "").Trim();
        if (trimmed.Length != 6 || !trimmed.All(char.IsDigit)) return null;
        var normalised = AuthService.NormaliseEmail(email);
        var now = _clock.GetUtcNow().UtcDateTime;
        var row = await _db.PendingSignups
            .Where(p => p.Email == normalised && p.CompletedAt == null && p.ExpiresAt > now)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        // Brute-force guard: the 6-digit code is a small space on a row that
        // lives for ~30 minutes, so cap wrong submissions. Once the row is
        // maxed out it's dead — the visitor must request a fresh code (which
        // supersedes this row). The high-entropy link token path is unaffected. #409
        //
        // The attempt is spent before the comparison, in one conditional
        // update, so parallel guesses can't all read the same count and slip
        // past the cap together; a right code gives it back. #1208
        var spent = await _db.PendingSignups
            .Where(p => p.Id == row.Id && p.FailedCodeAttempts < MaxCodeAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.FailedCodeAttempts, p => p.FailedCodeAttempts + 1), ct);
        if (spent == 0) return null;

        var candidate = HashCode(trimmed, row.LinkTokenHash);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(row.CodeHash)))
        {
            return null;
        }
        await _db.PendingSignups
            .Where(p => p.Id == row.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.FailedCodeAttempts, p => p.FailedCodeAttempts - 1), ct);

        if (row.VerifiedAt is null)
        {
            row.VerifiedAt = now;
            await _db.SaveChangesAsync(ct);
        }
        return row;
    }

    /// <summary>
    /// Returns the verified-but-uncompleted, unexpired row for an email, or
    /// <see langword="null"/>. This is the authoritative server-side check the
    /// completion endpoint runs before trusting the verified-email cookie.
    /// </summary>
    public async Task<PendingSignup?> FindVerifiedAsync(string email, CancellationToken ct = default)
    {
        var normalised = AuthService.NormaliseEmail(email);
        var now = _clock.GetUtcNow().UtcDateTime;
        return await _db.PendingSignups
            .Where(p => p.Email == normalised
                        && p.VerifiedAt != null
                        && p.CompletedAt == null
                        && p.ExpiresAt > now)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private static string GenerateCode()
    {
        // Six independent, uniformly-drawn digits — same approach as
        // EmailMfaService.GenerateCode.
        Span<char> chars = stackalloc char[6];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
        }
        return new string(chars);
    }

    private static string HashCode(string code, string linkTokenHash)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code + ":" + linkTokenHash));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>The plaintext secrets a freshly-started signup emails to the visitor.</summary>
public sealed record PendingSignupStart(string LinkToken, string Code);
