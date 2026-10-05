using ALDevToolbox.Services.Email;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Email;

/// <summary>
/// The plain-text part is built from the rendered HTML, so these pin how each
/// piece of email markup reads as text (issue #1028).
/// </summary>
public sealed class EmailPlainTextTests
{
    [Fact]
    public void Paragraphs_become_blocks_separated_by_one_blank_line()
    {
        EmailPlainText.FromHtml("<p>Hi Mads,</p>\n\n   <p>Your build   failed.</p>")
            .Should().Be("Hi Mads,\n\nYour build failed.");
    }

    [Fact]
    public void Entities_are_decoded()
    {
        EmailPlainText.FromHtml("<p>CRONUS &amp; Co &lt;test&gt;</p>").Should().Be("CRONUS & Co <test>");
    }

    [Fact]
    public void A_line_break_starts_a_new_line()
    {
        EmailPlainText.FromHtml("<p>One<br>Two</p>").Should().Be("One\nTwo");
    }

    [Fact]
    public void A_link_keeps_its_address_after_the_text()
    {
        EmailPlainText.FromHtml("<p>See <a href=\"https://cronus.example/b/1\">the build</a> for details.</p>")
            .Should().Be("See the build (https://cronus.example/b/1) for details.");
    }

    [Fact]
    public void A_link_whose_text_is_its_address_is_written_once()
    {
        EmailPlainText.FromHtml("<p><a href=\"https://cronus.example\">https://cronus.example</a></p>")
            .Should().Be("https://cronus.example");
    }

    [Fact]
    public void A_button_link_becomes_its_label_and_address_on_their_own_lines()
    {
        EmailPlainText.FromHtml(
                "<p>Hi,</p><table><tr><td><a data-email-text=\"button\" href=\"https://cronus.example/r?a=1&amp;b=2\">Reset password</a></td></tr></table><p>Thanks</p>")
            .Should().Be("Hi,\n\nReset password:\nhttps://cronus.example/r?a=1&b=2\n\nThanks");
    }

    [Fact]
    public void Skipped_elements_are_left_out_including_nested_elements_of_the_same_kind()
    {
        EmailPlainText.FromHtml(
                "<div data-email-text=\"skip\">Preview <div>nested</div> still hidden</div><p>Shown</p>")
            .Should().Be("Shown");
    }

    [Fact]
    public void The_head_is_left_out()
    {
        EmailPlainText.FromHtml(
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>p { color: red; }</style></head><body><p>Body</p></body></html>")
            .Should().Be("Body");
    }

    [Fact]
    public void List_items_become_dashed_lines()
    {
        EmailPlainText.FromHtml("<ul><li>One</li><li>Two</li></ul>").Should().Be("- One\n- Two");
    }

    [Fact]
    public void Cells_in_one_row_stay_on_one_line_apart()
    {
        EmailPlainText.FromHtml("<table><tr><td>Pipeline</td><td>CRONUS Main</td></tr><tr><td>Result</td><td>Failed</td></tr></table>")
            .Should().Be("Pipeline CRONUS Main\nResult Failed");
    }
}
