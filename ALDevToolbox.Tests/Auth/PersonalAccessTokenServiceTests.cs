using Microsoft.Extensions.DependencyInjection;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Behavioural tests for <see cref="PersonalAccessTokenService"/>:
/// plaintext is returned exactly once, only the SHA-256 hash is persisted,
/// expired / revoked / tampered tokens fail validation, and
/// <c>LastUsedAt</c> updates throttle to once a minute.
/// </summary>
public sealed class PersonalAccessTokenServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private async Task<User> SeedUserAsync(int orgId = TestDb.DefaultOrgId)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = orgId,
            Email = "alice@example.com",
            DisplayName = "Alice",
            PasswordHash = "ignored",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    private PersonalAccessTokenService NewService(Data.AppDbContext ctx) =>
        new(ctx, _clock, new CapturingEmailService(), new EmailRenderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance), NullLogger<PersonalAccessTokenService>.Instance);

    [Fact]
    public async Task Issue_returns_plaintext_and_persists_only_hash()
    {
        var user = await SeedUserAsync();

        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var issued = await svc.IssueAsync(user.Id, user.OrganizationId, "Cursor", expiresAt: null);

        issued.Plaintext.Should().StartWith(PersonalAccessTokenService.TokenPrefix);
        issued.Plaintext.Length.Should().BeGreaterThan(PersonalAccessTokenService.TokenPrefix.Length + 16);

        await using var verify = _db.NewContext();
        var row = await verify.PersonalAccessTokens.SingleAsync(p => p.Id == issued.Id);
        row.TokenHash.Should().NotBeEmpty();
        row.TokenHash.Should().NotContain(issued.Plaintext, "the persisted hash must not contain the plain-text token");
        row.TokenPrefix.Should().Be(issued.Plaintext[..12]);
        row.Scopes.Should().Be("mcp");
    }

    [Fact]
    public async Task Issue_with_empty_name_throws_field_keyed_validation()
    {
        var user = await SeedUserAsync();

        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await FluentActions.Awaiting(() => svc.IssueAsync(user.Id, user.OrganizationId, "   ", expiresAt: null))
            .Should().ThrowAsync<PlanValidationException>()
            .Where(ex => ex.Errors.ContainsKey("Name"));
    }

    [Fact]
    public async Task Issue_with_past_expiry_throws_field_keyed_validation()
    {
        var user = await SeedUserAsync();

        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var past = _clock.GetUtcNow().UtcDateTime.AddDays(-1);
        await FluentActions.Awaiting(() => svc.IssueAsync(user.Id, user.OrganizationId, "x", past))
            .Should().ThrowAsync<PlanValidationException>()
            .Where(ex => ex.Errors.ContainsKey("ExpiresAt"));
    }

    [Fact]
    public async Task Validate_resolves_active_token_to_principal()
    {
        var user = await SeedUserAsync();

        IssuedToken issued;
        await using (var ctx = _db.NewContext())
        {
            issued = await NewService(ctx).IssueAsync(user.Id, user.OrganizationId, "Cursor", expiresAt: null);
        }

        await using var verifyCtx = _db.NewContext();
        var principal = await NewService(verifyCtx).ValidateAsync(issued.Plaintext);

        principal.Should().NotBeNull();
        principal!.UserId.Should().Be(user.Id);
        principal.OrganizationId.Should().Be(user.OrganizationId);
        principal.Email.Should().Be(user.Email);
        principal.TokenId.Should().Be(issued.Id);
    }

    [Fact]
    public async Task Validate_rejects_revoked_token()
    {
        var user = await SeedUserAsync();
        IssuedToken issued;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            issued = await svc.IssueAsync(user.Id, user.OrganizationId, "x", expiresAt: null);
            await svc.RevokeAsync(issued.Id);
        }

        await using var verifyCtx = _db.NewContext();
        (await NewService(verifyCtx).ValidateAsync(issued.Plaintext)).Should().BeNull();
    }

    [Fact]
    public async Task Revoke_scoped_to_user_ignores_another_users_token()
    {
        // Two users in the same org. PAT rows are visible org-wide, so revoking
        // by id alone would let one member kill another's token (#375).
        var alice = await SeedUserAsync();
        User bob;
        await using (var seed = _db.NewContext())
        {
            bob = new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "bob@example.com",
                DisplayName = "Bob",
                PasswordHash = "ignored",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            };
            seed.Users.Add(bob);
            await seed.SaveChangesAsync();
        }

        IssuedToken aliceToken;
        await using (var ctx = _db.NewContext())
        {
            aliceToken = await NewService(ctx).IssueAsync(alice.Id, alice.OrganizationId, "Cursor", expiresAt: null);
        }

        // Bob tries to revoke Alice's token, scoped to his own id — no-op.
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeAsync(aliceToken.Id, ignoreOrgScope: false, forUserId: bob.Id);
        }
        await using (var verify = _db.NewContext())
        {
            (await verify.PersonalAccessTokens.SingleAsync(p => p.Id == aliceToken.Id)).RevokedAt
                .Should().BeNull("Bob must not be able to revoke Alice's token");
        }

        // Alice revoking her own token works.
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeAsync(aliceToken.Id, ignoreOrgScope: false, forUserId: alice.Id);
        }
        await using (var verify = _db.NewContext())
        {
            (await verify.PersonalAccessTokens.SingleAsync(p => p.Id == aliceToken.Id)).RevokedAt
                .Should().NotBeNull("the owner can revoke their own token");
        }
    }

    [Fact]
    public async Task Validate_rejects_expired_token()
    {
        var user = await SeedUserAsync();
        IssuedToken issued;
        await using (var ctx = _db.NewContext())
        {
            issued = await NewService(ctx).IssueAsync(
                user.Id, user.OrganizationId, "x", _clock.GetUtcNow().UtcDateTime.AddMinutes(5));
        }

        _clock.Advance(TimeSpan.FromHours(1));
        await using var verifyCtx = _db.NewContext();
        (await NewService(verifyCtx).ValidateAsync(issued.Plaintext)).Should().BeNull();
    }

    [Fact]
    public async Task Validate_rejects_tampered_token()
    {
        var user = await SeedUserAsync();
        IssuedToken issued;
        await using (var ctx = _db.NewContext())
        {
            issued = await NewService(ctx).IssueAsync(user.Id, user.OrganizationId, "x", expiresAt: null);
        }

        var tampered = issued.Plaintext[..^4] + "ZZZZ";
        await using var verifyCtx = _db.NewContext();
        (await NewService(verifyCtx).ValidateAsync(tampered)).Should().BeNull();
    }

    [Fact]
    public async Task Validate_rejects_token_without_pat_prefix()
    {
        await using var ctx = _db.NewContext();
        (await NewService(ctx).ValidateAsync("not-a-pat-token")).Should().BeNull();
        (await NewService(ctx).ValidateAsync(string.Empty)).Should().BeNull();
    }

    [Fact]
    public async Task Validate_rejects_token_for_disabled_user()
    {
        var user = await SeedUserAsync();
        IssuedToken issued;
        await using (var ctx = _db.NewContext())
        {
            issued = await NewService(ctx).IssueAsync(user.Id, user.OrganizationId, "x", expiresAt: null);
        }

        await using (var disable = _db.NewContext())
        {
            var row = await disable.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == user.Id);
            row.Status = UserStatus.Disabled;
            await disable.SaveChangesAsync();
        }

        await using var verifyCtx = _db.NewContext();
        (await NewService(verifyCtx).ValidateAsync(issued.Plaintext)).Should().BeNull();
    }

    [Fact]
    public async Task Validate_writes_last_used_at_once_within_throttle_window()
    {
        var user = await SeedUserAsync();
        IssuedToken issued;
        await using (var ctx = _db.NewContext())
        {
            issued = await NewService(ctx).IssueAsync(user.Id, user.OrganizationId, "x", expiresAt: null);
        }

        var t0 = _clock.GetUtcNow().UtcDateTime;
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ValidateAsync(issued.Plaintext);
        }

        DateTime? lastUsedAfterFirst;
        await using (var ctx = _db.NewContext())
        {
            lastUsedAfterFirst = (await ctx.PersonalAccessTokens.SingleAsync(p => p.Id == issued.Id)).LastUsedAt;
        }
        lastUsedAfterFirst.Should().Be(t0);

        // Second call within throttle window — LastUsedAt should not advance.
        _clock.Advance(TimeSpan.FromSeconds(10));
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ValidateAsync(issued.Plaintext);
        }

        await using (var ctx = _db.NewContext())
        {
            var row = await ctx.PersonalAccessTokens.SingleAsync(p => p.Id == issued.Id);
            row.LastUsedAt.Should().Be(t0, "second call was inside the 60-second throttle window");
        }

        // Past the throttle window — LastUsedAt should now move.
        _clock.Advance(TimeSpan.FromMinutes(2));
        var t2 = _clock.GetUtcNow().UtcDateTime;
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ValidateAsync(issued.Plaintext);
        }

        await using (var ctx = _db.NewContext())
        {
            var row = await ctx.PersonalAccessTokens.SingleAsync(p => p.Id == issued.Id);
            row.LastUsedAt.Should().Be(t2);
        }
    }

    [Fact]
    public async Task Revoke_is_idempotent_and_stamps_revoked_at_once()
    {
        var user = await SeedUserAsync();
        IssuedToken issued;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            issued = await svc.IssueAsync(user.Id, user.OrganizationId, "x", expiresAt: null);
            await svc.RevokeAsync(issued.Id);
        }
        var t0 = _clock.GetUtcNow().UtcDateTime;

        _clock.Advance(TimeSpan.FromMinutes(5));
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeAsync(issued.Id);
        }

        await using var verify = _db.NewContext();
        var row = await verify.PersonalAccessTokens.SingleAsync(p => p.Id == issued.Id);
        row.RevokedAt.Should().Be(t0, "the second revoke must not overwrite the original stamp");
    }

    [Fact]
    public async Task ListForUser_returns_tokens_newest_first()
    {
        var user = await SeedUserAsync();
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            await svc.IssueAsync(user.Id, user.OrganizationId, "first", expiresAt: null);
            _clock.Advance(TimeSpan.FromMinutes(1));
            await svc.IssueAsync(user.Id, user.OrganizationId, "second", expiresAt: null);
        }

        await using var ctx2 = _db.NewContext();
        var rows = await NewService(ctx2).ListForUserAsync(user.Id);

        rows.Should().HaveCount(2);
        rows[0].Name.Should().Be("second");
        rows[1].Name.Should().Be("first");
    }

    [Fact]
    public async Task Tokens_are_isolated_to_their_organisation_via_query_filter()
    {
        var alice = await SeedUserAsync(TestDb.DefaultOrgId);
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).IssueAsync(alice.Id, alice.OrganizationId, "alice-token", expiresAt: null);
        }

        // Switch the ambient org context to the other org — Alice's token must vanish.
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        await using var ctx2 = _db.NewContext();
        var visible = await ctx2.PersonalAccessTokens.ToListAsync();
        visible.Should().BeEmpty();

        // ListAllAsync (SiteAdmin path) bypasses the filter.
        var all = await NewService(ctx2).ListAllAsync();
        all.Should().ContainSingle(t => t.Name == "alice-token");

        // Restore for any later tests that share the fixture.
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
    }
}
