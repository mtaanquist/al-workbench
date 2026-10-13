using System.Text;
using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.Translation;

/// <summary>One edited target: the new text and, optionally, a new <c>state</c>.</summary>
public sealed record TargetEdit(string Target, string? State);

/// <summary>
/// Writes edited <c>&lt;target&gt;</c> values back into an XLIFF file's
/// <b>original text</b>, touching only the bytes inside the trans-units the
/// user actually edited. Everything else — indentation, attribute order,
/// self-closing tags, notes, the XML declaration, line endings, BOM — is left
/// exactly as the source tool (AL compiler, Poedit, VS Code extension, …)
/// wrote it. A no-edit run is therefore byte-identical to its input, which is
/// the whole point: git diffs show only the lines that changed.
///
/// We deliberately do <i>not</i> round-trip through <c>XDocument</c> — that
/// normalises tool-specific quirks and would noise up every diff. See
/// <c>.design/translator/</c> and the Translator plan for the rationale.
/// </summary>
public static class XliffTargetWriter
{
    // One trans-unit block. trans-units don't nest, so a non-greedy match
    // between the open tag and its close is safe. Singleline so '.' spans
    // newlines.
    private static readonly Regex TransUnitRegex = new(
        @"<trans-unit\b[^>]*>.*?</trans-unit>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // The id attribute on a trans-unit open tag (single or double quoted).
    private static readonly Regex IdAttrRegex = new(
        "\\bid\\s*=\\s*(?:\"(?<v>[^\"]*)\"|'(?<v>[^']*)')",
        RegexOptions.Compiled);

    // <target ...>inner</target> (inner may be empty / multi-line).
    private static readonly Regex TargetElementRegex = new(
        @"<target\b(?<attrs>[^>]*)>(?<inner>.*?)</target>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Self-closing <target ... /> (an empty target).
    private static readonly Regex TargetSelfClosingRegex = new(
        @"<target\b(?<attrs>[^>]*?)/>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex StateAttrRegex = new(
        "\\bstate\\s*=\\s*(?:\"[^\"]*\"|'[^']*')",
        RegexOptions.Compiled);

    private static readonly Regex SourceCloseRegex = new(
        @"</source>",
        RegexOptions.Compiled);

    // The first <file> open tag, and the target-language attribute on it.
    private static readonly Regex FileOpenTagRegex = new(
        @"<file\b[^>]*>",
        RegexOptions.Compiled);

    private static readonly Regex TargetLanguageAttrRegex = new(
        "\\btarget-language\\s*=\\s*(?:\"[^\"]*\"|'[^']*')",
        RegexOptions.Compiled);

    private static readonly Regex SourceIndentRegex = new(
        @"(?<indent>[^\S\r\n]*)<source\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="originalXml"/> with the edited targets applied.
    /// <paramref name="edits"/> is keyed by trans-unit id. Ids not present in
    /// the document are ignored; trans-units not present in the map are left
    /// untouched.
    /// </summary>
    /// <summary>
    /// Restates the <c>&lt;file&gt;</c> element's <c>target-language</c>.
    ///
    /// <para>Used by exactly one caller: a translation started from the AL
    /// compiler's generated <c>.g.xlf</c>, which declares its target language to
    /// be its source language because nothing in it is translated yet (issue
    /// #625). The file being written is a new language file, and a
    /// <c>da-DK.xlf</c> that says <c>target-language="en-US"</c> is not a Danish
    /// translation as far as AL is concerned.</para>
    ///
    /// <para>Deliberately not applied to an ordinary save: the byte-fidelity
    /// contract above is what keeps diffs clean, and a file that already names
    /// its language has nothing to correct. Returns the input unchanged when
    /// there is no <c>&lt;file&gt;</c> element to touch.</para>
    /// </summary>
    public static string SetTargetLanguage(string originalXml, string language)
    {
        ArgumentNullException.ThrowIfNull(originalXml);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        var file = FileOpenTagRegex.Match(originalXml);
        if (!file.Success) return originalXml;

        var tag = file.Value;
        var attribute = $"target-language=\"{language}\"";
        var updated = TargetLanguageAttrRegex.IsMatch(tag)
            ? TargetLanguageAttrRegex.Replace(tag, attribute, 1)
            // No attribute at all: put one just before the tag closes, keeping
            // whatever form that close takes.
            : tag.Insert(tag.Length - (tag.EndsWith("/>", StringComparison.Ordinal) ? 2 : 1), $" {attribute}");

        return string.Concat(
            originalXml.AsSpan(0, file.Index),
            updated,
            originalXml.AsSpan(file.Index + file.Length));
    }

    /// <summary>
    /// Names the language a translation started from the generated <c>.g.xlf</c>
    /// is in, when the file still says it is in its source language (issue
    /// #1250). The Translator keeps the generated text as its base and only
    /// remembers the language it was asked to translate into, so a copy
    /// exported from it would otherwise declare <c>target-language="en-US"</c>
    /// however much Danish it held.
    ///
    /// <para>Only a file whose target language is its source language (or is
    /// missing) is touched, which is the generated file's signature: an
    /// ordinary language file already names its language, and rewriting it
    /// would break the byte-fidelity contract above. Returns the input
    /// unchanged when <paramref name="language"/> is blank or is the source
    /// language itself.</para>
    /// </summary>
    public static string NameTargetLanguageOfUntranslatedFile(string originalXml, string? language)
    {
        ArgumentNullException.ThrowIfNull(originalXml);
        if (string.IsNullOrWhiteSpace(language)) return originalXml;
        language = language.Trim();

        var file = FileOpenTagRegex.Match(originalXml);
        if (!file.Success) return originalXml;

        var source = AttributeValue(file.Value, SourceLanguageAttrRegex);
        if (source is null || string.Equals(source, language, StringComparison.OrdinalIgnoreCase)) return originalXml;
        var target = AttributeValue(file.Value, TargetLanguageAttrRegex);
        if (target is not null && !string.Equals(target, source, StringComparison.OrdinalIgnoreCase)) return originalXml;

        return SetTargetLanguage(originalXml, language);
    }

    private static readonly Regex SourceLanguageAttrRegex = new(
        "\\bsource-language\\s*=\\s*(?:\"[^\"]*\"|'[^']*')",
        RegexOptions.Compiled);

    private static string? AttributeValue(string tag, Regex attribute)
    {
        var match = attribute.Match(tag);
        if (!match.Success) return null;
        var raw = match.Value[(match.Value.IndexOf('=') + 1)..].Trim();
        return raw.Length >= 2 ? raw[1..^1].Trim() : null;
    }

    public static string ApplyEdits(string originalXml, IReadOnlyDictionary<string, TargetEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(originalXml);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0) return originalXml;

        return TransUnitRegex.Replace(originalXml, match =>
        {
            var block = match.Value;
            var idMatch = IdAttrRegex.Match(block);
            if (!idMatch.Success) return block;
            var id = DecodeXml(idMatch.Groups["v"].Value);
            if (!edits.TryGetValue(id, out var edit)) return block;
            return ApplyToBlock(block, edit);
        });
    }

    private static string ApplyToBlock(string block, TargetEdit edit)
    {
        var escaped = EncodeXmlText(edit.Target);

        // 1) Existing <target>…</target>.
        var te = TargetElementRegex.Match(block);
        if (te.Success)
        {
            var attrs = SetState(te.Groups["attrs"].Value, edit.State);
            var replacement = $"<target{attrs}>{escaped}</target>";
            return block.Substring(0, te.Index) + replacement + block.Substring(te.Index + te.Length);
        }

        // 2) Self-closing <target/>.
        var ts = TargetSelfClosingRegex.Match(block);
        if (ts.Success)
        {
            var attrs = SetState(ts.Groups["attrs"].Value.TrimEnd(), edit.State);
            var replacement = $"<target{attrs}>{escaped}</target>";
            return block.Substring(0, ts.Index) + replacement + block.Substring(ts.Index + ts.Length);
        }

        // 3) No target at all — insert one right after </source>, matching the
        // source line's indentation so the inserted line reads naturally.
        var src = SourceCloseRegex.Match(block);
        if (src.Success)
        {
            var indent = SourceIndentRegex.Match(block) is { Success: true } im
                ? im.Groups["indent"].Value
                : "          ";
            var stateAttr = string.IsNullOrEmpty(edit.State) ? string.Empty : $" state=\"{EncodeXmlAttribute(edit.State)}\"";
            var insertion = $"\n{indent}<target{stateAttr}>{escaped}</target>";
            var at = src.Index + src.Length;
            return block.Substring(0, at) + insertion + block.Substring(at);
        }

        // No <source> either — leave the block alone rather than guess.
        return block;
    }

    /// <summary>
    /// Sets / replaces / removes the <c>state</c> attribute inside a target
    /// open-tag's attribute string. <paramref name="state"/> null leaves the
    /// existing attributes untouched.
    /// </summary>
    private static string SetState(string attrs, string? state)
    {
        if (state is null) return attrs;

        // The state value is fully client-controlled (the editor's JSON payload),
        // so it must be XML-attribute-escaped — otherwise a value like
        // `translated" foo="bar` breaks out of the attribute and injects into the
        // exported XLIFF (which is then re-parsed into the translation memory).
        // Regex.Replace also treats '$' specially, so escape via a match
        // evaluator rather than a replacement string. See issue #373.
        var encoded = EncodeXmlAttribute(state);

        if (StateAttrRegex.IsMatch(attrs))
        {
            return StateAttrRegex.Replace(attrs, _ => $"state=\"{encoded}\"", 1);
        }
        // Append, keeping a single leading space before the new attribute.
        var trimmed = attrs.TrimEnd();
        return $"{trimmed} state=\"{encoded}\"";
    }

    /// <summary>
    /// XML attribute-value escaping for the (client-controlled) <c>state</c>
    /// value. Escapes the quote that delimits the attribute plus the markup
    /// metacharacters, so a hostile value can't break out of the attribute. See
    /// issue #373.
    /// </summary>
    private static string EncodeXmlAttribute(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length + 16);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Minimal XML text escaping: only the characters that must be escaped in element content.</summary>
    private static string EncodeXmlText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length + 16);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Decodes the handful of entities that can appear in an id attribute so map keys line up.</summary>
    private static string DecodeXml(string value) => value
        .Replace("&lt;", "<")
        .Replace("&gt;", ">")
        .Replace("&quot;", "\"")
        .Replace("&apos;", "'")
        .Replace("&amp;", "&");
}
