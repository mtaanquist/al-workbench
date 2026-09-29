using System.Text;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Resolver for the WebAuthn relying-party-id and allowed origins. Read at
/// service-construction time from configuration. The RP id must be a
/// registrable suffix of every origin the user accesses the app from;
/// origins are the full <c>https://host</c> values the browser will compute
/// during the ceremony.
/// </summary>
public sealed record WebAuthnConfig(string RpId, IReadOnlyList<string> Origins, string RpName)
{
    public static WebAuthnConfig FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Auth:WebAuthn");
        var rpId = section["RpId"];
        var rpName = section["RpName"] ?? "AL Workbench";
        // Accept either the .NET-native indexed array form (Origins__0,
        // Origins__1, …) or a single comma-separated `OriginsCsv` value —
        // the latter is friendlier in a flat docker-compose .env file.
        var origins = section.GetSection("Origins").Get<string[]>();
        if (origins is null || origins.Length == 0)
        {
            var csv = section["OriginsCsv"];
            if (!string.IsNullOrWhiteSpace(csv))
            {
                origins = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }
        if ((origins is null || origins.Length == 0) && !string.IsNullOrEmpty(rpId))
        {
            origins = new[] { $"https://{rpId}" };
        }
        return new WebAuthnConfig(rpId ?? string.Empty, origins ?? Array.Empty<string>(), rpName);
    }
}

/// <summary>
/// WebAuthn (passkey) ceremonies wrapping <see cref="IFido2"/>. The challenge
/// for each ceremony round-trips via a short-lived
/// <c>IDataProtector</c>-protected cookie so we don't churn a state table;
/// each successful ceremony clears its cookie. A successful assertion is
/// full authentication — passkey replaces password + 2FA together.
/// </summary>
public sealed class PasskeyService
{
    public const string ChallengeProtectionPurpose = "ALDevToolbox.PasskeyChallenge";
    public const string RegistrationCookieName = "aldt_pk_reg";
    public const string LoginCookieName = "aldt_pk_login";
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;
    private readonly IFido2 _fido2;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;
    private readonly WebAuthnConfig _config;
    private readonly DeploymentIdentity _deployment;

    public PasskeyService(
        AppDbContext db,
        IFido2 fido2,
        IDataProtectionProvider protection,
        TimeProvider clock,
        WebAuthnConfig config,
        DeploymentIdentity deployment)
    {
        _db = db;
        _fido2 = fido2;
        _protector = protection.CreateProtector(ChallengeProtectionPurpose);
        _clock = clock;
        _config = config;
        _deployment = deployment;
    }

    /// <summary>Did the operator configure a WebAuthn RP id? Passkey UI hides itself when this is false.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_config.RpId);

    /// <summary>
    /// Builds the <see cref="CredentialCreateOptions"/> JSON for the browser
    /// to pass into <c>navigator.credentials.create</c>. Returns the options
    /// and the protected challenge envelope to set on a short-lived cookie.
    /// </summary>
    public async Task<(CredentialCreateOptions Options, string ProtectedChallenge)> BeginRegistrationAsync(
        int userId, CancellationToken ct = default)
    {
        AssertConfigured();
        // Fence category 4 (explicitly scoped user-id lookup): both reads pinned to the
        // signed-in user's id.
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        var existing = await _db.UserPasskeys.IgnoreQueryFilters()
            .Where(p => p.UserId == userId)
            .Select(p => p.CredentialId)
            .ToListAsync(ct);
        var excludeCredentials = existing
            .Select(id => new PublicKeyCredentialDescriptor(id))
            .ToList();

        var fidoUser = new Fido2User
        {
            Id = BitConverter.GetBytes(user.Id),
            Name = user.Email,
            DisplayName = user.DisplayName,
        };
        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = fidoUser,
            ExcludeCredentials = excludeCredentials,
            AuthenticatorSelection = AuthenticatorSelection.Default,
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var envelope = JsonSerializer.Serialize(new ChallengeEnvelope(userId, options.Challenge, _clock.GetUtcNow().UtcDateTime));
        return (options, _protector.Protect(envelope));
    }

    /// <summary>Validates the attestation response and persists the new credential.</summary>
    public async Task<UserPasskey> CompleteRegistrationAsync(
        int userId,
        string nickname,
        AuthenticatorAttestationRawResponse rawResponse,
        string protectedChallenge,
        CancellationToken ct = default)
    {
        AssertConfigured();
        var envelope = UnprotectEnvelope(protectedChallenge);
        if (envelope.UserId != userId)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "Passkey registration session expired. Try again." });
        }

        var trimmedNickname = string.IsNullOrWhiteSpace(nickname) ? "Passkey" : nickname.Trim();
        if (trimmedNickname.Length > 80) trimmedNickname = trimmedNickname[..80];

        IsCredentialIdUniqueToUserAsyncDelegate isUnique = async (args, _) =>
        {
            var cid = args.CredentialId;
            // Fence category 6 (existence-only uniqueness probe): credential ids are unique
            // deployment-wide; projects a bool, never a row.
            return !await _db.UserPasskeys.IgnoreQueryFilters().AnyAsync(p => p.CredentialId == cid, ct);
        };

        var originalOptions = ReconstructCreateOptions(envelope.Challenge, userId);
        var result = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = rawResponse,
            OriginalOptions = originalOptions,
            IsCredentialIdUniqueToUserCallback = isUnique,
        }, ct);

        var now = _clock.GetUtcNow().UtcDateTime;
        var passkey = new UserPasskey
        {
            UserId = userId,
            CredentialId = result.Id,
            PublicKey = result.PublicKey,
            SignCounter = result.SignCount,
            Transports = string.Join(',', result.Transports?.Select(t => t.ToString().ToLowerInvariant()) ?? Array.Empty<string>()),
            Aaguid = result.AaGuid,
            Nickname = trimmedNickname,
            CreatedAt = now,
        };
        _db.UserPasskeys.Add(passkey);
        await _db.SaveChangesAsync(ct);
        return passkey;
    }

    /// <summary>
    /// Builds the assertion options for sign-in. When <paramref name="emailHint"/>
    /// is supplied, narrows the allow-list to that user's credentials;
    /// otherwise issues a discoverable-credential challenge.
    /// </summary>
    public async Task<(AssertionOptions Options, string ProtectedChallenge)> BeginLoginAsync(string? emailHint, CancellationToken ct = default) =>
        await BeginLoginCoreAsync(emailHint, forUserId: null, requireUserVerification: false, ct);

    /// <summary>
    /// The step-up variant: the allow-list is pinned to the signed-in user's
    /// own passkeys and user verification (PIN or biometric) is required, so
    /// a touch alone cannot confirm it is them. The requirement rides in the
    /// protected envelope so <see cref="CompleteLoginAsync"/> enforces it on
    /// the way back rather than trusting the browser.
    /// </summary>
    public async Task<(AssertionOptions Options, string ProtectedChallenge)> BeginStepUpAsync(int userId, CancellationToken ct = default) =>
        await BeginLoginCoreAsync(emailHint: null, forUserId: userId, requireUserVerification: true, ct);

    private async Task<(AssertionOptions Options, string ProtectedChallenge)> BeginLoginCoreAsync(
        string? emailHint, int? forUserId, bool requireUserVerification, CancellationToken ct)
    {
        AssertConfigured();
        var allowCredentials = new List<PublicKeyCredentialDescriptor>();
        int? userId = null;
        if (forUserId is { } pinnedUserId)
        {
            userId = pinnedUserId;
            // Filtered: the caller is signed in, so the query stays inside their org.
            var creds = await _db.UserPasskeys
                .Where(p => p.UserId == pinnedUserId)
                .Select(p => p.CredentialId)
                .ToListAsync(ct);
            allowCredentials = creds.Select(id => new PublicKeyCredentialDescriptor(id)).ToList();
            if (allowCredentials.Count == 0)
            {
                throw new PlanValidationException(new Dictionary<string, string> { ["Passkey"] = "You don't have a passkey on this account." });
            }
        }
        else if (!string.IsNullOrWhiteSpace(emailHint))
        {
            var normalised = emailHint.Trim().ToLowerInvariant();
            // Fence category 1 (pre-auth routing): passkey sign-in, before any cookie exists;
            // pinned to the typed email and then to that user's credentials.
            var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == normalised, ct);
            if (user is not null)
            {
                userId = user.Id;
                // Fence category 1 (pre-auth routing): pinned to the user resolved above.
                var creds = await _db.UserPasskeys.IgnoreQueryFilters()
                    .Where(p => p.UserId == user.Id)
                    .Select(p => p.CredentialId)
                    .ToListAsync(ct);
                allowCredentials = creds.Select(id => new PublicKeyCredentialDescriptor(id)).ToList();
            }

            // Account-enumeration guard: an unknown email (or a known one with no
            // passkeys enrolled) used to return an empty allow-list while a real
            // user with passkeys returned a populated one — letting an attacker
            // probe which emails exist (and how many passkeys they have). Return a
            // single deterministic *synthetic* descriptor so the response shape is
            // indistinguishable. The id is keyed by the per-deployment secret, so
            // an attacker can't recompute it to tell a synthetic entry from a real
            // credential; an assertion against it simply fails to match, exactly
            // like an empty list did. See #490.
            if (allowCredentials.Count == 0)
            {
                allowCredentials = new List<PublicKeyCredentialDescriptor>
                {
                    new(SyntheticCredentialId(normalised)),
                };
            }
        }

        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowCredentials,
            UserVerification = requireUserVerification
                ? UserVerificationRequirement.Required
                : UserVerificationRequirement.Preferred,
        });
        var envelope = JsonSerializer.Serialize(new ChallengeEnvelope(userId, options.Challenge, _clock.GetUtcNow().UtcDateTime, requireUserVerification));
        return (options, _protector.Protect(envelope));
    }

    /// <summary>
    /// A stable, per-email pseudo credential id returned when the email hint
    /// matches no real credential, so the allow-list shape can't be used to
    /// enumerate accounts (see <see cref="BeginLoginAsync"/>). Keyed by the
    /// per-deployment secret via HMAC so it is deterministic (a repeated probe
    /// of the same email yields the same id — a per-request random id would
    /// itself distinguish synthetic from real) yet unguessable to an attacker
    /// who doesn't know the secret. 32 bytes matches a typical credential id.
    /// </summary>
    private byte[] SyntheticCredentialId(string normalisedEmail)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(_deployment.Id));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes("passkey-synthetic:" + normalisedEmail));
    }

    /// <summary>
    /// Validates the assertion response, bumps the credential's counter, and
    /// returns the authenticated user together with whether the authenticator
    /// reported user verification. A non-monotonic counter (signal of a
    /// cloned authenticator) raises <see cref="PlanValidationException"/>.
    /// A step-up envelope (<see cref="BeginStepUpAsync"/>) is refused here;
    /// <see cref="CompleteStepUpAsync"/> finishes those.
    /// </summary>
    public async Task<(User User, bool UserVerified)> CompleteLoginAsync(
        AuthenticatorAssertionRawResponse rawResponse,
        string protectedChallenge,
        CancellationToken ct = default)
    {
        AssertConfigured();
        var envelope = UnprotectEnvelope(protectedChallenge);

        var credentialId = rawResponse.RawId;
        // Fence category 1 (pre-auth routing): passkey assertion; pinned to the credential
        // id the authenticator returned, then to that passkey's user.
        var passkey = await _db.UserPasskeys.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.CredentialId == credentialId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["Passkey"] = "Unknown passkey." });

        // Fence category 1 (pre-auth routing): pinned to the passkey's own UserId.
        var user = await _db.Users.IgnoreQueryFilters()
            .Include(u => u.Organization)
            .FirstAsync(u => u.Id == passkey.UserId, ct);
        if (user.Status != UserStatus.Active)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Passkey"] = "This account isn't active." });
        }
        if (envelope.RequireUserVerification)
        {
            // A step-up envelope is finished by CompleteStepUpAsync, which
            // stays inside the tenant fence; it must not double as a sign-in.
            throw new PlanValidationException(new Dictionary<string, string> { ["Passkey"] = "That passkey check wasn't started as a sign-in." });
        }

        IsUserHandleOwnerOfCredentialIdAsync isOwner = (args, _) =>
            Task.FromResult(BitConverter.ToInt32(args.UserHandle) == passkey.UserId);

        var result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = rawResponse,
            OriginalOptions = ReconstructAssertionOptions(envelope.Challenge, envelope.RequireUserVerification),
            StoredPublicKey = passkey.PublicKey,
            StoredSignatureCounter = (uint)passkey.SignCounter,
            IsUserHandleOwnerOfCredentialIdCallback = isOwner,
        }, ct);

        if (result.SignCount <= passkey.SignCounter && result.SignCount != 0)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Passkey"] = "Passkey counter mismatch — refusing sign-in." });
        }

        passkey.SignCounter = result.SignCount;
        passkey.LastUsedAt = _clock.GetUtcNow().UtcDateTime;
        user.LastLoginAt = passkey.LastUsedAt;
        await _db.SaveChangesAsync(ct);
        return (user, UserVerifiedFlag(rawResponse));
    }

    /// <summary>
    /// The step-up counterpart of <see cref="CompleteLoginAsync"/>: the
    /// credential must belong to <paramref name="userId"/>, the envelope must
    /// be one <see cref="BeginStepUpAsync"/> issued, and the authenticator
    /// must report user verification. Filtered throughout, because the caller
    /// is signed in and the passkey has to be theirs: no tenant crossing, and
    /// no <c>LastLoginAt</c> stamp, since this is not a sign-in.
    /// </summary>
    public async Task CompleteStepUpAsync(
        int userId,
        AuthenticatorAssertionRawResponse rawResponse,
        string protectedChallenge,
        CancellationToken ct = default)
    {
        AssertConfigured();
        var envelope = UnprotectEnvelope(protectedChallenge);
        if (!envelope.RequireUserVerification || envelope.UserId != userId)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Passkey"] = "That passkey check wasn't started for this account." });
        }
        var credentialId = rawResponse.RawId;
        var passkey = await _db.UserPasskeys
            .FirstOrDefaultAsync(p => p.CredentialId == credentialId && p.UserId == userId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["Passkey"] = "That passkey belongs to a different account." });

        IsUserHandleOwnerOfCredentialIdAsync isOwner = (args, _) =>
            Task.FromResult(BitConverter.ToInt32(args.UserHandle) == userId);
        var result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = rawResponse,
            OriginalOptions = ReconstructAssertionOptions(envelope.Challenge, requireUserVerification: true),
            StoredPublicKey = passkey.PublicKey,
            StoredSignatureCounter = (uint)passkey.SignCounter,
            IsUserHandleOwnerOfCredentialIdCallback = isOwner,
        }, ct);
        if (result.SignCount <= passkey.SignCounter && result.SignCount != 0)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Passkey"] = "Passkey counter mismatch — refusing sign-in." });
        }
        if (!UserVerifiedFlag(rawResponse))
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Passkey"] = "That passkey didn't confirm it's you." });
        }
        passkey.SignCounter = result.SignCount;
        passkey.LastUsedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The UV bit of the authenticator data's flags byte (WebAuthn 6.1: the
    /// flags follow the 32-byte RP ID hash; bit 2 is user verified). Read
    /// straight off the response the library just validated, so it is the
    /// same bytes the signature covers.
    /// </summary>
    internal static bool UserVerifiedFlag(AuthenticatorAssertionRawResponse rawResponse)
    {
        var authData = rawResponse.Response?.AuthenticatorData;
        return authData is { Length: > 32 } && (authData[32] & 0x04) != 0;
    }

    public async Task DeleteAsync(int userId, int passkeyId, CancellationToken ct = default)
    {
        // Fence category 4 (explicitly scoped user-id lookup): pinned to p.UserId == userId.
        var row = await _db.UserPasskeys.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == passkeyId && p.UserId == userId, ct);
        if (row is null) return;
        _db.UserPasskeys.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RenameAsync(int userId, int passkeyId, string newName, CancellationToken ct = default)
    {
        var trimmed = (newName ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 80)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Name"] = "Name must be 1-80 characters." });
        }
        // Fence category 4 (explicitly scoped user-id lookup): pinned to p.UserId == userId.
        var row = await _db.UserPasskeys.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == passkeyId && p.UserId == userId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["PasskeyId"] = "Passkey not found." });
        row.Nickname = trimmed;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UserPasskey>> ListForUserAsync(int userId, CancellationToken ct = default) =>
        // Fence category 4 (explicitly scoped user-id lookup): pinned to p.UserId == userId.
        await _db.UserPasskeys.IgnoreQueryFilters()
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

    private void AssertConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "WebAuthn RP id is not configured. Set Auth:WebAuthn:RpId before exposing passkey flows.");
        }
    }

    private ChallengeEnvelope UnprotectEnvelope(string protectedChallenge)
    {
        if (string.IsNullOrEmpty(protectedChallenge))
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "Passkey session expired. Try again." });
        }
        try
        {
            var json = _protector.Unprotect(protectedChallenge);
            var env = JsonSerializer.Deserialize<ChallengeEnvelope>(json)
                ?? throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "Passkey session expired. Try again." });
            if ((_clock.GetUtcNow().UtcDateTime - env.IssuedAt) > ChallengeLifetime)
            {
                throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "Passkey session expired. Try again." });
            }
            return env;
        }
        catch (Exception ex) when (ex is not PlanValidationException)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "Passkey session expired. Try again." });
        }
    }

    private CredentialCreateOptions ReconstructCreateOptions(byte[] challenge, int userId)
    {
        var user = new Fido2User { Id = BitConverter.GetBytes(userId), Name = string.Empty, DisplayName = string.Empty };
        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = user,
            ExcludeCredentials = new List<PublicKeyCredentialDescriptor>(),
            AuthenticatorSelection = AuthenticatorSelection.Default,
            AttestationPreference = AttestationConveyancePreference.None,
        });
        options.Challenge = challenge;
        return options;
    }

    private AssertionOptions ReconstructAssertionOptions(byte[] challenge, bool requireUserVerification)
    {
        // The library refuses an assertion without the UV flag when the
        // original options required it, which is what makes a step-up
        // passkey stronger than a sign-in one.
        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = new List<PublicKeyCredentialDescriptor>(),
            UserVerification = requireUserVerification
                ? UserVerificationRequirement.Required
                : UserVerificationRequirement.Preferred,
        });
        options.Challenge = challenge;
        return options;
    }

    // RequireUserVerification defaults to false so an envelope issued before
    // this field existed still deserialises as an ordinary sign-in.
    private sealed record ChallengeEnvelope(int? UserId, byte[] Challenge, DateTime IssuedAt, bool RequireUserVerification = false);
}
