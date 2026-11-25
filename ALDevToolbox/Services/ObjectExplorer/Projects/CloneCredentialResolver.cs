using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.GitHub;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// One credential a manual build or a discovery may clone with, and where it
/// came from - so a log line can say which one worked without saying what it was.
/// </summary>
/// <param name="Secret">The token, carried into git's <c>http.extraHeader</c> and nowhere else.</param>
/// <param name="Source">Words for the log: "the connected GitHub account" or "the stored build token".</param>
public sealed record CloneCredential(string Secret, string Source);

/// <summary>
/// The credentials the acting user can clone a solution repository with, in
/// the order they should be tried.
///
/// <para><strong>The connected GitHub account comes first.</strong> Connecting
/// it on the Account page leaves the workbench holding a user-to-server token
/// from the GitHub App, refreshed on its own, that acts as the person with the
/// person's own access - the same "clone what I can see" semantics a personal
/// access token has, with nothing to create or renew. So a GitHub clone tries
/// it first, and the stored build token is what remains for the repositories
/// that token cannot reach: the App is installed on the organisation the
/// workbench connected, and a customer's own organisation may not have it.
/// Which is why the caller tries each in turn rather than picking one here -
/// only the clone knows whether a token reaches the repository.</para>
///
/// <para>Azure DevOps has no App, so it is the stored token or nothing. A
/// pull-request build never comes here: it has no user, and clones as the
/// installation (see <see cref="ProjectBuildOptions.InstallationToken"/>).</para>
///
/// <para>Lifted out of <see cref="ProjectBuildService"/> because both the build
/// clone and the discovery clone ask the same question, and because the answer
/// is worth a test on its own without standing up a compiler. See
/// <c>.design/github-integration.md</c>, "#624 Solution repositories".</para>
/// </summary>
public sealed class CloneCredentialResolver
{
    /// <summary>What the log calls the App token, and what a build log says was used.</summary>
    public const string ConnectedAccountSource = "the connected GitHub account";

    /// <summary>What the log calls the personal access token.</summary>
    public const string BuildTokenSource = "the stored build token";

    private readonly UserRepositoryTokenService _repoTokens;
    private readonly GitHubAccessService _gitHubAccess;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<CloneCredentialResolver> _logger;

    public CloneCredentialResolver(
        UserRepositoryTokenService repoTokens,
        GitHubAccessService gitHubAccess,
        IOrganizationContext orgContext,
        ILogger<CloneCredentialResolver> logger)
    {
        _repoTokens = repoTokens;
        _gitHubAccess = gitHubAccess;
        _orgContext = orgContext;
        _logger = logger;
    }

    /// <summary>
    /// The credentials to try for <paramref name="provider"/>, best first, and
    /// empty when the acting user has nothing that could reach it. Never
    /// throws: a connected account that cannot be read right now (GitHub down
    /// during a refresh, a key ring that no longer decrypts it) is a warning
    /// and a fall-through to the build token, because the token that used to
    /// be the only route must keep working when the newer one is unwell.
    /// </summary>
    public async Task<IReadOnlyList<CloneCredential>> ResolveAsync(
        RepositoryProvider provider, CancellationToken ct = default)
    {
        var credentials = new List<CloneCredential>(2);

        if (provider == RepositoryProvider.GitHub && _orgContext.CurrentUserId is { } userId)
        {
            try
            {
                var linked = await _gitHubAccess.ResolveUserTokenAsync(userId, ct);
                if (!string.IsNullOrEmpty(linked))
                {
                    credentials.Add(new CloneCredential(linked, ConnectedAccountSource));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Could not use the connected GitHub account of user {UserId} for a clone; falling back to the build token.",
                    userId);
            }
        }

        var pat = await _repoTokens.ResolveTokenAsync(provider, ct);
        if (!string.IsNullOrEmpty(pat))
        {
            credentials.Add(new CloneCredential(pat, BuildTokenSource));
        }

        return credentials;
    }

    /// <summary>
    /// What to tell the person when <see cref="ResolveAsync"/> came back empty:
    /// what they can set up, and where. GitHub has two answers because it has
    /// two routes; Azure DevOps has one.
    /// </summary>
    public static string NothingToCloneWith(RepositoryProvider provider) => provider == RepositoryProvider.GitHub
        ? "Your GitHub account is not connected and you have no GitHub build token. Connect your GitHub account, or add a token, under Account → Repository access."
        : $"You don't have a build token for {provider.DisplayName()}. Add one under Account → Repository access.";
}
