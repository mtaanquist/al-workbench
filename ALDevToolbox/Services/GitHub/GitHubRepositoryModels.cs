namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// One repository, as much of it as any GitHub feature in the workbench needs.
///
/// <para>The same shape comes back from three different GitHub routes - the
/// installation's repository list, a single repository read, and a freshly
/// created one - so it is written once here rather than three times at the
/// call sites.</para>
/// </summary>
/// <param name="FullName"><c>owner/name</c>, the form every route and every
/// stored reference uses.</param>
/// <param name="Owner">The account the repository belongs to.</param>
/// <param name="Name">The repository name on its own.</param>
/// <param name="DefaultBranch">
/// The branch a pull request targets. Never written to directly - see
/// <see cref="GitHubExtensionDeliveryService"/>.
/// </param>
/// <param name="IsPrivate">Rendered as a hint in the picker, so a consultant
/// can tell two similarly named repositories apart.</param>
/// <param name="Description">GitHub's one-line description, when it has one.</param>
/// <param name="HtmlUrl">The repository's page, for links out.</param>
/// <param name="CloneUrl">The HTTPS clone URL, which issue #624 fills a pipeline's field from.</param>
public sealed record GitHubRepositorySummary(
    string FullName,
    string Owner,
    string Name,
    string DefaultBranch,
    bool IsPrivate,
    string? Description,
    string HtmlUrl,
    string CloneUrl);

/// <summary>What one person may do in one repository, as GitHub told them.</summary>
/// <param name="CanSee">They can read it.</param>
/// <param name="CanPush">They can write to it, which is what publishing a release needs.</param>
public sealed record GitHubUserRepositoryAccess(bool CanSee, bool CanPush);

/// <summary>
/// One file read through the Contents API: its decoded text and the blob
/// <c>sha</c> that a later write has to quote to prove it changed the version
/// it read (issue #625 leans on the sha; #623 only reads).
/// </summary>
public sealed record GitHubFileContent(string Path, string Text, string Sha);

/// <summary>One file to commit: its repository-relative path and its bytes.</summary>
public sealed record GitHubCommitFile(string Path, byte[] Content);

/// <summary>A pull request the workbench opened, as the success state needs it.</summary>
public sealed record GitHubPullRequest(int Number, string HtmlUrl, string HeadBranch);

/// <summary>Who a commit is credited to, when the workbench has to say (issue #622).</summary>
/// <param name="Name">The display name git records as the author.</param>
/// <param name="Email">
/// The author's email. GitHub links a commit to an account by this address,
/// which is why a person's <c>users.noreply.github.com</c> address is the one
/// worth sending: it always resolves, and it is not their private address.
/// </param>
public sealed record GitHubCommitAuthor(string Name, string Email);

/// <summary>What <c>POST /orgs/{org}/repos</c> did.</summary>
public enum GitHubRepositoryCreationOutcome
{
    /// <summary>The repository now exists.</summary>
    Created,

    /// <summary>The organisation already has a repository by that name.</summary>
    NameTaken,

    /// <summary>The installation was not granted permission to create repositories.</summary>
    NotPermitted,
}

/// <summary>
/// The outcome of asking GitHub for a new repository, with the repository
/// itself when there is one. The two refusals are carried rather than thrown
/// because a page can word both of them for the user, beside the field that
/// caused them.
/// </summary>
public sealed record GitHubRepositoryCreationResult(
    GitHubRepositoryCreationOutcome Outcome,
    GitHubRepositorySummary? Repository);
