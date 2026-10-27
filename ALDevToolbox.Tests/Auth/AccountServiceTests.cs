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
            NullLogger<AccountService>.Instance, _clock, _db.OpenIddictTokens);

    private UserAdministrationService NewUserAdmin(Data.AppDbContext ctx) =>
        _db.NewUserAdministrationService(ctx, _clock);

    private PasswordResetService NewPasswordReset(Data.AppDbContext ctx) =>
        new(ctx, NewAuth(ctx), _clock, _db.OpenIddictTokens);

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
    public async Task A_double_submitted_signup_creates_one_account_and_one_organisation()
    {
        // Two posts for one address in flight at once, as a double-clicked
        // Sign up sends them. Each gets its own context, like two requests.
        Task<(SignupOutcome Outcome, User? User, Organization? Organization)> Submit() =>
            NewAccounts(_db.NewContext()).SignupAsync(
                "double@cronus.example", "Double", "verylongpassword12345", null, "CRONUS Double");

        var results = await Task.WhenAll(Submit(), Submit());

        results.Select(r => r.Outcome).Should().BeEquivalentTo(
            [SignupOutcome.OrganizationProvisioned, SignupOutcome.EmailAlreadyTaken]);
        await using var read = _db.NewContext();
        (await read.Organizations.CountAsync(o => o.Name == "CRONUS Double"))
            .Should().Be(1, "the losing submit must not leave a second organisation behind");
        (await read.Users.IgnoreQueryFilters().CountAsync(u => u.Email == "double@cronus.example"))
            .Should().Be(1);
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

        // And a fresh token stops working once its hour is up (#1271).
        var late = await svc.CreatePasswordResetTokenAsync("reset@example.com", "10.0.0.1");
        _clock.Advance(PasswordResetService.ResetTokenLifetime + TimeSpan.FromSeconds(1));
        Func<Task> expired = () => svc.ConsumePasswordResetTokenAsync(late!, "anotherlongpassword!");
        (await expired.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Token");
        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == 200);
        NewAuth(read).VerifyPassword("verylongnewpassword!", user.PasswordHash)
            .Should().BeTrue("the expired link must not have changed the password");
    }

    [Fact]
    public async Task Reset_token_is_refused_once_the_account_is_no_longer_active()
    {
        // A link issued before the account was disabled must not set a
        // password on it afterwards (#1271).
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = 201, OrganizationId = TestDb.DefaultOrgId, Email = "leaver@example.com",
                PasswordHash = "old", DisplayName = "Leaver", Role = UserRole.User,
                Status = UserStatus.Active, CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }
        var ctx = _db.NewContext();
        var svc = NewPasswordReset(ctx);
        var token = await svc.CreatePasswordResetTokenAsync("leaver@example.com", "10.0.0.1");
        token.Should().NotBeNull();
        await using (var disable = _db.NewContext())
        {
            await disable.Users.IgnoreQueryFilters().Where(u => u.Id == 201)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Disabled));
        }

        await using var fresh = _db.NewContext();
        Func<Task> act = () => NewPasswordReset(fresh).ConsumePasswordResetTokenAsync(token!, "verylongnewpassword!");
        await act.Should().ThrowAsync<PlanValidationException>();
        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == 201)).PasswordHash.Should().Be("old");
    }

    [Fact]
    public async Task A_reset_is_refused_when_the_account_is_disabled_while_the_submit_waits_for_the_lock()
    {
        // The disable lands after the consume has read the token and its user
        // but before it holds the user-row lock, so only a check made under the
        // lock can see it.
        await SeedResettableUserAsync("racing@example.com");
        string token;
        await using (var ctx = _db.NewContext())
        {
            token = (await NewPasswordReset(ctx).CreatePasswordResetTokenAsync("racing@example.com", "10.0.0.1"))!;
        }
        var disableBeforeLock = new BeforeUserLock(async () =>
        {
            await using var disable = _db.NewContext();
            await disable.Users.IgnoreQueryFilters().Where(u => u.Email == "racing@example.com")
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Disabled));
        });

        PlanValidationException? refused = null;
        await using (var consume = _db.NewContext(disableBeforeLock))
        {
            try
            {
                await NewPasswordReset(consume).ConsumePasswordResetTokenAsync(token, "verylongnewpassword!");
            }
            catch (PlanValidationException ex)
            {
                refused = ex;
            }
        }

        // First, so a lock taken some other way fails on its cause, not on the refusal.
        disableBeforeLock.Fired.Should().BeTrue("the disable has to land in the window under test");
        refused.Should().NotBeNull("the account was disabled before the lock was taken");
        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == "racing@example.com");
        NewAuth(read).VerifyPassword("somelongpassword1", user.PasswordHash)
            .Should().BeTrue("a disabled account must keep its old password");
    }

    /// <summary>
    /// Runs a callback once, just before the consume takes its user-row lock,
    /// whether the lock is sent as a statement or as a query.
    /// </summary>
    private sealed class BeforeUserLock : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private readonly Func<Task> _callback;
        public BeforeUserLock(Func<Task> callback) => _callback = callback;
        public bool Fired { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await FireBeforeLockAsync(command);
            return result;
        }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await FireBeforeLockAsync(command);
            return result;
        }

        private async Task FireBeforeLockAsync(System.Data.Common.DbCommand command)
        {
            if (Fired || !command.CommandText.Contains("FOR UPDATE")) return;
            Fired = true;
            await _callback();
        }
    }

    [Fact]
    public async Task A_reset_link_submitted_twice_at_once_sets_one_password()
    {
        // Both submits past the read before either wrote (#1455, as #1208
        // for magic links).
        await SeedResettableUserAsync("twice@example.com");
        string token;
        await using (var ctx = _db.NewContext())
        {
            token = (await NewPasswordReset(ctx).CreatePasswordResetTokenAsync("twice@example.com", "10.0.0.1"))!;
        }

        var passwords = new[] { "first-new-password-1", "second-new-password-2" };
        var results = await Task.WhenAll(passwords.Select(async password =>
        {
            await using var ctx = _db.NewContext();
            try
            {
                await NewPasswordReset(ctx).ConsumePasswordResetTokenAsync(token, password);
                return password;
            }
            catch (PlanValidationException)
            {
                return null;
            }
        }));

        var winner = results.Should().ContainSingle(r => r != null).Which!;
        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == "twice@example.com");
        NewAuth(read).VerifyPassword(winner, user.PasswordHash)
            .Should().BeTrue("the password is the one from the submit that claimed the link");
    }

    [Fact]
    public async Task Two_reset_requests_at_once_leave_exactly_one_live_link()
    {
        // Each request retires what is live and adds its own; without the lock
        // on the user row both retire nothing and both links stay live.
        await SeedResettableUserAsync("burst@example.com");

        var tokens = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var ctx = _db.NewContext();
            return await NewPasswordReset(ctx).CreatePasswordResetTokenAsync("burst@example.com", "10.0.0.1");
        }));

        tokens.Should().AllSatisfy(t => t.Should().NotBeNull());
        await using var read = _db.NewContext();
        (await read.PasswordResetTokens.IgnoreQueryFilters()
                .CountAsync(t => t.User!.Email == "burst@example.com" && t.ConsumedAt == null))
            .Should().Be(1);
    }

    [Fact]
    public async Task Requesting_a_reset_email_leaves_another_users_link_working()
    {
        await SeedResettableUserAsync("first@example.com");
        await SeedResettableUserAsync("second@example.com");
        await using var ctx = _db.NewContext();
        var svc = NewPasswordReset(ctx);
        var othersLink = await svc.CreatePasswordResetTokenAsync("second@example.com", "10.0.0.1");

        await svc.CreatePasswordResetTokenAsync("first@example.com", "10.0.0.1");

        await svc.ConsumePasswordResetTokenAsync(othersLink!, "second-users-new-password");
        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == "second@example.com");
        NewAuth(read).VerifyPassword("second-users-new-password", user.PasswordHash).Should().BeTrue();
    }

    [Fact]
    public async Task Requesting_a_new_reset_email_retires_the_earlier_link()
    {
        // The reset page promises this (#1455).
        await SeedResettableUserAsync("again@example.com", password: "originalpasswordlong");
        await using var ctx = _db.NewContext();
        var svc = NewPasswordReset(ctx);
        var first = await svc.CreatePasswordResetTokenAsync("again@example.com", "10.0.0.1");
        var second = await svc.CreatePasswordResetTokenAsync("again@example.com", "10.0.0.1");

        Func<Task> old = () => svc.ConsumePasswordResetTokenAsync(first!, "from-the-old-link-1");
        (await old.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Token");

        await svc.ConsumePasswordResetTokenAsync(second!, "from-the-new-link-1");
        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == "again@example.com");
        NewAuth(read).VerifyPassword("from-the-new-link-1", user.PasswordHash).Should().BeTrue();
    }

    [Fact]
    public async Task Requesting_a_reset_email_leaves_a_magic_link_alone()
    {
        // Only earlier reset links are retired; a sign-in link sent the same
        // minute is a different credential and keeps working.
        await SeedResettableUserAsync("both@example.com");
        await using var ctx = _db.NewContext();
        var svc = NewPasswordReset(ctx);
        var magic = await svc.CreateMagicLoginTokenAsync("both@example.com", "10.0.0.1");
        await svc.CreatePasswordResetTokenAsync("both@example.com", "10.0.0.1");

        var user = await svc.ConsumeMagicLoginTokenAsync(magic!);
        user.Email.Should().Be("both@example.com");
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
