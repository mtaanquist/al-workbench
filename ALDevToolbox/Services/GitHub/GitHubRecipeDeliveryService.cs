using System.Text;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Cookbook;

namespace ALDevToolbox.Services.GitHub;

/// <summary>What applying a recipe to a repository produced, for the caller to render.</summary>
/// <param name="Repository">The repository the pull request was opened on.</param>
/// <param name="PullRequest">The pull request itself - its number and the link the user needs next.</param>
/// <param name="IsNewPullRequest">
/// False when the commit joined a pull request that was already open, which is
/// the normal shape of applying the same recipe twice before the first one is
/// merged.
/// </param>
/// <param name="FileCount">How many of the recipe's files the commit carried.</param>
public sealed record GitHubRecipeDelivery(
    GitHubRepositorySummary Repository,
    GitHubPullRequest PullRequest,
    bool IsNewPullRequest,
    int FileCount);

/// <summary>
/// Puts a Cookbook recipe into a repository as a pull request (issue #626).
///
/// <para><strong>One operation, two callers.</strong> The download modal calls
/// this once for the repository a consultant picked; the admin page calls it
/// once per repository that has already taken the recipe, which is how a bug
/// found in a recipe becomes a fix pull request everywhere it landed. Both go
/// through this method, so the branch rule, the refusals and the attribution
/// are written once.</para>
///
/// <para><strong>The commit is the user's, and never the default
/// branch.</strong> As with "Add to repository" (#623) the write goes out on
/// the acting user's linked token, so GitHub enforces their own permissions and
/// the pull request is genuinely theirs; and it lands on
/// <c>aldt/recipe-&lt;slug&gt;</c> even when the default branch is unprotected,
/// because a recipe is a change somebody reviews. The branch is reused for as
/// long as its pull request is open, and the name is stepped once that pull
/// request is closed, so nothing under review is ever rewound.</para>
///
/// <para><strong>Files are written over, never merged.</strong> The tree is
/// layered onto the branch's own tree, so a repository whose copy of the recipe
/// has since diverged still gets the pull request and the diff shows what it
/// would change. Deciding that for the reviewer is not this feature's job.</para>
///
/// <para>Which repositories may be reached at all is
/// <see cref="GitHubRepositoryService.ResolveAsync"/>'s decision, so the page
/// and the <c>apply_recipe</c> tool inherit one gate. See
/// <c>.design/github-integration-phase2.md</c>.</para>
/// </summary>
public sealed class GitHubRecipeDeliveryService
{
    /// <summary>Branch names are <c>aldt/recipe-&lt;slug&gt;</c>, per the design doc.</summary>
    public const string BranchPrefix = "aldt/recipe-";

    /// <summary>
    /// How many branch names are tried before giving up. A handful is normal
    /// over the life of a recipe - one per round of fixes, each merged and left
    /// behind; ten means something else is going on and the person should hear
    /// about it rather than collect branches.
    /// </summary>
    private const int MaxBranchAttempts = 10;

    private readonly RecipeService _recipes;
    private readonly GitHubRepositoryService _repositories;
    private readonly GitHubAccessService _access;
    private readonly GitHubAppClient _github;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<GitHubRecipeDeliveryService> _logger;
    private readonly ALDevToolbox.Services.Tools.ToolEnablement _tools;

    public GitHubRecipeDeliveryService(
        RecipeService recipes,
        GitHubRepositoryService repositories,
        GitHubAccessService access,
        GitHubAppClient github,
        IOrganizationContext orgContext,
        ALDevToolbox.Services.Tools.ToolEnablement tools,
        ILogger<GitHubRecipeDeliveryService> logger)
    {
        _tools = tools;
        _recipes = recipes;
        _repositories = repositories;
        _access = access;
        _github = github;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; recipe delivery called outside an authenticated request.");

    /// <summary>
    /// Commits the recipe's files onto its own branch of
    /// <paramref name="repoFullName"/> and makes sure a pull request is open for
    /// them.
    ///
    /// <para>Refusals about the repository are field-keyed on
    /// <c>GitHubRepository</c> and refusals about the recipe on <c>Recipe</c>,
    /// so a page renders each beside the control that caused it and an MCP tool
    /// reports both as a validation failure.</para>
    /// </summary>
    /// <param name="recipeId">The recipe to apply.</param>
    /// <param name="repoFullName">The repository, as <c>owner/name</c>.</param>
    /// <param name="customerName">
    /// Optional customer the apply is recorded against, exactly as a download
    /// records one. A name that matches one of the organisation's solutions is
    /// stamped with its id.
    /// </param>
    /// <exception cref="PlanValidationException">The recipe or the repository cannot be used, with a reason the user can act on.</exception>
    /// <exception cref="GitHubApiException">GitHub refused one of the calls that make up the commit.</exception>
    public async Task<GitHubRecipeDelivery> ApplyAsync(
        int recipeId,
        string repoFullName,
        string? customerName = null,
        CancellationToken ct = default)
    {
        var userId = RequireUserId();
        // Applying opens a pull request in the person's name, from the recipe
        // page and from its admin page alike; the admin page is not under the
        // Cookbook route gate, so the rule is checked here.
        await _tools.EnsureStepUpAsync(Domain.Tools.ToolKey.Cookbook, ct);

        // The recipe first: one that does not exist, or that carries no files,
        // is not worth a round trip to GitHub, and neither refusal is about the
        // repository the caller picked.
        var recipe = await _recipes.GetAsync(recipeId, ct)
            ?? throw RefuseRecipe($"Recipe {recipeId} was not found. It may have been deleted since this page was opened.");
        if (recipe.Files.Count == 0)
        {
            throw RefuseRecipe(
                $"'{recipe.Title}' has no files in it yet, so there is nothing to put in a repository. "
                + "Add its files first.");
        }

        // Why-not before what: resolving would refuse all four of these
        // identically, and "that is not a repository we can offer you" is a poor
        // way to say "you have not connected your GitHub account".
        var access = await _repositories.GetAccessAsync(ct);
        if (!access.IsReady) throw Refuse(access.Readiness switch
        {
            GitHubRepositoryReadiness.NotConfigured =>
                "GitHub is not set up on this server yet, so a recipe cannot be put in a repository. "
                + "Ask whoever runs AL Workbench to set it up - meanwhile you can download it and commit it yourself.",
            GitHubRepositoryReadiness.NotConnected =>
                "Your organisation has not connected a GitHub organisation yet, so there is nowhere to put "
                + "this. An administrator connects one under Administration -> Repositories.",
            GitHubRepositoryReadiness.LinkNeedsRepair =>
                "Your GitHub account is no longer connected to the workbench. Connect it again on your "
                + "account page under Repository access, then try this again.",
            _ =>
                "Connect your own GitHub account first, on your account page under Repository access. "
                + "The pull request is opened in your name, so the workbench needs your GitHub account to do it.",
        });

        var repo = await _repositories.ResolveAsync(repoFullName, ct)
            ?? throw Refuse(
                "That repository is not one the workbench can offer you. Pick one from the list, "
                + "or ask an owner of your GitHub organisation to give you access to it.");

        var token = await _access.ResolveUserTokenAsync(userId, ct)
            ?? throw Refuse(
                "Connect your own GitHub account first, on your account page under Repository access. "
                + "The pull request is opened in your name, so the workbench needs your GitHub account to do it.");

        var target = await ChooseBranchAsync(token, repo, recipe.Title, ct);

        // The tree is layered onto whatever the branch (or the default branch)
        // already has, so a file the recipe carries replaces the repository's
        // copy of it and every other file is left alone.
        var baseTree = await _github.GetCommitTreeShaAsync(token, repo.Owner, repo.Name, target.ParentSha, ct);

        var blobs = new List<(string Path, string BlobSha)>(recipe.Files.Count);
        foreach (var file in recipe.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = RecipePaths.SafeEntryPath(file.RelativePath, file.FileName);
            var content = Encoding.UTF8.GetBytes(file.Content ?? string.Empty);
            blobs.Add((path, await _github.CreateBlobAsync(token, repo.Owner, repo.Name, content, ct)));
        }

        var tree = await _github.CreateTreeAsync(token, repo.Owner, repo.Name, baseTree, blobs, ct);
        var message = target.ExistingPullRequest is null
            ? $"Apply the {recipe.Title} recipe"
            : $"Update the {recipe.Title} recipe";
        var commit = await _github.CreateCommitAsync(
            token, repo.Owner, repo.Name, message, tree, target.ParentSha, ct: ct);

        if (target.ExistingPullRequest is null)
        {
            if (!await _github.CreateBranchAsync(token, repo.Owner, repo.Name, target.Branch, commit, ct))
            {
                // Somebody made that branch between the check and this call. It
                // is rare enough to say so rather than to loop again.
                throw Refuse(
                    $"Branch {target.Branch} appeared on '{repo.FullName}' while this was being prepared. "
                    + "Try again and the workbench will pick the next name.");
            }
        }
        else if (!await _github.UpdateBranchAsync(token, repo.Owner, repo.Name, target.Branch, commit, ct))
        {
            throw Refuse(
                $"Branch {target.Branch} on '{repo.FullName}' moved on while this was being prepared, so "
                + "nothing was committed. Try again to build on what is there now.");
        }

        var pullRequest = target.ExistingPullRequest ?? await _github.CreatePullRequestAsync(
            token, repo.Owner, repo.Name,
            title: $"Apply the {recipe.Title} recipe",
            head: target.Branch,
            baseBranch: repo.DefaultBranch,
            body: BuildBody(recipe.Title, recipe.Description, recipe.Files.Count),
            ct);

        // Recorded after the pull request exists: the history is meant to say
        // where the recipe went, and a row for a commit that never landed would
        // send the next fix to a repository that never took it.
        await _recipes.RecordRepositoryApplyAsync(recipeId, repo.FullName, customerName, userId, ct);

        _logger.LogInformation(
            "User {UserId} applied recipe {RecipeId} '{Title}' to {RepoFullName} on branch {Branch} as pull request "
            + "#{PullRequestNumber} ({PullRequestState}, {FileCount} files).",
            userId, recipeId, recipe.Title, repo.FullName, target.Branch, pullRequest.Number,
            target.ExistingPullRequest is null ? "opened" : "already open", recipe.Files.Count);

        return new GitHubRecipeDelivery(
            repo, pullRequest, IsNewPullRequest: target.ExistingPullRequest is null, recipe.Files.Count);
    }

    /// <summary>Where the commit is going: which branch, what it is committed on top of, and the pull request it joins if there is one.</summary>
    private sealed record BranchTarget(string Branch, string ParentSha, GitHubPullRequest? ExistingPullRequest);

    /// <summary>
    /// Picks the branch this apply belongs on, walking
    /// <c>aldt/recipe-&lt;slug&gt;</c>, <c>-2</c>, <c>-3</c> until one of two
    /// things is true: it has a pull request still open (join it, so a second
    /// apply lands in the review that is already running), or it does not exist
    /// at all (start it from the default branch). A branch whose pull request
    /// has been merged or closed is stepped past rather than reused - its
    /// history is somebody's merged work.
    /// </summary>
    private async Task<BranchTarget> ChooseBranchAsync(
        string token, GitHubRepositorySummary repo, string recipeTitle, CancellationToken ct)
    {
        var slug = RecipePaths.Slugify(recipeTitle);
        var baseName = BranchPrefix + (slug.Length == 0 ? "recipe" : slug);

        for (var attempt = 1; attempt <= MaxBranchAttempts; attempt++)
        {
            var branch = attempt == 1 ? baseName : $"{baseName}-{attempt}";

            var open = await _github.FindOpenPullRequestAsync(token, repo.Owner, repo.Name, branch, ct);
            if (open is not null)
            {
                var head = await _github.GetBranchHeadShaAsync(token, repo.Owner, repo.Name, branch, ct)
                    ?? throw Refuse(
                        $"Pull request #{open.Number} on '{repo.FullName}' says it comes from {branch}, but that "
                        + "branch is gone. Close the pull request on GitHub, then try again.");
                return new BranchTarget(branch, head, open);
            }

            if (await _github.GetBranchHeadShaAsync(token, repo.Owner, repo.Name, branch, ct) is null)
            {
                var defaultHead = await _github.GetBranchHeadShaAsync(
                    token, repo.Owner, repo.Name, repo.DefaultBranch, ct)
                    ?? throw Refuse(
                        $"'{repo.FullName}' has no commits on {repo.DefaultBranch} yet, so there is nothing to open "
                        + "a pull request against. Push something to it first, then come back.");
                return new BranchTarget(branch, defaultHead, null);
            }

            _logger.LogInformation(
                "Branch {Branch} on {RepoFullName} exists with no open pull request; trying the next name.",
                branch, repo.FullName);
        }

        throw Refuse(
            $"'{repo.FullName}' already has branches named {baseName} through {baseName}-{MaxBranchAttempts}, "
            + "and none of them has a pull request open. Tidy those up on GitHub, then try again.");
    }

    /// <summary>
    /// The pull request's description: what it brings, and where it came from.
    ///
    /// <para>Deliberately not worded as "adds" or "updates". A body is only ever
    /// written when the pull request is opened, and by the time a second commit
    /// joins it the words would already be there and could not be corrected;
    /// which of the two this commit is belongs on the commit message, where it
    /// stays true.</para>
    /// </summary>
    private static string BuildBody(string title, string description, int fileCount)
    {
        var lines = new List<string>
        {
            $"Puts the **{title}** recipe into this repository "
                + $"({fileCount} {(fileCount == 1 ? "file" : "files")}).",
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            lines.Add(description.Trim());
        }
        lines.Add("The recipe's files are written as they stand in the Cookbook, so a file this repository has "
            + "changed since is replaced rather than merged - the diff shows exactly what would change.");
        lines.Add("Sent from AL Workbench.");
        return string.Join("\n\n", lines);
    }

    private static PlanValidationException Refuse(string message) =>
        new(new Dictionary<string, string> { ["GitHubRepository"] = message });

    private static PlanValidationException RefuseRecipe(string message) =>
        new(new Dictionary<string, string> { ["Recipe"] = message });
}
