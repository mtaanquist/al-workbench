using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.Translation;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Translator;

/// <summary>
/// #1325: a file opened from disk that holds no translation yet is the
/// compiler's generated file. The Translator never saves into it - the next
/// build would wipe the translation - and exports a language file named the
/// way the repository path names one instead.
/// </summary>
public sealed class TranslationFileRulesTests
{
    [Theory]
    [InlineData("en-US", "en-US", "", true)]
    [InlineData("en-US", "EN-us", "", true)]
    [InlineData("en-US", "", "", true)]
    [InlineData("en-US", "da-DK", "", false)]
    [InlineData("en-US", "en-GB", "", false)]
    // A curated en-US file has real targets: somebody's work, not the build's.
    [InlineData("en-US", "en-US", "Posting date", false)]
    // So does a file that leaves out target-language but carries translations.
    [InlineData("en-US", "", "Bogfoeringsdato", false)]
    public void Only_a_file_with_no_targets_and_no_other_language_is_the_generated_one(
        string source, string target, string unitTarget, bool expected) =>
        TranslationFileRules.IsUntranslatedSource(new XliffDocument(
                target, source, "App",
                [new XliffTransUnit("u1", "Posting Date", unitTarget, null, null, null)]))
            .Should().Be(expected);

    [Theory]
    [InlineData("App.g.xlf", "da-DK", "App.da-DK.xlf")]
    [InlineData("Payment Import.g.xlf", "de-DE", "Payment Import.de-DE.xlf")]
    // Changing the language again replaces the tag rather than stacking a second one.
    [InlineData("App.da-DK.xlf", "nb-NO", "App.nb-NO.xlf")]
    // A copy saved under some other name still gets the language in its name.
    [InlineData("App.xlf", "da-DK", "App.da-DK.xlf")]
    [InlineData("App.G.XLF", "da-DK", "App.da-DK.xlf")]
    // A dot in the app's own name is not a language tag, whatever its case.
    [InlineData("Cronus.App.g.xlf", "da-DK", "Cronus.App.da-DK.xlf")]
    [InlineData("Cronus.App.xlf", "da-DK", "Cronus.App.da-DK.xlf")]
    [InlineData("my.app.xlf", "da-DK", "my.app.da-DK.xlf")]
    [InlineData("Cronus.ext.xlf", "da-DK", "Cronus.ext.da-DK.xlf")]
    public void The_export_is_named_as_the_language_file_beside_the_generated_one(
        string fileName, string language, string expected) =>
        TranslationFileRules.LanguageFileName(fileName, language).Should().Be(expected);
}
