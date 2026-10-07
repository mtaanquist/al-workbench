using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.GitHub;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// CRUD over <see cref="OeProject"/> and its <see cref="OeProjectRepository"/>
/// children — the admin surface that defines what the project-build pipeline
/// clones and compiles. Org-scoped via the EF query filter; mutations run inside
/// an authenticated request (<see cref="RequireOrganizationId"/> throws
/// otherwise). Validation throws <see cref="PlanValidationException"/> with
/// field-keyed errors so the form renders them inline. See
/// <c>.design/object-explorer-project-builds.md</c>.
/// </summary>
public sealed class ProjectService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly ProjectDiscoveryService _discovery;
    private readonly ILogger<ProjectService> _logger;

    public ProjectService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        ProjectDiscoveryService discovery,
        ILogger<ProjectService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _discovery = discovery;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; project mutation called outside an authenticated request.");

    /// <summary>
    /// True when the current user may manage <paramref name="projectId"/> (owner or
    /// org Admin / SiteAdmin) — for the UI to hide Build/Add/Delete affordances.
    /// Returns false when the project no longer exists.
    /// </summary>
    public async Task<bool> CanManageAsync(int projectId, CancellationToken ct = default)
    {
        var owner = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId && c.DeletedAt == null)
            .Select(c => new { c.CreatedByUserId })
            .FirstOrDefaultAsync(ct);
        return owner is not null && await _access.CanManageAsync(projectId, owner.CreatedByUserId, ct);
    }

    /// <summary>
    /// Whether the current user may change who can see the project, and delete it -
    /// the solution's own governance, which is narrower than managing it and is what
    /// <see cref="SetAccessAsync"/> enforces. For the page that decides whether to
    /// draw the Access tab at all. Returns false when the project no longer exists.
    /// </summary>
    public async Task<bool> CanChangeAccessAsync(int projectId, CancellationToken ct = default)
    {
        var owner = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId && c.DeletedAt == null)
            .Select(c => new { c.CreatedByUserId })
            .FirstOrDefaultAsync(ct);
        return owner is not null && await _access.CanDeleteAsync(owner.CreatedByUserId, ct);
    }

    /// <summary>
    /// Active (non-deleted) projects the current user may see, repositories
    /// included, ordered by name. Private projects the caller has no grant on are
    /// left out entirely — this feeds project <em>pickers</em> (new pipeline, new
    /// release pipeline), where a name you can't act on is only in the way. The
    /// locked-name row lives in <see cref="ArtifactService.ListProjectsAsync"/>,
    /// which is what <c>/projects</c> renders.
    /// </summary>
    public async Task<List<OeProject>> ListProjectsAsync(CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);
        return await _db.OeProjects
            .AsNoTracking()
            .Where(c => c.DeletedAt == null)
            .Where(ProjectAccess.VisibleProjectPredicate(snapshot))
            .Include(c => c.Repositories)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The solutions the current user may see, as the generator's Solution picker
    /// needs them: the name to search on, the short name and tenant id it pre-fills
    /// from, and the repositories it lists so a second workspace for the same
    /// customer is a visible choice. Private solutions the caller has no grant on
    /// are left out entirely, the same rule <c>list_solutions</c> applies to an
    /// agent. See <c>.design/customer-naming.md</c>.
    /// </summary>
    /// <param name="search">Optional substring matched against the name; blank returns all.</param>
    /// <param name="limit">
    /// Most rows to return. The picker shows a short list and asks the user to
    /// narrow it, so it reads one more than it shows and never pulls a whole
    /// organisation's solutions over a keystroke.
    /// </param>
    public async Task<List<SolutionOption>> ListSolutionOptionsAsync(
        string? search = null, int? limit = null, CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);
        var query = _db.OeProjects
            .AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Where(ProjectAccess.VisibleProjectPredicate(snapshot));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{term}%"));
        }

        if (limit is > 0) query = query.OrderBy(p => p.Name).Take(limit.Value);

        return await query
            .OrderBy(p => p.Name)
            .Select(p => new SolutionOption(
                p.Id,
                p.Name,
                p.ShortName,
                p.BcTenantId,
                p.Repositories
                    .OrderBy(r => r.DisplayName)
                    .Select(r => new SolutionRepositoryOption(r.DisplayName, r.Url))
                    .ToList()))
            .ToListAsync(ct);
    }

    /// <summary>
    /// A single active project with its repositories, or null when not found in this
    /// org. Throws <see cref="ProjectAccessDeniedException"/> when the project is
    /// Private and the caller has no grant on it; the detail page renders that as
    /// its not-found state.
    /// </summary>
    public async Task<OeProject?> GetProjectAsync(int id, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(id, ct);
        return await _db.OeProjects
            .AsNoTracking()
            .Where(c => c.Id == id && c.DeletedAt == null)
            .Include(c => c.Repositories)
            .Include(c => c.AutoUpdatePullRequestsByUser)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The releases this project's builds produced, newest first — linked via the
    /// import job's <see cref="OeImportJob.ProjectId"/> (a project Release carries
    /// no FK back to the project, only a name). Drives the project detail page's
    /// build history.
    /// </summary>
    public async Task<List<ProjectReleaseRow>> ListProjectReleasesAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);

        var releaseIds = await _db.OeImportJobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId)
            .Select(j => j.ReleaseId)
            .Distinct()
            .ToListAsync(ct);
        if (releaseIds.Count == 0) return new List<ProjectReleaseRow>();

        return await _db.OeReleases.AsNoTracking()
            .Where(r => releaseIds.Contains(r.Id))
            .OrderByDescending(r => r.ImportedAt)
            .Select(r => new ProjectReleaseRow(r.Id, r.Label, r.Status, r.BcVersion, r.ImportedAt, r.DeletedAt))
            .ToListAsync(ct);
    }

    /// <summary>Creates a project and its repositories. Returns the new id.</summary>
    public async Task<int> CreateProjectAsync(
        ProjectInput input, ProjectAccessSettings? access = null, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var (name, shortName, country, repos, slug) = await ValidateAsync(input, existingId: null, orgId, ct);
        await EnsureRepositoriesUnclaimedAsync(repos, projectId: null, existing: null, ct);

        // The level is chosen on the create form, and it is written in the same
        // SaveChanges as the solution itself. Not a create followed by SetAccessAsync:
        // that pair can half-succeed, and what it leaves behind is a solution at the
        // level nobody chose - which, since a Public solution is managed by everyone in
        // the organisation, is the wrong way round to fail. See
        // .design/teams-and-visibility.md.
        var (visibility, teamIds) = await ValidateAccessAsync(access, ct);

        var now = DateTime.UtcNow;
        var project = new OeProject
        {
            OrganizationId = orgId,
            Name = name,
            ShortName = shortName,
            Slug = slug ?? await FreeSlugAsync(SolutionSlug.Derive(shortName ?? name), existingId: null, ct),
            DefaultArtifactCountry = country,
            // The creator owns the project: they (or an org Admin) manage repos,
            // settings, builds, and deletion. See .design/artifacts.md.
            CreatedByUserId = _orgContext.CurrentUserId,
            Visibility = visibility,
            CreatedAt = now,
            UpdatedAt = now,
            Repositories = repos.Select(r => new OeProjectRepository
            {
                OrganizationId = orgId,
                Provider = r.Provider,
                Url = r.Url,
                DisplayName = r.DisplayName,
            }).ToList(),
            Teams = teamIds.Select(teamId => new OeProjectTeam
            {
                OrganizationId = orgId,
                TeamId = teamId,
                CreatedAt = now,
            }).ToList(),
        };
        _db.OeProjects.Add(project);
        try
        {
            await SaveTranslatingNameClashAsync(ct);
        }
        catch (PlanValidationException ex) when (slug is null && ex.Errors.ContainsKey("Slug"))
        {
            // A concurrent create took the derived slug between the read and the write.
            // The create form has no field to show that on, and nobody asked for this
            // slug, so pick the next free one and save again.
            project.Slug = await FreeSlugAsync(SolutionSlug.Derive(shortName ?? name), existingId: null, ct);
            await SaveTranslatingNameClashAsync(ct);
        }

        _logger.LogInformation("Created project {ProjectId} ({Name}) with {RepoCount} repo(s) at {Visibility} with {TeamCount} team(s) for org {OrgId}.",
            project.Id, name, project.Repositories.Count, visibility, teamIds.Count, orgId);

        // Warm the discovered-extensions cache in the background so the first
        // pipeline editor open is instant. Best-effort — a discovery enqueue
        // failure must never sink the create.
        if (project.Repositories.Count > 0) await WarmDiscoveryAsync(project.Id, ct);
        return project.Id;
    }

    /// <summary>
    /// Updates a project's name/country and reconciles its repository set with the
    /// posted one (the form owns the whole list).
    ///
    /// <para>Reconciled in place rather than replaced wholesale: a pipeline that
    /// publishes to a repository points at that <c>oe_project_repositories</c> row
    /// by id, and the foreign key is <c>ON DELETE SET NULL</c>. Dropping and
    /// re-adding every row would therefore quietly unset the Release repository of
    /// every pipeline in the solution each time somebody renamed it. Only a
    /// repository the user actually removed is deleted, and that one nulling the
    /// pipelines is the intended answer.</para>
    /// </summary>
    public async Task UpdateProjectAsync(int id, ProjectInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var (name, shortName, country, repos, slug) = await ValidateAsync(input, existingId: id, orgId, ct);

        var project = await _db.OeProjects
            .Include(c => c.Repositories)
            .FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct)
            ?? throw Validation("Name", "This project no longer exists.");

        // Only the owner, an org Admin, or an assigned team may edit settings /
        // change the repo set.
        await _access.EnsureCanManageAsync(project.Id, project.CreatedByUserId, ct);
        await EnsureRepositoriesUnclaimedAsync(repos, project.Id, project.Repositories, ct);

        project.Name = name;
        project.ShortName = shortName;
        // Null keeps the address the solution already has: a rename must not break
        // every link to it. Blank asks for a fresh one from the (new) name.
        if (slug is not null) project.Slug = slug;
        else if (input.Slug is not null) project.Slug = await FreeSlugAsync(SolutionSlug.Derive(shortName ?? name), existingId: id, ct);
        project.DefaultArtifactCountry = country;
        project.UpdatedAt = DateTime.UtcNow;
        var toggled = false;
        if (input.AutoUpdatePullRequests is { } auto && auto != project.AutoUpdatePullRequests)
        {
            // Turning it on makes the saver the person the pull requests are opened
            // as, the same rule as building on push (#1104).
            project.AutoUpdatePullRequests = auto;
            project.AutoUpdatePullRequestsByUserId = auto ? _orgContext.CurrentUserId : null;
            project.AutoUpdatePullRequestsBlocked = null;
            toggled = true;
        }

        var gitHubBefore = GitHubRepositoryKeys(project);
        ReconcileRepositories(project, repos, orgId);
        // Only a repository that was not there before - added, or its URL changed - is
        // somewhere the previous person never agreed to write. Removing one is not.
        if (!toggled && GitHubRepositoryKeys(project).Except(gitHubBefore).Any())
        {
            await FollowRepositoryChangeAsync(project, ct);
        }

        await SaveTranslatingNameClashAsync(ct);
        _logger.LogInformation("Updated project {ProjectId} ({Name}); now {RepoCount} repo(s).",
            project.Id, name, project.Repositories.Count);

        // The repo set may have changed — re-warm the discovery cache in the
        // background so the pipeline editor reflects it. Best-effort.
        if (project.Repositories.Count > 0) await WarmDiscoveryAsync(project.Id, ct);
    }

    /// <summary>
    /// Makes the acting person the one automatic update pull requests are opened as,
    /// and clears whatever held the last run up - "Resume with my GitHub account" on the
    /// Repositories tab (#1104). Nothing happens when the setting is off.
    /// </summary>
    /// <exception cref="PlanValidationException">The solution is gone, or the caller has no GitHub account connected.</exception>
    /// <exception cref="ProjectAccessDeniedException">The caller may not manage this solution.</exception>
    public async Task ResumeAutoUpdatePullRequestsAsync(int projectId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var userId = _orgContext.CurrentUserId
            ?? throw new InvalidOperationException("No user in scope; resuming automatic update pull requests needs a signed-in person.");
        var project = await _db.OeProjects
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw Validation("Name", "This solution no longer exists.");
        await _access.EnsureCanManageAsync(project.Id, project.CreatedByUserId, ct);
        if (!project.AutoUpdatePullRequests) return;
        // Taking it over without a GitHub account would only swap one reason it has
        // stopped for another, and clear the warning until the night says so again.
        if (!await HasGitHubAccountAsync(userId, ct))
        {
            throw Validation("AutoUpdatePullRequests", DependencyDriftService.NotLinkedRefusal);
        }

        project.AutoUpdatePullRequestsByUserId = userId;
        project.AutoUpdatePullRequestsBlocked = null;
        project.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "User {UserId} took over automatic update pull requests for solution {ProjectId}.", userId, projectId);
    }

    /// <summary>
    /// Adds one repository to a solution, leaving every other repository on it
    /// alone. Returns the solution's name, for the success state that says where
    /// the repository was registered.
    ///
    /// <para>Narrow on purpose. <see cref="UpdateProjectAsync"/> owns the whole
    /// repository list because the editor posts the whole list; a caller holding
    /// one repository it has just created - "Create repository" on New Workspace
    /// (issue #759) - would have to read the others back and post them again to
    /// use it, and anything edited in between would be overwritten by a list
    /// that never knew about it.</para>
    ///
    /// <para>A repository the solution already has is a no-op rather than a
    /// duplicate row: identity is provider plus normalised URL, the same rule
    /// <see cref="ReconcileRepositories"/> applies, so a retry after a
    /// half-failed create settles instead of piling up.</para>
    /// </summary>
    /// <exception cref="PlanValidationException">The solution is gone, or the URL is not one that provider serves.</exception>
    /// <exception cref="ProjectAccessDeniedException">The caller may not manage this solution.</exception>
    public async Task<string> AddRepositoryAsync(
        int projectId, ProjectRepositoryInput repository, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        ArgumentNullException.ThrowIfNull(repository);

        var project = await _db.OeProjects
            .Include(c => c.Repositories)
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw Validation("Name", "This solution no longer exists.");

        await _access.EnsureCanManageAsync(project.Id, project.CreatedByUserId, ct);

        var url = (repository.Url ?? string.Empty).Trim();
        if (url.Length == 0 || !RepositoryLinks.IsValidUrl(repository.Provider, url))
        {
            throw Validation("Url", repository.Provider == RepositoryProvider.AzureDevOps
                ? "Use an https Azure DevOps URL (dev.azure.com or *.visualstudio.com)."
                : "Use an https github.com URL.");
        }
        var display = (repository.DisplayName ?? string.Empty).Trim();
        if (display.Length == 0)
        {
            display = url.TrimEnd('/').Split('/').LastOrDefault()?.Replace(".git", "") ?? url;
        }

        var normalised = GitHubPullRequestBuildWorker.NormaliseRepositoryUrl(url);
        if (project.Repositories.Any(r => r.Provider == repository.Provider
                && GitHubPullRequestBuildWorker.NormaliseRepositoryUrl(r.Url) == normalised))
        {
            _logger.LogInformation(
                "Project {ProjectId} ({Name}) already has {Url}; nothing added.", project.Id, project.Name, url);
            return project.Name;
        }

        var claimed = await RepositoryClaimErrorsAsync(
            [new ProjectRepositoryInput(repository.Provider, url, display)], project.Id, existing: null, ct);
        if (claimed.TryGetValue(0, out var claimedBy)) throw Validation("Url", claimedBy);

        project.Repositories.Add(new OeProjectRepository
        {
            OrganizationId = orgId,
            ProjectId = project.Id,
            Provider = repository.Provider,
            Url = url,
            DisplayName = display,
        });
        project.UpdatedAt = DateTime.UtcNow;
        if (repository.Provider == RepositoryProvider.GitHub) await FollowRepositoryChangeAsync(project, ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Added {Url} to project {ProjectId} ({Name}).", url, project.Id, project.Name);

        // The same warm the editor's save does, for the same reason: the
        // pipeline editor should know this repository's extensions without
        // anybody asking it to look.
        await WarmDiscoveryAsync(project.Id, ct);
        return project.Name;
    }

    /// <summary>
    /// Refuses a posted repository that another active solution in this organisation
    /// already has (#1200), keyed <c>Repositories[i].Url</c> so the editor shows it under
    /// the row. See <see cref="RepositoryClaimErrorsAsync"/>.
    /// </summary>
    private async Task EnsureRepositoriesUnclaimedAsync(
        IReadOnlyList<ProjectRepositoryInput> repos, int? projectId,
        ICollection<OeProjectRepository>? existing, CancellationToken ct)
    {
        var claimed = await RepositoryClaimErrorsAsync(repos, projectId, existing, ct);
        if (claimed.Count > 0)
        {
            throw new PlanValidationException(
                claimed.ToDictionary(c => $"Repositories[{c.Key}].Url", c => c.Value));
        }
    }

    /// <summary>
    /// The refusal for each of <paramref name="repos"/> (by index) that another active
    /// solution in this organisation already has: several features resolve a repository
    /// to one solution, so a second would be picked arbitrarily (#1200). Only a repository
    /// this save adds - new, or its URL changed - is checked: <paramref name="existing"/>
    /// is the solution's current set, and a pair of solutions that already shared a
    /// repository before the rule existed keeps saving until somebody touches that row.
    /// </summary>
    private async Task<Dictionary<int, string>> RepositoryClaimErrorsAsync(
        IReadOnlyList<ProjectRepositoryInput> repos, int? projectId,
        ICollection<OeProjectRepository>? existing, CancellationToken ct)
    {
        static string Key(RepositoryProvider provider, string url) =>
            $"{provider}|{GitHubPullRequestBuildWorker.NormaliseRepositoryUrl(url)}";

        var had = (existing ?? []).Select(r => Key(r.Provider, r.Url)).ToHashSet(StringComparer.Ordinal);
        var added = repos
            .Select((r, i) => (Index: i, Key: Key(r.Provider, r.Url)))
            .Where(r => !had.Contains(r.Key))
            .ToList();
        var errors = new Dictionary<int, string>();
        if (added.Count == 0) return errors;

        var owners = await RepositoryOwnersAsync(projectId, ct);
        foreach (var (index, key) in added)
        {
            if (owners.TryGetValue(key, out var owner)) errors[index] = RepositoryClaimedMessage(owner);
        }
        return errors;
    }

    /// <summary>
    /// Every repository on an active solution other than <paramref name="projectId"/>,
    /// keyed provider plus normalised URL, mapped to how the refusal names its solution:
    /// its name when the caller may see it, null when it is private to others, so the
    /// message does not give a private solution's name away. Org-scoped by the ambient
    /// query filter; the URL is normalised in memory, which is why every row is read -
    /// an organisation has tens of repositories, not thousands.
    /// </summary>
    private async Task<Dictionary<string, string?>> RepositoryOwnersAsync(int? projectId, CancellationToken ct)
    {
        var rows = await (
                from r in _db.OeProjectRepositories.AsNoTracking()
                join p in _db.OeProjects.AsNoTracking() on r.ProjectId equals p.Id
                where p.DeletedAt == null && p.Id != (projectId ?? 0)
                orderby p.Id
                select new { r.Provider, r.Url, ProjectId = p.Id, p.Name })
            .ToListAsync(ct);
        if (rows.Count == 0) return new();

        var snapshot = await _access.GetSnapshotAsync(ct);
        var ids = rows.Select(r => r.ProjectId).Distinct().ToList();
        var visible = (await _db.OeProjects.AsNoTracking()
                .Where(ProjectAccess.VisibleProjectPredicate(snapshot))
                .Where(p => ids.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync(ct))
            .ToHashSet();

        var owners = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            owners.TryAdd(
                $"{row.Provider}|{GitHubPullRequestBuildWorker.NormaliseRepositoryUrl(row.Url)}",
                visible.Contains(row.ProjectId) ? row.Name : null);
        }
        return owners;
    }

    private static string RepositoryClaimedMessage(string? owner) =>
        owner is null
            ? "This repository already belongs to another solution. A repository can only belong to one solution."
            : $"This repository already belongs to the solution {owner}. A repository can only belong to one solution.";

    /// <summary>The solution's GitHub repositories, keyed the way <see cref="ReconcileRepositories"/> matches them.</summary>
    private static HashSet<string> GitHubRepositoryKeys(OeProject project) =>
        project.Repositories
            .Where(r => r.Provider == RepositoryProvider.GitHub)
            .Select(r => GitHubPullRequestBuildWorker.NormaliseRepositoryUrl(r.Url))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Makes the acting person the one automatic update pull requests are opened as,
    /// when they add a GitHub repository to the solution (or change one's address) while
    /// it is on - the "whoever
    /// last saved" rule building on push follows (#1101). Otherwise anyone who manages a
    /// Public solution could add a repository they cannot write to and have the nightly
    /// run open pull requests there with somebody else's GitHub account, which the
    /// button, running as whoever presses it, would refuse them.
    ///
    /// <para>When the acting person has no GitHub account connected, they still take it
    /// over, and the solution shows it as stopped straight away, with "Resume with my
    /// GitHub account" for anyone who manages it - the same pause the nightly run would
    /// reach. Nothing is opened as the previous person in between. See
    /// <c>.design/github-integration-phase2.md</c>.</para>
    /// </summary>
    private async Task FollowRepositoryChangeAsync(OeProject project, CancellationToken ct)
    {
        if (!project.AutoUpdatePullRequests) return;
        if (_orgContext.CurrentUserId is not { } userId || userId == project.AutoUpdatePullRequestsByUserId) return;

        var linked = await HasGitHubAccountAsync(userId, ct);
        project.AutoUpdatePullRequestsByUserId = userId;
        project.AutoUpdatePullRequestsBlocked = linked ? null : DependencyDriftService.AutomaticNotLinkedMessage;
        _logger.LogInformation(
            "User {UserId} changed the repositories of solution {ProjectId}, so its automatic update pull requests are now opened as them (GitHub account connected: {Linked}).",
            userId, project.Id, linked);
    }

    /// <summary>Whether <paramref name="userId"/> has connected a GitHub account - read from the database, without asking GitHub.</summary>
    private Task<bool> HasGitHubAccountAsync(int userId, CancellationToken ct) =>
        _db.UserExternalLogins.AsNoTracking()
            .AnyAsync(l => l.UserId == userId && l.Provider == GitHubAccessService.ProviderName, ct);

    /// <summary>
    /// Brings <paramref name="project"/>'s repository rows in line with the posted
    /// set, keeping the id of every repository that is still there.
    ///
    /// <para>Identity is provider plus normalised URL - the same repository is
    /// typed with and without the <c>.git</c> suffix and in either case, and a row
    /// that only had its display name edited must keep its id. Anything the posted
    /// set no longer names is deleted, which is the one case where a pipeline
    /// losing its Release repository is what the user asked for.</para>
    /// </summary>
    private void ReconcileRepositories(OeProject project, IReadOnlyList<ProjectRepositoryInput> repos, int orgId)
    {
        static string Key(RepositoryProvider provider, string url) =>
            $"{provider}|{GitHubPullRequestBuildWorker.NormaliseRepositoryUrl(url)}";

        var existing = project.Repositories.ToList();
        var kept = new HashSet<int>();
        var wanted = new List<OeProjectRepository>(repos.Count);

        foreach (var repo in repos)
        {
            var key = Key(repo.Provider, repo.Url);
            var match = existing.FirstOrDefault(e => !kept.Contains(e.Id) && Key(e.Provider, e.Url) == key);
            if (match is not null)
            {
                kept.Add(match.Id);
                // The URL is re-stamped so a cosmetic re-spelling (a .git suffix
                // dropped, say) is saved, without the row changing identity.
                match.Url = repo.Url;
                match.DisplayName = repo.DisplayName;
                wanted.Add(match);
                continue;
            }

            var added = new OeProjectRepository
            {
                OrganizationId = orgId,
                ProjectId = project.Id,
                Provider = repo.Provider,
                Url = repo.Url,
                DisplayName = repo.DisplayName,
            };
            project.Repositories.Add(added);
            wanted.Add(added);
        }

        var removed = existing.Where(e => !kept.Contains(e.Id)).ToList();
        if (removed.Count > 0)
        {
            _db.OeProjectRepositories.RemoveRange(removed);
            foreach (var row in removed) project.Repositories.Remove(row);
        }

        _logger.LogInformation(
            "Reconciled repositories on project {ProjectId}: {KeptCount} kept, {AddedCount} added, {RemovedCount} removed.",
            project.Id, kept.Count, wanted.Count - kept.Count, removed.Count);
    }

    /// <summary>
    /// Best-effort enqueue of a background discovery refresh after a repo change.
    /// Swallows failures (the access check already passed for this caller) so a
    /// cache warm never fails the project save it follows.
    /// </summary>
    private async Task WarmDiscoveryAsync(int projectId, CancellationToken ct)
    {
        try
        {
            await _discovery.RequestDiscoveryAsync(projectId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enqueue extension discovery for project {ProjectId} after a repo change.", projectId);
        }
    }

    // ── Access (visibility + assigned teams) ────────────────────────────

    /// <summary>
    /// The project's current access setting: its visibility level and the ids of the
    /// teams assigned to it. Readable by anyone who may see the project.
    /// </summary>
    public async Task<ProjectAccessSettings> GetAccessAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);

        var visibility = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Select(p => (ProjectVisibility?)p.Visibility)
            .FirstOrDefaultAsync(ct);

        var teamIds = await _db.OeProjectTeams.AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .Select(t => t.TeamId)
            .ToListAsync(ct);

        return new ProjectAccessSettings(visibility ?? ProjectVisibility.Public, teamIds);
    }

    /// <summary>
    /// Sets a project's visibility and the teams assigned to it, in one write.
    /// </summary>
    /// <remarks>
    /// <para>The two halves move together on purpose. The invariant is
    /// <c>Visibility != Public</c> ⇔ at least one team assigned, and two independent
    /// writes could interleave into a Private project with no team — a project
    /// nobody but an admin could reach. So a non-Public level with no teams is
    /// refused (field key <c>Teams</c>), and so is Public <em>with</em> teams: an
    /// assignment that grants nothing is a setting the user will misread later.</para>
    /// <para>A team id from another organisation is simply not found (the query
    /// filter), which is the right answer and the right error.</para>
    /// <para>Restricted to whoever may manage the project. See
    /// <c>.design/teams-and-visibility.md</c>.</para>
    /// </remarks>
    public async Task SetAccessAsync(
        int projectId, ProjectVisibility visibility, IReadOnlyList<int> teamIds, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        ArgumentNullException.ThrowIfNull(teamIds);

        var project = await _db.OeProjects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.DeletedAt == null, ct)
            ?? throw Validation("Visibility", "This project no longer exists.");

        // Changing who may see a solution is governance, not work on it: the same set
        // as deleting it, and for the same reason a team grant never included delete.
        // Under the old model manage was owner-and-admins here anyway; now that a
        // Public solution is managed by everyone, leaving this on manage would let
        // anybody re-govern it - lock a shared solution to a team of their own, or
        // open a narrowed one back up. See .design/teams-and-visibility.md.
        if (!await _access.CanDeleteAsync(project.CreatedByUserId, ct))
        {
            throw new ProjectAccessDeniedException(
                "Only the solution's owner or an organisation admin can change who may see it.");
        }

        var (_, wanted) = await ValidateAccessAsync(new ProjectAccessSettings(visibility, teamIds), ct);

        var existing = await _db.OeProjectTeams
            .Where(t => t.ProjectId == projectId)
            .ToListAsync(ct);

        // Diff rather than replace wholesale: an unchanged assignment shouldn't
        // churn the audit log with a delete plus an identical insert.
        var removed = existing.Where(t => !wanted.Contains(t.TeamId)).ToList();
        if (removed.Count > 0) _db.OeProjectTeams.RemoveRange(removed);

        var now = DateTime.UtcNow;
        foreach (var teamId in wanted.Where(id => existing.All(t => t.TeamId != id)))
        {
            _db.OeProjectTeams.Add(new OeProjectTeam
            {
                OrganizationId = orgId,
                ProjectId = projectId,
                TeamId = teamId,
                CreatedAt = now,
            });
        }

        project.Visibility = visibility;
        project.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Set project {ProjectId} visibility to {Visibility} with {TeamCount} team(s).",
            projectId, visibility, wanted.Count);
    }

    /// <summary>
    /// The invariant, in one place so creating a solution and re-levelling one cannot
    /// drift: <c>Visibility != Public</c> holds exactly when at least one team is
    /// assigned, and every team named has to exist in this organisation. A null
    /// <paramref name="access"/> means Public with no teams, which is what a caller
    /// that doesn't care about the level gets.
    /// </summary>
    private async Task<(ProjectVisibility Visibility, List<int> TeamIds)> ValidateAccessAsync(
        ProjectAccessSettings? access, CancellationToken ct)
    {
        var visibility = access?.Visibility ?? ProjectVisibility.Public;
        var wanted = (access?.TeamIds ?? Array.Empty<int>()).Distinct().ToList();

        if (visibility == ProjectVisibility.Public && wanted.Count > 0)
        {
            throw Validation("Teams",
                "A public project is open to everyone, so it can't have teams. Remove the teams, or pick a different visibility.");
        }
        if (visibility != ProjectVisibility.Public && wanted.Count == 0)
        {
            throw Validation("Teams", "Pick at least one team that keeps access to this project.");
        }

        if (wanted.Count > 0)
        {
            var known = await _db.Teams.AsNoTracking()
                .Where(t => wanted.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync(ct);
            if (known.Count != wanted.Count)
            {
                throw Validation("Teams", "One of those teams no longer exists. Reload the page and try again.");
            }
        }

        return (visibility, wanted);
    }

    // ── Supplemental symbols (manual-symbols recovery) ──────────────────

    /// <summary>
    /// The operator-supplied dependency symbols stored for a project, newest
    /// first. Read-only projection (no blob) for the admin list. See
    /// <c>.design/object-explorer-project-builds.md</c> ("Manual-symbols recovery").
    /// </summary>
    public async Task<List<ProjectSymbolRow>> ListSupplementalSymbolsAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);

        return await _db.OeProjectSymbols.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new ProjectSymbolRow(s.Id, s.FileName, s.ContentLength, s.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Stores one or more uploaded <c>.app</c> dependency symbols for a project,
    /// replacing any existing entry with the same file name (so re-uploading a
    /// corrected package overwrites rather than duplicates). Returns the number
    /// of packages saved. Validates that every upload is a non-empty <c>.app</c>;
    /// throws <see cref="PlanValidationException"/> (field key <c>Symbols</c>)
    /// otherwise so the manage page renders the error inline.
    /// </summary>
    public async Task<int> AddSupplementalSymbolsAsync(
        int projectId, IReadOnlyList<SupplementalSymbolUpload> uploads, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        ArgumentNullException.ThrowIfNull(uploads);

        var project = await _db.OeProjects.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["Symbols"] = "This project no longer exists." });

        await _access.EnsureCanManageAsync(projectId, project.CreatedByUserId, ct);

        if (uploads.Count == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Symbols"] = "Choose at least one .app symbol package to upload.",
            });
        }

        // Normalise + validate. A duplicate name within one batch collapses to the
        // last upload, mirroring the per-project file-name uniqueness.
        var staged = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in uploads)
        {
            var name = (u.FileName ?? string.Empty).Trim();
            if (name.Length == 0 || !name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["Symbols"] = $"\"{(name.Length == 0 ? "(unnamed)" : name)}\" isn't a .app file. Upload the dependency's compiled symbol package.",
                });
            }
            if (u.Content is not { Length: > 0 })
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["Symbols"] = $"\"{name}\" is empty.",
                });
            }
            staged[name] = u.Content;
        }

        // Replace same-named rows so a re-upload overwrites in place.
        var names = staged.Keys.ToList();
        var existing = await _db.OeProjectSymbols
            .Where(s => s.ProjectId == projectId && names.Contains(s.FileName))
            .ToListAsync(ct);
        if (existing.Count > 0) _db.OeProjectSymbols.RemoveRange(existing);

        var now = DateTime.UtcNow;
        foreach (var (name, content) in staged)
        {
            _db.OeProjectSymbols.Add(new OeProjectSymbol
            {
                OrganizationId = orgId,
                ProjectId = projectId,
                FileName = name,
                Content = content,
                ContentLength = content.Length,
                CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Stored {Count} supplemental symbol(s) for project {ProjectId} ({Name}).",
            staged.Count, projectId, project.Name);
        return staged.Count;
    }

    /// <summary>Removes one stored supplemental symbol from a project. No-op if it's already gone.</summary>
    public async Task DeleteSupplementalSymbolAsync(int projectId, int symbolId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var ownerId = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId)
            .Select(c => c.CreatedByUserId)
            .FirstOrDefaultAsync(ct);
        await _access.EnsureCanManageAsync(projectId, ownerId, ct);

        var symbol = await _db.OeProjectSymbols
            .FirstOrDefaultAsync(s => s.Id == symbolId && s.ProjectId == projectId, ct);
        if (symbol is null) return;
        _db.OeProjectSymbols.Remove(symbol);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Removed supplemental symbol {SymbolId} ({File}) from project {ProjectId}.",
            symbolId, symbol.FileName, projectId);
    }

    /// <summary>Soft-deletes a project (its repositories ride along via the soft-delete marker).</summary>
    public async Task SoftDeleteProjectAsync(int id, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects
            .FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct)
            ?? throw Validation("Name", "This project no longer exists.");

        // Deleting is stricter than managing: an assigned team does the work on a
        // project, it doesn't get to end it. See .design/teams-and-visibility.md.
        await _access.EnsureCanDeleteAsync(project.CreatedByUserId, ct);

        project.DeletedAt = DateTime.UtcNow;
        project.UpdatedAt = project.DeletedAt.Value;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Soft-deleted project {ProjectId}.", id);
    }

    /// <summary>
    /// Validates the input and returns the normalised name/country/repos. Throws
    /// <see cref="PlanValidationException"/> with field-keyed errors otherwise.
    /// </summary>
    private async Task<(string Name, string? ShortName, string? Country, IReadOnlyList<ProjectRepositoryInput> Repos, string? Slug)> ValidateAsync(
        ProjectInput input, int? existingId, int orgId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            errors["Name"] = "Give the project a name.";
        }
        else if (name.Length > 200)
        {
            errors["Name"] = "Keep the name under 200 characters.";
        }
        else
        {
            // Per-org name uniqueness among active rows (the DB enforces it too,
            // now via a case-insensitive lower(name) index — see #432); we
            // pre-check for a friendly inline error rather than a 500. Org-scoping
            // comes from the ambient EF query filter on OeProjects, so no explicit
            // organization_id predicate is needed here.
            var clash = await _db.OeProjects
                .AsNoTracking()
                .AnyAsync(c => c.DeletedAt == null
                               && c.Id != (existingId ?? 0)
                               && c.Name.ToLower() == name.ToLower(), ct);
            if (clash)
            {
                errors["Name"] = "Another solution already uses this name.";
            }
        }

        // The short name is only ever displayed, so it carries the generator's
        // rule and no other: short enough to shorten with, and nothing a file
        // name cannot hold. See .design/customer-naming.md.
        var shortName = (input.ShortName ?? string.Empty).Trim();
        if (shortName.Length > CustomerNaming.MaxShortNameLength || shortName.Any(char.IsControl))
        {
            errors["ShortName"] = "At most 50 characters.";
        }

        // A typed slug is used as typed (lowercased) or refused - never quietly
        // changed into a different address than the one the user asked for. Blank
        // and null come back null; the caller derives one from the name.
        string? slug = null;
        var typedSlug = (input.Slug ?? string.Empty).Trim().ToLowerInvariant();
        // A pasted link ("https://.../solutions/cronus") means its last part.
        if (typedSlug.Contains('/'))
        {
            typedSlug = typedSlug.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        }
        if (typedSlug.Length > 0)
        {
            if (!SolutionSlug.IsValid(typedSlug))
            {
                errors["Slug"] = typedSlug.All(char.IsAsciiDigit)
                    ? "Use at least one letter, e.g. cronus."
                    : typedSlug == "new"
                        ? "'new' is reserved and can't be used. Pick another, e.g. cronus-new."
                        : $"Use lowercase letters, digits and single dashes, at most {SolutionSlug.MaxLength} characters, e.g. cronus-dk.";
            }
            else if (await SlugTakenAsync(typedSlug, existingId, ct))
            {
                errors["Slug"] = $"Another solution already uses /solutions/{typedSlug}. Pick a different one, e.g. {SolutionSlug.WithCounter(typedSlug, 2)}.";
            }
            else
            {
                slug = typedSlug;
            }
        }

        // Required: builds compile against this localisation's base symbols, and
        // there's no org-wide fallback (the base app varies by country — some
        // localisations ship extra regulatory features).
        string? country = null;
        if (string.IsNullOrWhiteSpace(input.DefaultArtifactCountry))
        {
            errors["DefaultArtifactCountry"] = "Pick the country code of the Microsoft base to compile against, e.g. 'dk' or 'w1'.";
        }
        else
        {
            country = input.DefaultArtifactCountry.Trim().ToLowerInvariant();
            if (!CountryRegex.IsMatch(country))
            {
                errors["DefaultArtifactCountry"] = "Use a BC country code like 'dk' or 'w1'.";
            }
        }

        var repos = input.Repositories ?? Array.Empty<ProjectRepositoryInput>();
        var normalised = new List<ProjectRepositoryInput>(repos.Count);
        for (var i = 0; i < repos.Count; i++)
        {
            var repo = repos[i];
            var url = (repo.Url ?? string.Empty).Trim();
            var display = (repo.DisplayName ?? string.Empty).Trim();
            if (url.Length == 0)
            {
                errors[$"Repositories[{i}].Url"] = "Enter the repository URL.";
            }
            else if (!RepositoryLinks.IsValidUrl(repo.Provider, url))
            {
                errors[$"Repositories[{i}].Url"] = repo.Provider == RepositoryProvider.AzureDevOps
                    ? "Use an https Azure DevOps URL (dev.azure.com or *.visualstudio.com)."
                    : "Use an https github.com URL.";
            }
            // Default the display name to the last URL segment when the admin leaves it blank.
            if (display.Length == 0 && url.Length > 0)
            {
                display = url.TrimEnd('/').Split('/').LastOrDefault()?.Replace(".git", "") ?? url;
            }
            normalised.Add(new ProjectRepositoryInput(repo.Provider, url, display));
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);
        return (name, shortName.Length == 0 ? null : shortName, country, normalised, slug);
    }

    /// <summary>
    /// True when an active solution other than <paramref name="existingId"/> in this org
    /// already has <paramref name="slug"/>. Org-scoped by the ambient query filter.
    /// </summary>
    private Task<bool> SlugTakenAsync(string slug, int? existingId, CancellationToken ct) =>
        _db.OeProjects.AsNoTracking()
            .AnyAsync(p => p.DeletedAt == null && p.Id != (existingId ?? 0) && p.Slug == slug, ct);

    /// <summary>
    /// <paramref name="derived"/> itself when it is free, else the first free
    /// <c>-2</c>, <c>-3</c>... variant. Two names can derive the same slug ("A/S Jensen"
    /// and "AS Jensen"), and that must not be the user's problem to solve.
    /// </summary>
    private async Task<string> FreeSlugAsync(string derived, int? existingId, CancellationToken ct)
    {
        // Every variant shares the first few characters of the stem, even once a long
        // stem is cut to make room for the counter, so one prefix read covers them all.
        var prefix = derived[..Math.Min(derived.Length, SolutionSlug.MaxLength - 4)];
        var used = (await _db.OeProjects.AsNoTracking()
                .Where(p => p.DeletedAt == null && p.Id != (existingId ?? 0) && p.Slug != null && p.Slug.StartsWith(prefix))
                .Select(p => p.Slug)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        for (var attempt = 1; ; attempt++)
        {
            var candidate = SolutionSlug.WithCounter(derived, attempt);
            if (!used.Contains(candidate)) return candidate;
        }
    }

    /// <summary>
    /// The slug of solution <paramref name="id"/>, or null when it is deleted, in another
    /// org, or private to people this caller is not among - the numeric address then
    /// renders its own not-found rather than naming the solution in a redirect.
    /// </summary>
    public async Task<string?> FindVisibleSlugAsync(int id, CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);
        return await _db.OeProjects.AsNoTracking()
            .Where(ProjectAccess.VisibleProjectPredicate(snapshot))
            .Where(p => p.Id == id && p.DeletedAt == null)
            .Select(p => p.Slug)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The id of the active solution whose slug is <paramref name="slug"/>, or null when
    /// there is none in this org. Visibility is not decided here: the page that asked
    /// loads the solution through <see cref="GetProjectAsync"/>, which refuses a private
    /// one the same way it refuses an id.
    /// </summary>
    public async Task<int?> FindIdBySlugAsync(string slug, CancellationToken ct = default)
    {
        var key = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (!SolutionSlug.IsValid(key)) return null;
        return await _db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Slug == key)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Saves, turning the name-uniqueness backstop into the same field-keyed error
    /// the pre-check gives. The pre-check reads before this writes, so two
    /// concurrent saves can leave one to be caught by the case-insensitive unique
    /// index — and that has to read as an inline message, not a 500. See #702.
    /// </summary>
    private async Task SaveTranslatingNameClashAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex, "ix_oe_projects_organization_id_slug"))
        {
            throw Validation("Slug", "Another solution already uses this link name. Pick a different one, e.g. add the country: cronus-dk.");
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw Validation("Name", "Another solution already uses this name.");
        }
    }

    private static PlanValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });

    private static readonly System.Text.RegularExpressions.Regex CountryRegex =
        new("^[a-z0-9]{2,10}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // ── MCP id resolvers ────────────────────────────────────────────────
    //
    // Relocated from the MCP tool classes so the visibility fence has one
    // home per entity: a rule added here reaches the agent surface too.
    // They throw McpException because their refusal copy is written for an
    // agent; nothing on the web surface calls them.
    // See .design/teams-and-visibility.md.

    /// <summary>
    /// Resolves a project by name or id for an MCP caller, applying the
    /// visibility fence in the same query. A project the caller cannot see
    /// answers "does not exist", the same as an id in another org.
    /// </summary>
    public async Task<int> ResolveProjectAsync(string projectNameOrId, CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        if (int.TryParse(projectNameOrId, out var asId))
        {
            var exists = await _db.OeProjects.AsNoTracking()
                .Where(visible)
                .AnyAsync(p => p.Id == asId && p.DeletedAt == null, ct);
            if (!exists) throw new McpException($"Solution {asId} does not exist in this organisation.");
            return asId;
        }
        var name = projectNameOrId.Trim();
        var row = await _db.OeProjects.AsNoTracking()
            .Where(visible)
            .Where(p => p.DeletedAt == null && p.Name.ToLower() == name.ToLower())
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            throw new McpException($"Solution '{projectNameOrId}' was not found. Call list_solutions to see available solutions.");
        }
        return row.Id;
    }

    /// <summary>Resolves a build that must be ready and have produced a navigable release; returns (projectId, releaseId).</summary>
    public async Task<(int ProjectId, int ReleaseId)> ResolveReadyBuildAsync(int buildId, CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => _db.OeProjects.Where(visible).Any(p => p.Id == b.ProjectId))
            .Where(b => b.Id == buildId)
            .Select(b => new
            {
                b.ProjectId, b.Status, b.ReleaseId,
                HasObjects = _db.OeProjectBuilds.Where(OeProjectBuild.HasIndexedObjects).Any(x => x.Id == b.Id),
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new McpException($"Build {buildId} was not found in this organisation.");
        if (build.Status != ProjectBuildStatus.Ready || build.ReleaseId is null)
        {
            throw new McpException($"Build {buildId} can't be compared — only 'ready' builds that produced a release can be diffed.");
        }
        if (!build.HasObjects)
        {
            // Preview checks only record whether the code compiles (#1140).
            throw new McpException($"Build {buildId} is a preview check: it records compile results only, so it can't be compared. Use get_solution_build for its errors.");
        }
        return (build.ProjectId, build.ReleaseId.Value);
    }

}

/// <summary>Form-post shape for a project and its repositories. The repo list is owned wholesale by the editor.</summary>
/// <param name="Slug">
/// The solution's key in its web address. Null keeps the current one (a create derives
/// one from the name); blank derives a fresh one from the name; anything else is used
/// as typed, lowercased, and must be free. See <see cref="SolutionSlug"/>.
/// </param>
/// <param name="AutoUpdatePullRequests">
/// Whether the nightly drift check opens update pull requests on its own (#1104). Null
/// leaves it as it is; a create always starts with it off.
/// </param>
public sealed record ProjectInput(
    string Name,
    string? ShortName,
    string? DefaultArtifactCountry,
    IReadOnlyList<ProjectRepositoryInput> Repositories,
    string? Slug = null,
    bool? AutoUpdatePullRequests = null);

/// <summary>
/// One solution as the generator's Solution picker sees it: what it searches on
/// and what picking it pre-fills. See <c>.design/customer-naming.md</c>.
/// </summary>
public sealed record SolutionOption(
    int Id,
    string Name,
    string? ShortName,
    Guid? BcTenantId,
    IReadOnlyList<SolutionRepositoryOption> Repositories);

/// <summary>One of a solution's repositories, as the picker lists it.</summary>
public sealed record SolutionRepositoryOption(string DisplayName, string Url);

/// <summary>One repository row from the project editor.</summary>
public sealed record ProjectRepositoryInput(
    RepositoryProvider Provider,
    string Url,
    string DisplayName);

/// <summary>A project's access setting: how visible it is, and which teams are assigned to it.</summary>
public sealed record ProjectAccessSettings(ProjectVisibility Visibility, IReadOnlyList<int> TeamIds);

/// <summary>A release produced by one project's builds — the project detail page's build-history row.</summary>
public sealed record ProjectReleaseRow(int Id, string Label, string Status, string? BcVersion, DateTime ImportedAt, DateTime? DeletedAt);

/// <summary>One uploaded dependency symbol package, ready to store against a project.</summary>
public sealed record SupplementalSymbolUpload(string FileName, byte[] Content);

/// <summary>A stored supplemental symbol — the admin list row (no blob).</summary>
public sealed record ProjectSymbolRow(int Id, string FileName, int ContentLength, DateTime CreatedAt);
