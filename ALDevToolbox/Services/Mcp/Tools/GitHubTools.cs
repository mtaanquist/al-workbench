using System.ComponentModel;
using System.Text;
using System.Xml;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Mcp.Dtos;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.Translation;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// The GitHub workflows an agent can reach on their own: which repositories
/// the caller may act on, creating one for a workspace, adding an extension to
/// one, and translating a file in one (issue #633).
///
/// <para>Three of these are also options on <c>generate_workspace</c> /
/// <c>generate_extension</c>. They exist here as tools of their own because an
/// agent finds a capability by its name; the options stay, and both routes call
/// the same service, so neither can reach a repository the other would refuse.</para>
///
/// <para><strong>Every repository name goes through
/// <see cref="GitHubRepositoryService"/>.</strong> None of these tools query
/// GitHub or the database for a repository themselves, so an agent naming one
/// directly gets exactly the answer the web UI's picker would have given - see
/// the resolver rule in <c>PROJECT.md</c> and
/// <c>.design/github-integration-phase2.md</c>.</para>
/// </summary>
[McpServerToolType]
public sealed class GitHubTools
{
    private readonly GitHubRepositoryService _repositories;
    private readonly GitHubWorkspaceRepositoryService _workspaces;
    private readonly GitHubExtensionDeliveryService _delivery;
    private readonly GitHubTranslationService _translations;
    private readonly ILogger<GitHubTools> _logger;

    public GitHubTools(
        GitHubRepositoryService repositories,
        GitHubWorkspaceRepositoryService workspaces,
        GitHubExtensionDeliveryService delivery,
        GitHubTranslationService translations,
        ILogger<GitHubTools> logger)
    {
        _repositories = repositories;
        _workspaces = workspaces;
        _delivery = delivery;
        _translations = translations;
        _logger = logger;
    }

    /// <summary>The suffix the AL compiler gives the generated source file.</summary>
    private const string SourceFileSuffix = ".g.xlf";

    [McpServerTool(Name = "list_repositories", ReadOnly = true)]
    [Description(
        "Lists the GitHub repositories you can act on: the ones in the GitHub organisation your " +
        "organisation has connected that you can also open on GitHub yourself. Nothing else is ever " +
        "offered, and every other tool that takes a repository accepts only what this one lists. " +
        "When the list is empty because something is not set up yet - no GitHub organisation connected, " +
        "or your own GitHub account not connected - the answer says which, in a sentence you can pass " +
        "on to the person.")]
    public async Task<RepositoryListResult> ListRepositoriesAsync(CancellationToken ct = default)
    {
        try
        {
            var access = await _repositories.GetAccessAsync(ct);
            var repositories = await _repositories.ListAccessibleAsync(ct);
            return new RepositoryListResult(
                Readiness: access.Readiness.ToString(),
                Guidance: GuidanceFor(access.Readiness),
                Repositories: repositories
                    .Select(r => new RepositorySummary(
                        r.FullName, r.DefaultBranch, r.IsPrivate, r.Description, r.HtmlUrl, r.CloneUrl))
                    .ToList());
        }
        catch (GitHubApiException ex)
        {
            throw new McpException("GitHub refused the request: " + ex.Message);
        }
        catch (GitHubAppNotConfiguredException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "create_repository", ReadOnly = false, Idempotent = false)]
    [Description(
        "Generates a workspace and creates a GitHub repository holding it: the repository is created in " +
        "the GitHub organisation your organisation has connected - never anywhere else, which is why this " +
        "takes a name and not an owner - and the generated files are committed to it. You have to be a " +
        "member of that GitHub organisation; a call from someone who is not, or from an account that has " +
        "not connected its GitHub account, is refused and nothing is created. A name the organisation " +
        "already uses is refused too. The repository is registered on a solution only when the " +
        "organisation has Solutions enabled. The files are committed straight to the default branch, " +
        "and test and staging branches are created at the same commit; when the repository is the only " +
        "one on its solution, that solution also gets a build pipeline per branch. " +
        "If the GitHub organisation's branch rules refuse the commit, the call is refused with what an owner " +
        "of the organisation has to allow. Use generate_workspace instead when you want the workspace " +
        "as a ZIP.")]
    public async Task<RepositoryCreationResult> CreateRepositoryAsync(
        ProjectPlanInput plan,
        [Description("The repository name, without an owner. Letters, digits, hyphens, underscores and full stops, at most 100 characters.")]
        string name,
        [Description("Whether the new repository is private. Defaults to true.")]
        bool isPrivate = true,
        [Description("Optional. The id of an existing solution (from list_solutions) to register the new repository on. Leave it out to create a new solution named after the customer. Only applies when the organisation has Solutions enabled - passing one while it is switched off is refused.")]
        int? solutionId = null,
        CancellationToken ct = default)
    {
        try
        {
            // The same service the New Workspace page uses, so the organisation,
            // the membership check and the credential split are inherited rather
            // than restated - see "Keeping MCP parity with the web UI" in PROJECT.md.
            var created = await _workspaces.CreateAsync(
                plan.ToDomain(), (name ?? string.Empty).Trim(), isPrivate, solutionId, ct);
            return RepositoryCreationResult.From(created);
        }
        catch (Exception ex) when (IsGitHubRefusal(ex))
        {
            throw Translate(ex);
        }
    }

    [McpServerTool(Name = "add_extension_to_repository", ReadOnly = false, Idempotent = false)]
    [Description(
        "Generates a standalone extension and adds it to one of your GitHub repositories: the files are " +
        "committed to a branch of their own, in your name, and a pull request is opened for them. Nothing " +
        "on the repository's default branch changes until somebody merges it. Only repositories in the " +
        "GitHub organisation your organisation has connected, and that you can open on GitHub yourself, " +
        "are accepted; a repository that already has an extension in a folder of that name is refused, as " +
        "is a call from an account that has not connected its GitHub account. This returns the pull " +
        "request only - use generate_extension with addToRepository when you want the ZIP alongside it.")]
    public async Task<RepositoryDeliveryResult> AddExtensionToRepositoryAsync(
        StandaloneExtensionPlanInput plan,
        [Description("The repository as 'owner/name', from list_repositories.")]
        string repository,
        CancellationToken ct = default)
    {
        try
        {
            // The same service the New Extension page uses; the access rule is
            // inherited rather than restated here.
            var delivered = await _delivery.AddExtensionAsync(
                plan.ToDomain(), sibling: null, (repository ?? string.Empty).Trim(), ct);
            return RepositoryDeliveryResult.From(delivered);
        }
        catch (Exception ex) when (IsGitHubRefusal(ex))
        {
            throw Translate(ex);
        }
    }

    [McpServerTool(Name = "list_translation_files", ReadOnly = true)]
    [Description(
        "Lists the XLIFF translation files in one of your GitHub repositories - everything directly " +
        "inside a folder called Translations, wherever that folder sits, so an AL workspace with one per " +
        "extension is covered as well as a single-app repository. The file marked isSource is the one the " +
        "AL compiler generates: it holds every string and no translations, so it is the file a new " +
        "language starts from. Only repositories in the GitHub organisation your organisation has " +
        "connected, and that you can open on GitHub yourself, are accepted.")]
    public async Task<IReadOnlyList<TranslationFileSummary>> ListTranslationFilesAsync(
        [Description("The repository as 'owner/name', from list_repositories.")]
        string repository,
        CancellationToken ct = default)
    {
        try
        {
            var files = await _translations.ListFilesAsync((repository ?? string.Empty).Trim(), ct);
            return files
                .Select(f => new TranslationFileSummary(f.Path, f.Folder, f.Language, f.IsSource))
                .ToList();
        }
        catch (Exception ex) when (IsGitHubRefusal(ex))
        {
            throw Translate(ex);
        }
    }

    [McpServerTool(Name = "open_translation_pr", ReadOnly = false, Idempotent = false)]
    [Description(
        "Writes translations into an XLIFF file in one of your GitHub repositories and opens a pull " +
        "request for them. The file is read first and only the strings you name are changed, so every " +
        "other byte - indentation, notes, the strings somebody else translated - is left exactly as it " +
        "was and the diff is the translations themselves. Naming a string the file does not carry is " +
        "refused, and nothing is written. The commit goes out in your name, onto a branch named for the " +
        "language, never onto the default branch; translating the same language again while its pull " +
        "request is open adds a commit to that pull request rather than opening a second one. Starting " +
        "from the compiler's generated source file writes a new language file beside it instead of " +
        "changing the generated one. A file that changed in the repository since it was read is refused " +
        "rather than written over. Only repositories in the GitHub organisation your organisation has " +
        "connected, and that you can open on GitHub yourself, are accepted.")]
    public async Task<TranslationPullRequestResult> OpenTranslationPullRequestAsync(
        [Description("The repository as 'owner/name', from list_repositories.")]
        string repository,
        [Description("The file to translate, as list_translation_files gives its path.")]
        string path,
        [Description("The language being translated into, as a tag such as da-DK. It names the branch and, when the file read was the generated source file, the new file.")]
        string targetLanguage,
        [Description("The strings to translate: the trans-unit id exactly as the file carries it, the translated text, and optionally the XLIFF state to record, such as 'translated'.")]
        IReadOnlyList<TranslationUnitEditInput> edits,
        [Description("Optional. One line for the pull request body saying how far the translation has got. A count of what changed is used when you do not give one.")]
        string? summary = null,
        CancellationToken ct = default)
    {
        var repoFullName = (repository ?? string.Empty).Trim();
        var filePath = (path ?? string.Empty).Trim();
        var language = (targetLanguage ?? string.Empty).Trim();

        if (language.Length == 0) throw new McpException("Say which language you are translating into, e.g. da-DK.");
        if (edits is null || edits.Count == 0)
        {
            throw new McpException(
                "No translations were given, so there would be nothing to open a pull request for.");
        }

        try
        {
            var file = await _translations.OpenAsync(repoFullName, filePath, ct)
                ?? throw new McpException(
                    $"'{filePath}' is not in {repoFullName}. Call list_translation_files to see what is.");

            // Parse the file that was just read, so an id that is not in it is
            // reported as such rather than silently doing nothing: applying an
            // edit nobody can see landing is worse than refusing it.
            XliffDocument parsed;
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(file.Text));
                parsed = AlXliffParser.Parse(stream);
            }
            catch (Exception ex) when (ex is InvalidDataException or XmlException)
            {
                throw new McpException($"Could not read '{filePath}' as XLIFF v1.2: {ex.Message}");
            }

            // Two edits for one id would silently drop one of them in the map
            // below, and the agent would be told the write succeeded. Say which
            // ids came twice instead.
            var duplicates = edits
                .GroupBy(e => e.Id ?? string.Empty, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (duplicates.Count > 0)
            {
                throw new McpException(
                    "These string ids appear more than once, so nothing was written: "
                    + string.Join(", ", duplicates.Select(id => $"'{id}'"))
                    + ". Send one edit per id.");
            }

            var known = parsed.Units.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
            var unknown = edits
                .Select(e => e.Id ?? string.Empty)
                .Where(id => !known.Contains(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (unknown.Count > 0)
            {
                throw new McpException(
                    $"'{filePath}' has no strings with these ids, so nothing was written: "
                    + string.Join(", ", unknown.Select(id => $"'{id}'"))
                    + ". Ids have to match the file exactly.");
            }

            var map = edits.ToDictionary(
                e => e.Id,
                e => new TargetEdit(e.Target ?? string.Empty, e.State),
                StringComparer.Ordinal);

            var xml = XliffTargetWriter.ApplyEdits(file.Text, map);

            // The generated source file says its target language is its source
            // language, because nothing in it is translated yet. A file written
            // from it is a language file and has to say which language.
            var startedFromSource = filePath.EndsWith(SourceFileSuffix, StringComparison.OrdinalIgnoreCase);
            if (startedFromSource
                || !string.Equals(parsed.TargetLanguage, language, StringComparison.OrdinalIgnoreCase))
            {
                xml = XliffTargetWriter.SetTargetLanguage(xml, language);
            }

            // Where it lands, and which version it is based on. The generated
            // file belongs to the compiler, so a translation started from it is
            // written beside it as a file that does not exist yet - and a write
            // that quotes no sha is the one that says so.
            var savedPath = startedFromSource
                ? $"{filePath[..^SourceFileSuffix.Length]}.{language}.xlf"
                : filePath;
            // The repository as the one gate hands it over - the page passes the
            // one its picker offered, and this is the same object from the same
            // resolver. Opening the file above has already ruled out every
            // refusal, so a null here is a race, not a state worth its own copy.
            var repo = await _repositories.ResolveAsync(repoFullName, ct)
                ?? throw new McpException(
                    $"'{repoFullName}' is not a repository the workbench can offer you. "
                    + "Call list_repositories to see the ones it can.");

            var source = new RepositoryTranslationSource(
                repo,
                savedPath,
                savedPath[(savedPath.LastIndexOf('/') + 1)..],
                BaseSha: startedFromSource ? null : file.Sha);

            var save = await _translations.SaveAsync(
                source, language, xml,
                string.IsNullOrWhiteSpace(summary)
                    ? $"{map.Count} of {parsed.Units.Count} strings were updated."
                    : summary!.Trim(),
                ct);

            _logger.LogInformation(
                "An assistant translated {UnitCount} strings in {Path} of {RepoFullName} into {Language}.",
                map.Count, savedPath, repoFullName, language);

            return new TranslationPullRequestResult(
                PullRequest: new RepositoryDeliveryResult(
                    RepositoryFullName: repo.FullName,
                    Branch: save.PullRequest.HeadBranch,
                    BaseBranch: repo.DefaultBranch,
                    PullRequestNumber: save.PullRequest.Number,
                    PullRequestUrl: save.PullRequest.HtmlUrl,
                    IsNewPullRequest: save.IsNewPullRequest),
                IsNewPullRequest: save.IsNewPullRequest,
                SavedPath: savedPath,
                UnitsEdited: map.Count);
        }
        catch (GitHubContentConflictException ex)
        {
            throw new McpException(
                "The file changed in the repository since it was read: " + ex.Message
                + " Read it again with list_translation_files and open_translation_pr, and re-apply the "
                + "translations on top of what is there now.");
        }
        catch (Exception ex) when (IsGitHubRefusal(ex))
        {
            throw Translate(ex);
        }
    }

    /// <summary>
    /// The plain-words "what to do next" behind an empty repository list. The
    /// same four sentences the pages give, so a person hears one answer whether
    /// they asked their assistant or clicked.
    /// </summary>
    private static string? GuidanceFor(GitHubRepositoryReadiness readiness) => readiness switch
    {
        GitHubRepositoryReadiness.NotConfigured =>
            "GitHub is not set up on this server yet, so there are no repositories to offer. "
            + "Ask whoever runs AL Workbench to set it up.",
        GitHubRepositoryReadiness.NotConnected =>
            "Your organisation has not connected a GitHub organisation yet. An administrator connects "
            + "one under Administration -> Repositories.",
        GitHubRepositoryReadiness.LinkNeedsRepair =>
            "Your GitHub account is no longer connected to the workbench. Connect it again on your "
            + "account page under Repository access, then try this again.",
        GitHubRepositoryReadiness.NotLinked =>
            "Connect your own GitHub account first, on your account page under Repository access. "
            + "Everything the workbench does on GitHub is done in your name, so it needs your account to do it.",
        _ => null,
    };

    /// <summary>
    /// The three refusals every GitHub-facing tool shares. Kept as one predicate
    /// and one translator so all five tools word them identically.
    /// </summary>
    private static bool IsGitHubRefusal(Exception ex) =>
        ex is PlanValidationException or GitHubApiException or GitHubAppNotConfiguredException;

    private static McpException Translate(Exception ex) => ex switch
    {
        PlanValidationException validation =>
            new McpException("Validation failed: " + FormatErrors(validation.Errors)),
        GitHubApiException api => new McpException("GitHub refused the request: " + api.Message),
        _ => new McpException(ex.Message),
    };

    private static string FormatErrors(IReadOnlyDictionary<string, string> errors) =>
        string.Join("; ", errors.Select(kv => $"{kv.Key}: {kv.Value}"));
}
