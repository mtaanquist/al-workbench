using AwesomeAssertions;
using ALDevToolbox.Tests.Infrastructure;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Keeps debug output out of <c>wwwroot/</c>. Everything in that folder is
/// served to anyone who can reach the app, and #1015 merged three 36,000-line
/// HTML captures of the compare page there because they were written next to
/// the stylesheets they linked and then swept up by the commit. Captures and
/// other scratch files belong outside the repository.
///
/// Blazor renders every page, so the app ships no static HTML at all; any
/// <c>.html</c> file here is a stray. The size cap catches the same mistake
/// under another extension: the largest real asset is a few hundred KB.
/// </summary>
public sealed class StrayWwwrootFileTests
{
    private const long MaxAssetBytes = 1024 * 1024;

    [Fact]
    public void Wwwroot_holds_no_static_html_pages()
    {
        var html = Files()
            .Where(f => f.Extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                     || f.Extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            .Select(Relative)
            .ToList();

        html.Should().BeEmpty(
            "wwwroot is served publicly and the app renders its pages through Blazor, so an HTML file there is " +
            "almost certainly a debug capture. Move it out of the repository. Found: {0}",
            string.Join(", ", html));
    }

    [Fact]
    public void Wwwroot_holds_no_oversized_files()
    {
        var large = Files()
            .Where(f => f.Length > MaxAssetBytes)
            .Select(f => $"{Relative(f)} ({f.Length / 1024} KB)")
            .ToList();

        large.Should().BeEmpty(
            "no asset in wwwroot should exceed {0} KB; a file this size is usually a capture or dump. " +
            "If it is a real asset, raise the cap here with a reason. Found: {1}",
            MaxAssetBytes / 1024, string.Join(", ", large));
    }

    private static string Wwwroot => RepoRoot.Combine("ALDevToolbox", "wwwroot");

    private static IEnumerable<FileInfo> Files() =>
        new DirectoryInfo(Wwwroot).EnumerateFiles("*", SearchOption.AllDirectories);

    private static string Relative(FileInfo f) =>
        Path.GetRelativePath(Wwwroot, f.FullName).Replace('\\', '/');
}
