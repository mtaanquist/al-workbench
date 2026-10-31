using ALDevToolbox.Domain.Entities;
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
/// Happy-path and validation coverage for <see cref="RecipeService"/>:
/// create, update (file reconcile), soft-delete/restore, deprecate, and the
/// validation rules that protect the API contract.
/// </summary>
public sealed class RecipeServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Create_persists_recipe_and_files_in_order()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var input = new RecipeInput(
            Title: "Doc Attachment Factbox",
            Description: "Expose the standard factbox on an unsupported table.",
            Keywords: "  Factbox Attachment Subscriber ",
            Type: RecipeType.Pattern,
            Deprecated: false,
            Files: new[]
            {
                new RecipeFileInput("EventSub.Codeunit.al", "// subscriber"),
                new RecipeFileInput("MyPageExt.PageExt.al", "// page ext"),
            });

        var recipe = await svc.CreateAsync(input);

        await using var read = _db.NewContext();
        var persisted = await read.Recipes
            .Include(s => s.Files.OrderBy(f => f.Ordering))
            .SingleAsync(s => s.Id == recipe.Id);
        persisted.Title.Should().Be("Doc Attachment Factbox");
        persisted.Type.Should().Be(RecipeType.Pattern);
        persisted.Keywords.Should().Be("factbox,attachment,subscriber",
            "keywords are normalised to lower-case and stored comma-separated");
        persisted.Files.Select(f => f.FileName).Should().Equal(
            "EventSub.Codeunit.al", "MyPageExt.PageExt.al");
        persisted.Files.Select(f => f.Ordering).Should().Equal(0, 1);
    }

    [Fact]
    public async Task Update_reconciles_files_in_place_and_preserves_ids()
    {
        int recipeId;
        int fileAId, fileBId;
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Reorder Me")
                .WithFile("A.al", "// a")
                .WithFile("B.al", "// b");
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
            recipeId = s.Id;
            fileAId = s.Files.Single(f => f.FileName == "A.al").Id;
            fileBId = s.Files.Single(f => f.FileName == "B.al").Id;
        }

        var input = new RecipeInput(
            Title: "Reorder Me",
            Description: "Synthetic recipe used in tests.",
            Keywords: "test",
            Type: RecipeType.Snippet,
            Deprecated: false,
            Files: new[]
            {
                new RecipeFileInput("B.al", "// b edited"),
                new RecipeFileInput("A.al", "// a"),
            });

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(recipeId, input);
        }

        await using var verify = _db.NewContext();
        var files = await verify.RecipeFiles
            .Where(f => f.RecipeId == recipeId)
            .OrderBy(f => f.Ordering)
            .ToListAsync();
        files.Select(f => f.FileName).Should().Equal("B.al", "A.al");
        files[0].Id.Should().Be(fileAId, "reconciler mutates row[0] in place");
        files[0].Content.Should().Be("// b edited");
        files[1].Id.Should().Be(fileBId);
    }

    [Fact]
    public async Task Update_appends_and_removes_files_when_list_shape_changes()
    {
        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Resize Me")
                .WithFile("A.al", "// a")
                .WithFile("B.al", "// b");
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
            recipeId = s.Id;
        }

        // Drop B, add C.
        var input = new RecipeInput(
            "Resize Me", "Synthetic recipe used in tests.", "test", RecipeType.Snippet, false,
            new[]
            {
                new RecipeFileInput("A.al", "// a"),
                new RecipeFileInput("C.al", "// c"),
            });
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(recipeId, input);
        }

        await using var verify = _db.NewContext();
        var names = await verify.RecipeFiles
            .Where(f => f.RecipeId == recipeId)
            .OrderBy(f => f.Ordering)
            .Select(f => f.FileName)
            .ToListAsync();
        names.Should().Equal("A.al", "C.al");
    }

    /// <summary>
    /// The editor page keeps its context between saves. An assistant's
    /// update_recipe drops a file in between, and the page's next save must not
    /// update or delete that file a second time (#1600).
    /// </summary>
    [Fact]
    public async Task A_page_that_saved_once_can_save_again_after_someone_else_removed_a_file()
    {
        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Shared Edit").WithFile("A.al", "// a").WithFile("B.al", "// b");
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
            recipeId = s.Id;
        }
        RecipeInput With(params RecipeFileInput[] files) =>
            new("Shared Edit", "Synthetic recipe used in tests.", "test", RecipeType.Snippet, false, files);

        await using var page = _db.NewContext();
        var pageService = NewService(page);
        await pageService.UpdateAsync(recipeId, With(new RecipeFileInput("A.al", "// a"), new RecipeFileInput("B.al", "// b")));
        await using (var other = _db.NewContext())
        {
            await NewService(other).UpdateAsync(recipeId, With(new RecipeFileInput("A.al", "// a")));
        }
        await pageService.UpdateAsync(recipeId, With(new RecipeFileInput("A.al", "// a fixed"), new RecipeFileInput("B.al", "// b")));

        await using var verify = _db.NewContext();
        (await verify.RecipeFiles.Where(f => f.RecipeId == recipeId).OrderBy(f => f.Ordering).Select(f => f.Content).ToListAsync())
            .Should().Equal("// a fixed", "// b");
    }

    [Fact]
    public async Task Create_rejects_more_files_than_a_recipe_may_hold()
    {
        await using var ctx = _db.NewContext();
        var files = Enumerable.Range(0, RecipeService.MaxFiles + 1)
            .Select(i => new RecipeFileInput($"File{i}.al", "// x"))
            .ToArray();

        Func<Task> act = () => NewService(ctx).CreateAsync(new RecipeInput(
            "Too Many", "Synthetic recipe used in tests.", "test", RecipeType.Snippet, false, files));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Files");
    }

    [Fact]
    public async Task SoftDelete_then_restore_round_trips_DeletedAt()
    {
        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Delete Me").WithFile("A.al", "// a");
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
            recipeId = s.Id;
        }

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).SoftDeleteAsync(recipeId);
        }
        await using (var verify = _db.NewContext())
        {
            (await verify.Recipes.SingleAsync(s => s.Id == recipeId)).DeletedAt.Should().NotBeNull();
        }

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RestoreAsync(recipeId);
        }
        await using (var verify = _db.NewContext())
        {
            (await verify.Recipes.SingleAsync(s => s.Id == recipeId)).DeletedAt.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_deleted_recipe_is_not_served_but_the_admin_editor_still_opens_it()
    {
        // A withdrawn recipe must not reach the recipe page, the download,
        // apply-to-repository or an agent (#1251); the editor is where it is
        // looked at and restored, so it still loads there.
        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Withdrawn").WithFile("A.al", "// a");
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
            recipeId = s.Id;
        }
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).SoftDeleteAsync(recipeId);
        }

        await using var read = _db.NewContext();
        var svc = NewService(read);
        (await svc.GetAsync(recipeId)).Should().BeNull();
        var forAdmin = await svc.GetForAdminAsync(recipeId);
        forAdmin.Should().NotBeNull();
        forAdmin!.Files.Should().ContainSingle();
    }

    [Fact]
    public async Task Restore_refuses_when_a_live_recipe_took_the_title()
    {
        int deletedId;
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Contested").WithFile("A.al", "// a");
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
            deletedId = s.Id;
        }

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).SoftDeleteAsync(deletedId);
        }

        // A live recipe takes the same title while the first sits deleted.
        await using (var ctx = _db.NewContext())
        {
            ctx.Recipes.Add(RecipeBuilder.Default("Contested").WithFile("B.al", "// b"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        Func<Task> act = () => NewService(ctx2).RestoreAsync(deletedId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Title");

        await using var verify = _db.NewContext();
        (await verify.Recipes.SingleAsync(s => s.Id == deletedId)).DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SetDeprecated_flips_the_flag()
    {
        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Deprecate Me").WithFile("A.al", "// a");
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
            recipeId = s.Id;
        }

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).SetDeprecatedAsync(recipeId, deprecated: true);
        }
        await using var verify = _db.NewContext();
        (await verify.Recipes.SingleAsync(s => s.Id == recipeId)).Deprecated.Should().BeTrue();
    }

    [Fact]
    public async Task Create_rejects_missing_title_with_field_keyed_error()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var input = new RecipeInput(
            Title: "  ",
            Description: "Whatever",
            Keywords: "",
            Type: RecipeType.Snippet,
            Deprecated: false,
            Files: new[] { new RecipeFileInput("A.al", "// a") });

        Func<Task> act = () => svc.CreateAsync(input);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Title");
    }

    [Fact]
    public async Task Create_rejects_duplicate_title_within_org()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Recipes.Add(RecipeBuilder.Default("Taken").WithFile("A.al", "// a"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var svc = NewService(ctx2);
        Func<Task> act = () => svc.CreateAsync(new RecipeInput(
            Title: "Taken",
            Description: "Different body, same title.",
            Keywords: "",
            Type: RecipeType.Snippet,
            Deprecated: false,
            Files: new[] { new RecipeFileInput("B.al", "// b") }));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Title");
    }

    [Fact]
    public async Task Create_rejects_file_names_with_slashes_or_dot_dot()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var input = new RecipeInput(
            Title: "Bad Names",
            Description: "Bad file names should fail.",
            Keywords: "",
            Type: RecipeType.Snippet,
            Deprecated: false,
            Files: new[]
            {
                new RecipeFileInput("src/A.al", "// nope"),
                new RecipeFileInput("..\\B.al", "// nope"),
            });

        var ex = await Assert.ThrowsAsync<PlanValidationException>(() => svc.CreateAsync(input));
        ex.Errors.Should().ContainKey("Files[0].FileName");
        ex.Errors.Should().ContainKey("Files[1].FileName");
    }

    [Fact]
    public async Task Create_rejects_when_no_files_supplied()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var input = new RecipeInput(
            "Empty", "No files at all.", "", RecipeType.Snippet, false, Array.Empty<RecipeFileInput>());

        var ex = await Assert.ThrowsAsync<PlanValidationException>(() => svc.CreateAsync(input));
        ex.Errors.Should().ContainKey("Files");
    }

    [Fact]
    public async Task Create_rejects_duplicate_file_paths_case_insensitively()
    {
        // Two files with the same RelativePath/FileName are a duplicate. The
        // pre-rename "duplicate file name" rule generalises to the full path.
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var input = new RecipeInput(
            "Dupes", "Two files with the same name.", "", RecipeType.Snippet, false,
            new[]
            {
                new RecipeFileInput("Sample.al", "// 1"),
                new RecipeFileInput("sample.AL", "// 2"),
            });

        var ex = await Assert.ThrowsAsync<PlanValidationException>(() => svc.CreateAsync(input));
        ex.Errors.Should().ContainKey("Files[1].FileName");
    }

    [Fact]
    public async Task Create_allows_same_file_name_in_different_folders()
    {
        // The duplicate guard now keys on the joined RelativePath/FileName,
        // so an Init.al at the root and another in src/ are not a clash.
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var recipe = await svc.CreateAsync(new RecipeInput(
            "Same Name Diff Folder", "Body.", "", RecipeType.Pattern, false,
            new[]
            {
                new RecipeFileInput("Init.al", "// root"),
                new RecipeFileInput("Init.al", "// nested", "Sales"),
            }));
        recipe.Files.Should().HaveCount(2);
    }

    [Fact]
    public async Task SoftDelete_unknown_id_raises_validation_error()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() => svc.SoftDeleteAsync(9999));
        ex.Errors.Should().ContainKey("Id");
    }

    [Fact]
    public async Task Cross_org_data_is_invisible()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Recipes.Add(RecipeBuilder.Default("Org1", organizationId: TestDb.DefaultOrgId)
                .WithFile("A.al", "// a"));
            ctx.Recipes.Add(RecipeBuilder.Default("Org2", organizationId: TestDb.OtherOrgId)
                .WithFile("B.al", "// b"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var svc = NewService(ctx2);
        var rows = await svc.SearchAsync(query: null);
        rows.Should().HaveCount(1);
        rows[0].Title.Should().Be("Org1");
    }

    private RecipeService NewService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<RecipeService>.Instance, _db.OrgContext, _db.NewQuotaGuard(ctx));
}
