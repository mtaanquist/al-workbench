namespace ALDevToolbox.Services.Account;

/// <summary>
/// Codes for the form errors the account pages show after their endpoint
/// refuses a post: the signed-out ones (signup, signup details, accept-invite,
/// reset-password) and the signed-in Account page and authenticator setup.
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

    // Codes the signed-in endpoints redirect with directly, for sentences they
    // write themselves rather than catch from a service.
    public const string TotpAlreadySetUp = "totp-already-set-up";
    public const string TotpFirst = "totp-first";
    public const string EmailNotConfigured = "email-not-configured";
    public const string EmailCodeThrottled = "email-code-throttled";
    public const string EmailCodeNotSent = "email-code-not-sent";
    public const string EmailCodeWrong = "email-code-wrong";
    public const string PasswordWrong = "password-wrong";
    public const string PasswordLocked = "password-locked";
    public const string ConfirmationExpired = "confirmation-expired";
    public const string TokenExpiryChoice = "token-expiry-choice";
    public const string EntraNotSetUp = "entra-not-set-up";
    public const string GitHubNotSetUp = "github-not-set-up";
    public const string GitHubLinkStale = "github-link-stale";
    public const string GitHubCancelled = "github-cancelled";
    public const string GitHubTaken = "github-taken";
    public const string GitHubRefused = "github-refused";
    public const string GitHubUnlinkFailed = "github-unlink-failed";

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

        // Signed-in: the Account page and authenticator setup.
        ("totp-already-on", "The authenticator app is already turned on. Turn it off first if you want to set it up again."),
        ("totp-already-confirmed", "The authenticator app is already turned on. If you didn't see your recovery codes, generate new ones below."),
        ("totp-not-started", "Start setting up the authenticator app first."),
        ("totp-code-wrong", "That code didn't match. Check your authenticator and try again."),
        ("totp-restarted", "Setup was started again in another window. Scan the new QR code and enter a code from it."),
        (TotpAlreadySetUp, "Your authenticator app is already set up. Disable it before setting up a new one."),
        (TotpFirst, "Turn on the authenticator app first."),
        (EmailNotConfigured, "Email isn't set up on this site yet. Ask a site administrator."),
        (EmailCodeThrottled, "Several codes were sent recently. Wait a few minutes before asking for another one."),
        (EmailCodeNotSent, "We couldn't send the email code. Try again in a few minutes."),
        (EmailCodeWrong, "That code didn't match. Request a new one."),
        (PasswordWrong, StepUpService.WrongPasswordMessage),
        ("current-password-wrong", "Current password is incorrect."),
        (PasswordLocked, AuthService.CurrentPasswordLockedOutMessage),
        ("password-microsoft-only", "Your organisation signs in with Microsoft, so there is no password to change here. Use \"Sign in with Microsoft\" instead."),
        ("totp-step-up-first", StepUpService.TotpStepUpFirstMessage),
        ("totp-sign-in-again", StepUpService.TotpSignInAgainMessage),
        (ConfirmationExpired, "Your confirmation has expired. Choose \"Confirm with Microsoft\" again, or enter your password."),
        ("passkey-name-length", "Name must be 1-80 characters."),
        ("passkey-not-found", "Passkey not found."),
        ("token-name-missing", "Give the token a recognisable name."),
        ("token-name-long", "Name is too long (80 characters max)."),
        ("token-expiry-past", "Expiry must be in the future."),
        (TokenExpiryChoice, "Choose one of the expiry options offered."),
        (EntraNotSetUp, "Microsoft sign-in isn't set up for your organisation yet. An admin can turn it on under Administration."),
        ("entra-tenant-not-allowed", "That Microsoft account's organisation isn't on your organisation's allowed list. Ask your admin to add the tenant on the Identity page."),
        ("entra-taken", EntraSignInService.IdentityTakenMessage),
        ("entra-last-link", "Your organisation signs in with Microsoft only, so disconnecting your last Microsoft account would lock you out."),
        (GitHubNotSetUp, "GitHub is not set up on this server yet. Ask whoever runs AL Workbench to finish setting it up, then try again."),
        (GitHubLinkStale, "Connecting your GitHub account did not finish in time, or had already been done. Choose Connect GitHub to start again."),
        (GitHubCancelled, "GitHub was not connected. You can choose Connect GitHub whenever you want to try again."),
        (GitHubTaken, "That GitHub account is already connected to someone else. Sign in to GitHub as yourself, then try again."),
        (GitHubRefused, "GitHub could not finish connecting your account. Try again in a few minutes."),
        (GitHubUnlinkFailed, "An unexpected error occurred. Your GitHub account is still connected."),
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
