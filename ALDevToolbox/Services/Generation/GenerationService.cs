using System.Diagnostics;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Services.Generation;

/// <summary>
/// Builds an in-memory ZIP archive for a workspace or standalone extension
/// under the unified-extensions model (Issue #54). The caller streams the
/// resulting bytes to the HTTP response — algorithm in
/// <c>.design/generation-engine.md</c> and the per-extension contract in
/// <c>.design/unified-extensions.md</c>.
/// </summary>
/// <remarks>
/// Orchestrator only since #86: validation + DB loads + extension list build
/// happen here, and the actual ZIP writes are delegated to
/// <see cref="WorkspaceZipBuilder"/>. Mustache substitution is delegated to
/// <see cref="MustacheRenderer"/>.
/// </remarks>
public class GenerationService
{
    // Spaces are allowed: the display name (app.json "name") keeps them, while the
    // folder name is derived from it by CustomerNaming. See issue #520.
    private static readonly Regex ExtensionNameRegex = new(@"^[A-Za-z][A-Za-z0-9 ]*$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly OrganizationConfigService _orgConfig;
    private readonly FolderTreeHydrator _folderTree;
    private readonly IOrganizationContext _orgContext;
    private readonly MustacheRenderer _mustache;
    private readonly WorkspaceZipBuilder _zipBuilder;
    private readonly ILogger<GenerationService> _logger;

    public GenerationService(
        AppDbContext db,
        OrganizationConfigService orgConfig,
        FolderTreeHydrator folderTree,
        IOrganizationContext orgContext,
        MustacheRenderer mustache,
        WorkspaceZipBuilder zipBuilder,
        ILogger<GenerationService> logger)
    {
        _db = db;
        _orgConfig = orgConfig;
        _folderTree = folderTree;
        _orgContext = orgContext;
        _mustache = mustache;
        _zipBuilder = zipBuilder;
        _logger = logger;
    }

    private Task<OrganizationConfig> GetOrgConfigAsync(CancellationToken ct) =>
        _orgConfig.GetForAsync(
            _orgContext.CurrentOrganizationId
                ?? throw new InvalidOperationException("Generation invoked without an organisation in scope."),
            ct);

    private static string ResolvePrefix(OrganizationConfig orgConfig, ProjectPlan plan) =>
        ExtensionPrefixPolicy.Resolve(
            orgConfig.Settings, plan.ExtensionPrefix, plan.ShortName, plan.WorkspaceName);

    /// <summary>
    /// The prefix <paramref name="plan"/> will actually be generated with, per
    /// the organisation's <see cref="ExtensionPrefixMode"/>. Public so the MCP
    /// tool can report it back to the agent that asked - the generator resolves
    /// it again internally, off the same cached settings row.
    /// </summary>
    public async Task<string> ResolveExtensionPrefixAsync(ProjectPlan plan, CancellationToken ct = default) =>
        ResolvePrefix(await GetOrgConfigAsync(ct), plan);

    /// <summary>
    /// The organisation's folder naming style - how a customer's name becomes
    /// the workspace folder and the <c>.code-workspace</c> file name. Exposed
    /// for the callers that show or rewrite those names without generating
    /// anything themselves.
    /// </summary>
    public async Task<NamingStyle> GetFolderStyleAsync(CancellationToken ct = default) =>
        (await GetOrgConfigAsync(ct)).Settings.NamingFolderStyle;

    // ===== Workspace flow =====

    /// <summary>
    /// Generates a workspace ZIP for the given plan. The walk concatenates the
    /// template's required extensions, any optional extensions the user
    /// ticked, and one cloned extension per selected catalogue module — all
    /// in their display order.
    /// </summary>
    public async Task<GeneratedArchive> GenerateWorkspaceAsync(ProjectPlan plan, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var (resolvedPlan, template, extensions, orgConfig) = await PrepareWorkspaceAsync(plan, ct);

        var (stream, fileCount) = await _zipBuilder.BuildWorkspaceAsync(resolvedPlan, template, extensions, orgConfig, ct);
        var folderName = CustomerNaming.Apply(resolvedPlan.WorkspaceName, orgConfig.Settings.NamingFolderStyle);
        stopwatch.Stop();

        _logger.LogInformation(
            "Generated workspace '{Workspace}' from template '{Template}' with [{Extensions}]: {Files} files, {Bytes} bytes, {Ms} ms.",
            plan.WorkspaceName,
            plan.TemplateKey,
            string.Join(",", extensions.Select(e => e.Path)),
            fileCount,
            stream.Length,
            stopwatch.ElapsedMilliseconds);

        return new GeneratedArchive(stream, $"{folderName}.zip");
    }

    // ===== Standalone extension flow =====

    /// <summary>
    /// Generates a single-extension ZIP for the New Extension flow. The
    /// emitted layout reuses the workspace template's first extension as the
    /// scaffold (typically Core); dependencies come from the form rather than
    /// the template's declarations.
    /// </summary>
    /// <param name="plan">The extension to build.</param>
    /// <param name="sibling">
    /// The workspace this extension is being added to, when there is one. Its
    /// presence switches on the regenerated <c>.code-workspace</c> file and
    /// switches off the workspace-root files (that workspace already has them).
    /// </param>
    /// <param name="includeWorkspaceRootFiles">
    /// Whether the workspace-root files the template opts into (a .gitignore, a
    /// README stub, the shared ruleset) ride along. True for a download, whose
    /// extension folder <em>is</em> the root of what the user unzips. False when
    /// the extension is being added into somewhere that already has a root of
    /// its own - see <see cref="GitHub.GitHubExtensionDeliveryService"/>.
    /// Ignored in sibling mode, which never carries them.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<GeneratedArchive> GenerateExtensionAsync(
        StandaloneExtensionPlan plan,
        SiblingWorkspaceContext? sibling = null,
        bool includeWorkspaceRootFiles = true,
        CancellationToken ct = default)
    {
        ValidateExtensionPlan(plan);
        plan = await ResolveLatestVersionAsync(plan, ct);

        var stopwatch = Stopwatch.StartNew();
        var template = await LoadTemplateAsync(plan.TemplateKey, ct);

        // Use the first (required) template extension as the scaffold for a
        // standalone build. Falls back to no template folders when the
        // template doesn't declare any extensions — the static fallback
        // folders below carry the structure.
        var scaffold = template.WorkspaceExtensions
            .OrderBy(e => e.Ordering)
            .FirstOrDefault(e => e.Required);
        var folderRoots = scaffold is null
            ? new List<FolderNode>()
            : BuildFolderTree(scaffold.Folders);

        // OrgConfig is needed both for the sibling-rewrite path (which
        // needs the org's workspace JSON template) and for the per-extension
        // org files the standalone extension might opt into via
        // RuntimeTemplateIncludedFile. Always load it now.
        var orgConfig = await GetOrgConfigAsync(ct);

        var (stream, fileCount, folderName) = await _zipBuilder.BuildStandaloneAsync(
            plan, template, folderRoots, sibling, orgConfig, includeWorkspaceRootFiles, ct);
        stopwatch.Stop();

        _logger.LogInformation(
            "Generated {Mode} extension '{Extension}' from template '{Template}' with {Deps} deps: {Files} files, {Bytes} bytes, {Ms} ms.",
            sibling is null ? "standalone" : "sibling",
            plan.ExtensionName,
            plan.TemplateKey,
            plan.Dependencies.Count,
            fileCount,
            stream.Length,
            stopwatch.ElapsedMilliseconds);

        return new GeneratedArchive(stream, $"{folderName}.zip");
    }

    /// <summary>
    /// Everything <see cref="GenerateWorkspaceAsync"/> does before it starts
    /// writing bytes: validate the plan's shape, load the template and the
    /// selected modules, resolve the publisher, build the extension list, and
    /// check no two id ranges overlap.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="ValidateWorkspaceAsync"/> on purpose. The New
    /// Workspace page validates inline before letting its native POST through,
    /// and if the two paths ran different rules the page would wave through a
    /// plan the endpoint then rejects — landing the user on the error page this
    /// was built to avoid. One code path is the only way to keep that honest.
    /// </remarks>
    private async Task<(ProjectPlan Plan, RuntimeTemplate Template, List<EmittableExtension> Extensions, OrganizationConfig OrgConfig)>
        PrepareWorkspaceAsync(ProjectPlan plan, CancellationToken ct)
    {
        ValidateWorkspacePlan(plan);
        var (application, runtime) = await ResolveLatestVersionAsync(plan.ApplicationVersion, plan.RuntimeVersion, ct);
        plan = plan with { ApplicationVersion = application, RuntimeVersion = runtime };

        var template = await LoadTemplateAsync(plan.TemplateKey, ct);
        ValidateCoreRangeAgainstTemplate(plan, template);
        var modules = await LoadSelectedModulesAsync(plan.SelectedModuleKeys, ct);
        var orgConfig = await GetOrgConfigAsync(ct);

        // The prefix is the organisation's policy, not the caller's, so it is
        // decided here rather than trusted from the form or the MCP input -
        // under Hidden and Fixed whatever arrived is ignored. See
        // .design/customer-naming.md.
        plan = plan with { ExtensionPrefix = ResolvePrefix(orgConfig, plan) };

        // {{publisher}} resolves to the org's configuration default, falling
        // back to the template default for a fresh org. Resolved once here and
        // threaded into every extension so the per-extension app.json and the
        // workspace-root files agree. See GenerationNaming.ResolvePublisher.
        var publisher = GenerationNaming.ResolvePublisher(
            orgConfig.Settings.DefaultPublisher, template.Defaults.Publisher);

        var extensions = BuildExtensionList(template, plan, modules, publisher, orgConfig.Settings.NamingFolderStyle);
        ValidateIdRanges(extensions);
        ValidateExtensionNames(extensions);

        return (plan, template, extensions, orgConfig);
    }

    /// <summary>
    /// Runs every rule <see cref="GenerateWorkspaceAsync"/> would and returns
    /// the field-keyed errors rather than throwing. Empty means the plan is
    /// good. Lets the New Workspace page show a problem next to the field that
    /// caused it instead of posting the form away to an error page (#546).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ValidateWorkspaceAsync(
        ProjectPlan plan, CancellationToken ct = default)
    {
        try
        {
            await PrepareWorkspaceAsync(plan, ct);
            return NoErrors;
        }
        catch (PlanValidationException ex)
        {
            return ex.Errors;
        }
    }

    /// <summary>
    /// The <see cref="GenerateExtensionAsync"/> counterpart of
    /// <see cref="ValidateWorkspaceAsync"/>. The standalone flow has no
    /// cross-extension id check, so its rules are the plan's own shape plus
    /// "the template still exists".
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ValidateExtensionAsync(
        StandaloneExtensionPlan plan, CancellationToken ct = default)
    {
        try
        {
            ValidateExtensionPlan(plan);
            await ResolveLatestVersionAsync(plan, ct);
            await LoadTemplateAsync(plan.TemplateKey, ct);
            return NoErrors;
        }
        catch (PlanValidationException ex)
        {
            return ex.Errors;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> NoErrors =
        new Dictionary<string, string>();

    private async Task<StandaloneExtensionPlan> ResolveLatestVersionAsync(
        StandaloneExtensionPlan plan, CancellationToken ct)
    {
        var (application, runtime) = await ResolveLatestVersionAsync(plan.ApplicationVersion, plan.RuntimeVersion, ct);
        return plan with { ApplicationVersion = application, RuntimeVersion = runtime };
    }

    /// <summary>
    /// Swaps the "Latest" choice for the newest application version in the
    /// catalogue. The web forms do this before they build a plan, but an MCP
    /// caller can pass the sentinel straight through, and without this it
    /// landed in app.json as <c>"application": "latest"</c>. Both values swap
    /// together, as the forms do, so app.json stays consistent.
    /// </summary>
    private async Task<(string Application, string Runtime)> ResolveLatestVersionAsync(
        string application, string runtime, CancellationToken ct)
    {
        if (!IsLatest(application) && !IsLatest(runtime)) return (application, runtime);

        var latest = await ApplicationVersionService.FindLatestAsync(_db, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ApplicationVersion"] = "\"Latest\" needs at least one application version to pick from, "
                    + "and none have been added yet. Choose a specific version, or ask an admin to add one.",
            });
        return (latest.Application, latest.Runtime);

        static bool IsLatest(string value) =>
            string.Equals(value?.Trim(), ApplicationVersionService.LatestSentinel, StringComparison.OrdinalIgnoreCase);
    }

    // ===== Loading =====

    private async Task<RuntimeTemplate> LoadTemplateAsync(string key, CancellationToken ct)
    {
        // EF doesn't support a recursive Include on the folder tree, so the
        // top-level Folders + Files come through here and any nested levels
        // are loaded as a flat list below and reassembled.
        var template = await _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.DeletedAt == null && t.Key == key)
            .Include(t => t.WorkspaceExtensions.OrderBy(e => e.Ordering))
                .ThenInclude(e => e.Dependencies.OrderBy(d => d.Ordering))
            .Include(t => t.IncludedFiles.OrderBy(j => j.Ordering))
            .Include(t => t.RootFolders.OrderBy(f => f.Ordering))
            .FirstOrDefaultAsync(ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["TemplateKey"] = $"Template '{key}' was not found.",
            });

        // Folder tree hydration is delegated to FolderTreeHydrator so workspace
        // generation, template authoring loads, and cross-org imports share
        // one implementation (#77).
        await _folderTree.HydrateExtensionFolderTreeAsync(new[] { template }, ct);
        return template;
    }

    private async Task<List<Module>> LoadSelectedModulesAsync(IReadOnlyList<string> moduleKeys, CancellationToken ct)
    {
        if (moduleKeys.Count == 0) return new();

        var modules = await _db.Modules
            .AsNoTracking()
            .Where(m => m.DeletedAt == null && moduleKeys.Contains(m.Key))
            .Include(m => m.Dependencies.OrderBy(d => d.Ordering))
            .ToListAsync(ct);

        await _folderTree.HydrateModuleExtensionFolderTreeAsync(modules, ct);

        // Preserve user-selected ordering — EF returns them in whatever
        // order the IN-clause matched.
        var byKey = modules.ToDictionary(m => m.Key);
        var ordered = new List<Module>(moduleKeys.Count);
        foreach (var key in moduleKeys)
        {
            if (byKey.TryGetValue(key, out var module)) ordered.Add(module);
        }
        return ordered;
    }

    // ===== Extension list building =====

    /// <summary>
    /// Walks the template's declared extensions in display order — required
    /// first, then the optional ones the user ticked — and appends one cloned
    /// extension per selected catalogue module. Each
    /// <see cref="EmittableExtension"/> carries a fresh GUID, its resolved
    /// id-range, the substituted display name, and the source folder tree.
    /// </summary>
    private List<EmittableExtension> BuildExtensionList(RuntimeTemplate template, ProjectPlan plan, IReadOnlyList<Module> modules, string publisher, NamingStyle folderStyle)
    {
        var selectedOptional = new HashSet<string>(plan.SelectedExtensionPaths, StringComparer.Ordinal);
        var list = new List<EmittableExtension>();

        // The ranges themselves come from IdRangeAllocator, which the New
        // Workspace preview also calls so the ID it shows is the ID it will
        // emit (#546). Allocation walks the same two lists in the same order as
        // the loops below, so index alignment is the contract between them -
        // asserted in IdRangeAllocatorTests rather than assumed here.
        var ranges = IdRangeAllocator.Allocate(
            template, plan.SelectedExtensionPaths, modules, plan.CoreIdRangeFrom, plan.CoreIdRangeTo);
        var next = 0;

        foreach (var ext in template.WorkspaceExtensions.OrderBy(e => e.Ordering))
        {
            if (!ext.Required && !selectedOptional.Contains(ext.Path)) continue;
            var range = ranges[next++];
            list.Add(BuildFromTemplate(ext, template, plan, range.From, range.To, publisher, folderStyle));
        }

        foreach (var module in modules)
        {
            var range = ranges[next++];
            list.Add(BuildFromModule(module, template, plan, range.From, range.To, publisher, folderStyle));
        }

        return list;
    }

    private EmittableExtension BuildFromTemplate(WorkspaceExtension ext, RuntimeTemplate template, ProjectPlan plan, int from, int to, string publisher, NamingStyle folderStyle)
    {
        var name = SubstituteScalar(ext.NameTemplate, plan, template, folderStyle);
        return new EmittableExtension(
            Path: ext.Path,
            Name: name,
            Id: Guid.NewGuid(),
            IdRangeFrom: from,
            IdRangeTo: to,
            Application: !string.IsNullOrEmpty(ext.Application) ? ext.Application : plan.ApplicationVersion,
            Runtime: !string.IsNullOrEmpty(ext.Runtime) ? ext.Runtime : plan.RuntimeVersion,
            Publisher: publisher,
            IsModuleClone: false,
            ModuleKey: null,
            ModuleName: name,
            FolderRoots: BuildFolderTree(ext.Folders),
            Dependencies: ext.Dependencies
                .OrderBy(d => d.Ordering)
                .Select(d => new EmittableDependency(d.RefExtensionPath, d.RefModuleKey, d.LitId, d.LitName, d.LitPublisher, d.LitVersion))
                .ToList());
    }

    private EmittableExtension BuildFromModule(Module module, RuntimeTemplate template, ProjectPlan plan, int from, int to, string publisher, NamingStyle folderStyle)
    {
        // The cloned extension's folder name and rendered AL name both come
        // from Module.ExtensionName (a PascalCase admin-controlled value).
        // Module.Key stays as the URL/admin slug and the dep ref target —
        // not the folder.
        var nameTemplate = $"{{{{extension_prefix}}}} {module.ExtensionName}";
        var name = SubstituteScalar(nameTemplate, plan, template, folderStyle);

        // Module dependencies (from module_dependencies) become literal deps.
        // Implicit dependencies on every required template-declared extension
        // get added in WorkspaceZipBuilder.ResolveDependencies at emit time
        // so they pick up the freshly-generated GUIDs from the rest of the
        // list (used when rendering {{dependencies_array}} in app.json).
        var deps = module.Dependencies
            .OrderBy(d => d.Ordering)
            .Select(d => new EmittableDependency(null, null, d.DepId, d.DepName, d.DepPublisher, d.DepVersion))
            .ToList();

        return new EmittableExtension(
            Path: module.ExtensionName,
            Name: name,
            Id: Guid.NewGuid(),
            IdRangeFrom: from,
            IdRangeTo: to,
            Application: plan.ApplicationVersion,
            Runtime: plan.RuntimeVersion,
            Publisher: publisher,
            IsModuleClone: true,
            ModuleKey: module.Key,
            ModuleName: module.ExtensionName,
            FolderRoots: BuildModuleFolderTree(module.ExtensionFolders),
            Dependencies: deps);
    }

    private static List<FolderNode> BuildFolderTree(IEnumerable<WorkspaceExtensionFolder> roots) =>
        roots.OrderBy(f => f.Ordering)
            .Select(f => new FolderNode(
                f.Path,
                f.Files.OrderBy(x => x.Ordering).Select(x => new FileLeaf(x.Path, x.Content, x.IsExample)).ToList(),
                BuildFolderTree(f.Folders)))
            .ToList();

    private static List<FolderNode> BuildModuleFolderTree(IEnumerable<ModuleExtensionFolder> roots) =>
        roots.OrderBy(f => f.Ordering)
            .Select(f => new FolderNode(
                f.Path,
                f.Files.OrderBy(x => x.Ordering).Select(x => new FileLeaf(x.Path, x.Content, x.IsExample)).ToList(),
                BuildModuleFolderTree(f.Folders)))
            .ToList();

    // ===== Validation =====

    private static void ValidateWorkspacePlan(ProjectPlan plan)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(plan.TemplateKey)) errors[nameof(plan.TemplateKey)] = "Required.";
        // The customer name is typed as it should appear, in any script: the
        // folder and file names are transliterated out of it by CustomerNaming
        // rather than restricting what may be typed. All it has to carry is
        // something to name with, and nothing a file name cannot hold.
        // See .design/customer-naming.md.
        if (string.IsNullOrWhiteSpace(plan.WorkspaceName)
            || plan.WorkspaceName.Length > CustomerNaming.MaxLength
            || plan.WorkspaceName.Any(char.IsControl)
            || !CustomerNaming.HasNameCharacters(plan.WorkspaceName))
            errors[nameof(plan.WorkspaceName)] = "Required. Give the customer's name, for example CRONUS A/S.";
        // The short name is only ever displayed, so the same "nothing a file
        // name cannot hold" rule applies - it just has a tighter ceiling,
        // because shortening long names is the whole point of it.
        if (plan.ShortName is { } shortName
            && (shortName.Length > CustomerNaming.MaxShortNameLength || shortName.Any(char.IsControl)))
            errors[nameof(plan.ShortName)] = "At most 50 characters.";
        if (plan.CoreIdRangeFrom <= 0) errors[nameof(plan.CoreIdRangeFrom)] = "Must be greater than zero.";
        if (plan.CoreIdRangeTo <= plan.CoreIdRangeFrom) errors[nameof(plan.CoreIdRangeTo)] = "Must be greater than 'from'.";
        if (string.IsNullOrWhiteSpace(plan.ApplicationVersion)) errors[nameof(plan.ApplicationVersion)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.RuntimeVersion)) errors[nameof(plan.RuntimeVersion)] = "Required.";
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }

    private static void ValidateExtensionPlan(StandaloneExtensionPlan plan)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(plan.TemplateKey)) errors[nameof(plan.TemplateKey)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.ExtensionName) || !ExtensionNameRegex.IsMatch(plan.ExtensionName))
            errors[nameof(plan.ExtensionName)] = "Required. Letters, digits and spaces only; must start with a letter.";
        // Not a form field: the publisher always comes from the org's defaults.
        // "Required." would send the user hunting for an input that isn't there.
        if (string.IsNullOrWhiteSpace(plan.Publisher))
            errors[nameof(plan.Publisher)] =
                "Your organisation has no publisher name set yet, and every extension needs one. "
                + "An admin can set it under Administration, on the Defaults page.";
        if (plan.IdRangeFrom <= 0) errors[nameof(plan.IdRangeFrom)] = "Must be greater than zero.";
        if (plan.IdRangeTo <= plan.IdRangeFrom) errors[nameof(plan.IdRangeTo)] = "Must be greater than 'from'.";
        if (string.IsNullOrWhiteSpace(plan.ApplicationVersion)) errors[nameof(plan.ApplicationVersion)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.RuntimeVersion)) errors[nameof(plan.RuntimeVersion)] = "Required.";

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < plan.Dependencies.Count; i++)
        {
            var dep = plan.Dependencies[i];
            if (string.IsNullOrWhiteSpace(dep.DepId)) continue;
            if (!seenIds.Add(dep.DepId.Trim()))
            {
                errors[$"Dependencies[{i}].DepId"] = $"Duplicate dependency id '{dep.DepId}'.";
            }
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }

    /// <summary>
    /// The one Core-range rule that needs the template: everything the
    /// workspace allocates after Core is shifted by however far the workspace
    /// moved the end of the Core range (#730), so a large enough downward move
    /// would put the first slot after Core at or below object id 0.
    ///
    /// <para>Keyed on <c>CoreIdRangeTo</c> because that is the field the shift
    /// keys on, and because the New Workspace page renders that key inline next
    /// to the range editor rather than in its page-level error list.</para>
    /// </summary>
    private static void ValidateCoreRangeAgainstTemplate(ProjectPlan plan, RuntimeTemplate template)
    {
        if (IdRangeAllocator.ModuleRangeStart(template, plan.CoreIdRangeTo) >= 1) return;

        throw new PlanValidationException(new Dictionary<string, string>
        {
            [nameof(plan.CoreIdRangeTo)] =
                "Raise the Core range: the extensions after it would start below ID 1.",
        });
    }

    /// <summary>
    /// Rejects the plan when a rendered extension name is longer than Business
    /// Central will accept. The names only exist once the templates have been
    /// substituted, which is why this runs here rather than in
    /// <see cref="ValidateWorkspacePlan"/> - running it inside
    /// <c>PrepareWorkspaceAsync</c> still means the page catches it before the
    /// form posts, not on the way out of the generator.
    ///
    /// <para>Keyed on <c>ShortName</c> because that is the field the user can
    /// do something about: the customer name is the customer's, the prefix is
    /// the organisation's.</para>
    /// </summary>
    private static void ValidateExtensionNames(IReadOnlyList<EmittableExtension> extensions)
    {
        if (extensions.All(e => e.Name.Length <= CustomerNaming.MaxExtensionNameLength)) return;

        throw new PlanValidationException(new Dictionary<string, string>
        {
            ["ShortName"] =
                "The extension name would be longer than 200 characters, which Business Central "
                + "refuses. Use a shorter short name.",
        });
    }

    /// <summary>
    /// Walks the final extension list and rejects the plan when any two
    /// resolved id ranges overlap. Per <c>unified-extensions.md</c> a
    /// workspace's allocated ranges have to be disjoint or AL refuses to
    /// compile a multi-extension app.
    /// </summary>
    private static void ValidateIdRanges(IReadOnlyList<EmittableExtension> extensions)
    {
        var errors = new Dictionary<string, string>();
        for (var i = 0; i < extensions.Count; i++)
        {
            var a = extensions[i];
            for (var j = i + 1; j < extensions.Count; j++)
            {
                var b = extensions[j];
                if (a.IdRangeFrom <= b.IdRangeTo && b.IdRangeFrom <= a.IdRangeTo)
                {
                    errors[$"Extensions[{j}].IdRange"] =
                        $"Extension '{b.Path}' id range {b.IdRangeFrom}..{b.IdRangeTo} overlaps '{a.Path}' {a.IdRangeFrom}..{a.IdRangeTo}.";
                }
            }
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }

    // ===== Mustache substitution =====

    /// <summary>
    /// Scalar substitution used for the extension name (no folder-context
    /// awareness — names are built before folder traversal). Builds an
    /// empty-FolderPath context and delegates to <see cref="MustacheRenderer.Render"/>.
    /// </summary>
    /// <remarks>
    /// The result is trimmed: the stock name templates read
    /// <c>"{{extension_prefix}} Core"</c>, and an organisation that sets no
    /// prefix would otherwise get an extension called " Core".
    /// </remarks>
    private string SubstituteScalar(string source, ProjectPlan plan, RuntimeTemplate template, NamingStyle folderStyle)
    {
        var ctx = new MustacheContext(
            Name: source,
            WorkspaceName: plan.WorkspaceName,
            ShortName: plan.EffectiveShortName,
            ModuleName: source,
            Publisher: template.Defaults.Publisher,
            ExtensionPrefix: plan.ExtensionPrefix,
            Affix: template.Defaults.AffixType == AffixType.None ? string.Empty : template.Defaults.Affix,
            FolderPath: string.Empty,
            TenantId: plan.TenantId,
            FolderStyle: folderStyle);
        return _mustache.Render(source, ctx).Trim();
    }
}

/// <summary>Container for a finished archive. The stream is rewound and ready to copy to the HTTP response body.</summary>
public record GeneratedArchive(MemoryStream Stream, string FileName);

/// <summary>Sibling-extension context for the New Extension flow.</summary>
public record SiblingWorkspaceContext(
    string WorkspaceName,
    IReadOnlyList<string> ModuleKeys,
    IReadOnlyList<string> ExistingFolders,
    /// <summary>
    /// The workspace's short name, read back from the settings it saved. Null
    /// for a workspace generated before short names existed, which falls back
    /// to the customer name.
    /// </summary>
    string? ShortName = null)
{
    /// <summary>The short name with its fallback applied - see <see cref="ProjectPlan.EffectiveShortName"/>.</summary>
    public string EffectiveShortName => CustomerNaming.ShortNameOrFallback(ShortName, WorkspaceName);
}
