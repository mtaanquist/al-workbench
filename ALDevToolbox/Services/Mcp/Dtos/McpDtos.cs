using System.ComponentModel;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services.Mcp.Dtos;

/// <summary>
/// Input shape mirroring <see cref="ProjectPlan"/> for the
/// <c>generate_workspace</c> tool. Lives outside the domain layer so we
/// don't leak EF-coupled records to the MCP serialiser; <see cref="ToDomain"/>
/// is the one-liner mapping.
/// </summary>
public sealed record ProjectPlanInput(
    string TemplateKey,
    [property: Description("The customer's name, as it should appear, e.g. Jørgensen Møbler.")]
    string WorkspaceName,
    [property: Description("The word every generated extension's name starts with, e.g. JM in 'JM Core'. Only applies when your organisation leaves the prefix to each workspace; when it fixes one, or uses none, this is ignored and extensionPrefix in the result says what was used instead.")]
    string ExtensionPrefix,
    string Brief,
    string Description,
    [property: Description("A four-part application version, e.g. 28.2.0.0, or \"latest\" for the newest one in your organisation's catalogue.")]
    string ApplicationVersion,
    string RuntimeVersion,
    int CoreIdRangeFrom,
    int CoreIdRangeTo,
    bool IncludeExamples = true,
    [property: Description("Which of the template's optional extensions to generate, by path. get_template lists the legal values and says which are already required. Leave it out for the required ones only.")]
    IReadOnlyList<string>? SelectedExtensionPaths = null,
    [property: Description("Which modules to generate, by key, from list_modules. Leave it out to use the template's own default modules, which get_template lists.")]
    IReadOnlyList<string>? SelectedModuleKeys = null,
    [property: Description("Optional short form of the customer's name used in extension names, e.g. JM. Leave out to use the full name.")]
    string? ShortName = null)
{
    public ProjectPlan ToDomain() => new(
        TemplateKey,
        WorkspaceName,
        ShortName,
        ExtensionPrefix,
        Brief,
        Description,
        ApplicationVersion,
        RuntimeVersion,
        CoreIdRangeFrom,
        CoreIdRangeTo,
        IncludeExamples,
        SelectedExtensionPaths ?? Array.Empty<string>(),
        SelectedModuleKeys ?? Array.Empty<string>());
}

/// <summary>Mirror of <see cref="StandaloneExtensionPlan"/> for the MCP boundary.</summary>
public sealed record StandaloneExtensionPlanInput(
    string TemplateKey,
    [property: Description("The extension's name, e.g. Banking. Added to a repository that holds a solution, the solution's prefix is put in front of it (Banking becomes CRO Banking, beside CRO Core) and its folder is named without the prefix.")]
    string ExtensionName,
    string Brief,
    string Description,
    [property: Description("A four-part application version, e.g. 28.2.0.0, or \"latest\" for the newest one in your organisation's catalogue.")]
    string ApplicationVersion,
    string RuntimeVersion,
    int IdRangeFrom,
    int IdRangeTo,
    string Publisher,
    [property: Description("Whether to include the template's example files. Ignored when adding to a repository: examples are always left out there, so they cannot clash with the ones it already has.")]
    bool IncludeExamples = true,
    IReadOnlyList<DependencyEntryInput>? Dependencies = null)
{
    public StandaloneExtensionPlan ToDomain() => new(
        TemplateKey,
        ExtensionName,
        Brief,
        Description,
        ApplicationVersion,
        RuntimeVersion,
        IdRangeFrom,
        IdRangeTo,
        IncludeExamples,
        Publisher,
        Dependencies?.Select(d => d.ToDomain()).ToList() ?? new List<DependencyEntry>());
}

public sealed record DependencyEntryInput(string DepId, string DepName, string DepPublisher, string DepVersion)
{
    public DependencyEntry ToDomain() => new(DepId, DepName, DepPublisher, DepVersion);
}

/// <summary>
/// What a <c>generate_*</c> tool returns. The ZIP is inlined as base64 so
/// the agent has the bytes in hand without a follow-up download fetch.
/// </summary>
/// <param name="AddedToRepository">
/// Set only when the caller asked for the result to be added to a GitHub
/// repository (issue #623). The ZIP alongside it is the same one the pull
/// request carries, so an agent can hand either to the user.
/// </param>
/// <param name="CreatedRepository">
/// Set only when the caller asked for a new repository to be created for the
/// result (issue #622). As above, the ZIP alongside it is the one that was
/// committed, not a second generation with different extension GUIDs.
/// </param>
/// <param name="ExtensionPrefix">
/// The prefix the generated extension names actually carry, once the
/// organisation's prefix policy has had its say (#757) - which is not
/// necessarily the one the caller passed. Null for <c>generate_extension</c>,
/// which names one extension outright.
/// </param>
public sealed record WorkspaceResult(
    string FileName,
    string ContentBase64,
    int SizeBytes,
    string Sha256,
    RepositoryDeliveryResult? AddedToRepository = null,
    RepositoryCreationResult? CreatedRepository = null,
    string? ExtensionPrefix = null);

/// <summary>
/// The repository a <c>generate_*</c> tool created, when it was asked to put
/// its result in a new one.
/// </summary>
/// <param name="StandardsFileCount">
/// How many of the organisation's repository standard files were committed
/// alongside the workspace (issue #628).
/// </param>
/// <param name="StandardsWarning">
/// What GitHub refused while applying those standards, or null when nothing
/// was. The repository exists either way.
/// </param>
/// <param name="SolutionId">
/// The solution the repository was registered on (issue #759), or null when
/// registering it failed - <paramref name="SolutionWarning"/> then says so.
/// Null too, with no warning, for an organisation that has Solutions switched
/// off: it is registered on one only when the organisation uses them (#772).
/// </param>
/// <param name="SolutionName">That solution's name.</param>
/// <param name="SolutionCreated">
/// True when the solution was created for this customer, false when it was one
/// the caller named.
/// </param>
/// <param name="SolutionWarning">
/// Why the repository is not on a solution, or null when it is. The repository
/// exists either way.
/// </param>
public sealed record RepositoryCreationResult(
    string RepositoryFullName,
    string HtmlUrl,
    string CloneUrl,
    string DefaultBranch,
    bool IsPrivate,
    int FileCount,
    int StandardsFileCount = 0,
    string? StandardsWarning = null,
    int? SolutionId = null,
    string? SolutionName = null,
    bool SolutionCreated = false,
    string? SolutionWarning = null)
{
    /// <summary>
    /// The projection of a created repository, written once because two tools
    /// report the same thing: <c>generate_workspace</c> with its create-a-repository
    /// option, and <c>create_repository</c> on its own (issue #633).
    /// </summary>
    public static RepositoryCreationResult From(ALDevToolbox.Services.GitHub.GitHubWorkspaceRepository created) => new(
        RepositoryFullName: created.Repository.FullName,
        HtmlUrl: created.Repository.HtmlUrl,
        CloneUrl: created.Repository.CloneUrl,
        DefaultBranch: created.Repository.DefaultBranch,
        IsPrivate: created.Repository.IsPrivate,
        FileCount: created.FileCount,
        StandardsFileCount: created.StandardsFileCount,
        StandardsWarning: created.StandardsWarning,
        SolutionId: created.SolutionId,
        SolutionName: created.SolutionName,
        SolutionCreated: created.SolutionCreated,
        SolutionWarning: created.SolutionWarning);
}

/// <summary>
/// The pull request a <c>generate_*</c> tool opened, when it was asked to add
/// its result to an existing repository.
/// </summary>
/// <param name="IsNewPullRequest">
/// False when the commit joined a pull request that was already open on the
/// branch, which <c>apply_recipe</c> does when the same recipe is applied twice
/// before the first pull request is merged. A <c>generate_*</c> tool always
/// opens a fresh one, which is why it defaults to true.
/// </param>
public sealed record RepositoryDeliveryResult(
    string RepositoryFullName,
    string Branch,
    string BaseBranch,
    int PullRequestNumber,
    string PullRequestUrl,
    bool IsNewPullRequest = true)
{
    /// <summary>
    /// The projection of an extension added to a repository, written once
    /// because two tools report the same thing: <c>generate_extension</c> with
    /// its add-to-repository option, and <c>add_extension_to_repository</c> on
    /// its own (issue #633).
    /// </summary>
    public static RepositoryDeliveryResult From(ALDevToolbox.Services.GitHub.GitHubExtensionDelivery delivered) => new(
        RepositoryFullName: delivered.Repository.FullName,
        Branch: delivered.PullRequest.HeadBranch,
        BaseBranch: delivered.Repository.DefaultBranch,
        PullRequestNumber: delivered.PullRequest.Number,
        PullRequestUrl: delivered.PullRequest.HtmlUrl);
}

/// <summary>Trimmed projection of <see cref="RuntimeTemplate"/> for tool callers.</summary>
public sealed record TemplateSummary(
    string Key,
    string Name,
    string? Description,
    string Runtime,
    bool IsDefault,
    bool Deprecated,
    int CoreIdRangeFrom,
    int CoreIdRangeTo);

public sealed record ModuleSummary(
    string Key,
    string Name,
    [property: Description("The folder name and rendered AL extension name this module produces in a generated workspace.")]
    string ExtensionName,
    bool Deprecated);

/// <summary>
/// What <c>get_template</c> returns: everything the New Workspace form knows
/// when it renders, minus the per-extension folder trees, which need a separate
/// hydration pass and nothing has asked for yet (#792).
/// </summary>
public sealed record TemplateDetail(
    string Key,
    string Name,
    string? Description,
    string Runtime,
    bool IsDefault,
    bool Deprecated,
    int CoreIdRangeFrom,
    int CoreIdRangeTo,
    [property: Description("Where each module's ID range starts, and how wide it is.")]
    int ModuleIdRangeStart,
    int ModuleIdRangeSize,
    [property: Description("The extensions this template declares. A path from here is what selectedExtensionPaths takes; the required ones are always generated whether or not you name them.")]
    IReadOnlyList<TemplateExtensionSummary> Extensions,
    [property: Description("The modules this template pre-selects. Pass their keys in selectedModuleKeys to keep them, or a different set from list_modules to replace them.")]
    IReadOnlyList<ModuleSummary> DefaultModules,
    [property: Description("Files added to every generated workspace, such as the ruleset and .gitignore.")]
    IReadOnlyList<TemplateIncludedFileSummary> IncludedFiles,
    [property: Description("Empty folders created at the workspace root, e.g. .alpackages.")]
    IReadOnlyList<string> RootFolders,
    TemplateDefaultsSummary Defaults);

/// <summary>One extension a template declares.</summary>
public sealed record TemplateExtensionSummary(
    [property: Description("The value to pass in selectedExtensionPaths, e.g. Core.")]
    string Path,
    [property: Description("The extension's name before substitution, e.g. '{{prefix}} Core'.")]
    string NameTemplate,
    [property: Description("Required extensions are generated whether or not selectedExtensionPaths names them; the rest are opt-in.")]
    bool Required,
    [property: Description("Set only when this extension overrides the template's application version.")]
    string? Application,
    [property: Description("Set only when this extension overrides the template's runtime version.")]
    string? Runtime,
    [property: Description("Set only when this extension has its own ID range instead of one carved from the workspace's.")]
    int? IdRangeFrom,
    int? IdRangeTo);

/// <summary>One always-included file.</summary>
public sealed record TemplateIncludedFileSummary(
    string Path,
    [property: Description("WorkspaceRoot for a file at the top of the workspace, EveryExtension for one copied into each extension folder.")]
    string Scope);

/// <summary>The template's app.json defaults.</summary>
public sealed record TemplateDefaultsSummary(
    string Publisher,
    string Target,
    string Application,
    string Platform,
    string Affix,
    string AffixType,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> SupportedLocales);

public sealed record WellKnownDependencySummary(
    string DepId,
    string DepName,
    string DepPublisher,
    string DepVersion);

public sealed record RecipeSummary(
    int Id,
    string Title,
    string Description,
    string Keywords,
    string Type,
    bool Deprecated,
    int FileCount,
    string? MinimumApplicationVersionName = null,
    string? MinimumApplication = null,
    decimal? EstimatedValueHours = null);

public sealed record RecipeFileDto(string Path, string Content);

public sealed record RecipeDetail(
    int Id,
    string Title,
    string Description,
    string Keywords,
    string Type,
    bool Deprecated,
    IReadOnlyList<RecipeFileDto> Files,
    string? Instructions = null,
    string? MinimumApplicationVersionName = null,
    string? MinimumApplication = null,
    decimal? EstimatedValueHours = null);

/// <summary>
/// One file body submitted as part of a <see cref="SuggestRecipeInput"/>.
/// Distinct from the read-side <see cref="RecipeFileDto"/> (which uses a
/// single <c>Path</c> field combining folder + name) so the MCP-facing
/// field names match the domain's <c>RecipeFileInput</c>. <c>RelativePath</c>
/// is empty for files at the recipe's root.
/// </summary>
public sealed record RecipeFileInputDto(string FileName, string Content, string RelativePath = "");

/// <summary>
/// Input shape for the <c>suggest_recipe</c> tool. Mirrors
/// <see cref="ALDevToolbox.Services.Cookbook.RecipeSuggestionInput"/> for the MCP
/// boundary; <see cref="ToDomain"/> is the one-liner mapping. <c>Type</c>
/// is a string (<c>Snippet</c>, <c>Pattern</c>, or <c>Module</c>) so the
/// agent reads the same name humans see on the cookbook chip-row.
/// <c>GuidanceToken</c> is the short-lived signed token returned by
/// <c>get_cookbook_guidance</c>; the write tool refuses to run without
/// a valid one.
/// </summary>
public sealed record SuggestRecipeInput(
    string GuidanceToken,
    string Title,
    string Description,
    string Keywords,
    string Type,
    IReadOnlyList<RecipeFileInputDto> Files,
    string? Instructions = null,
    int? MinimumApplicationVersionId = null,
    decimal? EstimatedValueHours = null)
{
    public ALDevToolbox.Services.Cookbook.RecipeSuggestionInput ToDomain() => new(
        Title,
        Description,
        Keywords,
        ParseType(Type),
        Files
            .Select(f => new ALDevToolbox.Services.Cookbook.RecipeFileInput(f.FileName, f.Content, f.RelativePath))
            .ToList(),
        Instructions,
        MinimumApplicationVersionId,
        EstimatedValueHours);

    internal static ALDevToolbox.Domain.ValueObjects.RecipeType ParseType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ALDevToolbox.Domain.ValueObjects.RecipeType.Snippet;
        return Enum.TryParse<ALDevToolbox.Domain.ValueObjects.RecipeType>(raw.Trim(), ignoreCase: true, out var t)
            ? t
            : throw new ModelContextProtocol.McpException(
                $"Unknown recipe Type '{raw}'. Use one of: Snippet, Pattern, Module.");
    }
}

/// <summary>What <c>suggest_recipe</c> returns: the new suggestion's id plus a confirmation pointing the agent at the admin queue.</summary>
public sealed record SuggestRecipeResult(int SuggestionId, string Message);

/// <summary>
/// Input shape for the <c>update_recipe_suggestion</c> tool. Carries the
/// id of the suggestion being edited alongside the same fields
/// <see cref="SuggestRecipeInput"/> accepts; <see cref="ToDomain"/> drops
/// the id when handing off to the service layer (which already takes the
/// id as a separate argument). Requires the same <c>GuidanceToken</c>
/// gate as <see cref="SuggestRecipeInput"/>.
/// </summary>
public sealed record UpdateRecipeSuggestionInput(
    int SuggestionId,
    string GuidanceToken,
    string Title,
    string Description,
    string Keywords,
    string Type,
    IReadOnlyList<RecipeFileInputDto> Files,
    string? Instructions = null,
    int? MinimumApplicationVersionId = null,
    decimal? EstimatedValueHours = null)
{
    public ALDevToolbox.Services.Cookbook.RecipeSuggestionInput ToDomain() => new(
        Title,
        Description,
        Keywords,
        SuggestRecipeInput.ParseType(Type),
        Files
            .Select(f => new ALDevToolbox.Services.Cookbook.RecipeFileInput(f.FileName, f.Content, f.RelativePath))
            .ToList(),
        Instructions,
        MinimumApplicationVersionId,
        EstimatedValueHours);
}

/// <summary>What <c>update_recipe_suggestion</c> returns: the updated suggestion's id plus a confirmation.</summary>
public sealed record UpdateRecipeSuggestionResult(int SuggestionId, string Message);

/// <summary>
/// Input shape for the <c>update_recipe</c> tool: a full-replace payload
/// for an already-published recipe, mirroring <see cref="UpdateRecipeSuggestionInput"/>
/// plus the fields only published recipes carry. <c>Deprecated</c> is
/// nullable — omitting it keeps the recipe's current flag rather than
/// silently un-deprecating on every edit. Requires the same
/// <c>GuidanceToken</c> gate as the suggestion write tools; the caller
/// must additionally hold the Editor or Admin role.
/// </summary>
public sealed record UpdateRecipeInput(
    int RecipeId,
    string GuidanceToken,
    string Title,
    string Description,
    string Keywords,
    string Type,
    IReadOnlyList<RecipeFileInputDto> Files,
    string? Instructions = null,
    int? MinimumApplicationVersionId = null,
    decimal? EstimatedValueHours = null,
    bool? Deprecated = null)
{
    public ALDevToolbox.Services.Cookbook.RecipeInput ToDomain(bool currentDeprecated) => new(
        Title,
        Description,
        Keywords,
        SuggestRecipeInput.ParseType(Type),
        Deprecated ?? currentDeprecated,
        Files
            .Select(f => new ALDevToolbox.Services.Cookbook.RecipeFileInput(f.FileName, f.Content, f.RelativePath))
            .ToList(),
        Instructions,
        MinimumApplicationVersionId,
        EstimatedValueHours);
}

/// <summary>What <c>update_recipe</c> returns: the unchanged recipe id plus a confirmation.</summary>
public sealed record UpdateRecipeResult(int RecipeId, string Message);

/// <summary>
/// What <c>get_cookbook_guidance</c> returns: the org's authored markdown,
/// the built-in type taxonomy (so an empty org-level guidance still gives
/// the agent something to anchor on), and a short-lived signed
/// <c>GuidanceToken</c> the write tools require. <c>GuidanceTokenExpiresInSeconds</c>
/// is the lifetime in seconds; tokens older than that are refused.
/// </summary>
public sealed record CookbookGuidance(
    string Guidance,
    IReadOnlyList<string> RecipeTypes,
    IReadOnlyDictionary<string, string> TypeDescriptions,
    string GuidanceToken,
    int GuidanceTokenExpiresInSeconds);

/// <summary>
/// What <c>list_repositories</c> returns: the repositories the caller can act
/// on, and - when there are none for a reason they can do something about - the
/// sentence that says what to do next.
/// </summary>
/// <param name="Readiness">
/// The state of the GitHub connection, named as
/// <see cref="ALDevToolbox.Services.GitHub.GitHubRepositoryReadiness"/> names
/// it, so an agent can branch on the state rather than on the prose.
/// </param>
/// <param name="Guidance">
/// Null when everything is in place. Otherwise one plain sentence to pass on to
/// the person, naming the step that unblocks them.
/// </param>
public sealed record RepositoryListResult(
    string Readiness,
    string? Guidance,
    IReadOnlyList<RepositorySummary> Repositories);

/// <summary>
/// One repository, as an agent needs it: enough to name it in a later call, to
/// link to it, and to clone it.
/// </summary>
public sealed record RepositorySummary(
    string FullName,
    string DefaultBranch,
    bool IsPrivate,
    string? Description,
    string HtmlUrl,
    string CloneUrl);

/// <summary>One XLIFF file <c>list_translation_files</c> found in a repository.</summary>
/// <param name="Folder">
/// The folder holding the <c>Translations</c> folder - the extension, in an AL
/// workspace. Empty when <c>Translations</c> sits at the repository root.
/// </param>
/// <param name="IsSource">
/// True for the file the AL compiler generates. It holds every string and no
/// translations, so it is the file a new language starts from.
/// </param>
public sealed record TranslationFileSummary(
    string Path,
    string Folder,
    string? Language,
    bool IsSource);

/// <summary>One translated string for <c>open_translation_pr</c> to write.</summary>
/// <param name="Id">The trans-unit id, exactly as the file carries it.</param>
/// <param name="Target">The translated text.</param>
/// <param name="State">
/// Optional XLIFF state for this target, e.g. <c>translated</c>. Left as it was
/// when not given.
/// </param>
public sealed record TranslationUnitEditInput(string Id, string Target, string? State = null);

/// <summary>What <c>open_translation_pr</c> did.</summary>
/// <param name="SavedPath">
/// The file that was written. It is the file that was read, unless that was the
/// compiler's generated source file - then it is the new language file written
/// beside it, because the generated file belongs to the compiler.
/// </param>
/// <param name="UnitsEdited">How many strings the commit changed.</param>
public sealed record TranslationPullRequestResult(
    RepositoryDeliveryResult PullRequest,
    bool IsNewPullRequest,
    string SavedPath,
    int UnitsEdited);
