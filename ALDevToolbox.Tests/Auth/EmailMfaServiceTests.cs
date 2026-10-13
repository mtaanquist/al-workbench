using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Auth;

public sealed class EmailMfaServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private async Task<int> SeedUserAsync(int id = 9100)
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = id,
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"e{id}@example.com",
            PasswordHash = "x",
            DisplayName = "Mfa",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Code_is_six_digits()
    {
        var userId = await SeedUserAsync();
        await using var ctx = _db.NewContext();
        var code = await new EmailMfaService(ctx, _clock).IssueChallengeAsync(userId);
        code.Should().NotBeNull();
        code!.Length.Should().Be(6);
        code.Should().MatchRegex("^[0-9]{6}$");
    }

    [Fact]
    public async Task Verify_succeeds_then_refuses_replay()
    {
        var userId = await SeedUserAsync(9101);
        string? code;
        await using (var ctx = _db.NewContext())
        {
            code = await new EmailMfaService(ctx, _clock).IssueChallengeAsync(userId);
        }
        await using var ctx2 = _db.NewContext();
        var svc = new EmailMfaService(ctx2, _clock);
        (await svc.VerifyAsync(userId, code!)).Should().BeTrue();
        (await svc.VerifyAsync(userId, code!)).Should().BeFalse("single-use");
    }

    [Fact]
    public async Task Verify_rejects_code_issued_for_different_user()
    {
        var userIdA = await SeedUserAsync(9110);
        var userIdB = await SeedUserAsync(9111);
        string? code;
        await using (var ctx = _db.NewContext())
        {
            code = await new EmailMfaService(ctx, _clock).IssueChallengeAsync(userIdA);
        }
        await using var ctx2 = _db.NewContext();
        (await new EmailMfaService(ctx2, _clock).VerifyAsync(userIdB, code!))
            .Should().BeFalse("hash is salted with user id");
    }

    [Fact]
    public async Task Verify_locks_out_after_too_many_wrong_codes()
    {
        var userId = await SeedUserAsync(9130);
        string? code;
        await using (var ctx = _db.NewContext())
        {
            code = await new EmailMfaService(ctx, _clock).IssueChallengeAsync(userId);
        }

        await using var ctx2 = _db.NewContext();
        var svc = new EmailMfaService(ctx2, _clock);
        for (var i = 0; i < EmailMfaService.MaxVerifyAttempts; i++)
        {
            (await svc.VerifyAsync(userId, "000000")).Should().BeFalse();
        }
        // The challenge is dead now — even the correct code is refused until re-issue.
        (await svc.VerifyAsync(userId, code!)).Should().BeFalse("the live window is locked out after too many wrong guesses");
    }

    [Fact]
    public async Task Issue_returns_null_when_rate_limit_exceeded()
    {
        var userId = await SeedUserAsync(9120);
        await using var ctx = _db.NewContext();
        var svc = new EmailMfaService(ctx, _clock);
        (await svc.IssueChallengeAsync(userId)).Should().NotBeNull();
        (await svc.IssueChallengeAsync(userId)).Should().NotBeNull();
        (await svc.IssueChallengeAsync(userId)).Should().NotBeNull();
        (await svc.IssueChallengeAsync(userId)).Should().BeNull("three per ten minutes is the limit");
    }

    [Fact]
    public async Task Parallel_wrong_codes_cannot_overrun_the_cap()
    {
        // Every guess a request of its own, all in flight at once (#1208).
        var userId = await SeedUserAsync(9110);
        string code;
        await using (var ctx = _db.NewContext())
        {
            code = (await new EmailMfaService(ctx, _clock).IssueChallengeAsync(userId))!;
        }
        var wrong = code == "000000" ? "111111" : "000000";

        await Task.WhenAll(Enumerable.Range(0, 25).Select(async _ =>
        {
            await using var ctx = _db.NewContext();
            await new EmailMfaService(ctx, _clock).VerifyAsync(userId, wrong);
        }));

        await using (var read = _db.NewContext())
        {
            (await read.PasswordResetTokens.IgnoreQueryFilters().Where(t => t.UserId == userId).SumAsync(t => t.FailedAttempts))
                .Should().Be(EmailMfaService.MaxVerifyAttempts, "every guess that was compared is counted, and no more than the cap were");
        }
        await using var ctx2 = _db.NewContext();
        (await new EmailMfaService(ctx2, _clock).VerifyAsync(userId, code))
            .Should().BeFalse("the cap was reached, so even the right code is refused until a fresh one is sent");
    }

    [Fact]
    public async Task A_right_code_posted_twice_at_once_signs_in_once()
    {
        var userId = await SeedUserAsync(9111);
        string code;
        await using (var ctx = _db.NewContext())
        {
            code = (await new EmailMfaService(ctx, _clock).IssueChallengeAsync(userId))!;
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var ctx = _db.NewContext();
            return await new EmailMfaService(ctx, _clock).VerifyAsync(userId, code);
        }));

        results.Count(r => r).Should().Be(1);
    }

    [Fact]
    public async Task A_right_code_does_not_use_up_an_attempt()
    {
        var userId = await SeedUserAsync(9112);
        await using var ctx = _db.NewContext();
        var svc = new EmailMfaService(ctx, _clock);
        var code = (await svc.IssueChallengeAsync(userId))!;

        (await svc.VerifyAsync(userId, code)).Should().BeTrue();

        await using var read = _db.NewContext();
        (await read.PasswordResetTokens.IgnoreQueryFilters().Where(t => t.UserId == userId).SumAsync(t => t.FailedAttempts))
            .Should().Be(0);
    }
}
