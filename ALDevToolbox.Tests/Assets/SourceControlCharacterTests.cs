using AwesomeAssertions;
using ALDevToolbox.Tests.Infrastructure;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Keeps raw control characters out of C# source. A literal NUL byte in a string
/// made ripgrep and grep treat CallSiteReferenceEmitter.cs as binary, so every
/// search skipped a thousand-line file (#1549). Write the escape (<c>"\0"</c>,
/// <c>"\u001F"</c>) instead; tab, newline, carriage return and form feed are fine.
/// </summary>
public sealed class SourceControlCharacterTests
{
    [Fact]
    public void Csharp_sources_contain_no_raw_control_characters()
    {
        var offenders = new List<string>();
        foreach (var project in new[] { "ALDevToolbox", "ALDevToolbox.Tests" })
        {
            var root = RepoRoot.Combine(project);
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal)) continue;
                var bytes = File.ReadAllBytes(file);
                var at = Array.FindIndex(bytes, b => b < 0x20 && b is not ((byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C));
                if (at >= 0) offenders.Add($"{project}/{relative} (byte 0x{bytes[at]:X2} at offset {at})");
            }
        }

        offenders.Should().BeEmpty(
            "a raw control character makes search tools treat the file as binary; write the escape instead. Found: {0}",
            string.Join(", ", offenders));
    }
}
