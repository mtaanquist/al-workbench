using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Behavioural tests for <see cref="PendingSignupService"/> — the pre-account
/// email-verification step of the email-first signup flow. Covers token/code
/// verification, expiry, single-use, code binding, rate limiting, opportunistic
/// cleanup, and the non-enumeration invariant (no row, no email for an already-
/// registered address).
/// </summary>
public sealed class PendingSignupServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private PendingSignupService NewService(Data.AppDbContext ctx) =>
        new(ctx,
            new AuthService(ctx, NullLogger<AuthService>.Instance, _clock),
            new SystemSettingsService(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, _clock),
            _clock,
            NullLogger<PendingSignupService>.Instance);

    [Fact]
    public async Task Start_persists_a_row_and_returns_secrets_only_as_plaintext()
    {
        await using var ctx = _db.NewContext();
        var start = await NewService(ctx).StartAsync("new@example.com", "1.2.3.4");

        start.Should().NotBeNull();
        start!.LinkToken.Should().NotBeNullOrEmpty();
        start.Code.Should().MatchRegex("^[0-9]{6}$");

        await using var read = _db.NewContext();
        var row = await read.PendingSignups.IgnoreQueryFilters().SingleAsync();
        row.Email.Should().Be("new@example.com");
        row.LinkTokenHash.Should().NotBe(start.LinkToken, "only the hash is persisted");
        row.CodeHash.Should().NotBe(start.Code, "only the hash is persisted");
        row.VerifiedAt.Should().BeNull();
        row.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task VerifyByToken_stamps_verified_and_is_idempotent()
    {
        PendingSignupStart start;
        await using (var ctx = _db.NewContext())
        {
            start = (await NewService(ctx).StartAsync("link@example.com", "1.2.3.4"))!;
        }

        await using var ctx2 = _db.NewContext();
        var svc = NewService(ctx2);
        var first = await svc.VerifyByTokenAsync(start.LinkToken);
        first.Should().NotBeNull();
        first!.VerifiedAt.Should().NotBeNull();

        // A second click on the same link still resolves (idempotent), not a replay error.
        (await svc.VerifyByTokenAsync(start.LinkToken)).Should().NotBeNull();
    }

    [Fact]
    public async Task VerifyByCode_accepts_the_matching_code_and_rejects_a_wrong_one()
    {
        PendingSignupStart start;
        await using (var ctx = _db.NewContext())
        {
            start = (await NewService(ctx).StartAsync("code@example.com", "1.2.3.4"))!;
        }

        await using var ctx2 = _db.NewContext();
        var svc = NewService(ctx2);
        (await svc.VerifyByCodeAsync("code@example.com", "000000")).Should().BeNull("wrong code");
        (await svc.VerifyByCodeAsync("code@example.com", start.Code)).Should().NotBeNull();
    }

    [Fact]
    public async Task VerifyByCode_locks_out_after_too_many_wrong_codes()
    {
        PendingSignupStart start;
        await using (var ctx = _db.NewContext())
        {
            start = (await NewService(ctx).StartAsync("brute@example.com", "1.2.3.4"))!;
        }

        await using var ctx2 = _db.NewContext();
        var svc = NewService(ctx2);
        for (var i = 0; i < PendingSignupService.MaxCodeAttempts; i++)
        {
            (await svc.VerifyByCodeAsync("brute@example.com", "000000")).Should().BeNull();
        }
        // The row is dead now — even the correct code is refused until a fresh start.
        (await svc.VerifyByCodeAsync("brute@example.com", start.Code))
            .Should().BeNull("the row is locked out after too many wrong codes");
    }

    [Fact]
    public async Task Code_from_one_signup_does_not_verify_another()
    {
        PendingSignupStart startA;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            startA = (await svc.StartAsync("a@example.com", "1.2.3.4"))!;
            await svc.StartAsync("b@example.com", "1.2.3.4");
        }

        await using var ctx2 = _db.NewContext();
        (await NewService(ctx2).VerifyByCodeAsync("b@example.com", startA.Code))
            .Should().BeNull("the code hash is bound to its own row's link token");
    }

    [Fact]
    public async Task Expired_link_and_code_no_longer_verify()
    {
        PendingSignupStart start;
        await using (var ctx = _db.NewContext())
        {
            start = (await NewService(ctx).StartAsync("expire@example.com", "1.2.3.4"))!;
        }

        _clock.Advance(PendingSignupService.Lifetime + TimeSpan.FromMinutes(1));

        await using var ctx2 = _db.NewContext();
        var svc = NewService(ctx2);
        (await svc.VerifyByTokenAsync(start.LinkToken)).Should().BeNull();
        (await svc.VerifyByCodeAsync("expire@example.com", start.Code)).Should().BeNull();
    }

    [Fact]
    public async Task Completed_row_cannot_be_re_verified_or_found()
    {
        PendingSignupStart start;
        await using (var ctx = _db.NewContext())
        {
            start = (await NewService(ctx).StartAsync("done@example.com", "1.2.3.4"))!;
        }
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).VerifyByTokenAsync(start.LinkToken);
        }
        // Simulate AccountService.CompleteVerifiedSignupAsync stamping completion.
        await using (var ctx = _db.NewContext())
        {
            var row = await ctx.PendingSignups.IgnoreQueryFilters().SingleAsync();
            row.CompletedAt = _clock.GetUtcNow().UtcDateTime;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var svc = NewService(read);
        (await svc.VerifyByTokenAsync(start.LinkToken)).Should().BeNull("single-use");
        (await svc.FindVerifiedAsync("done@example.com")).Should().BeNull();
    }

    [Fact]
    public async Task Start_for_an_already_registered_email_creates_no_row_and_sends_nothing()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "taken@example.com",
                PasswordHash = "x",
                DisplayName = "Taken",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var start = await NewService(ctx).StartAsync("taken@example.com", "1.2.3.4");

        start.Should().BeNull("never reveal that the email already has an account");
        await using var read = _db.NewContext();
        (await read.PendingSignups.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Start_is_rate_limited_after_the_per_email_threshold()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        for (var i = 0; i < AuthService.MaxAttemptsPerEmail; i++)
        {
            (await svc.StartAsync("rl@example.com", "1.2.3.4")).Should().NotBeNull();
        }
        (await svc.StartAsync("rl@example.com", "1.2.3.4"))
            .Should().BeNull("ten sends per email in the window is the cap");
    }

    [Fact]
    public async Task Start_sweeps_expired_rows_and_supersedes_in_flight_attempts()
    {
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).StartAsync("sweep@example.com", "1.2.3.4");
        }

        // Let the first row expire, then start fresh for a different email.
        _clock.Advance(PendingSignupService.Lifetime + TimeSpan.FromMinutes(1));
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).StartAsync("other@example.com", "1.2.3.4");
        }

        await using var read = _db.NewContext();
        var rows = await read.PendingSignups.IgnoreQueryFilters().ToListAsync();
        rows.Should().ContainSingle("the expired row was swept on the next start");
        rows[0].Email.Should().Be("other@example.com");
    }

    [Fact]
    public async Task Restarting_the_same_email_keeps_only_the_newest_active_row()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await svc.StartAsync("again@example.com", "1.2.3.4");
        await svc.StartAsync("again@example.com", "1.2.3.4");

        await using var read = _db.NewContext();
        (await read.PendingSignups.IgnoreQueryFilters().CountAsync(p => p.Email == "again@example.com"))
            .Should().Be(1, "each start supersedes the prior in-flight row");
    }

    [Fact]
    public async Task Parallel_wrong_codes_cannot_overrun_the_cap()
    {
        // Every guess a request of its own, all in flight at once (#1208).
        PendingSignupStart start;
        await using (var ctx = _db.NewContext())
        {
            start = (await NewService(ctx).StartAsync("swarm@example.com", "1.2.3.4"))!;
        }
        var wrong = start.Code == "000000" ? "111111" : "000000";

        await Task.WhenAll(Enumerable.Range(0, 25).Select(async _ =>
        {
            await using var ctx = _db.NewContext();
            await NewService(ctx).VerifyByCodeAsync("swarm@example.com", wrong);
        }));

        await using (var read = _db.NewContext())
        {
            (await read.PendingSignups.SingleAsync(p => p.Email == "swarm@example.com")).FailedCodeAttempts
                .Should().Be(PendingSignupService.MaxCodeAttempts);
        }
        await using var ctx2 = _db.NewContext();
        (await NewService(ctx2).VerifyByCodeAsync("swarm@example.com", start.Code))
            .Should().BeNull("the cap was reached, so even the right code is refused");
    }

    [Fact]
    public async Task A_right_code_does_not_use_up_an_attempt()
    {
        PendingSignupStart start;
        await using (var ctx = _db.NewContext())
        {
            start = (await NewService(ctx).StartAsync("right@example.com", "1.2.3.4"))!;
        }

        await using var ctx2 = _db.NewContext();
        (await NewService(ctx2).VerifyByCodeAsync("right@example.com", start.Code)).Should().NotBeNull();

        await using var read = _db.NewContext();
        (await read.PendingSignups.SingleAsync(p => p.Email == "right@example.com")).FailedCodeAttempts.Should().Be(0);
    }
}
