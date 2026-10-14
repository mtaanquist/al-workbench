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
/// Cross-service flows: login orchestration when 2FA is enrolled, the refined
/// self-delete guard, admin email change, and SiteAdmin MFA reset.
/// </summary>
public sealed class AccountSecurityFlowTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private AuthService NewAuth(Data.AppDbContext ctx) =>
        new(ctx, NullLogger<AuthService>.Instance, _clock);
    private SystemSettingsService NewSettings(Data.AppDbContext ctx) =>
        new(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, _clock);
    private AccountService NewAccounts(Data.AppDbContext ctx) =>
        new(ctx, NewAuth(ctx), NewSettings(ctx),
            new ALDevToolbox.Services.SingleTenant.SingleTenantModeState(false),
            NullLogger<AccountService>.Instance, _clock, _db.OpenIddictTokens);
    private UserAdministrationService NewUserAdmin(Data.AppDbContext ctx) =>
        _db.NewUserAdministrationService(ctx, _clock);

    [Fact]
    public async Task TryLogin_returns_MfaRequired_when_totp_enabled()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            var auth = NewAuth(seed);
            seed.Users.Add(new User
            {
                Id = 5000, OrganizationId = orgId, Email = "mfa@example.com",
                PasswordHash = auth.HashPassword("verylongpassword12345"),
                DisplayName = "M", Role = UserRole.User, Status = UserStatus.Active,
                TotpEnabled = true, CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();
        var (outcome, user) = await NewAuth(ctx).TryLoginAsync(
            "mfa@example.com", "verylongpassword12345", "127.0.0.1");
        outcome.Should().Be(LoginOutcome.MfaRequired);
        user.Should().NotBeNull();
        user!.LastLoginAt.Should().BeNull("Last-login stamp is deferred until CompleteMfaAsync");
    }

    [Fact]
    public async Task CompleteMfa_stamps_last_login_and_records_success()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = 5010, OrganizationId = orgId, Email = "mfa2@example.com",
                PasswordHash = "x", DisplayName = "M2",
                Role = UserRole.User, Status = UserStatus.Active, TotpEnabled = true,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }
        await using (var ctx = _db.NewContext())
        {
            var auth = NewAuth(ctx);
            var user = await auth.CompleteMfaAsync(5010, "127.0.0.1");
            user.LastLoginAt.Should().NotBeNull();
        }
        await using var read = _db.NewContext();
        var attempt = await read.LoginAttempts.IgnoreQueryFilters()
            .Where(a => a.Email == "mfa2@example.com")
            .OrderByDescending(a => a.Timestamp).FirstAsync();
        attempt.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Mfa_is_not_throttled_below_the_cap()
    {
        await SeedMfaUserAsync(5300, "mfathrottle1@example.com");
        await using var ctx = _db.NewContext();
        var auth = NewAuth(ctx);

        // Every attempt up to the cap goes ahead (and, unreleased, stands as a failure).
        for (var i = 0; i < AuthService.MaxMfaAttempts; i++)
            (await auth.TryReserveMfaAttemptAsync(5300, "127.0.0.1", default)).Should().NotBeNull();
    }

    [Fact]
    public async Task Mfa_is_throttled_after_the_cap_of_failures()
    {
        await SeedMfaUserAsync(5310, "mfathrottle2@example.com");
        await using (var ctx = _db.NewContext())
        {
            var auth = NewAuth(ctx);
            for (var i = 0; i < AuthService.MaxMfaAttempts; i++)
                await auth.TryReserveMfaAttemptAsync(5310, "127.0.0.1", default);
            (await auth.TryReserveMfaAttemptAsync(5310, "127.0.0.1", default))
                .Should().BeNull("MaxMfaAttempts consecutive failures locks the second factor");
        }

        await using var read = _db.NewContext();
        (await read.LoginAttempts.IgnoreQueryFilters()
                .CountAsync(a => a.Email == "mfathrottle2@example.com" && !a.Succeeded))
            .Should().Be(AuthService.MaxMfaAttempts, "each wrong code is recorded, and the refused one is not");
    }

    [Fact]
    public async Task Mfa_throttle_clears_after_a_successful_attempt()
    {
        await SeedMfaUserAsync(5320, "mfathrottle3@example.com");
        await using var ctx = _db.NewContext();
        var auth = NewAuth(ctx);
        for (var i = 0; i < AuthService.MaxMfaAttempts; i++)
            await auth.TryReserveMfaAttemptAsync(5320, "127.0.0.1", default);
        (await auth.TryReserveMfaAttemptAsync(5320, "127.0.0.1", default)).Should().BeNull();

        // A success (e.g. finally entering the right code) breaks the failure
        // streak, so the account isn't stuck locked.
        _clock.Advance(TimeSpan.FromSeconds(1));
        await auth.CompleteMfaAsync(5320, "127.0.0.1", default);
        (await auth.TryReserveMfaAttemptAsync(5320, "127.0.0.1", default)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_released_attempt_is_not_counted_as_a_failure()
    {
        await SeedMfaUserAsync(5330, "mfathrottle4@example.com");
        await using (var ctx = _db.NewContext())
        {
            var auth = NewAuth(ctx);
            var attempt = await auth.TryReserveMfaAttemptAsync(5330, "127.0.0.1", default);
            await auth.ReleaseMfaAttemptAsync(attempt!.Value, default);
        }

        await using var read = _db.NewContext();
        (await read.LoginAttempts.IgnoreQueryFilters().AnyAsync(a => a.Email == "mfathrottle4@example.com"))
            .Should().BeFalse("a right code is not a failed attempt");
    }

    [Fact]
    public async Task Parallel_guesses_cannot_overrun_the_mfa_cap()
    {
        // Each guess a request of its own, all in flight at once (#1208).
        // Checking before writing let every one of them read a count of zero.
        await SeedMfaUserAsync(5340, "mfathrottle5@example.com");
        const int guesses = 25;

        var attempts = await Task.WhenAll(Enumerable.Range(0, guesses).Select(async _ =>
        {
            await using var ctx = _db.NewContext();
            return await NewAuth(ctx).TryReserveMfaAttemptAsync(5340, "127.0.0.1", default);
        }));

        attempts.Count(a => a is not null).Should().BeLessThanOrEqualTo(AuthService.MaxMfaAttempts);
    }

    [Fact]
    public async Task A_reset_or_sign_in_link_request_does_not_lift_the_mfa_throttle()
    {
        // Issuing either link records a success row; that used to end the
        // run of failures, so someone with the password could interleave
        // requests and keep guessing (#1259).
        await SeedMfaUserAsync(5350, "mfathrottle6@example.com");
        await using var ctx = _db.NewContext();
        var auth = NewAuth(ctx);
        var resets = new PasswordResetService(ctx, auth, _clock, _db.OpenIddictTokens);
        for (var i = 0; i < AuthService.MaxMfaAttempts; i++)
            await auth.TryReserveMfaAttemptAsync(5350, "127.0.0.1", default);

        _clock.Advance(TimeSpan.FromSeconds(1));
        (await resets.CreatePasswordResetTokenAsync("mfathrottle6@example.com", "127.0.0.1")).Should().NotBeNull();
        (await auth.TryReserveMfaAttemptAsync(5350, "127.0.0.1", default)).Should().BeNull();

        _clock.Advance(TimeSpan.FromSeconds(1));
        (await resets.CreateMagicLoginTokenAsync("mfathrottle6@example.com", "127.0.0.1")).Should().NotBeNull();
        (await auth.TryReserveMfaAttemptAsync(5350, "127.0.0.1", default)).Should().BeNull();
    }

    [Fact]
    public async Task Successful_sign_ins_from_one_address_do_not_use_up_its_login_limit()
    {
        // An office behind one address: thirty good sign-ins must not lock
        // the next person out (#1259). Each user signs in once, so the
        // per-email limit never comes into it.
        const int people = AuthService.MaxAttemptsPerIp + 1;
        await using (var seed = _db.NewContext())
        {
            var hash = NewAuth(seed).HashPassword("verylongpassword12345");
            for (var i = 0; i < people; i++)
            {
                seed.Users.Add(new User
                {
                    Id = 5500 + i, OrganizationId = TestDb.DefaultOrgId, Email = $"office{i}@example.com",
                    PasswordHash = hash, DisplayName = "Office", Role = UserRole.User,
                    Status = UserStatus.Active, CreatedAt = _clock.GetUtcNow().UtcDateTime,
                });
            }
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var auth = NewAuth(ctx);
        for (var i = 0; i < people; i++)
        {
            var (outcome, _) = await auth.TryLoginAsync($"office{i}@example.com", "verylongpassword12345", "203.0.113.7");
            outcome.Should().Be(LoginOutcome.Success, $"sign-in {i + 1} from the office address");
        }
    }

    [Fact]
    public async Task Failed_sign_ins_from_one_address_still_use_up_its_login_limit()
    {
        await using var ctx = _db.NewContext();
        var auth = NewAuth(ctx);
        for (var i = 0; i < AuthService.MaxAttemptsPerIp; i++)
            await auth.TryLoginAsync($"nobody{i}@example.com", "wrong-password-123", "203.0.113.8");

        var (outcome, _) = await auth.TryLoginAsync("nobody-else@example.com", "wrong-password-123", "203.0.113.8");
        outcome.Should().Be(LoginOutcome.RateLimited);
    }

    private async Task SeedMfaUserAsync(int id, string email)
    {
        await using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            Id = id, OrganizationId = TestDb.DefaultOrgId, Email = email,
            PasswordHash = "x", DisplayName = "MFA", Role = UserRole.User,
            Status = UserStatus.Active, TotpEnabled = true,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        });
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task Admin_email_change_persists_pending_and_confirm_swaps()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.AddRange(
                new User { Id = 5400, OrganizationId = orgId, Email = "admin@example.com", PasswordHash = "x",
                    DisplayName = "Admin", Role = UserRole.Admin, Status = UserStatus.Active,
                    CreatedAt = _clock.GetUtcNow().UtcDateTime },
                new User { Id = 5401, OrganizationId = orgId, Email = "old@example.com", PasswordHash = "x",
                    DisplayName = "Target", Role = UserRole.User, Status = UserStatus.Active,
                    CreatedAt = _clock.GetUtcNow().UtcDateTime });
            await seed.SaveChangesAsync();
        }
        string token;
        await using (var ctx = _db.NewContext())
        {
            token = (await NewUserAdmin(ctx).RequestEmailChangeAsync(5401, "new@example.com", orgId, 5400)).ConfirmToken;
        }
        await using (var read = _db.NewContext())
        {
            var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == 5401);
            user.PendingEmail.Should().Be("new@example.com");
            user.Email.Should().Be("old@example.com", "until the token is consumed");
        }
        _clock.Advance(UserAdministrationService.EmailChangeConfirmDelay);
        await using (var ctx = _db.NewContext())
        {
            var result = await NewUserAdmin(ctx).ConfirmEmailChangeAsync(token);
            result.Outcome.Should().Be(EmailChangeConfirmOutcome.Confirmed);
        }
        await using (var read = _db.NewContext())
        {
            var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == 5401);
            user.Email.Should().Be("new@example.com");
            user.PendingEmail.Should().BeNull();
        }
    }

    [Fact]
    public async Task Reissued_email_change_invalidates_prior_token()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.AddRange(
                new User { Id = 5450, OrganizationId = orgId, Email = "admin2@example.com", PasswordHash = "x",
                    DisplayName = "Admin2", Role = UserRole.Admin, Status = UserStatus.Active,
                    CreatedAt = _clock.GetUtcNow().UtcDateTime },
                new User { Id = 5451, OrganizationId = orgId, Email = "user@example.com", PasswordHash = "x",
                    DisplayName = "User", Role = UserRole.User, Status = UserStatus.Active,
                    CreatedAt = _clock.GetUtcNow().UtcDateTime });
            await seed.SaveChangesAsync();
        }
        string firstToken, secondToken;
        await using (var ctx = _db.NewContext())
        {
            firstToken = (await NewUserAdmin(ctx).RequestEmailChangeAsync(5451, "first@example.com", orgId, 5450)).ConfirmToken;
        }
        await using (var ctx = _db.NewContext())
        {
            secondToken = (await NewUserAdmin(ctx).RequestEmailChangeAsync(5451, "second@example.com", orgId, 5450)).ConfirmToken;
        }
        _clock.Advance(UserAdministrationService.EmailChangeConfirmDelay);
        await using (var ctx = _db.NewContext())
        {
            var result = await NewUserAdmin(ctx).ConfirmEmailChangeAsync(firstToken);
            result.Outcome.Should().Be(EmailChangeConfirmOutcome.Invalid, "old token must be invalidated when a newer change is requested");
        }
        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == 5451);
        user.Email.Should().Be("user@example.com", "the old token must not have swapped the email");
        // Second token still works against the current pending value.
        await using (var ctx = _db.NewContext())
        {
            (await NewUserAdmin(ctx).ConfirmEmailChangeAsync(secondToken)).Outcome.Should().Be(EmailChangeConfirmOutcome.Confirmed);
        }
    }

    [Fact]
    public async Task Admin_cannot_change_own_email_via_admin_path()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User { Id = 5500, OrganizationId = orgId, Email = "selfadmin@example.com",
                PasswordHash = "x", DisplayName = "A", Role = UserRole.Admin, Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime });
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();
        var act = () => NewUserAdmin(ctx).RequestEmailChangeAsync(5500, "fresh@example.com", orgId, 5500);
        await act.Should().ThrowAsync<PlanValidationException>();
    }

    [Fact]
    public async Task ResetMfa_wipes_secret_codes_passkeys_and_flags()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User { Id = 5600, OrganizationId = orgId, Email = "reset@example.com",
                PasswordHash = "x", DisplayName = "R", Role = UserRole.User, Status = UserStatus.Active,
                TotpEnabled = true, EmailMfaEnabled = true,
                CreatedAt = _clock.GetUtcNow().UtcDateTime });
            seed.UserTotpSecrets.Add(new UserTotpSecret { UserId = 5600, SecretEncrypted = "x",
                ConfirmedAt = _clock.GetUtcNow().UtcDateTime, CreatedAt = _clock.GetUtcNow().UtcDateTime });
            seed.UserRecoveryCodes.Add(new UserRecoveryCode { UserId = 5600, CodeHash = "h",
                CreatedAt = _clock.GetUtcNow().UtcDateTime });
            seed.UserPasskeys.Add(new UserPasskey { UserId = 5600,
                CredentialId = new byte[] { 1, 2, 3 }, PublicKey = new byte[] { 4, 5, 6 },
                SignCounter = 1, Transports = "usb", Nickname = "Yubi",
                CreatedAt = _clock.GetUtcNow().UtcDateTime });
            await seed.SaveChangesAsync();
        }
        _db.OrgContext.IsSiteAdmin = true;
        await using (var ctx = _db.NewContext())
        {
            await NewUserAdmin(ctx).ResetMfaAsync(5600);
        }
        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == 5600);
        user.TotpEnabled.Should().BeFalse();
        user.EmailMfaEnabled.Should().BeFalse();
        (await read.UserTotpSecrets.IgnoreQueryFilters().CountAsync(s => s.UserId == 5600)).Should().Be(0);
        (await read.UserRecoveryCodes.IgnoreQueryFilters().CountAsync(c => c.UserId == 5600)).Should().Be(0);
        (await read.UserPasskeys.IgnoreQueryFilters().CountAsync(p => p.UserId == 5600)).Should().Be(0);
    }

    [Fact]
    public async Task ResetMfa_refuses_a_caller_who_is_not_a_site_admin()
    {
        // The reads cross the tenant fence, so the service checks the role
        // itself rather than trusting the route to have done it (#1272).
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User { Id = 5610, OrganizationId = TestDb.DefaultOrgId, Email = "keep@example.com",
                PasswordHash = "x", DisplayName = "K", Role = UserRole.User, Status = UserStatus.Active,
                TotpEnabled = true, CreatedAt = _clock.GetUtcNow().UtcDateTime });
            await seed.SaveChangesAsync();
        }
        _db.OrgContext.IsSiteAdmin = false;

        await using (var ctx = _db.NewContext())
        {
            var act = () => NewUserAdmin(ctx).ResetMfaAsync(5610);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == 5610)).TotpEnabled.Should().BeTrue();
    }
}
