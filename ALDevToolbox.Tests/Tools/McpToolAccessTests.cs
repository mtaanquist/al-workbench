using ALDevToolbox.Domain.Tools;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// Pins <see cref="McpToolAccess"/> to <see cref="McpToolCatalog"/>: the
/// step-up rule reaches every MCP tool that writes, and never a read-only
/// one. A new writing tool cannot ship without deciding which tool's rule
/// governs it.
/// </summary>
public sealed class McpToolAccessTests
{
    [Fact]
    public void Every_writing_tool_is_mapped_and_no_read_only_tool_is()
    {
        var writing = McpToolCatalog.All.Where(t => t.Writes).Select(t => t.Name).ToHashSet();
        var mapped = McpToolAccess.WritingTools.Keys.ToHashSet();

        writing.Except(mapped).Should().BeEmpty(
            "a writing MCP tool must name the tool whose step-up rule covers it (Domain/Tools/McpToolAccess.cs)");
        mapped.Except(writing).Should().BeEmpty(
            "read-only MCP tools are never gated by step-up");
    }

    [Fact]
    public void Read_only_and_unknown_names_map_to_nothing()
    {
        McpToolAccess.ToolFor("list_environments").Should().BeNull();
        McpToolAccess.ToolFor("no_such_tool").Should().BeNull();
        McpToolAccess.ToolFor("deploy_build").Should().Be(ToolKey.Releases);
    }
}
