using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Auth;

public sealed class RecoveryCodeServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private async Task<int> SeedUserAsync(int id = 8100)
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = id,
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"rc{id}@example.com",
            PasswordHash = "x",
            DisplayName = "RC",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Regenerate_issues_ten_codes_only_hashes_are_persisted()
    {
        var userId = await SeedUserAsync();
        IReadOnlyList<string> codes;
        await using (var ctx = _db.NewContext())
        {
            codes = await new RecoveryCodeService(ctx, _clock).RegenerateAsync(userId);
        }
        codes.Should().HaveCount(10);
        codes.Distinct().Should().HaveCount(10);

        await using var read = _db.NewContext();
        var rows = await read.UserRecoveryCodes.IgnoreQueryFilters().Where(c => c.UserId == userId).ToListAsync();
        rows.Should().HaveCount(10);
        rows.Should().OnlyContain(r => !codes.Contains(r.CodeHash), "stored value is a BCrypt hash, never the plaintext");
    }

    [Fact]
    public async Task Consume_is_single_use_and_reduces_remaining_count()
    {
        var userId = await SeedUserAsync(8101);
        IReadOnlyList<string> codes;
        await using (var ctx = _db.NewContext())
        {
            codes = await new RecoveryCodeService(ctx, _clock).RegenerateAsync(userId);
        }

        await using var ctx1 = _db.NewContext();
        var svc1 = new RecoveryCodeService(ctx1, _clock);
        (await svc1.ConsumeAsync(userId, codes[0])).Should().BeTrue();
        (await svc1.ConsumeAsync(userId, codes[0])).Should().BeFalse("each code is single-use");

        await using var ctx2 = _db.NewContext();
        (await new RecoveryCodeService(ctx2, _clock).RemainingAsync(userId)).Should().Be(9);
    }

    [Fact]
    public async Task Regenerate_wipes_prior_codes()
    {
        var userId = await SeedUserAsync(8102);
        await using (var ctx = _db.NewContext())
        {
            await new RecoveryCodeService(ctx, _clock).RegenerateAsync(userId);
            await new RecoveryCodeService(ctx, _clock).RegenerateAsync(userId);
        }
        await using var read = _db.NewContext();
        (await read.UserRecoveryCodes.IgnoreQueryFilters().CountAsync(c => c.UserId == userId)).Should().Be(10);
    }

    [Fact]
    public async Task A_code_posted_twice_at_once_is_accepted_once()
    {
        // Two requests carrying the same code, both past the BCrypt check
        // before either wrote (#1208).
        var userId = await SeedUserAsync(8110);
        IReadOnlyList<string> codes;
        await using (var ctx = _db.NewContext())
        {
            codes = await new RecoveryCodeService(ctx, _clock).RegenerateAsync(userId);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var ctx = _db.NewContext();
            return await new RecoveryCodeService(ctx, _clock).ConsumeAsync(userId, codes[0]);
        }));

        results.Count(r => r).Should().Be(1);
    }
}
