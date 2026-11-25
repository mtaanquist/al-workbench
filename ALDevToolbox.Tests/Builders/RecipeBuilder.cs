using ALDevToolbox.Tests.Infrastructure;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Tests.Builders;

/// <summary>
/// Constructs <see cref="Recipe"/> rows pre-populated with sensible defaults.
/// Keep this small — fluent extensions only, no canned variants beyond what
/// multiple tests share.
/// </summary>
public static class RecipeBuilder
{
    public const int DefaultOrganizationId = TestDb.DefaultOrgId;

    public static Recipe Default(
        string title = "Test Recipe",
        int organizationId = DefaultOrganizationId,
        RecipeType type = RecipeType.Snippet) => new()
    {
        OrganizationId = organizationId,
        Title = title,
        Description = "Synthetic recipe used in tests.",
        Keywords = "test sample",
        Type = type,
        Deprecated = false,
        CreatedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
    };

    public static Recipe WithFile(this Recipe recipe, string fileName, string content, string relativePath = "")
    {
        recipe.Files.Add(new RecipeFile
        {
            OrganizationId = recipe.OrganizationId,
            Ordering = recipe.Files.Count,
            RelativePath = relativePath,
            FileName = fileName,
            Content = content,
        });
        return recipe;
    }
}
