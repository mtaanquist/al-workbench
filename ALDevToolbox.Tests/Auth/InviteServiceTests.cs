using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Account;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Behavioural tests for <see cref="InviteService"/> (P4.19). Cover the
/// single-use contract, 7-day expiry, org-scoped revocation, and the
/// happy-path acceptance flow that activates a brand-new user.
/// </summary>
public sealed class InviteServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 11, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private async Task<User> SeedAdminAsync(int orgId, int userId = 500, string email = "admin@example.com")
    {
        await using var ctx = _db.NewContext();
        var admin = new User
        {
            Id = userId,
            OrganizationId = orgId,
            Email = email,
            PasswordHash = "x",
            DisplayName = "Admin",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.Users.Add(admin);
        await ctx.SaveChangesAsync();
        return admin;
    }

    private InviteService NewService(ALDevToolbox.Data.AppDbContext ctx, int actingUserId, int orgId)
    {
        var orgContext = new AmbientOrganizationContext
        {
            CurrentOrganizationId = orgId,
            CurrentUserId = actingUserId,
        };
        return new InviteService(ctx, orgContext, new AuthService(ctx, NullLogger<AuthService>.Instance, _clock), _clock, NullLogger<InviteService>.Instance);
    }

    [Fact]
    public async Task Invite_creates_a_pending_row_and_returns_a_raw_token_that_is_not_persisted()
    {
        var admin = await SeedAdminAsync(TestDb.DefaultOrgId);
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx, admin.Id, TestDb.DefaultOrgId);

        var (token, inviteId) = await svc.CreateAsync("invitee@example.com", UserRole.User, "Welcome!");

        token.Should().NotBeNullOrWhiteSpace();
        token.Length.Should().Be(64, "32-byte hex token");

        await using var read = _db.NewContext();
        var row = await read.Invites.IgnoreQueryFilters().SingleAsync(i => i.Id == inviteId);
        row.Email.Should().Be("invitee@example.com");
        row.Role.Should().Be(UserRole.User);
        row.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        row.InvitedByUserId.Should().Be(admin.Id);
        row.AcceptedAt.Should().BeNull();
        row.RevokedAt.Should().BeNull();
        row.TokenHash.Should().NotBe(token, "raw token must not be stored");
        row.ExpiresAt.Should().Be(_clock.GetUtcNow().UtcDateTime + InviteService.InviteLifetime);
    }

    [Fact]
    public async Task Invite_rejects_email_already_in_organisation()
    {
        await SeedAdminAsync(TestDb.DefaultOrgId, userId: 501, email: "alice@example.com");
        var admin = await SeedAdminAsync(TestDb.DefaultOrgId, userId: 502, email: "admin@example.com");
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx, admin.Id, TestDb.DefaultOrgId);

        Func<Task> act = () => svc.CreateAsync("alice@example.com", UserRole.User, null);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Email");
    }

    [Fact]
    public async Task Invite_normalises_email_case_and_trims_whitespace()
    {
        var admin = await SeedAdminAsync(TestDb.DefaultOrgId);
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx, admin.Id, TestDb.DefaultOrgId);

        var (_, inviteId) = await svc.CreateAsync("  Mixed.Case@Example.COM  ", UserRole.Admin, null);
        await using var read = _db.NewContext();
        var row = await read.Invites.IgnoreQueryFilters().SingleAsync(i => i.Id == inviteId);
        row.Email.Should().Be("mixed.case@example.com");
    }

    [Fact]
    public async Task Accept_activates_a_new_user_with_the_invite_role()
    {
        var admin = await SeedAdminAsync(TestDb.DefaultOrgId);
        string token;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx, admin.Id, TestDb.DefaultOrgId);
            (token, _) = await svc.CreateAsync("newadmin@example.com", UserRole.Admin, null);
        }

        await using (var ctx = _db.NewContext())
        {
            // Accept runs anonymously — no org context.
            var orgContext = new AmbientOrganizationContext();
            var svc = new InviteService(ctx, orgContext, new AuthService(ctx, NullLogger<AuthService>.Instance, _clock), _clock, NullLogger<InviteService>.Instance);
            var user = await svc.AcceptAsync(token, "New Admin", "verylongpassword1!");
            user.Email.Should().Be("newadmin@example.com");
            user.Role.Should().Be(UserRole.Admin);
            user.Status.Should().Be(UserStatus.Active);
            user.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        }

        await using var read = _db.NewContext();
        var row = await read.Invites.IgnoreQueryFilters().FirstAsync(i => i.Email == "newadmin@example.com");
        row.AcceptedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Accept_activates_a_new_user_with_the_editor_role()
    {
        var admin = await SeedAdminAsync(TestDb.DefaultOrgId);
        string token;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx, admin.Id, TestDb.DefaultOrgId);
            (token, _) = await svc.CreateAsync("editor@example.com", UserRole.Editor, null);
        }

        await using (var ctx = _db.NewContext())
        {
            var svc = new InviteService(ctx, new AmbientOrganizationContext(), new AuthService(ctx, NullLogger<AuthService>.Instance, _clock), _clock, NullLogger<InviteService>.Instance);
            var user = await svc.AcceptAsync(token, "New Editor", "verylongpassword1!");
            user.Email.Should().Be("editor@example.com");
            user.Role.Should().Be(UserRole.Editor);
            user.Status.Should().Be(UserStatus.Active);
            user.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        }
    }

    [Fact]
    public async Task Accept_with_a_password_is_refused_in_a_microsoft_only_organisation()
    {
        // Every later password sign-in, reset and magic link is refused there,
        // so the password could never be used again (#1336).
        var admin = await SeedAdminAsync(TestDb.DefaultOrgId);
        string token;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx, admin.Id, TestDb.DefaultOrgId);
            (token, _) = await svc.CreateAsync("entra.only@example.com", UserRole.User, null);
            var settings = await ctx.OrganizationSettings.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
            if (settings is null)
            {
                settings = new OrganizationSettings { OrganizationId = TestDb.DefaultOrgId };
                ctx.OrganizationSettings.Add(settings);
            }
            settings.EntraEnabled = true;
            settings.LocalLoginPolicy = LocalLoginPolicy.EntraOnly;
            settings.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            var svc = new InviteService(ctx, new AmbientOrganizationContext(), new AuthService(ctx, NullLogger<AuthService>.Instance, _clock), _clock, NullLogger<InviteService>.Instance);
            Func<Task> act = () => svc.AcceptAsync(token, "Entra Person", "verylongpassword1!");
            var ex = await act.Should().ThrowAsync<PlanValidationException>();
            ex.Which.Errors.Should().ContainKey("Password");
        }

        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == "entra.only@example.com")).Should().BeFalse();
        (await read.Invites.IgnoreQueryFilters().SingleAsync(i => i.Email == "entra.only@example.com")).AcceptedAt
            .Should().BeNull("the invite stays usable for accepting with Microsoft");
    }

    [Fact]
    public async Task Accepting_with_microsoft_is_refused_where_a_password_works()
    {
        var admin = await SeedAdminAsync(TestDb.DefaultOrgId);
        string token;
        await using (var ctx = _db.NewContext())
        {
            (token, _) = await NewService(ctx, admin.Id, TestDb.DefaultOrgId).CreateAsync("password.org@example.com", UserRole.User, null);
        }

        await using var ctx2 = _db.NewContext();
        var svc = new InviteService(ctx2, new AmbientOrganizationContext(), new AuthService(ctx2, NullLogger<AuthService>.Instance, _clock), _clock, NullLogger<InviteService>.Instance);
        Func<Task> act = () => svc.PrepareMicrosoftAcceptanceAsync(token, "Password Person");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Password");
    }

    [Fact]
    public async Task Accepting_with_microsoft_is_allowed_in_a_microsoft_only_organisation()
    {
        var admin = await SeedAdminAsync(TestDb.DefaultOrgId);
        string token;
        await using (var ctx = _db.NewContext())
        {
            (token, _) = await NewService(ctx, admin.Id, TestDb.DefaultOrgId).CreateAsync("ms.org@example.com", UserRole.User, null);
        }
        await MakeMicrosoftOnlyAsync(TestDb.DefaultOrgId);

        await using var ctx2 = _db.NewContext();
        var svc = new InviteService(ctx2, new AmbientOrganizationContext(), new AuthService(ctx2, NullLogger<AuthService>.Instance, _clock), _clock, NullLogger<InviteService>.Instance);
        var invite = await svc.PrepareMicrosoftAcceptanceAsync(token, "MS Person");
        invite.Email.Should().Be("ms.org@example.com");
    }

    private async Task MakeMicrosoftOnlyAsync(int orgId)
    {
        await using var ctx = _db.NewContext();
        var settings = await ctx.OrganizationSettings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.OrganizationId == orgId);
        if (settings is null)
        {
            settings = new OrganizationSettings { OrganizationId = orgId };
            ctx.OrganizationSettings.Add(settings);
        }
        settings.EntraEnabled = true;
        settings.LocalLoginPolicy = LocalLoginPolicy.EntraOnly;
        settings.UpdatedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Accept_rejects_a_second_use_of_the_same_token()
    {
        var admin = await SeedAdminAsync(TestDb.DefaultOrgId);
        string token;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx, admin.Id, TestDb.DefaultOrgId);
            (token, _) = await svc.CreateAsync("once@example.com", UserRole.User, null);
        }

        await using (var ctx = _db.NewContext())
        {
            var svc = new InviteService(ctx, new AmbientOrganizationContext(), new AuthService(ctx, NullLogger<AuthService>.Instance, _clock), _clock, NullLogger<InviteService>.Instance);
            await svc.AcceptAsync(token, "Once", "verylongpassword1!");
        }

        await using (var ctx = _db.NewContext())
        {
            var svc = new InviteService(ctx, new AmbientOrganizationContext(), new AuthService(ctx, NullLogger<AuthService>.Instance, _clock), _clock, NullLogger<InviteService>.Instance);
            Func<Task> reuse = () => svc.AcceptAsync(token, "Twice", "verylongpassword1!");
            var ex = await reuse.Should().ThrowAsync<PlanValidationException>();
            ex.Which.Errors.Should().ContainKey("Token");
        }
    }

    [Fact]
    public async Task Accept_rejects_an_expired_token()
    {
        var admin = await SeedAdminAsync(TestDb.DefaultOrgId);
        string token;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx, admin.Id, TestDb.DefaultOrgId);
            (token, _) = await svc.CreateAsync("late@example.com", UserRole.User, null);
        }

        // Advance just past the 7-day window.
        _clock.Advance(InviteService.InviteLifetime + TimeSpan.FromSeconds(1));

        await using var ctx2 = _db.NewContext();
        var svc2 = new InviteService(ctx2, new AmbientOrganizationContext(), new AuthService(ctx2, NullLogger<AuthService>.Instance, _clock), _clock, NullLogger<InviteService>.Instance);
        Func<Task> act = () => svc2.AcceptAsync(token, "Late", "verylongpassword1!");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Token");
    }

    [Fact]
    public async Task Revoke_marks_the_invite_so_it_cannot_be_accepted()
    {
        var admin = await SeedAdminAsync(TestDb.DefaultOrgId);
        string token;
        int inviteId;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx, admin.Id, TestDb.DefaultOrgId);
            (token, inviteId) = await svc.CreateAsync("nope@example.com", UserRole.User, null);
        }

        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx, admin.Id, TestDb.DefaultOrgId);
            await svc.RevokeAsync(inviteId);
        }

        await using var ctx3 = _db.NewContext();
        var svc3 = new InviteService(ctx3, new AmbientOrganizationContext(), new AuthService(ctx3, NullLogger<AuthService>.Instance, _clock), _clock, NullLogger<InviteService>.Instance);
        Func<Task> act = () => svc3.AcceptAsync(token, "Nope", "verylongpassword1!");
        await act.Should().ThrowAsync<PlanValidationException>();
    }

    [Fact]
    public async Task Revoke_refuses_invites_belonging_to_a_different_organisation()
    {
        // Admin in OrgA creates an invite; an admin in OrgB tries to revoke it.
        var adminA = await SeedAdminAsync(TestDb.DefaultOrgId, userId: 600, email: "a@example.com");
        var adminB = await SeedAdminAsync(TestDb.OtherOrgId, userId: 601, email: "b@example.com");

        int inviteId;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx, adminA.Id, TestDb.DefaultOrgId);
            (_, inviteId) = await svc.CreateAsync("victim@example.com", UserRole.User, null);
        }

        await using var ctx2 = _db.NewContext();
        var svcB = NewService(ctx2, adminB.Id, TestDb.OtherOrgId);
        Func<Task> act = () => svcB.RevokeAsync(inviteId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("InviteId");
    }

    [Fact]
    public async Task Token_storage_is_hashed_so_a_DB_read_does_not_yield_usable_tokens()
    {
        var admin = await SeedAdminAsync(TestDb.DefaultOrgId);
        string token;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx, admin.Id, TestDb.DefaultOrgId);
            (token, _) = await svc.CreateAsync("hashed@example.com", UserRole.User, null);
        }

        await using var read = _db.NewContext();
        var rows = await read.Invites.IgnoreQueryFilters().ToListAsync();
        rows.Should().NotBeEmpty();
        rows.Should().NotContain(r => r.TokenHash == token, "the raw token must never appear in the DB");
        rows.Should().AllSatisfy(r => r.TokenHash.Length.Should().Be(64, "hex SHA-256 is 64 characters"));
    }

    private async Task<string> InviteAsync(User admin, string email)
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx, admin.Id, admin.OrganizationId);
        var (token, _) = await svc.CreateAsync(email, UserRole.User, null);
        return token;
    }

    private async Task<Func<Task<User>>> AcceptLaterAsync(string token)
    {
        var ctx = _db.NewContext();
        var svc = new InviteService(ctx, new AmbientOrganizationContext(), new AuthService(ctx, NullLogger<AuthService>.Instance, _clock), _clock, NullLogger<InviteService>.Instance);
        return () => svc.AcceptAsync(token, "Taken Person", "verylongpassword1!");
    }

    [Fact]
    public async Task Invite_for_an_email_held_in_another_organisation_is_issued_but_cannot_be_accepted()
    {
        // Issue #1207: the admin learns nothing about other organisations when
        // inviting; only the invitee, at acceptance, learns the address is taken.
        await SeedAdminAsync(TestDb.OtherOrgId, userId: 700, email: "shared@example.com");
        var admin = await SeedAdminAsync(TestDb.DefaultOrgId, userId: 701, email: "admin@example.com");

        var token = await InviteAsync(admin, "shared@example.com");

        var act = await AcceptLaterAsync(token);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Email");

        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().CountAsync(u => u.Email == "shared@example.com")).Should().Be(1);
    }

    [Fact]
    public async Task Accept_refuses_rather_than_returning_an_account_created_after_the_invite()
    {
        // Accepting used to hand back the existing account, which the endpoint then
        // signed in without that account's password.
        var admin = await SeedAdminAsync(TestDb.DefaultOrgId, userId: 710, email: "admin@example.com");
        var token = await InviteAsync(admin, "late@example.com");
        await SeedAdminAsync(TestDb.DefaultOrgId, userId: 711, email: "late@example.com");

        var act = await AcceptLaterAsync(token);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Email");
    }
}
