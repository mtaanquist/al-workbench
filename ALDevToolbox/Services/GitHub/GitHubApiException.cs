using System.Net;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// A call to GitHub came back with a failure status. Carries the status and
/// the <c>message</c> field GitHub's error bodies always include, so a page can
/// render the real cause ("a repository with that name already exists") rather
/// than a generic failure. Rate-limit headers are logged by the client, not
/// carried here. See <c>.design/github-integration.md</c>.
/// </summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException(HttpStatusCode statusCode, string message, string? documentationUrl = null)
        : base(message)
    {
        StatusCode = statusCode;
        DocumentationUrl = documentationUrl;
    }

    /// <summary>The HTTP status GitHub answered with.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>GitHub's <c>documentation_url</c>, when the error body carried one.</summary>
    public string? DocumentationUrl { get; }

    /// <summary>
    /// True when GitHub refused the write because a ruleset or a branch
    /// protection rule forbade it - "Repository rule violations found", which
    /// it answers with a 422.
    ///
    /// <para>Matched on the two words rather than on the whole sentence:
    /// GitHub appends the rules that fired ("Changes must be made through a
    /// pull request.") and that tail is not something to depend on. The
    /// distinction matters because a rule violation has a specific remedy the
    /// caller can name - on a new repository, the GitHub App missing from the
    /// ruleset's bypass list - where any other refusal does not.</para>
    /// </summary>
    public bool IsRuleViolation =>
        StatusCode == HttpStatusCode.UnprocessableEntity
        && Message.Contains("rule violation", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when GitHub refused the call because the credential is making too many -
    /// a 429, or a 403 whose message names a rate limit ("You have exceeded a secondary
    /// rate limit", "API rate limit exceeded"). Every further call on the same
    /// credential is refused too until it cools down, so a caller working through a
    /// list stops rather than spending the rest of it on refusals.
    /// </summary>
    public bool IsRateLimited =>
        StatusCode == HttpStatusCode.TooManyRequests
        || (StatusCode == HttpStatusCode.Forbidden
            && Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The deployment has no usable GitHub App registration — no app id, no private
/// key, or a key the Data Protection ring can no longer decrypt. Distinct from
/// <see cref="GitHubApiException"/> because nothing was asked of GitHub: the fix
/// is on <c>/site-admin/settings/github</c>, not on GitHub's side.
/// </summary>
public sealed class GitHubAppNotConfiguredException : Exception
{
    public GitHubAppNotConfiguredException()
        : base("GitHub is not set up on this server yet.")
    {
    }
}

/// <summary>
/// A write was refused because the file in the repository is no longer the
/// version the workbench read. Its own type rather than a
/// <see cref="GitHubApiException"/> because it is not a failure to report: it
/// is the answer that stops the Translator committing over somebody else's
/// work, and the page it reaches renders a way back rather than an error.
/// See <c>.design/github-integration.md</c>, issue #625.
/// </summary>
public sealed class GitHubContentConflictException : Exception
{
    public GitHubContentConflictException(string path, string message)
        : base(message)
    {
        Path = path;
    }

    /// <summary>The repository-relative path whose contents moved on.</summary>
    public string Path { get; }
}
