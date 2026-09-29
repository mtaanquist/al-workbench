using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// How a cookie session was established. Stamped into the auth cookie at
/// sign-in and at every step-up so the app can answer "did this person prove
/// it was them recently?" without a server-side session table. The names are
/// persisted inside the cookie's protected properties, so renaming a member
/// silently turns every live session into a non-strong one until its next
/// sign-in - harmless, but worth knowing.
/// </summary>
public enum SignInMethod
{
    /// <summary>Email and password with no second factor enrolled.</summary>
    Password,
    /// <summary>Password followed by an authenticator-app code.</summary>
    Totp,
    /// <summary>Password followed by an emailed code.</summary>
    EmailCode,
    /// <summary>Password followed by a recovery code.</summary>
    RecoveryCode,
    /// <summary>A passkey assertion where the authenticator did not report user verification.</summary>
    Passkey,
    /// <summary>A passkey assertion with user verification (PIN or biometric) required and reported.</summary>
    PasskeyVerified,
    /// <summary>A magic link consumed from the mailbox.</summary>
    MagicLink,
    /// <summary>
    /// Microsoft Entra ID with an <c>auth_time</c> in the token: Microsoft
    /// says when the person last authenticated interactively, and that moment
    /// is the strong one. Entra only supplies the claim when asked with
    /// <c>max_age</c>, which the step-up handshake does.
    /// </summary>
    Entra,
    /// <summary>
    /// Microsoft Entra ID without <c>auth_time</c>: Microsoft may have reused
    /// its existing browser session, so nothing proves the person was there.
    /// Weak, like a password.
    /// </summary>
    EntraSso,
}

/// <summary>
/// The two cookie stamps behind step-up: how the session was established and
/// when the person last proved it with a second factor. They live in the auth
/// properties, because <c>CookieSessionRevalidation</c> rebuilds the claims
/// from the user row and would drop a claim written only at sign-in, and are
/// mirrored onto the principal for the code that only sees claims. See
/// ".design/auth-and-audit.md", "Step-up for sensitive tools".
/// </summary>
public static class StepUpAuth
{
    /// <summary>How long a strong sign-in or step-up keeps a session fresh for gated tools.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>Auth-properties key: the <see cref="SignInMethod"/> name.</summary>
    public const string MethodKey = "auth_method";

    /// <summary>Auth-properties key: UTC round-trip timestamp of the last strong sign-in or step-up.</summary>
    public const string StrongAuthAtKey = "strong_auth_at";

    /// <summary>Claim type mirroring <see cref="MethodKey"/>.</summary>
    public const string MethodClaim = "auth_method";

    /// <summary>Claim type mirroring <see cref="StrongAuthAtKey"/>.</summary>
    public const string StrongAuthAtClaim = "strong_auth_at";

    /// <summary>
    /// The methods that count as proving presence with more than a password.
    /// Passkey without reported user verification does not, because the
    /// authenticator may have accepted a touch alone; magic link does not,
    /// because the mailbox is the only factor; Microsoft without
    /// <c>auth_time</c> does not, because Microsoft may have reused its own
    /// session without the person doing anything.
    /// </summary>
    public static bool IsStrong(SignInMethod method) => method is
        SignInMethod.Totp or SignInMethod.EmailCode or SignInMethod.RecoveryCode
        or SignInMethod.PasskeyVerified or SignInMethod.Entra;

    /// <summary>
    /// Writes the two stamps exactly as given. <paramref name="strongAuthAt"/>
    /// is the caller's decision: a fresh sign-in passes "now" for a strong
    /// method and null otherwise (<see cref="StrongMomentFor"/>), a re-issue
    /// of the same session passes the moment it already had, and an Entra
    /// sign-in passes the token's <c>auth_time</c>.
    /// </summary>
    public static void Stamp(AuthenticationProperties properties, SignInMethod method, DateTime? strongAuthAt)
    {
        properties.Items[MethodKey] = method.ToString();
        if (strongAuthAt is { } at)
        {
            properties.Items[StrongAuthAtKey] = at.ToString("o", CultureInfo.InvariantCulture);
        }
        else
        {
            properties.Items.Remove(StrongAuthAtKey);
        }
    }

    /// <summary>The strong moment a fresh sign-in earns: now for a strong method, nothing otherwise.</summary>
    public static DateTime? StrongMomentFor(SignInMethod method, DateTime now) => IsStrong(method) ? now : null;

    /// <summary>The method mirrored on the principal, or null when the session predates the stamps.</summary>
    public static SignInMethod? Method(ClaimsPrincipal? principal) =>
        Enum.TryParse<SignInMethod>(principal?.FindFirst(MethodClaim)?.Value, out var method) ? method : null;

    /// <summary>
    /// Copies the two stamps from the properties onto the identity as claims,
    /// replacing any earlier copy. Called at sign-in and by the cookie
    /// revalidation after it rebuilds the principal.
    /// </summary>
    public static void ApplyClaims(ClaimsIdentity identity, AuthenticationProperties properties)
    {
        foreach (var stale in identity.FindAll(c => c.Type is MethodClaim or StrongAuthAtClaim).ToList())
        {
            identity.RemoveClaim(stale);
        }
        if (properties.Items.TryGetValue(MethodKey, out var method) && !string.IsNullOrEmpty(method))
        {
            identity.AddClaim(new Claim(MethodClaim, method));
        }
        if (properties.Items.TryGetValue(StrongAuthAtKey, out var at) && !string.IsNullOrEmpty(at))
        {
            identity.AddClaim(new Claim(StrongAuthAtClaim, at));
        }
    }

    /// <summary>The last strong moment stamped on the properties, or null when the session never had one.</summary>
    public static DateTime? StrongAuthAt(AuthenticationProperties? properties) =>
        properties is not null && properties.Items.TryGetValue(StrongAuthAtKey, out var raw)
            ? ParseStamp(raw) : null;

    /// <summary>The last strong moment mirrored on the principal, or null.</summary>
    public static DateTime? StrongAuthAt(ClaimsPrincipal? principal) =>
        ParseStamp(principal?.FindFirst(StrongAuthAtClaim)?.Value);

    /// <summary>True while the strong moment is within <see cref="Window"/> of <paramref name="now"/>.</summary>
    public static bool IsFresh(DateTime? strongAuthAt, DateTime now) =>
        strongAuthAt is { } at && now - at <= Window && at <= now.Add(TimeSpan.FromMinutes(5));

    public static bool IsFresh(ClaimsPrincipal? principal, DateTime now) => IsFresh(StrongAuthAt(principal), now);

    private static DateTime? ParseStamp(string? raw) =>
        !string.IsNullOrEmpty(raw)
        && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : null;
}
