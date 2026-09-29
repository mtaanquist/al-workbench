using System.Security.Claims;
using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Statics shared across the endpoint extension classes: antiforgery
/// validation, claim-principal construction, attachment headers, etc.
/// </summary>
internal static class EndpointHelpers
{
    public static ClaimsIdentity BuildIdentity(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(HttpOrganizationContext.UserIdClaim, user.Id.ToString()),
            new(HttpOrganizationContext.OrganizationIdClaim, user.OrganizationId.ToString()),
            new("org_name", user.Organization?.Name ?? string.Empty),
            // Cached per-org MCP opt-out. Authoritative check still runs at
            // /mcp request time against the DB so a stale claim can't smuggle
            // a banned org back in; this claim only feeds the nav-link
            // visibility so the link disappears without a DB hit per render.
            new("org_mcp_enabled", (user.Organization?.McpEnabled ?? true) ? "true" : "false"),
            // Comma-joined ToolKey names this org has switched off, feeding the
            // sidebar and the route-access gate without a per-render DB hit. MCP
            // is folded in (derived from McpEnabled) so the gate reads one claim
            // for every tool. Refreshes on each cookie revalidation (~5 min) via
            // CookieSessionRevalidation, which rebuilds the principal from the
            // current row. Like org_mcp_enabled, this is a visibility hint only.
            new("org_disabled_tools", BuildDisabledToolsClaim(user.Organization)),
            // Comma-joined ToolKey names this org wants a recent second factor
            // for. Read by ToolAccessGate on page routes; the MCP call filter and
            // the OAuth consent check read the live row instead, so this too is
            // a hint that refreshes with the cookie.
            new(StepUpToolsClaim, string.Join(',', user.Organization?.StepUpTools ?? new List<string>())),
        };
        if (user.IsSiteAdmin)
        {
            // The boolean claim feeds IOrganizationContext.IsSiteAdmin; the
            // role claim lets [Authorize(Roles = "SiteAdmin")] work without
            // a custom policy.
            claims.Add(new Claim(HttpOrganizationContext.SiteAdminClaim, "true"));
            claims.Add(new Claim(ClaimTypes.Role, HttpOrganizationContext.SiteAdminRole));
        }
        // Tags the cookie when the signed-in user belongs to the singleton
        // system org so IOrganizationContext.IsSystemOrganization resolves
        // without a per-request DB lookup. Sign-in paths Include the org nav
        // already (see AccountService.TryLoginAsync and friends).
        if (user.Organization?.IsSystem == true)
        {
            claims.Add(new Claim(HttpOrganizationContext.SystemOrgClaim, "true"));
        }
        return new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>Claim type carrying the org's disabled tools as a comma-joined list of <see cref="Domain.Tools.ToolKey"/> names.</summary>
    public const string DisabledToolsClaim = "org_disabled_tools";

    /// <summary>
    /// Builds the <see cref="DisabledToolsClaim"/> value from the org's stored
    /// <see cref="Organization.DisabledTools"/>, folding in <c>Mcp</c> when the
    /// org has MCP switched off so the gate and nav can read one claim for every
    /// tool. A null org (shouldn't happen post-sign-in) yields an empty value.
    /// </summary>
    private static string BuildDisabledToolsClaim(Organization? org)
    {
        if (org is null) return string.Empty;
        var names = new List<string>(org.DisabledTools);
        if (!org.McpEnabled) names.Add(nameof(Domain.Tools.ToolKey.Mcp));
        return string.Join(',', names);
    }

    /// <summary>
    /// Reads the <see cref="DisabledToolsClaim"/> off a principal into a set of
    /// <see cref="Domain.Tools.ToolKey"/>s. Returns an empty set when the claim
    /// is absent (e.g. PAT / OAuth principals that don't carry it — those
    /// surfaces enforce org access at their own endpoints).
    /// </summary>
    public static HashSet<Domain.Tools.ToolKey> ReadDisabledTools(ClaimsPrincipal? user)
    {
        var raw = user?.FindFirst(DisabledToolsClaim)?.Value;
        if (string.IsNullOrEmpty(raw)) return new HashSet<Domain.Tools.ToolKey>();
        return Domain.Tools.ToolCatalog.ParseDisabled(
            raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>Claim type carrying the org's step-up tools as a comma-joined list of <see cref="Domain.Tools.ToolKey"/> names.</summary>
    public const string StepUpToolsClaim = "org_step_up_tools";

    /// <summary>
    /// Reads the <see cref="StepUpToolsClaim"/> off a principal. Empty when the
    /// claim is absent (PAT / OAuth principals), which is why those surfaces
    /// consult <see cref="Services.Tools.ToolEnablement"/> against the row.
    /// </summary>
    public static HashSet<Domain.Tools.ToolKey> ReadStepUpTools(ClaimsPrincipal? user)
    {
        var raw = user?.FindFirst(StepUpToolsClaim)?.Value;
        if (string.IsNullOrEmpty(raw)) return new HashSet<Domain.Tools.ToolKey>();
        return Domain.Tools.ToolCatalog.ParseKeys(
            raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// Issues the auth cookie for a fresh sign-in, stamping how the session
    /// was established (<see cref="Services.Account.StepUpAuth"/>). Every
    /// sign-in path goes through here so the method is never left unstamped.
    /// A strong method earns "now" as its strong moment; an Entra sign-in
    /// passes the token's <c>auth_time</c> as <paramref name="strongAuthAt"/>
    /// instead, because that is when Microsoft says the person was there.
    /// </summary>
    public static Task SignInUserAsync(HttpContext ctx, User user, Services.Account.SignInMethod method, DateTime? strongAuthAt = null)
    {
        var clock = ctx.RequestServices.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow().UtcDateTime;
        var properties = PersistentSignIn(ctx, method,
            strongAuthAt ?? Services.Account.StepUpAuth.StrongMomentFor(method, now), signedInAt: now);
        return IssueAsync(ctx, user, properties);
    }

    /// <summary>
    /// Re-issues the cookie for a session that already exists, keeping
    /// <c>signed_in_at</c> so a session a password reset should have ended
    /// cannot be rescued by re-issuing it (a step-up in the five-minute
    /// revalidation gap, for instance), and taking the method and strong
    /// moment the caller decides: a step-up passes the factor that just
    /// succeeded and "now"; a password change passes the session's own
    /// stamps unchanged, because typing the current password proves nothing
    /// new.
    /// </summary>
    public static async Task ReissueUserAsync(HttpContext ctx, User user, Services.Account.SignInMethod method, DateTime? strongAuthAt)
    {
        var clock = ctx.RequestServices.GetRequiredService<TimeProvider>();
        var current = await ctx.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var signedInAt = current.Properties?.Items.TryGetValue(SignedInAtKey, out var raw) == true
            && DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : clock.GetUtcNow().UtcDateTime;
        await IssueAsync(ctx, user, PersistentSignIn(ctx, method, strongAuthAt, signedInAt));
    }

    private static Task IssueAsync(HttpContext ctx, User user, Microsoft.AspNetCore.Authentication.AuthenticationProperties properties)
    {
        var identity = BuildIdentity(user);
        Services.Account.StepUpAuth.ApplyClaims(identity, properties);
        return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), properties);
    }

    /// <summary>
    /// Auth-properties key carrying the moment this cookie was issued, as a
    /// round-trippable UTC string. The cookie handler's own <c>IssuedUtc</c>
    /// is re-stamped on every sliding renewal, so it can't answer "was this
    /// session started before the password changed?" — this one is written at
    /// sign-in and survives renewals untouched. Read by
    /// <see cref="CookieSessionRevalidation"/>. Issue #675.
    /// </summary>
    public const string SignedInAtKey = "signed_in_at";

    /// <summary>
    /// Marks the auth cookie persistent so it survives browser restarts for
    /// the full <c>ExpireTimeSpan</c> window (see Program.cs). Without this the
    /// cookie is a session cookie and the browser drops it on close, defeating
    /// the long expiry. Also records <see cref="SignedInAtKey"/>. Returns a
    /// fresh instance per call — the auth stack mutates the properties bag
    /// during sign-in.
    /// </summary>
    public static Microsoft.AspNetCore.Authentication.AuthenticationProperties PersistentSignIn(
        HttpContext ctx, Services.Account.SignInMethod method, DateTime? strongAuthAt, DateTime signedInAt)
    {
        var props = new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true };
        props.Items[SignedInAtKey] = signedInAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        Services.Account.StepUpAuth.Stamp(props, method, strongAuthAt);
        return props;
    }

    /// <summary>Open-redirect guard: only allow same-site relative paths.</summary>
    public static string ResolveSafeReturn(string requestedReturn) =>
        !string.IsNullOrEmpty(requestedReturn)
            && Uri.IsWellFormedUriString(requestedReturn, UriKind.Relative)
            && requestedReturn.StartsWith('/')
            && !requestedReturn.StartsWith("//", StringComparison.Ordinal)
            && !requestedReturn.StartsWith("/\\", StringComparison.Ordinal)
                ? requestedReturn
                : "/";

    public static string ResolveIp(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

    /// <summary>
    /// Per-user cache key for endpoint-scoped in-memory state (e.g. the
    /// References-session cache). Falls back to the auth name when the
    /// NameIdentifier claim is absent; returns null for anonymous calls so
    /// callers can short-circuit with 401.
    /// </summary>
    public static string? OwnerKey(HttpContext ctx)
    {
        var user = ctx.User;
        if (user?.Identity?.IsAuthenticated != true) return null;
        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.Identity.Name
            ?? null;
    }

    /// <summary>
    /// Strips path-hostile characters from a release label or module name for
    /// use in <c>Content-Disposition: attachment; filename=…</c>. Conservative:
    /// keeps alphanumerics, dot, dash, and underscore; everything else becomes
    /// a single dash. Empty input falls back to a placeholder so the header
    /// stays a valid token.
    /// </summary>
    public static string SanitiseFileName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "module";
        var chars = new char[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            chars[i] = char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-';
        }
        return new string(chars);
    }

    /// <summary>
    /// True when a checkbox in <paramref name="form"/> posted an on value.
    /// </summary>
    /// <remarks>
    /// A checkbox paired with a hidden <c>false</c> - the standard way to tell
    /// "switched off" apart from "not on this form at all" - posts BOTH values.
    /// <c>StringValues</c> renders that pair as <c>"false,true"</c>, so the
    /// obvious <c>form["X"] == "true"</c> reads a ticked box as unticked. Every
    /// call site had written that comparison by hand; this is the one that knows
    /// about the pair. Shared/Switch.razor is what emits it.
    /// </remarks>
    public static bool IsChecked(IFormCollection form, string name)
    {
        foreach (var value in form[name])
        {
            if (value == "true" || value == "on") return true;
        }
        return false;
    }

    public static void WriteAttachmentHeaders(HttpContext ctx, string fileName)
    {
        ctx.Response.ContentType = "application/zip";
        var cd = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        cd.SetHttpFileName(fileName);
        ctx.Response.Headers.ContentDisposition = cd.ToString();
    }

    public static async Task<bool> ValidateAntiforgeryAsync(HttpContext ctx, IAntiforgery antiforgery, CancellationToken ct)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(ctx);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync("Antiforgery validation failed. Reload the form and try again.", ct);
            return false;
        }
    }

    public static void SetGenerationCompleteCookie(HttpContext ctx, string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        ctx.Response.Cookies.Append("aldt-gen", token, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            MaxAge = TimeSpan.FromSeconds(30),
        });
    }

    /// <summary>
    /// Renders a generator's server-side validation failure as a page in the
    /// app's own styling, rather than the plain-text dump of raw field keys it
    /// used to be (#546).
    ///
    /// The generator forms post natively so the ZIP can stream straight back,
    /// which means a validation failure lands the browser on the endpoint's
    /// response instead of the form. Until the two pages carry field-keyed
    /// error rendering of their own, this at least tells the user which fields
    /// are wrong, in words, and points them back at their still-filled form —
    /// a normal Back restores the posted values.
    ///
    /// Keep the field-name map in step with the two forms' labels: the keys are
    /// plan property names, and a user has never seen those.
    /// </summary>
    public static async Task WriteValidationPageAsync(
        HttpContext ctx, IReadOnlyDictionary<string, string> errors, string backHref, string backLabel, CancellationToken ct)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        ctx.Response.ContentType = "text/html; charset=utf-8";

        var items = string.Concat(errors.Select(e =>
            $"<li><strong>{HtmlEncode(FriendlyFieldName(e.Key))}</strong> — {HtmlEncode(e.Value)}</li>"));

        // $$ so a single brace is literal CSS and {{ }} is the interpolation.
        await ctx.Response.WriteAsync($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Check the form</title>
            <link rel="stylesheet" href="/fonts.css"><link rel="stylesheet" href="/tokens.css">
            <link rel="stylesheet" href="/components.css"><link rel="stylesheet" href="/pages.css">
            <link rel="stylesheet" href="/app.css">
            <style>body { padding: var(--space-7) var(--space-5); max-width: 640px; margin: 0 auto; }</style>
            </head><body>
              <div class="page">
                <div class="page-head"><div>
                  <h1 class="page-head__title">Check the form</h1>
                  <p class="page-head__sub">Nothing was generated. Go back and fix these, then try again — your entries are still there.</p>
                </div></div>
                <div class="alert alert--danger" role="alert"><span><ul>{{items}}</ul></span></div>
                <p><a class="btn btn--primary" href="{{HtmlEncode(backHref)}}">{{HtmlEncode(backLabel)}}</a></p>
              </div>
            </body></html>
            """, ct);
    }

    private static string HtmlEncode(string s) => System.Net.WebUtility.HtmlEncode(s);

    /// <summary>
    /// Plan property name → the label the form actually shows.
    ///
    /// The keys come from <c>nameof(plan.X)</c> inside
    /// <c>GenerationService.ValidateWorkspacePlan</c> / <c>ValidateExtensionPlan</c>,
    /// so they are C# property names and a user has never seen one.
    /// <c>GenerationFieldNameTests</c> reads that validator and fails the build
    /// if a key it can throw has no entry here — an unmapped key falls through
    /// to its raw name, which is exactly the jargon #546 is about.
    /// </summary>
    internal static string FriendlyFieldName(string key)
    {
        // Per-dependency rules key on `Dependencies[2].DepId`. The index is the
        // only part the user can act on, and it is 0-based in the plan.
        var indexed = System.Text.RegularExpressions.Regex.Match(key, @"^Dependencies\[(\d+)\]\.(\w+)$");
        if (indexed.Success)
        {
            var ordinal = int.Parse(indexed.Groups[1].Value) + 1;
            return indexed.Groups[2].Value switch
            {
                "DepId" => $"Dependency {ordinal}, ID",
                "DepName" => $"Dependency {ordinal}, name",
                "DepPublisher" => $"Dependency {ordinal}, publisher",
                "DepVersion" => $"Dependency {ordinal}, version",
                _ => $"Dependency {ordinal}",
            };
        }

        return key switch
        {
            // The New Workspace form asks for the customer, not a workspace
            // name; the plan property keeps its old name (CLAUDE.md's spine
            // rule), so the label is where the two meet.
            "WorkspaceName" => "Customer",
            "ShortName" => "Short name",
            "ExtensionName" => "Extension name",
            "Publisher" => "Publisher",
            "CustomerName" => "Customer",
            "Brief" => "Brief",
            "ApplicationVersion" => "Application version",
            "RuntimeVersion" => "Runtime version",
            "CoreIdRangeFrom" => "First object ID",
            "CoreIdRangeTo" => "Last object ID",
            "IdRangeFrom" => "First object ID",
            "IdRangeTo" => "Last object ID",
            "TemplateKey" => "Template",
            "SelectedModuleKeys" => "Modules",
            "Dependencies" => "Dependencies",
            "TenantId" => "Tenant ID",
            _ => key,
        };
    }

    public const string MfaPendingCookieName = "alwb_mfa";
    public const string MfaProtectionPurpose = "ALDevToolbox.MfaPending";
    public const string OneShotInviteCookieName = "alwb_invite_link";
    public const string OneShotInviteProtectionPurpose = "ALDevToolbox.OneShotInviteUrl";
    public const string OneShotRecoveryCodesCookieName = "alwb_recovery_codes";
    public const string OneShotRecoveryCodesProtectionPurpose = "ALDevToolbox.OneShotRecoveryCodes";
    public const string OneShotPatCookieName = "alwb_pat_created";
    public const string OneShotPatProtectionPurpose = "ALDevToolbox.OneShotPat";
    public static readonly TimeSpan MfaCookieLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan OneShotCookieLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The "password OK, second factor pending" state, and since step-up
    /// (<see cref="Services.Account.StepUpAuth"/>) also the "signed in, confirm
    /// it's you" state: <paramref name="StepUp"/> marks the latter, and the
    /// two availability flags tell the challenge page whether to offer a
    /// passkey or a Microsoft re-sign-in beside the code methods. The trailing
    /// parameters default so a cookie issued before they existed still reads.
    /// </summary>
    public sealed record MfaPending(
        int UserId, bool TotpEnabled, bool EmailMfaEnabled, DateTime IssuedAt, string ReturnUrl,
        bool StepUp = false, bool PasskeyAvailable = false, bool EntraAvailable = false);

    public static void SetMfaPendingCookie(HttpContext ctx, IDataProtectionProvider protection, MfaPending state)
    {
        var protector = protection.CreateProtector(MfaProtectionPurpose);
        var payload = protector.Protect(JsonSerializer.Serialize(state));
        ctx.Response.Cookies.Append(MfaPendingCookieName, payload, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            MaxAge = MfaCookieLifetime,
        });
    }

    public static MfaPending? ReadMfaPendingCookie(HttpContext ctx, IDataProtectionProvider protection, TimeProvider clock)
    {
        if (!ctx.Request.Cookies.TryGetValue(MfaPendingCookieName, out var raw) || string.IsNullOrEmpty(raw)) return null;
        try
        {
            var protector = protection.CreateProtector(MfaProtectionPurpose);
            var json = protector.Unprotect(raw);
            var state = JsonSerializer.Deserialize<MfaPending>(json);
            if (state is null) return null;
            if (clock.GetUtcNow().UtcDateTime - state.IssuedAt > MfaCookieLifetime) return null;
            return state;
        }
        catch
        {
            return null;
        }
    }

    public static void ClearMfaPendingCookie(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(MfaPendingCookieName);

    public const string SignupVerifiedCookieName = "alwb_signup_verified";
    public const string SignupVerifiedProtectionPurpose = "ALDevToolbox.SignupVerified";
    public static readonly TimeSpan SignupVerifiedCookieLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Carries the freshly-verified signup email from the verify step to the
    /// details step. A UX hint only — the completion endpoint re-validates the
    /// email against a verified, uncompleted <c>pending_signups</c> row, so a
    /// forged or replayed cookie is inert. Matches the signed-cookie posture of
    /// <see cref="MfaPending"/>.
    /// </summary>
    public sealed record SignupVerified(int PendingSignupId, string Email, DateTime IssuedAt);

    public static void SetSignupVerifiedCookie(HttpContext ctx, IDataProtectionProvider protection, SignupVerified state)
    {
        var protector = protection.CreateProtector(SignupVerifiedProtectionPurpose);
        var payload = protector.Protect(JsonSerializer.Serialize(state));
        ctx.Response.Cookies.Append(SignupVerifiedCookieName, payload, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            MaxAge = SignupVerifiedCookieLifetime,
        });
    }

    public static SignupVerified? ReadSignupVerifiedCookie(HttpContext ctx, IDataProtectionProvider protection, TimeProvider clock)
    {
        if (!ctx.Request.Cookies.TryGetValue(SignupVerifiedCookieName, out var raw) || string.IsNullOrEmpty(raw)) return null;
        try
        {
            var protector = protection.CreateProtector(SignupVerifiedProtectionPurpose);
            var state = JsonSerializer.Deserialize<SignupVerified>(protector.Unprotect(raw));
            if (state is null) return null;
            if (clock.GetUtcNow().UtcDateTime - state.IssuedAt > SignupVerifiedCookieLifetime) return null;
            return state;
        }
        catch
        {
            return null;
        }
    }

    public static void ClearSignupVerifiedCookie(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(SignupVerifiedCookieName);

    /// <summary>
    /// Writes a protected one-shot cookie: a short-lived, Data-Protection-signed
    /// value the reader consumes once and clears. Each named one-shot helper
    /// delegates here with its own cookie name and protection purpose so the
    /// shared <see cref="CookieOptions"/> and lifetime stay in one place.
    /// </summary>
    private static void SetOneShotCookie(HttpContext ctx, IDataProtectionProvider protection, string name, string purpose, string value)
    {
        var protector = protection.CreateProtector(purpose);
        var payload = protector.Protect(value);
        ctx.Response.Cookies.Append(name, payload, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            MaxAge = OneShotCookieLifetime,
        });
    }

    /// <summary>
    /// Reads and clears a protected one-shot cookie, returning the unprotected
    /// payload or null when the cookie is absent, empty, or fails to unprotect
    /// (tamper or expiry — treated as absent). The clear is skipped when the
    /// response has already started: once the prerender HTML has been flushed
    /// (e.g. when this runs from a Blazor OnAfterRenderAsync on the interactive
    /// circuit), touching Response.Cookies throws "Headers are read-only". The
    /// cookie has a short TTL and is consumed on this read either way, so
    /// silently skipping the delete is safe.
    /// </summary>
    private static string? ReadAndClearOneShotCookie(HttpContext ctx, IDataProtectionProvider protection, string name, string purpose)
    {
        if (!ctx.Request.Cookies.TryGetValue(name, out var raw) || string.IsNullOrEmpty(raw)) return null;
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.Cookies.Delete(name);
        }
        try
        {
            var protector = protection.CreateProtector(purpose);
            return protector.Unprotect(raw);
        }
        catch
        {
            return null;
        }
    }

    public static void SetOneShotInviteCookie(HttpContext ctx, IDataProtectionProvider protection, string url) =>
        SetOneShotCookie(ctx, protection, OneShotInviteCookieName, OneShotInviteProtectionPurpose, url);

    public static string? ReadAndClearOneShotInviteCookie(HttpContext ctx, IDataProtectionProvider protection) =>
        ReadAndClearOneShotCookie(ctx, protection, OneShotInviteCookieName, OneShotInviteProtectionPurpose);

    public static void SetOneShotRecoveryCodesCookie(HttpContext ctx, IDataProtectionProvider protection, IEnumerable<string> codes) =>
        SetOneShotCookie(ctx, protection, OneShotRecoveryCodesCookieName, OneShotRecoveryCodesProtectionPurpose, string.Join('\n', codes));

    public static string[]? ReadAndClearOneShotRecoveryCodesCookie(HttpContext ctx, IDataProtectionProvider protection) =>
        ReadAndClearOneShotCookie(ctx, protection, OneShotRecoveryCodesCookieName, OneShotRecoveryCodesProtectionPurpose)
            ?.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Stash for the one-shot Personal Access Token reveal screen. The
    /// plaintext appears only here — once the cookie is consumed, the user
    /// can't see it again. Lifetime matches the other one-shot cookies.
    /// </summary>
    public sealed record OneShotPat(int TokenId, string Plaintext, string Name, DateTime CreatedAt, DateTime? ExpiresAt);

    public static void SetOneShotPatCookie(HttpContext ctx, IDataProtectionProvider protection, OneShotPat value) =>
        SetOneShotCookie(ctx, protection, OneShotPatCookieName, OneShotPatProtectionPurpose, JsonSerializer.Serialize(value));

    public static OneShotPat? ReadAndClearOneShotPatCookie(HttpContext ctx, IDataProtectionProvider protection)
    {
        var raw = ReadAndClearOneShotCookie(ctx, protection, OneShotPatCookieName, OneShotPatProtectionPurpose);
        if (raw is null) return null;
        try
        {
            return JsonSerializer.Deserialize<OneShotPat>(raw);
        }
        catch
        {
            return null;
        }
    }
}
