using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Templates;

/// <summary>
/// Guards the per-organisation default template flag: <c>SetDefaultAsync</c>
/// clears the previous default, refuses to flag a soft-deleted or deprecated
/// row, and the soft-delete / deprecate paths drop the flag so the filtered
/// unique index on (organization_id, is_default) never collides.
/// </summary>
public sealed class DefaultTemplateTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SetDefault_clears_the_previous_default_in_the_same_org()
    {
        int firstId, secondId;
        await using (var ctx = _db.NewContext())
        {
            var first = TemplateBuilder.Default("runtime-a", runtime: "13");
            first.IsDefault = true;
            ctx.RuntimeTemplates.Add(first);
            ctx.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-b", runtime: "15"));
            await ctx.SaveChangesAsync();
            firstId = ctx.RuntimeTemplates.Single(t => t.Key == "runtime-a").Id;
            secondId = ctx.RuntimeTemplates.Single(t => t.Key == "runtime-b").Id;
        }

        await using (var ctx = _db.NewContext())
        {
            await NewTemplateService(ctx).SetDefaultAsync(secondId);
        }

        await using (var verify = _db.NewContext())
        {
            var rows = await verify.RuntimeTemplates.AsNoTracking().ToListAsync();
            rows.Should().HaveCount(2);
            rows.Single(t => t.Id == firstId).IsDefault.Should().BeFalse();
            rows.Single(t => t.Id == secondId).IsDefault.Should().BeTrue();
        }
    }

    [Fact]
    public async Task SetDefault_is_a_noop_when_the_target_is_already_the_default()
    {
        int id;
        DateTime originalUpdatedAt;
        await using (var ctx = _db.NewContext())
        {
            var t = TemplateBuilder.Default("runtime-noop");
            t.IsDefault = true;
            ctx.RuntimeTemplates.Add(t);
            await ctx.SaveChangesAsync();
            id = ctx.RuntimeTemplates.Single().Id;
            originalUpdatedAt = ctx.RuntimeTemplates.Single().UpdatedAt;
        }

        await using (var ctx = _db.NewContext())
        {
            await NewTemplateService(ctx).SetDefaultAsync(id);
        }

        await using (var verify = _db.NewContext())
        {
            var updated = await verify.RuntimeTemplates.AsNoTracking()
                .Where(t => t.Id == id)
                .Select(t => t.UpdatedAt)
                .SingleAsync();
            updated.Should().Be(originalUpdatedAt);
        }
    }

    [Fact]
    public async Task SetDefault_refuses_to_flag_a_soft_deleted_template()
    {
        int id;
        await using (var ctx = _db.NewContext())
        {
            var t = TemplateBuilder.Default("runtime-deleted");
            t.DeletedAt = DateTime.UtcNow;
            ctx.RuntimeTemplates.Add(t);
            await ctx.SaveChangesAsync();
            id = ctx.RuntimeTemplates.IgnoreQueryFilters().Single().Id;
        }

        await using (var ctx = _db.NewContext())
        {
            var act = async () => await NewTemplateService(ctx).SetDefaultAsync(id);
            await act.Should().ThrowAsync<PlanValidationException>();
        }
    }

    [Fact]
    public async Task SetDefault_refuses_to_flag_a_deprecated_template()
    {
        int id;
        await using (var ctx = _db.NewContext())
        {
            var t = TemplateBuilder.Default("runtime-deprecated");
            t.Deprecated = true;
            ctx.RuntimeTemplates.Add(t);
            await ctx.SaveChangesAsync();
            id = ctx.RuntimeTemplates.Single().Id;
        }

        await using (var ctx = _db.NewContext())
        {
            var act = async () => await NewTemplateService(ctx).SetDefaultAsync(id);
            await act.Should().ThrowAsync<PlanValidationException>();
        }
    }

    [Fact]
    public async Task SoftDelete_clears_the_default_flag()
    {
        int id;
        await using (var ctx = _db.NewContext())
        {
            var t = TemplateBuilder.Default("runtime-soft-default");
            t.IsDefault = true;
            ctx.RuntimeTemplates.Add(t);
            await ctx.SaveChangesAsync();
            id = ctx.RuntimeTemplates.Single().Id;
        }

        await using (var ctx = _db.NewContext())
        {
            await NewTemplateService(ctx).SoftDeleteAsync(id);
        }

        await using (var verify = _db.NewContext())
        {
            var row = await verify.RuntimeTemplates.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(t => t.Id == id);
            row.IsDefault.Should().BeFalse();
        }
    }

    [Fact]
    public async Task BulkDeprecate_clears_the_default_flag()
    {
        int id;
        await using (var ctx = _db.NewContext())
        {
            var t = TemplateBuilder.Default("runtime-deprecate-default");
            t.IsDefault = true;
            ctx.RuntimeTemplates.Add(t);
            await ctx.SaveChangesAsync();
            id = ctx.RuntimeTemplates.Single().Id;
        }

        await using (var ctx = _db.NewContext())
        {
            await NewTemplateService(ctx).BulkDeprecateAsync(new[] { id });
        }

        await using (var verify = _db.NewContext())
        {
            var row = await verify.RuntimeTemplates.AsNoTracking().SingleAsync(t => t.Id == id);
            row.IsDefault.Should().BeFalse();
            row.Deprecated.Should().BeTrue();
        }
    }

    [Fact]
    public async Task GetDefault_returns_null_when_the_default_row_is_soft_deleted()
    {
        await using (var ctx = _db.NewContext())
        {
            var t = TemplateBuilder.Default("runtime-stale-default");
            t.IsDefault = true;
            t.DeletedAt = DateTime.UtcNow;
            ctx.RuntimeTemplates.Add(t);
            await ctx.SaveChangesAsync();
        }

        await using (var verify = _db.NewContext())
        {
            var def = await NewTemplateService(verify).GetDefaultAsync();
            def.Should().BeNull();
        }
    }

    private TemplateService NewTemplateService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<TemplateService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx), _db.NewQuotaGuard(ctx));
}
