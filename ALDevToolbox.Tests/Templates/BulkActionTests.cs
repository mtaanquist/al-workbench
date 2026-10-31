using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Templates;

/// <summary>
/// Exercises the bulk admin actions added in Milestone P4.20: per-entity
/// transactions so the audit interceptor writes one row per template / module,
/// per-row failure surfaces so partial successes don't roll back, and
/// cross-org URL tampering bounces off the EF query filter so an org admin
/// can't operate on another org's rows.
/// </summary>
public sealed class BulkActionTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Bulk_soft_delete_on_three_templates_marks_them_deleted_and_writes_three_audit_rows()
    {
        var ids = await SeedTemplatesAsync("alpha", "beta", "gamma");

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin@example.com")))
        {
            var svc = NewTemplateService(ctx);
            var result = await svc.BulkSoftDeleteAsync(ids);
            result.AllSucceeded.Should().BeTrue();
            result.SucceededCount.Should().Be(3);
        }

        await using (var verify = _db.NewContext())
        {
            var rows = await verify.RuntimeTemplates.IgnoreQueryFilters()
                .Where(t => ids.Contains(t.Id))
                .ToListAsync();
            rows.Should().OnlyContain(t => t.DeletedAt != null);

            var auditRows = await verify.AuditLog
                .Where(a => a.EntityType == AuditEntityType.RuntimeTemplate && ids.Contains(a.EntityId))
                .OrderBy(a => a.Id)
                .ToListAsync();
            auditRows.Should().HaveCount(3);
            auditRows.Should().OnlyContain(a => a.Action == AuditAction.Updated && a.ChangedBy == "admin@example.com");
            // Audit timestamps should reflect the per-entity write order.
            auditRows.Select(a => a.Timestamp).Should().BeInAscendingOrder();
        }
    }

    [Fact]
    public async Task Bulk_un_deprecate_clears_the_flag_on_selected_rows()
    {
        var ids = await SeedTemplatesAsync("a", "b");
        await using (var seed = _db.NewContext())
        {
            foreach (var t in seed.RuntimeTemplates.IgnoreQueryFilters().Where(t => ids.Contains(t.Id)))
            {
                t.Deprecated = true;
            }
            await seed.SaveChangesAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            var result = await NewTemplateService(ctx).BulkUnDeprecateAsync(ids);
            result.AllSucceeded.Should().BeTrue();
        }

        await using (var verify = _db.NewContext())
        {
            (await verify.RuntimeTemplates.Where(t => ids.Contains(t.Id)).ToListAsync())
                .Should().OnlyContain(t => !t.Deprecated);
        }
    }

    [Fact]
    public async Task Bulk_soft_delete_on_other_orgs_template_fails_per_row_and_does_not_mutate_it()
    {
        int defaultId;
        int otherId;
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("mine", organizationId: TestDb.DefaultOrgId));
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("not-mine", organizationId: TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
            defaultId = (await seed.RuntimeTemplates.IgnoreQueryFilters().FirstAsync(t => t.Key == "mine")).Id;
            otherId = (await seed.RuntimeTemplates.IgnoreQueryFilters().FirstAsync(t => t.Key == "not-mine")).Id;
        }

        // Acting as DefaultOrg admin, attempt to bulk-act on the other org's id.
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using (var ctx = _db.NewContext())
        {
            var result = await NewTemplateService(ctx).BulkSoftDeleteAsync(new[] { defaultId, otherId });
            result.SucceededIds.Should().BeEquivalentTo(new[] { defaultId });
            result.Failures.Should().ContainSingle(f => f.Id == otherId);
            result.Failures[0].Reason.Should().Contain("Not found");
        }

        // Confirm the other org's row is untouched.
        await using (var verify = _db.NewContext())
        {
            var other = await verify.RuntimeTemplates.IgnoreQueryFilters()
                .FirstAsync(t => t.Id == otherId);
            other.DeletedAt.Should().BeNull();
        }
    }

    [Fact]
    public async Task Bulk_restore_on_a_live_row_counts_as_success_without_churning_the_audit_log()
    {
        var ids = await SeedTemplatesAsync("a", "b");

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin")))
        {
            var result = await NewTemplateService(ctx).BulkRestoreAsync(ids);
            // Both rows are already "restored" — short-circuit treats them as
            // success but doesn't write an audit row for a no-op.
            result.AllSucceeded.Should().BeTrue();
        }

        await using (var verify = _db.NewContext())
        {
            // The seed path didn't go through the audit interceptor, but the
            // no-op bulk restore mustn't have added any rows either. Net audit
            // rows for these template ids should be zero.
            var auditRows = await verify.AuditLog
                .Where(a => a.EntityType == AuditEntityType.RuntimeTemplate && ids.Contains(a.EntityId))
                .ToListAsync();
            auditRows.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Bulk_soft_delete_on_modules_marks_them_deleted()
    {
        int idA, idB;
        await using (var seed = _db.NewContext())
        {
            seed.Modules.Add(ModuleBuilder.Default("mod-a"));
            seed.Modules.Add(ModuleBuilder.Default("mod-b"));
            await seed.SaveChangesAsync();
            idA = (await seed.Modules.FirstAsync(m => m.Key == "mod-a")).Id;
            idB = (await seed.Modules.FirstAsync(m => m.Key == "mod-b")).Id;
        }

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin")))
        {
            var result = await NewModuleService(ctx).BulkSoftDeleteAsync(new[] { idA, idB });
            result.AllSucceeded.Should().BeTrue();
        }

        await using (var verify = _db.NewContext())
        {
            var rows = await verify.Modules.IgnoreQueryFilters()
                .Where(m => m.Id == idA || m.Id == idB)
                .ToListAsync();
            rows.Should().OnlyContain(m => m.DeletedAt != null);

            var auditRows = await verify.AuditLog
                .Where(a => a.EntityType == AuditEntityType.Module)
                .ToListAsync();
            auditRows.Where(a => a.Action == AuditAction.Updated).Should().HaveCount(2);
        }
    }

    private async Task<List<int>> SeedTemplatesAsync(params string[] keys)
    {
        var ids = new List<int>();
        await using var ctx = _db.NewContext();
        foreach (var k in keys)
        {
            ctx.RuntimeTemplates.Add(TemplateBuilder.Default(k));
        }
        await ctx.SaveChangesAsync();
        foreach (var k in keys)
        {
            ids.Add((await ctx.RuntimeTemplates.FirstAsync(t => t.Key == k)).Id);
        }
        return ids;
    }

    private TemplateService NewTemplateService(AppDbContext ctx) =>
        new(ctx, NullLogger<TemplateService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx), _db.NewQuotaGuard(ctx));

    private ModuleService NewModuleService(AppDbContext ctx) =>
        new(ctx, NullLogger<ModuleService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx), _db.NewQuotaGuard(ctx));

    private static AuditInterceptor NewInterceptor(string? name)
    {
        var http = new Microsoft.AspNetCore.Http.HttpContextAccessor();
        if (name is not null)
        {
            var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            ctx.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, name) }, "test"));
            http.HttpContext = ctx;
        }
        return new AuditInterceptor(http);
    }
}
