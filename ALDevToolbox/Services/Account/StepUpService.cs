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

    /// <summary>
    /// The authenticator-app counterpart of <see cref="StepUpFirstMessage"/>:
    /// the confirmation ran out between opening the setup page and sending
    /// the code.
    /// </summary>
    public const string TotpStepUpFirstMessage = "Your confirmation ran out. Confirm it's you again, then scan the new QR code.";

    public const string WrongPasswordMessage = "Password is incorrect.";

    /// <summary>The session's account could not be read: sign in again.</summary>
    public const string TotpSignInAgainMessage = "Sign in again, then set up the authenticator app.";

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
    public Task<string?> PasskeyRegistrationRefusalAsync(
        ClaimsPrincipal principal, int userId, string? password, string ip, CancellationToken ct) =>
        NewWayInRefusalAsync(principal, userId, password, ip,
            "Sign in again, then add the passkey.", StepUpFirstMessage, ct);

    /// <summary>
    /// Null when this session may turn on an authenticator app, else the
    /// sentence to show. The same proof as adding a passkey: a second factor
    /// added from a borrowed session would lock the owner out of their own
    /// password sign-in (#1614, AUTH4-3).
    /// </summary>
    public Task<string?> TotpEnrollmentRefusalAsync(
        ClaimsPrincipal principal, int userId, string? password, string ip, CancellationToken ct) =>
        NewWayInRefusalAsync(principal, userId, password, ip,
            TotpSignInAgainMessage, TotpStepUpFirstMessage, ct);

    /// <summary>
    /// Whether this session must step up before adding a sign-in method:
    /// it is not fresh and the account has a way to confirm. False means the
    /// session may go ahead, with the current password when it is not fresh.
    /// What the pages ask before showing the add controls.
    /// </summary>
    public async Task<bool> MustStepUpBeforeAddingAsync(ClaimsPrincipal principal, int userId, CancellationToken ct)
    {
        if (await IsFreshForPasskeyAsync(principal, ct)) return false;
        var methods = await AvailableMethodsAsync(userId, StepUpAuth.Method(principal), ct);
        return methods?.Any == true;
    }

    private async Task<string?> NewWayInRefusalAsync(
        ClaimsPrincipal principal, int userId, string? password, string ip,
        string signInAgain, string stepUpFirst, CancellationToken ct)
    {
        if (await IsFreshForPasskeyAsync(principal, ct)) return null;
        var methods = await AvailableMethodsAsync(userId, StepUpAuth.Method(principal), ct);
        if (methods is null) return signInAgain;
        if (methods.Any) return stepUpFirst;

        // Filtered: the signed-in user's own row, inside their org.
        var account = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Email, u.PasswordHash })
            .FirstAsync(ct);
        // Counted towards the sign-in lockout, as every current-password check is (#1614).
        return await _auth.CheckCurrentPasswordAsync(account.Email, account.PasswordHash, password, ip, ct) switch
        {
            CurrentPasswordCheck.Correct => null,
            CurrentPasswordCheck.LockedOut => AuthService.CurrentPasswordLockedOutMessage,
            _ => WrongPasswordMessage,
        };
    }
}
