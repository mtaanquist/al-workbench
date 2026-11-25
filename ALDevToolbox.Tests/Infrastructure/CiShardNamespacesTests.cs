using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// CI splits the suite in two by namespace: shard a runs the names in
/// <c>SHARD_A_NAMESPACES</c> and shard b runs everything else. A stale or
/// misspelt name fails nothing there - shard a just runs less and shard b gets
/// it back - so this pins that every listed name still holds tests (#1604).
/// The workflow step checks the same thing before it runs.
/// </summary>
public sealed partial class CiShardNamespacesTests
{
    [Fact]
    public void Every_namespace_in_the_shard_list_holds_tests()
    {
        var workflow = File.ReadAllText(RepoRoot.Combine(".github", "workflows", "build.yml"));
        var match = ShardList().Match(workflow);
        match.Success.Should().BeTrue("build.yml sets SHARD_A_NAMESPACES for the test shards");

        var names = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        names.Should().NotBeEmpty();

        var testNamespaces = typeof(CiShardNamespacesTests).Assembly.GetTypes()
            .Where(t => t.GetMethods().Any(m => m.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length > 0))
            .Select(t => t.Namespace ?? string.Empty)
            .ToHashSet();

        foreach (var name in names)
        {
            var prefix = $"ALDevToolbox.Tests.{name}";
            testNamespaces.Should().Contain(ns => ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal),
                $"SHARD_A_NAMESPACES lists '{name}', so some test must live in {prefix}");
        }
    }

    [GeneratedRegex("SHARD_A_NAMESPACES: \"([^\"]*)\"")]
    private static partial Regex ShardList();
}
