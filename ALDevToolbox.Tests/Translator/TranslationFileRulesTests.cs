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
    [InlineData("en-US", "en-US", true)]
    [InlineData("en-US", "EN-us", true)]
    [InlineData("en-US", null, true)]
    [InlineData("en-US", "", true)]
    [InlineData("en-US", "da-DK", false)]
    [InlineData("en-US", "en-GB", false)]
    public void A_file_whose_target_is_its_source_language_holds_no_translation_yet(
        string source, string? target, bool expected) =>
        TranslationFileRules.IsUntranslatedSource(source, target).Should().Be(expected);

    [Theory]
    [InlineData("App.g.xlf", "da-DK", "App.da-DK.xlf")]
    [InlineData("Payment Import.g.xlf", "de-DE", "Payment Import.de-DE.xlf")]
    // Changing the language again replaces the tag rather than stacking a second one.
    [InlineData("App.da-DK.xlf", "nb-NO", "App.nb-NO.xlf")]
    // A copy saved under some other name still gets the language in its name.
    [InlineData("App.xlf", "da-DK", "App.da-DK.xlf")]
    [InlineData("App.G.XLF", "da-DK", "App.da-DK.xlf")]
    // A dot in the app's own name is not a language tag.
    [InlineData("Cronus.App.g.xlf", "da-DK", "Cronus.App.da-DK.xlf")]
    [InlineData("Cronus.App.xlf", "da-DK", "Cronus.App.da-DK.xlf")]
    public void The_export_is_named_as_the_language_file_beside_the_generated_one(
        string fileName, string language, string expected) =>
        TranslationFileRules.LanguageFileName(fileName, language).Should().Be(expected);
}
