using System.Reflection;
using ALDevToolbox.Domain.Tools;
using AwesomeAssertions;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// Pins the user-facing MCP tool table to the tools the server actually
/// registers.
///
/// The two pages that list the tools (<c>/tools/mcp</c> and <c>/docs/mcp</c>)
/// each carried a hand-typed table, and both had drifted: 13 and 15 rows
/// against 38 registered tools, including two names
/// (<c>search_snippets</c>, <c>get_snippet</c>) that were renamed with the
/// Cookbook and no longer exist — so the docs named tools the server would
/// refuse. Nothing failed, because nothing was checking.
///
/// These tests reflect over the same <c>[McpServerTool]</c> attributes
/// <c>WithToolsFromAssembly()</c> discovers, so adding a tool without
/// documenting it, or renaming one without updating the page, is a red build.
/// </summary>
public sealed class McpToolCatalogTests
{
    private static IReadOnlyList<McpServerToolAttribute> RegisteredTools() =>
        typeof(ALDevToolbox.Services.Mcp.IMcpAvailability).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

    [Fact]
    public void Every_registered_tool_is_documented_and_every_documented_tool_is_registered()
    {
        var registered = RegisteredTools().Select(a => a.Name).ToHashSet();
        var documented = McpToolCatalog.All.Select(t => t.Name).ToHashSet();

        // Named separately so a failure says which direction went wrong rather
        // than dumping a set difference the reader has to diff by eye.
        documented.Except(registered).Should().BeEmpty(
            "the docs must not name a tool the server does not register");
        registered.Except(documented).Should().BeEmpty(
            "a new MCP tool has to be added to McpToolCatalog so it shows up on /tools/mcp and /docs/mcp");
    }

    [Fact]
    public void The_catalogue_agrees_with_the_server_about_which_tools_write()
    {
        var readOnlyByName = RegisteredTools().ToDictionary(a => a.Name!, a => a.ReadOnly);

        foreach (var tool in McpToolCatalog.All)
        {
            readOnlyByName.Should().ContainKey(tool.Name);
            tool.Writes.Should().Be(
                readOnlyByName[tool.Name] == false,
                "the docs mark {0} as {1}, so the tool's ReadOnly flag has to agree",
                tool.Name,
                tool.Writes ? "changing something" : "read-only");
        }
    }

    [Fact]
    public void Every_writing_tool_names_the_tool_whose_step_up_rule_covers_it()
    {
        // The step-up rule an admin sets per tool reaches the MCP surface
        // through this field; a writing tool without one would be ungated.
        foreach (var tool in McpToolCatalog.All)
        {
            if (tool.Writes)
            {
                tool.Tool.Should().NotBeNull("{0} changes something, so it needs a tool to follow", tool.Name);
            }
            else
            {
                tool.Tool.Should().BeNull("{0} is read-only and is never gated by step-up", tool.Name);
            }
        }
        McpToolCatalog.ToolFor("deploy_build").Should().Be(ToolKey.Releases);
        McpToolCatalog.ToolFor("list_environments").Should().BeNull();
        McpToolCatalog.ToolFor("no_such_tool").Should().BeNull();
    }

    [Fact]
    public void Every_tool_lands_in_a_group_the_table_actually_renders()
    {
        McpToolCatalog.All.Select(t => t.Group).Distinct()
            .Should().BeSubsetOf(McpToolCatalog.Groups,
                "a tool in an unlisted group would be silently missing from the page");

        foreach (var group in McpToolCatalog.Groups)
        {
            McpToolCatalog.InGroup(group).Should().NotBeEmpty(
                "an empty group renders as a heading with nothing under it");
        }
    }

    [Fact]
    public void Blurbs_are_written_for_a_person()
    {
        foreach (var tool in McpToolCatalog.All)
        {
            tool.Blurb.Should().EndWith(".", "these render as prose, not as labels");
            tool.Blurb.Should().NotContain("_", "a blurb that names another tool by its wire "
                + "name is agent prompt text, not help for a reader");
        }
    }
}
