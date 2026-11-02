namespace ALDevToolbox.Services.Account;

/// <summary>
/// Codes for the form errors the signed-out pages (signup, signup details,
/// accept-invite, reset-password) show after their endpoint refuses a post.
/// The endpoint redirects with <c>reason=&lt;code&gt;</c> instead of the
/// sentence, and the page looks the copy up here, so nobody can send a link
/// that makes the real page show their own text as an error (#1544, the same
/// rule #1503 applied to <c>/login/magic</c> with <see cref="MagicLinkReasons"/>).
/// </summary>
/// <remarks>
/// The table holds every sentence those flows' services can throw. A sentence
/// that isn't in it travels as <see cref="Generic"/>, so a new validation rule
/// shows a vaguer message until it is added here, never the URL's text.
/// </remarks>
public static class FormErrorReasons
{
    /// <summary>The code for a sentence the table doesn't know.</summary>
    public const string Generic = "check";

    /// <summary>What the page shows for <see cref="Generic"/> or an unknown code.</summary>
    public const string GenericMessage = "Something you entered wasn't accepted. Check the form and try again.";

    /// <summary>The invite is spent, revoked or expired.</summary>
    public const string InviteInvalid = "invite-invalid";

    private static readonly (string Code, string Message)[] Known =
    [
        ("email-invalid", "Enter a valid email address."),
        ("email-domain", "Sign-ups from this email domain are not allowed on this site. Contact the site administrator if you believe this is a mistake."),
        ("new-org-blocked", "This site doesn't allow creating new organisations. Ask an administrator to invite you."),
        ("name-length", "Full name must be 2-80 characters."),
        ("org-name-length", "Organisation name must be 2-80 characters."),
        ("slug-invalid", "Slug must use lowercase letters, digits and hyphens only."),
        ("password-short", $"Password must be at least {AuthService.MinPasswordLength} characters."),
        (InviteInvalid, "This invite link is no longer valid. Ask the admin for a new one."),
        ("invite-password", "Set a password to accept this invite."),
        ("invite-microsoft-only", InviteService.MicrosoftOnlyMessage),
        ("account-exists", "You already have an account with this email. Sign in instead."),
    ];

    /// <summary>The code to redirect with for a sentence a service threw.</summary>
    public static string CodeFor(string? message)
    {
        foreach (var (code, known) in Known)
        {
            if (known == message) return code;
        }
        return Generic;
    }

    /// <summary>The sentence a page shows for a code from its query string.</summary>
    public static string MessageFor(string? code)
    {
        foreach (var (known, message) in Known)
        {
            if (known == code) return message;
        }
        return GenericMessage;
    }
}
