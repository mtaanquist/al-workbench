namespace ALDevToolbox.Components.Email;

/// <summary>
/// The design tokens an email uses, resolved to literal values. Email clients
/// ignore custom properties and most stylesheets, so the email components set
/// every style inline from these instead of from <c>tokens.css</c>. Each value
/// names the light-theme token it copies; change the token, change it here.
/// See <c>.design/email.md</c>.
/// </summary>
public static class EmailTheme
{
    /// <summary><c>--bg</c>: the page behind the card.</summary>
    public const string Background = "#F5F7F9";

    /// <summary><c>--surface</c>: the card.</summary>
    public const string Surface = "#FFFFFF";

    /// <summary><c>--border</c>: the card's edge.</summary>
    public const string Border = "#E2E6EB";

    /// <summary><c>--ink</c>: the wordmark.</summary>
    public const string Ink = "#1B2430";

    /// <summary><c>--ink-2</c>: body text.</summary>
    public const string Body = "#333F4F";

    /// <summary><c>--ink-3</c>: the footer and other secondary lines.</summary>
    public const string Muted = "#5A6675";

    /// <summary><c>--primary</c>: the brand mark beside the wordmark.</summary>
    public const string Brand = "#00B7C3";

    /// <summary><c>--primary-strong</c>: the primary button's fill.</summary>
    public const string ButtonFill = "#008089";

    /// <summary><c>--on-primary-strong</c>: text on the button.</summary>
    public const string ButtonText = "#FFFFFF";

    /// <summary><c>--primary-ink</c>: links in text.</summary>
    public const string Link = "#00646B";

    /// <summary><c>--font-sans</c>, with single quotes so it fits inside a style attribute.</summary>
    public const string Font =
        "'Segoe UI', 'Segoe WP', Segoe, Selawik, system-ui, -apple-system, Tahoma, Helvetica, Arial, sans-serif";

    /// <summary><c>--font-mono</c>, with single quotes so it fits inside a style attribute. For one-time codes.</summary>
    public const string Mono = "ui-monospace, 'Cascadia Code', 'JetBrains Mono', Consolas, 'SF Mono', monospace";
}
