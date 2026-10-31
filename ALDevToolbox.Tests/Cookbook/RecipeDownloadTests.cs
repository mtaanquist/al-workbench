using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Cookbook;

namespace ALDevToolbox.Tests.Cookbook;

/// <summary>
/// Coverage for the customer download tracking added with the Cookbook
/// improvements: <see cref="RecipeService.RecordDownloadAsync"/>,
/// <see cref="RecipeService.GetDownloadsAsync"/>, and
/// <see cref="RecipeService.GetCustomerSuggestionsAsync"/>. The point is tracing a
/// later bug in a recipe to whoever received it -- so copies count too, and the
/// customer name is asked for but not required (#539), and a name that matches
/// one of the org's projects is stamped with that project's id so the question
/// becomes a join rather than a string match (#541).
/// </summary>
public sealed class RecipeDownloadTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<int> SeedRecipeAsync(string title = "Downloadable", int orgId = TestDb.DefaultOrgId)
    {
        await using var ctx = _db.NewContext();
        var recipe = RecipeBuilder.Default(title, organizationId: orgId).WithFile("A.al", "// a");
        ctx.Recipes.Add(recipe);
        await ctx.SaveChangesAsync();
        return recipe.Id;
    }

    private async Task<int> SeedUserAsync(int userId = 700)
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = userId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"u{userId}@example.com",
            PasswordHash = "x",
            DisplayName = "Downloader",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
        });
        await ctx.SaveChangesAsync();
        return userId;
    }

    private async Task<int> SeedProjectAsync(string name, bool deleted = false)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            CreatedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
            DeletedAt = deleted ? new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) : null,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    [Fact]
    public async Task RecordDownload_inserts_row_scoped_to_org()
    {
        var recipeId = await SeedRecipeAsync();
        var userId = await SeedUserAsync();

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordDownloadAsync(recipeId, "  Acme A/S  ", userId);
        }

        await using var verify = _db.NewContext();
        var row = await verify.RecipeDownloads.SingleAsync(d => d.RecipeId == recipeId);
        row.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        row.CustomerName.Should().Be("Acme A/S", "the customer name is trimmed");
        row.DownloadedByUserId.Should().Be(userId);
        row.DownloadedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task GetDownloads_loads_the_downloading_user_nav()
    {
        var recipeId = await SeedRecipeAsync();
        var userId = await SeedUserAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordDownloadAsync(recipeId, "Acme", userId);
        }

        await using var read = _db.NewContext();
        var downloads = await NewService(read).GetDownloadsAsync(recipeId);
        downloads.Should().ContainSingle();
        downloads[0].DownloadedByUser!.Email.Should().Be($"u{userId}@example.com");
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public async Task RecordDownload_accepts_a_blank_customer_and_stores_null(string? customer)
    {
        // Gating the download on this field collected "test" and "x" from
        // everyone downloading for a demo. Null is the honest answer and the
        // admin panel renders it as "Not recorded". See issue #539.
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordDownloadAsync(recipeId, customer, userId: null);
        }

        await using var verify = _db.NewContext();
        var row = await verify.RecipeDownloads.SingleAsync(d => d.RecipeId == recipeId);
        row.CustomerName.Should().BeNull();
        row.Source.Should().Be(RecipeUseSource.Download);
    }

    [Fact]
    public async Task RecordDownload_rejects_an_oversized_customer_name()
    {
        var recipeId = await SeedRecipeAsync();
        await using var ctx = _db.NewContext();
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            NewService(ctx).RecordDownloadAsync(
                recipeId, new string('x', RecipeService.MaxCustomerNameLength + 1), userId: null));
        ex.Errors.Should().ContainKey("CustomerName");
    }

    [Fact]
    public async Task RecordCopy_records_a_use_with_no_customer()
    {
        // Single-file recipes are almost always taken with Copy, which never
        // opened the download modal -- so the history used to under-count
        // exactly the recipes people used most.
        var recipeId = await SeedRecipeAsync();
        var userId = await SeedUserAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordCopyAsync(recipeId, userId);
        }

        await using var verify = _db.NewContext();
        var row = await verify.RecipeDownloads.SingleAsync(d => d.RecipeId == recipeId);
        row.Source.Should().Be(RecipeUseSource.Copy);
        row.CustomerName.Should().BeNull();
        row.DownloadedByUserId.Should().Be(userId);
    }

    [Fact]
    public async Task RecordCopy_rejects_unknown_recipe()
    {
        await using var ctx = _db.NewContext();
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            NewService(ctx).RecordCopyAsync(9999, userId: null));
        ex.Errors.Should().ContainKey("Id");
    }

    [Fact]
    public async Task GetCustomerNames_skips_uses_with_no_customer()
    {
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            await svc.RecordDownloadAsync(recipeId, "CRONUS A/S", userId: null);
            await svc.RecordDownloadAsync(recipeId, "  ", userId: null);
            await svc.RecordCopyAsync(recipeId, userId: null);
        }

        await using var read = _db.NewContext();
        var names = await NewService(read).GetCustomerSuggestionsAsync();
        names.Should().Equal(
            new[] { "CRONUS A/S" },
            "a blank download and a copy contribute no name");
    }

    [Fact]
    public async Task RecordDownload_rejects_unknown_recipe()
    {
        await using var ctx = _db.NewContext();
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            NewService(ctx).RecordDownloadAsync(9999, "Acme", userId: null));
        ex.Errors.Should().ContainKey("Id");
    }

    [Fact]
    public async Task GetDownloads_returns_newest_first()
    {
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            // No sleep: GetDownloadsAsync tiebreaks on the monotonic Id, so the
            // second insert sorts first even within the same timestamp tick. #395
            await svc.RecordDownloadAsync(recipeId, "First", userId: null);
            await svc.RecordDownloadAsync(recipeId, "Second", userId: null);
        }

        await using var read = _db.NewContext();
        var downloads = await NewService(read).GetDownloadsAsync(recipeId);
        downloads.Select(d => d.CustomerName).Should().Equal("Second", "First");
    }

    [Fact]
    public async Task GetCustomerNames_returns_distinct_sorted_names()
    {
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            await svc.RecordDownloadAsync(recipeId, "Beta", userId: null);
            await svc.RecordDownloadAsync(recipeId, "Alpha", userId: null);
            await svc.RecordDownloadAsync(recipeId, "Beta", userId: null);
        }

        await using var read = _db.NewContext();
        var names = await NewService(read).GetCustomerSuggestionsAsync();
        names.Should().Equal("Alpha", "Beta");
    }

    [Fact]
    public async Task Cross_org_download_history_is_invisible()
    {
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordDownloadAsync(recipeId, "Acme", userId: null);
        }

        // Switch the ambient org to the other tenant; the filter must hide it.
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        await using var read = _db.NewContext();
        var names = await NewService(read).GetCustomerSuggestionsAsync();
        names.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCustomerSuggestions_offers_active_projects()
    {
        // The app already knows the customers -- somebody spelled each of them
        // once when they set the project up. #541
        await SeedProjectAsync("CRONUS A/S");
        await SeedProjectAsync("Retired Customer", deleted: true);
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordDownloadAsync(recipeId, "Nordwind GmbH", userId: null);
        }

        await using var read = _db.NewContext();
        var names = await NewService(read).GetCustomerSuggestionsAsync();
        names.Should().Equal(
            new[] { "CRONUS A/S", "Nordwind GmbH" },
            "active projects and recorded names merge; a soft-deleted project is gone");
    }

    [Fact]
    public async Task GetCustomerSuggestions_dedups_case_insensitively_preferring_the_project_spelling()
    {
        await SeedProjectAsync("CRONUS A/S");
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordDownloadAsync(recipeId, "cronus a/s", userId: null);
        }

        await using var read = _db.NewContext();
        var names = await NewService(read).GetCustomerSuggestionsAsync();
        names.Should().Equal(
            new[] { "CRONUS A/S" },
            "the project's spelling is the one the rest of the tool labels things with");
    }

    [Fact]
    public async Task RecordDownload_stamps_the_project_on_a_case_insensitive_name_match()
    {
        // Project.Name is unique per org among active rows, so "picked a project"
        // and "typed exactly a project's name" are the same thing. #541
        var projectId = await SeedProjectAsync("CRONUS A/S");
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordDownloadAsync(recipeId, "  cronus a/s  ", userId: null);
        }

        await using var verify = _db.NewContext();
        var row = await verify.RecipeDownloads.SingleAsync(d => d.RecipeId == recipeId);
        row.ProjectId.Should().Be(projectId);
        row.CustomerName.Should().Be("cronus a/s", "the name is still stored as typed -- it is the label");
    }

    /// <summary>
    /// A Private solution's exact name typed by someone outside its team is
    /// recorded as a plain customer name: the download neither lands on the
    /// solution nor confirms the name exists (#1545).
    /// </summary>
    [Fact]
    public async Task RecordDownload_does_not_match_a_private_solution_the_downloader_cannot_see()
    {
        var projectId = await SeedProjectAsync("CRONUS Private");
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjects.Where(p => p.Id == projectId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Visibility, ProjectVisibility.Private));
        }
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordDownloadAsync(recipeId, "CRONUS Private", userId: null);
        }

        await using var verify = _db.NewContext();
        var row = await verify.RecipeDownloads.SingleAsync(d => d.RecipeId == recipeId);
        row.ProjectId.Should().BeNull();
        row.CustomerName.Should().Be("CRONUS Private");
    }

    [Fact]
    public async Task RecordDownload_leaves_the_project_null_when_nothing_matches()
    {
        await SeedProjectAsync("CRONUS A/S");
        await SeedProjectAsync("Nordwind GmbH", deleted: true);
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            // A partial match is not a match, a soft-deleted project is not a
            // match, and a blank name has nothing to match.
            await svc.RecordDownloadAsync(recipeId, "CRONUS", userId: null);
            await svc.RecordDownloadAsync(recipeId, "Nordwind GmbH", userId: null);
            await svc.RecordDownloadAsync(recipeId, null, userId: null);
        }

        await using var verify = _db.NewContext();
        var rows = await verify.RecipeDownloads.Where(d => d.RecipeId == recipeId).ToListAsync();
        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(d => d.ProjectId == null);
    }

    [Fact]
    public async Task RecordDownload_does_not_match_a_project_in_another_org()
    {
        // The EF query filter is the only thing scoping the lookup -- prove it.
        await using (var seed = _db.NewContext())
        {
            seed.OeProjects.Add(new OeProject
            {
                OrganizationId = TestDb.OtherOrgId,
                Name = "CRONUS A/S",
                CreatedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordDownloadAsync(recipeId, "CRONUS A/S", userId: null);
        }

        await using var verify = _db.NewContext();
        var row = await verify.RecipeDownloads.SingleAsync(d => d.RecipeId == recipeId);
        row.ProjectId.Should().BeNull();
    }

    [Fact]
    public async Task GetDownloads_loads_the_matched_project_nav()
    {
        var projectId = await SeedProjectAsync("CRONUS A/S");
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            await svc.RecordDownloadAsync(recipeId, "CRONUS A/S", userId: null);
            await svc.RecordDownloadAsync(recipeId, "Some Other Customer", userId: null);
        }

        await using var read = _db.NewContext();
        var downloads = await NewService(read).GetDownloadsAsync(recipeId);
        downloads.Should().HaveCount(2);
        downloads.Single(d => d.CustomerName == "CRONUS A/S").Project!.Id.Should().Be(projectId);
        downloads.Single(d => d.CustomerName == "Some Other Customer").Project.Should().BeNull();
    }

    [Fact]
    public async Task RecordRepositoryApply_records_the_repository_and_the_matched_project()
    {
        // The apply is a download that also names a place, so a later fix knows
        // which repositories to open a pull request against. #626
        var projectId = await SeedProjectAsync("CRONUS A/S");
        var recipeId = await SeedRecipeAsync();
        var userId = await SeedUserAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordRepositoryApplyAsync(
                recipeId, "  cronus-dk/solution-a  ", "  cronus a/s  ", userId);
        }

        await using var verify = _db.NewContext();
        var row = await verify.RecipeDownloads.SingleAsync(d => d.RecipeId == recipeId);
        row.Source.Should().Be(RecipeUseSource.Repository);
        row.Repository.Should().Be("cronus-dk/solution-a", "the repository is trimmed");
        row.CustomerName.Should().Be("cronus a/s");
        row.ProjectId.Should().Be(projectId);
        row.DownloadedByUserId.Should().Be(userId);
    }

    [Fact]
    public async Task RecordRepositoryApply_accepts_no_customer()
    {
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordRepositoryApplyAsync(recipeId, "cronus-dk/solution-a", null, userId: null);
        }

        await using var verify = _db.NewContext();
        var row = await verify.RecipeDownloads.SingleAsync(d => d.RecipeId == recipeId);
        row.CustomerName.Should().BeNull();
        row.ProjectId.Should().BeNull();
        row.Repository.Should().Be("cronus-dk/solution-a");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RecordRepositoryApply_rejects_a_missing_repository(string repository)
    {
        var recipeId = await SeedRecipeAsync();
        await using var ctx = _db.NewContext();
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            NewService(ctx).RecordRepositoryApplyAsync(recipeId, repository, null, userId: null));
        ex.Errors.Should().ContainKey("GitHubRepository");
    }

    [Fact]
    public async Task RecordRepositoryApply_rejects_an_oversized_repository()
    {
        var recipeId = await SeedRecipeAsync();
        await using var ctx = _db.NewContext();
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            NewService(ctx).RecordRepositoryApplyAsync(
                recipeId, new string('x', RecipeService.MaxRepositoryLength + 1), null, userId: null));
        ex.Errors.Should().ContainKey("GitHubRepository");
    }

    [Fact]
    public async Task RecordRepositoryApply_rejects_an_oversized_customer_name()
    {
        var recipeId = await SeedRecipeAsync();
        await using var ctx = _db.NewContext();
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            NewService(ctx).RecordRepositoryApplyAsync(
                recipeId, "cronus-dk/solution-a",
                new string('x', RecipeService.MaxCustomerNameLength + 1), userId: null));
        ex.Errors.Should().ContainKey("CustomerName");
    }

    [Fact]
    public async Task RecordRepositoryApply_rejects_unknown_recipe()
    {
        await using var ctx = _db.NewContext();
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            NewService(ctx).RecordRepositoryApplyAsync(9999, "cronus-dk/solution-a", null, userId: null));
        ex.Errors.Should().ContainKey("Id");
    }

    [Fact]
    public async Task GetAppliedRepositories_returns_each_repository_once_most_recent_first()
    {
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            await svc.RecordRepositoryApplyAsync(recipeId, "cronus-dk/first", null, userId: null);
            await svc.RecordRepositoryApplyAsync(recipeId, "cronus-dk/second", null, userId: null);
            // Applying again moves the first one back to the front of the list.
            await svc.RecordRepositoryApplyAsync(recipeId, "cronus-dk/first", null, userId: null);
            // Downloads and copies name no place, so they contribute nothing.
            await svc.RecordDownloadAsync(recipeId, "CRONUS A/S", userId: null);
            await svc.RecordCopyAsync(recipeId, userId: null);
        }

        await using var read = _db.NewContext();
        var repositories = await NewService(read).GetAppliedRepositoriesAsync(recipeId);
        repositories.Should().Equal("cronus-dk/first", "cronus-dk/second");
    }

    [Fact]
    public async Task GetAppliedRepositories_is_empty_for_a_recipe_nobody_has_applied()
    {
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordDownloadAsync(recipeId, "CRONUS A/S", userId: null);
        }

        await using var read = _db.NewContext();
        (await NewService(read).GetAppliedRepositoriesAsync(recipeId)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetAppliedRepositories_does_not_cross_organisations()
    {
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordRepositoryApplyAsync(recipeId, "cronus-dk/first", null, userId: null);
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        await using var read = _db.NewContext();
        (await NewService(read).GetAppliedRepositoriesAsync(recipeId)).Should().BeEmpty();
    }

    private RecipeService NewService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<RecipeService>.Instance, _db.OrgContext, _db.NewQuotaGuard(ctx), new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext));
}
