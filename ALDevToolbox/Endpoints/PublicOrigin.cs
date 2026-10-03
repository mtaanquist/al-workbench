namespace ALDevToolbox.Endpoints;

/// <summary>
/// The origin (<c>scheme://host[:port]</c>) that credential-bearing email
/// links are built from, read once at startup from <c>PUBLIC_BASE_URL</c>.
/// <para>
/// Password-reset, magic-link, invite, email-change and signup-verification
/// links used to be interpolated straight from the inbound request's own
/// <c>Host</c> header. Because <c>/forgot-password</c> is anonymous, an
/// attacker could mint a valid antiforgery pair, POST the victim's address
/// with a forged <c>Host</c>, and have the victim receive a genuine email
/// pointing the single-use token at the attacker's site (issue #670).
/// </para>
/// <para>
/// When the variable is set every such link is built from it and the request
/// host is ignored. When it is unset we keep the old behaviour so existing
/// deployments don't break, and warn once at startup. Operators should also
/// set <c>AllowedHosts</c> so Kestrel's host filtering rejects a foreign
/// <c>Host</c> before any handler runs — see <c>.design/deployment.md</c>.
/// </para>
/// </summary>
public sealed class PublicOrigin
{
    public const string EnvVarName = "PUBLIC_BASE_URL";

    /// <summary>The normalised configured origin, or <c>null</c> when unset or unusable.</summary>
    public string? Configured { get; }

    /// <summary>The raw value when it was present but could not be parsed — logged as a warning at startup.</summary>
    public string? InvalidValue { get; }

    public PublicOrigin(string? configured, string? invalidValue = null)
    {
        Configured = configured;
        InvalidValue = invalidValue;
    }

    public bool IsConfigured => Configured is not null;

    /// <summary>
    /// Normalises a configured base URL: absolute, http or https only, with
    /// any trailing slash removed so callers can concatenate a rooted path.
    /// Returns <c>null</c> for blank or unusable input — a typo must not stop
    /// the app from booting, it falls back to the request host and warns.
    /// </summary>
    public static string? Parse(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;

        return trimmed.TrimEnd('/');
    }

    /// <summary>Reads and parses <c>PUBLIC_BASE_URL</c>.</summary>
    public static PublicOrigin FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable(EnvVarName);
        if (string.IsNullOrWhiteSpace(raw)) return new PublicOrigin(null);

        var parsed = Parse(raw);
        return parsed is null ? new PublicOrigin(null, raw.Trim()) : new PublicOrigin(parsed);
    }

    /// <summary>
    /// The origin to put in a link: the configured value when there is one,
    /// otherwise the request's own scheme and host (the pre-#670 behaviour).
    /// </summary>
    public string For(HttpContext ctx) =>
        Configured ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}";

    /// <summary>Startup log line describing where email links will point.</summary>
    public static void Log(ILogger logger, PublicOrigin origin)
    {
        if (origin.InvalidValue is not null)
        {
            logger.LogWarning(
                "Ignoring unusable {EnvVar} value {Value} — it must be an absolute http:// or https:// URL.",
                EnvVarName, origin.InvalidValue);
        }

        if (origin.IsConfigured)
        {
            logger.LogInformation(
                "Email links will be built from {EnvVar} ({Origin}).", EnvVarName, origin.Configured);
        }
        else
        {
            logger.LogWarning(
                "No {EnvVar} configured — password reset, magic-link, invite and email verification links will use whatever Host header the request carries, and notification emails are not sent. Set {EnvVar} (and AllowedHosts) for internet-facing deployments.",
                EnvVarName, EnvVarName);
        }
    }
}
