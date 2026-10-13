using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.Mcp;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// Switching a tool off has to reach the MCP surface too (issue #1245): the
/// route gate only covers the pages, and an assistant that could still call
/// <c>deploy_build</c> after an Admin switched Deployment pipelines off is the
/// failure this pins. The gate answers from the organisation row, which is
/// what an MCP request has (its principal carries no disabled-tools claim).
/// </summary>
public sealed class McpToolGateTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_tool_switched_off_for_the_organisation_is_refused_by_name()
    {
        await DisableForOrgAsync(ToolKey.Releases);
        await using var ctx = _db.NewContext();
        var gate = new McpToolGate(_db.NewToolEnablement(ctx));

        var act = () => gate.EnsureAvailableAsync("deploy_build");

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("Deployment pipelines is switched off");
    }

    [Fact]
    public async Task Read_only_tools_of_a_switched_off_tool_are_refused_too()
    {
        await DisableForOrgAsync(ToolKey.Projects);
        await using var ctx = _db.NewContext();
        var gate = new McpToolGate(_db.NewToolEnablement(ctx));

        (await gate.IsAvailableAsync("list_environments")).Should().BeFalse();
        (await gate.IsAvailableAsync("list_customer_contacts")).Should().BeFalse();
        (await gate.IsAvailableAsync("search_objects")).Should().BeTrue("only the named tool is off");
    }

    [Fact]
    public async Task A_tool_switched_off_site_wide_is_refused_even_where_the_organisation_left_it_on()
    {
        var site = TestDb.EverythingEnabled();
        site.Set(new[] { ToolKey.Cookbook });
        await using var ctx = _db.NewContext();
        var gate = new McpToolGate(_db.NewToolEnablement(ctx, site));

        (await gate.IsAvailableAsync("get_recipe")).Should().BeFalse();
        var act = () => gate.EnsureAvailableAsync("apply_recipe");
        await act.Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task Tools_nobody_switched_off_and_tools_no_switch_owns_stay_available()
    {
        await DisableForOrgAsync(ToolKey.Translator);
        await using var ctx = _db.NewContext();
        var gate = new McpToolGate(_db.NewToolEnablement(ctx));

        await gate.EnsureAvailableAsync("deploy_build");
        (await gate.IsAvailableAsync("list_repositories")).Should().BeTrue(
            "it serves several tools at once, so only the MCP switch turns it off");
        (await gate.IsAvailableAsync("search_bcquality")).Should().BeTrue();
    }

    private async Task DisableForOrgAsync(params ToolKey[] keys)
    {
        await using var ctx = _db.NewContext();
        var org = await ctx.Organizations.SingleAsync(o => o.Id == TestDb.DefaultOrgId);
        org.DisabledTools = ToolCatalog.Format(keys);
        await ctx.SaveChangesAsync();
    }
}
