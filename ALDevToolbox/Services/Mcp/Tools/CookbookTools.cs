using System.ComponentModel;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Mcp.Dtos;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using ALDevToolbox.Services.Cookbook;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// MCP tools that wrap <see cref="RecipeService"/> and
/// <see cref="RecipeSuggestionService"/>. Lets an agent answer
/// "is there a recipe for X?"-style questions without going through the
/// web UI. All reads are org-scoped by the EF query filter.
///
/// Authoring conventions are surfaced through
/// <see cref="GetCookbookGuidanceAsync"/>, which hands the agent a
/// short-lived signed <c>GuidanceToken</c>. <see cref="SuggestAsync"/>
/// and <see cref="UpdateSuggestionAsync"/> require that token and reject
/// the call without it — the ordering of "read guidance, then submit" is
/// mandatory rather than suggested.
/// </summary>
[McpServerToolType]
public sealed class CookbookTools
{
    /// <summary>
    /// Data Protection purpose string for the cookbook-guidance token. The
    /// suffix is versioned so we can rotate the format without invalidating
    /// every Data Protection purpose ring at the same time.
    /// </summary>
    public const string GuidanceTokenProtectionPurpose = "ALDevToolbox.Cookbook.GuidanceToken.v1";

    /// <summary>
    /// How long a token issued by <see cref="GetCookbookGuidanceAsync"/>
    /// stays usable. Long enough for an agent to draft a recipe after
    /// reading the guidance; short enough to force a re-read per working
    /// session.
    /// </summary>
    public static readonly TimeSpan GuidanceTokenLifetime = TimeSpan.FromMinutes(30);

    private const int DefaultRecipeLimit = 25;
    private const int MaxRecipeLimit = 50;

    /// <summary>A search row's description is cut here; get_recipe has the whole one (#1512).</summary>
    internal const int MaxSummaryDescriptionLength = 300;

    /// <summary>
    /// How many characters of recipe text and file bodies one get_recipe answer carries
    /// (#1512), leaving room for JSON escaping under what an assistant client accepts.
    /// </summary>
    internal const int MaxRecipeAnswerLength = 30_000;

    /// <summary>The least room file bodies get, however long the recipe's own text is.</summary>
    private const int MinRecipeContentLength = 10_000;

    private readonly RecipeService _recipes;
    private readonly AppDbContext _db;
    private readonly RecipeSuggestionService _suggestions;
    private readonly IOrganizationContext _orgContext;
    private readonly IDataProtector _guidanceProtector;
    private readonly TimeProvider _clock;
    private readonly GitHubRecipeDeliveryService _delivery;

    public CookbookTools(
        RecipeService recipes,
        AppDbContext db,
        RecipeSuggestionService suggestions,
        IOrganizationContext orgContext,
        IDataProtectionProvider protectionProvider,
        TimeProvider clock,
        GitHubRecipeDeliveryService delivery)
    {
        _recipes = recipes;
        _db = db;
        _suggestions = suggestions;
        _orgContext = orgContext;
        _guidanceProtector = protectionProvider.CreateProtector(GuidanceTokenProtectionPurpose);
        _clock = clock;
        _delivery = delivery;
    }

    /// <summary>
    /// Built-in copy describing what each <see cref="RecipeType"/> means.
    /// Lives in code (not in the DB) so an empty org-level
    /// <see cref="OrganizationSettings.CookbookGuidance"/> still steers an
    /// agent towards the right type bucket.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> BuiltInTypeDescriptions =
        new Dictionary<string, string>
        {
            ["Snippet"] =
                "1-2 files, flat (no folders). A single self-contained AL artefact you can describe in one sentence: " +
                "an OnAfterValidate subscriber for X, a tableextension adding field Y, a helper codeunit that does Z. " +
                "No setup tables, no permission sets, no folder structure. " +
                "Not a Snippet if: more than one AL object type is involved, or files need to live in different folders.",
            ["Pattern"] =
                "3-8 files, often one or two folders. A coordinated set of files solving one bounded problem. " +
                "Typical shapes: an event subscriber + the table/page it modifies; a setup table + setup page + install " +
                "codeunit so a feature has somewhere to keep its configuration; a factbox + its subscriber + the " +
                "pageextensions that surface it. May use RelativePath to group related objects (Setup/, Events/). " +
                "Extends existing surfaces; doesn't introduce a new top-level namespace. " +
                "Not a Pattern if: it's just one file (use Snippet), or it carries its own namespace, permission sets, " +
                "and a recognisable feature surface (use Module).",
            ["Module"] =
                "10+ files across multiple folders. A near-complete feature area with its own top-level namespace, " +
                "typically including its own permission sets and often translations. Drops into an existing workspace " +
                "as a self-contained subfolder — bigger than a Pattern (own surface, not just an extension to existing " +
                "surfaces), smaller than a full BC app (no app.json of its own; it contributes to a workspace's existing " +
                "extension). " +
                "Not a Module if: it's a coordinated handful of files extending existing functionality (use Pattern), " +
                "or it's intended as a standalone app rather than something to integrate.",
        };

    [McpServerTool(Name = "search_recipes", ReadOnly = true)]
    [Description(
        "Searches the caller's organisation Cookbook by free-text query (title, description, keywords). " +
        "Returns lightweight summaries with the recipe Type (Snippet, Pattern, or Module) — search itself " +
        "ignores type; the type field is for the agent to post-filter. Returns at most 25 by default, in title order; " +
        "Truncated and Note say whether more matched, so use a more specific query. A description longer than 300 characters " +
        "is cut here; get_recipe has the whole one, and the files.")]
    public async Task<CappedRows<RecipeSummary>> SearchAsync(
        [Description("Search text. Matches against title, description, and keywords. Pass an empty string to list everything, up to the limit.")] string query,
        [Description("If true, also return recipes that have been marked deprecated. Defaults to false.")] bool includeDeprecated = false,
        [Description("How many to return. Default 25, at least 1, at most 50.")] int limit = DefaultRecipeLimit,
        CancellationToken ct = default)
    {
        var cap = Math.Clamp(limit, 1, MaxRecipeLimit);
        var rows = await _recipes.SearchAsync(query, includeDeprecated, cap + 1, ct);
        if (rows.Count == 0) return new CappedRows<RecipeSummary>(Array.Empty<RecipeSummary>(), false, null);
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await _db.RecipeFiles.AsNoTracking()
            .Where(f => ids.Contains(f.RecipeId))
            .GroupBy(f => f.RecipeId)
            .Select(g => new { RecipeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RecipeId, x => x.Count, ct);
        var summaries = rows.Select(s => new RecipeSummary(
            s.Id, s.Title,
            s.Description.Length > MaxSummaryDescriptionLength ? s.Description[..MaxSummaryDescriptionLength] + "..." : s.Description,
            s.Keywords,
            s.Type.ToString(),
            s.Deprecated,
            counts.TryGetValue(s.Id, out var c) ? c : 0,
            s.MinimumApplicationVersion?.Name,
            s.MinimumApplicationVersion?.Application,
            s.EstimatedValueHours)).ToList();
        return CappedRows<RecipeSummary>.From(summaries, cap, "recipes", "ordered by title",
            "Use a more specific query to narrow the search.");
    }

    [McpServerTool(Name = "get_recipe", ReadOnly = true)]
    [Description("Returns the content of a recipe (each file's path and body) by id. Each file's Path includes its folder layout (RelativePath/FileName). File bodies come back in order while they fit in one answer (about 30,000 characters with the recipe's text); a file past that is listed with its path and a null body, and a single file too large for one answer is cut. Truncated and Note say which, and how to read the rest: pass path (a file's Path, or a folder to get the files under it), and offset to continue a cut file. Use search_recipes to find candidate ids.")]
    public async Task<RecipeDetail> GetAsync(
        [Description("Recipe id from search_recipes.")] int id,
        [Description("Optional file Path, or a folder, to return only those files, e.g. 'Setup/SetupTable.al' or 'Setup'.")] string? path = null,
        [Description("Optional character offset into the one file path names, to continue a file that was cut. Default 0.")] int offset = 0,
        CancellationToken ct = default)
    {
        var row = await _recipes.GetAsync(id, ct);
        if (row is null)
        {
            throw new McpException($"Recipe {id} not found, or not visible to your organisation.");
        }

        var files = row.Files.OrderBy(f => f.Ordering)
            .Select(f => (Path: string.IsNullOrEmpty(f.RelativePath) ? f.FileName : f.RelativePath + "/" + f.FileName, f.Content))
            .ToList();
        var wanted = path?.Trim().Trim('/');
        if (!string.IsNullOrEmpty(wanted))
        {
            files = files.Where(f => string.Equals(f.Path, wanted, StringComparison.OrdinalIgnoreCase)
                                     || f.Path.StartsWith(wanted + "/", StringComparison.OrdinalIgnoreCase)).ToList();
            if (files.Count == 0)
            {
                throw new McpException($"Recipe {id} has no file or folder '{path}'. Call get_recipe without path to see its files.");
            }
        }
        if (offset != 0)
        {
            if (files.Count != 1 || offset < 0 || offset >= files[0].Content.Length)
            {
                throw new McpException("offset needs path to name one file, and must lie inside that file's body.");
            }
            files = [(files[0].Path, files[0].Content[offset..])];
        }

        // The recipe's own text rides in the same answer, so it comes off the budget.
        var metadata = row.Title.Length + row.Description.Length + row.Keywords.Length + (row.Instructions?.Length ?? 0);
        var budget = Math.Max(MinRecipeContentLength, MaxRecipeAnswerLength - metadata);
        var fit = FitFiles(files, budget);

        var notes = new List<string>();
        if (fit.LeftOut > 0)
            notes.Add($"{fit.LeftOut} of {fit.Files.Count} files are listed without their body to keep the answer short. Call get_recipe again with path set to one of those files' Path to read it.");
        if (fit.CutAt is { } cutAt)
            notes.Add($"{fit.Files[0].Path} was cut after {cutAt} of {files[0].Content.Length} characters. Call get_recipe again with path set to it and offset {offset + cutAt} to read on.");
        return new RecipeDetail(
            row.Id, row.Title, row.Description, row.Keywords,
            row.Type.ToString(),
            row.Deprecated,
            fit.Files,
            row.Instructions,
            row.MinimumApplicationVersion?.Name,
            row.MinimumApplicationVersion?.Application,
            row.EstimatedValueHours,
            Truncated: notes.Count > 0,
            Note: notes.Count > 0 ? string.Join(" ", notes) : null);
    }

    /// <summary>
    /// Keeps file bodies in order while they fit in <paramref name="budget"/> characters and
    /// lists the rest by path alone. The first body is always kept, cut at the budget if it
    /// must be, so a call for one large file still answers; <c>CutAt</c> says where.
    /// </summary>
    internal static (List<RecipeFileDto> Files, int LeftOut, int? CutAt) FitFiles(
        IReadOnlyList<(string Path, string Content)> files, int budget)
    {
        var result = new List<RecipeFileDto>(files.Count);
        var used = 0;
        var leftOut = 0;
        int? cutAt = null;
        foreach (var (filePath, content) in files)
        {
            if (result.Count == 0 && content.Length > budget)
            {
                result.Add(new RecipeFileDto(filePath, content[..budget]));
                used = budget;
                cutAt = budget;
            }
            else if (used + content.Length <= budget)
            {
                result.Add(new RecipeFileDto(filePath, content));
                used += content.Length;
            }
            else
            {
                result.Add(new RecipeFileDto(filePath, null));
                leftOut++;
            }
        }
        return (result, leftOut, cutAt);
    }

    [McpServerTool(Name = "get_cookbook_guidance", ReadOnly = true)]
    [Description(
        "Returns the caller organisation's authoring conventions for the Cookbook, the built-in description " +
        "of each recipe Type (Snippet, Pattern, Module), AND a short-lived GuidanceToken that suggest_recipe " +
        "and update_recipe_suggestion require. Calling this tool is MANDATORY before submitting a recipe — " +
        "the write tools reject the call without a valid token from a recent get_cookbook_guidance.")]
    public async Task<CookbookGuidance> GetCookbookGuidanceAsync(CancellationToken ct = default)
    {
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new McpException("No organisation in scope; call this from an authenticated MCP session.");

        var guidance = await _db.OrganizationSettings
            .AsNoTracking()
            .Where(s => s.OrganizationId == orgId)
            .Select(s => s.CookbookGuidance)
            .FirstOrDefaultAsync(ct)
            ?? string.Empty;

        var token = IssueGuidanceToken(orgId);
        return new CookbookGuidance(
            guidance,
            BuiltInTypeDescriptions.Keys.ToList(),
            BuiltInTypeDescriptions,
            token,
            (int)GuidanceTokenLifetime.TotalSeconds);
    }

    [McpServerTool(Name = "suggest_recipe", ReadOnly = false, Idempotent = false)]
    [Description(
        "Submits a draft recipe to the caller organisation's admin review queue at /admin/cookbook/suggestions. " +
        "MANDATORY two-step protocol: first call get_cookbook_guidance and pass the returned GuidanceToken in " +
        "this call's input. The call is rejected with a clear error if the token is missing, expired (older " +
        "than 30 minutes), or issued for a different organisation. " +
        "The submission is NOT immediately visible from search_recipes — an admin must approve it first. " +
        "Use this when you have a useful AL pattern (e.g. extracted and generalised from a customer codebase, " +
        "with customer-specific names stripped) that the team's Cookbook is missing. " +
        "Provide a descriptive Title, a 1-3 sentence Description of when to use it, comma-separated Keywords, " +
        "a Type (Snippet / Pattern / Module — see get_cookbook_guidance for definitions), and one or more files. " +
        "Optionally propose EstimatedValueHours — roughly how many hours of implementer work the recipe saves. " +
        "Each file has a flat FileName (no slashes), an optional RelativePath for the folder it lives in (no '..', " +
        "no leading '/', empty = root), and the file body in Content. Returns the new SuggestionId.")]
    public async Task<SuggestRecipeResult> SuggestAsync(
        [Description("The draft recipe payload. GuidanceToken (from get_cookbook_guidance) is required; Title, Description and Type are required; Files must contain at least one entry with a flat FileName, optional RelativePath, and the file body in Content.")] SuggestRecipeInput input,
        CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        RequireValidGuidanceToken(input.GuidanceToken, orgId);

        try
        {
            var id = await _suggestions.SubmitAsync(input.ToDomain(), ct);
            return new SuggestRecipeResult(
                id,
                "Submitted for review. An admin will approve or reject it at /admin/cookbook/suggestions.");
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
        }
    }

    [McpServerTool(Name = "update_recipe_suggestion", ReadOnly = false, Idempotent = true)]
    [Description(
        "Edits a pending recipe suggestion that the caller previously submitted via suggest_recipe. " +
        "MANDATORY two-step protocol: include a fresh GuidanceToken from get_cookbook_guidance (same rules as suggest_recipe). " +
        "Only the original submitter can edit, and only while the suggestion is still Pending — " +
        "approved or rejected suggestions are terminal and refuse the update (rejected suggestions " +
        "should be re-submitted as a new draft). The full payload replaces the existing fields and " +
        "file list, so include every file you want the suggestion to end up with, not just the ones " +
        "you're changing. Returns the unchanged SuggestionId on success.")]
    public async Task<UpdateRecipeSuggestionResult> UpdateSuggestionAsync(
        [Description("The replacement payload, including the id of the suggestion to update and a GuidanceToken from get_cookbook_guidance. Same field rules as suggest_recipe: Title, Description and Type required, Files non-empty, FileName flat (no slashes or '..'), RelativePath relative (no leading '/', no '..').")] UpdateRecipeSuggestionInput input,
        CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        RequireValidGuidanceToken(input.GuidanceToken, orgId);

        try
        {
            await _suggestions.UpdateAsync(input.SuggestionId, input.ToDomain(), ct);
            return new UpdateRecipeSuggestionResult(
                input.SuggestionId,
                "Updated. The suggestion is still Pending at /admin/cookbook/suggestions.");
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
        }
    }

    [McpServerTool(Name = "update_recipe", ReadOnly = false, Idempotent = true)]
    [Description(
        "Edits an already-published recipe. Requires the Editor or Admin role — regular users should use " +
        "suggest_recipe / update_recipe_suggestion and go through the review queue instead. " +
        "MANDATORY two-step protocol: include a fresh GuidanceToken from get_cookbook_guidance (same rules " +
        "as suggest_recipe). The payload is a full replace: every field and every file you want the recipe " +
        "to end up with, not just the ones you're changing — call get_recipe first and start from its " +
        "current content. Deprecated is optional and keeps the recipe's current flag when omitted. " +
        "Returns the unchanged RecipeId on success.")]
    public async Task<UpdateRecipeResult> UpdateRecipeAsync(
        [Description("The replacement payload, including the id of the recipe to update (from search_recipes) and a GuidanceToken from get_cookbook_guidance. Same field rules as suggest_recipe, plus optional EstimatedValueHours and Deprecated.")] UpdateRecipeInput input,
        CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        RequireValidGuidanceToken(input.GuidanceToken, orgId);
        await RequireEditorAsync(ct);

        // Resolve the current Deprecated flag so an omitted field preserves
        // it instead of resetting; also fronts a clear not-found error.
        var current = await _db.Recipes.AsNoTracking()
            .Where(r => r.Id == input.RecipeId && r.DeletedAt == null)
            .Select(r => new { r.Deprecated })
            .FirstOrDefaultAsync(ct)
            ?? throw new McpException($"Recipe {input.RecipeId} not found, or not visible to your organisation.");

        try
        {
            await _recipes.UpdateAsync(input.RecipeId, input.ToDomain(current.Deprecated), ct);
            return new UpdateRecipeResult(input.RecipeId, "Updated. The change is live in the Cookbook.");
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
        }
    }

    [McpServerTool(Name = "apply_recipe", ReadOnly = false, Idempotent = false)]
    [Description(
        "Puts a recipe's files into one of your organisation's GitHub repositories and opens a pull request " +
        "for them. The commit goes out in your name, onto a branch of its own - never onto the repository's " +
        "default branch - so nothing changes there until somebody merges it. Applying the same recipe again " +
        "while its pull request is still open adds a commit to that pull request instead of opening a second " +
        "one. The recipe's files are written as they stand in the Cookbook: a file the repository has changed " +
        "since is replaced rather than merged, and the diff shows what would change. " +
        "Only repositories in the GitHub organisation your organisation has connected, and that you can open " +
        "on GitHub yourself, are accepted; anything else is refused, as is a call from an account that has not " +
        "connected its GitHub account. Optionally name the customer this is for, which records the apply " +
        "against that Solution, exactly as the download does.")]
    public async Task<RepositoryDeliveryResult> ApplyRecipeAsync(
        [Description("Recipe id from search_recipes.")] int recipeId,
        [Description("The repository as 'owner/name'. It must be in the GitHub organisation your organisation has connected, and one you can open on GitHub yourself.")] string repository,
        [Description("Optional. The customer this recipe is being applied for; a name that matches one of your organisation's Solutions is recorded against it.")] string? customer = null,
        CancellationToken ct = default)
    {
        try
        {
            // Routed through the same service the Cookbook page uses, so the
            // access rule is inherited rather than restated here - see
            // "Keeping MCP parity with the web UI" in PROJECT.md.
            var delivered = await _delivery.ApplyAsync(recipeId, (repository ?? string.Empty).Trim(), customer, ct);
            return new RepositoryDeliveryResult(
                delivered.Repository.FullName,
                delivered.PullRequest.HeadBranch,
                delivered.Repository.DefaultBranch,
                delivered.PullRequest.Number,
                delivered.PullRequest.HtmlUrl,
                delivered.IsNewPullRequest);
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
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

    /// <summary>
    /// Throws unless the acting MCP user is Editor / Admin (or a SiteAdmin).
    /// Mirrors the role gate on the /admin/cookbook edit pages — regular
    /// users go through the suggestion queue instead.
    /// </summary>
    private async Task RequireEditorAsync(CancellationToken ct)
    {
        var userId = _orgContext.CurrentUserId
            ?? throw new McpException("No user in scope; call this from an authenticated MCP session.");
        var role = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (UserRole?)u.Role)
            .FirstOrDefaultAsync(ct);
        if (!_orgContext.IsSiteAdmin && role is not (UserRole.Editor or UserRole.Admin))
        {
            throw new McpException(
                "Editing published recipes requires the Editor or Admin role. " +
                "Use suggest_recipe to propose the change through the review queue instead.");
        }
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new McpException("No organisation in scope; call this from an authenticated MCP session.");

    /// <summary>
    /// Issues a token binding the caller's organisation to a deadline. The
    /// payload is <c>"orgId|expiresUnix"</c> passed through
    /// <see cref="IDataProtector"/> so the agent can't forge or alter it —
    /// the Data Protection key ring carries both HMAC and key rotation.
    /// </summary>
    internal string IssueGuidanceToken(int organizationId)
    {
        var expiresAt = _clock.GetUtcNow().Add(GuidanceTokenLifetime);
        var payload = organizationId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "|"
            + expiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return _guidanceProtector.Protect(payload);
    }

    /// <summary>
    /// Verifies a token from <see cref="IssueGuidanceToken"/>: not tampered,
    /// not expired, and issued to the same organisation that's now writing.
    /// Throws <see cref="McpException"/> with a message that names the
    /// recovery action so the agent knows how to fix the next call.
    /// </summary>
    private void RequireValidGuidanceToken(string? token, int callerOrgId)
    {
        const string fixupHint = "Call get_cookbook_guidance and pass the returned GuidanceToken to this tool.";

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new McpException(
                "Missing GuidanceToken. " + fixupHint);
        }

        string payload;
        try
        {
            payload = _guidanceProtector.Unprotect(token);
        }
        catch
        {
            // Tampered, truncated, base64-mangled, or signed with a key ring
            // that's no longer trusted (e.g. /app-keys volume reset).
            throw new McpException(
                "GuidanceToken is invalid or no longer accepted. " + fixupHint);
        }

        var parts = payload.Split('|', 2);
        if (parts.Length != 2
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var tokenOrgId)
            || !long.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var expiresUnix))
        {
            throw new McpException(
                "GuidanceToken payload is malformed. " + fixupHint);
        }

        if (tokenOrgId != callerOrgId)
        {
            // The token signs the org id, so this only fires if someone
            // pasted a token issued in a different organisation. Refuse
            // with the same generic recovery message so the surface stays
            // narrow.
            throw new McpException(
                "GuidanceToken is for a different organisation. " + fixupHint);
        }

        var nowUnix = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (nowUnix >= expiresUnix)
        {
            throw new McpException(
                "GuidanceToken has expired (tokens are valid for "
                + (int)GuidanceTokenLifetime.TotalMinutes
                + " minutes). " + fixupHint);
        }
    }

    private static string FormatErrors(IReadOnlyDictionary<string, string> errors) =>
        string.Join("; ", errors.Select(kv => $"{kv.Key}: {kv.Value}"));
}
