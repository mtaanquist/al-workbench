using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;

namespace ALDevToolbox.Services.Migration;

/// <summary>
/// Migrate Repositories (issue #1698): moving a customer's Azure DevOps
/// repositories into one GitHub repository laid out by a workspace template.
/// This part (#1699) lists a project's repositories and previews what a
/// migration of the chosen ones would move; nothing is created yet.
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

    private readonly AzureDevOpsClient _azureDevOps;
    private readonly UserRepositoryTokenService _tokens;
    private readonly RepositoryMigrationQueue _queue;
    private readonly IOrganizationContext _org;
    private readonly ILogger<RepositoryMigrationService> _logger;

    public RepositoryMigrationService(
        AzureDevOpsClient azureDevOps,
        UserRepositoryTokenService tokens,
        RepositoryMigrationQueue queue,
        IOrganizationContext org,
        ILogger<RepositoryMigrationService> logger)
    {
        _azureDevOps = azureDevOps;
        _tokens = tokens;
        _queue = queue;
        _org = org;
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
    /// <exception cref="PlanValidationException">The address, the choice, the token, or a busy server.</exception>
    public async Task<MigrationPreview> StartPreviewAsync(
        string? projectAddress, IReadOnlyCollection<string> repositoryIds, CancellationToken ct = default)
    {
        var project = ParseProject(projectAddress);
        var chosen = (repositoryIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (chosen.Count == 0)
        {
            throw Refuse(RepositoriesField, "Choose at least one repository to move.");
        }

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

        var job = new MigrationPreviewJob(Guid.NewGuid(), identity, project, repositories);
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
