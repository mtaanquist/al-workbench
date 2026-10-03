using ALDevToolbox.Components.Email;
using ALDevToolbox.Services.Email;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Email;

/// <summary>
/// The shared email layout rendered end to end: HTML through Razor, the
/// plain-text part from that HTML (issue #1028).
/// </summary>
public sealed class EmailRendererTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    private EmailRenderer NewRenderer() => new(_services, NullLoggerFactory.Instance);

    private static RenderFragment Text(string value) => b => b.AddContent(0, value);

    private Task<ALDevToolbox.Services.EmailContent> RenderLayoutAsync(
        string subject = "Subject", string? organizationName = "CRONUS A/S", string href = "https://cronus.example/reset?token=abc&step=2")
    {
        RenderFragment body = b =>
        {
            b.OpenComponent<EmailParagraph>(0);
            b.AddComponentParameter(1, nameof(EmailParagraph.ChildContent), Text("Hi <script>alert(1)</script>,"));
            b.CloseComponent();
            b.OpenComponent<EmailButton>(2);
            b.AddComponentParameter(3, nameof(EmailButton.Href), href);
            b.AddComponentParameter(4, nameof(EmailButton.ChildContent), Text("Reset password"));
            b.CloseComponent();
        };
        return NewRenderer().RenderAsync<EmailLayout>(subject, new Dictionary<string, object?>
        {
            [nameof(EmailLayout.ChildContent)] = body,
            [nameof(EmailLayout.Preheader)] = "Reset link inside",
            [nameof(EmailLayout.OrganizationName)] = organizationName,
            [nameof(EmailLayout.Reason)] = "You received this because you asked to reset your password.",
        });
    }

    [Fact]
    public async Task Values_reach_the_html_encoded()
    {
        var content = await RenderLayoutAsync();

        content.HtmlBody.Should().NotContain("<script>");
        content.HtmlBody.Should().Contain("&lt;script&gt;");
        content.HtmlBody.Should().Contain("token=abc&amp;step=2");
    }

    [Fact]
    public async Task The_html_is_a_whole_document_with_the_wordmark_card_and_footer()
    {
        var content = await RenderLayoutAsync();

        content.HtmlBody.Should().StartWith("<!DOCTYPE html>");
        content.HtmlBody.Should().Contain("AL Workbench");
        content.HtmlBody.Should().Contain("Sent by AL Workbench for CRONUS A/S.");
        // Every style is inline: email clients drop stylesheets and custom properties.
        content.HtmlBody.Should().NotContain("var(--");
        content.HtmlBody.Should().NotContain("class=");
    }

    [Fact]
    public async Task The_plain_text_part_reads_as_the_same_message()
    {
        var content = await RenderLayoutAsync();

        content.TextBody.Should().Be(
            "AL Workbench\n\n"
            + "Hi <script>alert(1)</script>,\n\n"
            + "Reset password:\nhttps://cronus.example/reset?token=abc&step=2\n\n"
            + "Sent by AL Workbench for CRONUS A/S.\n\n"
            + "You received this because you asked to reset your password.");
    }

    [Fact]
    public async Task The_footer_names_no_organisation_when_there_is_none()
    {
        var content = await RenderLayoutAsync(organizationName: null);

        content.TextBody.Should().Contain("Sent by AL Workbench.");
    }

    [Fact]
    public async Task The_subject_has_control_characters_collapsed()
    {
        var content = await RenderLayoutAsync(subject: "  Build failed:\r\nCRONUS\t ");

        content.Subject.Should().Be("Build failed:  CRONUS");
    }

    [Fact]
    public async Task The_site_admin_test_email_renders_through_the_layout()
    {
        var content = await SiteAdminTestEmail.RenderAsync(NewRenderer(), "Mads <admin>");

        content.Subject.Should().Be(SiteAdminTestEmail.Subject);
        content.HtmlBody.Should().Contain("Hi Mads &lt;admin&gt;,");
        content.TextBody.Should().Contain("Hi Mads <admin>,");
        content.TextBody.Should().Contain("the email settings are working");
    }
}
