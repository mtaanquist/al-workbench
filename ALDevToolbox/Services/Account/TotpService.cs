using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Net.Codecrete.QrCodeGenerator;
using OtpNet;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Result of <see cref="TotpService.BeginEnrollmentAsync"/>. The Base32 secret
/// and <c>otpauth://</c> URI are the same value in two presentations; the QR
/// PNG is rendered server-side so we don't ship a JS QR library.
/// All three are shown to the user once during setup.
/// </summary>
public sealed record TotpEnrollment(string SecretBase32, string OtpAuthUri, byte[] QrPng);

/// <summary>
/// TOTP (RFC 6238) enrollment + verification. Secret is generated server-side
/// (20 random bytes, Base32-encoded), encrypted at rest via Data Protection
/// (purpose <see cref="TotpSecretProtectionPurpose"/>), and decrypted only to
/// run the rolling-code verifier. Confirmation is two-step — the user scans
/// the QR / types the secret into their authenticator, then sends back a
/// current code to prove the seed transferred correctly.
/// </summary>
public sealed class TotpService
{
    // The purpose string is part of the ciphertext's identity: every stored TOTP
    // seed was protected under it, so it keeps the old spelling for good. The
    // issuer below is only the label the authenticator app shows, so it follows
    // the product name - enrolments made before the rename keep reading "AL Dev
    // Toolbox" in the user's app, and their codes keep verifying either way.
    public const string TotpSecretProtectionPurpose = "ALDevToolbox.UserTotpSecret";
    private const string Issuer = "AL Workbench";

    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;
    private readonly RecoveryCodeService _recoveryCodes;

    public TotpService(
        AppDbContext db,
        IDataProtectionProvider protection,
        TimeProvider clock,
        RecoveryCodeService recoveryCodes)
    {
        _db = db;
        _protector = protection.CreateProtector(TotpSecretProtectionPurpose);
        _clock = clock;
        _recoveryCodes = recoveryCodes;
    }

    /// <summary>
    /// Generates a fresh secret and persists / overwrites the unconfirmed
    /// enrollment row for the user. The returned <see cref="TotpEnrollment"/>
    /// is for one-time display: the QR / Base32 lets the user scan; submitting
    /// a current code to <see cref="ConfirmEnrollmentAsync"/> flips the
    /// enrollment to active.
    /// </summary>
    public async Task<TotpEnrollment> BeginEnrollmentAsync(int userId, CancellationToken ct = default)
    {
        // Fence category 4 (explicitly scoped user-id lookup): both reads pinned to userId.
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        var raw = RandomNumberGenerator.GetBytes(20);
        var secretBase32 = Base32Encoding.ToString(raw);
        var encrypted = _protector.Protect(secretBase32);
        var now = _clock.GetUtcNow().UtcDateTime;

        // Same category 4 read, pinned to s.UserId == userId.
        var existing = await _db.UserTotpSecrets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (existing is null)
        {
            _db.UserTotpSecrets.Add(new UserTotpSecret
            {
                UserId = userId,
                SecretEncrypted = encrypted,
                CreatedAt = now,
            });
        }
        else if (existing.ConfirmedAt is null)
        {
            // Overwrite a still-unconfirmed enrollment: the user is restarting
            // setup; the prior secret never reached an authenticator.
            existing.SecretEncrypted = encrypted;
            existing.CreatedAt = now;
        }
        else
        {
            // Already confirmed — refuse to overwrite without an explicit
            // disable first, so a hijacked session can't silently rotate the
            // factor out from under the user.
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Totp"] = "The authenticator app is already turned on. Turn it off first if you want to set it up again."
            });
        }
        await _db.SaveChangesAsync(ct);

        var label = $"{Issuer}:{user.Email}";
        var uri = $"otpauth://totp/{Uri.EscapeDataString(label)}?secret={secretBase32}&issuer={Uri.EscapeDataString(Issuer)}";
        var qr = RenderQr(uri);
        return new TotpEnrollment(secretBase32, uri, qr);
    }

    /// <summary>
    /// Verifies a code against the still-unconfirmed enrollment; on success
    /// stamps <c>confirmed_at</c>, flips <see cref="User.TotpEnabled"/>, and
    /// issues 10 fresh recovery codes (returned for one-time display). Refused
    /// once the enrollment is confirmed (issue #1422).
    /// </summary>
    public async Task<IReadOnlyList<string>> ConfirmEnrollmentAsync(int userId, string code, CancellationToken ct = default)
    {
        // Fence category 4 (explicitly scoped user-id lookup): pinned to s.UserId == userId.
        var secret = await _db.UserTotpSecrets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.UserId == userId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["Totp"] = "Start setting up the authenticator app first." });
        // Confirming again would wipe the recovery codes and hand out new ones, with nothing
        // asked but a code in its window - one that may already have signed someone in.
        // Regenerating codes asks for the password, so this path must not offer a way round it.
        if (secret.ConfirmedAt is not null)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Totp"] = "The authenticator app is already turned on. Turn it off first if you want to set it up again."
            });
        }

        var base32 = _protector.Unprotect(secret.SecretEncrypted);
        if (VerifyCode(base32, code) is not { } step)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Code"] = "That code didn't match. Check your authenticator and try again." });
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        secret.ConfirmedAt = now;
        secret.LastUsedAt = now;
        // The code that confirmed setup counts as used, like any other.
        secret.LastUsedTimeStep = step;
        // Fence category 4 (explicitly scoped user-id lookup): pinned to u.Id == userId.
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        user.TotpEnabled = true;
        await _db.SaveChangesAsync(ct);

        return await _recoveryCodes.RegenerateAsync(userId, ct);
    }

    /// <summary>
    /// Verifies a code against the user's confirmed TOTP secret. Returns false
    /// when there's no confirmed enrollment or the code is wrong / out of
    /// window, and when the code's time step is not later than the last one
    /// accepted: a code works once, so one someone else saw or relayed (a
    /// screen share during a step-up, a phishing proxy) cannot be replayed
    /// inside its ~90-second window. The step is claimed with a conditional
    /// update, like a recovery code, so two posts racing with the same code
    /// cannot both win. Stamps <c>last_used_at</c> on success.
    /// </summary>
    public async Task<bool> VerifyAsync(int userId, string code, CancellationToken ct = default)
    {
        // Fence category 4 (explicitly scoped user-id lookup): pinned to s.UserId == userId
        // (the half-authenticated MFA state).
        var confirmed = _db.UserTotpSecrets.IgnoreQueryFilters()
            .Where(s => s.UserId == userId && s.ConfirmedAt != null);
        var secret = await confirmed.AsNoTracking().FirstOrDefaultAsync(ct);
        if (secret is null) return false;
        var base32 = _protector.Unprotect(secret.SecretEncrypted);
        if (VerifyCode(base32, code) is not { } step) return false;
        var now = _clock.GetUtcNow().UtcDateTime;
        var claimed = await confirmed
            .Where(s => s.Id == secret.Id && (s.LastUsedTimeStep == null || s.LastUsedTimeStep < step))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.LastUsedTimeStep, step)
                .SetProperty(s => s.LastUsedAt, now), ct);
        return claimed == 1;
    }

    /// <summary>
    /// Drops the TOTP secret and every recovery code in one step. Flips
    /// <see cref="User.TotpEnabled"/> back to false.
    /// </summary>
    public async Task DisableAsync(int userId, CancellationToken ct = default)
    {
        // Fence category 4 (explicitly scoped user-id lookup): the three reads below are all
        // pinned to the same userId.
        var secret = await _db.UserTotpSecrets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (secret is not null) _db.UserTotpSecrets.Remove(secret);
        // Same category 4 read, pinned to c.UserId == userId.
        var codes = await _db.UserRecoveryCodes.IgnoreQueryFilters()
            .Where(c => c.UserId == userId).ToListAsync(ct);
        _db.UserRecoveryCodes.RemoveRange(codes);
        // Same category 4 read, pinned to u.Id == userId.
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        user.TotpEnabled = false;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The matched time step, or null when the code does not match.</summary>
    private long? VerifyCode(string secretBase32, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var trimmed = code.Replace(" ", "").Trim();
        if (trimmed.Length != 6 || !trimmed.All(char.IsDigit)) return null;
        var secretBytes = Base32Encoding.ToBytes(secretBase32);
        var totp = new Totp(secretBytes);
        // VerificationWindow.RfcSpecifiedNetworkDelay accepts the current step
        // and one on either side (±30s), which is RFC-recommended to handle
        // clock skew without opening a brute-force window.
        return totp.VerifyTotp(_clock.GetUtcNow().UtcDateTime, trimmed, out var step, VerificationWindow.RfcSpecifiedNetworkDelay)
            ? step
            : null;
    }

    private static byte[] RenderQr(string content)
    {
        // Quartile is the error-correction level this page has always used.
        // border is the quiet zone in modules: the QR spec wants 4, and this
        // library defaults to 0, which produces a symbol some readers refuse.
        var qr = QrCode.EncodeText(content, QrCode.Ecc.Quartile);
        return qr.ToPngBitmap(border: 4, scale: 6);
    }
}
