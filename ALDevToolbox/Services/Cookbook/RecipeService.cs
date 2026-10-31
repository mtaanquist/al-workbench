using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;

namespace ALDevToolbox.Services.Cookbook;

/// <summary>
/// Read- and write-side service for <see cref="Recipe"/>s. Reads back the
/// browser pages at <c>/cookbook</c> and the admin CRUD pages at
/// <c>/admin/cookbook</c>; writes validate via field-keyed
/// <see cref="PlanValidationException"/> so the form can render errors inline.
/// Search uses Postgres <c>ILIKE</c> with the trigram index added by the
/// <c>AddSnippets</c> migration (carried across the
/// <c>RenameSnippetsToCookbook</c> table rename).
/// </summary>
public sealed class RecipeService
{
    /// <summary>Cap on per-file content size so a runaway paste can't blow up the DB row.</summary>
    public const int MaxFileContentLength = 100_000;
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 2000;
    public const int MaxKeywordsLength = 500;
    public const int MaxFileNameLength = 260;
    /// <summary>Cap on the combined <c>RelativePath/FileName</c> length per row, matching the column index.</summary>
    public const int MaxRelativePathLength = 260;
    /// <summary>Cap on folder nesting depth inside a recipe.</summary>
    public const int MaxRelativePathSegments = 8;
    /// <summary>
    /// Generous Markdown body cap. Big enough for multi-section setup notes
    /// with code fences; small enough that a runaway paste can't bloat the
    /// row (a single recipe would have to be quite pathological to top this).
    /// </summary>
    public const int MaxInstructionsLength = 10_000;
    /// <summary>Upper bound on the estimated-value hours field — a sanity cap, not a billing rule.</summary>
    public const decimal MaxEstimatedValueHours = 100_000m;
    /// <summary>Cap on a recorded download's customer name.</summary>
    public const int MaxCustomerNameLength = 200;
    /// <summary>Cap on a recorded repository's <c>owner/name</c>, matching the column.</summary>
    public const int MaxRepositoryLength = 300;

    // One segment in a recipe file's RelativePath. Letters/digits/`._-`,
    // plus space anywhere but the first character. Matches the same shape
    // we use for organisation file paths (see OrganizationConfigService),
    // tightened by the no-leading-space rule so segments like " Foo"
    // don't slip in.
    private static readonly Regex PathSegmentRegex =
        new(@"^[A-Za-z0-9._-][A-Za-z0-9._ -]*$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly ILogger<RecipeService> _logger;
    private readonly IOrganizationContext _orgContext;
    private readonly StorageQuotaGuard _quotaGuard;
    private readonly ProjectAccess _projectAccess;

    public RecipeService(
        AppDbContext db, ILogger<RecipeService> logger, IOrganizationContext orgContext, StorageQuotaGuard quotaGuard,
        ProjectAccess projectAccess)
    {
        _db = db;
        _logger = logger;
        _orgContext = orgContext;
        _quotaGuard = quotaGuard;
        _projectAccess = projectAccess;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    /// <summary>
    /// Returns recipes matching the fuzzy <paramref name="query"/> over title,
    /// description, and keywords (case-insensitive, substring). An empty query
    /// returns every active recipe. Soft-deleted rows are always excluded;
    /// deprecated rows are excluded unless <paramref name="includeDeprecated"/> is set.
    /// Recipe type is deliberately NOT part of the search expression — the
    /// browser uses it as a post-filter chip-row instead. <paramref name="take"/>
    /// stops the read after that many rows, in title order; null reads them all.
    /// </summary>
    public async Task<List<Recipe>> SearchAsync(
        string? query, bool includeDeprecated = false, int? take = null, CancellationToken ct = default)
    {
        var trimmed = (query ?? string.Empty).Trim();
        var rows = _db.Recipes
            .AsNoTracking()
            .Include(s => s.MinimumApplicationVersion)
            .Where(s => s.DeletedAt == null);

        if (!includeDeprecated)
        {
            rows = rows.Where(s => !s.Deprecated);
        }

        if (trimmed.Length > 0)
        {
            // Escaped so a literal % or _ in the search is a character, not a
            // wildcard: "_" used to match every recipe. Same as the palette's
            // recipe source.
            var pattern = "%" + ObjectSearchService.EscapeLike(trimmed) + "%";
            rows = rows.Where(s =>
                EF.Functions.ILike(s.Title, pattern, "\\")
                || EF.Functions.ILike(s.Description, pattern, "\\")
                || EF.Functions.ILike(s.Keywords, pattern, "\\"));
        }

        var ordered = rows.OrderBy(s => s.Title).ThenBy(s => s.Id);
        return await (take is { } n ? ordered.Take(n) : ordered).ToListAsync(ct);
    }

    /// <summary>
    /// File counts keyed by recipe id, for the Cookbook grid card foot
    /// ("N files"). Cheaper than hydrating <see cref="Recipe.Files"/> with their
    /// content; recipes with no files are simply absent from the map (treat as 0).
    /// </summary>
    public async Task<Dictionary<int, int>> GetFileCountsAsync(CancellationToken ct = default)
    {
        var rows = await _db.RecipeFiles
            .AsNoTracking()
            .GroupBy(f => f.RecipeId)
            .Select(g => new { RecipeId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.RecipeId, r => r.Count);
    }

    /// <summary>Returns every recipe (optionally including soft-deleted), with files. Drives the admin list.</summary>
    public Task<List<Recipe>> GetAllForAdminAsync(bool includeDeleted, CancellationToken ct = default)
    {
        var query = _db.Recipes.AsNoTracking();
        if (!includeDeleted)
        {
            query = query.Where(s => s.DeletedAt == null);
        }

        return query
            .Include(s => s.Files.OrderBy(f => f.Ordering))
            .Include(s => s.MinimumApplicationVersion)
            .OrderBy(s => s.DeletedAt == null ? 0 : 1)
            .ThenBy(s => s.Title)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns a single recipe with its files, for everything that reads or
    /// delivers one: the recipe page, the download, apply-to-repository and the
    /// MCP tools. <c>null</c> when it is not in this org or has been deleted -
    /// a recipe an Editor withdrew must not still be served to a stale tab or
    /// an agent holding its id (issue #1251).
    /// </summary>
    public Task<Recipe?> GetAsync(int id, CancellationToken ct = default) =>
        WithFiles().FirstOrDefaultAsync(s => s.Id == id && s.DeletedAt == null, ct);

    /// <summary>
    /// Returns a single recipe with its files whether or not it has been
    /// deleted, for the admin editor, which is where a deleted recipe is
    /// looked at and restored. <c>null</c> when not found in this org.
    /// </summary>
    public Task<Recipe?> GetForAdminAsync(int id, CancellationToken ct = default) =>
        WithFiles().FirstOrDefaultAsync(s => s.Id == id, ct);

    private IQueryable<Recipe> WithFiles() =>
        _db.Recipes
            .AsNoTracking()
            .Include(s => s.Files.OrderBy(f => f.Ordering))
            .Include(s => s.MinimumApplicationVersion);

    /// <summary>Creates a recipe plus its files. Throws <see cref="PlanValidationException"/> on validation failure.</summary>
    public async Task<Recipe> CreateAsync(RecipeInput input, CancellationToken ct = default)
    {
        await _quotaGuard.EnsureCanWriteAsync(ct);
        var orgId = RequireOrganizationId();
        await ValidateAsync(input, existingId: null, orgId, ct);

        var now = DateTime.UtcNow;
        var recipe = new Recipe
        {
            OrganizationId = orgId,
            Title = input.Title.Trim(),
            Description = input.Description.Trim(),
            Keywords = NormaliseKeywords(input.Keywords),
            Type = input.Type,
            Deprecated = input.Deprecated,
            Instructions = NullIfBlank(input.Instructions),
            MinimumApplicationVersionId = input.MinimumApplicationVersionId,
            EstimatedValueHours = input.EstimatedValueHours,
            CreatedAt = now,
            UpdatedAt = now,
            Files = input.Files
                .Select((f, i) => new RecipeFile
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    RelativePath = NormaliseRelativePath(f.RelativePath),
                    FileName = f.FileName.Trim(),
                    Content = f.Content ?? string.Empty,
                })
                .ToList(),
        };

        _db.Recipes.Add(recipe);
        await SaveTranslatingTitleClashAsync(
            $"A recipe with title '{recipe.Title}' already exists.", ct);

        _logger.LogInformation(
            "Created recipe '{Title}' (id={Id}, type={Type}) with {FileCount} file(s).",
            recipe.Title, recipe.Id, recipe.Type, recipe.Files.Count);
        return recipe;
    }

    /// <summary>Updates a recipe's fields and reconciles its file list.</summary>
    public async Task UpdateAsync(int id, RecipeInput input, CancellationToken ct = default)
    {
        await _quotaGuard.EnsureCanWriteAsync(ct);
        var orgId = RequireOrganizationId();
        var existing = await _db.Recipes
            .Include(s => s.Files)
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Recipe with id {id} was not found.",
            });

        await ValidateAsync(input, existingId: id, orgId, ct);

        existing.Title = input.Title.Trim();
        existing.Description = input.Description.Trim();
        existing.Keywords = NormaliseKeywords(input.Keywords);
        existing.Type = input.Type;
        existing.Deprecated = input.Deprecated;
        existing.Instructions = NullIfBlank(input.Instructions);
        existing.MinimumApplicationVersionId = input.MinimumApplicationVersionId;
        existing.EstimatedValueHours = input.EstimatedValueHours;
        existing.UpdatedAt = DateTime.UtcNow;

        ReconcileFiles(existing, input.Files, orgId);

        await SaveTranslatingTitleClashAsync(
            $"A recipe with title '{existing.Title}' already exists.", ct);

        _logger.LogInformation(
            "Updated recipe '{Title}' (id={Id}, type={Type}); now has {FileCount} file(s).",
            existing.Title, existing.Id, existing.Type, existing.Files.Count);
    }

    /// <summary>Soft-deletes a recipe by stamping <see cref="Recipe.DeletedAt"/>.</summary>
    public async Task SoftDeleteAsync(int id, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var existing = await _db.Recipes.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Recipe with id {id} was not found.",
            });

        if (existing.DeletedAt is not null) return;

        existing.DeletedAt = DateTime.UtcNow;
        existing.UpdatedAt = existing.DeletedAt.Value;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Soft-deleted recipe '{Title}' (id={Id}).", existing.Title, existing.Id);
    }

    /// <summary>Clears <see cref="Recipe.DeletedAt"/> on a previously soft-deleted recipe.</summary>
    public async Task RestoreAsync(int id, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var existing = await _db.Recipes.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Recipe with id {id} was not found.",
            });

        if (existing.DeletedAt is null) return;

        // The (organization_id, title) unique index only covers live rows, so
        // a live recipe may have taken this title while this one was deleted.
        var titleTaken = await _db.Recipes
            .AsNoTracking()
            .AnyAsync(s => s.OrganizationId == existing.OrganizationId
                           && s.Title == existing.Title
                           && s.DeletedAt == null
                           && s.Id != existing.Id, ct);
        if (titleTaken)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Title"] = $"A recipe with title '{existing.Title}' already exists. Rename or delete it before restoring this one.",
            });
        }

        existing.DeletedAt = null;
        existing.UpdatedAt = DateTime.UtcNow;
        await SaveTranslatingTitleClashAsync(
            $"A recipe with title '{existing.Title}' already exists. Rename or delete it before restoring this one.", ct);
        _logger.LogInformation("Restored recipe '{Title}' (id={Id}).", existing.Title, existing.Id);
    }

    /// <summary>Flips the <see cref="Recipe.Deprecated"/> flag.</summary>
    public async Task SetDeprecatedAsync(int id, bool deprecated, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var existing = await _db.Recipes.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Recipe with id {id} was not found.",
            });

        if (existing.Deprecated == deprecated) return;

        existing.Deprecated = deprecated;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Set recipe '{Title}' (id={Id}) deprecated={Deprecated}.",
            existing.Title, existing.Id, deprecated);
    }

    /// <summary>
    /// Records that a recipe was downloaded, optionally for a named customer,
    /// so a later bug in a recipe can be traced to where it landed (see the
    /// admin "Where this recipe has been used" panel).
    ///
    /// <paramref name="customerName"/> is optional. It used to be required and
    /// the download was gated on it, which mostly produced "test" and "x" from
    /// people downloading for a demo; null records the honest answer instead.
    /// See issue #539. Throws <see cref="PlanValidationException"/> when the
    /// recipe is missing in this org or the name is oversized.
    /// </summary>
    public async Task RecordDownloadAsync(int recipeId, string? customerName, int? userId, CancellationToken ct = default)
    {
        var name = customerName?.Trim();
        if (name is { Length: > MaxCustomerNameLength })
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["CustomerName"] = $"Customer name must be {MaxCustomerNameLength} characters or fewer.",
            });
        }

        if (string.IsNullOrEmpty(name))
        {
            await RecordUseAsync(
                recipeId, null, RecipeUseSource.Download, projectId: null, userId, repository: null, ct);
            return;
        }

        await RecordUseAsync(
            recipeId, name, RecipeUseSource.Download, await ResolveProjectIdAsync(name, ct), userId,
            repository: null, ct);
    }

    /// <summary>
    /// Finds the active project whose name is exactly <paramref name="name"/>,
    /// ignoring case, so a download names a real row and not just a string.
    /// <c>Project.Name</c> is unique per org among active rows, which is what
    /// makes this safe: "the user picked a project from the suggestions" and
    /// "the user typed exactly a project's name" are indistinguishable *and*
    /// interchangeable, so the modal never has to tell them apart. Soft-deleted
    /// projects are skipped - their name is free again and matching it would
    /// attribute a new download to a project nobody uses any more.
    ///
    /// Compares with <c>ToLower()</c> (Postgres <c>lower()</c>) rather than
    /// <c>ILike</c> because a customer name may legitimately contain <c>_</c> or
    /// <c>%</c>, which <c>ILike</c> would read as wildcards and match the wrong
    /// project. Org-scoped by the EF query filter. See issue #541.
    ///
    /// Only solutions the caller can see are matched: a Private solution's exact
    /// name typed by someone outside its team is recorded as a plain customer
    /// name, so the download neither lands on that solution nor confirms the
    /// name exists (#1545).
    /// </summary>
    private async Task<int?> ResolveProjectIdAsync(string name, CancellationToken ct)
    {
        // Explicit even though RecordUseAsync repeats it: this query runs first,
        // and a lookup that leans on the tenant filter should refuse outright
        // rather than quietly match nothing when there's no org in context.
        RequireOrganizationId();
        var lowered = name.ToLowerInvariant();
        var snapshot = await _projectAccess.GetSnapshotAsync(ct);
        return await _db.OeProjects
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Name.ToLower() == lowered)
            .Where(ProjectAccess.VisibleProjectPredicate(snapshot))
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Records that a user copied a file out of a recipe. Asks for nothing and
    /// carries no customer - the point is that the usage history stops
    /// under-counting single-file recipes, which are almost always taken with
    /// Copy and so never reached the download modal at all. See issue #539.
    ///
    /// Callers record this once per visit, not once per file.
    /// </summary>
    public Task RecordCopyAsync(int recipeId, int? userId, CancellationToken ct = default) =>
        RecordUseAsync(recipeId, customerName: null, RecipeUseSource.Copy, projectId: null, userId,
            repository: null, ct);

    /// <summary>
    /// Records that a recipe was applied to a GitHub repository as a pull
    /// request (issue #626). The same shape as a download - the customer name
    /// is optional and resolves to a solution when it names one - with the
    /// repository stored beside it, because that is the attribution a later fix
    /// can act on: the distinct repositories are what
    /// <see cref="GetAppliedRepositoriesAsync"/> hands the admin page so a bug
    /// found in the recipe can be pull-requested everywhere it landed.
    ///
    /// <para>The repository has already been through
    /// <c>GitHubRepositoryService.ResolveAsync</c> by the time this is called,
    /// so it names a place the acting user may write to rather than a string
    /// they typed.</para>
    /// </summary>
    /// <exception cref="PlanValidationException">The recipe is missing in this org, or a value is oversized.</exception>
    public async Task RecordRepositoryApplyAsync(
        int recipeId, string repository, string? customerName, int? userId, CancellationToken ct = default)
    {
        var repo = (repository ?? string.Empty).Trim();
        if (repo.Length is 0 or > MaxRepositoryLength)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["GitHubRepository"] = $"Repository must be given, and be {MaxRepositoryLength} characters or fewer.",
            });
        }

        var name = customerName?.Trim();
        if (name is { Length: > MaxCustomerNameLength })
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["CustomerName"] = $"Customer name must be {MaxCustomerNameLength} characters or fewer.",
            });
        }

        var projectId = string.IsNullOrEmpty(name) ? null : await ResolveProjectIdAsync(name, ct);
        await RecordUseAsync(
            recipeId,
            string.IsNullOrEmpty(name) ? null : name,
            RecipeUseSource.Repository,
            projectId,
            userId,
            repo,
            ct);
    }

    /// <summary>
    /// The distinct repositories this recipe has been applied to, the most
    /// recently applied first. Drives the admin page's "Update the repositories
    /// that use this recipe" card, which opens one pull request per entry.
    /// See issue #626.
    /// </summary>
    public async Task<List<string>> GetAppliedRepositoriesAsync(int recipeId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        // Grouped in the database rather than de-duplicated in memory: a recipe
        // that keeps being applied collects a row per apply and only the names
        // are wanted. Ordered by each repository's most recent apply so the
        // list reads the way the history above it does.
        return await _db.RecipeDownloads
            .AsNoTracking()
            .Where(d => d.RecipeId == recipeId && d.Repository != null)
            .GroupBy(d => d.Repository!)
            .Select(g => new { Repository = g.Key, LastUsedAt = g.Max(d => d.DownloadedAt) })
            .OrderByDescending(x => x.LastUsedAt)
            .Select(x => x.Repository)
            .ToListAsync(ct);
    }

    private async Task RecordUseAsync(
        int recipeId, string? customerName, RecipeUseSource source, int? projectId, int? userId,
        string? repository, CancellationToken ct)
    {
        var orgId = RequireOrganizationId();

        var recipeExists = await _db.Recipes
            .AsNoTracking()
            .AnyAsync(r => r.Id == recipeId && r.DeletedAt == null, ct);
        if (!recipeExists)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Recipe with id {recipeId} was not found.",
            });
        }

        _db.RecipeDownloads.Add(new RecipeDownload
        {
            OrganizationId = orgId,
            RecipeId = recipeId,
            CustomerName = customerName,
            ProjectId = projectId,
            Source = source,
            Repository = repository,
            DownloadedByUserId = userId,
            DownloadedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Recorded {Source} of recipe {RecipeId} for customer '{Customer}' (project {ProjectId}, repository {Repository}) by user {UserId}.",
            source, recipeId, customerName ?? "(not recorded)", projectId, repository ?? "(none)", userId);
    }

    /// <summary>
    /// Download history for a recipe, newest first, with the downloading user's
    /// and the matched project's navs loaded for the admin panel. Drives the
    /// "applied to customers" list on the admin recipe page, where a row with a
    /// project links straight to it (see issue #541).
    /// </summary>
    public Task<List<RecipeDownload>> GetDownloadsAsync(int recipeId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        return _db.RecipeDownloads
            .AsNoTracking()
            .Include(d => d.DownloadedByUser)
            .Include(d => d.Project)
            .Where(d => d.RecipeId == recipeId)
            // Id is the monotonic (chronological) tiebreaker so two downloads in
            // the same timestamp tick still order newest-first deterministically.
            // See issue #395.
            .OrderByDescending(d => d.DownloadedAt)
            .ThenByDescending(d => d.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Names to offer in the download modal's autocomplete: this org's active
    /// projects, plus any customer name recorded on an earlier use that isn't
    /// one of them. The project list is there because the app already knows the
    /// customers - somebody spelled each of them once, when they set the project
    /// up - so the common case becomes a pick rather than a spelling. Uses with
    /// no customer recorded contribute nothing: a copy never has one, and a
    /// download no longer has to.
    ///
    /// De-duplicated case-insensitively with the *project* spelling winning,
    /// since that is the label the rest of the tool uses; then sorted
    /// alphabetically into one flat list, because a datalist the user is
    /// filtering by typing gains nothing from being grouped. Two queries rather
    /// than a SQL union so the de-dup uses the same comparison as
    /// <see cref="RecordDownloadAsync"/>'s project match. See issue #541.
    ///
    /// Only projects the caller can see are offered, and a recorded name that
    /// spells a Private project the caller cannot see is left out too, so the
    /// list never confirms a hidden project's name (#1545).
    /// </summary>
    public async Task<List<string>> GetCustomerSuggestionsAsync(CancellationToken ct = default)
    {
        RequireOrganizationId();
        var snapshot = await _projectAccess.GetSnapshotAsync(ct);

        var projectNames = await _db.OeProjects
            .AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Where(ProjectAccess.VisibleProjectPredicate(snapshot))
            .Select(p => p.Name)
            .ToListAsync(ct);

        // Every active project's name, less the visible ones: what is left
        // names a project this caller cannot see.
        var hiddenNames = new HashSet<string>(
            await _db.OeProjects
                .AsNoTracking()
                .Where(p => p.DeletedAt == null)
                .Select(p => p.Name)
                .ToListAsync(ct),
            StringComparer.OrdinalIgnoreCase);
        hiddenNames.ExceptWith(projectNames);

        var recordedNames = await _db.RecipeDownloads
            .AsNoTracking()
            .Where(d => d.CustomerName != null)
            .Select(d => d.CustomerName!)
            .Distinct()
            .ToListAsync(ct);

        var suggestions = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Projects first so theirs is the spelling that survives the de-dup.
        foreach (var name in projectNames.Concat(recordedNames.Where(n => !hiddenNames.Contains(n))))
        {
            if (seen.Add(name)) suggestions.Add(name);
        }

        suggestions.Sort(StringComparer.OrdinalIgnoreCase);
        return suggestions;
    }

    /// <summary>
    /// Tokenises the raw keyword input into a canonical, comma-separated tag
    /// list. Whitespace and commas separate tags, except inside a
    /// double-quoted phrase — so <c>"document attachments" factbox</c> yields
    /// the two tags <c>document attachments</c> and <c>factbox</c>. Each tag is
    /// lower-cased, has its internal whitespace collapsed to single spaces, and
    /// any stray comma (only reachable inside quotes, since comma is the
    /// storage delimiter) folded to a space so it can't corrupt the delimiter.
    /// Empty tags are dropped and duplicates removed (first-seen order kept).
    /// Comma-separated rather than space-separated so a multi-word tag survives
    /// the round-trip; substring <c>ILike</c> search is unaffected.
    /// </summary>
    internal static string NormaliseKeywords(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = new System.Text.StringBuilder();
        var inQuote = false;

        void Flush()
        {
            if (current.Length == 0) return;
            // Collapse internal whitespace runs and fold commas to spaces.
            var tag = CollapseWhitespace(current.ToString().Replace(',', ' ')).ToLowerInvariant();
            current.Clear();
            if (tag.Length > 0 && seen.Add(tag))
            {
                tags.Add(tag);
            }
        }

        foreach (var c in raw)
        {
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && (char.IsWhiteSpace(c) || c == ','))
            {
                Flush();
            }
            else
            {
                current.Append(c);
            }
        }
        Flush();

        return string.Join(',', tags);
    }

    /// <summary>Collapses runs of whitespace to single spaces and trims the ends.</summary>
    private static string CollapseWhitespace(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        var lastWasSpace = true; // suppress leading whitespace
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Trims to null for whitespace-only input so an empty textarea persists as null rather than "".</summary>
    internal static string? NullIfBlank(string? raw)
    {
        var trimmed = raw?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Normalises a user-typed relative path: trims whitespace, collapses
    /// backslashes to forward slashes, strips a leading or trailing
    /// <c>/</c>. Doesn't validate — leave that to <see cref="ValidateFiles"/>
    /// so a malformed path surfaces with a field-keyed error rather than
    /// silently mutating.
    /// </summary>
    internal static string NormaliseRelativePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var path = raw.Trim().Replace('\\', '/');
        path = path.Trim('/');
        return path;
    }

    /// <summary>
    /// Saves, turning the title-uniqueness backstop into the same field-keyed error
    /// the pre-check gives. The pre-check reads before this writes, so two
    /// concurrent saves can leave one to be caught by the filtered
    /// <c>(organization_id, title)</c> unique index — and that has to read as an
    /// inline message on the Title field, not a 500. See issue #702.
    /// </summary>
    private async Task SaveTranslatingTitleClashAsync(string message, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Title"] = message });
        }
    }

    private async Task ValidateAsync(RecipeInput input, int? existingId, int orgId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        var title = input.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(title))
        {
            errors[nameof(input.Title)] = "Title is required.";
        }
        else if (title.Length > MaxTitleLength)
        {
            errors[nameof(input.Title)] = $"Title must be {MaxTitleLength} characters or fewer.";
        }
        else
        {
            var existingTitleOwner = await _db.Recipes
                .AsNoTracking()
                .Where(s => s.OrganizationId == orgId && s.Title == title && s.DeletedAt == null)
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync(ct);
            if (existingTitleOwner is not null && existingTitleOwner != existingId)
            {
                errors[nameof(input.Title)] = $"A recipe with title '{title}' already exists.";
            }
        }

        var description = input.Description?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(description))
        {
            errors[nameof(input.Description)] = "Description is required.";
        }
        else if (description.Length > MaxDescriptionLength)
        {
            errors[nameof(input.Description)] = $"Description must be {MaxDescriptionLength} characters or fewer.";
        }

        if ((input.Keywords ?? string.Empty).Length > MaxKeywordsLength)
        {
            errors[nameof(input.Keywords)] = $"Keywords must be {MaxKeywordsLength} characters or fewer.";
        }

        if (!Enum.IsDefined(typeof(RecipeType), input.Type))
        {
            errors[nameof(input.Type)] = "Unknown recipe type.";
        }

        if (input.EstimatedValueHours is decimal hours)
        {
            if (hours < 0)
            {
                errors[nameof(input.EstimatedValueHours)] = "Estimated value (hours) can't be negative.";
            }
            else if (hours > MaxEstimatedValueHours)
            {
                errors[nameof(input.EstimatedValueHours)] = $"Estimated value (hours) must be {MaxEstimatedValueHours:0} or fewer.";
            }
        }

        await ValidateMetadataAsync(_db, input.Instructions, input.MinimumApplicationVersionId, errors, ct);

        ValidateFiles(input.Files, errors);

        if (errors.Count > 0)
        {
            throw new PlanValidationException(errors);
        }
    }

    /// <summary>
    /// Validates the optional Markdown instructions length and that the picked
    /// <see cref="ApplicationVersion"/> exists and isn't soft-deleted. Shared
    /// with <see cref="RecipeSuggestionService"/> so both surfaces enforce
    /// the same rules.
    /// </summary>
    internal static async Task ValidateMetadataAsync(
        AppDbContext db,
        string? instructions,
        int? minimumApplicationVersionId,
        IDictionary<string, string> errors,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(instructions) && instructions.Length > MaxInstructionsLength)
        {
            errors["Instructions"] = $"Instructions must be {MaxInstructionsLength} characters or fewer.";
        }

        if (minimumApplicationVersionId is int versionId)
        {
            // Allow rows the EF filter would normally hide (other orgs are
            // impossible here because the catalogue is org-scoped, but a row
            // that was soft-deleted between page load and save would
            // otherwise produce an opaque FK error). Soft-deleted rows are
            // refused with a friendly message; deprecated rows are accepted
            // so an existing recipe stays valid after a catalogue cleanup.
            var row = await db.ApplicationVersions
                .AsNoTracking()
                .Where(a => a.Id == versionId)
                .Select(a => new { a.DeletedAt })
                .FirstOrDefaultAsync(ct);
            if (row is null)
            {
                errors["MinimumApplicationVersionId"] = "Selected application version no longer exists.";
            }
            else if (row.DeletedAt is not null)
            {
                errors["MinimumApplicationVersionId"] = "Selected application version has been removed from the catalogue.";
            }
        }
    }

    /// <summary>
    /// Shared file-list validation reused by <see cref="RecipeService"/> and
    /// <see cref="RecipeSuggestionService"/> so both surfaces enforce the same
    /// rules on relative paths, file names, content size and duplicates.
    /// </summary>
    internal static void ValidateFiles(IReadOnlyList<RecipeFileInput> files, IDictionary<string, string> errors)
    {
        if (files.Count == 0)
        {
            errors["Files"] = "At least one file is required.";
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var name = file.FileName?.Trim() ?? string.Empty;
            var relPath = NormaliseRelativePath(file.RelativePath);
            var fileFieldKey = $"Files[{i}].FileName";
            var pathFieldKey = $"Files[{i}].RelativePath";
            var contentFieldKey = $"Files[{i}].Content";

            if (string.IsNullOrEmpty(name))
            {
                errors[fileFieldKey] = "File name is required.";
            }
            else if (name.Length > MaxFileNameLength)
            {
                errors[fileFieldKey] = $"File name must be {MaxFileNameLength} characters or fewer.";
            }
            else if (name.Contains('/') || name.Contains('\\') || name.Contains("..") || name.Any(char.IsControl))
            {
                errors[fileFieldKey] = "File name must be a flat file name — no slashes, no '..', no control characters.";
            }

            if (relPath.Length > 0)
            {
                if (relPath.Length > MaxRelativePathLength)
                {
                    errors[pathFieldKey] = $"Folder path must be {MaxRelativePathLength} characters or fewer.";
                }
                else
                {
                    var segments = relPath.Split('/');
                    if (segments.Length > MaxRelativePathSegments)
                    {
                        errors[pathFieldKey] = $"Folder path must have at most {MaxRelativePathSegments} segments.";
                    }
                    else
                    {
                        foreach (var segment in segments)
                        {
                            if (segment.Length == 0 || segment == "." || segment == ".." || !PathSegmentRegex.IsMatch(segment))
                            {
                                errors[pathFieldKey] = "Folder path segments must use letters, digits, spaces, '.', '_' or '-' — no '..', '.', or empty segments.";
                                break;
                            }
                        }
                    }
                }
            }

            if (!errors.ContainsKey(fileFieldKey) && !errors.ContainsKey(pathFieldKey))
            {
                var key = relPath.Length == 0 ? name : relPath + "/" + name;
                if (!seen.Add(key))
                {
                    errors[fileFieldKey] = $"Duplicate path '{key}' (case-insensitive).";
                }
            }

            var content = file.Content ?? string.Empty;
            if (content.Length > MaxFileContentLength)
            {
                errors[contentFieldKey] = $"File content must be {MaxFileContentLength} characters or fewer.";
            }
        }
    }

    private static void ReconcileFiles(Recipe existing, IReadOnlyList<RecipeFileInput> inputs, int orgId)
    {
        var existingFiles = existing.Files.OrderBy(f => f.Ordering).ToList();

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var name = input.FileName.Trim();
            var relPath = NormaliseRelativePath(input.RelativePath);
            var content = input.Content ?? string.Empty;

            if (i < existingFiles.Count)
            {
                var file = existingFiles[i];
                file.Ordering = i;
                file.RelativePath = relPath;
                file.FileName = name;
                file.Content = content;
            }
            else
            {
                existing.Files.Add(new RecipeFile
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    RelativePath = relPath,
                    FileName = name,
                    Content = content,
                });
            }
        }

        for (var i = inputs.Count; i < existingFiles.Count; i++)
        {
            existing.Files.Remove(existingFiles[i]);
        }
    }
}

/// <summary>Form-shaped admin input for recipe create/update.</summary>
public record RecipeInput(
    string Title,
    string Description,
    string Keywords,
    RecipeType Type,
    bool Deprecated,
    IReadOnlyList<RecipeFileInput> Files,
    string? Instructions = null,
    int? MinimumApplicationVersionId = null,
    decimal? EstimatedValueHours = null);

/// <summary>One file row submitted by the recipe file editor.</summary>
public record RecipeFileInput(string FileName, string Content, string RelativePath = "");
