using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Cookbook;

/// <summary>
/// User-submitted recipe suggestions and the admin approval queue. Any
/// signed-in user can <see cref="SubmitAsync"/>; admins
/// <see cref="ApproveAsync"/> (promoting the draft to a real
/// <see cref="Recipe"/>) or <see cref="RejectAsync"/> from the queue at
/// <c>/admin/cookbook/suggestions</c>.
/// </summary>
public sealed class RecipeSuggestionService
{
    private readonly AppDbContext _db;
    private readonly ILogger<RecipeSuggestionService> _logger;
    private readonly IOrganizationContext _orgContext;

    public RecipeSuggestionService(AppDbContext db, ILogger<RecipeSuggestionService> logger, IOrganizationContext orgContext)
    {
        _db = db;
        _logger = logger;
        _orgContext = orgContext;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; service mutation called outside an authenticated request.");

    /// <summary>Submits a draft recipe for admin review. Returns the persisted row's id.</summary>
    public async Task<int> SubmitAsync(RecipeSuggestionInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var userId = RequireUserId();

        await ValidateAsync(input, ct);

        var now = DateTime.UtcNow;
        var suggestion = new RecipeSuggestion
        {
            OrganizationId = orgId,
            SuggestedByUserId = userId,
            Title = input.Title.Trim(),
            Description = input.Description.Trim(),
            Keywords = RecipeService.NormaliseKeywords(input.Keywords),
            Type = input.Type,
            Instructions = RecipeService.NullIfBlank(input.Instructions),
            MinimumApplicationVersionId = input.MinimumApplicationVersionId,
            EstimatedValueHours = input.EstimatedValueHours,
            Decision = RecipeSuggestionDecision.Pending,
            RequestedAt = now,
            Files = input.Files
                .Select((f, i) => new RecipeSuggestionFile
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    RelativePath = RecipeService.NormaliseRelativePath(f.RelativePath),
                    FileName = f.FileName.Trim(),
                    Content = f.Content ?? string.Empty,
                })
                .ToList(),
        };

        _db.RecipeSuggestions.Add(suggestion);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserId} submitted recipe suggestion '{Title}' (id={Id}, type={Type}) with {FileCount} file(s).",
            userId, suggestion.Title, suggestion.Id, suggestion.Type, suggestion.Files.Count);
        return suggestion.Id;
    }

    /// <summary>Returns pending suggestions (with files) for the admin queue, oldest first.</summary>
    public Task<List<RecipeSuggestion>> GetPendingAsync(CancellationToken ct = default)
    {
        return _db.RecipeSuggestions
            .AsNoTracking()
            .Where(s => s.Decision == RecipeSuggestionDecision.Pending)
            .Include(s => s.Files.OrderBy(f => f.Ordering))
            .Include(s => s.SuggestedByUser)
            .Include(s => s.MinimumApplicationVersion)
            .OrderBy(s => s.RequestedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns one suggestion (with files) for the detail view, whatever its
    /// decision, with the deciding user so a decided one can say who decided it.
    /// </summary>
    public Task<RecipeSuggestion?> GetAsync(int id, CancellationToken ct = default)
    {
        return _db.RecipeSuggestions
            .AsNoTracking()
            .Include(s => s.Files.OrderBy(f => f.Ordering))
            .Include(s => s.SuggestedByUser)
            .Include(s => s.DecidedByUser)
            .Include(s => s.MinimumApplicationVersion)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    /// <summary>
    /// Updates a pending suggestion in place. Only the user who originally
    /// submitted it can edit; approved or rejected suggestions are
    /// terminal and refuse the update. Reconciles the file list against
    /// the new input by position (existing rows are reused, extras are
    /// added, surplus rows are removed) so the audit trail attributes
    /// the change to the file rows rather than recording delete+create.
    /// </summary>
    public async Task UpdateAsync(int suggestionId, RecipeSuggestionInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var userId = RequireUserId();

        var existing = await _db.RecipeSuggestions
            .Include(s => s.Files)
            .FirstOrDefaultAsync(s => s.Id == suggestionId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Suggestion with id {suggestionId} was not found.",
            });

        if (existing.Decision != RecipeSuggestionDecision.Pending)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Decision"] = $"Suggestion is already {existing.Decision.ToString().ToLowerInvariant()} and can no longer be edited.",
            });
        }

        if (existing.SuggestedByUserId != userId)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["SuggestedByUserId"] = "Only the user who submitted a suggestion can edit it.",
            });
        }

        await ValidateAsync(input, ct);

        existing.Title = input.Title.Trim();
        existing.Description = input.Description.Trim();
        existing.Keywords = RecipeService.NormaliseKeywords(input.Keywords);
        existing.Type = input.Type;
        existing.Instructions = RecipeService.NullIfBlank(input.Instructions);
        existing.MinimumApplicationVersionId = input.MinimumApplicationVersionId;
        existing.EstimatedValueHours = input.EstimatedValueHours;

        ReconcileFiles(existing, input.Files, orgId);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserId} updated recipe suggestion '{Title}' (id={Id}, type={Type}); now has {FileCount} file(s).",
            userId, existing.Title, existing.Id, existing.Type, existing.Files.Count);
    }

    /// <summary>
    /// Promotes a pending suggestion to a real <see cref="Recipe"/>. Runs
    /// inside a transaction so the recipe, its files, and the suggestion's
    /// decision columns either all land or none do.
    /// </summary>
    public async Task<Recipe> ApproveAsync(int suggestionId, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var deciderId = RequireUserId();

        var suggestion = await _db.RecipeSuggestions
            .Include(s => s.Files)
            .FirstOrDefaultAsync(s => s.Id == suggestionId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Suggestion with id {suggestionId} was not found.",
            });

        if (suggestion.Decision != RecipeSuggestionDecision.Pending)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Decision"] = $"Suggestion is already {suggestion.Decision.ToString().ToLowerInvariant()}.",
            });
        }

        // Title-uniqueness within the org is the only reason approval can
        // fail outside the original validation pass — another suggestion
        // could have been approved with the same title in between.
        var titleClash = await _db.Recipes
            .AsNoTracking()
            .AnyAsync(s => s.OrganizationId == orgId
                           && s.Title == suggestion.Title
                           && s.DeletedAt == null, ct);
        if (titleClash)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Title"] = $"A recipe with title '{suggestion.Title}' already exists. Reject this suggestion or edit the existing recipe.",
            });
        }

        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var recipe = new Recipe
        {
            OrganizationId = orgId,
            Title = suggestion.Title,
            Description = suggestion.Description,
            Keywords = suggestion.Keywords,
            Type = suggestion.Type,
            Deprecated = false,
            Instructions = suggestion.Instructions,
            MinimumApplicationVersionId = suggestion.MinimumApplicationVersionId,
            EstimatedValueHours = suggestion.EstimatedValueHours,
            CreatedAt = now,
            UpdatedAt = now,
            Files = suggestion.Files
                .OrderBy(f => f.Ordering)
                .Select((f, i) => new RecipeFile
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    RelativePath = f.RelativePath,
                    FileName = f.FileName,
                    Content = f.Content,
                })
                .ToList(),
        };

        try
        {
            _db.Recipes.Add(recipe);
            await _db.SaveChangesAsync(ct);

            suggestion.Decision = RecipeSuggestionDecision.Approved;
            suggestion.DecidedAt = now;
            suggestion.DecidedByUserId = deciderId;
            suggestion.ApprovedRecipeId = recipe.Id;
            await _db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // The context is circuit-scoped in Blazor Server, so a failed
            // insert would stay tracked as Added and be retried (and fail)
            // on every later save through this context. Drop the tracked
            // state before surfacing the error.
            _db.ChangeTracker.Clear();
            _logger.LogWarning(ex,
                "Approving recipe suggestion '{Title}' (id={SuggestionId}) failed at save.",
                suggestion.Title, suggestion.Id);
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Title"] = $"Could not publish '{suggestion.Title}' — most likely a recipe with the same title was created at the same time. Reload and try again.",
            });
        }

        _logger.LogInformation(
            "Approved recipe suggestion '{Title}' (id={SuggestionId}) → recipe id={RecipeId}.",
            suggestion.Title, suggestion.Id, recipe.Id);
        return recipe;
    }

    /// <summary>Rejects a pending suggestion. Optional <paramref name="note"/> is recorded for the audit trail.</summary>
    public async Task RejectAsync(int suggestionId, string? note, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var deciderId = RequireUserId();

        var suggestion = await _db.RecipeSuggestions
            .FirstOrDefaultAsync(s => s.Id == suggestionId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Suggestion with id {suggestionId} was not found.",
            });

        if (suggestion.Decision != RecipeSuggestionDecision.Pending)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Decision"] = $"Suggestion is already {suggestion.Decision.ToString().ToLowerInvariant()}.",
            });
        }

        suggestion.Decision = RecipeSuggestionDecision.Rejected;
        suggestion.DecidedAt = DateTime.UtcNow;
        suggestion.DecidedByUserId = deciderId;
        var trimmedNote = note?.Trim();
        suggestion.DecisionNote = string.IsNullOrEmpty(trimmedNote) ? null : trimmedNote;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Rejected recipe suggestion '{Title}' (id={Id}).",
            suggestion.Title, suggestion.Id);
    }

    private async Task ValidateAsync(RecipeSuggestionInput input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        var title = input.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(title))
        {
            errors[nameof(input.Title)] = "Title is required.";
        }
        else if (title.Length > RecipeService.MaxTitleLength)
        {
            errors[nameof(input.Title)] = $"Title must be {RecipeService.MaxTitleLength} characters or fewer.";
        }

        var description = input.Description?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(description))
        {
            errors[nameof(input.Description)] = "Description is required.";
        }
        else if (description.Length > RecipeService.MaxDescriptionLength)
        {
            errors[nameof(input.Description)] = $"Description must be {RecipeService.MaxDescriptionLength} characters or fewer.";
        }

        if ((input.Keywords ?? string.Empty).Length > RecipeService.MaxKeywordsLength)
        {
            errors[nameof(input.Keywords)] = $"Keywords must be {RecipeService.MaxKeywordsLength} characters or fewer.";
        }

        if (!Enum.IsDefined(typeof(RecipeType), input.Type))
        {
            errors[nameof(input.Type)] = "Unknown recipe type.";
        }

        if (input.EstimatedValueHours is { } hours)
        {
            if (hours < 0)
            {
                errors[nameof(input.EstimatedValueHours)] = "Estimated value (hours) can't be negative.";
            }
            else if (hours > RecipeService.MaxEstimatedValueHours)
            {
                errors[nameof(input.EstimatedValueHours)] = $"Estimated value (hours) must be {RecipeService.MaxEstimatedValueHours:0} or fewer.";
            }
        }

        await RecipeService.ValidateMetadataAsync(
            _db, input.Instructions, input.MinimumApplicationVersionId, errors, ct);

        RecipeService.ValidateFiles(input.Files, errors);

        if (errors.Count > 0)
        {
            throw new PlanValidationException(errors);
        }
    }

    private static void ReconcileFiles(RecipeSuggestion existing, IReadOnlyList<RecipeFileInput> inputs, int orgId)
    {
        var existingFiles = existing.Files.OrderBy(f => f.Ordering).ToList();

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var name = input.FileName.Trim();
            var relPath = RecipeService.NormaliseRelativePath(input.RelativePath);
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
                existing.Files.Add(new RecipeSuggestionFile
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

/// <summary>Form-shaped input from <c>/cookbook/suggest</c>.</summary>
public record RecipeSuggestionInput(
    string Title,
    string Description,
    string Keywords,
    RecipeType Type,
    IReadOnlyList<RecipeFileInput> Files,
    string? Instructions = null,
    int? MinimumApplicationVersionId = null,
    decimal? EstimatedValueHours = null);
