using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Verifies the EF query filter scopes reads to the acting org. Cross-org
/// content from another tenant is invisible until tests explicitly switch
/// the ambient context — which is exactly the boundary the milestone calls
/// for.
/// </summary>
public sealed class CrossOrgIsolationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Templates_in_other_org_are_invisible()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-default", organizationId: TestDb.DefaultOrgId));
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-other", organizationId: TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
        }

        // Default org sees only its own template.
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using (var ctx = _db.NewContext())
        {
            (await ctx.RuntimeTemplates.Select(t => t.Key).ToListAsync())
                .Should().BeEquivalentTo(new[] { "runtime-default" });
        }

        // Other org sees only its own template.
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        await using (var ctx = _db.NewContext())
        {
            (await ctx.RuntimeTemplates.Select(t => t.Key).ToListAsync())
                .Should().BeEquivalentTo(new[] { "runtime-other" });
        }

        // No org context: no rows visible (filter sentinel is 0).
        _db.OrgContext.CurrentOrganizationId = null;
        await using (var ctx = _db.NewContext())
        {
            (await ctx.RuntimeTemplates.AnyAsync()).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Modules_and_catalogue_scope_per_org()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Modules.Add(ModuleBuilder.Default("mod-default", organizationId: TestDb.DefaultOrgId));
            seed.Modules.Add(ModuleBuilder.Default("mod-other", organizationId: TestDb.OtherOrgId));
            seed.WellKnownDependencies.Add(WellKnownDependencyBuilder.ForNav(
                "00000000-0000-0000-0000-000000000001", "Default Dep", organizationId: TestDb.DefaultOrgId));
            seed.WellKnownDependencies.Add(WellKnownDependencyBuilder.ForNav(
                "00000000-0000-0000-0000-000000000002", "Other Dep", organizationId: TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using (var ctx = _db.NewContext())
        {
            (await ctx.Modules.Select(m => m.Key).ToListAsync()).Should().Equal("mod-default");
            (await ctx.WellKnownDependencies.Select(w => w.DepName).ToListAsync()).Should().Equal("Default Dep");
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        await using (var ctx = _db.NewContext())
        {
            (await ctx.Modules.Select(m => m.Key).ToListAsync()).Should().Equal("mod-other");
            (await ctx.WellKnownDependencies.Select(w => w.DepName).ToListAsync()).Should().Equal("Other Dep");
        }
    }

    /// <summary>
    /// #678: audit_log used to opt out of the tenant query filter, leaving
    /// isolation to every read remembering its own organization_id predicate.
    /// These assertions deliberately use raw <c>_db.AuditLog</c> queries with
    /// no predicate, so they fail if the filter is ever removed again — the
    /// explicit predicates in <c>AuditService</c> would otherwise mask it.
    /// </summary>
    [Fact]
    public async Task Audit_rows_from_another_org_are_invisible_without_an_explicit_predicate()
    {
        int defaultId, otherId, seedId;
        AuditLogEntry theirsEarlier;
        await using (var seed = _db.NewContext())
        {
            var mine = NewAuditEntry(TestDb.DefaultOrgId, "mine");
            var theirs = NewAuditEntry(TestDb.OtherOrgId, "theirs");
            theirsEarlier = NewAuditEntry(TestDb.OtherOrgId, "theirs-earlier");
            theirsEarlier.Timestamp = theirs.Timestamp.AddMinutes(-5);
            seed.AuditLog.Add(theirsEarlier);
            // Startup seed and bootstrap inserts carry no organisation.
            var systemRow = NewAuditEntry(null, "startup-seed");
            seed.AuditLog.AddRange(mine, theirs, systemRow);
            await seed.SaveChangesAsync();
            defaultId = mine.Id;
            otherId = theirs.Id;
            seedId = systemRow.Id;
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using (var ctx = _db.NewContext())
        {
            (await ctx.AuditLog.Where(e => e.Id == otherId).AnyAsync())
                .Should().BeFalse("the query filter alone must hide another org's audit row");
            (await ctx.AuditLog.Where(e => e.Id == defaultId).AnyAsync()).Should().BeTrue();
            (await ctx.AuditLog.Where(e => e.Id == seedId).AnyAsync())
                .Should().BeTrue("null-org seed rows stay visible by design");
        }

        // Every public AuditService read is empty for the other org's row.
        var svc = new AuditService(_db.NewContextFactory(), _db.OrgContext);
        (await svc.GetRecentAsync()).Should().NotContain(e => e.Id == otherId);
        var (paged, _) = await svc.GetPagedAsync(new AuditFilter(null, null, null, null, null, null), 0, 100);
        paged.Should().NotContain(e => e.Id == otherId);
        (await svc.GetForEntityAsync(AuditEntityType.RuntimeTemplate, 99))
            .Should().NotContain(e => e.Id == otherId);
        (await svc.GetByIdAsync(otherId)).Should().BeNull();
        // The other org's newer row must not surface, even though it is the
        // true "next" entry for that entity in that tenant.
        (await svc.GetNextForEntityAsync(theirsEarlier))?.OrganizationId
            .Should().NotBe(TestDb.OtherOrgId);
    }

    /// <summary>
    /// Since #1260, <c>SystemSettings</c> and <c>Backup</c> audit rows carry no
    /// organisation (they belong to the whole deployment, and their snapshots
    /// hold the SMTP host and the off-site bucket). The query filter admits
    /// null-org rows to every tenant, so <c>AuditService</c>'s own
    /// <c>organization_id</c> predicates are the only thing keeping them off an
    /// ordinary organisation's audit page. Pin every read (#1346).
    /// </summary>
    [Fact]
    public async Task Deployment_wide_audit_rows_never_reach_an_organisations_audit_reads()
    {
        int deploymentRowId;
        AuditLogEntry mineEarlier;
        await using (var seed = _db.NewContext())
        {
            // An org-owned row about the same subject, earlier, so "what came
            // next" for it would be the deployment-wide row if the predicate went.
            mineEarlier = NewAuditEntry(TestDb.DefaultOrgId, "mine-earlier");
            mineEarlier.EntityType = AuditEntityType.SystemSettings;
            mineEarlier.EntityId = 1;
            mineEarlier.Timestamp = DateTime.UtcNow.AddMinutes(-5);
            var deploymentRow = NewAuditEntry(null, "root");
            deploymentRow.EntityType = AuditEntityType.SystemSettings;
            deploymentRow.EntityId = 1;
            deploymentRow.SnapshotJson = """{"SmtpHost":"smtp.cronus.example"}""";
            seed.AuditLog.AddRange(mineEarlier, deploymentRow);
            await seed.SaveChangesAsync();
            deploymentRowId = deploymentRow.Id;
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using (var ctx = _db.NewContext())
        {
            (await ctx.AuditLog.AnyAsync(e => e.Id == deploymentRowId))
                .Should().BeTrue("the filter admits null-org rows, which is why the service predicates matter");
        }

        var svc = new AuditService(_db.NewContextFactory(), _db.OrgContext);
        (await svc.GetRecentAsync()).Should().NotContain(e => e.Id == deploymentRowId);
        var (paged, total) = await svc.GetPagedAsync(
            new AuditFilter(AuditEntityType.SystemSettings, null, null, null, null, null), 0, 100);
        paged.Should().NotContain(e => e.Id == deploymentRowId);
        total.Should().Be(1, "only the organisation's own row counts");
        (await svc.GetForEntityAsync(AuditEntityType.SystemSettings, 1))
            .Should().NotContain(e => e.Id == deploymentRowId);
        (await svc.GetByIdAsync(deploymentRowId)).Should().BeNull();
        (await svc.GetNextForEntityAsync(mineEarlier)).Should().BeNull();
    }

    [Fact]
    public async Task IgnoreQueryFilters_lets_pre_login_paths_read_across_orgs()
    {
        // The /login path needs to look up users without knowing which org
        // they're in yet. IgnoreQueryFilters() is the contract for that.
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.OtherOrgId,
                Email = "alice@example.com",
                PasswordHash = "x",
                DisplayName = "Alice",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using (var ctx = _db.NewContext())
        {
            (await ctx.Users.AnyAsync(u => u.Email == "alice@example.com"))
                .Should().BeFalse("query filter scopes to the Default org");
            (await ctx.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == "alice@example.com"))
                .Should().BeTrue();
        }
    }

    private static AuditLogEntry NewAuditEntry(int? organizationId, string changedBy) => new()
    {
        OrganizationId = organizationId,
        EntityType = AuditEntityType.RuntimeTemplate,
        EntityId = 99,
        Action = AuditAction.Updated,
        Timestamp = DateTime.UtcNow,
        ChangedBy = changedBy,
    };
}
