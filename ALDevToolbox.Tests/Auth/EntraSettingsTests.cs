using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Tests for the Entra ID settings surface (issue #552, slice 1): per-org
/// tenant allow-list validation, the enable guards, client-secret encryption,
/// the deployment-wide app registration on system_settings, and the
/// user_external_logins uniqueness + tenant-isolation contract.
/// </summary>
public sealed class EntraSettingsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SystemSettingsService NewSystemSettings(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);

    private static readonly string TenantA = "11111111-2222-3333-4444-555555555555";
    private static readonly string ClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    // --- Org-level settings -------------------------------------------------

    [Fact]
    public async Task Identity_view_reports_the_org_name_its_toggles_and_the_sign_in_blast_radius()
    {
        // Two active members, one of whom signs in with Microsoft.
        await using (var seed = _db.NewContext())
        {
            var linked = NewActiveUser("linked@cronus.test");
            var passwordOnly = NewActiveUser("password-only@cronus.test");
            seed.Users.AddRange(linked, passwordOnly);
            await seed.SaveChangesAsync();
            seed.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = linked.Id,
                Provider = "entra",
                Subject = Guid.NewGuid().ToString(),
                Issuer = TenantA,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var view = await _db.NewOrganizationAdminService(_db.NewContext()).GetIdentityViewAsync();

        view.Name.Should().Be("Tenant");
        view.RequireStrongAuth.Should().BeFalse();
        view.AutoJoinVerifiedDomainUsers.Should().BeFalse();
        view.MembersTotal.Should().Be(2);
        view.MembersWithoutEntraLink.Should().Be(1);
        view.AdminHasEntraLink.Should().BeFalse("neither member is an admin");
    }

    [Fact]
    public async Task Tools_view_returns_the_orgs_mcp_switch_and_stored_disabled_set()
    {
        await _db.NewOrganizationAdminService(_db.NewContext())
            .SetDisabledToolsAsync(new[] { ALDevToolbox.Domain.Tools.ToolKey.Projects });

        var view = await _db.NewOrganizationAdminService(_db.NewContext()).GetToolsViewAsync();

        view.DisabledTools.Should().Contain(ALDevToolbox.Domain.Tools.ToolKey.Projects);
        view.McpEnabled.Should().BeTrue();
    }

    private static User NewActiveUser(string email) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        Email = email,
        DisplayName = email,
        PasswordHash = "placeholder",
        Role = UserRole.User,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task SaveEntra_rejects_a_tenant_id_that_is_not_a_guid()
    {
        var svc = _db.NewOrganizationAdminService(_db.NewContext());

        Func<Task> act = () => svc.SaveEntraAsync(new OrgEntraInput(
            Enabled: false, AllowedTenantIds: new[] { "contoso.com" },
            ClientId: null, ClientSecret: null, ClearClientSecret: false));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("EntraAllowedTenantIds");
    }

    [Fact]
    public async Task SaveEntra_refuses_enabling_without_a_tenant_id()
    {
        // Give the deployment a registration so only the tenant rule can fail.
        await NewSystemSettings(_db.NewContext()).SaveEntraAppAsync(
            new EntraAppInput(ClientId, "secret", ClearClientSecret: false));
        var svc = _db.NewOrganizationAdminService(_db.NewContext());

        Func<Task> act = () => svc.SaveEntraAsync(new OrgEntraInput(
            Enabled: true, AllowedTenantIds: Array.Empty<string>(),
            ClientId: null, ClientSecret: null, ClearClientSecret: false));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("EntraAllowedTenantIds");
    }

    [Fact]
    public async Task SaveEntra_refuses_enabling_when_no_registration_exists_anywhere()
    {
        var svc = _db.NewOrganizationAdminService(_db.NewContext());

        Func<Task> act = () => svc.SaveEntraAsync(new OrgEntraInput(
            Enabled: true, AllowedTenantIds: new[] { TenantA },
            ClientId: null, ClientSecret: null, ClearClientSecret: false));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("EntraEnabled");
    }

    [Fact]
    public async Task SaveEntra_enables_via_the_deployment_wide_registration()
    {
        await NewSystemSettings(_db.NewContext()).SaveEntraAppAsync(
            new EntraAppInput(ClientId, "secret", ClearClientSecret: false));
        var svc = _db.NewOrganizationAdminService(_db.NewContext());

        await svc.SaveEntraAsync(new OrgEntraInput(
            Enabled: true, AllowedTenantIds: new[] { TenantA.ToUpperInvariant(), TenantA },
            ClientId: null, ClientSecret: null, ClearClientSecret: false));

        var view = await _db.NewOrganizationAdminService(_db.NewContext()).GetEntraViewAsync();
        view.Enabled.Should().BeTrue();
        view.AllowedTenantIds.Should().ContainSingle("tenant ids are lowercased and de-duplicated")
            .Which.Should().Be(TenantA);
        view.ClientId.Should().BeNull();
        view.HasClientSecret.Should().BeFalse();
    }

    [Fact]
    public async Task SaveEntra_rejects_a_secret_without_a_client_id()
    {
        var svc = _db.NewOrganizationAdminService(_db.NewContext());

        Func<Task> act = () => svc.SaveEntraAsync(new OrgEntraInput(
            Enabled: false, AllowedTenantIds: Array.Empty<string>(),
            ClientId: null, ClientSecret: "orphan-secret", ClearClientSecret: false));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("EntraClientSecret");
    }

    [Fact]
    public async Task SaveEntra_stores_the_org_secret_encrypted_and_round_trips()
    {
        var svc = _db.NewOrganizationAdminService(_db.NewContext());

        await svc.SaveEntraAsync(new OrgEntraInput(
            Enabled: true, AllowedTenantIds: new[] { TenantA },
            ClientId: ClientId, ClientSecret: "s3cret-value", ClearClientSecret: false));

        await using var read = _db.NewContext();
        var row = await read.OrganizationSettings.AsNoTracking()
            .FirstAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
        row.EntraClientSecretEncrypted.Should().NotBeNullOrEmpty()
            .And.NotBe("s3cret-value", "the column holds Data-Protection ciphertext, never plaintext");
        var protector = _db.DataProtectionProvider.CreateProtector(
            OrganizationAdminService.EntraClientSecretProtectionPurpose);
        protector.Unprotect(row.EntraClientSecretEncrypted!).Should().Be("s3cret-value");
    }

    [Fact]
    public async Task SaveEntra_clearing_the_client_id_clears_the_stored_secret()
    {
        var svc = _db.NewOrganizationAdminService(_db.NewContext());
        await svc.SaveEntraAsync(new OrgEntraInput(
            Enabled: true, AllowedTenantIds: new[] { TenantA },
            ClientId: ClientId, ClientSecret: "s3cret-value", ClearClientSecret: false));

        // Dropping back to the deployment-wide registration would fail the
        // enable guard (none configured in this fixture), so disable too.
        await svc.SaveEntraAsync(new OrgEntraInput(
            Enabled: false, AllowedTenantIds: new[] { TenantA },
            ClientId: null, ClientSecret: null, ClearClientSecret: false));

        var view = await _db.NewOrganizationAdminService(_db.NewContext()).GetEntraViewAsync();
        view.ClientId.Should().BeNull();
        view.HasClientSecret.Should().BeFalse("a secret is meaningless without its registration");
    }

    // --- Deployment-wide settings ------------------------------------------

    [Fact]
    public async Task SaveEntraApp_rejects_a_client_id_that_is_not_a_guid()
    {
        var svc = NewSystemSettings(_db.NewContext());

        Func<Task> act = () => svc.SaveEntraAppAsync(
            new EntraAppInput("not-a-guid", null, ClearClientSecret: false));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("EntraClientId");
    }

    [Fact]
    public async Task SaveEntraApp_stores_the_secret_encrypted_and_clear_flag_wipes_it()
    {
        var svc = NewSystemSettings(_db.NewContext());
        await svc.SaveEntraAppAsync(new EntraAppInput(ClientId, "deploy-secret", ClearClientSecret: false));

        await using (var read = _db.NewContext())
        {
            var row = await read.SystemSettings.AsNoTracking().FirstAsync(s => s.Id == 1);
            row.EntraClientSecretEncrypted.Should().NotBeNullOrEmpty().And.NotBe("deploy-secret");
            var protector = _db.DataProtectionProvider.CreateProtector(
                SystemSettingsService.EntraClientSecretProtectionPurpose);
            protector.Unprotect(row.EntraClientSecretEncrypted!).Should().Be("deploy-secret");
        }

        await NewSystemSettings(_db.NewContext()).SaveEntraAppAsync(
            new EntraAppInput(ClientId, null, ClearClientSecret: true));
        var view = await NewSystemSettings(_db.NewContext()).GetEntraAppViewAsync();
        view.ClientId.Should().Be(ClientId);
        view.HasClientSecret.Should().BeFalse();
    }

    // --- user_external_logins contract -------------------------------------

    private async Task<int> SeedUserAsync(int orgId, string email)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = orgId,
            Email = email,
            DisplayName = "Test User",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task External_login_unique_on_provider_issuer_subject()
    {
        var userA = await SeedUserAsync(TestDb.DefaultOrgId, "a@cronus.com");
        var userB = await SeedUserAsync(TestDb.DefaultOrgId, "b@cronus.com");

        await using (var ctx = _db.NewContext())
        {
            ctx.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = userA, Provider = "entra", Issuer = TenantA,
                Subject = "oid-1", DisplayIdentity = "a@cronus.com", CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            ctx.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = userB, Provider = "entra", Issuer = TenantA,
                Subject = "oid-1", DisplayIdentity = "b@cronus.com", CreatedAt = DateTime.UtcNow,
            });
            Func<Task> act = () => ctx.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>(
                "one external identity must never map to two local users");
        }
    }

    [Fact]
    public async Task External_logins_are_scoped_to_the_current_org()
    {
        var otherOrgUser = await SeedUserAsync(TestDb.OtherOrgId, "c@other.example");
        await using (var seed = _db.NewContext())
        {
            seed.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = otherOrgUser, Provider = "entra", Issuer = TenantA,
                Subject = "oid-other", DisplayIdentity = "c@other.example", CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        // Fixture context is scoped to the Default org.
        (await ctx.UserExternalLogins.AsNoTracking().AnyAsync())
            .Should().BeFalse("the query filter must hide other orgs' external logins");
        (await ctx.UserExternalLogins.IgnoreQueryFilters().AsNoTracking().AnyAsync())
            .Should().BeTrue("the row exists; only the filter hides it");
    }

    [Fact]
    public async Task Local_login_policy_defaults_to_allow_all()
    {
        var svc = _db.NewOrganizationAdminService(_db.NewContext());
        await svc.SaveEntraAsync(new OrgEntraInput(
            Enabled: false, AllowedTenantIds: Array.Empty<string>(),
            ClientId: null, ClientSecret: null, ClearClientSecret: false));

        await using var read = _db.NewContext();
        var row = await read.OrganizationSettings.AsNoTracking()
            .FirstAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
        row.LocalLoginPolicy.Should().Be(LocalLoginPolicy.AllowAll);
    }
}
