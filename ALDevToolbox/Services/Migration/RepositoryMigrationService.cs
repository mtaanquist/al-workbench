using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.GitHub;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Services.Migration;

/// <summary>
/// Migrate Repositories (issue #1698): moving a customer's Azure DevOps
/// repositories into one GitHub repository laid out by a workspace template.
/// It lists a project's repositories (#1699) and previews what a migration of
/// the chosen ones into a template would move and change (#1700), then
/// creates the repository, the solution and its pipelines from a preview (#1701).
///
/// <para>Admin only: the page carries <c>[Authorize(Roles = "Admin")]</c>, and
/// every call acts with the Admin's own Azure DevOps token, so it can only read
/// what that person can read in Azure DevOps.</para>
/// </summary>
public sealed class RepositoryMigrationService
{
    /// <summary>Field key for errors about the project address.</summary>
    public const string ProjectField = "Project";

    /// <summary>Field key for errors about which repositories were chosen.</summary>
    public const string RepositoriesField = "Repositories";

    /// <summary>Field key for errors about the template.</summary>
    public const string TemplateField = "TemplateKey";

    /// <summary>Field key for errors about the customer's name.</summary>
    public const string CustomerField = "CustomerName";

    /// <summary>Field key for errors about the customer's short name.</summary>
    public const string ShortNameField = "ShortName";

    /// <summary>Field key for errors about the solution that already has the customer's name.</summary>
    public const string SolutionField = "ReplaceSolution";

    /// <summary>Field key for errors about the new repository's name.</summary>
    public const string RepositoryNameField = GitHubWorkspaceRepositoryService.NameField;

    /// <summary>Field key for errors about GitHub, and about starting the migration.</summary>
    public const string RepositoryField = GitHubWorkspaceRepositoryService.RepositoryField;

    private readonly AzureDevOpsClient _azureDevOps;
    private readonly UserRepositoryTokenService _tokens;
    private readonly RepositoryMigrationQueue _queue;
    private readonly IOrganizationContext _org;
    private readonly TemplateService _templates;
    private readonly ApplicationVersionService _versions;
    private readonly GenerationService _generation;
    private readonly RepositoryMigrationPublisher _publisher;
    private readonly AppDbContext _db;
    private readonly ILogger<RepositoryMigrationService> _logger;

    public RepositoryMigrationService(
        AzureDevOpsClient azureDevOps,
        UserRepositoryTokenService tokens,
        RepositoryMigrationQueue queue,
        IOrganizationContext org,
        TemplateService templates,
        ApplicationVersionService versions,
        GenerationService generation,
        RepositoryMigrationPublisher publisher,
        AppDbContext db,
        ILogger<RepositoryMigrationService> logger)
    {
        _azureDevOps = azureDevOps;
        _tokens = tokens;
        _queue = queue;
        _org = org;
        _templates = templates;
        _versions = versions;
        _generation = generation;
        _publisher = publisher;
        _db = db;
        _logger = logger;
    }

    /// <summary>True when the acting person has an Azure DevOps token stored.</summary>
    public Task<bool> HasAzureDevOpsTokenAsync(CancellationToken ct = default) =>
        _tokens.HasTokenAsync(RepositoryProvider.AzureDevOps, ct);

    /// <summary>The repositories in the Azure DevOps project at <paramref name="projectAddress"/>.</summary>
    /// <exception cref="PlanValidationException">
    /// The address does not name a project, the person has no token, or Azure DevOps
    /// refused; keyed to <see cref="ProjectField"/>.
    /// </exception>
    public async Task<SourceProjectListing> ListRepositoriesAsync(string? projectAddress, CancellationToken ct = default)
    {
        var project = ParseProject(projectAddress);
        var token = await RequireTokenAsync(ct);
        var repositories = await ListAsync(project, token, ct);
        return new SourceProjectListing(project, repositories);
    }

    /// <summary>
    /// Queues a preview of migrating the repositories with ids
    /// <paramref name="repositoryIds"/> from the project at <paramref name="projectAddress"/>.
    /// The repositories are listed from Azure DevOps again rather than taken from
    /// the page, so only a repository that really is in that project can be cloned.
    /// </summary>
    /// <exception cref="PlanValidationException">The address, the choice, the target, the token, or a busy server.</exception>
    public async Task<MigrationPreview> StartPreviewAsync(
        string? projectAddress, IReadOnlyCollection<string> repositoryIds, MigrationTargetInput target, CancellationToken ct = default)
    {
        var project = ParseProject(projectAddress);
        var chosen = (repositoryIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (chosen.Count == 0)
        {
            throw Refuse(RepositoriesField, "Choose at least one repository to move.");
        }
        var migrationTarget = await BuildTargetAsync(target, ct);

        var repositories = await ListChosenAsync(project, chosen, RepositoriesField,
            "Some of the chosen repositories are no longer in this project. Show the repositories again and choose again.", ct);
        var identity = RequireIdentity("starting a migration preview");

        var job = new MigrationPreviewJob(Guid.NewGuid(), identity, project, repositories, migrationTarget);
        var preview = _queue.TryStart(job, project.DisplayName, project.WebUrl, out var refusal);
        if (preview is null)
        {
            throw Refuse(RepositoriesField, refusal ?? "The preview could not be started. Try again in a few minutes.");
        }

        _logger.LogInformation(
            "Queued migration preview {PreviewId} of {RepositoryCount} repositories from {Project} for user {UserId}.",
            preview.Id, repositories.Count, project.DisplayName, identity.UserId);
        return preview;
    }

    /// <summary>The preview with id <paramref name="id"/>, when the acting person started it in this organisation.</summary>
    public MigrationPreview? GetPreview(Guid id) =>
        _org.CurrentOrganizationId is { } orgId && _org.CurrentUserId is { } userId
            ? _queue.Get(id, orgId, userId)
            : null;

    /// <summary>
    /// The solution that already has <paramref name="customerName"/>, and whether
    /// a migration joins it or may replace it. Null when the name is free. Step 3
    /// asks as the name is typed, so a clash shows before the preview runs (#1706);
    /// step 5 asks again for the preview's name.
    /// </summary>
    public Task<MigrationSolutionClash?> FindSolutionClashAsync(string? customerName, CancellationToken ct = default) =>
        _publisher.FindSolutionClashAsync(customerName ?? string.Empty, ct);

    /// <summary>Where the repository would go on GitHub, or what stops it being created.</summary>
    public Task<MigrationGitHubReadiness> CheckGitHubAsync(CancellationToken ct = default) =>
        _publisher.CheckGitHubAsync(ct);

    /// <summary>
    /// Queues the migration of what the finished preview <paramref name="previewId"/>
    /// read: the same repositories, template and customer. The run reads the
    /// repositories again and stops if <c>main</c> moved since the preview, so what
    /// is created is what the Admin looked at. Everything that would refuse the
    /// creation before it starts (the name, GitHub, a solution of the same name) is
    /// checked here, on the request.
    /// </summary>
    /// <exception cref="PlanValidationException">
    /// Keyed to <see cref="RepositoryNameField"/>, <see cref="RepositoryField"/>,
    /// <see cref="SolutionField"/> or the target's own fields.
    /// </exception>
    public async Task<MigrationPreview> StartMigrationAsync(Guid previewId, MigrationCreateInput input, CancellationToken ct = default)
    {
        var preview = GetPreview(previewId);
        if (preview is not { CanCreate: true })
        {
            throw Refuse(RepositoryField, "This preview can't be migrated any more. Preview again, then create the repository from the new preview.");
        }

        var project = ParseProject(preview.ProjectUrl);
        // What the preview laid out, template included: an edit to the template
        // since then is not picked up, so what is created is what was looked at (#1706).
        var target = preview.Target
            ?? throw Refuse(RepositoryField, "This preview can't be migrated any more. Preview again, then create the repository from the new preview.");

        var clash = await _publisher.FindSolutionClashAsync(target.CustomerName, ct);
        var request = new MigrationCreateRequest(
            (input.RepositoryName ?? string.Empty).Trim(),
            input.IsPrivate,
            input.AddPipelines,
            input.ReplaceExistingSolution && clash is { CanReplace: true } ? clash.Id : null,
            preview.Repositories
                .Where(r => r.Commit is not null)
                .ToDictionary(r => r.Id, r => r.Commit!, StringComparer.OrdinalIgnoreCase));
        await _publisher.CheckAsync(target.CustomerName, request, ct);

        var repositories = await ListChosenAsync(project, preview.Repositories.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
            RepositoryField, "Some of the previewed repositories are no longer in the Azure DevOps project. Preview again.", ct);
        var identity = RequireIdentity("starting a migration");

        var job = new MigrationPreviewJob(Guid.NewGuid(), identity, project, repositories, target, request);
        // One preview creates at most one repository, even from two tabs.
        if (!_queue.TryClaim(preview.Id, job.Id))
        {
            throw Refuse(RepositoryField, "This preview has already been used to start a migration. Preview again to start another.");
        }
        var run = _queue.TryStart(job, project.DisplayName, project.WebUrl, out var refusal);
        if (run is null)
        {
            _queue.Release(preview.Id, job.Id);
            throw Refuse(RepositoryField, refusal ?? "The migration could not be started. Try again in a few minutes.");
        }

        _logger.LogInformation(
            "Queued migration {RunId} of {RepositoryCount} repositories from {Project} into {RepositoryName} for user {UserId}.",
            run.Id, repositories.Count, project.DisplayName, request.RepositoryName, identity.UserId);
        return run;
    }

    /// <summary>How many earlier migrations the page lists at first, and adds on each Show more.</summary>
    public const int HistoryPageSize = 25;

    /// <summary>
    /// The organisation's migrations, newest first: the first <paramref name="take"/>
    /// of those whose customer, Azure DevOps project or GitHub repository contains
    /// <paramref name="search"/> (all of them when it is blank), and how many match in all.
    /// </summary>
    public async Task<RepositoryMigrationHistoryPage> ListHistoryAsync(
        string? search = null, int take = HistoryPageSize, CancellationToken ct = default)
    {
        var query = _db.RepositoryMigrations.AsNoTracking();
        var term = (search ?? string.Empty).Trim();
        if (term.Length > 0)
        {
            var pattern = $"%{ObjectExplorer.Explore.ObjectSearchService.EscapeLike(term)}%";
            query = query.Where(m => EF.Functions.ILike(m.CustomerName, pattern, "\\")
                || EF.Functions.ILike(m.SourceProject, pattern, "\\")
                || (m.RepositoryFullName != null && EF.Functions.ILike(m.RepositoryFullName, pattern, "\\")));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(Math.Max(take, 1))
            .Select(m => new
            {
                m.Id, m.CreatedAt, m.CustomerName, m.ShortName, m.SourceProject, m.SourceProjectUrl, m.SourceRepositoryIds,
                m.TemplateKey, m.AddTemplateExtensions, m.RepositoryFullName, m.RepositoryUrl,
                SolutionId = m.Project != null && m.Project.DeletedAt == null ? m.ProjectId : null,
                SolutionSlug = m.Project != null && m.Project.DeletedAt == null ? m.Project.Slug : null,
                StartedBy = m.CreatedByUser != null ? (m.CreatedByUser.DisplayName != "" ? m.CreatedByUser.DisplayName : m.CreatedByUser.Email) : null,
                m.Succeeded, m.Message,
            })
            .ToListAsync(ct);

        return new RepositoryMigrationHistoryPage(
            rows.Select(m => new RepositoryMigrationHistoryRow(
                m.Id, m.CreatedAt, m.CustomerName, m.SourceProject, m.SourceProjectUrl,
                m.RepositoryFullName, m.RepositoryUrl, m.SolutionId, m.SolutionSlug, m.StartedBy, m.Succeeded, m.Message,
                m.SourceRepositoryIds.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                m.ShortName, m.TemplateKey, m.AddTemplateExtensions))
                .ToList(),
            total);
    }

    /// <summary>
    /// The workspace plan a migration's template is generated from: the
    /// template's own defaults, the customer's name, no examples, and the
    /// template's modules only when its extensions are wanted. Validated the way
    /// New Workspace validates, so a bad template or name is refused here rather
    /// than after the repositories were cloned.
    /// </summary>
    private async Task<MigrationTarget> BuildTargetAsync(MigrationTargetInput input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var key = (input.TemplateKey ?? string.Empty).Trim();
        var customer = (input.CustomerName ?? string.Empty).Trim();
        var shortName = (input.ShortName ?? string.Empty).Trim();
        var template = key.Length == 0 ? null : await _templates.GetByKeyAsync(key, ct);
        if (template is null || template.DeletedAt is not null)
        {
            errors[TemplateField] = key.Length == 0 ? "Choose the template the new repository is laid out by." : "That template no longer exists. Choose another.";
        }
        if (customer.Length == 0)
        {
            errors[CustomerField] = "Enter the customer's name, for example CRONUS A/S.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var versions = await _versions.GetActiveAsync(ct: ct);
        var (application, runtime) = TemplateDefaultsResolver.ResolveApplicationAndRuntime(template!, versions);
        var plan = new ProjectPlan(
            TemplateKey: template!.Key,
            WorkspaceName: customer,
            ShortName: shortName,
            ExtensionPrefix: string.Empty,
            Brief: string.Empty,
            Description: string.Empty,
            ApplicationVersion: application,
            RuntimeVersion: runtime,
            CoreIdRangeFrom: template.CoreIdRangeFrom,
            CoreIdRangeTo: template.CoreIdRangeTo,
            IncludeExamples: false,
            SelectedExtensionPaths: [],
            SelectedModuleKeys: input.AddTemplateExtensions
                ? await _templates.GetDefaultModuleKeysAsync(template.Key, ct)
                : [],
            // The customer's own tenant when their solution knows it, so the
            // launch configurations point at their environments; blank otherwise,
            // as New Workspace leaves it when nobody typed one.
            TenantId: await TenantIdAsync(customer, ct) ?? string.Empty);
        plan = plan with { ExtensionPrefix = await _generation.ResolveExtensionPrefixAsync(plan, ct) };

        var problems = await _generation.ValidateWorkspaceAsync(plan, ct);
        if (problems.Count > 0)
        {
            var mapped = new Dictionary<string, string>();
            foreach (var (field, message) in problems)
            {
                var target = field switch
                {
                    "WorkspaceName" => CustomerField,
                    "ShortName" => ShortNameField,
                    _ => TemplateField,
                };
                mapped[target] = mapped.TryGetValue(target, out var before) ? before + " " + message : message;
            }
            throw new PlanValidationException(mapped);
        }

        return new MigrationTarget(template.Key, template.Name, customer, input.AddTemplateExtensions, plan,
            await ProductionVersionAsync(customer, ct));
    }

    /// <summary>
    /// The customer's Entra tenant, when a solution of their name knows it: the
    /// one its Business Central connection is set up with, else the one its
    /// environments were read from.
    /// </summary>
    private async Task<string?> TenantIdAsync(string customer, CancellationToken ct)
    {
        var lower = customer.ToLowerInvariant();
        var solution = await _db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Name.ToLower() == lower)
            .Select(p => new
            {
                p.BcTenantId,
                FromEnvironments = p.Environments
                    .Where(e => e.AadTenantId != null && e.MissingSince == null && e.SoftDeletedOn == null)
                    .Select(e => e.AadTenantId)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        return (solution?.BcTenantId ?? solution?.FromEnvironments)?.ToString();
    }

    /// <summary>
    /// The Business Central version the customer runs in production, when a
    /// solution of their name already exists and has seen its environments: the
    /// lowest, if it has several, since every app has to install on all of them.
    /// Null when there is no such solution or no production environment.
    /// </summary>
    private async Task<string?> ProductionVersionAsync(string customer, CancellationToken ct)
    {
        var lower = customer.ToLowerInvariant();
        var versions = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Project!.DeletedAt == null && e.Project.Name.ToLower() == lower
                && e.Type.ToLower() == "production" && e.MissingSince == null && e.SoftDeletedOn == null && e.Version != null)
            .Select(e => e.Version!)
            .ToListAsync(ct);
        return versions
            .Select(v => (Text: v, Parsed: Version.TryParse(v, out var parsed) ? parsed : null))
            .Where(v => v.Parsed is not null)
            .OrderBy(v => v.Parsed)
            .Select(v => v.Text)
            .FirstOrDefault();
    }

    /// <summary>
    /// The repositories with ids <paramref name="ids"/>, listed from Azure DevOps
    /// again rather than taken from the page, so only a repository that really is
    /// in the project is ever cloned.
    /// </summary>
    private async Task<List<AzureDevOpsRepository>> ListChosenAsync(
        AzureDevOpsProject project, IReadOnlySet<string> ids, string field, string missing, CancellationToken ct)
    {
        var token = await RequireTokenAsync(ct);
        var listed = await ListAsync(project, token, ct);
        var repositories = listed.Where(r => ids.Contains(r.Id)).ToList();
        if (repositories.Count != ids.Count) throw Refuse(field, missing);
        return repositories;
    }

    private AmbientOrganizationScope.OrganizationIdentity RequireIdentity(string what)
    {
        var identity = AmbientOrganizationScope.OrganizationIdentity.FromContext(_org, what);
        return identity.UserId is null
            ? throw new InvalidOperationException($"{what} needs a signed-in person.")
            : identity;
    }

    private static AzureDevOpsProject ParseProject(string? projectAddress)
    {
        if (string.IsNullOrWhiteSpace(projectAddress))
        {
            throw Refuse(ProjectField, "Enter the address of the Azure DevOps project.");
        }
        if (!AzureDevOpsProject.TryParse(projectAddress, out var project))
        {
            throw Refuse(ProjectField,
                "That isn't the address of an Azure DevOps project. Copy it from your browser while the project is open, "
                + "for example https://dev.azure.com/cronus/Sales.");
        }
        return project!;
    }

    private async Task<string> RequireTokenAsync(CancellationToken ct)
    {
        var token = await _tokens.ResolveTokenAsync(RepositoryProvider.AzureDevOps, ct);
        if (string.IsNullOrEmpty(token))
        {
            throw Refuse(ProjectField,
                "You don't have an Azure DevOps token yet. Add one with the Code (Read) scope under Account → Repository access.");
        }
        return token;
    }

    private async Task<IReadOnlyList<AzureDevOpsRepository>> ListAsync(AzureDevOpsProject project, string token, CancellationToken ct)
    {
        try
        {
            return await _azureDevOps.ListRepositoriesAsync(project, token, ct);
        }
        catch (AzureDevOpsApiException ex)
        {
            throw Refuse(ProjectField, ex.Message);
        }
    }

    private static PlanValidationException Refuse(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}

/// <summary>An Azure DevOps project and the repositories in it.</summary>
public sealed record SourceProjectListing(AzureDevOpsProject Project, IReadOnlyList<AzureDevOpsRepository> Repositories);

/// <summary>How the Admin wants the repository created, as the page sends it.</summary>
/// <param name="ReplaceExistingSolution">
/// Delete the solution that already has the customer's name first. Honoured only
/// when that solution came from an earlier migration whose repository is gone.
/// </param>
public sealed record MigrationCreateInput(
    string? RepositoryName,
    bool IsPrivate = true,
    bool AddPipelines = true,
    bool ReplaceExistingSolution = false);

/// <summary>One earlier migration, as the page lists it.</summary>
/// <param name="SolutionId">The solution it created, while that solution still exists.</param>
/// <param name="SourceRepositoryIds">
/// Azure DevOps's ids for the repositories it moved, so Migrate again can tick the
/// same ones. Empty on runs from before they were kept.
/// </param>
public sealed record RepositoryMigrationHistoryRow(
    int Id,
    DateTime CreatedAt,
    string CustomerName,
    string SourceProject,
    string SourceProjectUrl,
    string? RepositoryFullName,
    string? RepositoryUrl,
    int? SolutionId,
    string? SolutionSlug,
    string? StartedBy,
    bool Succeeded,
    string? Message,
    IReadOnlyList<string> SourceRepositoryIds,
    string? ShortName,
    string TemplateKey,
    bool AddTemplateExtensions);

/// <summary>A page of earlier migrations, and how many match in all.</summary>
public sealed record RepositoryMigrationHistoryPage(IReadOnlyList<RepositoryMigrationHistoryRow> Rows, int Total);

/// <summary>What the Admin chose to move the repositories into, as the page sends it.</summary>
/// <param name="ShortName">Optional short form of the customer's name, as on New Workspace.</param>
/// <param name="AddTemplateExtensions">Whether the template's own extensions and modules come along too.</param>
public sealed record MigrationTargetInput(
    string? TemplateKey,
    string? CustomerName,
    string? ShortName = null,
    bool AddTemplateExtensions = false);
