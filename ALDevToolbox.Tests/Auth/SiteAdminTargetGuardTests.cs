using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// A SiteAdmin's email, status and role can only be changed by another
/// SiteAdmin, and the last active SiteAdmin can't be disabled from the org
/// Users page. Runs in the fixture's ambient org, as the admin endpoints and
/// the Users page do in production (the request's own org).
/// </summary>
public sealed class SiteAdminTargetGuardTests : IDisposable
{
    private const int OrgId = TestDb.DefaultOrgId;

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Org_admin_cannot_disable_a_site_admin()
    {
        var (_, siteAdminId) = await SeedOrgAdminAndSiteAdminAsync();

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).DisableUserAsync(siteAdminId, OrgId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("UserId");
        (await ReadUserAsync(siteAdminId)).Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public async Task Org_admin_cannot_enable_a_disabled_site_admin()
    {
        var (_, siteAdminId) = await SeedOrgAdminAndSiteAdminAsync();
        await using (var seed = _db.NewContext())
        {
            var u = await seed.Users.SingleAsync(x => x.Id == siteAdminId);
            u.Status = UserStatus.Disabled;
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).EnableUserAsync(siteAdminId, OrgId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("UserId");
        (await ReadUserAsync(siteAdminId)).Status.Should().Be(UserStatus.Disabled);
    }

    [Fact]
    public async Task Org_admin_cannot_change_a_site_admins_role()
    {
        var (_, siteAdminId) = await SeedOrgAdminAndSiteAdminAsync();

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).ChangeRoleAsync(siteAdminId, UserRole.User, OrgId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("UserId");
        (await ReadUserAsync(siteAdminId)).Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task Org_admin_cannot_request_an_email_change_for_a_site_admin()
    {
        var (orgAdminId, siteAdminId) = await SeedOrgAdminAndSiteAdminAsync();

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).RequestEmailChangeAsync(siteAdminId, "elsewhere@example.com", OrgId, orgAdminId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("UserId");
        var user = await ReadUserAsync(siteAdminId);
        user.PendingEmail.Should().BeNull();
        await using var read = _db.NewContext();
        (await read.PasswordResetTokens.AnyAsync(t => t.UserId == siteAdminId)).Should().BeFalse();
    }

    [Fact]
    public async Task Bulk_disable_by_an_org_admin_skips_the_site_admin_and_disables_the_rest()
    {
        var (_, siteAdminId) = await SeedOrgAdminAndSiteAdminAsync();
        var memberId = await SeedUserAsync("member@example.com", UserRole.User, siteAdmin: false);

        BulkActionResult result;
        await using (var ctx = _db.NewContext())
        {
            result = await NewUserAdmin(ctx).BulkDisableUsersAsync([siteAdminId, memberId], OrgId);
        }

        result.SucceededIds.Should().Equal(memberId);
        result.Failures.Should().ContainSingle().Which.Id.Should().Be(siteAdminId);
        (await ReadUserAsync(siteAdminId)).Status.Should().Be(UserStatus.Active);
        (await ReadUserAsync(memberId)).Status.Should().Be(UserStatus.Disabled);
    }

    [Fact]
    public async Task Site_admin_can_change_another_site_admins_role_and_status()
    {
        var actorId = await SeedUserAsync("operator@example.com", UserRole.Admin, siteAdmin: true);
        var otherId = await SeedUserAsync("second-operator@example.com", UserRole.Admin, siteAdmin: true);
        ActAs(actorId, siteAdmin: true);

        await using (var ctx = _db.NewContext()) await NewUserAdmin(ctx).ChangeRoleAsync(otherId, UserRole.Editor, OrgId);
        await using (var ctx = _db.NewContext()) await NewUserAdmin(ctx).DisableUserAsync(otherId, OrgId);

        var other = await ReadUserAsync(otherId);
        other.Role.Should().Be(UserRole.Editor);
        other.Status.Should().Be(UserStatus.Disabled);
    }

    [Fact]
    public async Task Site_admin_can_request_an_email_change_for_another_site_admin()
    {
        var actorId = await SeedUserAsync("operator@example.com", UserRole.Admin, siteAdmin: true);
        var otherId = await SeedUserAsync("second-operator@example.com", UserRole.Admin, siteAdmin: true);
        ActAs(actorId, siteAdmin: true);

        await using (var ctx = _db.NewContext())
        {
            await NewUserAdmin(ctx).RequestEmailChangeAsync(otherId, "renamed@example.com", OrgId, actorId);
        }

        (await ReadUserAsync(otherId)).PendingEmail.Should().Be("renamed@example.com");
    }

    [Fact]
    public async Task Disable_refuses_the_last_active_site_admin()
    {
        // A second Admin keeps the org's last-admin guard out of the way, so
        // the refusal below can only come from the SiteAdmin one.
        await SeedUserAsync("org-admin@example.com", UserRole.Admin, siteAdmin: false);
        var soloId = await SeedUserAsync("operator@example.com", UserRole.Admin, siteAdmin: true);
        ActAs(soloId, siteAdmin: true);

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).DisableUserAsync(soloId, OrgId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("LastSiteAdmin");
        (await ReadUserAsync(soloId)).Status.Should().Be(UserStatus.Active);
    }

    private UserAdministrationService NewUserAdmin(Data.AppDbContext ctx) =>
        _db.NewUserAdministrationService(ctx, _clock);

    private void ActAs(int userId, bool siteAdmin)
    {
        _db.OrgContext.CurrentOrganizationId = OrgId;
        _db.OrgContext.CurrentUserId = userId;
        _db.OrgContext.IsSiteAdmin = siteAdmin;
    }

    /// <summary>
    /// The usual single-org setup: the bootstrap SiteAdmin and an ordinary org
    /// Admin side by side, with the org Admin acting.
    /// </summary>
    private async Task<(int OrgAdminId, int SiteAdminId)> SeedOrgAdminAndSiteAdminAsync()
    {
        var siteAdminId = await SeedUserAsync("operator@example.com", UserRole.Admin, siteAdmin: true);
        var orgAdminId = await SeedUserAsync("org-admin@example.com", UserRole.Admin, siteAdmin: false);
        ActAs(orgAdminId, siteAdmin: false);
        return (orgAdminId, siteAdminId);
    }

    private async Task<int> SeedUserAsync(string email, UserRole role, bool siteAdmin)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = OrgId,
            Email = email,
            DisplayName = email,
            PasswordHash = "placeholder",
            Role = role,
            IsSiteAdmin = siteAdmin,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private async Task<User> ReadUserAsync(int id)
    {
        await using var read = _db.NewContext();
        return await read.Users.AsNoTracking().SingleAsync(u => u.Id == id);
    }
}
