using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Account;

/// <summary>The ways a signed-in user can step up right now. <see cref="Any"/> is false for an account with nothing to confirm with.</summary>
public sealed record StepUpMethods(bool Totp, bool EmailCode, bool Passkey, bool Entra)
{
    public bool Any => Totp || EmailCode || Passkey || Entra;
}

/// <summary>
/// What a signed-in user can step up with, and the one rule that leans on it
/// outside the step-up page itself: whether this session may add a passkey.
/// Filtered throughout, because the caller is that user, signed in. See
/// ".design/auth-and-audit.md", "Step-up for sensitive tools" and "Passkeys
/// (WebAuthn)".
/// </summary>
public sealed class StepUpService
{
    /// <summary>
    /// The longest a strong moment counts for adding a passkey, whatever the
    /// organisation chose for its tools: a passkey is a sign-in that outlives
    /// the session, so a day-long tool window is too generous for it.
    /// </summary>
    public static readonly TimeSpan PasskeyWindowCap = TimeSpan.FromMinutes(15);

    /// <summary>
    /// What a session that has a way to confirm, but has not used it lately, is
    /// told. The page only reaches this when a confirmation ran out while it was
    /// open (otherwise it shows the confirm link instead of the button), so it
    /// says that, and the page swaps the confirm link back in beside it.
    /// </summary>
    public const string StepUpFirstMessage = "Your confirmation ran out. Choose Add a passkey again to confirm it's you, then finish adding it.";

    public const string WrongPasswordMessage = "Password is incorrect.";

    private readonly AppDbContext _db;
    private readonly PasskeyService _passkeys;
    private readonly EntraSignInService _entra;
    private readonly AuthService _auth;
    private readonly ToolEnablement _tools;
    private readonly TimeProvider _clock;

    public StepUpService(
        AppDbContext db,
        PasskeyService passkeys,
        EntraSignInService entra,
        AuthService auth,
        ToolEnablement tools,
        TimeProvider clock)
    {
        _db = db;
        _passkeys = passkeys;
        _entra = entra;
        _auth = auth;
        _tools = tools;
        _clock = clock;
    }

    /// <summary>
    /// What the step-up page would offer <paramref name="userId"/>, or null
    /// when the row is gone. An emailed code is left out of a session that
    /// began with a magic link: that would be the mailbox twice.
    /// </summary>
    public async Task<StepUpMethods?> AvailableMethodsAsync(int userId, SignInMethod? sessionMethod, CancellationToken ct)
    {
        // Filtered: the signed-in user's own row, inside their org.
        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.TotpEnabled, u.EmailMfaEnabled })
            .FirstOrDefaultAsync(ct);
        if (user is null) return null;

        var emailUsable = user.EmailMfaEnabled && sessionMethod != SignInMethod.MagicLink;
        var passkeyAvailable = _passkeys.IsConfigured
            && await _db.UserPasskeys.AnyAsync(p => p.UserId == userId, ct);
        var entraAvailable = await _db.UserExternalLogins.AnyAsync(
                l => l.UserId == userId && l.Provider == EntraSignInService.ProviderName, ct)
            && await _entra.ResolveChallengeForCurrentOrgAsync(ct) is not null;
        return new StepUpMethods(user.TotpEnabled, emailUsable, passkeyAvailable, entraAvailable);
    }

    /// <summary>
    /// True while <paramref name="principal"/> proved a second factor within
    /// the organisation's step-up window, capped at <see cref="PasskeyWindowCap"/>.
    /// </summary>
    public async Task<bool> IsFreshForPasskeyAsync(ClaimsPrincipal? principal, CancellationToken ct)
    {
        var window = await _tools.StepUpWindowAsync(ct);
        if (window > PasskeyWindowCap) window = PasskeyWindowCap;
        return StepUpAuth.IsFresh(principal, _clock.GetUtcNow().UtcDateTime, window);
    }

    /// <summary>
    /// Null when this session may add a passkey, else the sentence to show.
    /// A fresh session may. Otherwise an account with a way to step up is
    /// sent to do that, and an account with none (a password and nothing
    /// else, so far) gives its current password, which is the most it can
    /// prove and keeps a first passkey possible.
    /// </summary>
    public async Task<string?> PasskeyRegistrationRefusalAsync(
        ClaimsPrincipal principal, int userId, string? password, CancellationToken ct)
    {
        if (await IsFreshForPasskeyAsync(principal, ct)) return null;
        var methods = await AvailableMethodsAsync(userId, StepUpAuth.Method(principal), ct);
        if (methods is null) return "Sign in again, then add the passkey.";
        if (methods.Any) return StepUpFirstMessage;

        // Filtered: the signed-in user's own row, inside their org.
        var hash = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.PasswordHash)
            .FirstAsync(ct);
        return !string.IsNullOrEmpty(password) && _auth.VerifyPassword(password, hash)
            ? null
            : WrongPasswordMessage;
    }
}
