using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Navigation;
using ALDevToolbox.Services;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Tests.Infrastructure;

namespace ALDevToolbox.Tests.Icons;

/// <summary>
/// Guards the vendored Lucide replacement for the unmaintained Lucide.Blazor
/// package (issue #47). The third test is the important one — it's the build-
/// time check that would have caught PR #46's <c>users-round</c> render-time
/// <c>KeyNotFoundException</c> before it shipped.
/// </summary>
public sealed class IconCatalogTests
{
    private static IconCatalog NewCatalog() => new(NullLogger<IconCatalog>.Instance);

    [Fact]
    public void Known_icon_returns_non_empty_inner_svg()
    {
        var catalog = NewCatalog();

        var inner = catalog.GetInnerSvg("users-round");

        inner.Should().NotBeNull();
        inner!.Should().NotBeNullOrWhiteSpace();
        // Lucide icons are composed of <path>, <circle>, <line>, <rect>, <polyline>
        // and <ellipse> children. If the inner blob doesn't contain at least one
        // of those, the regex extraction in IconCatalog has regressed.
        inner.Should().MatchRegex("<(path|circle|line|rect|polyline|ellipse)\\b");
        inner.Should().NotContain("<svg");
    }

    [Fact]
    public void Unknown_icon_returns_null_instead_of_throwing()
    {
        var catalog = NewCatalog();

        // The whole point of the swap: lucide.dev's current name for users-round
        // didn't exist in the old package and crashed every authenticated render.
        // The replacement must degrade gracefully.
        var act = () => catalog.GetInnerSvg("this-icon-does-not-exist");

        act.Should().NotThrow();
        catalog.GetInnerSvg("this-icon-does-not-exist").Should().BeNull();
    }

    [Fact]
    public void Catalogue_contains_every_icon_name_referenced_by_a_razor_call_site()
    {
        var catalog = NewCatalog();
        var available = new HashSet<string>(catalog.Names, StringComparer.Ordinal);

        var referenced = CollectReferencedIconNames();
        referenced.Should().NotBeEmpty("the test couldn't find any <Icon Name=...> usage — has the component moved?");

        var missing = referenced
            .Where(kv => !available.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        var report = string.Join("; ", missing.Select(kv => $"{kv.Key} (in {string.Join(", ", kv.Value)})"));

        missing.Should().BeEmpty(
            "every icon name used in the app's .razor and .cs sources must have a vendored SVG under Resources/Icons/. " +
            "Missing: {0}", report);
    }

    [Fact]
    public void Catalogue_contains_every_icon_the_sidebar_and_palette_lists_name()
    {
        var available = new HashSet<string>(NewCatalog().Names, StringComparer.Ordinal);

        var named = NavDestinations.All.Select(d => (d.Label, d.Icon))
            .Concat(PaletteCommands.All.Select(c => (c.Label, c.Icon)))
            .ToList();

        named.Should().NotBeEmpty();
        named.Where(n => !available.Contains(n.Icon))
            .Select(n => $"{n.Icon} ({n.Label})")
            .Should().BeEmpty("a typo here renders an invisible placeholder in the sidebar or the palette");
    }

    private static IReadOnlyDictionary<string, List<string>> CollectReferencedIconNames()
    {
        var componentsDir = FindComponentsDir();
        var hits = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // The shapes we cover:
        //   <Icon Name="folder-plus" ...>
        //   any parameter or member ending in Icon: <EmptyState Icon="plug">,
        //   ConfirmIcon="trash-2", confirmIcon: "trash-2", Icon = "rocket"
        var literal = new Regex("(?:<Icon\\s+[^>]*?Name=|\\b\\w*[Ii]con\\s*[=:]\\s*)\"(?<name>[a-z0-9-]+)\"", RegexOptions.Compiled);
        // And the conditional form: Name="@(cond ? "a" : "b")" — both branches.
        var conditional = new Regex("(?:Name|\\w*Icon)=\"@\\([^?]+\\?\\s*\"(?<a>[a-z0-9-]+)\"\\s*:\\s*\"(?<b>[a-z0-9-]+)\"\\s*\\)\"", RegexOptions.Compiled);

        // Icons named positionally in C# (the sidebar and palette lists) are
        // covered by the runtime test below rather than by a pattern.
        var appDir = Path.GetDirectoryName(componentsDir)!;
        var sources = Directory.EnumerateFiles(appDir, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".razor", StringComparison.Ordinal) || p.EndsWith(".cs", StringComparison.Ordinal))
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj" or "Migrations"));

        foreach (var file in sources)
        {
            var rel = Path.GetRelativePath(appDir, file);
            var content = File.ReadAllText(file);
            foreach (Match m in literal.Matches(content)) Record(hits, m.Groups["name"].Value, rel);
            foreach (Match m in conditional.Matches(content))
            {
                Record(hits, m.Groups["a"].Value, rel);
                Record(hits, m.Groups["b"].Value, rel);
            }
        }

        return hits;
    }

    private static void Record(Dictionary<string, List<string>> hits, string name, string file)
    {
        if (!hits.TryGetValue(name, out var files))
        {
            files = new List<string>();
            hits[name] = files;
        }
        if (!files.Contains(file)) files.Add(file);
    }

    private static string FindComponentsDir()
    {
        // Walk up from the test binary until we find the repo root marker, then
        // descend into the app's Components folder. Works for both `dotnet test`
        // (bin/Debug/net10.0) and IDE runners.
        var dir = RepoRoot.Directory;

        dir.Should().NotBeNull("could not locate repo root");
        var components = Path.Combine(dir!.FullName, "ALDevToolbox", "Components");
        Directory.Exists(components).Should().BeTrue("expected Components folder at {0}", components);
        return components;
    }
}
