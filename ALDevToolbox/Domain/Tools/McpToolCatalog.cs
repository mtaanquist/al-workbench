namespace ALDevToolbox.Domain.Tools;

/// <summary>
/// One MCP tool, described for a person rather than for an agent.
/// </summary>
/// <param name="Name">The wire name the agent calls, e.g. <c>search_objects</c>.</param>
/// <param name="Group">Which part of the workbench it reaches, used to group the docs table.</param>
/// <param name="Blurb">One plain sentence. Not the agent-facing <c>[Description]</c>, which
/// is prompt text ("use search_recipes to find candidate ids") and reads as nonsense to a
/// consultant looking up what their assistant can do.</param>
/// <param name="Writes">True when the tool changes something. Mirrors
/// <c>McpServerToolAttribute.ReadOnly == false</c>; the catalogue test pins the two together.</param>
/// <param name="Tool">For a writing tool, the toggleable tool whose step-up rule
/// (Administration → Tools) governs it. Read-only tools carry none: a recent second factor
/// guards the actions that spend a stored credential, not the lookups around them.
/// <c>McpToolCatalogTests</c> requires every writing tool to name one.</param>
public sealed record McpToolDescriptor(string Name, string Group, string Blurb, bool Writes, ToolKey? Tool = null);

/// <summary>
/// The user-facing description of every tool an AI assistant can call, for
/// <c>/tools/mcp</c> and <c>/docs/mcp</c>.
///
/// It exists because both pages used to carry a hand-typed table, and both had
/// gone stale: they listed 13 and 15 tools against the 38 actually registered,
/// and two of the entries (<c>search_snippets</c>, <c>get_snippet</c>) were
/// renamed with the Cookbook and no longer exist, so the docs told people to
/// ask for a tool the server would refuse.
///
/// <c>McpToolCatalogTests</c> reflects over the same
/// <c>[McpServerTool]</c> attributes <c>WithToolsFromAssembly()</c> registers
/// and fails if this list and the server disagree on either the set of names or
/// which of them write. A new tool cannot ship undocumented, and a renamed one
/// cannot leave a dead name on the page.
/// </summary>
public static class McpToolCatalog
{
    /// <summary>The toggleable tool a writing MCP tool belongs to, or null for a read-only or unknown one.</summary>
    public static ToolKey? ToolFor(string mcpToolName) =>
        All.FirstOrDefault(t => t.Name == mcpToolName)?.Tool;

    public const string Generation = "Templates and generation";
    public const string ObjectExplorer = "Object Explorer";
    public const string Cookbook = "Cookbook";
    public const string Translator = "Translator";
    public const string Projects = "Solutions and pipelines";
    public const string Operations = "Environments and customers";
    public const string Repositories = "GitHub repositories";
    public const string BcQuality = "Quality guidance";

    /// <summary>Group order for the docs table — the order the tools appear in the sidebar.</summary>
    public static readonly IReadOnlyList<string> Groups =
        new[] { Generation, ObjectExplorer, Cookbook, Translator, Projects, Operations, Repositories, BcQuality };

    public static readonly IReadOnlyList<McpToolDescriptor> All = new[]
    {
        // ---- Templates and generation ----
        new McpToolDescriptor("list_templates", Generation,
            "Lists the workspace templates your organisation can generate from.", false),
        new McpToolDescriptor("get_template", Generation,
            "Shows what one template will generate, so the assistant can offer its optional parts.", false),
        new McpToolDescriptor("list_modules", Generation,
            "Lists the optional modules that can be added to a workspace.", false),
        new McpToolDescriptor("list_well_known_dependencies", Generation,
            "Lists the dependencies your organisation has on file, so the assistant names them correctly.", false),
        new McpToolDescriptor("generate_workspace", Generation,
            "Builds a new workspace and hands back the ZIP, or creates a repository for it in "
            + "your GitHub organisation.", true, ToolKey.Templates),
        new McpToolDescriptor("generate_extension", Generation,
            "Builds a new standalone extension and hands back the ZIP, or adds it to one of "
            + "your GitHub repositories as a pull request.", true, ToolKey.Templates),

        // ---- Object Explorer ----
        new McpToolDescriptor("list_releases", ObjectExplorer,
            "Lists the Business Central releases that have been imported.", false),
        new McpToolDescriptor("list_release_modules", ObjectExplorer,
            "Lists the apps and modules inside one release.", false),
        new McpToolDescriptor("compare_releases", ObjectExplorer,
            "Reports what changed between two releases.", false),
        new McpToolDescriptor("compare_release_files", ObjectExplorer,
            "Reports which files differ between two releases, including the ones that hold no "
            + "objects, such as permission sets and translations.", false),
        new McpToolDescriptor("search_objects", ObjectExplorer,
            "Finds tables, pages, codeunits and the rest by name or number.", false),
        new McpToolDescriptor("search_procedures", ObjectExplorer,
            "Finds procedures by name across a release.", false),
        new McpToolDescriptor("search_content", ObjectExplorer,
            "Searches the AL source itself, for when you remember the code but not the object.", false),
        new McpToolDescriptor("find_references", ObjectExplorer,
            "Finds every place that touches a table, field or procedure.", false),
        new McpToolDescriptor("find_system_references", ObjectExplorer,
            "Finds uses of platform types that the base application does not declare.", false),
        new McpToolDescriptor("get_object_outline", ObjectExplorer,
            "Returns one object's fields, procedures and triggers.", false),
        new McpToolDescriptor("get_procedure_source", ObjectExplorer,
            "Returns the body of one procedure, up to 200 lines.", false),
        new McpToolDescriptor("list_procedure_calls", ObjectExplorer,
            "Returns what one procedure calls and which fields it touches, so you can trace what it ends up doing.", false),
        new McpToolDescriptor("download_symbol_reference", ObjectExplorer,
            "Fetches a release's symbol package, so the assistant can compile against it.", false),

        // ---- Cookbook ----
        new McpToolDescriptor("search_recipes", Cookbook,
            "Searches your organisation's recipes by title, description or keyword.", false),
        new McpToolDescriptor("get_recipe", Cookbook,
            "Returns one recipe in full, every file included.", false),
        new McpToolDescriptor("get_cookbook_guidance", Cookbook,
            "Reads your organisation's house rules for writing a recipe, so its suggestions "
            + "follow your house style.", false),
        new McpToolDescriptor("suggest_recipe", Cookbook,
            "Submits a new recipe for an editor to review. Nothing is published without a person.", true, ToolKey.Cookbook),
        new McpToolDescriptor("update_recipe_suggestion", Cookbook,
            "Revises a suggestion the assistant already submitted.", true, ToolKey.Cookbook),
        new McpToolDescriptor("update_recipe", Cookbook,
            "Edits a published recipe. Needs the Editor role.", true, ToolKey.Cookbook),
        new McpToolDescriptor("apply_recipe", Cookbook,
            "Puts a recipe into one of your GitHub repositories as a pull request, in your name.", true, ToolKey.Cookbook),

        // ---- Translator ----
        new McpToolDescriptor("list_translation_languages", Translator,
            "Lists the languages a release has been translated into.", false),
        new McpToolDescriptor("search_translations", Translator,
            "Finds how a phrase was translated in a release.", false),
        new McpToolDescriptor("search_translation_memory", Translator,
            "Searches your organisation's own past translations for a phrase.", false),
        new McpToolDescriptor("machine_translate", Translator,
            "Proposes a translation. It is a suggestion until someone accepts it.", false),
        new McpToolDescriptor("vote_translation", Translator,
            "Votes a suggested translation up or down.", true, ToolKey.Translator),
        new McpToolDescriptor("remove_translation", Translator,
            "Withdraws a translation the assistant suggested.", true, ToolKey.Translator),

        // ---- Solutions and pipelines ----
        new McpToolDescriptor("list_solutions", Projects,
            "Lists the customer solutions you can see.", false),
        new McpToolDescriptor("list_solution_builds", Projects,
            "Lists the builds recorded against one solution.", false),
        new McpToolDescriptor("get_solution_build", Projects,
            "Returns one build with its apps and their versions.", false),
        new McpToolDescriptor("compare_solution_builds", Projects,
            "Reports what changed between two builds of the same solution.", false),
        new McpToolDescriptor("list_pipelines", Projects,
            "Lists the build pipelines set up for a solution.", false),
        new McpToolDescriptor("list_pipeline_builds", Projects,
            "Lists what one pipeline has built, and how each run ended.", false),
        new McpToolDescriptor("list_deployment_pipelines", Projects,
            "Lists the deployment pipelines that install builds into a Business Central environment.", false),
        new McpToolDescriptor("list_deployments", Projects,
            "Lists what one deployment pipeline has installed, when, and whether it landed.", false),
        new McpToolDescriptor("deploy_build", Projects,
            "Deploys a build to a Business Central environment.", true, ToolKey.Releases),
        new McpToolDescriptor("list_github_releases", Projects,
            "Lists the GitHub releases a deployment pipeline can install.", false),
        new McpToolDescriptor("stage_github_release", Projects,
            "Fetches the app files from a GitHub release so they can be deployed.", true, ToolKey.Releases),

        // ---- Environments and customers (read-only; see DeliverTools) ----
        new McpToolDescriptor("get_solution", Operations,
            "Shows where a customer's Business Central runs, which version, and whether it is connected.", false),
        new McpToolDescriptor("list_environments", Operations,
            "Lists your customers' Business Central environments with version, storage and next update, "
            + "filtered the way you ask.", false),
        new McpToolDescriptor("get_environment", Operations,
            "Shows one environment in full, including the apps installed in it.", false),
        new McpToolDescriptor("list_environment_history", Operations,
            "Lists what was done to an environment from the workbench, and by whom.", false),
        new McpToolDescriptor("list_upgrades", Operations,
            "Lists the platform updates coming to each environment, and when. Needs permission to "
            + "manage environment updates.", false),
        new McpToolDescriptor("list_recent_deployments", Operations,
            "Lists recent deployments across all your customers, and why any of them failed.", false),
        new McpToolDescriptor("list_customer_contacts", Operations,
            "Lists who to call or write to at a customer, with phone numbers and email addresses.", false),
        new McpToolDescriptor("get_customer_access", Operations,
            "Returns the notes on how to get into a customer's system, and what it is integrated with.", false),
        new McpToolDescriptor("list_customer_knowledge", Operations,
            "Tells you who here knows a customer, or which customers a colleague knows.", false),
        new McpToolDescriptor("list_customer_modules", Operations,
            "Tells you which customers have a module, or which modules a customer has.", false),

        // ---- GitHub repositories ----
        new McpToolDescriptor("list_repositories", Repositories,
            "Lists the GitHub repositories you can work on, and says what to connect when there "
            + "are none yet.", false),
        new McpToolDescriptor("create_repository", Repositories,
            "Creates a repository in your GitHub organisation with a new workspace in it.", true, ToolKey.Templates),
        new McpToolDescriptor("add_extension_to_repository", Repositories,
            "Adds a new extension to one of your repositories as a pull request, in your name.", true, ToolKey.Templates),
        new McpToolDescriptor("list_translation_files", Repositories,
            "Lists the translation files in one of your repositories.", false),
        new McpToolDescriptor("open_translation_pr", Repositories,
            "Writes translations into a file in one of your repositories and opens a pull request "
            + "for them.", true, ToolKey.Translator),

        // ---- Quality guidance ----
        new McpToolDescriptor("search_bcquality", BcQuality,
            "Searches Microsoft's published Business Central quality guidance, filtered to the "
            + "version you are building for.", false),
        new McpToolDescriptor("get_bcquality_article", BcQuality,
            "Returns one piece of that guidance in full, with its good and bad code examples.", false),
    };

    /// <summary>The tools in one group, in catalogue order.</summary>
    public static IEnumerable<McpToolDescriptor> InGroup(string group) =>
        All.Where(t => t.Group == group);
}
