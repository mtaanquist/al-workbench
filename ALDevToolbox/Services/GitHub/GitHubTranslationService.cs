using System.Text;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Translation;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// One XLIFF file the Translator offers to open from a repository.
/// </summary>
/// <param name="Path">Where it lives in the repository, from the root.</param>
/// <param name="FileName">Its own name, which is what the list shows.</param>
/// <param name="Folder">
/// The folder holding the <c>Translations</c> folder - the extension, in an AL
/// workspace. Empty when <c>Translations</c> sits at the repository root. Two
/// extensions in one repository can both have a <c>da-DK</c> file, so the list
/// groups by this rather than making the consultant read paths.
/// </param>
/// <param name="Language">
/// The language tag in the file name (<c>da-DK</c> in <c>App.da-DK.xlf</c>),
/// when there is one.
/// </param>
/// <param name="IsSource">
/// True for the <c>.g.xlf</c> the AL compiler generates. It holds every string
/// with no translations in it, so it is the file you start a new language from.
/// </param>
public sealed record RepositoryTranslationFile(
    string Path,
    string FileName,
    string Folder,
    string? Language,
    bool IsSource);

/// <summary>
/// Where an open translation file came from, and which version of it the
/// editor is working on.
///
/// <para><see cref="BaseSha"/> is the load-time blob sha, replaced by the sha
/// of each save. It is what the next write quotes, so it has to track what the
/// editor's content is actually based on rather than what was first read.</para>
/// </summary>
public sealed record RepositoryTranslationSource(
    GitHubRepositorySummary Repository,
    string Path,
    string FileName,
    string? BaseSha);

/// <summary>What a save produced.</summary>
/// <param name="PullRequest">The pull request the translation is now in.</param>
/// <param name="IsNewPullRequest">
/// False when the save added a commit to a pull request that was already open,
/// which is the normal shape of a second save.
/// </param>
/// <param name="Source">The source with its sha moved on to the version just written.</param>
public sealed record RepositoryTranslationSave(
    GitHubPullRequest PullRequest,
    bool IsNewPullRequest,
    RepositoryTranslationSource Source);

/// <summary>
/// The Translator's round trip to a repository (issue #625): list the XLIFF
/// files in one, read one, and save the edited file back as a pull request.
///
/// <para><strong>The write is the user's own, and never the default
/// branch.</strong> Like "Add to repository" (#623) the commit goes out on the
/// acting user's linked token, so GitHub enforces their permissions natively
/// and the pull request is genuinely theirs; and it lands on
/// <c>aldt/translate-&lt;language&gt;</c> even when the default branch is
/// unprotected, because a translation is a change somebody reviews. The branch
/// is reused for as long as its pull request is open, so translating over three
/// afternoons produces one pull request rather than three.</para>
///
/// <para><strong>Nothing is ever written over blind.</strong> Every save quotes
/// the blob sha of the version the editor started from. If the file in the
/// repository has moved on - a colleague committed, or the branch already
/// carries a different translation - the write is refused with
/// <see cref="GitHubContentConflictException"/> and the page offers a way back.
/// That refusal is the point of the feature: the failure worth designing
/// against is a translator quietly undoing someone else's afternoon.</para>
///
/// <para>Which repositories can be reached at all is
/// <see cref="GitHubRepositoryService.ResolveAsync"/>'s decision, so this
/// inherits the one gate rather than re-deciding it. See
/// <c>.design/github-integration.md</c>.</para>
/// </summary>
public sealed class GitHubTranslationService
{
    /// <summary>Branch names are <c>aldt/translate-&lt;language&gt;</c>, per the design doc.</summary>
    public const string BranchPrefix = "aldt/translate-";

    private readonly GitHubRepositoryService _repositories;
    private readonly GitHubAccessService _access;
    private readonly GitHubAppClient _github;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<GitHubTranslationService> _logger;

    public GitHubTranslationService(
        GitHubRepositoryService repositories,
        GitHubAccessService access,
        GitHubAppClient github,
        IOrganizationContext orgContext,
        ILogger<GitHubTranslationService> logger)
    {
        _repositories = repositories;
        _access = access;
        _github = github;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; GitHubTranslationService called outside an authenticated request.");

    /// <summary>
    /// The XLIFF files in a repository: everything one level inside a folder
    /// called <c>Translations</c>, wherever that folder sits. An AL workspace
    /// keeps one per extension, a single-app repository keeps one at the root,
    /// and both are found by the same rule.
    ///
    /// <para>Ordered the way the list reads: extension folders alphabetically,
    /// the generated source file first inside each one, then the languages.</para>
    /// </summary>
    /// <exception cref="PlanValidationException">The repository cannot be reached or is not one this user may open.</exception>
    /// <exception cref="GitHubApiException">GitHub refused to list the files.</exception>
    public async Task<IReadOnlyList<RepositoryTranslationFile>> ListFilesAsync(
        string repoFullName, CancellationToken ct = default)
    {
        var (repo, token) = await ResolveAsync(repoFullName, ct);
        var tree = await _github.ListTreeAsync(token, repo.Owner, repo.Name, repo.DefaultBranch, ct);

        var files = tree.Entries
            .Where(e => string.Equals(e.Type, "blob", StringComparison.Ordinal))
            .Where(e => TranslationFileRules.IsTranslationFile(e.Path))
            .Select(e => Describe(e.Path))
            .OrderBy(f => f.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(f => f.IsSource)
            .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "Found {FileCount} translation files in {RepoFullName} on {Branch}.",
            files.Count, repo.FullName, repo.DefaultBranch);
        return files;
    }

    /// <summary>
    /// Reads one translation file, or <see langword="null"/> when it is not
    /// there any more. The caller loads the text exactly as it loads an
    /// uploaded file; the sha that comes back is what a later save quotes.
    /// </summary>
    /// <exception cref="PlanValidationException">The repository cannot be reached or is not one this user may open.</exception>
    public async Task<GitHubFileContent?> OpenAsync(
        string repoFullName, string path, CancellationToken ct = default)
    {
        var (repo, token) = await ResolveAsync(repoFullName, ct);
        var file = await _github.GetFileAsync(token, repo.Owner, repo.Name, path, repo.DefaultBranch, ct);
        if (file is null)
        {
            _logger.LogInformation("{Path} is no longer in {RepoFullName}.", path, repo.FullName);
        }
        return file;
    }

    /// <summary>
    /// Reads a file from the language's translation branch while a pull
    /// request is open for it, or <see langword="null"/> when none is open or
    /// the branch does not carry the file.
    ///
    /// <para>While a translation's pull request is open, its branch is ahead of
    /// the default branch: the file there carries the earlier translations and
    /// the sha the next save is compared against, and a language file started
    /// from the generated source file exists only there. A second round of
    /// translations has to start from that version, not the default branch's,
    /// or <see cref="SaveAsync"/> refuses it as a conflict every time.</para>
    ///
    /// <para>Once that pull request is merged or closed the branch is history,
    /// not work in progress. GitHub keeps it unless the repository deletes
    /// merged branches, so "the branch exists" says nothing about whether it is
    /// current: reading it then would hide every string added to the default
    /// branch since (#1329). The next save moves the branch on instead - see
    /// <see cref="PrepareBranchAsync"/>.</para>
    /// </summary>
    /// <exception cref="PlanValidationException">The repository cannot be reached or is not one this user may open.</exception>
    public async Task<GitHubFileContent?> OpenOnTranslationBranchAsync(
        string repoFullName, string path, string targetLanguage, CancellationToken ct = default)
    {
        var (repo, token) = await ResolveAsync(repoFullName, ct);
        var branch = BranchPrefix + Slug(targetLanguage);
        if (await _github.FindOpenPullRequestAsync(token, repo.Owner, repo.Name, branch, ct) is null) return null;
        return await _github.GetFileAsync(token, repo.Owner, repo.Name, path, branch, ct);
    }

    /// <summary>
    /// Commits the edited file onto the translation branch and makes sure a
    /// pull request is open for it.
    ///
    /// <para>The order matters: the commit lands first, because a pull request
    /// from a branch identical to the default one has nothing to show and
    /// GitHub refuses to open it.</para>
    /// </summary>
    /// <param name="source">Where the file came from, carrying the sha this edit is based on.</param>
    /// <param name="targetLanguage">The language being translated, which names the branch.</param>
    /// <param name="xml">The finished file, byte for byte as it should land in the repository.</param>
    /// <param name="summary">One line for the pull request body saying how far the translation has got.</param>
    /// <exception cref="GitHubContentConflictException">The file changed in the repository since it was opened.</exception>
    /// <exception cref="PlanValidationException">The repository cannot be written to, with a reason the user can act on.</exception>
    /// <exception cref="GitHubApiException">GitHub refused one of the calls.</exception>
    public async Task<RepositoryTranslationSave> SaveAsync(
        RepositoryTranslationSource source,
        string targetLanguage,
        string xml,
        string summary,
        CancellationToken ct = default)
    {
        var userId = RequireUserId();
        var (repo, token) = await ResolveAsync(source.Repository.FullName, ct);
        var branch = BranchPrefix + Slug(targetLanguage);

        var open = await PrepareBranchAsync(token, repo, branch, ct);

        // What is on the branch right now is what a write would replace. If it
        // is not the version this editor started from, somebody else got there
        // first - on the branch or on the default branch it was cut from - and
        // the honest answer is to stop rather than to win the race.
        var current = await _github.GetFileAsync(token, repo.Owner, repo.Name, source.Path, branch, ct);

        // A null base sha says "I am creating this file". Finding one there
        // already is not a race - it is a translation somebody has been working
        // on, and writing this one over it would throw all of it away.
        if (current is not null && source.BaseSha is null)
        {
            throw Refuse(
                $"'{repo.FullName}' already has a {targetLanguage} translation at {source.Path}. Open that "
                + "file instead - saving this one would replace everything already translated in it.");
        }

        if (current is not null && !string.Equals(current.Sha, source.BaseSha, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Refusing to save {Path} to {RepoFullName}: it is at {CurrentSha}, this edit is based on {BaseSha}.",
                source.Path, repo.FullName, current.Sha, source.BaseSha ?? "nothing");
            throw new GitHubContentConflictException(
                source.Path, "The file changed in the repository since it was opened.");
        }

        var write = await _github.PutFileAsync(
            token, repo.Owner, repo.Name, source.Path, branch,
            // No author: this write goes out on the user's own token, so GitHub
            // already credits the commit to them.
            $"Translate {source.FileName}", Encoding.UTF8.GetBytes(xml), current?.Sha, ct: ct);

        // Find before create: a pull request that is still open takes the new
        // commit, so a second afternoon's work joins the first one's review
        // instead of opening a second pull request beside it.
        var existing = open ?? await _github.FindOpenPullRequestAsync(token, repo.Owner, repo.Name, branch, ct);
        var pullRequest = existing ?? await _github.CreatePullRequestAsync(
            token, repo.Owner, repo.Name,
            title: $"Translate {source.FileName} into {targetLanguage}",
            head: branch,
            baseBranch: repo.DefaultBranch,
            body: BuildBody(source, targetLanguage, summary),
            ct);

        _logger.LogInformation(
            "User {UserId} saved {Path} to {RepoFullName} on {Branch} in pull request #{PullRequestNumber} ({PullRequestState}).",
            userId, source.Path, repo.FullName, branch, pullRequest.Number,
            existing is null ? "opened" : "already open");

        return new RepositoryTranslationSave(
            pullRequest,
            IsNewPullRequest: existing is null,
            Source: source with { BaseSha = write.ContentSha });
    }

    /// <summary>
    /// Makes sure the translation branch is ready to take this save, and
    /// returns its open pull request when it has one.
    ///
    /// <list type="bullet">
    /// <item>No branch yet: cut it from the default branch. One someone else
    /// created in the meantime is left where it is - the sha the write quotes
    /// is what keeps that safe.</item>
    /// <item>A branch with an open pull request: left exactly where it is.
    /// That is the review in progress, and the quoted sha keeps adding to it
    /// safe.</item>
    /// <item>A branch with no open pull request: the previous round was merged
    /// or closed and the branch kept. Building on it would miss everything the
    /// default branch gained since and open a pull request that conflicts with
    /// it (#1329), so it is moved to the default branch's head first - a
    /// fast-forward when the last round was merged with a merge commit, a reset
    /// otherwise. A squash-merged round is in the default branch already, and a
    /// closed one stays on GitHub under its pull request.</item>
    /// </list>
    /// </summary>
    private async Task<GitHubPullRequest?> PrepareBranchAsync(
        string token, GitHubRepositorySummary repo, string branch, CancellationToken ct)
    {
        var head = await _github.GetBranchHeadShaAsync(token, repo.Owner, repo.Name, branch, ct);
        if (head is not null)
        {
            var open = await _github.FindOpenPullRequestAsync(token, repo.Owner, repo.Name, branch, ct);
            if (open is not null) return open;
        }

        var baseHead = await _github.GetBranchHeadShaAsync(token, repo.Owner, repo.Name, repo.DefaultBranch, ct)
            ?? throw Refuse(
                $"'{repo.FullName}' has no commits on {repo.DefaultBranch} yet, so there is nothing to open a "
                + "pull request against. Push something to it first, then try again.");

        if (head is null)
        {
            await _github.CreateBranchAsync(token, repo.Owner, repo.Name, branch, baseHead, ct);
            _logger.LogInformation(
                "Started branch {Branch} on {RepoFullName} from {DefaultBranch}.",
                branch, repo.FullName, repo.DefaultBranch);
            return null;
        }

        if (string.Equals(head, baseHead, StringComparison.Ordinal)) return null;

        if (!await _github.UpdateBranchAsync(token, repo.Owner, repo.Name, branch, baseHead, ct))
        {
            await _github.ResetBranchAsync(token, repo.Owner, repo.Name, branch, baseHead, ct);
        }
        _logger.LogInformation(
            "Moved {Branch} on {RepoFullName} from {OldHead} to the head of {DefaultBranch}: no pull request was open for it.",
            branch, repo.FullName, head, repo.DefaultBranch);
        return null;
    }

    /// <summary>
    /// The repository this caller may act on, and the token to act with.
    /// Every refusal is a field-keyed <see cref="PlanValidationException"/> on
    /// <c>GitHubRepository</c>, the same key "Add to repository" uses, so one
    /// message shape covers both features.
    /// </summary>
    private async Task<(GitHubRepositorySummary Repo, string Token)> ResolveAsync(
        string repoFullName, CancellationToken ct)
    {
        var userId = RequireUserId();

        // Why-not first, so the answer names the thing the person can change.
        // Resolving would refuse all of these identically, and "that is not a
        // repository we can offer you" is a poor way to say "your GitHub
        // account is no longer connected".
        var access = await _repositories.GetAccessAsync(ct);
        if (!access.IsReady) throw Refuse(access.Readiness switch
        {
            GitHubRepositoryReadiness.NotConfigured =>
                "GitHub is not set up on this server yet. Ask whoever runs AL Workbench to set it up - "
                + "meanwhile you can export the file and commit it yourself.",
            GitHubRepositoryReadiness.NotConnected =>
                "Your organisation has not connected a GitHub organisation yet. An administrator connects "
                + "one under Administration -> Repositories.",
            GitHubRepositoryReadiness.LinkNeedsRepair =>
                "Your GitHub account is no longer connected to the workbench. Connect it again on your "
                + "account page under Repository access, then try again.",
            _ =>
                "Connect your own GitHub account first, on your account page under Repository access. "
                + "The pull request is opened in your name, so the workbench needs your GitHub account to do it.",
        });

        var repo = await _repositories.ResolveAsync(repoFullName, ct)
            ?? throw Refuse(
                "That repository is not one the workbench can offer you. Pick one from the list, or ask an "
                + "owner of your GitHub organisation to give you access to it.");

        var token = await _access.ResolveUserTokenAsync(userId, ct)
            ?? throw Refuse(
                "Connect your own GitHub account first, on your account page under Repository access. "
                + "The pull request is opened in your name, so the workbench needs your GitHub account to do it.");

        return (repo, token);
    }

    /// <summary>
    /// One tree entry as the list shows it. The rules themselves live in
    /// <see cref="TranslationFileRules"/>, which the translation-memory ingest
    /// shares - "which files in this repository are translations" is one
    /// question and two answers to it would drift apart.
    /// </summary>
    private static RepositoryTranslationFile Describe(string path)
    {
        var place = TranslationFileRules.Describe(path);
        return new RepositoryTranslationFile(
            place.Path, place.FileName, place.Folder, place.Language, place.IsSource);
    }

    /// <summary>The pull request's description: what changed, and where it came from.</summary>
    private static string BuildBody(RepositoryTranslationSource source, string targetLanguage, string summary)
    {
        var lines = new List<string>
        {
            $"Updates the **{targetLanguage}** translation in `{source.Path}`.",
        };
        if (!string.IsNullOrWhiteSpace(summary))
        {
            lines.Add(summary.Trim());
        }
        lines.Add("Translated in AL Workbench. Every other byte of the file is unchanged, so the diff is "
            + "the translations themselves.");
        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// A branch-safe form of a language tag. Real tags are already letters,
    /// digits and hyphens, so this is a belt rather than the rule.
    /// </summary>
    private static string Slug(string language)
    {
        var slug = new string((language ?? string.Empty)
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray())
            .Trim('-', '.');
        return slug.Length == 0 ? "translation" : slug;
    }

    private static PlanValidationException Refuse(string message) =>
        new(new Dictionary<string, string> { ["GitHubRepository"] = message });
}
