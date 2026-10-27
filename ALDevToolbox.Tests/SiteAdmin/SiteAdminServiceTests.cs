using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Account;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Behavioural tests for <see cref="SiteAdminService"/>: cross-organisation
/// search bypasses the per-org query filter, promote / demote toggles the
/// IsSiteAdmin flag (and refuses to demote the last SiteAdmin), and audit
/// search returns rows from multiple organisations in one call.
/// </summary>
public sealed class SiteAdminServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Search_returns_users_from_multiple_organisations_for_a_site_admin()
    {
        await SeedUsers();

        var ctx = _db.NewContext();
        var svc = new SiteAdminService(ctx, NewSiteAdminContext(), NullLogger<SiteAdminService>.Instance);
        // "example.com" matches every seeded email — both orgs' users appear.
        var rows = await svc.SearchUsersAsync("example.com");

        rows.Select(r => r.OrganizationId).Distinct().Should().HaveCountGreaterThanOrEqualTo(2,
            "the SiteAdmin search bypasses the per-org query filter");
        rows.Should().Contain(r => r.Email == "alice@example.com");
        rows.Should().Contain(r => r.Email == "bob@example.com");
    }

    [Fact]
    public async Task Get_memberships_finds_the_account_for_an_email_in_another_org()
    {
        // Emails are unique deployment-wide since #1207, so an address has at
        // most one membership; the lookup still has to cross organisations.
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(NewUser("member@example.com", TestDb.OtherOrgId, role: UserRole.Admin));
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var svc = new SiteAdminService(ctx, NewSiteAdminContext(), NullLogger<SiteAdminService>.Instance);
        var memberships = await svc.GetMembershipsAsync("member@example.com");
        memberships.Select(m => m.OrganizationId).Should().Equal(TestDb.OtherOrgId);
    }

    [Fact]
    public async Task Promote_sets_is_site_admin_true()
    {
        var userId = await SeedSingleUser();

        var ctx = _db.NewContext();
        var svc = new SiteAdminService(ctx, NewSiteAdminContext(), NullLogger<SiteAdminService>.Instance);
        await svc.PromoteAsync(userId);

        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
        user.IsSiteAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task Demote_refuses_to_remove_the_last_site_admin()
    {
        var userId = await SeedSingleUser(siteAdmin: true);

        var ctx = _db.NewContext();
        var svc = new SiteAdminService(ctx, NewSiteAdminContext(), NullLogger<SiteAdminService>.Instance);
        Func<Task> demote = () => svc.DemoteAsync(userId);
        var ex = await demote.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LastSiteAdmin");
    }

    [Fact]
    public async Task Demote_succeeds_when_another_site_admin_remains()
    {
        var firstId = await SeedSingleUser(siteAdmin: true);
        var secondId = await SeedSingleUser(email: "second@example.com", siteAdmin: true);

        var ctx = _db.NewContext();
        var svc = new SiteAdminService(ctx, NewSiteAdminContext(), NullLogger<SiteAdminService>.Instance);
        await svc.DemoteAsync(firstId);

        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == firstId)).IsSiteAdmin.Should().BeFalse();
        (await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == secondId)).IsSiteAdmin.Should().BeTrue();
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Disabled)]
    public async Task Demote_refuses_when_the_only_other_site_admin_is_not_active(UserStatus otherStatus)
    {
        // The previous count included non-disabled SiteAdmins only and used
        // `<= 1`, so an active subject + a disabled other looked like a
        // single-row count and threw correctly — but an active subject + a
        // pending other counted as two and allowed the demote, leaving zero
        // active SiteAdmins. The pending row is the regression; the disabled
        // one pins the rule from the other side.
        var firstId = await SeedSingleUser(siteAdmin: true);
        var secondId = await SeedSingleUser(email: "second@example.com", siteAdmin: true);
        await using (var ctx = _db.NewContext())
        {
            var second = await ctx.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == secondId);
            second.Status = otherStatus;
            await ctx.SaveChangesAsync();
        }

        var ctxAct = _db.NewContext();
        var svc = new SiteAdminService(ctxAct, NewSiteAdminContext(), NullLogger<SiteAdminService>.Instance);
        Func<Task> demote = () => svc.DemoteAsync(firstId);
        var ex = await demote.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LastSiteAdmin");
        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == firstId)).IsSiteAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task Two_site_admins_demoting_each_other_at_once_leave_one_standing()
    {
        // Count-then-save without a lock let both demotes count the other and
        // both go ahead, leaving nobody able to reach /site-admin (#1337). A
        // race is not certain to interleave on any one try, so run several.
        for (var round = 1; round <= 8; round++)
        {
            var (first, second) = await SeedSiteAdminPairAsync(round);

            var outcomes = await Task.WhenAll(
                AttemptAsync(ctx => NewSiteAdminService(ctx).DemoteAsync(first)),
                AttemptAsync(ctx => NewSiteAdminService(ctx).DemoteAsync(second)));

            outcomes.Count(ok => ok).Should().Be(1, $"exactly one of the two may go ahead (round {round})");
            (await ActiveSiteAdminCountAsync()).Should().Be(1, $"the deployment keeps a SiteAdmin (round {round})");
        }
    }

    [Fact]
    public async Task A_demote_racing_a_disable_leaves_one_site_admin_standing()
    {
        // The org Admin page's disable locks only the organisation row, which a
        // demote never takes, so the two paths share the SiteAdmin guard's own
        // lock (#1337).
        for (var round = 1; round <= 8; round++)
        {
            var (first, second) = await SeedSiteAdminPairAsync(round);
            var demoteFirst = round % 2 == 0;

            var outcomes = await Task.WhenAll(
                AttemptAsync(ctx => NewSiteAdminService(ctx).DemoteAsync(demoteFirst ? first : second)),
                AttemptAsync(ctx => NewUserAdministrationService(ctx)
                    .DisableUserAsync(demoteFirst ? second : first, TestDb.DefaultOrgId)));

            outcomes.Count(ok => ok).Should().Be(1, $"exactly one of the two may go ahead (round {round})");
            (await ActiveSiteAdminCountAsync()).Should().Be(1, $"the deployment keeps a SiteAdmin (round {round})");
        }
    }

    /// <summary>
    /// Clears every SiteAdmin flag left by earlier rounds and seeds two fresh
    /// active SiteAdmins in the default organisation.
    /// </summary>
    private async Task<(int First, int Second)> SeedSiteAdminPairAsync(int round)
    {
        await using (var ctx = _db.NewContext())
        {
            await ctx.Users.IgnoreQueryFilters()
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsSiteAdmin, false));
        }
        var first = await SeedSingleUser(email: $"race-{round}-a@example.com", siteAdmin: true);
        var second = await SeedSingleUser(email: $"race-{round}-b@example.com", siteAdmin: true);
        return (first, second);
    }

    private async Task<bool> AttemptAsync(Func<ALDevToolbox.Data.AppDbContext, Task> op)
    {
        // Audited, as in production: the audit row's foreign key to the
        // organisation takes a lock of its own, and an unaudited context once
        // hid a deadlock between the org guard and the SiteAdmin guard (#1337).
        await using var ctx = _db.NewContextWithAudit(NewAuditInterceptor());
        try { await op(ctx); return true; }
        catch (PlanValidationException ex) when (ex.Errors.ContainsKey("LastSiteAdmin")) { return false; }
    }

    private static ALDevToolbox.Data.AuditInterceptor NewAuditInterceptor()
    {
        var http = new Microsoft.AspNetCore.Http.HttpContextAccessor
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "root"),
                    new System.Security.Claims.Claim(HttpOrganizationContext.OrganizationIdClaim,
                        TestDb.DefaultOrgId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ], "test")),
            },
        };
        return new ALDevToolbox.Data.AuditInterceptor(http);
    }

    private async Task<int> ActiveSiteAdminCountAsync()
    {
        await using var read = _db.NewContext();
        return await read.Users.IgnoreQueryFilters()
            .CountAsync(u => u.IsSiteAdmin && u.Status == UserStatus.Active);
    }

    private static SiteAdminService NewSiteAdminService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NewSiteAdminContext(), NullLogger<SiteAdminService>.Instance);

    private UserAdministrationService NewUserAdministrationService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, TimeProvider.System, _db.OpenIddictTokens, NewSiteAdminContext());

    [Fact]
    public async Task Promote_without_site_admin_context_throws()
    {
        var userId = await SeedSingleUser();
        var ctx = _db.NewContext();
        var svc = new SiteAdminService(ctx, _db.OrgContext, NullLogger<SiteAdminService>.Instance);
        Func<Task> act = () => svc.PromoteAsync(userId);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Audit_search_spans_organisations()
    {
        // The audit interceptor stamps OrganizationId from the entity itself
        // (RuntimeTemplate.OrganizationId). Adding rows in two different orgs
        // produces audit rows in both — and SearchAuditAsync should return
        // both because it bypasses the per-org query filter.
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            ctx.RuntimeTemplates.Add(Builders.TemplateBuilder.Default("rt-default", organizationId: TestDb.DefaultOrgId));
            await ctx.SaveChangesAsync();
        }
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            ctx.RuntimeTemplates.Add(Builders.TemplateBuilder.Default("rt-other", organizationId: TestDb.OtherOrgId));
            await ctx.SaveChangesAsync();
        }
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;

        var ctxSearch = _db.NewContext();
        var svc = new SiteAdminService(ctxSearch, NewSiteAdminContext(), NullLogger<SiteAdminService>.Instance);
        var result = await svc.SearchAuditAsync(
            entityType: AuditEntityType.RuntimeTemplate,
            organizationId: null,
            actorContains: null,
            fromUtc: null,
            toUtc: null);

        result.Rows.Select(r => r.OrganizationId).Where(o => o is not null).Distinct()
            .Should().Contain(new int?[] { TestDb.DefaultOrgId, TestDb.OtherOrgId });
    }

    [Fact]
    public async Task Audit_search_paginates_and_reports_total()
    {
        // Seed five audit rows with increasing timestamps so ordering is
        // deterministic (newest first).
        await using (var seed = _db.NewContext())
        {
            for (var i = 0; i < 5; i++)
            {
                seed.AuditLog.Add(new AuditLogEntry
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    EntityType = AuditEntityType.RuntimeTemplate,
                    EntityId = i,
                    Action = AuditAction.Created,
                    ChangedBy = "alice@example.com",
                    Timestamp = new DateTime(2026, 5, 14, 10, i, 0, DateTimeKind.Utc),
                    SnapshotJson = "{}",
                });
            }
            await seed.SaveChangesAsync();
        }

        var svc = new SiteAdminService(_db.NewContext(), NewSiteAdminContext(), NullLogger<SiteAdminService>.Instance);

        var firstPage = await svc.SearchAuditAsync(
            entityType: AuditEntityType.RuntimeTemplate,
            organizationId: null, actorContains: null, fromUtc: null, toUtc: null,
            skip: 0, take: 2);
        firstPage.Total.Should().Be(5, "Total counts every match regardless of the page size");
        firstPage.Rows.Should().HaveCount(2);
        // Newest first: EntityId 4 then 3.
        firstPage.Rows.Select(r => r.EntityId).Should().ContainInOrder(4, 3);

        var thirdPage = await svc.SearchAuditAsync(
            entityType: AuditEntityType.RuntimeTemplate,
            organizationId: null, actorContains: null, fromUtc: null, toUtc: null,
            skip: 4, take: 2);
        thirdPage.Total.Should().Be(5);
        thirdPage.Rows.Should().HaveCount(1, "the last page holds the remainder");
        thirdPage.Rows.Single().EntityId.Should().Be(0);
    }

    private async Task SeedUsers()
    {
        await using var ctx = _db.NewContext();
        ctx.Users.AddRange(
            NewUser("alice@example.com", TestDb.DefaultOrgId),
            NewUser("bob@example.com", TestDb.OtherOrgId));
        await ctx.SaveChangesAsync();
    }

    private async Task<int> SeedSingleUser(string email = "promote@example.com", bool siteAdmin = false)
    {
        await using var ctx = _db.NewContext();
        var user = NewUser(email, TestDb.DefaultOrgId);
        user.IsSiteAdmin = siteAdmin;
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private static User NewUser(string email, int orgId, UserRole role = UserRole.User) => new()
    {
        OrganizationId = orgId,
        Email = email,
        DisplayName = email,
        PasswordHash = "x",
        Role = role,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };

    private static AmbientOrganizationContext NewSiteAdminContext() => new()
    {
        CurrentOrganizationId = TestDb.DefaultOrgId,
        CurrentUserId = 1,
        IsSiteAdmin = true,
    };
}
