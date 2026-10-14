namespace ALDevToolbox.Services.Translation;

/// <summary>
/// One XLIFF file found in a repository, described by the rules below: where it
/// sits, which extension folder owns it, the language its name carries, and
/// whether it is the compiler-generated source file.
/// </summary>
/// <param name="Path">Where it lives in the repository, from the root.</param>
/// <param name="FileName">Its own name.</param>
/// <param name="Folder">
/// The folder holding the <c>Translations</c> folder - the extension, in an AL
/// workspace. Empty when <c>Translations</c> sits at the repository root.
/// </param>
/// <param name="Language">The language tag in the file name, when there is one.</param>
/// <param name="IsSource">True for the <c>.g.xlf</c> the AL compiler generates.</param>
public sealed record TranslationFilePlace(
    string Path,
    string FileName,
    string Folder,
    string? Language,
    bool IsSource);

/// <summary>
/// What counts as an AL translation file in a repository, and what its path
/// says about it.
///
/// <para>These rules were the Translator's own (issue #625) until the
/// translation-memory ingest (#631) needed exactly the same ones: "which files
/// in this repository are translations" is one question, and two answers to it
/// would drift. <see cref="ALDevToolbox.Services.GitHub.GitHubTranslationService"/>
/// and <see cref="TranslationMemoryIngestService"/> both ask here.</para>
///
/// <para>See <c>.design/github-integration.md</c> (#625) and
/// <c>.design/github-integration-phase2.md</c> (#631).</para>
/// </summary>
public static class TranslationFileRules
{
    /// <summary>The folder AL keeps translation files in.</summary>
    public const string TranslationsFolder = "Translations";

    /// <summary>The suffix the AL compiler gives the generated source file.</summary>
    public const string SourceFileSuffix = ".g.xlf";

    /// <summary>
    /// True for a file sitting directly inside a folder called
    /// <c>Translations</c>, at any depth. "One level under
    /// <c>Translations/</c>" is the rule from the design doc; the folder's own
    /// depth is not fixed, because an AL workspace keeps one per extension.
    /// </summary>
    public static bool IsTranslationFile(string path)
    {
        var segments = path.Split('/');
        if (segments.Length < 2) return false;
        if (!string.Equals(segments[^2], TranslationsFolder, StringComparison.OrdinalIgnoreCase)) return false;
        var name = segments[^1];
        return name.EndsWith(".xlf", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".xliff", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What a translation file's path says about it. The caller has already
    /// decided it <em>is</em> one (<see cref="IsTranslationFile"/>).
    /// </summary>
    public static TranslationFilePlace Describe(string path)
    {
        var segments = path.Split('/');
        var name = segments[^1];
        var folder = segments.Length >= 3 ? string.Join('/', segments[..^2]) : string.Empty;
        var isSource = name.EndsWith(SourceFileSuffix, StringComparison.OrdinalIgnoreCase);
        return new TranslationFilePlace(path, name, folder, isSource ? null : ReadLanguage(name), isSource);
    }

    /// <summary>
    /// True for a file that holds no translation yet: it names no target
    /// language, or names the source language as its target. That is how the
    /// AL compiler writes the generated <c>.g.xlf</c>, and it is the only test
    /// a file opened from disk gives us - its name is whatever the user's
    /// copy is called.
    ///
    /// <para>Such a file is the build's, not the translator's: the next build
    /// writes it again and anything saved into it is gone (#1325). A
    /// translation started from it belongs in a language file beside it - see
    /// <see cref="LanguageFileName"/>.</para>
    /// </summary>
    public static bool IsUntranslatedSource(string? sourceLanguage, string? targetLanguage) =>
        string.IsNullOrWhiteSpace(targetLanguage)
        || string.Equals(sourceLanguage?.Trim(), targetLanguage.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The name of the language file a translation started from
    /// <paramref name="fileName"/> is saved as: <c>App.g.xlf</c> becomes
    /// <c>App.da-DK.xlf</c>, the same name the repository path gives it. A name
    /// that already carries a language tag has it replaced, so changing the
    /// language twice does not stack two tags.
    /// </summary>
    public static string LanguageFileName(string fileName, string language)
    {
        var dot = fileName.LastIndexOf('.');
        var stem = dot > 0 ? fileName[..dot] : fileName;
        if (stem.EndsWith(".g", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^2];
        }
        // Language tags start lower case (da-DK, fr); "My.App.xlf" has no tag.
        else if (dot > 0 && ReadLanguage(fileName) is { } tag && char.IsAsciiLetterLower(tag[0]))
        {
            stem = stem[..^(tag.Length + 1)];
        }
        return $"{stem}.{language}.xlf";
    }

    /// <summary>
    /// The language tag out of an AL translation file name - the <c>da-DK</c>
    /// in <c>Base Application.da-DK.xlf</c>. Null when the name does not carry
    /// one, which is not a problem: the list falls back to the file name and
    /// the language the file itself declares is read when it is opened.
    /// </summary>
    public static string? ReadLanguage(string fileName)
    {
        var withoutExtension = fileName[..fileName.LastIndexOf('.')];
        var dot = withoutExtension.LastIndexOf('.');
        if (dot < 0) return null;

        var candidate = withoutExtension[(dot + 1)..];
        var parts = candidate.Split('-');
        if (parts.Length is < 1 or > 3) return null;
        if (parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsAsciiLetter)) return null;
        if (parts.Skip(1).Any(p => p.Length is < 2 or > 4 || !p.All(char.IsAsciiLetterOrDigit))) return null;
        return candidate;
    }
}
