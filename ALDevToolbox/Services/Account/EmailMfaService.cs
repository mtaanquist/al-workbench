using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Email-based 2FA codes: 6-digit numeric, single use, 10-minute lifetime.
/// Stored hash is <c>SHA-256(code + ":" + user_id)</c>; the user-id binding
/// stops the short numeric space being a global rainbow-table target. Per-
/// user rate limit: 3 issues per 10 minutes, counted from the
/// <c>password_reset_tokens</c> rows with purpose
/// <see cref="TokenPurpose.EmailMfaChallenge"/> (survives process restart;
/// no extra in-memory state).
///
/// Separate from <see cref="PasswordResetService"/> because the storage
/// shape — short numeric + user-id-bound hash — is intentionally different.
/// </summary>
public sealed class EmailMfaService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int MaxIssuesPerWindow = 3;
    public static readonly TimeSpan IssueWindow = TimeSpan.FromMinutes(10);
    /// <summary>Wrong-code guesses allowed on a code before a new one has to be sent (#409, #1432).</summary>
    public const int MaxVerifyAttempts = 5;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public EmailMfaService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Generates and persists a fresh 6-digit code for the user. Returns the
    /// plaintext for the caller to email; only the user-bound SHA-256 hash
    /// lands in the database. Retires the user's older live codes, so a new
    /// code is the way out of a wrong-guess lockout. Returns <c>null</c> when
    /// the per-user rate limit is exceeded.
    /// </summary>
    public async Task<string?> IssueChallengeAsync(int userId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var since = now - IssueWindow;

        // The user row lock queues two issues for the same user one after the
        // other, so the throttle count and the retire-then-insert below can't
        // interleave: two sends at once can neither both pass a full throttle
        // nor leave two live codes behind. Raw SQL, like
        // ClaimEmailChangeTokenAsync: a row lock pinned to the user id, with no
        // query filter to bypass.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM users WHERE id = {userId} FOR UPDATE", ct);

        // Fence category 4 (explicitly scoped user-id lookup): pinned to t.UserId == userId
        // (the half-authenticated login state or the signed-in user).
        var userChallenges = _db.PasswordResetTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == userId && t.Purpose == TokenPurpose.EmailMfaChallenge);
        var recent = await userChallenges.CountAsync(t => t.CreatedAt >= since, ct);
        if (recent >= MaxIssuesPerWindow) return null;

        // A new code retires every older one, so only the code in the latest
        // email works and it starts with a full MaxVerifyAttempts. That is the
        // way out of a lockout; the issue throttle above is what still bounds
        // total guesses (#1432).
        await userChallenges
            .Where(t => t.ConsumedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now), ct);

        var code = GenerateCode();
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = userId,
            TokenHash = HashCode(code, userId),
            Purpose = TokenPurpose.EmailMfaChallenge,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return code;
    }

    /// <summary>
    /// Consumes a code; returns true on first valid match. Single-use is
    /// enforced via <c>ConsumedAt</c>.
    /// </summary>
    public async Task<bool> VerifyAsync(int userId, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var trimmed = code.Replace(" ", "").Trim();
        if (trimmed.Length != 6 || !trimmed.All(char.IsDigit)) return false;
        var now = _clock.GetUtcNow().UtcDateTime;
        var hash = HashCode(trimmed, userId);

        // All live (unconsumed, unexpired) challenges for this user. Issuing
        // retires the older codes, so this is normally one row; rows issued
        // before #1432 could still overlap, and any of them is acceptable.
        //
        // Fence category 4 (explicitly scoped user-id lookup): pinned to t.UserId == userId
        // (the half-authenticated login state or the signed-in user). The writes
        // below narrow this same query to one of its rows.
        var userChallenges = _db.PasswordResetTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == userId && t.Purpose == TokenPurpose.EmailMfaChallenge);
        var live = await userChallenges
            .Where(t => t.ConsumedAt == null && t.ExpiresAt > now)
            .AsNoTracking()
            .ToListAsync(ct);
        if (live.Count == 0) return false;

        // Brute-force guard: cap total wrong guesses across the live window so
        // the 6-digit space can't be walked before the codes expire. Once the
        // cap is hit the user must request a fresh code, which retires this one
        // and starts a new allowance; the issue throttle bounds how often that
        // can happen. #409, #1432
        //
        // Guesses are charged to the newest live challenge, and the charge is
        // made before comparing, in one conditional update, so parallel
        // guesses can't all read the same total and slip past the cap
        // together. A right code gives the attempt back. #1208
        var newest = live.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).First();
        var allowance = MaxVerifyAttempts - live.Where(t => t.Id != newest.Id).Sum(t => t.FailedAttempts);
        var charged = await userChallenges
            .Where(t => t.Id == newest.Id && t.FailedAttempts < allowance)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.FailedAttempts, t => t.FailedAttempts + 1), ct);
        if (charged == 0) return false;

        var match = live.FirstOrDefault(t => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(t.TokenHash), Encoding.ASCII.GetBytes(hash)));
        if (match is null) return false;

        await userChallenges
            .Where(t => t.Id == newest.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.FailedAttempts, t => t.FailedAttempts - 1), ct);
        // Single use, claimed: of two posts of the same right code, one wins.
        var consumed = await userChallenges
            .Where(t => t.Id == match.Id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now), ct);
        return consumed == 1;
    }

    /// <summary>Flips <see cref="User.EmailMfaEnabled"/>. Caller verifies the code first.</summary>
    public async Task EnableAsync(int userId, CancellationToken ct = default)
    {
        // Fence category 4 (explicitly scoped user-id lookup): pinned to u.Id == userId.
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        user.EmailMfaEnabled = true;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DisableAsync(int userId, CancellationToken ct = default)
    {
        // Fence category 4 (explicitly scoped user-id lookup): pinned to u.Id == userId.
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        user.EmailMfaEnabled = false;
        await _db.SaveChangesAsync(ct);
    }

    private static string GenerateCode()
    {
        // Six independent digits via a uniformly-distributed 32-bit draw per
        // digit. Using `random % 10` on a Span<byte> would skew the
        // distribution slightly; not material at this scale but cheap to do
        // right.
        Span<char> chars = stackalloc char[6];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
        }
        return new string(chars);
    }

    private static string HashCode(string code, int userId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code + ":" + userId));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
