using ALDevToolbox.Services.Generation;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// Why the repository picker cannot offer a list yet - or that it can. Every
/// value except <see cref="Ready"/> is answered from the database alone, so
/// the picker still renders its guidance when GitHub itself is unreachable.
/// </summary>
public enum GitHubRepositoryReadiness
{
    /// <summary>Nobody has registered a GitHub App for this deployment. Only whoever runs the server can fix it.</summary>
    NotConfigured,

    /// <summary>The deployment has an App, but this organisation has not connected a GitHub organisation to it.</summary>
    NotConnected,

    /// <summary>The organisation is connected, but the acting user has not connected their own GitHub account.</summary>
    NotLinked,

    /// <summary>The user's link exists but its credentials no longer work, so they have to connect again.</summary>
    LinkNeedsRepair,

    /// <summary>Everything is in place; the list can be fetched.</summary>
    Ready,
}

/// <summary>
/// What the picker knows before it asks GitHub anything.
/// </summary>
/// <param name="Readiness">Which of the states above applies.</param>
/// <param name="OrgLogin">The connected GitHub organisation, when there is one - named in the copy so the user knows which one is meant.</param>
public sealed record GitHubRepositoryAccess(GitHubRepositoryReadiness Readiness, string? OrgLogin)
{
    /// <summary>True when a repository list can be fetched.</summary>
    public bool IsReady => Readiness == GitHubRepositoryReadiness.Ready;
}

/// <summary>
/// The repositories of the connected GitHub organisation, as one person may
/// see them - the read half every GitHub feature in the workbench shares.
///
/// <para><strong>This is the gate.</strong> Two credentials meet here, and the
/// split is the security decision from <c>.design/github-integration.md</c>:
/// the <em>installation</em> token answers "what did the organisation share
/// with the app", and the <em>acting user's own</em> token narrows that to what
/// they can see themselves. Nothing outside this service should combine them,
/// because <see cref="ResolveAsync"/> is what stops a caller - a page, an
/// endpoint, or an MCP tool - reaching a repository the picker would never
/// have offered.</para>
/// </summary>
public sealed class GitHubRepositoryService
{
    private readonly GitHubAppClient _github;
    private readonly GitHubAccessService _access;
    private readonly GitHubConnectionService _connection;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<GitHubRepositoryService> _logger;

    public GitHubRepositoryService(
        GitHubAppClient github,
        GitHubAccessService access,
        GitHubConnectionService connection,
        IOrganizationContext orgContext,
        ILogger<GitHubRepositoryService> logger)
    {
        _github = github;
        _access = access;
        _connection = connection;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; GitHubRepositoryService called outside an authenticated request.");

    /// <summary>
    /// Whether a repository list can be offered, and why not when it cannot.
    /// Costs two cached reads and never calls GitHub, so a page can render its
    /// guidance even while GitHub is down.
    /// </summary>
    public async Task<GitHubRepositoryAccess> GetAccessAsync(CancellationToken ct = default)
    {
        var connection = await _connection.GetStatusAsync(ct);
        if (!connection.DeploymentConfigured)
        {
            return new GitHubRepositoryAccess(GitHubRepositoryReadiness.NotConfigured, null);
        }
        if (!connection.IsConnected)
        {
            return new GitHubRepositoryAccess(GitHubRepositoryReadiness.NotConnected, null);
        }

        var link = await _access.GetLinkStatusAsync(ct);
        var readiness = link switch
        {
            { IsLinked: false } => GitHubRepositoryReadiness.NotLinked,
            { NeedsRelink: true } => GitHubRepositoryReadiness.LinkNeedsRepair,
            _ => GitHubRepositoryReadiness.Ready,
        };
        return new GitHubRepositoryAccess(readiness, connection.OrgLogin);
    }

    /// <summary>
    /// The connected organisation's repositories, narrowed to the ones the
    /// acting user can open on GitHub themselves and sorted by name so the
    /// typeahead's order is stable between renders.
    ///
    /// <para>Empty when the organisation is not connected or the user is not
    /// linked - the caller has already been told which, by
    /// <see cref="GetAccessAsync"/>.</para>
    /// </summary>
    /// <exception cref="GitHubApiException">GitHub refused to list them.</exception>
    /// <exception cref="GitHubAppNotConfiguredException">The deployment's App registration is gone or unreadable.</exception>
    public async Task<IReadOnlyList<GitHubRepositorySummary>> ListAccessibleAsync(CancellationToken ct = default) =>
        (await ListAccessibleWithTruncationAsync(ct)).Repositories;

    /// <summary>
    /// <see cref="ListAccessibleAsync"/>, plus whether GitHub's listing stopped before the
    /// end of the installation's repositories, so a search that finds nothing can say the
    /// repository may be past that point rather than not there (#1527).
    /// </summary>
    public async Task<GitHubInstallationRepositories> ListAccessibleWithTruncationAsync(CancellationToken ct = default)
    {
        var userId = RequireUserId();
        var access = await GetAccessAsync(ct);
        if (!access.IsReady) return new([], false);

        var installationId = (await _connection.GetStatusAsync(ct)).InstallationId;
        if (installationId is null) return new([], false);

        var token = await _github.GetInstallationTokenAsync(installationId.Value, ct);
        var listing = await _github.ListInstallationRepositoriesAsync(token, ct);
        var all = listing.Repositories;
        var visible = await _access.FilterAccessibleAsync(userId, all.Select(r => r.FullName), ct);
        var allowed = visible.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offered = all
            .Where(r => allowed.Contains(r.FullName))
            .OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "Offering {OfferedCount} of the installation's {RepositoryCount} repositories to user {UserId}.",
            offered.Count, all.Count, userId);
        return new GitHubInstallationRepositories(offered, listing.Truncated);
    }

    /// <summary>
    /// Turns a <c>owner/name</c> a caller supplied into a repository it is
    /// allowed to act on, or <see langword="null"/> when it is not one.
    ///
    /// <para>This is the resolver every caller routes an id through, so the
    /// access rule is written once (see the resolver rule in
    /// <c>PROJECT.md</c>): an MCP tool naming a repository directly gets
    /// exactly the same answer the picker would have given, and cannot reach
    /// something the web UI would refuse. Two things have to hold, and both are
    /// refusals rather than errors:</para>
    /// <list type="number">
    /// <item><description>The repository belongs to the GitHub organisation
    /// this workbench organisation connected - the picker offers nothing else,
    /// so neither does this.</description></item>
    /// <item><description>The acting user can open it on GitHub themselves,
    /// asked with their own token. An answer we could not get is a no.</description></item>
    /// </list>
    /// </summary>
    public async Task<GitHubRepositorySummary?> ResolveAsync(string repoFullName, CancellationToken ct = default)
    {
        var userId = RequireUserId();
        var parts = (repoFullName ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;

        var access = await GetAccessAsync(ct);
        if (!access.IsReady) return null;

        if (!string.Equals(parts[0], access.OrgLogin, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "User {UserId} asked for {RepoFullName}, which is outside the connected GitHub organisation {OrgLogin}.",
                userId, repoFullName, access.OrgLogin);
            return null;
        }

        if (!await _access.CanAccessRepoAsync(userId, repoFullName!, ct)) return null;

        var token = await _access.ResolveUserTokenAsync(userId, ct);
        if (token is null) return null;

        // Read as the user, not as the installation: the default branch this
        // returns is what the pull request will target, and it should be the
        // one they can see.
        return await _github.GetRepositoryAsync(token, parts[0], parts[1], ct);
    }

    /// <summary>
    /// The saved <c>workspace.aldt.toml</c> at a repository's root, or
    /// <see langword="null"/> when there is not one. A repository without it is
    /// an ordinary outcome - the form stays manual - not a failure.
    /// </summary>
    public async Task<GitHubFileContent?> TryReadWorkspaceConfigAsync(
        GitHubRepositorySummary repo, CancellationToken ct = default)
    {
        var token = await _access.ResolveUserTokenAsync(RequireUserId(), ct);
        if (token is null) return null;
        return await _github.GetFileAsync(
            token, repo.Owner, repo.Name, WorkspaceConfigService.FileName, repo.DefaultBranch, ct);
    }
}
