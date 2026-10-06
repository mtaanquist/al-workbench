using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.GitHub;

/// <summary>What "Create repository" produced, for the success state to render.</summary>
/// <param name="Repository">The repository that now exists, including the link the user needs next.</param>
/// <param name="FileCount">How many generated files the repository was filled with.</param>
/// <param name="ArchiveFileName">The name the same workspace would download under.</param>
/// <param name="StandardsFileCount">
/// How many of the organisation's repository standard files were committed
/// alongside the workspace (issue #628). Zero when none are configured.
/// </param>
/// <param name="StandardsWarning">
/// What GitHub refused while applying the standards, in words the person who
/// pressed the button can act on - or null when nothing was refused. The
/// repository exists and is committed by the time this can be set, so it is a
/// warning on a success rather than a failure.
/// </param>
/// <param name="Archive">
/// The very bytes that were committed, as the ZIP. Carried out rather than
/// thrown away because the MCP tool hands the caller both the repository and
/// the files, and generating a second time would mint different extension
/// GUIDs - a download that quietly disagreed with the repository beside it.
/// The web page ignores it.
/// </param>
/// <param name="SolutionId">
/// The solution the repository was registered on (issue #759), or null when
/// registering it failed - in which case <paramref name="SolutionWarning"/>
/// says so.
/// </param>
/// <param name="SolutionName">That solution's name, for the success state to link.</param>
/// <param name="SolutionCreated">
/// True when the solution was created for this customer rather than picked. The
/// success state says "created" or "registered on" accordingly.
/// </param>
/// <param name="SolutionWarning">
/// Why the repository is not on a solution, in words the person who pressed the
/// button can act on - or null when it is. The repository exists and is
/// committed by the time this can be set, so it is a warning on a success
/// rather than a failure, the same shape as <paramref name="StandardsWarning"/>.
/// </param>
/// <param name="Branches">
/// The branches created beside the default branch (<c>test</c> and <c>staging</c>),
/// at the same commit. Empty when none were.
/// </param>
/// <param name="BranchesWarning">Which branches GitHub refused, or null when none were refused.</param>
/// <param name="PipelineNames">
/// The build pipelines added to the solution, one per branch. Empty when the
/// repository is not on a solution, Pipelines is switched off, or the solution
/// has other repositories (see <paramref name="PipelinesWarning"/>).
/// </param>
/// <param name="PipelinesWarning">Why no build pipelines were added, or null.</param>
public sealed record GitHubWorkspaceRepository(
    GitHubRepositorySummary Repository,
    int FileCount,
    string ArchiveFileName,
    byte[] Archive,
    int StandardsFileCount = 0,
    string? StandardsWarning = null,
    int? SolutionId = null,
    string? SolutionName = null,
    bool SolutionCreated = false,
    string? SolutionWarning = null,
    IReadOnlyList<string>? Branches = null,
    string? BranchesWarning = null,
    IReadOnlyList<string>? PipelineNames = null,
    string? PipelinesWarning = null);

/// <summary>
/// Creates a repository in the connected GitHub organisation and puts a freshly
/// generated workspace in it (issue #622).
///
/// <para><strong>Generation is unchanged.</strong> The files are the ones the
/// ZIP is built from - the same in-memory archive, read back entry by entry -
/// so the download and the repository can never drift apart. Nothing is
/// queued: this runs on the request thread inside the button's own loading
/// state.</para>
///
/// <para><strong>The organisation acts, not the person.</strong> Both calls go
/// out on the installation token, which is the credential split the design doc
/// settles: creating a repository is an act of the organisation, and no
/// individual should need <c>admin:org</c> on their own account for the workbench
/// to work. The first commit rides the same token deliberately - the repository
/// is seconds old and was made by the app, so the person who asked for it may
/// have no permissions on it yet, and asking with their token would fail for a
/// reason they could do nothing about. What their own account <em>is</em> used
/// for is the gate: GitHub is asked whether they are a member of the
/// organisation before anything is created, and the commit is credited to them
/// so the history says who asked. This is the mirror image of
/// <see cref="GitHubExtensionDeliveryService"/>, where a write into somebody's
/// <em>existing</em> repository goes out as the user and GitHub enforces their
/// permissions natively.</para>
///
/// <para>See <c>.design/github-integration.md</c>.</para>
/// </summary>
public sealed class GitHubWorkspaceRepositoryService
{
    /// <summary>Error key for problems with the repository name the user typed.</summary>
    public const string NameField = "GitHubRepositoryName";

    /// <summary>Error key for problems with GitHub itself rather than with one field.</summary>
    public const string RepositoryField = "GitHubRepository";

    /// <summary>Error key for problems with the solution the caller chose to register on.</summary>
    public const string SolutionField = "SolutionId";

    /// <summary>
    /// GitHub's own rule for a repository name: letters, digits, and the three
    /// punctuation marks it keeps, up to 100 characters, and never <c>.</c> or
    /// <c>..</c> (which are directory names, not repositories). Anything else
    /// GitHub silently rewrites, and a repository whose name is not the one the
    /// user typed is worse than a refusal.
    ///
    /// <para>The <c>pattern</c> attribute on the New Workspace field is this
    /// same expression, so the browser refuses exactly what the server would -
    /// see CLAUDE.md on mirroring server rules in the form. The hyphen is
    /// escaped for the browser's sake, not .NET's: browsers compile
    /// <c>pattern</c> with the RegExp <c>v</c> flag, under which a bare
    /// <c>-</c> inside a character class is a syntax error - and a pattern that
    /// does not compile is dropped silently, leaving the field claiming that
    /// input the server refuses is fine.</para>
    /// </summary>
    public const string NamePattern = @"^(?!\.{1,2}$)[A-Za-z0-9._\-]{1,100}$";

    private static readonly Regex NameRegex = new(NamePattern, RegexOptions.Compiled);

    /// <summary>
    /// The branches every new repository gets beside its default branch, each
    /// with a build pipeline: work moves through test and staging before it
    /// reaches the default branch.
    /// </summary>
    public static readonly IReadOnlyList<string> WorkingBranches = ["test", "staging"];

    private readonly GenerationService _generation;
    private readonly GitHubRepositoryService _repositories;
    private readonly GitHubConnectionService _connection;
    private readonly GitHubAccessService _access;
    private readonly GitHubAppClient _github;
    private readonly GitHubRepositoryStandardsService _standards;
    private readonly ProjectService _projects;
    private readonly PipelineService _pipelines;
    private readonly OrganizationConfigService _orgConfig;
    private readonly ToolEnablement _tools;
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<GitHubWorkspaceRepositoryService> _logger;

    public GitHubWorkspaceRepositoryService(
        GenerationService generation,
        GitHubRepositoryService repositories,
        GitHubConnectionService connection,
        GitHubAccessService access,
        GitHubAppClient github,
        GitHubRepositoryStandardsService standards,
        ProjectService projects,
        PipelineService pipelines,
        OrganizationConfigService orgConfig,
        ToolEnablement tools,
        AppDbContext db,
        IOrganizationContext orgContext,
        ILogger<GitHubWorkspaceRepositoryService> logger)
    {
        _generation = generation;
        _repositories = repositories;
        _connection = connection;
        _access = access;
        _github = github;
        _standards = standards;
        _projects = projects;
        _pipelines = pipelines;
        _orgConfig = orgConfig;
        _tools = tools;
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; repository creation called outside an authenticated request.");

    /// <summary>
    /// The repository name a customer called <paramref name="workspaceName"/>
    /// suggests: the transliterated name in the organisation's repository
    /// style, which "Jørgensen Møbler" can reach as well as "CRONUS" can. The
    /// style defaults to lowercase kebab-case, which is both a legal GitHub
    /// name and the shape people actually name repositories. Only a suggestion
    /// - the user can type anything the rule above allows.
    /// See <see cref="Generation.CustomerNaming"/>.
    /// </summary>
    public static string SuggestName(string? workspaceName, NamingStyle style = NamingStyle.KebabCase) =>
        CustomerNaming.Apply(workspaceName, style);

    /// <summary>
    /// Generates <paramref name="plan"/> and creates
    /// <paramref name="repositoryName"/> in the connected GitHub organisation
    /// with those files in it.
    ///
    /// <para>Nothing is created until every refusal has been ruled out, so a
    /// plan the generator rejects, a name GitHub would rewrite, or a user who
    /// is not in the organisation never leaves an empty repository behind.
    /// Every refusal is a field-keyed <see cref="PlanValidationException"/> -
    /// on <see cref="NameField"/> when the user can fix it by typing something
    /// else, on <see cref="RepositoryField"/> when they cannot - so a page
    /// renders it beside the right control and an MCP tool reports it as a
    /// validation failure.</para>
    ///
    /// <para>The organisation is never a parameter: it is the one this
    /// workbench organisation connected, so a caller naming a repository cannot
    /// aim it anywhere else.</para>
    ///
    /// <para>The repository is also what registers the customer as a solution
    /// (issue #759): <paramref name="solutionId"/> names one to add it to, and
    /// leaving it out creates one named after the customer. That happens last,
    /// after the repository exists, and a failure there is a warning on the
    /// result rather than an exception - see
    /// <c>.design/customer-naming.md</c>.</para>
    /// </summary>
    /// <param name="solutionId">
    /// An existing solution to register the new repository on, which the caller
    /// must be allowed to manage. Null creates one for the customer.
    /// </param>
    /// <exception cref="PlanValidationException">The plan, the name, the solution, or the caller's access is not good enough.</exception>
    /// <exception cref="GitHubApiException">GitHub refused one of the calls that fill the repository.</exception>
    public async Task<GitHubWorkspaceRepository> CreateAsync(
        ProjectPlan plan, string repositoryName, bool isPrivate, int? solutionId = null,
        CancellationToken ct = default)
    {
        var userId = RequireUserId();

        // The plan's own rules first: a workspace nobody could generate is not
        // worth a round trip to GitHub, and its errors are keyed to the fields
        // that caused them rather than to the repository.
        var planErrors = await _generation.ValidateWorkspaceAsync(plan, ct);
        if (planErrors.Count > 0) throw new PlanValidationException(planErrors.ToDictionary(e => e.Key, e => e.Value));

        var name = (repositoryName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw Refuse(NameField, "Give the repository a name.");
        }
        if (!NameRegex.IsMatch(name))
        {
            throw Refuse(NameField,
                "GitHub repository names can only contain letters, digits, hyphens, underscores and full "
                + "stops, and can be at most 100 characters long.");
        }

        // An organisation that has Solutions switched off gets a repository and
        // nothing else: there is no Solutions surface for a solution to be seen
        // on, so registering one would be inventing work the organisation asked
        // not to have (issue #772). A caller that named one is told why, rather
        // than having the id quietly ignored.
        var solutionsEnabled = await _tools.IsEnabledAsync(ToolKey.Projects, ct);
        if (!solutionsEnabled && solutionId is not null)
        {
            throw Refuse(SolutionField,
                "Solutions are switched off for your organisation, so the repository cannot be "
                + "registered on one.");
        }

        // A solution the caller may not add a repository to is a refusal like
        // any other, so it is ruled out here rather than after a repository
        // exists that has nowhere to go. Same answer whether it is somebody
        // else's or gone: an id they cannot act on.
        if (solutionsEnabled && solutionId is { } chosen && !await _projects.CanManageAsync(chosen, ct))
        {
            throw Refuse(SolutionField,
                "You cannot add a repository to that solution. Pick a different customer, or ask "
                + "whoever owns the solution to add the repository for you.");
        }

        // Why-not first, so the answer names the thing the caller can change.
        var access = await _repositories.GetAccessAsync(ct);
        if (!access.IsReady) throw Refuse(RepositoryField, access.Readiness switch
        {
            GitHubRepositoryReadiness.NotConfigured =>
                "GitHub is not set up on this server yet, so no repository can be created. "
                + "Ask whoever runs AL Workbench to set it up.",
            GitHubRepositoryReadiness.NotConnected =>
                "Your organisation has not connected a GitHub organisation yet, so there is nowhere to "
                + "create this. An administrator connects one under Administration -> Repositories.",
            GitHubRepositoryReadiness.LinkNeedsRepair =>
                "Your GitHub account is no longer connected to the workbench. Connect it again on your "
                + "account page under Repository access, then try this again.",
            _ =>
                "Connect your own GitHub account first, on your account page under Repository access. "
                + "The workbench checks that you are in the GitHub organisation before it creates anything there.",
        });

        var connection = await _connection.GetStatusAsync(ct);
        var orgLogin = connection.OrgLogin!;
        var installationId = connection.InstallationId!.Value;

        if (!await _access.IsOrgMemberAsync(userId, ct))
        {
            throw Refuse(RepositoryField,
                $"GitHub does not list you as a member of {orgLogin}, so the workbench will not create a "
                + "repository there for you. Ask an owner of that organisation to add you, then try again.");
        }

        // The connection records what the installation was granted, so the
        // hopeless case can be refused before a round trip. Only when GitHub
        // actually reported the permissions - an older connection recorded
        // none, and refusing on a blank is refusing on no evidence.
        if (connection.Permissions.Count > 0 && !connection.CanCreateRepositories)
        {
            throw Refuse(RepositoryField, NotPermittedMessage(orgLogin));
        }

        // Generate before creating anything: a generator failure now costs
        // nothing, while the same failure after the repository exists would
        // leave an empty one behind with no way to retry into it.
        var (files, archiveName, archiveBytes) = await BuildFilesAsync(plan, ct);

        var token = await _github.GetInstallationTokenAsync(installationId, ct);
        var created = await _github.CreateOrganizationRepositoryAsync(
            token, orgLogin, name, isPrivate, string.IsNullOrWhiteSpace(plan.Brief) ? null : plan.Brief.Trim(), ct);
        var repository = created.Outcome switch
        {
            GitHubRepositoryCreationOutcome.Created => created.Repository!,
            GitHubRepositoryCreationOutcome.NameTaken => throw Refuse(NameField,
                $"{orgLogin} already has a repository called {name}. Pick a different name."),
            _ => throw Refuse(RepositoryField, NotPermittedMessage(orgLogin)),
        };

        // The organisation's standards ride in the same commit as the generated
        // files, so read them before the fill rather than after it.
        var standards = await _standards.GetAsync(ct);
        var (standardsFileCount, headSha) = await FillAsync(
            token, repository, orgLogin, plan, files, standards.Files, userId, ct);
        // Before the ruleset, for the same reason the files are: a ruleset that
        // covers every branch would otherwise refuse the app its own branches.
        var branches = await CreateWorkingBranchesAsync(token, repository, headSha, ct);
        var rulesetWarning = await ApplyRulesetAsync(
            token, repository, standards.Ruleset is { IsEmpty: false } configured ? configured : null, ct);
        // Last, because a solution with no repository is the orphan the whole
        // ordering exists to avoid. Skipped entirely when the organisation does
        // not use Solutions.
        var solution = solutionsEnabled
            ? await RegisterSolutionAsync(plan, repository, solutionId, ct)
            : default((int? Id, string? Name, bool Created, string? Warning));
        var pipelines = solution.Id is { } registeredOn && headSha is not null
            ? await CreateBuildPipelinesAsync(
                registeredOn, [repository.DefaultBranch, .. branches.Created], ct)
            : default((IReadOnlyList<string> Names, string? Warning));
        await RecordAsync(repository, plan, files.Count, solution.Id, ct);

        _logger.LogInformation(
            "User {UserId} created the repository {RepoFullName} from workspace '{Workspace}' "
            + "(template '{Template}', {FileCount} files, {Visibility}, solution {SolutionId}, "
            + "{BranchCount} extra branches, {PipelineCount} build pipelines).",
            userId, repository.FullName, plan.WorkspaceName, plan.TemplateKey, files.Count,
            isPrivate ? "private" : "public", solution.Id, branches.Created.Count, pipelines.Names?.Count ?? 0);

        return new GitHubWorkspaceRepository(
            repository, files.Count, archiveName, archiveBytes,
            standardsFileCount, rulesetWarning,
            solution.Id, solution.Name, solution.Created, solution.Warning,
            branches.Created, branches.Warning,
            pipelines.Names ?? [], pipelines.Warning);
    }

    /// <summary>
    /// Creates the <see cref="WorkingBranches"/> at the commit the default
    /// branch was just moved on to, so a new repository starts with the branches
    /// a team works through before anything reaches the default branch.
    ///
    /// <para><strong>Nothing here may throw.</strong> The repository exists and
    /// holds the workspace by now, so a branch GitHub refuses is a sentence beside
    /// the success, like a refused ruleset. A branch that is already there counts
    /// as created: it can only have come from someone working in the repository
    /// in the seconds since it was made, and it is the branch they wanted.</para>
    /// </summary>
    private async Task<(IReadOnlyList<string> Created, string? Warning)> CreateWorkingBranchesAsync(
        string token, GitHubRepositorySummary repository, string? headSha, CancellationToken ct)
    {
        if (headSha is null) return ([], null);

        var created = new List<string>();
        var refused = new List<string>();
        foreach (var branch in WorkingBranches)
        {
            if (string.Equals(branch, repository.DefaultBranch, StringComparison.Ordinal)) continue;
            try
            {
                await _github.CreateBranchAsync(token, repository.Owner, repository.Name, branch, headSha, ct);
                created.Add(branch);
            }
            catch (GitHubApiException ex)
            {
                _logger.LogWarning(
                    ex, "GitHub refused to create branch {Branch} on {RepoFullName}.", branch, repository.FullName);
                refused.Add(branch);
            }
        }

        if (created.Count > 0)
        {
            _logger.LogInformation(
                "Created the branches {Branches} on {RepoFullName}.", string.Join(", ", created), repository.FullName);
        }
        return (created, refused.Count == 0 ? null :
            $"The repository is ready, but GitHub would not create the {JoinBranches(refused)} "
            + $"{(refused.Count == 1 ? "branch" : "branches")}. Create {(refused.Count == 1 ? "it" : "them")} "
            + $"on GitHub from {repository.DefaultBranch}.");
    }

    /// <summary>
    /// Gives the solution a build pipeline per branch of the new repository,
    /// building every extension in it, so the first push to any of them can be
    /// built without setting anything up.
    ///
    /// <para>Only when the new repository is the solution's only one. A pipeline
    /// checks its branch out in every repository of the solution, so a
    /// <c>test</c> pipeline on a solution whose older repositories have no
    /// <c>test</c> branch would fail every build. Then the person is told to add
    /// the pipelines themselves. A branch the solution already has a pipeline for
    /// is left alone.</para>
    ///
    /// <para><strong>Nothing here may throw</strong>, for the same reason as the
    /// solution itself. A pipeline that would not save is cleared out of the
    /// context, so it cannot ride along on the audit entry's save.</para>
    /// </summary>
    private async Task<(IReadOnlyList<string> Names, string? Warning)> CreateBuildPipelinesAsync(
        int solutionId, IReadOnlyList<string> branches, CancellationToken ct)
    {
        const string AddThemYourself =
            "Add build pipelines from the solution's Pipelines tab.";
        try
        {
            if (!await _tools.IsEnabledAsync(ToolKey.Pipelines, ct)) return ([], null);

            var repositoryCount = await _db.OeProjectRepositories.AsNoTracking()
                .CountAsync(r => r.ProjectId == solutionId, ct);
            if (repositoryCount != 1)
            {
                return ([],
                    "No build pipelines were added, because the solution has other repositories that may "
                    + "not have the same branches. " + AddThemYourself);
            }

            var existing = await _pipelines.ListPipelinesAsync(solutionId, ct);
            var names = new List<string>();
            foreach (var branch in branches)
            {
                if (existing.Any(p => string.Equals(p.Branch, branch, StringComparison.Ordinal))) continue;
                var id = await _pipelines.CreatePipelineAsync(
                    new PipelineInput(solutionId, CustomName: null, SelectedAppIds: null, Branch: branch), ct);
                names.Add((await _pipelines.GetPipelineAsync(id, ct))!.Name);
            }
            return (names, null);
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            _logger.LogWarning(
                ex, "Could not add build pipelines to solution {SolutionId} for the new repository.", solutionId);
            return ([], "The repository is ready, but its build pipelines could not be added. " + AddThemYourself);
        }
    }

    /// <summary>"test", "test and staging", "a, b and c".</summary>
    private static string JoinBranches(IReadOnlyList<string> branches) =>
        branches.Count == 1
            ? branches[0]
            : string.Join(", ", branches.Take(branches.Count - 1)) + " and " + branches[^1];

    /// <summary>
    /// Registers the new repository on the customer's solution (issue #759):
    /// on the one the caller picked, or on one created for the customer.
    ///
    /// <para><strong>Nothing here may throw.</strong> The repository exists and
    /// holds the workspace by the time this runs, so a solution that would not
    /// save has to come back as a sentence beside a success - the same shape as
    /// a refused ruleset. The likeliest cause is a name another solution already
    /// uses, which is a thing the person can sort out in a moment and not a
    /// reason to lose the repository they just made.</para>
    ///
    /// <para>The clone URL is what gets stored, because that is the shape the
    /// solution editor validates and what the build pipeline clones - the same
    /// row a person typing the repository in by hand would have produced.</para>
    /// </summary>
    private async Task<(int? Id, string? Name, bool Created, string? Warning)> RegisterSolutionAsync(
        ProjectPlan plan, GitHubRepositorySummary repository, int? solutionId, CancellationToken ct)
    {
        var row = new ProjectRepositoryInput(
            RepositoryProvider.GitHub, repository.CloneUrl, repository.Name);
        try
        {
            if (solutionId is { } id)
            {
                return (id, await _projects.AddRepositoryAsync(id, row, ct), false, null);
            }

            var name = plan.WorkspaceName.Trim();
            var created = await _projects.CreateProjectAsync(new ProjectInput(
                name,
                string.IsNullOrWhiteSpace(plan.ShortName) ? null : plan.ShortName!.Trim(),
                await DefaultCountryAsync(ct),
                [row]),
                access: null, ct);
            return (created, name, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "{RepoFullName} was created but could not be registered on a solution.",
                repository.FullName);
            return (null, null, false,
                "The repository is ready, but it is not on a solution yet. Add it from Solutions, on "
                + "that customer's Repositories tab.");
        }
    }

    /// <summary>
    /// The country code a solution created from here compiles against. The New
    /// Workspace form never asks for one, so this is the organisation's own
    /// first import country - the same pre-fill the untracked-repositories panel
    /// offers - and the worldwide base when it has none. Either way it is one
    /// field on the solution's own page, changed in a moment, and a solution
    /// that refused to save over it would be worse than a guess.
    /// </summary>
    private async Task<string> DefaultCountryAsync(CancellationToken ct)
    {
        var settings = (await _orgConfig.GetCurrentAsync(ct)).Settings;
        return OrganizationConfigService.ParseAutoImportCountries(settings.AutoImportCountry)
            .FirstOrDefault() ?? "w1";
    }

    /// <summary>
    /// The generated file set, read straight back out of the archive the
    /// download would have handed over. Reading the ZIP rather than teaching
    /// the generator a second output shape is deliberate: there is then exactly
    /// one description of what a generated workspace contains -
    /// <c>workspace.aldt.toml</c> among them, which is what lets the New
    /// Extension page fill itself in from this repository later.
    ///
    /// <para>The archive nests everything under the workspace folder, because
    /// that folder is what a user unzips. A repository <em>is</em> that folder,
    /// so the prefix comes off: <c>CRONUSCustomer/app.json</c> is committed as
    /// <c>app.json</c>.</para>
    /// </summary>
    private async Task<(List<GitHubCommitFile> Files, string ArchiveName, byte[] Archive)> BuildFilesAsync(
        ProjectPlan plan, CancellationToken ct)
    {
        var archive = await _generation.GenerateWorkspaceAsync(plan, ct);
        await using var stream = archive.Stream;
        stream.Position = 0;

        // The archive is named after the workspace folder it nests everything
        // under, so the prefix to strip comes off the name the generator just
        // used rather than being derived a second time (and possibly in a
        // different style than the organisation has set).
        var root = Path.GetFileNameWithoutExtension(archive.FileName) + "/";
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
                var path = entry.FullName.StartsWith(root, StringComparison.Ordinal)
                    ? entry.FullName[root.Length..]
                    : entry.FullName;
                files.Add(new GitHubCommitFile(path, buffer.ToArray()));
            }
        }
        return (files, archive.FileName, stream.ToArray());
    }

    /// <summary>
    /// Fills the new repository with the generated files and the organisation's
    /// standards, on the default branch, and returns how many standards files
    /// went in and the commit the default branch now points at (null when there
    /// was nothing to commit).
    ///
    /// <para><strong>Two writes, both to the default branch.</strong> The
    /// first is one file through the Contents API, which creates the
    /// repository's initial commit and, with it, the default branch: a
    /// repository created with <c>auto_init: false</c> has no commits, and the
    /// Git Data API refuses every call on one - <c>409 Conflict: Git Repository
    /// is empty.</c> - so blobs and trees have nothing to attach to until
    /// something has committed. Letting GitHub auto-initialise instead would
    /// plant a README nobody generated, which is the property the repository is
    /// created empty to protect. The second is the whole workspace as one
    /// commit on top of the seed, built from a tree that lists everything the
    /// repository should end up with, and the branch is moved on to it.</para>
    ///
    /// <para><strong>Why a plain update of the default branch is fine.</strong>
    /// The GitHub App is on the bypass list of the organisation ruleset that
    /// governs the default branch, so the installation token is exempt from its
    /// pull-request rule. The earlier shape of this flow (issue #811) went to
    /// great lengths to never update that branch - a throwaway seed branch, a
    /// root commit, a ref created outright, a default-branch switch and a
    /// pull-request fallback - and it was the default-branch switch that kept
    /// failing. With the bypass in place none of it is needed, and a rule
    /// violation here means the bypass is missing, which is reported as such
    /// rather than routed around.</para>
    ///
    /// <para>See <c>.design/github-integration.md</c>, "#622 New workspace".</para>
    /// </summary>
    private async Task<(int StandardsFileCount, string? HeadSha)> FillAsync(
        string token, GitHubRepositorySummary repository, string orgLogin, ProjectPlan plan,
        List<GitHubCommitFile> files, IReadOnlyList<GitHubRepositoryStandardFile> standardsFiles,
        int userId, CancellationToken ct)
    {
        var owner = repository.Owner;
        var name = repository.Name;
        var branch = repository.DefaultBranch;

        var contents = Merge(files, standardsFiles);
        if (ChooseSeed(contents) is not { } seed)
        {
            _logger.LogWarning(
                "The '{Template}' template generated no files, so {RepoFullName} was left empty.",
                plan.TemplateKey, repository.FullName);
            return (0, null);
        }

        // Every commit names the same person: without an author the seed is
        // credited to the app, so a new repository would open on a commit by a
        // bot rather than by the consultant who asked for it.
        var author = await ResolveAuthorAsync(userId, ct);

        string seedSha;
        try
        {
            seedSha = (await _github.PutFileAsync(
                token, owner, name, seed.Path, branch,
                "Initial commit", seed.Content, baseSha: null, author: author, ct: ct)).CommitSha;
        }
        catch (GitHubContentConflictException)
        {
            // The write quoted no base sha, so GitHub only refuses it if that
            // path is already there - which in a repository this new means
            // something else got in first. Same answer as a branch that moved.
            throw RaceRefusal(repository);
        }
        catch (GitHubApiException ex) when (ex.IsRuleViolation)
        {
            throw BypassRefusal(repository, orgLogin, ex);
        }

        var blobs = new List<(string Path, string BlobSha)>(contents.Count);
        foreach (var file in contents)
        {
            ct.ThrowIfCancellationRequested();
            blobs.Add((file.Path, await _github.CreateBlobAsync(token, owner, name, file.Content, ct)));
        }

        // Built from nothing and listing everything the repository should end
        // up with, the seeded file included: layering onto the seed's tree
        // would make "exactly what we generated" an accident of what happened
        // to be there rather than a fact about the tree.
        var tree = await _github.CreateTreeAsync(token, owner, name, baseTreeSha: null, blobs, ct);
        var commit = await _github.CreateCommitAsync(
            token, owner, name, $"Add the {plan.WorkspaceName} workspace", tree,
            parentSha: seedSha, author: author, ct: ct);

        try
        {
            if (!await _github.UpdateBranchAsync(token, owner, name, branch, commit, ct))
            {
                // Not a fast-forward from the seed, on a repository created
                // seconds ago: something else got in first.
                throw RaceRefusal(repository);
            }
        }
        catch (GitHubApiException ex) when (ex.IsRuleViolation)
        {
            throw BypassRefusal(repository, orgLogin, ex);
        }

        _logger.LogInformation(
            "Filled {RepoFullName} with {FileCount} file(s) on {Branch}.",
            repository.FullName, contents.Count, branch);
        return (standardsFiles.Count, commit);
    }

    /// <summary>
    /// What to say when the organisation's branch rules refused a write the
    /// workbench expected to be allowed. The one cause is the GitHub App not
    /// being on the ruleset's bypass list, which is a setting an owner of the
    /// GitHub organisation changes - so the message says where that setting
    /// is, names the repository so the person can find and delete it, and
    /// says how to try again.
    /// </summary>
    private PlanValidationException BypassRefusal(
        GitHubRepositorySummary repository, string orgLogin, GitHubApiException cause)
    {
        _logger.LogWarning(
            cause, "The branch rules on {RepoFullName} refused the workspace on {Branch}; "
            + "the GitHub App is not allowed to bypass them.",
            repository.FullName, repository.DefaultBranch);
        return Refuse(RepositoryField,
            $"The branch rules in {orgLogin} would not let AL Workbench commit to "
            + $"{repository.DefaultBranch}. An owner of {orgLogin} can fix that in the GitHub "
            + "organisation's settings, under Repository → Rulesets: open the ruleset that covers "
            + $"{repository.DefaultBranch} and add AL Workbench to its bypass list. "
            + $"{repository.FullName} was created, but the workspace is not in it. Delete "
            + $"{repository.FullName} on GitHub, then press Create repository again - or use the "
            + "Download ZIP button and push the workspace yourself.");
    }

    /// <summary>
    /// The generated files with the organisation's standard files laid over
    /// them (issue #628): a standard at a path the generator also produced
    /// replaces it, so the organisation's standard wins over the template.
    ///
    /// <para>One list rather than two commits: the workspace commit's tree is
    /// built from nothing, so it has to describe the whole repository.</para>
    /// </summary>
    private static List<GitHubCommitFile> Merge(
        List<GitHubCommitFile> generated, IReadOnlyList<GitHubRepositoryStandardFile> standards)
    {
        // Git paths are case-sensitive, so "readme.md" and "README.md" are two
        // files and only an exact match is an override.
        var byPath = new Dictionary<string, GitHubCommitFile>(StringComparer.Ordinal);
        var order = new List<string>(generated.Count + standards.Count);
        foreach (var file in generated)
        {
            if (byPath.TryAdd(file.Path, file)) order.Add(file.Path);
        }
        foreach (var standard in standards)
        {
            if (!byPath.ContainsKey(standard.Path)) order.Add(standard.Path);
            byPath[standard.Path] = new GitHubCommitFile(
                standard.Path, Encoding.UTF8.GetBytes(standard.Content));
        }
        return order.Select(path => byPath[path]).ToList();
    }

    /// <summary>
    /// Puts the organisation's branch ruleset on the new repository (issue
    /// #628), after its files.
    ///
    /// <para><strong>Files first, ruleset second.</strong> A repository ruleset
    /// has no bypass for the app unless an admin adds one, so creating it
    /// before the commit would block the very files it is meant to sit
    /// alongside.</para>
    ///
    /// <para><strong>A refusal is a warning.</strong> By the time this runs the
    /// repository exists and carries the workspace, so failing here would leave
    /// a repository behind with a stack trace over it. GitHub's refusal is
    /// logged and returned as a sentence for the success card instead -
    /// typically the installation not being allowed to change repository
    /// settings.</para>
    /// </summary>
    private async Task<string?> ApplyRulesetAsync(
        string token, GitHubRepositorySummary repository, GitHubRepositoryRuleset? ruleset,
        CancellationToken ct)
    {
        if (ruleset is null) return null;

        try
        {
            await _github.CreateRepositoryRulesetAsync(
                token, repository.Owner, repository.Name, ruleset, ct);
            _logger.LogInformation("Set the branch rules on {RepoFullName}.", repository.FullName);
            return null;
        }
        catch (GitHubApiException ex)
        {
            _logger.LogWarning(
                ex, "GitHub refused the branch rules on {RepoFullName}.", repository.FullName);
            return
                "The repository is ready, but GitHub would not set your branch rules on it. "
                + "AL Workbench may not be allowed to change repository settings in this GitHub "
                + "organisation - an owner of it can allow that. Until then, set the rules on GitHub.";
        }
    }

    /// <summary>
    /// What to say when somebody else wrote to the repository in the seconds
    /// between its creation and the workbench filling it in. Not a case to paper
    /// over: whatever is in there now is not what was generated, and the person
    /// has to look.
    /// </summary>
    private static PlanValidationException RaceRefusal(GitHubRepositorySummary repository) =>
        Refuse(RepositoryField,
            $"Something else pushed to {repository.FullName} while the workbench was filling it in, so "
            + "the generated files were not committed. Open it on GitHub to see what is there.");

    /// <summary>
    /// The one generated file that goes in through the Contents API to give the
    /// repository its first commit.
    ///
    /// <para>Chosen rather than taken at random: this file is what a repository
    /// is left holding if everything after the seed is refused. A README is what
    /// GitHub itself would have put there, and a <c>.gitignore</c> is the next
    /// most ordinary thing to find in an initial commit - but which files a
    /// workspace has is up to the template, so the rule falls back to the first
    /// path in order and never depends on a template opting either of them
    /// in.</para>
    /// </summary>
    private static GitHubCommitFile? ChooseSeed(List<GitHubCommitFile> files) =>
        files.FirstOrDefault(f => f.Path.Equals(PlatformOrganizationFiles.ReadmePath, StringComparison.OrdinalIgnoreCase))
        ?? files.FirstOrDefault(f => f.Path.Equals(PlatformOrganizationFiles.GitignorePath, StringComparison.OrdinalIgnoreCase))
        ?? files.OrderBy(f => f.Path, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>
    /// The person the commit is credited to, so a repository's history names
    /// whoever asked for it rather than the app that made the call. Their
    /// GitHub <c>noreply</c> address is used deliberately: it links the commit
    /// to their account without publishing an address they did not give us.
    /// Null when the link says nothing usable, which is not worth failing over.
    /// </summary>
    private async Task<GitHubCommitAuthor?> ResolveAuthorAsync(int userId, CancellationToken ct)
    {
        var link = await _access.GetLinkStatusAsync(ct);
        if (string.IsNullOrWhiteSpace(link.Login))
        {
            _logger.LogInformation("User {UserId} has no GitHub login on file; the commit is the app's.", userId);
            return null;
        }
        var email = link.GitHubUserId is { } id
            ? $"{id}+{link.Login}@users.noreply.github.com"
            : $"{link.Login}@users.noreply.github.com";
        return new GitHubCommitAuthor(link.Login!, email);
    }

    /// <summary>
    /// Records the repository in the audit log, so "who created this from the
    /// workbench" has an answer months later.
    ///
    /// <para>Written by hand rather than by <c>AuditInterceptor</c> because
    /// nothing of ours changed - the row this describes lives on GitHub, which
    /// is why the repository's full name carries the identity: an id from
    /// GitHub would read as a primary key of ours.</para>
    ///
    /// <para><c>EntityId</c> is the solution the repository was registered on
    /// (issue #759), which is the one row of ours this act does touch, and zero
    /// when registering it failed. Never an id from GitHub.</para>
    /// </summary>
    private async Task RecordAsync(
        GitHubRepositorySummary repository, ProjectPlan plan, int fileCount, int? solutionId,
        CancellationToken ct)
    {
        _db.AuditLog.Add(new AuditLogEntry
        {
            Timestamp = DateTime.UtcNow,
            ChangedBy = await AuditActor.ResolveAsync(_db, _orgContext.CurrentUserId, ct),
            ChangedByUserId = _orgContext.CurrentUserId,
            OrganizationId = _orgContext.CurrentOrganizationId,
            EntityType = AuditEntityType.GitHubRepository,
            EntityId = solutionId ?? 0,
            Action = AuditAction.Created,
            EntityName = repository.FullName,
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Recorded {RepoFullName} in the audit log for workspace '{Workspace}' "
            + "({FileCount} files, solution {SolutionId}).",
            repository.FullName, plan.WorkspaceName, fileCount, solutionId);
    }

    /// <summary>
    /// One sentence for the missing grant, said the same way whether it was
    /// spotted from the recorded permissions or by GitHub refusing the call.
    /// Deliberately not "administration:write" - the person reading it has to
    /// ask somebody for something, not quote a permission name.
    /// </summary>
    private static string NotPermittedMessage(string orgLogin) =>
        $"AL Workbench has not been allowed to create repositories in {orgLogin}. An owner of that "
        + "GitHub organisation can allow it, and then this will work.";

    private static PlanValidationException Refuse(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}
