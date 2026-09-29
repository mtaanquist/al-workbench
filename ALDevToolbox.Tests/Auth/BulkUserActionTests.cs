using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Covers the bulk user actions added in Milestone P4.20: per-row last-admin
/// protection on role-change, cross-org URL tampering rejected as a per-row
/// failure, and the bulk operation continues past a failing row instead of
/// rolling everything back.
/// </summary>
public sealed class BulkUserActionTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 11, 0, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Bulk_disable_two_users_marks_them_disabled()
    {
        var orgId = TestDb.DefaultOrgId;
        var (adminId, aliceId, bobId) = await SeedTwoUsersWithAdminAsync(orgId);

        await using var ctx = _db.NewContext();
        var svc = _db.NewUserAdministrationService(ctx, _clock);
        var result = await svc.BulkDisableUsersAsync(new[] { aliceId, bobId }, orgId);

        result.AllSucceeded.Should().BeTrue();
        result.SucceededIds.Should().BeEquivalentTo(new[] { aliceId, bobId });

        await using var read = _db.NewContext();
        var statuses = await read.Users.IgnoreQueryFilters()
            .Where(u => u.Id == aliceId || u.Id == bobId)
            .Select(u => u.Status)
            .ToListAsync();
        statuses.Should().OnlyContain(s => s == UserStatus.Disabled);
        // The admin row stays untouched.
        (await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == adminId)).Status
            .Should().Be(UserStatus.Active);
    }

    [Fact]
    public async Task Bulk_role_change_respects_last_admin_guard_per_row()
    {
        var orgId = TestDb.DefaultOrgId;
        // Two admins; demoting one is fine, demoting both leaves zero. The
        // bulk call should succeed on the first and surface the last-admin
        // failure for the second.
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(NewUser(orgId, id: 100, "admin-a@example.com", "Admin A", UserRole.Admin));
            seed.Users.Add(NewUser(orgId, id: 101, "admin-b@example.com", "Admin B", UserRole.Admin));
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var svc = _db.NewUserAdministrationService(ctx, _clock);
        var result = await svc.BulkChangeRoleAsync(new[] { 100, 101 }, UserRole.User, orgId);

        result.SucceededCount.Should().Be(1);
        result.Failures.Should().ContainSingle();
        result.Failures[0].Reason.Should().Contain("last active admin");

        await using var read = _db.NewContext();
        var roles = await read.Users.IgnoreQueryFilters()
            .Where(u => u.Id == 100 || u.Id == 101)
            .OrderBy(u => u.Id)
            .Select(u => u.Role)
            .ToListAsync();
        // One demoted, one stays Admin.
        roles.Should().Contain(UserRole.Admin);
        roles.Should().Contain(UserRole.User);
    }

    [Fact]
    public async Task Bulk_disable_on_another_orgs_user_fails_per_row_without_mutating_them()
    {
        var orgA = TestDb.DefaultOrgId;
        var orgB = TestDb.OtherOrgId;
        int idA, idB;
        await using (var seed = _db.NewContext())
        {
            // Both orgs need at least one admin so the disable on the
            // target user isn't blocked by the last-admin guard.
            seed.Users.Add(NewUser(orgA, id: 200, "admin-a@example.com", "Admin A", UserRole.Admin));
            seed.Users.Add(NewUser(orgA, id: 201, "alice@example.com", "Alice", UserRole.User));
            seed.Users.Add(NewUser(orgB, id: 202, "admin-b@example.com", "Admin B", UserRole.Admin));
            seed.Users.Add(NewUser(orgB, id: 203, "bob@example.com", "Bob", UserRole.User));
            await seed.SaveChangesAsync();
            idA = 201;
            idB = 203;
        }

        _db.OrgContext.CurrentOrganizationId = orgA;
        await using var ctx = _db.NewContext();
        var svc = _db.NewUserAdministrationService(ctx, _clock);
        var result = await svc.BulkDisableUsersAsync(new[] { idA, idB }, orgA);

        result.SucceededIds.Should().BeEquivalentTo(new[] { idA });
        result.Failures.Should().ContainSingle(f => f.Id == idB);

        await using var read = _db.NewContext();
        // OrgB's user must remain Active — tampering with the id list must not
        // reach across organisations.
        var orgBUser = await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == idB);
        orgBUser.Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public async Task Bulk_failure_for_cross_org_id_does_not_leak_the_other_orgs_display_name()
    {
        var orgA = TestDb.DefaultOrgId;
        var orgB = TestDb.OtherOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(NewUser(orgA, id: 300, "admin-a@example.com", "Admin A", UserRole.Admin));
            seed.Users.Add(NewUser(orgB, id: 301, "secret@example.com", "OrgB Secret Name", UserRole.User));
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var svc = _db.NewUserAdministrationService(ctx, _clock);
        var result = await svc.BulkDisableUsersAsync(new[] { 301 }, orgA);

        // The row fails (cross-org id not found in orgA) and the failure's display
        // name is the placeholder, never orgB's real name. See #489.
        result.Failures.Should().ContainSingle(f => f.Id == 301);
        result.Failures[0].DisplayName.Should().Be("#301");
        result.Failures[0].DisplayName.Should().NotContain("OrgB Secret Name");
    }

    private async Task<(int admin, int alice, int bob)> SeedTwoUsersWithAdminAsync(int orgId)
    {
        await using var seed = _db.NewContext();
        seed.Users.Add(NewUser(orgId, id: 50, "admin@example.com", "Admin", UserRole.Admin));
        seed.Users.Add(NewUser(orgId, id: 51, "alice@example.com", "Alice", UserRole.User));
        seed.Users.Add(NewUser(orgId, id: 52, "bob@example.com", "Bob", UserRole.User));
        await seed.SaveChangesAsync();
        return (50, 51, 52);
    }

    private User NewUser(int orgId, int id, string email, string display, UserRole role) => new()
    {
        Id = id,
        OrganizationId = orgId,
        Email = email,
        PasswordHash = "x",
        DisplayName = display,
        Role = role,
        Status = UserStatus.Active,
        CreatedAt = _clock.GetUtcNow().UtcDateTime,
    };
}
