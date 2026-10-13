using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.Tools;
using ModelContextProtocol;

namespace ALDevToolbox.Services.Mcp;

/// <summary>
/// Switching a tool off (Administration → Tools, or site-wide by a SiteAdmin)
/// reaches the MCP surface here. The pages are covered by
/// <c>ToolAccessGate</c>'s route prefixes, which an MCP call never passes
/// through, so without this an assistant could still deploy a build after an
/// Admin had switched Deployment pipelines off (issue #1245).
///
/// <para>Each MCP tool names the toggleable tool it belongs to in
/// <see cref="McpToolCatalog"/>; a tool that names none (one that serves
/// several tools, or none) is governed by the MCP switch alone. The MCP
/// request filters ask this twice: once to refuse a call, once to drop the
/// switched-off tools from <c>tools/list</c>, so an assistant is not offered a
/// tool it would only be refused.</para>
/// </summary>
public sealed class McpToolGate
{
    private readonly ToolEnablement _tools;

    public McpToolGate(ToolEnablement tools)
    {
        _tools = tools;
    }

    /// <summary>True when the MCP tool named <paramref name="mcpToolName"/> may be offered and called.</summary>
    public async Task<bool> IsAvailableAsync(string mcpToolName, CancellationToken ct = default) =>
        McpToolCatalog.ToolFor(mcpToolName) is not { } key || await _tools.IsEnabledAsync(key, ct);

    /// <summary>Refuses the call when the tool it belongs to is switched off, saying which tool and who can change it.</summary>
    /// <exception cref="McpException">The tool is switched off for the acting organisation or the whole site.</exception>
    public async Task EnsureAvailableAsync(string mcpToolName, CancellationToken ct = default)
    {
        if (McpToolCatalog.ToolFor(mcpToolName) is not { } key) return;
        if (await _tools.IsEnabledAsync(key, ct)) return;
        throw new McpException(
            $"{mcpToolName} cannot be used because {ToolCatalog.Describe(key).Name} is switched off. "
            + "Ask an administrator to switch it back on if you need it.");
    }
}
