using ALDevToolbox.Services.Generation;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Generation;

/// <summary>
/// <c>{{application_version_major}}</c> and <c>{{application_version_minor}}</c>
/// split the resolved application version so a template's <c>app.json</c> can
/// start its own version with the Business Central release it targets.
/// </summary>
public sealed class ApplicationVersionPartsTests
{
    private readonly MustacheRenderer _renderer = new(NullLogger<MustacheRenderer>.Instance);

    [Theory]
    [InlineData("28.2.0.0", "28", "2")]
    [InlineData("27.0.12345.0", "27", "0")]
    [InlineData(" 26.5 ", "26", "5")]
    [InlineData("28", "28", "0")]
    [InlineData("", "", "")]
    public void Major_and_minor_come_from_the_application_version(string version, string major, string minor)
    {
        var rendered = _renderer.Render(
            "{{application_version_major}}|{{application_version_minor}}",
            Context(version));

        rendered.Should().Be($"{major}|{minor}");
    }

    [Fact]
    public void They_compose_into_an_app_version()
    {
        var rendered = _renderer.Render(
            "\"version\": \"{{application_version_major}}.{{application_version_minor}}.0.0\"",
            Context("28.2.0.0"));

        rendered.Should().Be("\"version\": \"28.2.0.0\"");
    }

    private static MustacheContext Context(string applicationVersion) => new(
        Name: "CRONUS Core",
        WorkspaceName: "CRONUS",
        ShortName: "CRONUS",
        ModuleName: "CRONUS Core",
        Publisher: "CRONUS",
        ExtensionPrefix: "CRO",
        Affix: string.Empty,
        FolderPath: string.Empty,
        ApplicationVersion: applicationVersion);
}
