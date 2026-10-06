using System.IO.Compression;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;

namespace ALDevToolbox.Services.GitHub;

/// <summary>What "Add to repository" produced, for the success state to render.</summary>
/// <param name="Repository">The repository the pull request was opened on.</param>
/// <param name="PullRequest">The pull request itself - its number and the link the user needs next.</param>
/// <param name="FolderName">The extension folder the commit added.</param>
/// <param name="FileCount">How many files the commit carried.</param>
/// <param name="ArchiveFileName">The name the same extension would download under.</param>
/// <param name="Archive">
/// The very bytes that were committed, as the ZIP. Carried out rather than
/// thrown away because the MCP tool hands the caller both the pull request and
/// the files, and generating a second time would produce different extension
/// GUIDs - a download that quietly disagreed with the pull request beside it.
/// The web page ignores it.
/// </param>
public sealed record GitHubExtensionDelivery(
    GitHubRepositorySummary Repository,
    GitHubPullRequest PullRequest,
    string FolderName,
    int FileCount,
    string ArchiveFileName,
    byte[] Archive);

/// <summary>
/// Adds a freshly generated extension to an existing repository as a pull
/// request (issue #623).
///
/// <para><strong>Generation is unchanged.</strong> The files are the ones the
/// ZIP is built from - the same in-memory archive, read back entry by entry -
/// so the download and the pull request can never drift apart. Nothing is
/// queued: this runs on the request thread inside the button's own loading
/// state, like every other GitHub call in this milestone.</para>
///
/// <para><strong>The commit is the user's, and never touches the default
/// branch.</strong> The write goes out on the acting user's linked token, so
/// GitHub enforces their own permissions natively and the pull request is
/// genuinely theirs rather than a bot's; and it lands on a branch of its own
/// even when the default branch is unprotected, because "add something to an
/// existing repository" is a proposal, not a fait accompli. Which repositories
/// may be reached at all is <see cref="GitHubRepositoryService.ResolveAsync"/>'s
/// decision, so every caller - page or MCP tool - inherits one gate.</para>
///
/// <para>See <c>.design/github-integration.md</c>.</para>
/// </summary>
public sealed class GitHubExtensionDeliveryService
{
    /// <summary>Branch names are <c>aldt/add-&lt;folder&gt;</c>, per the design doc.</summary>
    private const string BranchPrefix = "aldt/add-";

    /// <summary>
    /// How many times a taken branch name is stepped before giving up. A second
    /// attempt at the same extension is normal (the first pull request is still
    /// open); ten of them means something else is going on and the user should
    /// hear about it rather than collect branches.
    /// </summary>
    private const int MaxBranchAttempts = 10;

    /// <summary>The file that marks a folder as an existing AL extension.</summary>
    private const string ExtensionMarkerFile = "app.json";

    private readonly GenerationService _generation;
    private readonly WorkspaceConfigService _configs;
    private readonly GitHubRepositoryService _repositories;
    private readonly GitHubAccessService _access;
    private readonly GitHubAppClient _github;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<GitHubExtensionDeliveryService> _logger;

    public GitHubExtensionDeliveryService(
        GenerationService generation,
        WorkspaceConfigService configs,
        GitHubRepositoryService repositories,
        GitHubAccessService access,
        GitHubAppClient github,
        IOrganizationContext orgContext,
        ILogger<GitHubExtensionDeliveryService> logger)
    {
        _generation = generation;
        _configs = configs;
        _repositories = repositories;
        _access = access;
        _github = github;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; extension delivery called outside an authenticated request.");

    /// <summary>
    /// Generates <paramref name="plan"/> and opens a pull request adding it to
    /// <paramref name="repoFullName"/>.
    ///
    /// <para>A repository holding a solution the workbench generated is joined
    /// as that solution, whoever calls: its saved settings are read here and
    /// win over <paramref name="sibling"/>, so the page and the MCP tools name
    /// the extension with the solution's prefix, leave the example files out
    /// and update the solution's files the same way. <paramref name="sibling"/>
    /// only counts for a repository with no saved settings.</para>
    ///
    /// <para>Every refusal is a field-keyed <see cref="PlanValidationException"/>
    /// on <c>GitHubRepository</c>, so a page renders it beside the picker and an
    /// MCP tool reports it as a validation failure - one message, both callers.
    /// The plan's own rules are checked by the generator before anything is
    /// asked of GitHub, and come back keyed to their own fields as usual.</para>
    /// </summary>
    /// <exception cref="PlanValidationException">The plan is invalid, or the repository cannot be written to.</exception>
    /// <exception cref="GitHubApiException">GitHub refused one of the calls that make up the commit.</exception>
    public async Task<GitHubExtensionDelivery> AddExtensionAsync(
        StandaloneExtensionPlan plan,
        SiblingWorkspaceContext? sibling,
        string repoFullName,
        CancellationToken ct = default)
    {
        var userId = RequireUserId();

        // Whatever is going into a repository is going in beside something
        // that is already there, so the template's example files stay out
        // even when the repository saved no settings to say what it holds.
        plan = plan with { IncludeExamples = false };

        // The plan's own rules first: an extension nobody could generate is not
        // worth a round trip to GitHub, and the errors it raises are keyed to
        // the fields that caused them rather than to the repository.
        var planErrors = await _generation.ValidateExtensionAsync(plan, ct: ct);
        if (planErrors.Count > 0) throw new PlanValidationException(planErrors.ToDictionary(e => e.Key, e => e.Value));

        // Why-not first, so the answer names the thing the caller can change.
        // Resolving a repository would refuse all four of these identically,
        // and "that is not a repository we can offer you" is a poor way to say
        // "you have not connected your GitHub account".
        var access = await _repositories.GetAccessAsync(ct);
        if (!access.IsReady) throw Refuse(access.Readiness switch
        {
            GitHubRepositoryReadiness.NotConfigured =>
                "GitHub is not set up on this server yet, so nothing can be added to a repository. "
                + "Ask whoever runs AL Workbench to set it up.",
            GitHubRepositoryReadiness.NotConnected =>
                "Your organisation has not connected a GitHub organisation yet, so there is nowhere to "
                + "add this. An administrator connects one under Administration -> Repositories.",
            GitHubRepositoryReadiness.LinkNeedsRepair =>
                "Your GitHub account is no longer connected to the workbench. Connect it again on your "
                + "account page under Repository access, then try this again.",
            _ =>
                "Connect your own GitHub account first, on your account page under Repository access. "
                + "The extension is added in your name, so the workbench needs your GitHub account to do it.",
        });

        var repo = await _repositories.ResolveAsync(repoFullName, ct)
            ?? throw Refuse(
                "That repository is not one the workbench can offer you. Pick one from the list, "
                + "or ask an owner of your GitHub organisation to give you access to it.");

        var token = await _access.ResolveUserTokenAsync(userId, ct)
            ?? throw Refuse(
                "Connect your own GitHub account first, on your account page under Repository access. "
                + "The extension is added in your name, so the workbench needs your GitHub account to do it.");

        var baseSha = await _github.GetBranchHeadShaAsync(token, repo.Owner, repo.Name, repo.DefaultBranch, ct)
            ?? throw Refuse(
                $"'{repo.FullName}' has no commits on {repo.DefaultBranch} yet, so there is nothing to open a "
                + "pull request against. Push something to it first, then come back.");

        sibling = await ReadSavedWorkspaceAsync(token, repo, ct) ?? sibling;
        if (sibling is not null)
        {
            // Settled here rather than left to the generator, because the
            // folder checked below has to be the one it writes.
            sibling = await _generation.ResolveSiblingAsync(sibling, ct);
            var siblingErrors = await _generation.ValidateExtensionAsync(plan, sibling, ct);
            if (siblingErrors.Count > 0)
            {
                throw new PlanValidationException(siblingErrors.ToDictionary(e => e.Key, e => e.Value));
            }
        }

        var folderName = sibling?.FolderNameFor(plan.ExtensionName)
            ?? CustomerNaming.Apply(plan.ExtensionName, NamingStyle.PascalCase);
        var extensionName = sibling?.ExtensionNameFor(plan.ExtensionName) ?? plan.ExtensionName;
        var existing = await _github.GetFileAsync(
            token, repo.Owner, repo.Name, $"{folderName}/{ExtensionMarkerFile}", repo.DefaultBranch, ct);
        if (existing is not null)
        {
            throw Refuse(
                $"'{repo.FullName}' already has an extension in a folder called {folderName}. "
                + "Give this one a different name, or add to the existing extension in your editor instead.");
        }

        var (files, archiveName, archiveBytes) = await BuildFilesAsync(plan, sibling, ct);
        // Only for the pull-request body: the sibling workspace's file is named
        // in the organisation's folder style, the same one the generator just
        // rewrote it under.
        var folderStyle = await _generation.GetFolderStyleAsync(ct);

        var blobs = new List<(string Path, string BlobSha)>(files.Count);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            blobs.Add((file.Path, await _github.CreateBlobAsync(token, repo.Owner, repo.Name, file.Content, ct)));
        }

        var baseTree = await _github.GetCommitTreeShaAsync(token, repo.Owner, repo.Name, baseSha, ct);
        var tree = await _github.CreateTreeAsync(token, repo.Owner, repo.Name, baseTree, blobs, ct);
        var commit = await _github.CreateCommitAsync(
            token, repo.Owner, repo.Name,
            $"Add the {extensionName} extension", tree, baseSha, ct: ct);

        var branch = await CreateBranchAsync(token, repo, folderName, commit, ct);
        var pullRequest = await _github.CreatePullRequestAsync(
            token, repo.Owner, repo.Name,
            title: $"Add the {extensionName} extension",
            head: branch,
            baseBranch: repo.DefaultBranch,
            body: BuildBody(plan, extensionName, sibling, folderName, files.Count, folderStyle),
            ct);

        _logger.LogInformation(
            "User {UserId} added extension '{Extension}' to {RepoFullName} on branch {Branch} as pull request #{PullRequestNumber} ({FileCount} files).",
            userId, extensionName, repo.FullName, branch, pullRequest.Number, files.Count);

        return new GitHubExtensionDelivery(
            repo, pullRequest, folderName, files.Count, archiveName, archiveBytes);
    }

    /// <summary>
    /// The solution a repository holds, read from the settings the workbench
    /// saved at its root - the same file picking the repository on the page
    /// fills the form from. Null when there is no such file, when it describes
    /// a single extension rather than a solution, or when it can no longer be
    /// read (a template since deleted, say): the extension is then added as a
    /// standalone one, which is what the page does with the same repository.
    /// </summary>
    private async Task<SiblingWorkspaceContext?> ReadSavedWorkspaceAsync(
        string token, GitHubRepositorySummary repo, CancellationToken ct)
    {
        var file = await _github.GetFileAsync(
            token, repo.Owner, repo.Name, WorkspaceConfigService.FileName, repo.DefaultBranch, ct);
        if (file is null) return null;

        WorkspaceConfigImport import;
        try
        {
            import = await _configs.ParseAsync(file.Text, ct);
        }
        catch (PlanValidationException ex)
        {
            _logger.LogWarning(
                "The saved config in {RepoFullName} could not be read, so the extension is added on its own: {Errors}",
                repo.FullName, string.Join("; ", ex.Errors.Select(e => $"{e.Key}: {e.Value}")));
            return null;
        }

        if (import.Workspace is not { } workspace) return null;
        return new SiblingWorkspaceContext(
            workspace.WorkspaceName,
            workspace.SelectedModuleKeys,
            import.Extensions.Select(e => e.Folder).ToList(),
            workspace.ShortName,
            workspace.ExtensionPrefix,
            SavedPlan: workspace,
            SavedExtensions: import.Extensions);
    }

    /// <summary>
    /// The generated file set, read straight back out of the archive the
    /// download would have handed over. Reading the ZIP rather than teaching
    /// the generator a second output shape is deliberate: there is then exactly
    /// one description of what a generated extension contains.
    ///
    /// <para>The workspace-root files a template opts into (a .gitignore, a
    /// README stub, the shared ruleset) are left out. A standalone download
    /// carries them because the extension folder <em>is</em> the root of what
    /// the user unzips; a repository already has its own root, and committing a
    /// second copy one level down would be noise at best.</para>
    /// </summary>
    private async Task<(List<GitHubCommitFile> Files, string ArchiveName, byte[] Archive)> BuildFilesAsync(
        StandaloneExtensionPlan plan, SiblingWorkspaceContext? sibling, CancellationToken ct)
    {
        var archive = await _generation.GenerateExtensionAsync(
            plan, sibling, includeWorkspaceRootFiles: false, ct);
        await using var stream = archive.Stream;
        stream.Position = 0;

        var files = new List<GitHubCommitFile>();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var entry in zip.Entries)
            {
                // Directory entries have an empty name; the generator writes
                // none, but a ZIP reader should not assume that.
                if (string.IsNullOrEmpty(entry.Name)) continue;
                using var entryStream = entry.Open();
                using var buffer = new MemoryStream();
                await entryStream.CopyToAsync(buffer, ct);
                files.Add(new GitHubCommitFile(entry.FullName, buffer.ToArray()));
            }
        }
        return (files, archive.FileName, stream.ToArray());
    }

    /// <summary>
    /// Points a new branch at the commit, stepping the name when it is taken.
    /// A previous attempt's branch is never moved: its pull request may already
    /// be under review.
    /// </summary>
    private async Task<string> CreateBranchAsync(
        string token, GitHubRepositorySummary repo, string folderName, string commitSha, CancellationToken ct)
    {
        var baseName = BranchPrefix + Slug(folderName);
        for (var attempt = 1; attempt <= MaxBranchAttempts; attempt++)
        {
            var branch = attempt == 1 ? baseName : $"{baseName}-{attempt}";
            if (await _github.CreateBranchAsync(token, repo.Owner, repo.Name, branch, commitSha, ct))
            {
                return branch;
            }
            _logger.LogInformation(
                "Branch {Branch} already exists on {RepoFullName}; trying the next name.", branch, repo.FullName);
        }

        throw Refuse(
            $"'{repo.FullName}' already has branches named {baseName} through {baseName}-{MaxBranchAttempts}. "
            + "Tidy those up on GitHub, or give this extension a different name.");
    }

    /// <summary>The pull request's description: what it adds, and where it came from.</summary>
    private static string BuildBody(
        StandaloneExtensionPlan plan, string extensionName, SiblingWorkspaceContext? sibling, string folderName,
        int fileCount, NamingStyle folderStyle)
    {
        var lines = new List<string>
        {
            $"Adds the **{extensionName}** extension in `{folderName}/` ({fileCount} files), "
                + $"object IDs {plan.IdRangeFrom}-{plan.IdRangeTo}.",
        };
        if (!string.IsNullOrWhiteSpace(plan.Brief))
        {
            lines.Add(plan.Brief.Trim());
        }
        if (sibling is not null)
        {
            // Only when the builder rewrote it: an older saved workspace lists no
            // folders, and the file is then left alone.
            if (sibling.ExistingFolders.Count > 0)
            {
                lines.Add($"The `{CustomerNaming.Apply(sibling.WorkspaceName, folderStyle)}.code-workspace` file is "
                    + "updated so the new folder opens with the rest of the workspace.");
            }
            if (sibling.SavedPlan is not null && sibling.SavedExtensions is { Count: > 0 })
            {
                lines.Add($"`{WorkspaceConfigService.FileName}` now lists it too, so the next extension added "
                    + "here starts after its object IDs.");
            }
        }
        lines.Add("Generated by AL Workbench.");
        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// A branch-safe form of the folder name. The extension name is already
    /// letters and digits by the time it gets here, so this is a belt on top of
    /// the generator's rule rather than the rule itself.
    /// </summary>
    private static string Slug(string folderName)
    {
        var slug = new string(folderName
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray())
            .Trim('-', '.');
        return slug.Length == 0 ? "extension" : slug;
    }

    private static PlanValidationException Refuse(string message) =>
        new(new Dictionary<string, string> { ["GitHubRepository"] = message });
}
