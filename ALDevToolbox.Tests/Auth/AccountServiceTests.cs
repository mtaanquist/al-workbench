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
/// Behavioural tests for the (post-#88) account surface: BCrypt round-trip
/// and login lockout/rate-limit on <see cref="AuthService"/>,
/// signup + last-active-admin on <see cref="AccountService"/> /
/// <see cref="UserAdministrationService"/>, and reset-token single-use on
/// <see cref="PasswordResetService"/>.
/// </summary>
public sealed class AccountServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private AuthService NewAuth(Data.AppDbContext ctx) =>
        new(ctx, NullLogger<AuthService>.Instance, _clock);

    private SystemSettingsService NewSettings(Data.AppDbContext ctx) =>
        new(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, _clock);

    private AccountService NewAccounts(Data.AppDbContext ctx, bool singleTenant = false) =>
        new(ctx, NewAuth(ctx), NewSettings(ctx),
            new ALDevToolbox.Services.SingleTenant.SingleTenantModeState(singleTenant),
            NullLogger<AccountService>.Instance, _clock);

    private UserAdministrationService NewUserAdmin(Data.AppDbContext ctx) =>
        _db.NewUserAdministrationService(ctx, _clock);

    private PasswordResetService NewPasswordReset(Data.AppDbContext ctx) =>
        new(ctx, NewAuth(ctx), _clock);

    [Fact]
    public void BCrypt_round_trip_verifies_only_the_original_password()
    {
        using var ctx = _db.NewContext();
        var auth = NewAuth(ctx);
        var hash = auth.HashPassword("correct-horse-battery");
        auth.VerifyPassword("correct-horse-battery", hash).Should().BeTrue();
        auth.VerifyPassword("wrong-horse-battery", hash).Should().BeFalse();
    }

    [Fact]
    public async Task Signup_with_existing_slug_attaches_user_as_pending()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Organizations.Add(new Organization
            {
                Id = 99, Name = "Acme", Slug = "acme",
                IsPending = false, CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var svc = NewAccounts(ctx);
        var (outcome, user, org) = await svc.SignupAsync(
            "alice@example.com", "Alice", "verylongpassword12345", "acme", null);

        outcome.Should().Be(SignupOutcome.PendingApproval);
        user.Should().NotBeNull();
        user!.Status.Should().Be(UserStatus.Pending);
        user.Role.Should().Be(UserRole.User);
        org!.Slug.Should().Be("acme");
    }

    [Fact]
    public async Task Single_tenant_mode_blocks_new_org_creation_at_signup()
    {
        var ctx = _db.NewContext();
        var svc = NewAccounts(ctx, singleTenant: true);

        // No claimed domain and no existing slug would normally provision a
        // brand-new org — single-tenant mode refuses that path.
        var act = () => svc.SignupAsync(
            "bob@example.com", "Bob", "verylongpassword12345", null, "Bob's Org");

        await act.Should().ThrowAsync<PlanValidationException>();

        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == "bob@example.com"))
            .Should().BeFalse("the refused signup must not create a user");
    }

    [Fact]
    public async Task Single_tenant_mode_still_lets_users_join_an_existing_org()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Organizations.Add(new Organization
            {
                Id = 99, Name = "Acme", Slug = "acme",
                IsPending = false, CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var svc = NewAccounts(ctx, singleTenant: true);
        var (outcome, user, org) = await svc.SignupAsync(
            "alice@example.com", "Alice", "verylongpassword12345", "acme", null);

        outcome.Should().Be(SignupOutcome.PendingApproval);
        user!.Status.Should().Be(UserStatus.Pending);
        org!.Slug.Should().Be("acme");
    }

    [Fact]
    public async Task Signup_with_blank_slug_auto_approves_as_new_org_admin()
    {
        var ctx = _db.NewContext();
        var svc = NewAccounts(ctx);
        var (outcome, user, org) = await svc.SignupAsync(
            "bob@example.com", "Bob", "verylongpassword12345", null, "Bob's Org");

        outcome.Should().Be(SignupOutcome.OrganizationProvisioned);
        // Brand-new orgs auto-approve their first user (we have no superuser
        // to do otherwise — see .design/auth-and-audit.md).
        org!.IsPending.Should().BeFalse();
        org.IsSystem.Should().BeFalse("new orgs are regular orgs; the system org is the singleton Default");
        user!.Role.Should().Be(UserRole.Admin, "the first user in a brand-new org runs it");
        user.Status.Should().Be(UserStatus.Active);

        // The audit trail still records the signup; auto-approval points at the user themselves.
        await using var read = _db.NewContext();
        var request = await read.SignupRequests.IgnoreQueryFilters().SingleAsync(r => r.UserId == user.Id);
        request.Decision.Should().Be(SignupDecision.Approved);
        request.DecidedByUserId.Should().Be(user.Id);
        request.DecidedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Signup_rejects_short_passwords()
    {
        var ctx = _db.NewContext();
        var svc = NewAccounts(ctx);
        Func<Task> act = () => svc.SignupAsync("c@example.com", "Carol", "short", null, "Carol's Org");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Password");
    }

    [Fact]
    public async Task Signup_blocked_when_email_domain_not_in_allowlist()
    {
        await SetAllowlistAsync("allowed.com");

        var ctx = _db.NewContext();
        var svc = NewAccounts(ctx);
        Func<Task> act = () => svc.SignupAsync(
            "intruder@blocked.com", "Mallory", "verylongpassword12345",
            organizationSlug: null, organizationName: "Mallory Inc");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Email");
    }

    [Fact]
    public async Task Signup_allowed_when_email_domain_matches_allowlist_exactly()
    {
        await SetAllowlistAsync("allowed.com");

        var ctx = _db.NewContext();
        var svc = NewAccounts(ctx);
        var (outcome, user, org) = await svc.SignupAsync(
            "alice@allowed.com", "Alice", "verylongpassword12345",
            organizationSlug: null, organizationName: "Alice Org");
        outcome.Should().Be(SignupOutcome.OrganizationProvisioned);
        user.Should().NotBeNull();
        org.Should().NotBeNull();
    }

    [Fact]
    public async Task Signup_allowlist_does_not_grant_subdomains()
    {
        await SetAllowlistAsync("allowed.com");

        var ctx = _db.NewContext();
        var svc = NewAccounts(ctx);
        Func<Task> act = () => svc.SignupAsync(
            "user@sub.allowed.com", "User", "verylongpassword12345",
            organizationSlug: null, organizationName: "Sub Org");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Email");
    }

    [Fact]
    public async Task Signup_open_when_allowlist_empty()
    {
        // No allowlist row touched — feature off, any domain accepted.
        var ctx = _db.NewContext();
        var svc = NewAccounts(ctx);
        var (outcome, _, _) = await svc.SignupAsync(
            "anyone@anywhere.example", "Anyone", "verylongpassword12345",
            organizationSlug: null, organizationName: "Anyone Org");
        outcome.Should().Be(SignupOutcome.OrganizationProvisioned);
    }

    [Fact]
    public async Task HasStrongAuth_true_when_totp_enabled()
    {
        var userId = await SeedUserAsync(totp: true, emailMfa: false, passkey: false);
        await using var ctx = _db.NewContext();
        (await NewAuth(ctx).HasStrongAuthAsync(userId)).Should().BeTrue();
    }

    [Fact]
    public async Task HasStrongAuth_true_when_email_mfa_enabled()
    {
        var userId = await SeedUserAsync(totp: false, emailMfa: true, passkey: false);
        await using var ctx = _db.NewContext();
        (await NewAuth(ctx).HasStrongAuthAsync(userId)).Should().BeTrue();
    }

    [Fact]
    public async Task HasStrongAuth_true_when_passkey_registered()
    {
        var userId = await SeedUserAsync(totp: false, emailMfa: false, passkey: true);
        await using var ctx = _db.NewContext();
        (await NewAuth(ctx).HasStrongAuthAsync(userId)).Should().BeTrue();
    }

    [Fact]
    public async Task HasStrongAuth_false_when_none_enrolled()
    {
        var userId = await SeedUserAsync(totp: false, emailMfa: false, passkey: false);
        await using var ctx = _db.NewContext();
        (await NewAuth(ctx).HasStrongAuthAsync(userId)).Should().BeFalse();
    }

    private async Task<int> SeedUserAsync(bool totp, bool emailMfa, bool passkey)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"strongauth-{Guid.NewGuid():N}@example.com",
            DisplayName = "StrongAuth Tester",
            PasswordHash = "ignored",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = now,
            TotpEnabled = totp,
            EmailMfaEnabled = emailMfa,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        if (passkey)
        {
            ctx.UserPasskeys.Add(new UserPasskey
            {
                UserId = user.Id,
                CredentialId = Guid.NewGuid().ToByteArray(),
                PublicKey = new byte[] { 1, 2, 3 },
                SignCounter = 0,
                Transports = "internal",
                CreatedAt = now,
            });
            await ctx.SaveChangesAsync();
        }
        return user.Id;
    }

    private async Task SetAllowlistAsync(params string[] domains)
    {
        await using var ctx = _db.NewContext();
        var row = await ctx.SystemSettings.FirstOrDefaultAsync(s => s.Id == 1);
        if (row is null)
        {
            row = new Domain.Entities.SystemSettings { Id = 1, UpdatedAt = _clock.GetUtcNow().UtcDateTime };
            ctx.SystemSettings.Add(row);
        }
        row.SignupEmailDomainAllowlist = string.Join('\n', domains);
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Signup_new_org_uses_provided_organisation_name_not_display_name()
    {
        var ctx = _db.NewContext();
        var svc = NewAccounts(ctx);
        var (outcome, _, org) = await svc.SignupAsync(
            "dani@example.com", "Dani — Engineering", "verylongpassword12345",
            organizationSlug: null, organizationName: "Acme Holdings");

        outcome.Should().Be(SignupOutcome.OrganizationProvisioned);
        org!.Name.Should().Be("Acme Holdings",
            "the org name should come from the new OrganizationName field, not from the user's display name");
        org.Slug.Should().StartWith("acme",
            "the slug should derive from the org name (Slugify), not the display name");
    }

    [Fact]
    public async Task Signup_new_org_requires_organisation_name_when_slug_blank()
    {
        var ctx = _db.NewContext();
        var svc = NewAccounts(ctx);
        Func<Task> act = () => svc.SignupAsync(
            "ed@example.com", "Ed", "verylongpassword12345",
            organizationSlug: null, organizationName: null);

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("OrganizationName");
    }

    [Fact]
    public async Task Signup_routes_via_claimed_email_domain_even_when_slug_is_typed()
    {
        // The Default org has claimed acme.com.
        await using (var seed = _db.NewContext())
        {
            seed.OrganizationEmailDomains.Add(new OrganizationEmailDomain
            {
                OrganizationId = TestDb.DefaultOrgId,
                Domain = "acme.com",
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            // And a Different org exists that the user would otherwise have
            // attached themselves to via slug.
            seed.Organizations.Add(new Organization
            {
                Id = 555, Name = "Different", Slug = "different",
                IsPending = false, CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var svc = NewAccounts(ctx);
        var (outcome, user, org) = await svc.SignupAsync(
            "fran@acme.com", "Fran", "verylongpassword12345",
            organizationSlug: "different", organizationName: "Made Up");

        outcome.Should().Be(SignupOutcome.PendingApproval,
            "domain-routed signups land as Pending — admins of the claiming org still approve via /admin/users");
        org!.Id.Should().Be(TestDb.DefaultOrgId,
            "the email-domain match should win over the user's typed slug");
        user!.Status.Should().Be(UserStatus.Pending);
        user.Role.Should().Be(UserRole.User);
    }

    [Fact]
    public async Task Last_active_admin_cannot_demote_themselves()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = 100,
                OrganizationId = orgId,
                Email = "admin@example.com",
                PasswordHash = "x",
                DisplayName = "Admin",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var users = NewUserAdmin(ctx);
        Func<Task> demote = () => users.ChangeRoleAsync(100, UserRole.User, orgId);
        var ex = await demote.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LastAdmin");
    }

    [Fact]
    public async Task Five_consecutive_failures_lock_the_account_for_15_minutes()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            var hash = NewAuth(seed).HashPassword("rightpasswordlong");
            seed.Users.Add(new User
            {
                OrganizationId = orgId,
                Email = "lockme@example.com",
                PasswordHash = hash,
                DisplayName = "Lock Me",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var auth = NewAuth(ctx);
        for (var i = 0; i < AuthService.LockoutThreshold; i++)
        {
            var (outcome, _) = await auth.TryLoginAsync("lockme@example.com", "wrongpassword12345", "1.2.3.4");
            outcome.Should().Be(LoginOutcome.InvalidCredentials);
        }

        // Sixth attempt — now the right password — gets locked out.
        var (next, _) = await auth.TryLoginAsync("lockme@example.com", "rightpasswordlong", "1.2.3.4");
        next.Should().Be(LoginOutcome.LockedOut);

        // Advance the clock past the lockout window.
        _clock.Advance(AuthService.LockoutWindow + TimeSpan.FromSeconds(1));
        var (later, user) = await auth.TryLoginAsync("lockme@example.com", "rightpasswordlong", "1.2.3.4");
        later.Should().Be(LoginOutcome.Success);
        user.Should().NotBeNull();
    }

    [Fact]
    public async Task Fourth_failure_still_allows_a_correct_login_on_the_fifth_attempt()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            var hash = NewAuth(seed).HashPassword("rightpasswordlong");
            seed.Users.Add(new User
            {
                OrganizationId = orgId,
                Email = "boundary@example.com",
                PasswordHash = hash,
                DisplayName = "Boundary",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var auth = NewAuth(ctx);

        for (var i = 0; i < AuthService.LockoutThreshold - 1; i++)
        {
            var (failed, _) = await auth.TryLoginAsync("boundary@example.com", "wrongpassword12345", "1.2.3.4");
            failed.Should().Be(LoginOutcome.InvalidCredentials);
        }

        // N-1 failures should still allow the correct password through.
        var (outcome, user) = await auth.TryLoginAsync("boundary@example.com", "rightpasswordlong", "1.2.3.4");
        outcome.Should().Be(LoginOutcome.Success);
        user.Should().NotBeNull();
    }

    [Fact]
    public async Task Wrong_password_attempts_past_the_threshold_also_lock_out()
    {
        // Regression: previously the lockout check only ran on the success
        // path, so an attacker spamming wrong passwords could keep failing
        // past the threshold without ever being told they were locked.
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            var hash = NewAuth(seed).HashPassword("rightpasswordlong");
            seed.Users.Add(new User
            {
                OrganizationId = orgId,
                Email = "spammed@example.com",
                PasswordHash = hash,
                DisplayName = "Spammed",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var auth = NewAuth(ctx);
        for (var i = 0; i < AuthService.LockoutThreshold; i++)
        {
            await auth.TryLoginAsync("spammed@example.com", "wrongpassword12345", "1.2.3.4");
        }

        var (next, _) = await auth.TryLoginAsync("spammed@example.com", "stillwrongpassword", "1.2.3.4");
        next.Should().Be(LoginOutcome.LockedOut);
    }

    [Fact]
    public async Task Per_email_rate_limit_blocks_more_than_ten_attempts_in_fifteen_minutes()
    {
        var ctx = _db.NewContext();
        var auth = NewAuth(ctx);

        for (var i = 0; i < AuthService.MaxAttemptsPerEmail; i++)
        {
            await auth.TryLoginAsync("noone@example.com", "wrong", "1.2.3.4");
        }

        var (outcome, _) = await auth.TryLoginAsync("noone@example.com", "wrong", "1.2.3.4");
        outcome.Should().Be(LoginOutcome.RateLimited);
    }

    [Fact]
    public async Task Reset_token_is_single_use_and_expires_after_one_hour()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = 200,
                OrganizationId = orgId,
                Email = "reset@example.com",
                PasswordHash = "old",
                DisplayName = "Reset",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var svc = NewPasswordReset(ctx);
        var token = await svc.CreatePasswordResetTokenAsync("reset@example.com", "10.0.0.1");
        token.Should().NotBeNull();

        await svc.ConsumePasswordResetTokenAsync(token!, "verylongnewpassword!");

        // Second use of the same token must fail.
        Func<Task> reuse = () => svc.ConsumePasswordResetTokenAsync(token!, "verylongnewpassword!");
        await reuse.Should().ThrowAsync<PlanValidationException>();
    }

    [Fact]
    public async Task Reset_requests_are_throttled_on_the_same_window_as_sign_in()
    {
        // Without a limit this is an unauthenticated way to make the site send
        // mail to an address of the caller's choosing, as often as they like -
        // and since #790 every request also writes a durable outbox row.
        await SeedResettableUserAsync("throttle@example.com");
        var ctx = _db.NewContext();
        var svc = NewPasswordReset(ctx);

        for (var i = 0; i < AuthService.MaxAttemptsPerEmail; i++)
        {
            (await svc.CreatePasswordResetTokenAsync("throttle@example.com", "10.0.0.1"))
                .Should().NotBeNull("request {0} is inside the window", i + 1);
        }

        (await svc.CreatePasswordResetTokenAsync("throttle@example.com", "10.0.0.1"))
            .Should().BeNull("the per-email window is spent");

        _clock.Advance(AuthService.RateWindow + TimeSpan.FromSeconds(1));
        (await svc.CreatePasswordResetTokenAsync("throttle@example.com", "10.0.0.1"))
            .Should().NotBeNull("the window has passed");
    }

    [Fact]
    public async Task An_unknown_address_still_costs_the_caller_an_attempt()
    {
        // Otherwise cycling addresses is free and the per-IP window never trips.
        var ctx = _db.NewContext();
        var svc = NewPasswordReset(ctx);

        await svc.CreatePasswordResetTokenAsync("ghost@example.com", "10.0.0.9");

        (await ctx.LoginAttempts.CountAsync(a => a.Ip == "10.0.0.9")).Should().Be(1);
    }

    [Fact]
    public async Task Asking_for_a_reset_cannot_lock_the_owner_out_of_sign_in()
    {
        // The trap in recording these at all: five consecutive *failures* lock
        // an account, so if issuing a link counted as a failure, anyone who
        // knows an email could lock its owner out by asking five times. Issuing
        // records a success instead, which is why this passes.
        await SeedResettableUserAsync("victim@example.com", password: "rightpasswordlong");
        var ctx = _db.NewContext();
        var svc = NewPasswordReset(ctx);
        var auth = NewAuth(ctx);

        for (var i = 0; i < AuthService.LockoutThreshold; i++)
        {
            (await svc.CreatePasswordResetTokenAsync("victim@example.com", "10.0.0.7")).Should().NotBeNull();
        }

        var (outcome, _) = await auth.TryLoginAsync("victim@example.com", "rightpasswordlong", "10.0.0.8");
        outcome.Should().Be(LoginOutcome.Success);
    }

    private async Task SeedResettableUserAsync(string email, string password = "somelongpassword1")
    {
        await using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            PasswordHash = NewAuth(seed).HashPassword(password),
            DisplayName = "Reset Subject",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        });
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task Reset_token_for_unknown_email_returns_null_so_we_dont_leak_existence()
    {
        var ctx = _db.NewContext();
        var svc = NewPasswordReset(ctx);
        var token = await svc.CreatePasswordResetTokenAsync("ghost@example.com", "10.0.0.1");
        token.Should().BeNull();
        (await ctx.PasswordResetTokens.AnyAsync()).Should().BeFalse();
    }
}

/// <summary>
/// Hand-rolled <see cref="TimeProvider"/> so tests can advance the clock
/// without waiting on real time. We use it for both lockout-window expiry and
/// reset-token TTL.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public FakeTimeProvider(DateTimeOffset start) { _now = start; }
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
