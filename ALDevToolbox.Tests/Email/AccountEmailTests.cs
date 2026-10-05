using ALDevToolbox.Components.Email;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Email;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Email;

/// <summary>
/// The account emails on the shared layout (issue #1029): each one encodes what
/// a person typed, and carries its link or code in both the HTML and the text.
/// </summary>
public sealed class AccountEmailTests : IDisposable
{
    private const string Hostile = "<script>x</script>";
    private const string Link = "https://workbench.cronus.example/path?token=abc&step=2";

    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    private EmailRenderer Renderer => new(_services, NullLoggerFactory.Instance);

    public static TheoryData<string> LinkEmails =>
    [
        nameof(PasswordResetEmail), nameof(MagicLinkEmail), nameof(EmailChangeConfirmEmail),
        nameof(SignupVerificationEmail), nameof(InviteEmail), nameof(SignupApprovedEmail), nameof(SignupPendingEmail),
    ];

    private Task<EmailContent> RenderAsync(string email) => email switch
    {
        nameof(PasswordResetEmail) => PasswordResetEmail.RenderAsync(Renderer, Hostile, Link),
        nameof(MagicLinkEmail) => MagicLinkEmail.RenderAsync(Renderer, Hostile, Link),
        nameof(EmailChangeConfirmEmail) => EmailChangeConfirmEmail.RenderAsync(Renderer, Hostile, Link),
        nameof(SignupVerificationEmail) => SignupVerificationEmail.RenderAsync(Renderer, Link, "123456"),
        nameof(InviteEmail) => InviteEmail.RenderAsync(Renderer, Hostile, "CRONUS A/S", "Editor", Hostile, Link),
        nameof(SignupApprovedEmail) => SignupApprovedEmail.RenderAsync(Renderer, Hostile, "CRONUS A/S", Link),
        nameof(SignupPendingEmail) => SignupPendingEmail.RenderAsync(Renderer, Hostile, "new@cronus.example", "CRONUS A/S", Link),
        _ => throw new ArgumentOutOfRangeException(nameof(email)),
    };

    [Theory]
    [MemberData(nameof(LinkEmails))]
    public async Task The_link_is_in_both_parts_and_typed_values_are_encoded(string email)
    {
        var content = await RenderAsync(email);

        content.HtmlBody.Should().Contain("token=abc&amp;step=2");
        content.TextBody.Should().Contain(Link);
        content.HtmlBody.Should().NotContain(Hostile);
        // The verification email has no typed value in it; every other one does,
        // so its absence must mean it was encoded, not dropped.
        if (email != nameof(SignupVerificationEmail))
            content.HtmlBody.Should().Contain("&lt;script&gt;");
        content.Subject.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_sign_in_code_is_in_both_parts()
    {
        var content = await MfaCodeEmail.RenderAsync(Renderer, "Mads", "482913");

        content.HtmlBody.Should().Contain("482913");
        content.TextBody.Should().Contain("Your sign-in code is:\n\n482913");
        // Once, in the body: the preview line inbox lists show must not carry it.
        content.HtmlBody.Split("482913").Should().HaveCount(2);
    }

    [Fact]
    public async Task The_signup_code_follows_the_button_in_the_text()
    {
        var content = await SignupVerificationEmail.RenderAsync(Renderer, Link, "123456");

        content.TextBody.Should().Contain($"Confirm email address:\n{Link}");
        content.TextBody.Should().Contain("Or enter this code on the signup page:\n\n123456");
    }

    [Fact]
    public async Task The_invite_shows_the_welcome_note_only_when_there_is_one()
    {
        var with = await InviteEmail.RenderAsync(Renderer, "Mads", "CRONUS A/S", "Editor", "Welcome aboard", Link);
        var without = await InviteEmail.RenderAsync(Renderer, "Mads", "CRONUS A/S", "Editor", "  ", Link);

        with.TextBody.Should().Contain("Welcome aboard");
        without.HtmlBody.Should().NotContain("<blockquote");
    }

    [Fact]
    public async Task A_declined_signup_has_no_sign_in_link()
    {
        var content = await SignupDeclinedEmail.RenderAsync(Renderer, "Mads", "CRONUS A/S");

        content.Subject.Should().Be("Signup declined: CRONUS A/S");
        content.HtmlBody.Should().NotContain("<a ");
    }

    [Fact]
    public async Task An_organisation_name_with_a_line_break_does_not_break_the_subject()
    {
        var content = await SignupApprovedEmail.RenderAsync(Renderer, "Mads", "CRONUS\r\nA/S", Link);

        content.Subject.Should().NotContain("\n");
    }
}
