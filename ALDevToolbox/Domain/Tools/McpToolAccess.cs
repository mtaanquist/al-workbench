namespace ALDevToolbox.Domain.Tools;

/// <summary>
/// Which toggleable tool each <em>writing</em> MCP tool belongs to, so the
/// step-up rule an admin sets per tool on Administration → Tools reaches the
/// MCP surface too. Read-only MCP tools are deliberately absent: a recent
/// second factor guards the actions that spend a stored credential (creating
/// a repository, opening a pull request, deploying to an environment), not
/// the lookups around them. <c>McpToolAccessTests</c> pins this map to the
/// catalogue: every tool with <see cref="McpToolDescriptor.Writes"/> must
/// appear here, and nothing else may.
/// </summary>
public static class McpToolAccess
{
    public static readonly IReadOnlyDictionary<string, ToolKey> WritingTools = new Dictionary<string, ToolKey>(StringComparer.Ordinal)
    {
        // Templates and generation, including the GitHub repository variants:
        // both create a repository or a pull request in the org's GitHub.
        ["generate_workspace"] = ToolKey.Templates,
        ["generate_extension"] = ToolKey.Templates,
        ["create_repository"] = ToolKey.Templates,
        ["add_extension_to_repository"] = ToolKey.Templates,
        // Cookbook: suggestions stay inside the workbench; apply_recipe opens a
        // pull request.
        ["suggest_recipe"] = ToolKey.Cookbook,
        ["update_recipe_suggestion"] = ToolKey.Cookbook,
        ["update_recipe"] = ToolKey.Cookbook,
        ["apply_recipe"] = ToolKey.Cookbook,
        // Translator: votes and withdrawals stay inside; open_translation_pr
        // reaches GitHub.
        ["vote_translation"] = ToolKey.Translator,
        ["remove_translation"] = ToolKey.Translator,
        ["open_translation_pr"] = ToolKey.Translator,
        // Deployment pipelines: both touch a customer's Business Central
        // environment or fetch the files that will.
        ["deploy_build"] = ToolKey.Releases,
        ["stage_github_release"] = ToolKey.Releases,
    };

    /// <summary>The tool a writing MCP tool belongs to, or null for a read-only or unknown tool.</summary>
    public static ToolKey? ToolFor(string mcpToolName) =>
        WritingTools.TryGetValue(mcpToolName, out var key) ? key : null;
}
