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
        ActAs(TestDb.OtherOrgId, adminId);

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
        ActAs(TestDb.OtherOrgId, adminId);

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
        ActAs(TestDb.DefaultOrgId, adminId);

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
        ActAs(TestDb.OtherOrgId, adminId);
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
        ActAs(orgId, await SeedActiveAdminAsync(orgId, "primary@example.com"));
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
        ActAs(orgId, await SeedActiveAdminAsync(orgId, "primary-default@example.com"));
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

        // Read back through a fresh context: the revoke is one set-based UPDATE
        // (#1259), which the manager's own tracked copy of the token doesn't see.
        var tokenId = await tokens.GetIdAsync(token);
        await using var read = _db.NewContext();
        (await read.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>().AsNoTracking()
                .SingleAsync(t => t.Id == tokenId)).Status
            .Should().Be(OpenIddict.Abstractions.OpenIddictConstants.Statuses.Revoked);

        var now = _clock.GetUtcNow().UtcDateTime;
        (await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == subjectId)).CredentialsChangedAt
            .Should().Be(now, "CookieSessionRevalidation drops sessions that started before this");
        (await read.PersonalAccessTokens.IgnoreQueryFilters().SingleAsync(p => p.UserId == subjectId)).RevokedAt
            .Should().Be(now);
        (await read.OAuthConsents.IgnoreQueryFilters().SingleAsync(c => c.UserId == subjectId)).RevokedAt
            .Should().Be(now);
    }

    [Fact]
    public async Task Disable_revokes_the_invites_the_user_sent_that_are_still_pending()
    {
        // An Admin on the way out could otherwise invite an address they
        // control as Admin and use it for a week after being disabled (#1544).
        var orgId = TestDb.DefaultOrgId;
        var actorId = await SeedActiveAdminAsync(orgId, "primary-default@example.com");
        ActAs(orgId, actorId);
        var leaverId = await SeedActiveUserAsync(orgId, "leaver-admin@example.com", UserRole.Admin);
        var now = _clock.GetUtcNow().UtcDateTime;
        Invite NewInvite(int by, string email, DateTime? acceptedAt = null) => new()
        {
            OrganizationId = orgId, Email = email, Role = UserRole.Admin, TokenHash = "hash-" + email,
            CreatedAt = now, ExpiresAt = now.AddDays(7), AcceptedAt = acceptedAt, InvitedByUserId = by,
        };
        await using (var seed = _db.NewContext())
        {
            seed.Invites.AddRange(
                NewInvite(leaverId, "backdoor@elsewhere.example"),
                NewInvite(leaverId, "accepted@cronus.example", acceptedAt: now.AddHours(-1)),
                NewInvite(actorId, "colleague@cronus.example"));
            await seed.SaveChangesAsync();
        }

        await using (var ctx = _db.NewContext()) await NewUserAdmin(ctx).DisableUserAsync(leaverId, orgId);

        await using var read = _db.NewContext();
        var invites = await read.Invites.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(i => i.Email);
        invites["backdoor@elsewhere.example"].RevokedAt.Should().Be(now);
        invites["accepted@cronus.example"].RevokedAt.Should().BeNull("an accepted invite has nothing left to revoke");
        invites["colleague@cronus.example"].RevokedAt.Should().BeNull("another admin's invites are theirs to keep");
    }

    [Fact]
    public async Task Disable_refuses_to_lock_out_the_last_active_admin()
    {
        var orgId = TestDb.OtherOrgId;
        var soloAdmin = await SeedActiveAdminAsync(orgId, "lonely@example.com");
        ActAs(orgId, soloAdmin);

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).DisableUserAsync(soloAdmin, orgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LastAdmin");
    }

    [Fact]
    public async Task Two_admins_taking_each_other_out_at_once_leave_one_admin_standing()
    {
        // Count-then-save without a lock let both calls count two admins and
        // both go ahead, leaving the organisation with none (#1260). A race
        // is not certain to interleave on any one try, so run several.
        var orgId = TestDb.OtherOrgId;
        var standing = await SeedActiveAdminAsync(orgId, "admin-0@example.com");
        for (var round = 1; round <= 8; round++)
        {
            var challenger = await SeedActiveAdminAsync(orgId, $"admin-{round}@example.com");
            var demoteFirst = round % 2 == 0;
            // Both calls act as the standing admin, so one of them is on their
            // own row. When that one wins, the other is refused because its
            // actor is no longer an active admin; otherwise the last-admin
            // guard refuses it. Either way exactly one may go ahead.
            ActAs(orgId, standing);

            async Task<bool> Attempt(Func<UserAdministrationService, Task> op)
            {
                await using var ctx = _db.NewContext();
                try { await op(NewUserAdmin(ctx)); return true; }
                catch (PlanValidationException ex) when (ex.Errors.ContainsKey("LastAdmin") || ex.Errors.ContainsKey("Actor")) { return false; }
            }

            var outcomes = await Task.WhenAll(
                Attempt(s => s.ChangeRoleAsync(demoteFirst ? standing : challenger, UserRole.User, orgId)),
                Attempt(s => s.DisableUserAsync(demoteFirst ? challenger : standing, orgId)));

            outcomes.Count(ok => ok).Should().Be(1, $"exactly one of the two may go ahead (round {round})");
            await using var read = _db.NewContext();
            var admins = await read.Users.IgnoreQueryFilters()
                .Where(u => u.OrganizationId == orgId && u.Role == UserRole.Admin && u.Status == UserStatus.Active)
                .Select(u => u.Id).ToListAsync();
            admins.Should().ContainSingle($"the organisation keeps an admin (round {round})");
            standing = admins[0];
        }
    }

    [Fact]
    public async Task ChangeRole_singleton_demote_refuses_to_strip_the_last_active_admin()
    {
        var orgId = TestDb.OtherOrgId;
        var soloAdmin = await SeedActiveAdminAsync(orgId, "lonely@example.com");
        ActAs(orgId, soloAdmin);

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).ChangeRoleAsync(soloAdmin, UserRole.User, orgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LastAdmin");
    }

    [Fact]
    public async Task ChangeDisplayName_admin_corrects_a_users_name_and_trims_it()
    {
        var orgId = TestDb.OtherOrgId;
        ActAs(orgId, await SeedActiveAdminAsync(orgId, "admin@example.com"));
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
        ActAs(orgId, await SeedActiveAdminAsync(orgId, "admin@example.com"));
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
        ActAs(TestDb.DefaultOrgId, await SeedActiveAdminAsync(TestDb.DefaultOrgId, "admin@example.com"));

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).ChangeDisplayNameAsync(subjectId, "New Name", TestDb.DefaultOrgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("UserId");
    }

    [Fact]
    public async Task ChangeRole_promotes_user_to_editor_without_tripping_last_admin_guard()
    {
        var orgId = TestDb.OtherOrgId;
        ActAs(orgId, await SeedActiveAdminAsync(orgId, "primary@example.com"));
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
        ActAs(orgId, soloAdmin);

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
        ActAs(orgId, await SeedActiveAdminAsync(orgId, "two@example.com"));

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
        await SeedActiveAdminAsync(TestDb.DefaultOrgId, "default-admin@example.com");
        ActAs(TestDb.DefaultOrgId, await SeedActiveAdminAsync(TestDb.DefaultOrgId, "acting@example.com"));

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).DisableUserAsync(subjectId, TestDb.DefaultOrgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("UserId");
    }

    // ===== Acting admin re-read from the database (#1534) =====

    [Fact]
    public async Task A_demoted_admin_cannot_promote_themselves_back()
    {
        // The Users page runs in a circuit that keeps the principal it
        // connected with, so the actor's role claim can say Admin long after
        // another admin demoted them. The service reads the row instead.
        var orgId = TestDb.OtherOrgId;
        var alice = await SeedActiveAdminAsync(orgId, "alice@example.com");
        await SeedActiveAdminAsync(orgId, "bob@example.com");
        await SetRoleAndStatusAsync(alice, UserRole.User, UserStatus.Active);
        ActAs(orgId, alice);

        await using (var ctx = _db.NewContext())
        {
            Func<Task> act = () => NewUserAdmin(ctx).ChangeRoleAsync(alice, UserRole.Admin, orgId);
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Actor");
        }

        (await ReadUserAsync(alice)).Role.Should().Be(UserRole.User);
    }

    [Fact]
    public async Task A_disabled_admin_cannot_re_enable_themselves_or_change_anyone_else()
    {
        var orgId = TestDb.OtherOrgId;
        var alice = await SeedActiveAdminAsync(orgId, "alice@example.com");
        await SeedActiveAdminAsync(orgId, "bob@example.com");
        var member = await SeedActiveUserAsync(orgId, "member@example.com", UserRole.User);
        await SetRoleAndStatusAsync(alice, UserRole.Admin, UserStatus.Disabled);
        ActAs(orgId, alice);

        var attempts = new Func<UserAdministrationService, Task>[]
        {
            s => s.EnableUserAsync(alice, orgId),
            s => s.ChangeRoleAsync(member, UserRole.Admin, orgId),
            s => s.DisableUserAsync(member, orgId),
            s => s.ChangeDisplayNameAsync(member, "Renamed Member", orgId),
            s => s.RequestEmailChangeAsync(member, "member@elsewhere.example", orgId, alice),
        };
        foreach (var attempt in attempts)
        {
            await using var ctx = _db.NewContext();
            Func<Task> act = () => attempt(NewUserAdmin(ctx));
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Actor");
        }

        (await ReadUserAsync(alice)).Status.Should().Be(UserStatus.Disabled);
        var after = await ReadUserAsync(member);
        after.Role.Should().Be(UserRole.User);
        after.Status.Should().Be(UserStatus.Active);
        after.DisplayName.Should().Be("member@example.com");
        after.PendingEmail.Should().BeNull();
    }

    [Fact]
    public async Task A_demoted_admin_cannot_approve_a_signup()
    {
        var (signupId, userId, _) = await SeedPendingSignupAsync(TestDb.OtherOrgId);
        var alice = await SeedActiveAdminAsync(TestDb.OtherOrgId, "alice@example.com");
        await SetRoleAndStatusAsync(alice, UserRole.Editor, UserStatus.Active);
        ActAs(TestDb.OtherOrgId, alice);

        await using (var ctx = _db.NewContext())
        {
            Func<Task> act = () => NewUserAdmin(ctx).ApproveSignupAsync(signupId, alice, TestDb.OtherOrgId);
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Actor");
        }

        (await ReadUserAsync(userId)).Status.Should().Be(UserStatus.Pending);
    }

    [Fact]
    public async Task Admin_actions_refuse_without_a_signed_in_actor()
    {
        var orgId = TestDb.OtherOrgId;
        await SeedActiveAdminAsync(orgId, "admin@example.com");
        var member = await SeedActiveUserAsync(orgId, "member@example.com", UserRole.User);
        _db.OrgContext.CurrentOrganizationId = orgId;
        _db.OrgContext.CurrentUserId = null;

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).ChangeRoleAsync(member, UserRole.Admin, orgId);
        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Actor");
    }

    // ===== ChangePasswordAsync =====

    [Fact]
    public async Task Change_password_rejects_wrong_current_password()
    {
        var userId = await SeedActiveUserWithPasswordAsync(TestDb.DefaultOrgId, "p@example.com", "correctpasswordlong");

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewService(ctx).ChangePasswordAsync(userId, "wrong-current", "newpasswordlong12345");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("CurrentPassword");
    }

    [Fact]
    public async Task Wrong_current_passwords_count_towards_the_sign_in_lockout()
    {
        // A session without the password could otherwise guess it here with no
        // limit (#1614). Each wrong guess is a failed attempt for the email,
        // and once the email is locked out even the right one is refused.
        var userId = await SeedActiveUserWithPasswordAsync(TestDb.DefaultOrgId, "guess@example.com", "correctpasswordlong");
        for (var i = 0; i < AuthService.LockoutThreshold; i++)
        {
            await using var ctx = _db.NewContext();
            Func<Task> wrong = () => NewService(ctx).ChangePasswordAsync(userId, $"guess-{i}", "newpasswordlong12345");
            (await wrong.Should().ThrowAsync<PlanValidationException>())
                .Which.Errors["CurrentPassword"].Should().Be("Current password is incorrect.");
        }

        await using (var ctx = _db.NewContext())
        {
            Func<Task> right = () => NewService(ctx).ChangePasswordAsync(userId, "correctpasswordlong", "newpasswordlong12345");
            (await right.Should().ThrowAsync<PlanValidationException>())
                .Which.Errors["CurrentPassword"].Should().Be(AuthService.CurrentPasswordLockedOutMessage);
        }

        await using var read = _db.NewContext();
        (await read.LoginAttempts.CountAsync(a => a.Email == "guess@example.com" && !a.Succeeded))
            .Should().Be(AuthService.LockoutThreshold);
        var user = await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
        NewAuth(read).VerifyPassword("correctpasswordlong", user.PasswordHash).Should().BeTrue("nothing was changed");
    }

    [Fact]
    public async Task A_wrong_current_password_is_recorded_with_the_callers_address()
    {
        var userId = await SeedActiveUserWithPasswordAsync(TestDb.DefaultOrgId, "guess-ip@example.com", "correctpasswordlong");

        await using (var ctx = _db.NewContext())
        {
            Func<Task> wrong = () => NewService(ctx).ChangePasswordAsync(userId, "guess", "newpasswordlong12345", "203.0.113.7");
            await wrong.Should().ThrowAsync<PlanValidationException>();
        }

        await using var read = _db.NewContext();
        (await read.LoginAttempts.SingleAsync(a => a.Email == "guess-ip@example.com")).Ip.Should().Be("203.0.113.7");
    }

    [Fact]
    public async Task Change_password_rejects_new_password_that_fails_policy()
    {
        var userId = await SeedActiveUserWithPasswordAsync(TestDb.DefaultOrgId, "p@example.com", "correctpasswordlong");

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewService(ctx).ChangePasswordAsync(userId, "correctpasswordlong", "short");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("NewPassword");
    }

    [Fact]
    public async Task Change_password_happy_path_re_hashes_so_old_password_no_longer_verifies()
    {
        var userId = await SeedActiveUserWithPasswordAsync(TestDb.DefaultOrgId, "p@example.com", "correctpasswordlong");

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
        var userId = await SeedActiveUserAsync(TestDb.DefaultOrgId, "name@example.com", UserRole.User);

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

    [Fact]
    public async Task Self_service_changes_cannot_reach_a_user_in_another_organisation()
    {
        // Both run inside the tenant filter now (#1336): the signed-in user's
        // own row is in their own organisation, so a foreign id finds nothing.
        var strangerId = await SeedActiveUserWithPasswordAsync(TestDb.OtherOrgId, "stranger@example.com", "correctpasswordlong");

        await using (var ctx = _db.NewContext())
        {
            Func<Task> rename = () => NewService(ctx).ChangeDisplayNameAsync(strangerId, "Renamed");
            await rename.Should().ThrowAsync<InvalidOperationException>();
            Func<Task> repassword = () => NewService(ctx).ChangePasswordAsync(strangerId, "correctpasswordlong", "newcorrectpasswordlong");
            await repassword.Should().ThrowAsync<InvalidOperationException>();
        }

        await using var read = _db.NewContext();
        var stranger = await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == strangerId);
        stranger.DisplayName.Should().NotBe("Renamed");
        NewAuth(read).VerifyPassword("correctpasswordlong", stranger.PasswordHash).Should().BeTrue();
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
            NullLogger<AccountService>.Instance, _clock, _db.OpenIddictTokens);

    /// <summary>
    /// UserAdministrationService — admin actions on existing users. Used for
    /// ApproveSignup / RejectSignup / Disable / Enable / ChangeRole.
    /// </summary>
    private UserAdministrationService NewUserAdmin(Data.AppDbContext ctx) =>
        _db.NewUserAdministrationService(ctx, _clock);

    private async Task<int> SeedActiveAdminAsync(int orgId, string email) =>
        await SeedActiveUserAsync(orgId, email, UserRole.Admin);

    /// <summary>
    /// Signs <paramref name="userId"/> in as the acting admin of
    /// <paramref name="orgId"/>, the way the admin endpoints and the Users
    /// page call the service: the acting org is the request's own.
    /// </summary>
    private void ActAs(int orgId, int userId)
    {
        _db.OrgContext.CurrentOrganizationId = orgId;
        _db.OrgContext.CurrentUserId = userId;
        _db.OrgContext.IsSiteAdmin = false;
    }

    private async Task SetRoleAndStatusAsync(int userId, UserRole role, UserStatus status)
    {
        await using var ctx = _db.NewContext();
        await ctx.Users.IgnoreQueryFilters().Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, role).SetProperty(u => u.Status, status));
    }

    private async Task<User> ReadUserAsync(int userId)
    {
        await using var read = _db.NewContext();
        return await read.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == userId);
    }

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
