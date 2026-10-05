using System.Text;
using System.Text.RegularExpressions;
using MimeKit.Text;

namespace ALDevToolbox.Services.Email;

/// <summary>
/// Builds the plain-text part of an email from its rendered HTML. Not a general
/// HTML-to-text converter: it reads the markup the components in
/// <c>Components/Email/</c> produce, and two markers they set decide what
/// HTML-only furniture becomes in text (see <c>.design/email.md</c>):
/// <list type="bullet">
/// <item><c>data-email-text="skip"</c> leaves an element out (the hidden
/// preview line, the "if the button does not work" fallback).</item>
/// <item><c>data-email-text="button"</c> on a link writes its label and its
/// address on two lines, which is how a button reads in text.</item>
/// </list>
/// Any other link with an address that differs from its text keeps the
/// address in brackets after it, so nothing a reader can click in the HTML is
/// lost in the text. Tokenising is MimeKit's <see cref="HtmlTokenizer"/>, which
/// also decodes entities.
/// </summary>
public static partial class EmailPlainText
{
    private const string MarkerAttribute = "data-email-text";

    public static string FromHtml(string html)
    {
        var text = new StringBuilder();
        var skipped = new Stack<HtmlTagId>();
        string? linkHref = null;
        var linkIsButton = false;
        var linkStart = 0;

        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader);
        while (tokenizer.ReadNextToken(out var token))
        {
            if (token is HtmlTagToken tag)
            {
                var isVoid = tag.Id.IsEmptyElement() || tag.IsEmptyElement;
                if (skipped.Count > 0)
                {
                    // Inside an element left out: only track nesting so the
                    // matching end tag is recognised.
                    if (isVoid || tag.Id != skipped.Peek()) continue;
                    if (tag.IsEndTag) skipped.Pop();
                    else skipped.Push(tag.Id);
                    continue;
                }

                if (!tag.IsEndTag && !isVoid && IsSkipped(tag))
                {
                    skipped.Push(tag.Id);
                    continue;
                }

                switch (tag.Id)
                {
                    case HtmlTagId.Br:
                        text.Append('\n');
                        break;
                    case HtmlTagId.LI when !tag.IsEndTag:
                        text.Append("\n- ");
                        break;
                    case HtmlTagId.A when !tag.IsEndTag:
                        linkHref = Attribute(tag, "href");
                        linkIsButton = Attribute(tag, MarkerAttribute) == "button";
                        linkStart = text.Length;
                        break;
                    case HtmlTagId.A:
                        CloseLink(text, linkHref, linkIsButton, linkStart);
                        linkHref = null;
                        break;
                    case HtmlTagId.TD or HtmlTagId.TH when tag.IsEndTag:
                        // Cells in one row stay on one line, apart.
                        text.Append(' ');
                        break;
                    default:
                        if (IsParagraph(tag.Id)) text.Append("\n\n");
                        else if (IsLine(tag.Id)) EndLine(text);
                        break;
                }
            }
            else if (token is HtmlDataToken data && skipped.Count == 0)
            {
                text.Append(Whitespace().Replace(data.Data, " "));
            }
        }

        return Tidy(text.ToString());
    }

    /// <summary>
    /// Ends the current line unless it is already ended, so a row's start and
    /// end tags make one line break rather than a blank line between rows.
    /// </summary>
    private static void EndLine(StringBuilder text)
    {
        while (text.Length > 0 && text[^1] == ' ') text.Length--;
        if (text.Length > 0 && text[^1] != '\n') text.Append('\n');
    }

    private static void CloseLink(StringBuilder text, string? href, bool isButton, int start)
    {
        if (string.IsNullOrWhiteSpace(href)) return;
        var label = text.ToString(start, text.Length - start).Trim();
        if (isButton)
        {
            text.Length = start;
            text.Append("\n\n").Append(label).Append(":\n").Append(href).Append("\n\n");
        }
        else if (!string.Equals(label, href, StringComparison.Ordinal))
        {
            text.Append(" (").Append(href).Append(')');
        }
    }

    /// <summary>One line per row, trimmed, at most one blank line in a row, none at the ends.</summary>
    private static string Tidy(string raw)
    {
        var result = new StringBuilder();
        var blank = 0;
        foreach (var line in raw.Split('\n').Select(l => l.Trim()))
        {
            if (line.Length == 0)
            {
                blank++;
                continue;
            }
            if (result.Length > 0) result.Append(blank > 0 ? "\n\n" : "\n");
            result.Append(line);
            blank = 0;
        }
        return result.ToString();
    }

    private static bool IsSkipped(HtmlTagToken tag) =>
        tag.Id is HtmlTagId.Head or HtmlTagId.Style or HtmlTagId.Script or HtmlTagId.Title
        || Attribute(tag, MarkerAttribute) == "skip";

    private static bool IsParagraph(HtmlTagId id) =>
        id is HtmlTagId.P or HtmlTagId.H1 or HtmlTagId.H2 or HtmlTagId.H3 or HtmlTagId.H4
            or HtmlTagId.H5 or HtmlTagId.H6 or HtmlTagId.BlockQuote or HtmlTagId.UL or HtmlTagId.OL
            or HtmlTagId.HR or HtmlTagId.Table or HtmlTagId.Pre;

    private static bool IsLine(HtmlTagId id) =>
        id is HtmlTagId.Div or HtmlTagId.TR;

    private static string? Attribute(HtmlTagToken tag, string name) =>
        tag.Attributes.TryGetValue(name, out var attribute) ? attribute.Value : null;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
