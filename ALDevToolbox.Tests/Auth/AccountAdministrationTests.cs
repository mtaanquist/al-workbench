using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Coverage backfill for the admin / self-service surfaces on
/// <see cref="AccountService"/> that <see cref="AccountServiceTests"/>
/// doesn't reach: approve / reject signup, singleton disable / enable /
/// change-role variants, password and display-name self-service, and the
/// account-deletion org-cascade decision matrix. Issue #70.
/// </summary>
public sealed class AccountAdministrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    // ===== ApproveSignupAsync / RejectSignupAsync =====

    [Fact]
    public async Task Approve_marks_signup_approved_promotes_user_and_records_decider()
    {
        var (signupId, userId, _) = await SeedPendingSignupAsync(TestDb.OtherOrgId);
        var adminId = await SeedActiveAdminAsync(TestDb.OtherOrgId, "admin@example.com");

        await using (var ctx = _db.NewContext())
        {
            await NewUserAdmin(ctx).ApproveSignupAsync(signupId, adminId, TestDb.OtherOrgId);
        }

        await using var read = _db.NewContext();
        var req = await read.SignupRequests.IgnoreQueryFilters().FirstAsync(r => r.Id == signupId);
        req.Decision.Should().Be(SignupDecision.Approved);
        req.DecidedByUserId.Should().Be(adminId);
        req.DecidedAt.Should().NotBeNull();
        var user = await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
        user.Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public async Task Reject_marks_signup_rejected_and_removes_the_pending_user()
    {
        var (signupId, userId, _) = await SeedPendingSignupAsync(TestDb.OtherOrgId);
        var adminId = await SeedActiveAdminAsync(TestDb.OtherOrgId, "admin@example.com");

        await using (var ctx = _db.NewContext())
        {
            await NewUserAdmin(ctx).RejectSignupAsync(signupId, adminId, TestDb.OtherOrgId);
        }

        await using var read = _db.NewContext();
        var req = await read.SignupRequests.IgnoreQueryFilters().FirstAsync(r => r.Id == signupId);
        req.Decision.Should().Be(SignupDecision.Rejected);
        (await read.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId))
            .Should().BeFalse("rejected signups remove the placeholder user row");
    }

    [Fact]
    public async Task Approve_refuses_when_acting_org_does_not_match_the_request()
    {
        var (signupId, _, _) = await SeedPendingSignupAsync(TestDb.OtherOrgId);
        var adminId = await SeedActiveAdminAsync(TestDb.DefaultOrgId, "admin@example.com");

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).ApproveSignupAsync(signupId, adminId, TestDb.DefaultOrgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("RequestId");
    }

    [Fact]
    public async Task Approve_refuses_when_the_signup_has_already_been_decided()
    {
        var (signupId, _, _) = await SeedPendingSignupAsync(TestDb.OtherOrgId);
        var adminId = await SeedActiveAdminAsync(TestDb.OtherOrgId, "admin@example.com");
        await using (var ctx = _db.NewContext())
        {
            await NewUserAdmin(ctx).ApproveSignupAsync(signupId, adminId, TestDb.OtherOrgId);
        }

        await using var ctx2 = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx2).ApproveSignupAsync(signupId, adminId, TestDb.OtherOrgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Decision");
    }

    // ===== Disable / Enable / ChangeRole singletons =====

    [Fact]
    public async Task Disable_then_enable_round_trips_user_status()
    {
        var orgId = TestDb.OtherOrgId;
        await SeedActiveAdminAsync(orgId, "primary@example.com");
        var subjectId = await SeedActiveUserAsync(orgId, "subject@example.com", UserRole.User);

        await using (var ctx = _db.NewContext()) await NewUserAdmin(ctx).DisableUserAsync(subjectId, orgId);
        await using (var read1 = _db.NewContext())
        {
            (await read1.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == subjectId)).Status
                .Should().Be(UserStatus.Disabled);
        }

        await using (var ctx = _db.NewContext()) await NewUserAdmin(ctx).EnableUserAsync(subjectId, orgId);
        await using var read2 = _db.NewContext();
        (await read2.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == subjectId)).Status
            .Should().Be(UserStatus.Active);
    }

    [Fact]
    public async Task Disable_cuts_every_way_back_in()
    {
        // Disabling is the "this person has left" action: the cookie must die
        // on its next revalidation, and their tokens and assistant consents
        // must not come back to life if the account is re-enabled later. The
        // revocations are filtered writes, so this runs in the fixture's
        // ambient org, as the admin endpoint does in production.
        var orgId = TestDb.DefaultOrgId;
        await SeedActiveAdminAsync(orgId, "primary-default@example.com");
        var subjectId = await SeedActiveUserAsync(orgId, "leaver@example.com", UserRole.User);
        await using (var seed = _db.NewContext())
        {
            seed.PersonalAccessTokens.Add(new PersonalAccessToken
            {
                UserId = subjectId, OrganizationId = orgId, Name = "laptop",
                TokenHash = "hash-1", TokenPrefix = "aldt_pat_1", CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            seed.OAuthConsents.Add(new OAuthConsent
            {
                UserId = subjectId, OrganizationId = orgId, ClientId = "claude-desktop",
                ScopesGranted = "mcp", GrantedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }
        var tokens = _db.OpenIddictTokens;
        var token = await tokens.CreateAsync(new OpenIddict.Abstractions.OpenIddictTokenDescriptor
        {
            Subject = subjectId.ToString(),
            Type = OpenIddict.Abstractions.OpenIddictConstants.TokenTypes.Bearer,
            Status = OpenIddict.Abstractions.OpenIddictConstants.Statuses.Valid,
            CreationDate = _clock.GetUtcNow(),
        });

        await using (var ctx = _db.NewContext()) await NewUserAdmin(ctx).DisableUserAsync(subjectId, orgId);

        (await tokens.GetStatusAsync((await tokens.FindByIdAsync((await tokens.GetIdAsync(token))!))!))
            .Should().Be(OpenIddict.Abstractions.OpenIddictConstants.Statuses.Revoked);

        await using var read = _db.NewContext();
        var now = _clock.GetUtcNow().UtcDateTime;
        (await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == subjectId)).CredentialsChangedAt
            .Should().Be(now, "CookieSessionRevalidation drops sessions that started before this");
        (await read.PersonalAccessTokens.IgnoreQueryFilters().SingleAsync(p => p.UserId == subjectId)).RevokedAt
            .Should().Be(now);
        (await read.OAuthConsents.IgnoreQueryFilters().SingleAsync(c => c.UserId == subjectId)).RevokedAt
            .Should().Be(now);
    }

    [Fact]
    public async Task Disable_refuses_to_lock_out_the_last_active_admin()
    {
        var orgId = TestDb.OtherOrgId;
        var soloAdmin = await SeedActiveAdminAsync(orgId, "lonely@example.com");

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).DisableUserAsync(soloAdmin, orgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LastAdmin");
    }

    [Fact]
    public async Task ChangeRole_singleton_demote_refuses_to_strip_the_last_active_admin()
    {
        var orgId = TestDb.OtherOrgId;
        var soloAdmin = await SeedActiveAdminAsync(orgId, "lonely@example.com");

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).ChangeRoleAsync(soloAdmin, UserRole.User, orgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LastAdmin");
    }

    [Fact]
    public async Task ChangeDisplayName_admin_corrects_a_users_name_and_trims_it()
    {
        var orgId = TestDb.OtherOrgId;
        var subjectId = await SeedActiveUserAsync(orgId, "typo@example.com", UserRole.User);

        await using (var ctx = _db.NewContext())
        {
            await NewUserAdmin(ctx).ChangeDisplayNameAsync(subjectId, "   Correct Name   ", orgId);
        }

        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == subjectId)).DisplayName
            .Should().Be("Correct Name", "the admin edit trims surrounding whitespace like the self-service change");
    }

    [Fact]
    public async Task ChangeDisplayName_rejects_a_name_outside_the_length_bounds()
    {
        var orgId = TestDb.OtherOrgId;
        var subjectId = await SeedActiveUserAsync(orgId, "shortname@example.com", UserRole.User);

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).ChangeDisplayNameAsync(subjectId, "x", orgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("DisplayName");
    }

    [Fact]
    public async Task ChangeDisplayName_refuses_to_edit_a_user_in_another_org()
    {
        // The acting admin's org must own the target user — LoadUserAsync is
        // the tenant-isolation guard for this admin action.
        var subjectId = await SeedActiveUserAsync(TestDb.OtherOrgId, "outsider@example.com", UserRole.User);

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).ChangeDisplayNameAsync(subjectId, "New Name", TestDb.DefaultOrgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("UserId");
    }

    [Fact]
    public async Task ChangeRole_promotes_user_to_editor_without_tripping_last_admin_guard()
    {
        var orgId = TestDb.OtherOrgId;
        await SeedActiveAdminAsync(orgId, "primary@example.com");
        var subjectId = await SeedActiveUserAsync(orgId, "subject@example.com", UserRole.User);

        await using (var ctx = _db.NewContext())
        {
            await NewUserAdmin(ctx).ChangeRoleAsync(subjectId, UserRole.Editor, orgId);
        }

        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == subjectId)).Role
            .Should().Be(UserRole.Editor);
    }

    [Fact]
    public async Task ChangeRole_refuses_to_demote_the_last_admin_to_editor()
    {
        var orgId = TestDb.OtherOrgId;
        var soloAdmin = await SeedActiveAdminAsync(orgId, "lonely@example.com");

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).ChangeRoleAsync(soloAdmin, UserRole.Editor, orgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LastAdmin");
    }

    [Fact]
    public async Task ChangeRole_allows_demotion_to_editor_when_another_admin_remains()
    {
        var orgId = TestDb.OtherOrgId;
        var demoted = await SeedActiveAdminAsync(orgId, "one@example.com");
        await SeedActiveAdminAsync(orgId, "two@example.com");

        await using (var ctx = _db.NewContext())
        {
            await NewUserAdmin(ctx).ChangeRoleAsync(demoted, UserRole.Editor, orgId);
        }

        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == demoted)).Role
            .Should().Be(UserRole.Editor);
    }

    [Fact]
    public async Task Singleton_actions_refuse_to_cross_organisation_boundaries()
    {
        // Acting in DefaultOrg, target is in OtherOrg: must refuse.
        await SeedActiveAdminAsync(TestDb.OtherOrgId, "lonely@example.com");
        var subjectId = await SeedActiveUserAsync(TestDb.OtherOrgId, "victim@example.com", UserRole.User);

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).DisableUserAsync(subjectId, TestDb.DefaultOrgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("UserId");
    }

    // ===== ChangePasswordAsync =====

    [Fact]
    public async Task Change_password_rejects_wrong_current_password()
    {
        var userId = await SeedActiveUserWithPasswordAsync(TestDb.OtherOrgId, "p@example.com", "correctpasswordlong");

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewService(ctx).ChangePasswordAsync(userId, "wrong-current", "newpasswordlong12345");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("CurrentPassword");
    }

    [Fact]
    public async Task Change_password_rejects_new_password_that_fails_policy()
    {
        var userId = await SeedActiveUserWithPasswordAsync(TestDb.OtherOrgId, "p@example.com", "correctpasswordlong");

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewService(ctx).ChangePasswordAsync(userId, "correctpasswordlong", "short");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("NewPassword");
    }

    [Fact]
    public async Task Change_password_happy_path_re_hashes_so_old_password_no_longer_verifies()
    {
        var userId = await SeedActiveUserWithPasswordAsync(TestDb.OtherOrgId, "p@example.com", "correctpasswordlong");

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ChangePasswordAsync(userId, "correctpasswordlong", "newcorrectpasswordlong");
        }

        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
        var auth = NewAuth(read);
        auth.VerifyPassword("newcorrectpasswordlong", user.PasswordHash).Should().BeTrue();
        auth.VerifyPassword("correctpasswordlong", user.PasswordHash).Should().BeFalse();
    }

    // ===== ChangeDisplayNameAsync =====

    [Fact]
    public async Task Change_display_name_trims_whitespace_and_rejects_too_short()
    {
        var userId = await SeedActiveUserAsync(TestDb.OtherOrgId, "name@example.com", UserRole.User);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ChangeDisplayNameAsync(userId, "   Padded Name   ");
        }
        await using (var read = _db.NewContext())
        {
            (await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId)).DisplayName
                .Should().Be("Padded Name");
        }

        await using var rejectCtx = _db.NewContext();
        Func<Task> act = () => NewService(rejectCtx).ChangeDisplayNameAsync(userId, " ");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("DisplayName");
    }

    // ===== GetAccountOverviewAsync (the Account page's opening read) =====

    [Fact]
    public async Task Account_overview_returns_the_user_their_organisation_and_the_entra_flag()
    {
        var userId = await SeedActiveUserAsync(TestDb.DefaultOrgId, "member@cronus.test", UserRole.User);

        await using var ctx = _db.NewContext();
        var overview = await NewService(ctx).GetAccountOverviewAsync(userId);

        overview.Should().NotBeNull();
        overview!.User.Id.Should().Be(userId);
        overview.Organization!.Id.Should().Be(TestDb.DefaultOrgId);
        overview.OrganizationEntraEnabled.Should().BeFalse("no organisation settings row turns it on");
    }

    [Fact]
    public async Task Account_overview_is_null_for_a_user_outside_the_current_organisation()
    {
        var otherUserId = await SeedActiveUserAsync(TestDb.OtherOrgId, "stranger@cronus.test", UserRole.User);

        await using var ctx = _db.NewContext();
        (await NewService(ctx).GetAccountOverviewAsync(otherUserId)).Should().BeNull();
    }

    // ===== GetAdministrationPageAsync (Administration -> Users read model) =====

    [Fact]
    public async Task Administration_page_lists_this_orgs_pending_signups_members_and_invites()
    {
        var (_, pendingUserId, _) = await SeedPendingSignupAsync(TestDb.DefaultOrgId);
        var adminId = await SeedActiveAdminAsync(TestDb.DefaultOrgId, "admin@cronus.test");
        await SeedInviteAsync(TestDb.DefaultOrgId, adminId, "invitee@cronus.test");

        await using var ctx = _db.NewContext();
        var page = await NewUserAdmin(ctx).GetAdministrationPageAsync(TestDb.DefaultOrgId);

        page.Pending.Should().ContainSingle()
            .Which.User!.Id.Should().Be(pendingUserId);
        page.Active.Select(u => u.Id).Should().Contain(adminId);
        page.Active.Select(u => u.Id).Should().NotContain(pendingUserId,
            "pending accounts belong in the signups section, not the members list");
        page.Invites.Should().ContainSingle()
            .Which.InvitedBy!.Id.Should().Be(adminId);
    }

    [Fact]
    public async Task Administration_page_does_not_leak_another_orgs_members_or_signups()
    {
        await SeedPendingSignupAsync(TestDb.OtherOrgId);
        var otherAdminId = await SeedActiveAdminAsync(TestDb.OtherOrgId, "other-admin@cronus.test");
        var ownAdminId = await SeedActiveAdminAsync(TestDb.DefaultOrgId, "own-admin@cronus.test");

        await using var ctx = _db.NewContext();
        var page = await NewUserAdmin(ctx).GetAdministrationPageAsync(TestDb.DefaultOrgId);

        page.Pending.Should().BeEmpty();
        page.Active.Select(u => u.Id).Should().Equal(ownAdminId);
        page.Active.Select(u => u.Id).Should().NotContain(otherAdminId);
    }

    // ===== Fixture helpers =====

    private AuthService NewAuth(Data.AppDbContext ctx) =>
        new(ctx, NullLogger<AuthService>.Instance, _clock);

    private SystemSettingsService NewSettings(Data.AppDbContext ctx) =>
        new(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, _clock);

    /// <summary>
    /// Slim AccountService — signup + self-service. Used for ChangePassword,
    /// ChangeDisplayName, DeleteAccount.
    /// </summary>
    private AccountService NewService(Data.AppDbContext ctx) =>
        new(ctx, NewAuth(ctx), NewSettings(ctx),
            new ALDevToolbox.Services.SingleTenant.SingleTenantModeState(false),
            NullLogger<AccountService>.Instance, _clock);

    /// <summary>
    /// UserAdministrationService — admin actions on existing users. Used for
    /// ApproveSignup / RejectSignup / Disable / Enable / ChangeRole.
    /// </summary>
    private UserAdministrationService NewUserAdmin(Data.AppDbContext ctx) =>
        _db.NewUserAdministrationService(ctx, _clock);

    private async Task<int> SeedActiveAdminAsync(int orgId, string email) =>
        await SeedActiveUserAsync(orgId, email, UserRole.Admin);

    private async Task<int> SeedActiveUserAsync(int orgId, string email, UserRole role)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = orgId,
            Email = email,
            DisplayName = email,
            PasswordHash = "placeholder",
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private async Task<int> SeedActiveUserWithPasswordAsync(int orgId, string email, string password)
    {
        await using var ctx = _db.NewContext();
        var auth = NewAuth(ctx);
        var user = new User
        {
            OrganizationId = orgId,
            Email = email,
            DisplayName = email,
            PasswordHash = auth.HashPassword(password),
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private async Task SeedInviteAsync(int orgId, int invitedByUserId, string email)
    {
        await using var ctx = _db.NewContext();
        ctx.Invites.Add(new Invite
        {
            OrganizationId = orgId,
            Email = email,
            Role = UserRole.User,
            TokenHash = Guid.NewGuid().ToString("N"),
            InvitedByUserId = invitedByUserId,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            ExpiresAt = _clock.GetUtcNow().UtcDateTime.AddDays(7),
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<(int SignupId, int UserId, int OrgId)> SeedPendingSignupAsync(int orgId)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = orgId,
            Email = $"pending-{Guid.NewGuid():N}@example.com",
            DisplayName = "Pending",
            PasswordHash = "placeholder",
            Role = UserRole.User,
            Status = UserStatus.Pending,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        var req = new SignupRequest
        {
            OrganizationId = orgId,
            UserId = user.Id,
            Email = user.Email,
            RequestedAt = _clock.GetUtcNow().UtcDateTime,
            Decision = SignupDecision.Pending,
        };
        ctx.SignupRequests.Add(req);
        await ctx.SaveChangesAsync();
        return (req.Id, user.Id, orgId);
    }
}
