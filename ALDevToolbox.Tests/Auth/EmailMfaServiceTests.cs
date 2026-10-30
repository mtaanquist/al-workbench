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

        var wrong = code == "000000" ? "111111" : "000000";

        await using var ctx2 = _db.NewContext();
        var svc = new EmailMfaService(ctx2, _clock);
        for (var i = 0; i < EmailMfaService.MaxVerifyAttempts; i++)
        {
            (await svc.VerifyAsync(userId, wrong)).Should().BeFalse();
        }
        // The code is dead now - even the correct one is refused until a new code is sent.
        (await svc.VerifyAsync(userId, code!)).Should().BeFalse("the code is locked out after too many wrong guesses");
    }

    [Fact]
    public async Task A_new_code_lifts_the_lockout_and_retires_the_old_one()
    {
        var userId = await SeedUserAsync(9131);
        await using var ctx = _db.NewContext();
        var svc = new EmailMfaService(ctx, _clock);
        var oldCode = (await svc.IssueChallengeAsync(userId))!;
        var wrong = oldCode == "000000" ? "111111" : "000000";
        for (var i = 0; i < EmailMfaService.MaxVerifyAttempts; i++)
        {
            (await svc.VerifyAsync(userId, wrong)).Should().BeFalse();
        }

        var newCode = (await svc.IssueChallengeAsync(userId))!;

        if (newCode != oldCode)
        {
            (await svc.VerifyAsync(userId, oldCode)).Should().BeFalse("sending a new code retires the old one");
        }
        (await svc.VerifyAsync(userId, newCode)).Should().BeTrue("the new code starts with a full allowance (#1432)");
    }

    [Fact]
    public async Task Only_the_latest_code_works()
    {
        var userId = await SeedUserAsync(9132);
        await using var ctx = _db.NewContext();
        var svc = new EmailMfaService(ctx, _clock);
        var first = await svc.IssueChallengeAsync(userId);
        var second = await svc.IssueChallengeAsync(userId);
        // Two random codes can be the same. Send again until they differ, but only within
        // the send limit: past it a send returns null, which the loop must not run into.
        for (var sent = 2; second == first && sent < EmailMfaService.MaxIssuesPerWindow; sent++)
        {
            second = await svc.IssueChallengeAsync(userId);
        }
        first.Should().NotBeNull();
        second.Should().NotBeNull().And.NotBe(first, "a code the same as the first could not tell the two apart");

        (await svc.VerifyAsync(userId, first!)).Should().BeFalse("the later send retired the first code");
        (await svc.VerifyAsync(userId, second!)).Should().BeTrue();
    }

    [Fact]
    public async Task Parallel_sends_leave_one_live_code()
    {
        var userId = await SeedUserAsync(9133);

        var codes = await Task.WhenAll(Enumerable.Range(0, EmailMfaService.MaxIssuesPerWindow).Select(async _ =>
        {
            await using var ctx = _db.NewContext();
            return await new EmailMfaService(ctx, _clock).IssueChallengeAsync(userId);
        }));

        codes.Should().AllSatisfy(c => c.Should().NotBeNull());
        await using var read = _db.NewContext();
        (await read.PasswordResetTokens.IgnoreQueryFilters()
                .CountAsync(t => t.UserId == userId && t.ConsumedAt == null))
            .Should().Be(1, "each send retires the codes before it, even when the sends race");
    }

    [Fact]
    public async Task A_code_older_than_its_lifetime_is_refused()
    {
        var userId = await SeedUserAsync(9134);
        await using var ctx = _db.NewContext();
        var svc = new EmailMfaService(ctx, _clock);
        var code = (await svc.IssueChallengeAsync(userId))!;

        _clock.Advance(TimeSpan.FromMinutes(11));

        (await svc.VerifyAsync(userId, code)).Should().BeFalse("codes last ten minutes");
    }

    [Fact]
    public async Task Sending_works_again_once_the_throttle_window_has_passed()
    {
        var userId = await SeedUserAsync(9135);
        await using var ctx = _db.NewContext();
        var svc = new EmailMfaService(ctx, _clock);
        for (var i = 0; i < EmailMfaService.MaxIssuesPerWindow; i++)
        {
            (await svc.IssueChallengeAsync(userId)).Should().NotBeNull();
        }
        (await svc.IssueChallengeAsync(userId)).Should().BeNull();

        _clock.Advance(TimeSpan.FromMinutes(11));

        var code = await svc.IssueChallengeAsync(userId);
        code.Should().NotBeNull("the earlier sends have left the window");
        (await svc.VerifyAsync(userId, code!)).Should().BeTrue();
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
            .Should().BeFalse("the cap was reached, so even the right code is refused until a new one is sent");
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
