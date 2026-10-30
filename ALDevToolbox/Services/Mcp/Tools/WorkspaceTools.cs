using System.ComponentModel;
using System.Security.Cryptography;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Mcp.Dtos;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// MCP tools that wrap <see cref="TemplateService"/>, <see cref="ModuleService"/>,
/// <see cref="CatalogService"/>, and <see cref="GenerationService"/>.
/// Read tools return summaries; <c>generate_*</c> returns a short-lived
/// download link to the ZIP (see <see cref="WorkspaceResult"/> and
/// <see cref="GeneratedDownloadStore"/>).
/// </summary>
[McpServerToolType]
public sealed class WorkspaceTools
{
    private readonly TemplateService _templates;
    private readonly ModuleService _modules;
    private readonly CatalogService _catalog;
    private readonly GenerationService _generation;
    private readonly GitHubExtensionDeliveryService _delivery;
    private readonly GitHubWorkspaceRepositoryService _repositories;
    private readonly GeneratedDownloadStore _downloads;
    private readonly IOrganizationContext _orgContext;
    private readonly McpOptions _options;

    public WorkspaceTools(
        TemplateService templates,
        ModuleService modules,
        CatalogService catalog,
        GenerationService generation,
        GitHubExtensionDeliveryService delivery,
        GitHubWorkspaceRepositoryService repositories,
        GeneratedDownloadStore downloads,
        IOrganizationContext orgContext,
        IOptions<McpOptions> options)
    {
        _templates = templates;
        _modules = modules;
        _catalog = catalog;
        _generation = generation;
        _delivery = delivery;
        _repositories = repositories;
        _downloads = downloads;
        _orgContext = orgContext;
        _options = options.Value;
    }

    [McpServerTool(Name = "list_templates", ReadOnly = true)]
    [Description("Lists the workspace and standalone-extension templates available in the caller's organisation. Returns each template's key (use it as templateKey when generating), name, runtime version, and default core ID range.")]
    public async Task<IReadOnlyList<TemplateSummary>> ListTemplatesAsync(
        [Description("If true, include templates that have been deprecated. Defaults to false.")] bool includeDeprecated = false,
        CancellationToken ct = default)
    {
        var rows = await _templates.GetTemplatesAsync(includeDeprecated: includeDeprecated, ct);
        return rows.Select(t => new TemplateSummary(
            t.Key, t.Name, t.Description, t.Runtime, t.IsDefault, t.Deprecated,
            t.CoreIdRangeFrom, t.CoreIdRangeTo)).ToList();
    }

    [McpServerTool(Name = "get_template", ReadOnly = true)]
    [Description("Returns what one template will generate: the extensions it declares (and which are optional), the modules it pre-selects, the files and root folders every workspace gets, and its app.json defaults. Use it before generate_workspace to find the legal values for selectedExtensionPaths. Per-extension folder contents are not included.")]
    public async Task<TemplateDetail> GetTemplateAsync(
        [Description("Template key from list_templates.")] string templateKey,
        CancellationToken ct = default)
    {
        var row = await _templates.GetByKeyAsync(templateKey, ct);
        // GetByKeyAsync does not filter soft-deleted rows, but list_templates
        // does, so refuse them here rather than answering for a template the
        // caller could not have found (#792).
        if (row is null || row.DeletedAt is not null)
        {
            throw new McpException(
                $"Template '{templateKey}' not found, or not visible to your organisation. Use list_templates for the keys you can use.");
        }

        return new TemplateDetail(
            row.Key, row.Name, row.Description, row.Runtime, row.IsDefault, row.Deprecated,
            row.CoreIdRangeFrom, row.CoreIdRangeTo,
            row.ModuleIdRangeStart, row.ModuleIdRangeSize,
            row.WorkspaceExtensions.Select(e => new TemplateExtensionSummary(
                e.Path, e.NameTemplate, e.Required, e.Application, e.Runtime,
                e.IdRangeFrom, e.IdRangeTo)).ToList(),
            // Soft-deleted modules drop out, matching what New Workspace does
            // when it pre-selects this set: a key the form would never tick is
            // not one to hand an agent. Deprecated ones stay, carrying the flag,
            // because the template does still name them.
            row.DefaultModules
                .Where(d => d.Module is not null && d.Module.DeletedAt is null)
                .Select(d => new ModuleSummary(
                    d.Module!.Key, d.Module.Name, d.Module.ExtensionName, d.Module.Deprecated,
                    d.Module.SourceRepository, d.Module.SourceCommitSha))
                .ToList(),
            row.IncludedFiles
                .Where(f => f.OrganizationFile is not null)
                .Select(f => new TemplateIncludedFileSummary(
                    f.OrganizationFile!.Path, f.OrganizationFile.Scope.ToString()))
                .ToList(),
            row.RootFolders.Select(f => f.Path).ToList(),
            new TemplateDefaultsSummary(
                row.Defaults.Publisher, row.Defaults.Target, row.Defaults.Application,
                row.Defaults.Platform, row.Defaults.Affix, row.Defaults.AffixType.ToString(),
                row.Defaults.Features, row.Defaults.SupportedLocales));
    }

    [McpServerTool(Name = "list_modules", ReadOnly = true)]
    [Description("Lists the optional modules (per-org named code blocks) that can be selected when generating a workspace.")]
    public async Task<IReadOnlyList<ModuleSummary>> ListModulesAsync(
        [Description("If true, include modules that have been deprecated. Defaults to false.")] bool includeDeprecated = false,
        CancellationToken ct = default)
    {
        var rows = await _modules.GetAllForAdminAsync(includeDeleted: false, ct);
        if (!includeDeprecated)
        {
            rows = rows.Where(m => !m.Deprecated).ToList();
        }
        return rows.Select(m => new ModuleSummary(
            m.Key, m.Name, m.ExtensionName, m.Deprecated, m.SourceRepository, m.SourceCommitSha)).ToList();
    }

    [McpServerTool(Name = "list_well_known_dependencies", ReadOnly = true)]
    [Description("Lists the catalogue of well-known BC dependencies (id, publisher, version) the caller can add when generating a standalone extension.")]
    public async Task<IReadOnlyList<WellKnownDependencySummary>> ListWellKnownDependenciesAsync(
        CancellationToken ct = default)
    {
        var rows = await _catalog.GetAllAsync(ct);
        return rows.Select(w => new WellKnownDependencySummary(
            w.DepId, w.DepName, w.DepPublisher, w.DepVersionDefault)).ToList();
    }

    [McpServerTool(Name = "generate_workspace", ReadOnly = false, Idempotent = false)]
    [Description("Generates a new BC workspace as a ZIP. Pass the template key from list_templates, the workspace details, and the Core ID range. The result gives the ZIP's file name, size and SHA-256, and a downloadPath the user appends to the app's base URL to fetch it while signed in as themselves; the link works for 30 minutes and only for the person who generated it, and an app restart ends it early, so generate again if it has gone. The bytes are not returned inline. Set createRepository to also create a repository for it in your organisation's connected GitHub organisation and commit the generated files to it, or open a pull request holding them when that organisation only allows changes to the default branch through one; when the organisation has Solutions enabled, that repository is also registered on a solution, either the one solutionId names or a new one named after the customer. The repository also gets test and staging branches, and a solution it is the only repository on gets a build pipeline per branch.")]
    public async Task<WorkspaceResult> GenerateWorkspaceAsync(
        ProjectPlanInput plan,
        [Description("Optional. A repository name (no owner - it is created in the GitHub organisation your organisation has connected, and nowhere else). When set, the repository is created and the generated files are committed to it; createdRepository in the result carries its link. When the GitHub organisation only allows changes to the default branch through a pull request, the files are put in one instead and createdRepository.pullRequestUrl says where. You have to be a member of that GitHub organisation.")]
        string? createRepository = null,
        [Description("Whether a repository created by createRepository is private. Defaults to true.")]
        bool repositoryPrivate = true,
        [Description("Optional. The id of an existing solution (from list_solutions) to register the new repository on. Leave it out to create a new solution named after the customer. Only used with createRepository, and only when the organisation has Solutions enabled - passing one while it is switched off is refused.")]
        int? solutionId = null,
        CancellationToken ct = default)
    {
        try
        {
            var domainPlan = await plan.ToDomainAsync(_templates, ct);
            // What the prefix resolved to under the organisation's policy, so
            // the agent is told the names it is getting rather than the ones it
            // asked for. The generator applies the same rule internally.
            var prefix = await _generation.ResolveExtensionPrefixAsync(domainPlan, ct);
            if (!string.IsNullOrWhiteSpace(createRepository))
            {
                // Routed through the same service the New Workspace page uses,
                // so the organisation, the membership check and the credential
                // split are inherited rather than restated here - see "Keeping
                // MCP parity with the web UI" in PROJECT.md. The tool names a
                // repository, never an owner, so an agent cannot aim it at an
                // organisation the page would not offer.
                var created = await _repositories.CreateAsync(
                    domainPlan, createRepository!.Trim(), repositoryPrivate, solutionId, ct);
                return BuildCreatedResult(created) with { ExtensionPrefix = prefix };
            }

            var archive = await _generation.GenerateWorkspaceAsync(domainPlan, ct);
            try { return BuildResult(archive) with { ExtensionPrefix = prefix }; }
            finally { archive.Stream.Dispose(); }
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
        }
        catch (GitHubApiException ex)
        {
            throw new McpException("GitHub refused the request: " + ex.Message);
        }
        catch (GitHubAppNotConfiguredException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "generate_extension", ReadOnly = false, Idempotent = false)]
    [Description("Generates a single standalone BC extension as a ZIP. Pass the template key from list_templates, the extension details, its app ID range, publisher, and any optional dependencies. The result gives a downloadPath the user appends to the app's base URL to fetch the ZIP, as generate_workspace describes; the bytes are not returned inline. Set addToRepository to also add the extension to one of your organisation's GitHub repositories as a pull request.")]
    public async Task<WorkspaceResult> GenerateExtensionAsync(
        StandaloneExtensionPlanInput plan,
        [Description("Optional. A repository as 'owner/name'. When set, the extension is committed to a new branch there and a pull request is opened for it, in your name and never onto the repository's default branch; addedToRepository in the result carries the pull request. A repository holding a solution is joined as that solution, as add_extension_to_repository describes. Only repositories in the GitHub organisation your organisation has connected, and that you can open on GitHub yourself, are accepted.")]
        string? addToRepository = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(addToRepository))
            {
                // Routed through the same service the New Extension page uses,
                // so the access rule is inherited rather than restated here -
                // see "Keeping MCP parity with the web UI" in PROJECT.md. A
                // repository the picker would not offer is refused here too.
                var delivered = await _delivery.AddExtensionAsync(
                    plan.ToDomain(), sibling: null, addToRepository!.Trim(), ct);
                return BuildDeliveredResult(delivered);
            }

            var archive = await _generation.GenerateExtensionAsync(plan.ToDomain(), sibling: null, ct: ct);
            try { return BuildResult(archive); }
            finally { archive.Stream.Dispose(); }
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
        }
        catch (GitHubApiException ex)
        {
            throw new McpException("GitHub refused the request: " + ex.Message);
        }
        catch (GitHubAppNotConfiguredException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>
    /// The result for the add-to-repository path: the pull request, and the size and
    /// hash of the ZIP that went into it. No download link: the pull request holds the
    /// files, and the bytes are not repeated.
    /// </summary>
    private static WorkspaceResult BuildDeliveredResult(GitHubExtensionDelivery delivered) => new(
        FileName: delivered.ArchiveFileName,
        DownloadPath: null,
        DownloadExpiresAt: null,
        SizeBytes: delivered.Archive.Length,
        Sha256: Sha256Of(delivered.Archive),
        AddedToRepository: RepositoryDeliveryResult.From(delivered));

    /// <summary>
    /// The result for the create-a-repository path: the repository, and the size and
    /// hash of the ZIP whose files were committed to it. No download link, as above.
    /// </summary>
    private static WorkspaceResult BuildCreatedResult(GitHubWorkspaceRepository created) => new(
        FileName: created.ArchiveFileName,
        DownloadPath: null,
        DownloadExpiresAt: null,
        SizeBytes: created.Archive.Length,
        Sha256: Sha256Of(created.Archive),
        CreatedRepository: RepositoryCreationResult.From(created));

    /// <summary>
    /// Keeps the archive for the caller and returns the link to it. The size cap still
    /// applies: the store holds the bytes in memory until the link expires.
    /// </summary>
    private WorkspaceResult BuildResult(GeneratedArchive archive)
    {
        var bytes = archive.Stream.ToArray();
        if (bytes.Length > _options.MaxWorkspaceBytes)
        {
            throw new McpException(
                $"The generated ZIP is {bytes.Length} bytes, more than the {_options.MaxWorkspaceBytes} this server hands out. " +
                "Generate it from the web UI instead, or ask a SiteAdmin to raise the cap.");
        }
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new McpException("No organisation in scope; call this from an authenticated MCP session.");
        var userId = _orgContext.CurrentUserId
            ?? throw new McpException("No user in scope; call this from an authenticated MCP session.");
        var link = _downloads.Put(orgId, userId, archive.FileName, bytes)
            ?? throw new McpException(
                "The server is holding too many generated archives to keep this one. Try again in a few minutes, or have it written to a GitHub repository instead.");
        return new WorkspaceResult(
            FileName: archive.FileName,
            DownloadPath: link.DownloadPath,
            DownloadExpiresAt: link.ExpiresAt,
            SizeBytes: bytes.Length,
            Sha256: Sha256Of(bytes));
    }

    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string FormatErrors(IReadOnlyDictionary<string, string> errors) =>
        string.Join("; ", errors.Select(kv => $"{kv.Key}: {kv.Value}"));
}
