using ALDevToolbox.Services;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Services.Notifications;

namespace ALDevToolbox.Components.Email;

/// <summary>
/// One email as the preview page shows it: a name and a line on when it is
/// sent, both in the words of the site administrator reading the page, and
/// how to render it with sample data.
/// </summary>
/// <param name="Key">Stable slug for the page's query string.</param>
/// <param name="Component">The email component, so a test can check every email has a preview.</param>
public sealed record EmailPreview(
    string Key,
    string Name,
    string WhenSent,
    Type Component,
    Func<EmailRenderer, CancellationToken, Task<EmailContent>> RenderAsync);

/// <summary>
/// Every email the app sends, with sample data, for the preview tab on
/// /site-admin/email (issue #1030). A new email registers here and gets a
/// preview with no page change; <c>EmailPreviewTests</c> fails while an email
/// component has no entry.
///
/// <para>
/// Sample data is CRONUS throughout, and every link points at
/// <see cref="SampleOrigin"/>, a reserved <c>.example</c> name, so a click in
/// a preview (or in a preview sent to yourself) goes nowhere. Never a real
/// token: a working reset link has no business on an admin page.
/// </para>
/// </summary>
public static class EmailPreviews
{
    public const string SampleOrigin = "https://workbench.cronus.example";

    private const string Person = "Alex Hansen";
    private const string Admin = "Sam Berg";
    private const string Organization = "CRONUS A/S";
    private const string SampleToken = "token=preview";

    public static IReadOnlyList<EmailPreview> All { get; } =
    [
        new("password-reset", "Password reset link",
            "When someone asks to reset their password on the sign-in page.",
            typeof(PasswordResetEmail),
            (r, ct) => PasswordResetEmail.RenderAsync(r, Person, $"{SampleOrigin}/reset-password?{SampleToken}", ct)),
        new("sign-in-link", "Sign-in link",
            "When someone asks for a sign-in link instead of typing their password.",
            typeof(MagicLinkEmail),
            (r, ct) => MagicLinkEmail.RenderAsync(r, Person, $"{SampleOrigin}/auth/magic?{SampleToken}", ct)),
        new("two-factor-code", "Two-factor code",
            "When someone signs in with an emailed code as their second step, or turns that step on.",
            typeof(MfaCodeEmail),
            (r, ct) => MfaCodeEmail.RenderAsync(r, Person, "482913", ct)),
        new("signup-verification", "Email address verification",
            "The first step of signing up: confirms the address belongs to the person signing up.",
            typeof(SignupVerificationEmail),
            (r, ct) => SignupVerificationEmail.RenderAsync(r, $"{SampleOrigin}/signup/verify?{SampleToken}", "123456", ct)),
        new("signup-pending", "New signup - notice to admins",
            "To an organisation's administrators when someone asks to join it.",
            typeof(SignupPendingEmail),
            (r, ct) => SignupPendingEmail.RenderAsync(r, Admin, "alex.hansen@cronus.example", Organization,
                $"{SampleOrigin}/admin/administration/users", ct)),
        new("signup-approved", "Signup approved",
            "When an administrator approves someone who asked to join. Listed as a signup decision under Delivery.",
            typeof(SignupApprovedEmail),
            (r, ct) => SignupApprovedEmail.RenderAsync(r, Person, Organization, $"{SampleOrigin}/login", ct)),
        new("signup-declined", "Signup declined",
            "When an administrator declines someone who asked to join. Listed as a signup decision under Delivery.",
            typeof(SignupDeclinedEmail),
            (r, ct) => SignupDeclinedEmail.RenderAsync(r, Person, Organization, ct)),
        new("invite", "Invitation to join",
            "When an administrator invites someone by email. The welcome note is optional.",
            typeof(InviteEmail),
            (r, ct) => InviteEmail.RenderAsync(r, Admin, Organization, "Editor",
                "Welcome aboard. Start with the solutions page.", $"{SampleOrigin}/accept-invite?{SampleToken}", ct)),
        new("email-change", "New email address confirmation",
            "To the new address when an administrator changes someone's email.",
            typeof(EmailChangeConfirmEmail),
            (r, ct) => EmailChangeConfirmEmail.RenderAsync(r, Person, $"{SampleOrigin}/account/confirm-email?{SampleToken}", ct)),
        new("build-failed", "Build failed",
            "When a build pipeline someone started or created fails after working. Sent again only once it works.",
            typeof(BuildNotificationEmail),
            (r, ct) => BuildNotificationEmail.RenderAsync(r, Person, Organization, "CRONUS Coffee", "Main",
                failed: true, nightlyCheck: false, target: null, bcVersion: "26.4",
                failureMessage: "Coffee Extension: 2 errors.\nsrc/Codeunit/CoffeeMgt.Codeunit.al(41,17): error AL0118: The name 'Brew' does not exist in the current context.",
                $"{SampleOrigin}/pipelines/12?build=345", $"{SampleOrigin}{NotificationService.SettingsPath}", ct)),
        new("build-working-again", "Nightly check working again",
            "When the nightly check against an upcoming Business Central version works after failing.",
            typeof(BuildNotificationEmail),
            (r, ct) => BuildNotificationEmail.RenderAsync(r, Person, Organization, "CRONUS Coffee", "Main",
                failed: false, nightlyCheck: true, target: "Next major", bcVersion: "27.0",
                failureMessage: null, $"{SampleOrigin}/pipelines/12?build=351", $"{SampleOrigin}{NotificationService.SettingsPath}", ct)),
        new("test", "Test email",
            "When a site administrator sends a test from the email settings.",
            typeof(SiteAdminTestEmail),
            (r, ct) => SiteAdminTestEmail.RenderAsync(r, Person, ct)),
    ];

    public static EmailPreview? Find(string? key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));
}
