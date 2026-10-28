using ALDevToolbox.Services.Generation;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Generation;

/// <summary>
/// The renderer rules that the generation tests only reach indirectly. The
/// full substitution table is <c>MustacheVariableCatalog</c>, whose own test
/// runs every name through generation.
/// </summary>
public sealed class MustacheRendererTests
{
    private static readonly MustacheRenderer Renderer = new(NullLogger<MustacheRenderer>.Instance);

    private static MustacheContext Context(string folderPath = "") => new(
        Name: "CRONUS Core",
        WorkspaceName: "CRONUS Customer",
        ShortName: "CRO",
        ModuleName: string.Empty,
        Publisher: "CRONUS",
        ExtensionPrefix: "CRO",
        Affix: "CRO",
        FolderPath: folderPath);

    [Fact]
    public void Namespace_is_the_folder_path_with_dots()
    {
        Renderer.Render("namespace {{namespace}};", Context("src/codeunits/sales"))
            .Should().Be("namespace src.codeunits.sales;");
    }

    [Fact]
    public void Namespace_is_empty_at_the_root()
    {
        Renderer.Render("[{{namespace}}]", Context()).Should().Be("[]");
    }

    [Fact]
    public void An_unknown_variable_is_left_as_written()
    {
        // Blanking it would hide the typo from the person who wrote it.
        Renderer.Render("{{affix}} {{not_a_var}}", Context())
            .Should().Be("CRO {{not_a_var}}");
    }

    [Fact]
    public void A_deprecated_camel_case_name_still_resolves()
    {
        Renderer.Render("{{workspaceName}} / {{shortName}}", Context())
            .Should().Be("CRONUS Customer / CRO");
    }
}
