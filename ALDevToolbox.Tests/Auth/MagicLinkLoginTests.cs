using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Behavioural tests for the magic-link sign-in extensions to
/// <see cref="PasswordResetService"/> (P4.19): 15-minute expiry, single-use
/// semantics, purpose separation from password-reset tokens, opaque response
/// for unknown emails, and the per-email rate limit.
/// </summary>
public sealed class MagicLinkLoginTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 11, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private PasswordResetService NewSvc(Data.AppDbContext ctx) =>
        new(ctx,
            new AuthService(ctx, NullLogger<AuthService>.Instance, _clock),
            _clock,
            _db.OpenIddictTokens);

    private async Task<User> SeedActiveUserAsync(string email = "user@example.com", int id = 700)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            Id = id,
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            PasswordHash = "x",
            DisplayName = "Magic",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Magic_link_token_is_single_use_and_expires_after_fifteen_minutes()
    {
        await SeedActiveUserAsync();
        await using var ctx = _db.NewContext();
        var svc = NewSvc(ctx);

        var token = await svc.CreateMagicLoginTokenAsync("user@example.com", "1.2.3.4");
        token.Should().NotBeNull();

        var user = await svc.ConsumeMagicLoginTokenAsync(token!);
        user.Email.Should().Be("user@example.com");

        // Second use must fail.
        Func<Task> reuse = () => svc.ConsumeMagicLoginTokenAsync(token!);
        await reuse.Should().ThrowAsync<PlanValidationException>();
    }

    [Fact]
    public async Task Magic_link_opened_twice_at_once_signs_in_once()
    {
        // Two requests carrying the same link, both past the read before
        // either wrote (#1208).
        await SeedActiveUserAsync("twice@example.com", 701);
        string token;
        await using (var ctx = _db.NewContext())
        {
            token = (await NewSvc(ctx).CreateMagicLoginTokenAsync("twice@example.com", "1.2.3.4"))!;
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var ctx = _db.NewContext();
            try
            {
                await NewSvc(ctx).ConsumeMagicLoginTokenAsync(token);
                return true;
            }
            catch (PlanValidationException)
            {
                return false;
            }
        }));

        results.Count(r => r).Should().Be(1);
    }

    [Fact]
    public async Task Magic_link_token_expires_at_the_fifteen_minute_boundary()
    {
        await SeedActiveUserAsync();
        await using var ctx = _db.NewContext();
        var svc = NewSvc(ctx);

        var token = await svc.CreateMagicLoginTokenAsync("user@example.com", "1.2.3.4");
        token.Should().NotBeNull();

        _clock.Advance(PasswordResetService.MagicLinkTokenLifetime + TimeSpan.FromSeconds(1));

        Func<Task> act = () => svc.ConsumeMagicLoginTokenAsync(token!);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Token");
    }

    [Fact]
    public async Task Unknown_email_returns_null_so_response_is_opaque()
    {
        await using var ctx = _db.NewContext();
        var svc = NewSvc(ctx);

        var token = await svc.CreateMagicLoginTokenAsync("ghost@example.com", "1.2.3.4");
        token.Should().BeNull();
        // No token row should be persisted for unknown emails.
        (await ctx.PasswordResetTokens.AnyAsync(t => t.Purpose == TokenPurpose.MagicLogin)).Should().BeFalse();
    }

    [Fact]
    public async Task Disabled_user_does_not_receive_a_magic_link_token()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = 701,
                OrganizationId = TestDb.DefaultOrgId,
                Email = "off@example.com",
                PasswordHash = "x",
                DisplayName = "Off",
                Role = UserRole.User,
                Status = UserStatus.Disabled,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var svc = NewSvc(ctx);

        var token = await svc.CreateMagicLoginTokenAsync("off@example.com", "1.2.3.4");
        token.Should().BeNull();
    }

    [Fact]
    public async Task Magic_link_token_cannot_be_consumed_as_a_password_reset_token()
    {
        await SeedActiveUserAsync();
        await using var ctx = _db.NewContext();
        var svc = NewSvc(ctx);

        var token = await svc.CreateMagicLoginTokenAsync("user@example.com", "1.2.3.4");
        token.Should().NotBeNull();

        Func<Task> act = () => svc.ConsumePasswordResetTokenAsync(token!, "verylongnewpassword!");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Token");
    }

    [Fact]
    public async Task Password_reset_token_cannot_be_consumed_as_a_magic_link_token()
    {
        await SeedActiveUserAsync();
        await using var ctx = _db.NewContext();
        var svc = NewSvc(ctx);

        var token = await svc.CreatePasswordResetTokenAsync("user@example.com", "10.0.0.1");
        token.Should().NotBeNull();

        Func<Task> act = () => svc.ConsumeMagicLoginTokenAsync(token!);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Token");
    }

    [Fact]
    public async Task Per_email_rate_limit_blocks_more_than_ten_magic_link_requests_in_fifteen_minutes()
    {
        await SeedActiveUserAsync();
        await using var ctx = _db.NewContext();
        var svc = NewSvc(ctx);

        for (var i = 0; i < AuthService.MaxAttemptsPerEmail; i++)
        {
            var t = await svc.CreateMagicLoginTokenAsync("user@example.com", "1.2.3.4");
            t.Should().NotBeNull("attempt {0} is within the rate window", i);
        }

        // 11th attempt is rate-limited — returns null (opaque), no new token row.
        var blocked = await svc.CreateMagicLoginTokenAsync("user@example.com", "1.2.3.4");
        blocked.Should().BeNull();
        var rows = await ctx.PasswordResetTokens
            .Where(t => t.Purpose == TokenPurpose.MagicLogin)
            .CountAsync();
        rows.Should().Be(AuthService.MaxAttemptsPerEmail, "rate-limited requests must not persist tokens");
    }

    [Fact]
    public async Task Token_storage_is_hashed_for_both_purposes()
    {
        await SeedActiveUserAsync();
        await using var ctx = _db.NewContext();
        var svc = NewSvc(ctx);

        var resetToken = await svc.CreatePasswordResetTokenAsync("user@example.com", "10.0.0.1");
        var magicToken = await svc.CreateMagicLoginTokenAsync("user@example.com", "1.2.3.4");

        var rows = await ctx.PasswordResetTokens.IgnoreQueryFilters().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().NotContain(r => r.TokenHash == resetToken);
        rows.Should().NotContain(r => r.TokenHash == magicToken);
        rows.Should().AllSatisfy(r => r.TokenHash.Length.Should().Be(64, "hex SHA-256 is 64 characters"));
    }
}
