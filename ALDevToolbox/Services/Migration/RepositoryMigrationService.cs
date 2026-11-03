using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Services.Migration;

/// <summary>
/// Migrate Repositories (issue #1698): moving a customer's Azure DevOps
/// repositories into one GitHub repository laid out by a workspace template.
/// It lists a project's repositories (#1699) and previews what a migration of
/// the chosen ones into a template would move and change (#1700); nothing is
/// created yet.
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

    private readonly AzureDevOpsClient _azureDevOps;
    private readonly UserRepositoryTokenService _tokens;
    private readonly RepositoryMigrationQueue _queue;
    private readonly IOrganizationContext _org;
    private readonly TemplateService _templates;
    private readonly ApplicationVersionService _versions;
    private readonly GenerationService _generation;
    private readonly ILogger<RepositoryMigrationService> _logger;

    public RepositoryMigrationService(
        AzureDevOpsClient azureDevOps,
        UserRepositoryTokenService tokens,
        RepositoryMigrationQueue queue,
        IOrganizationContext org,
        TemplateService templates,
        ApplicationVersionService versions,
        GenerationService generation,
        ILogger<RepositoryMigrationService> logger)
    {
        _azureDevOps = azureDevOps;
        _tokens = tokens;
        _queue = queue;
        _org = org;
        _templates = templates;
        _versions = versions;
        _generation = generation;
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
        var token = await RequireTokenAsync(ct).ConfigureAwait(false);
        var repositories = await ListAsync(project, token, ct).ConfigureAwait(false);
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
        var migrationTarget = await BuildTargetAsync(target, ct).ConfigureAwait(false);

        var token = await RequireTokenAsync(ct).ConfigureAwait(false);
        var listed = await ListAsync(project, token, ct).ConfigureAwait(false);
        var repositories = listed.Where(r => chosen.Contains(r.Id)).ToList();
        if (repositories.Count != chosen.Count)
        {
            throw Refuse(RepositoriesField,
                "Some of the chosen repositories are no longer in this project. Show the repositories again and choose again.");
        }

        var identity = AmbientOrganizationScope.OrganizationIdentity.FromContext(_org, "starting a migration preview");
        if (identity.UserId is null)
        {
            throw new InvalidOperationException("A migration preview needs a signed-in person.");
        }

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
        var template = key.Length == 0 ? null : await _templates.GetByKeyAsync(key, ct).ConfigureAwait(false);
        if (template is null || template.DeletedAt is not null)
        {
            errors[TemplateField] = key.Length == 0 ? "Choose the template the new repository is laid out by." : "That template no longer exists. Choose another.";
        }
        if (customer.Length == 0)
        {
            errors[CustomerField] = "Enter the customer's name, for example CRONUS A/S.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var versions = await _versions.GetActiveAsync(ct: ct).ConfigureAwait(false);
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
                ? await _templates.GetDefaultModuleKeysAsync(template.Key, ct).ConfigureAwait(false)
                : [],
            TenantId: Guid.NewGuid().ToString());
        plan = plan with { ExtensionPrefix = await _generation.ResolveExtensionPrefixAsync(plan, ct).ConfigureAwait(false) };

        var problems = await _generation.ValidateWorkspaceAsync(plan, ct).ConfigureAwait(false);
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

        return new MigrationTarget(template.Key, template.Name, customer, input.AddTemplateExtensions, plan);
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
        var token = await _tokens.ResolveTokenAsync(RepositoryProvider.AzureDevOps, ct).ConfigureAwait(false);
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
            return await _azureDevOps.ListRepositoriesAsync(project, token, ct).ConfigureAwait(false);
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

/// <summary>What the Admin chose to move the repositories into, as the page sends it.</summary>
/// <param name="ShortName">Optional short form of the customer's name, as on New Workspace.</param>
/// <param name="AddTemplateExtensions">Whether the template's own extensions and modules come along too.</param>
public sealed record MigrationTargetInput(
    string? TemplateKey,
    string? CustomerName,
    string? ShortName = null,
    bool AddTemplateExtensions = false);
