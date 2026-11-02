using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Templates;

/// <summary>
/// Guards the soft-delete contract for templates and modules: a soft-deleted
/// row is invisible to the default query filter (which is what end-user
/// dropdowns and the templates browser rely on), stays visible to admin
/// queries that opt into <c>includeDeleted</c>, and round-trips cleanly through
/// restore. The state mutations also have to update <c>UpdatedAt</c> so the
/// audit log can order the timeline.
/// </summary>
public sealed class SoftDeleteRoundTripTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Template_soft_delete_hides_the_row_from_active_queries_and_restore_brings_it_back()
    {
        int templateId;
        await using (var ctx = _db.NewContext())
        {
            ctx.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-soft-delete"));
            await ctx.SaveChangesAsync();
            templateId = ctx.RuntimeTemplates.Single().Id;
        }

        // Soft-delete.
        await using (var ctx = _db.NewContext())
        {
            await NewTemplateService(ctx).SoftDeleteAsync(templateId);
        }

        await using (var verify = _db.NewContext())
        {
            (await NewTemplateService(verify).GetTemplatesAsync())
                .Should().BeEmpty();
            // Admin view with includeDeleted=true sees the row, with DeletedAt set.
            var admin = await NewTemplateService(verify).GetAllForAdminAsync(includeDeleted: true);
            admin.Should().ContainSingle();
            admin[0].DeletedAt.Should().NotBeNull();
            admin[0].UpdatedAt.Should().Be(admin[0].DeletedAt!.Value);
        }

        // Restore.
        await using (var ctx = _db.NewContext())
        {
            await NewTemplateService(ctx).RestoreAsync(templateId);
        }

        await using (var verify = _db.NewContext())
        {
            var rows = await NewTemplateService(verify).GetTemplatesAsync();
            rows.Should().ContainSingle();
            rows[0].DeletedAt.Should().BeNull();
            // UpdatedAt is bumped on restore too — without it, the audit log
            // can't order the restore vs. the original soft-delete.
            rows[0].UpdatedAt.Should().BeAfter(rows[0].CreatedAt);
        }
    }

    [Fact]
    public async Task Template_soft_delete_is_idempotent_when_called_twice()
    {
        int templateId;
        await using (var ctx = _db.NewContext())
        {
            ctx.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-idem"));
            await ctx.SaveChangesAsync();
            templateId = ctx.RuntimeTemplates.Single().Id;
        }

        await using (var ctx = _db.NewContext())
        {
            await NewTemplateService(ctx).SoftDeleteAsync(templateId);
        }

        DateTime firstDeletedAt;
        await using (var verify = _db.NewContext())
        {
            firstDeletedAt = (await verify.RuntimeTemplates
                .IgnoreQueryFilters()
                .Where(t => t.Id == templateId)
                .Select(t => t.DeletedAt)
                .SingleAsync())!.Value;
        }

        // Second call short-circuits — no UpdatedAt churn.
        await using (var ctx = _db.NewContext())
        {
            await NewTemplateService(ctx).SoftDeleteAsync(templateId);
        }

        await using (var verify = _db.NewContext())
        {
            var deletedAt = await verify.RuntimeTemplates
                .IgnoreQueryFilters()
                .Where(t => t.Id == templateId)
                .Select(t => t.DeletedAt)
                .SingleAsync();
            deletedAt.Should().Be(firstDeletedAt);
        }
    }

    [Fact]
    public async Task Module_soft_delete_round_trips_through_restore()
    {
        int moduleId;
        await using (var ctx = _db.NewContext())
        {
            ctx.Modules.Add(ModuleBuilder.Default("mod-soft-delete"));
            await ctx.SaveChangesAsync();
            moduleId = ctx.Modules.Single().Id;
        }

        await using (var ctx = _db.NewContext())
        {
            await NewModuleService(ctx).SoftDeleteAsync(moduleId);
        }

        await using (var verify = _db.NewContext())
        {
            // includeDeleted=false hides soft-deleted rows — same predicate the
            // end-user-facing TemplateService.GetModulesAsync read uses.
            (await NewModuleService(verify).GetAllForAdminAsync(includeDeleted: false))
                .Should().BeEmpty();
            var admin = await NewModuleService(verify).GetAllForAdminAsync(includeDeleted: true);
            admin.Should().ContainSingle();
            admin[0].DeletedAt.Should().NotBeNull();
            admin[0].UpdatedAt.Should().Be(admin[0].DeletedAt!.Value);
        }

        await using (var ctx = _db.NewContext())
        {
            await NewModuleService(ctx).RestoreAsync(moduleId);
        }

        await using (var verify = _db.NewContext())
        {
            var rows = await NewModuleService(verify).GetAllForAdminAsync(includeDeleted: false);
            rows.Should().ContainSingle();
            rows[0].DeletedAt.Should().BeNull();
            rows[0].UpdatedAt.Should().BeAfter(rows[0].CreatedAt);
        }
    }

    [Fact]
    public async Task Restore_on_a_live_row_is_a_no_op()
    {
        int templateId;
        await using (var ctx = _db.NewContext())
        {
            ctx.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-live"));
            await ctx.SaveChangesAsync();
            templateId = ctx.RuntimeTemplates.Single().Id;
        }

        DateTime originalUpdatedAt;
        await using (var verify = _db.NewContext())
        {
            originalUpdatedAt = await verify.RuntimeTemplates
                .Where(t => t.Id == templateId)
                .Select(t => t.UpdatedAt)
                .SingleAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            await NewTemplateService(ctx).RestoreAsync(templateId);
        }

        await using (var verify = _db.NewContext())
        {
            var updatedAt = await verify.RuntimeTemplates
                .Where(t => t.Id == templateId)
                .Select(t => t.UpdatedAt)
                .SingleAsync();
            updatedAt.Should().Be(originalUpdatedAt);
        }
    }

    private TemplateService NewTemplateService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<TemplateService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx), _db.NewQuotaGuard(ctx));

    private ModuleService NewModuleService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<ModuleService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx), _db.NewQuotaGuard(ctx));
}
