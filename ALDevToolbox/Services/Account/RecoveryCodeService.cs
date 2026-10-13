using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Issues and consumes single-use recovery codes for the 2FA flow. Codes are
/// 10 chars, two five-char groups (<c>7HK3M-2QPRA</c>), drawn from an
/// unambiguous alphabet (no <c>0/O</c>, no <c>1/I/L</c>). Stored hashed via
/// BCrypt — the codes are weaker than passwords by definition but should
/// still resist offline brute force.
/// </summary>
public sealed class RecoveryCodeService
{
    public const int CodeCount = 10;
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int GroupLength = 5;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public RecoveryCodeService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Wipes any existing codes and issues a fresh batch of
    /// <see cref="CodeCount"/>. Returns the plaintext codes for one-time
    /// display; only the hashes are persisted.
    /// </summary>
    public async Task<IReadOnlyList<string>> RegenerateAsync(int userId, CancellationToken ct = default)
    {
        // Fence category 4 (explicitly scoped user-id lookup): pinned to c.UserId == userId.
        var existing = await _db.UserRecoveryCodes.IgnoreQueryFilters()
            .Where(c => c.UserId == userId).ToListAsync(ct);
        _db.UserRecoveryCodes.RemoveRange(existing);

        var now = _clock.GetUtcNow().UtcDateTime;
        var codes = new List<string>(CodeCount);
        for (var i = 0; i < CodeCount; i++)
        {
            var plain = GenerateCode();
            codes.Add(plain);
            _db.UserRecoveryCodes.Add(new UserRecoveryCode
            {
                UserId = userId,
                CodeHash = BCrypt.Net.BCrypt.HashPassword(Normalise(plain), workFactor: 10),
                CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync(ct);
        return codes;
    }

    /// <summary>
    /// Matches the supplied code against unconsumed rows; stamps the matching
    /// row with <c>consumed_at</c> on first hit. Returns true when a code was
    /// consumed.
    /// </summary>
    public async Task<bool> ConsumeAsync(int userId, string code, CancellationToken ct = default)
    {
        var normalised = Normalise(code);
        if (normalised.Length != GroupLength * 2) return false;

        // Fence category 4 (explicitly scoped user-id lookup): pinned to c.UserId == userId
        // (the half-authenticated MFA state).
        var unused = _db.UserRecoveryCodes.IgnoreQueryFilters()
            .Where(c => c.UserId == userId && c.ConsumedAt == null);
        var candidates = await unused.AsNoTracking().ToListAsync(ct);
        foreach (var row in candidates)
        {
            if (BCrypt.Net.BCrypt.Verify(normalised, row.CodeHash))
            {
                // Single use, claimed with a conditional update so two posts
                // of the same code can't both sign in. #1208
                var now = _clock.GetUtcNow().UtcDateTime;
                var claimed = await unused
                    .Where(c => c.Id == row.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);
                return claimed == 1;
            }
        }
        return false;
    }

    /// <summary>Count of still-usable codes — surfaced on the /account page.</summary>
    public async Task<int> RemainingAsync(int userId, CancellationToken ct = default) =>
        // Fence category 4 (explicitly scoped user-id lookup): pinned to c.UserId == userId.
        await _db.UserRecoveryCodes.IgnoreQueryFilters()
            .CountAsync(c => c.UserId == userId && c.ConsumedAt == null, ct);

    private static string GenerateCode()
    {
        var chars = new char[GroupLength * 2 + 1];
        for (var i = 0; i < GroupLength * 2; i++)
        {
            // RandomNumberGenerator.GetInt32 uses rejection sampling, so it's
            // unbiased over the 31-char alphabet — unlike `byte % Alphabet.Length`,
            // where 256 % 31 leaves values 0–7 slightly over-represented. #411
            chars[i < GroupLength ? i : i + 1] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        chars[GroupLength] = '-';
        return new string(chars);
    }

    /// <summary>
    /// Strips hyphens and whitespace, uppercases, before hashing or
    /// comparison. Lets the user type with or without the dash and in any
    /// case without being told their valid code is wrong.
    /// </summary>
    private static string Normalise(string code) =>
        new string((code ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
}
