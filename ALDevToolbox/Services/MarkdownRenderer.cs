using Markdig;
using Markdig.Extensions.GenericAttributes;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace ALDevToolbox.Services;

/// <summary>
/// Singleton wrapper around <see cref="Markdig"/> for the few places in the UI
/// that render user- and agent-authored Markdown (recipe instructions and the
/// MCP-submitted recipe suggestions an admin reviews). The pipeline is
/// configured once on construction so the parser tables aren't rebuilt per
/// request.
///
/// <para>
/// Three sanitisation layers, all required: <c>DisableHtml()</c> drops inline /
/// block HTML so a pasted <c>&lt;script&gt;</c> can't reach the page;
/// <see cref="SanitizeLinks"/> rewrites every link / image destination and
/// every <c>&lt;scheme:...&gt;</c> autolink because <c>DisableHtml()</c> does
/// <b>not</b> touch URI schemes — without it <c>[x](javascript:...)</c> and
/// <c>&lt;javascript:...&gt;</c> render live, script-executing anchors; and the
/// generic-attributes extension that <c>UseAdvancedExtensions()</c> brings in is
/// removed, because its <c>{key=value}</c> syntax writes arbitrary attributes
/// (event handlers, a second <c>href</c>) straight onto the element. Only
/// http(s), mailto, tel, and relative / fragment URLs survive.
/// </para>
/// </summary>
public sealed class MarkdownRenderer
{
    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto", "tel" };

    private readonly MarkdownPipeline _pipeline;

    public MarkdownRenderer()
    {
        // UseAdvancedExtensions enables auto-links, fenced code, tables, task
        // lists, footnotes — the common GFM-ish set authors expect.
        // GenericAttributes is dropped: "{onclick=...}" after a link or heading
        // would otherwise land on the element verbatim, past every other check.
        var builder = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml();
        builder.Extensions.TryRemove<GenericAttributesExtension>();
        _pipeline = builder.Build();
    }

    /// <summary>
    /// Renders the supplied Markdown to a Blazor <see cref="MarkupString"/>.
    /// Returns an empty <see cref="MarkupString"/> for null / whitespace input
    /// so callers can chain without a null check.
    /// </summary>
    public MarkupString Render(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new MarkupString(string.Empty);
        }
        var document = Markdown.Parse(markdown, _pipeline);
        SanitizeLinks(document);
        using var writer = new StringWriter();
        var renderer = new Markdig.Renderers.HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return new MarkupString(writer.ToString());
    }

    /// <summary>
    /// Neutralises every <see cref="LinkInline"/> (covers both <c>&lt;a&gt;</c>
    /// and <c>&lt;img&gt;</c>) whose destination carries a disallowed URI
    /// scheme, replacing it with <c>about:blank</c>. A CommonMark autolink
    /// (<c>&lt;scheme:...&gt;</c>) is a separate <see cref="AutolinkInline"/>
    /// node the link check never sees, so an unsafe one is replaced by its
    /// text as a plain literal. Email autolinks always render as
    /// <c>mailto:</c> and are left alone. Relative URLs, fragments, and the
    /// allow-listed schemes pass through unchanged.
    /// </summary>
    private static void SanitizeLinks(MarkdownObject node)
    {
        var unsafeAutolinks = new List<AutolinkInline>();
        foreach (var descendant in node.Descendants())
        {
            switch (descendant)
            {
                case LinkInline link when !IsSafeUrl(link.Url):
                    link.Url = "about:blank";
                    break;
                case AutolinkInline autolink when !autolink.IsEmail && !IsSafeUrl(autolink.Url):
                    unsafeAutolinks.Add(autolink);
                    break;
            }
        }
        // Replaced after the walk: swapping nodes mid-enumeration would
        // invalidate the descendant iterator.
        foreach (var autolink in unsafeAutolinks)
        {
            autolink.ReplaceBy(new LiteralInline(autolink.Url));
        }
    }

    /// <summary>
    /// True when <paramref name="url"/> is safe to emit as a link / image
    /// destination. A URL without an explicit scheme (relative path, anchor,
    /// protocol-relative) is allowed; one with a scheme is allowed only when
    /// the scheme is in <see cref="AllowedSchemes"/>. Browsers ignore embedded
    /// whitespace / control characters and treat schemes case-insensitively,
    /// so those are stripped before the check to defeat
    /// <c>java&#92;tscript:</c>-style evasions.
    /// </summary>
    internal static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        // Strip characters a browser would ignore when parsing the scheme.
        Span<char> buffer = stackalloc char[url.Length];
        var length = 0;
        foreach (var c in url)
        {
            if (c > ' ')
            {
                buffer[length++] = c;
            }
        }
        var cleaned = buffer[..length];

        var colon = cleaned.IndexOf(':');
        if (colon < 0)
        {
            // No scheme — relative, fragment (#…), or protocol-relative (//…).
            return true;
        }

        // A '/', '?', or '#' before the first ':' means the colon belongs to a
        // path segment (e.g. "foo/bar:baz"), not a scheme — treat as relative.
        var slash = cleaned.IndexOfAny('/', '?');
        var hash = cleaned.IndexOf('#');
        if ((slash >= 0 && slash < colon) || (hash >= 0 && hash < colon))
        {
            return true;
        }

        var scheme = cleaned[..colon].ToString();
        return AllowedSchemes.Contains(scheme);
    }
}
