using ALDevToolbox.Data;
using ALDevToolbox.Data.Configurations;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Tests for local_login_policy = EntraOnly enforcement (issue #552, slice
/// 3): password/magic-link/reset refusals, the SiteAdmin break-glass
/// exemption, the enable guard, and the self-service link/unlink rules.
/// </summary>
public sealed class EntraLoginPolicyTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Password = "correct-horse-battery";
    private const string Ip = "203.0.113.10";

    private AuthService NewAuth(AppDbContext ctx) =>
        new(ctx, NullLogger<AuthService>.Instance, TimeProvider.System);

    private EntraSignInService NewEntra(AppDbContext ctx) =>
        new(ctx, NewAuth(ctx), _db.OrgContext, _db.DataProtectionProvider, TimeProvider.System,
            NullLogger<EntraSignInService>.Instance);

    private async Task SetPolicyAsync(int orgId, LocalLoginPolicy policy, bool entraEnabled = true)
    {
        await using var ctx = _db.NewContext();
        var row = await ctx.OrganizationSettings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OrganizationId == orgId);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            ctx.OrganizationSettings.Add(row);
        }
        row.EntraEnabled = entraEnabled;
        row.EntraAllowedTenantIds = new List<string> { TenantA };
        row.LocalLoginPolicy = policy;
        row.UpdatedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
    }

    private async Task<int> SeedUserAsync(
        int orgId, string email, bool siteAdmin = false, UserRole role = UserRole.User, bool withLink = false)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = orgId,
            Email = email,
            DisplayName = "Test User",
            PasswordHash = NewAuth(ctx).HashPassword(Password),
            Role = role,
            Status = UserStatus.Active,
            IsSiteAdmin = siteAdmin,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        if (withLink)
        {
            ctx.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = user.Id, Provider = "entra", Issuer = TenantA,
                Subject = Guid.NewGuid().ToString(), DisplayIdentity = email, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        return user.Id;
    }

    [Fact]
    public async Task Password_login_is_refused_for_an_entra_only_org()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);
        await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await using var ctx = _db.NewContext();

        var (outcome, user) = await NewAuth(ctx).TryLoginAsync("mette@cronus.com", Password, Ip);

        outcome.Should().Be(LoginOutcome.LocalLoginDisabled);
        user.Should().BeNull();
    }

    [Fact]
    public async Task Site_admin_password_login_survives_entra_only_as_break_glass()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);
        await SeedUserAsync(TestDb.DefaultOrgId, "operator@cronus.com", siteAdmin: true);
        await using var ctx = _db.NewContext();

        var (outcome, user) = await NewAuth(ctx).TryLoginAsync("operator@cronus.com", Password, Ip);

        outcome.Should().Be(LoginOutcome.Success);
        user.Should().NotBeNull();
    }

    [Fact]
    public async Task Magic_link_issuance_and_consumption_are_refused_for_an_entra_only_org()
    {
        await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        string preIssued;
        await using (var ctx = _db.NewContext())
        {
            // Issue while the org still allows local login...
            var resets = new PasswordResetService(ctx, NewAuth(ctx), TimeProvider.System, _db.OpenIddictTokens);
            preIssued = (await resets.CreateMagicLoginTokenAsync("mette@cronus.com", Ip))!;
            preIssued.Should().NotBeNull();
        }

        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);

        await using (var ctx = _db.NewContext())
        {
            var resets = new PasswordResetService(ctx, NewAuth(ctx), TimeProvider.System, _db.OpenIddictTokens);
            (await resets.CreateMagicLoginTokenAsync("mette@cronus.com", Ip))
                .Should().BeNull("no new magic links for a Microsoft-only org");
            Func<Task> act = () => resets.ConsumeMagicLoginTokenAsync(preIssued);
            var ex = await act.Should().ThrowAsync<PlanValidationException>(
                "a link issued before the policy flip must not bypass it");
            ex.Which.Errors["Token"].Should().Contain("Microsoft");
        }
    }

    [Fact]
    public async Task Password_reset_tokens_are_not_issued_for_an_entra_only_org()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);
        await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await using var ctx = _db.NewContext();
        var resets = new PasswordResetService(ctx, NewAuth(ctx), TimeProvider.System, _db.OpenIddictTokens);

        (await resets.CreatePasswordResetTokenAsync("mette@cronus.com", "10.0.0.1")).Should().BeNull();
    }

    [Fact]
    public async Task A_reset_link_issued_before_the_switch_to_entra_only_cannot_set_a_password()
    {
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        string preIssued;
        await using (var ctx = _db.NewContext())
        {
            var resets = new PasswordResetService(ctx, NewAuth(ctx), TimeProvider.System, _db.OpenIddictTokens);
            preIssued = (await resets.CreatePasswordResetTokenAsync("mette@cronus.com", Ip))!;
            preIssued.Should().NotBeNull();
        }
        string hashBefore;
        await using (var read = _db.NewContext())
        {
            hashBefore = (await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).PasswordHash;
        }

        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);

        await using (var ctx = _db.NewContext())
        {
            var resets = new PasswordResetService(ctx, NewAuth(ctx), TimeProvider.System, _db.OpenIddictTokens);
            Func<Task> act = () => resets.ConsumePasswordResetTokenAsync(preIssued, "a-brand-new-password-1");
            var ex = await act.Should().ThrowAsync<PlanValidationException>(
                "a link issued before the policy flip must not bypass it");
            ex.Which.Errors["Token"].Should().Contain("Microsoft");
        }

        await using var after = _db.NewContext();
        (await after.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).PasswordHash.Should().Be(hashBefore);
        (await after.PasswordResetTokens.IgnoreQueryFilters().SingleAsync(t => t.UserId == userId)).ConsumedAt
            .Should().BeNull();
    }

    /// <summary>
    /// The switch lands after the consume has checked the policy and before it commits.
    /// The settings row is held until the commit, so the switch waits for the reset rather
    /// than slipping in under it and leaving a password set in a Microsoft-only org.
    /// </summary>
    [Fact]
    public async Task A_switch_to_entra_only_during_a_reset_waits_for_the_reset_to_finish()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.AllowAll);
        await SeedUserAsync(TestDb.DefaultOrgId, "racing@cronus.com");
        string token;
        await using (var ctx = _db.NewContext())
        {
            var resets = new PasswordResetService(ctx, NewAuth(ctx), TimeProvider.System, _db.OpenIddictTokens);
            token = (await resets.CreatePasswordResetTokenAsync("racing@cronus.com", Ip))!;
        }

        Task? switchToEntraOnly = null;
        var switchWaited = false;
        // The token claim is the consume's first write after its policy check.
        var afterCheck = new BeforeCommand(
            sql => sql.Contains("UPDATE") && sql.Contains("password_reset_tokens") && !sql.Contains("FOR UPDATE"),
            async () =>
            {
                switchToEntraOnly = Task.Run(async () =>
                {
                    await using var admin = _db.NewContext();
                    await admin.OrganizationSettings.IgnoreQueryFilters()
                        .Where(s => s.OrganizationId == TestDb.DefaultOrgId)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.LocalLoginPolicy, LocalLoginPolicy.EntraOnly));
                });
                // The switch either finishes (nothing held the row) or queues on a lock.
                await using var watch = _db.NewContext();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!switchToEntraOnly.IsCompleted && DateTime.UtcNow < deadline)
                {
                    var waiting = await watch.Database.SqlQuery<int>(
                        $"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%organization_settings%'")
                        .SingleAsync();
                    if (waiting > 0) { switchWaited = true; break; }
                }
            });

        await using (var consume = _db.NewContext(afterCheck))
        {
            var resets = new PasswordResetService(consume, NewAuth(consume), TimeProvider.System, _db.OpenIddictTokens);
            await resets.ConsumePasswordResetTokenAsync(token, "a-brand-new-password-1");
        }
        await switchToEntraOnly!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        afterCheck.Fired.Should().BeTrue("the switch has to land in the window under test");
        switchWaited.Should().BeTrue("a switch made after the policy check must wait for the reset to commit");
        await using var read = _db.NewContext();
        (await read.OrganizationSettings.IgnoreQueryFilters().SingleAsync(s => s.OrganizationId == TestDb.DefaultOrgId))
            .LocalLoginPolicy.Should().Be(LocalLoginPolicy.EntraOnly, "the switch still lands, after the reset");
    }

    [Fact]
    public async Task A_site_admin_in_an_entra_only_org_can_still_reset_their_password_as_break_glass()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);
        await SeedUserAsync(TestDb.DefaultOrgId, "operator@cronus.com", siteAdmin: true);
        const string newPassword = "a-brand-new-password-1";

        await using (var ctx = _db.NewContext())
        {
            var resets = new PasswordResetService(ctx, NewAuth(ctx), TimeProvider.System, _db.OpenIddictTokens);
            var token = await resets.CreatePasswordResetTokenAsync("operator@cronus.com", Ip);
            token.Should().NotBeNull();
            await resets.ConsumePasswordResetTokenAsync(token!, newPassword);
        }

        await using var login = _db.NewContext();
        var (outcome, _) = await NewAuth(login).TryLoginAsync("operator@cronus.com", newPassword, Ip);
        outcome.Should().Be(LoginOutcome.Success);
    }

    private AccountService NewAccounts(AppDbContext ctx) =>
        new(ctx, NewAuth(ctx),
            new SystemSettingsService(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System),
            new ALDevToolbox.Services.SingleTenant.SingleTenantModeState(false),
            NullLogger<AccountService>.Instance, TimeProvider.System, _db.OpenIddictTokens);

    [Fact]
    public async Task Changing_a_password_is_refused_for_a_member_of_an_entra_only_org()
    {
        // A password set now would come back to life if the policy were
        // relaxed later (#1455).
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);
        string hashBefore;
        await using (var read = _db.NewContext())
        {
            hashBefore = (await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).PasswordHash;
        }

        await using (var ctx = _db.NewContext())
        {
            Func<Task> act = () => NewAccounts(ctx).ChangePasswordAsync(userId, Password, "a-brand-new-password-1", "198.51.100.1");
            var ex = await act.Should().ThrowAsync<PlanValidationException>();
            ex.Which.Errors["Password"].Should().Contain("Microsoft");
        }

        await using var after = _db.NewContext();
        (await after.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).PasswordHash.Should().Be(hashBefore);
    }

    [Fact]
    public async Task The_account_page_knows_password_sign_in_is_off_for_members_but_not_site_admins()
    {
        // What decides whether the Account page shows the password form; the
        // same rule as the refusal above.
        var memberId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        var operatorId = await SeedUserAsync(TestDb.DefaultOrgId, "operator@cronus.com", siteAdmin: true);
        await using (var before = _db.NewContext())
        {
            (await NewAccounts(before).GetAccountOverviewAsync(memberId))!.PasswordSignInDisabled.Should().BeFalse();
        }

        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);

        await using var ctx = _db.NewContext();
        (await NewAccounts(ctx).GetAccountOverviewAsync(memberId))!.PasswordSignInDisabled.Should().BeTrue();
        (await NewAccounts(ctx).GetAccountOverviewAsync(operatorId))!.PasswordSignInDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_site_admin_in_an_entra_only_org_can_still_change_their_password()
    {
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "operator@cronus.com", siteAdmin: true);
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);
        const string newPassword = "a-brand-new-password-1";

        await using (var ctx = _db.NewContext())
        {
            await NewAccounts(ctx).ChangePasswordAsync(userId, Password, newPassword, "198.51.100.1");
        }

        await using var login = _db.NewContext();
        var (outcome, _) = await NewAuth(login).TryLoginAsync("operator@cronus.com", newPassword, Ip);
        outcome.Should().Be(LoginOutcome.Success);
    }

    [Fact]
    public async Task Entra_only_cannot_be_enabled_until_an_admin_has_a_link()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);
        var input = new OrgEntraInput(
            Enabled: true, AllowedTenantIds: new[] { TenantA },
            ClientId: "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", ClientSecret: null,
            ClearClientSecret: false, LocalLoginPolicy: LocalLoginPolicy.EntraOnly);

        Func<Task> act = () => svc.SaveEntraAsync(input);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LocalLoginPolicy");

        await SeedUserAsync(TestDb.DefaultOrgId, "admin@cronus.com", role: UserRole.Admin, withLink: true);
        await svc.SaveEntraAsync(input);

        var view = await _db.NewOrganizationAdminService(_db.NewContext()).GetEntraViewAsync();
        view.LocalLoginPolicy.Should().Be(LocalLoginPolicy.EntraOnly);
    }

    [Fact]
    public async Task Entra_only_requires_entra_to_be_enabled()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);

        Func<Task> act = () => svc.SaveEntraAsync(new OrgEntraInput(
            Enabled: false, AllowedTenantIds: Array.Empty<string>(),
            ClientId: null, ClientSecret: null, ClearClientSecret: false,
            LocalLoginPolicy: LocalLoginPolicy.EntraOnly));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LocalLoginPolicy");
    }

    [Fact]
    public async Task Unlink_refuses_the_last_link_in_an_entra_only_org()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com", withLink: true);
        await using var ctx = _db.NewContext();
        var entra = NewEntra(ctx);
        var linkId = (await entra.ListLinksAsync(userId)).Single().Id;

        Func<Task> act = () => entra.UnlinkAsync(userId, linkId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors["EntraLink"].Should().Contain("lock you out");
    }

    [Fact]
    public async Task Unlink_works_when_local_login_is_still_allowed()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.AllowAll);
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com", withLink: true);
        await using var ctx = _db.NewContext();
        var entra = NewEntra(ctx);
        var linkId = (await entra.ListLinksAsync(userId)).Single().Id;

        await entra.UnlinkAsync(userId, linkId);

        (await entra.ListLinksAsync(userId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Link_refuses_a_tenant_not_on_the_orgs_allow_list()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.AllowAll);
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await using var ctx = _db.NewContext();

        Func<Task> act = () => NewEntra(ctx).LinkAsync(userId,
            new EntraTokenIdentity("99999999-9999-9999-9999-999999999999", Oid, "mette@cronus.com", "Mette"));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("EntraLink");
    }

    [Fact]
    public async Task Link_refuses_an_identity_already_connected_to_someone_else()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.AllowAll);
        var first = await SeedUserAsync(TestDb.DefaultOrgId, "first@cronus.com");
        var second = await SeedUserAsync(TestDb.DefaultOrgId, "second@cronus.com");
        await using var ctx = _db.NewContext();
        var entra = NewEntra(ctx);
        await entra.LinkAsync(first, new EntraTokenIdentity(TenantA, Oid, "first@cronus.com", null));

        Func<Task> act = () => entra.LinkAsync(second, new EntraTokenIdentity(TenantA, Oid, "second@cronus.com", null));
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors["EntraLink"].Should().Contain("different user");

        // Re-linking your own identity is a no-op, not an error.
        await entra.LinkAsync(first, new EntraTokenIdentity(TenantA, Oid, "first@cronus.com", null));
    }

    /// <summary>
    /// Issue #736. The pre-check sees past the tenant filter, so a stranger
    /// seeded up front is refused by the check, not by the index. This pins the
    /// backstop instead: the identity is claimed by another organisation
    /// *during* the save, so the check found nothing and the insert loses on
    /// the deployment-wide unique index. That has to read as the same refusal,
    /// not a DbUpdateException the endpoint turns into a 500.
    /// </summary>
    [Fact]
    public async Task Link_refuses_an_identity_claimed_between_the_check_and_the_save()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.AllowAll);
        var mine = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        var strangerId = await SeedUserAsync(TestDb.OtherOrgId, "stranger@cronus.com");

        await using var ctx = _db.NewContext(new ClaimIdentityDuringSaveInterceptor(_db, strangerId));
        var entra = NewEntra(ctx);

        Func<Task> act = () => entra.LinkAsync(mine, new EntraTokenIdentity(TenantA, Oid, "mette@cronus.com", null));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors["EntraLink"].Should().Contain("different user");
        // Nothing about the other organisation reaches the person reading it.
        ex.Which.Errors["EntraLink"].Should().NotContain("stranger@cronus.com").And.NotContain("organisation");

        await using var read = _db.NewContext();
        var rows = await read.UserExternalLogins.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Provider == EntraSignInService.ProviderName
                && l.Issuer == TenantA && l.Subject == Oid)
            .ToListAsync();
        rows.Should().ContainSingle("the refused link wrote nothing")
            .Which.UserId.Should().Be(strangerId, "the row that won the race is untouched");
    }

    /// <summary>
    /// The catch names the identity index, so a save that breaks a *different*
    /// unique index is still a real fault. (The SQLSTATE half of the same
    /// contract is covered by
    /// <c>StartupSeedRaceTests.IsUniqueViolation_does_not_swallow_other_database_faults</c>.)
    /// </summary>
    [Fact]
    public void A_unique_violation_on_another_index_is_not_the_identity_clash()
    {
        var elsewhere = new PostgresException(
            "duplicate key value violates unique constraint", "ERROR", "ERROR",
            PostgresErrorCodes.UniqueViolation, constraintName: "IX_users_email");
        DbErrors.IsUniqueViolation(new DbUpdateException("save failed", elsewhere),
                UserExternalLoginConfiguration.IdentityIndexName)
            .Should().BeFalse("a clash on another index is a different rule and must propagate as a fault");

        var identity = new PostgresException(
            "duplicate key value violates unique constraint", "ERROR", "ERROR",
            PostgresErrorCodes.UniqueViolation,
            constraintName: UserExternalLoginConfiguration.IdentityIndexName);
        DbErrors.IsUniqueViolation(new DbUpdateException("save failed", identity),
                UserExternalLoginConfiguration.IdentityIndexName)
            .Should().BeTrue("this is the one LinkAsync translates into a refusal");
    }

    /// <summary>
    /// Gives the Microsoft identity under test to a user in another
    /// organisation while the context under test is saving, which is the only
    /// way to land between <c>LinkAsync</c>'s existence check and its insert.
    /// </summary>
    private sealed class ClaimIdentityDuringSaveInterceptor : SaveChangesInterceptor
    {
        private readonly TestDb _db;
        private readonly int _strangerUserId;
        private bool _claimed;

        public ClaimIdentityDuringSaveInterceptor(TestDb db, int strangerUserId)
        {
            _db = db;
            _strangerUserId = strangerUserId;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_claimed)
            {
                _claimed = true;
                await using var other = _db.NewContext();
                other.UserExternalLogins.Add(new UserExternalLogin
                {
                    UserId = _strangerUserId,
                    Provider = EntraSignInService.ProviderName,
                    Issuer = TenantA,
                    Subject = Oid,
                    DisplayIdentity = "stranger@cronus.com",
                    CreatedAt = DateTime.UtcNow,
                });
                await other.SaveChangesAsync(cancellationToken);
            }
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Login_surface_collapses_the_password_form_only_when_every_org_is_entra_only()
    {
        // "Every org" includes the system org, which is a row like any other.
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);
        await SetPolicyAsync(TestDb.SystemOrgId, LocalLoginPolicy.EntraOnly, entraEnabled: false);
        await using (var ctx = _db.NewContext())
        {
            var (entra, passwordPrimary) = await NewEntra(ctx).GetLoginSurfaceAsync();
            entra.Should().BeTrue();
            passwordPrimary.Should().BeTrue("the Other org still allows passwords");
        }

        await SetPolicyAsync(TestDb.OtherOrgId, LocalLoginPolicy.EntraOnly, entraEnabled: false);
        await using (var ctx = _db.NewContext())
        {
            var (_, passwordPrimary) = await NewEntra(ctx).GetLoginSurfaceAsync();
            passwordPrimary.Should().BeFalse("every org is Microsoft-only now");
        }
    }
}
