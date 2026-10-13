using ALDevToolbox.Services;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Cookbook;

/// <summary>
/// Sanity coverage for <see cref="MarkdownRenderer"/>: empty input yields
/// empty output, headings / fenced code render, and raw HTML in the source
/// is stripped at the parser level so a pasted script tag can't escape into
/// a published recipe's instructions section.
/// </summary>
public sealed class MarkdownRendererTests
{
    private readonly MarkdownRenderer _sut = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Render_returns_empty_for_blank_input(string? input)
    {
        _sut.Render(input).Value.Should().BeEmpty();
    }

    [Fact]
    public void Render_emits_heading_for_markdown_h2()
    {
        var html = _sut.Render("## Setup").Value;
        html.Should().Contain("<h2");
        html.Should().Contain("Setup");
    }

    [Fact]
    public void Render_strips_raw_script_tag_from_source()
    {
        var html = _sut.Render("Hello <script>alert(1)</script> world").Value;
        html.Should().NotContain("<script");
        // The literal text from the source is preserved (with the < / > escaped),
        // so authors can still see the offending payload — they just can't
        // execute it as HTML.
        html.Should().Contain("Hello");
        html.Should().Contain("world");
    }

    [Fact]
    public void Render_supports_fenced_code()
    {
        var html = _sut.Render("```al\nfield(1; \"Foo\"; Text[50]) { }\n```").Value;
        html.Should().Contain("<pre");
        html.Should().Contain("<code");
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JavaScript:alert(1))")]
    [InlineData("[click](  javascript:alert(1))")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[click](vbscript:msgbox(1))")]
    public void Render_neutralises_dangerous_link_schemes(string markdown)
    {
        var html = _sut.Render(markdown).Value;
        html.Should().NotContainEquivalentOf("javascript:");
        html.Should().NotContainEquivalentOf("vbscript:");
        html.Should().NotContain("data:text/html");
        html.Should().Contain("about:blank");
    }

    [Theory]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("<JAVASCRIPT:alert(1)>")]
    [InlineData("<data:text/html,x>")]
    [InlineData("<vbscript:msgbox(1)>")]
    [InlineData("see <javascript:alert(1)> here")]
    public void Render_does_not_link_autolinks_with_dangerous_schemes(string markdown)
    {
        var html = _sut.Render(markdown).Value;
        html.Should().NotContain("<a");
        html.Should().NotContainEquivalentOf("href=");
    }

    [Theory]
    [InlineData("<https://example.com/page>", "href=\"https://example.com/page\"")]
    [InlineData("<dev@example.com>", "href=\"mailto:dev@example.com\"")]
    [InlineData("<mailto:dev@example.com>", "href=\"mailto:dev@example.com\"")]
    public void Render_keeps_safe_autolinks(string markdown, string expectedHref)
    {
        _sut.Render(markdown).Value.Should().Contain(expectedHref);
    }

    [Theory]
    [InlineData("[x](https://example.com){onclick=alert(1)}")]
    [InlineData("## Setup {onmouseover=alert(1)}")]
    [InlineData("[x](https://example.com){href=javascript:alert(1)}")]
    public void Render_does_not_turn_attribute_blocks_into_element_attributes(string markdown)
    {
        var html = _sut.Render(markdown).Value;
        html.Should().NotContain("onclick=\"");
        html.Should().NotContain("onmouseover=\"");
        html.Should().NotContainEquivalentOf("href=\"javascript:");
    }

    [Fact]
    public void IsSafeUrl_strips_control_chars_before_scheme_check()
    {
        // Belt-and-suspenders: even if a destination reaches the renderer with
        // embedded control characters a browser would ignore (e.g. a tab inside
        // "java<TAB>script:"), the scheme check still classifies it as unsafe.
        MarkdownRenderer.IsSafeUrl("java\tscript:alert(1)").Should().BeFalse();
        MarkdownRenderer.IsSafeUrl("  JAVASCRIPT:alert(1)").Should().BeFalse();
        MarkdownRenderer.IsSafeUrl("https://example.com").Should().BeTrue();
        MarkdownRenderer.IsSafeUrl("/relative").Should().BeTrue();
        MarkdownRenderer.IsSafeUrl("#anchor").Should().BeTrue();
    }

    [Fact]
    public void Render_neutralises_dangerous_image_source()
    {
        var html = _sut.Render("![x](javascript:alert(1))").Value;
        html.Should().NotContainEquivalentOf("javascript:");
        html.Should().Contain("about:blank");
    }

    [Theory]
    [InlineData("[ok](https://example.com/page)", "https://example.com/page")]
    [InlineData("[ok](http://example.com)", "http://example.com")]
    [InlineData("[ok](mailto:dev@example.com)", "mailto:dev@example.com")]
    [InlineData("[ok](/relative/path)", "/relative/path")]
    [InlineData("[ok](#anchor)", "#anchor")]
    [InlineData("[ok](page/with:colon)", "page/with:colon")]
    public void Render_preserves_safe_link_destinations(string markdown, string expectedHref)
    {
        var html = _sut.Render(markdown).Value;
        html.Should().Contain($"href=\"{expectedHref}\"");
        html.Should().NotContain("about:blank");
    }
}
