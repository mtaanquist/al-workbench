using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.OAuth;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;

namespace ALDevToolbox.Tests.OAuth;

/// <summary>
/// <see cref="OAuthClientAdminService.RevokeConsentAsync"/> has three modes —
/// self-service, org admin, SiteAdmin — and only the last reads the consent
/// table with <c>IgnoreQueryFilters()</c> (#1337); the first two stay inside
/// the tenant filter, re-check scope by hand, and refuse silently. A broken guard means a user revoking someone else's consent just
/// works, with nothing but a log line to show for it. See issue #667.
/// </summary>
public sealed class OAuthClientAdminServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _openIddict;

    public OAuthClientAdminServiceTests()
    {
        var db = _db;
        _openIddict = new ServiceCollection()
            .AddLogging()
            .AddScoped(_ => db.NewContext())
            .AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AppDbContext>())
            .Services
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _openIddict.Dispose();
        _db.Dispose();
    }

    private OAuthClientAdminService NewService(AppDbContext ctx) =>
        new(_openIddict.GetRequiredService<IOpenIddictApplicationManager>(),
            _openIddict.GetRequiredService<IOpenIddictTokenManager>(),
            ctx,
            _clock,
            NullLogger<OAuthClientAdminService>.Instance);

    private async Task<User> SeedUserAsync(int organizationId, string email)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = organizationId,
            Email = email,
            DisplayName = email,
            PasswordHash = "ignored",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    private async Task<OAuthConsent> SeedConsentAsync(User user, string clientId = "claude-desktop")
    {
        await using var ctx = _db.NewContext();
        var consent = new OAuthConsent
        {
            UserId = user.Id,
            OrganizationId = user.OrganizationId,
            ClientId = clientId,
            ScopesGranted = "mcp",
            GrantedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.OAuthConsents.Add(consent);
        await ctx.SaveChangesAsync();
        return consent;
    }

    private async Task<DateTime?> RevokedAtAsync(int consentId)
    {
        await using var ctx = _db.NewContext();
        return (await ctx.OAuthConsents.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(c => c.Id == consentId)).RevokedAt;
    }

    [Fact]
    public async Task Self_service_revoke_of_your_own_consent_stamps_revoked_at()
    {
        var user = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var consent = await SeedConsentAsync(user);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeConsentAsync(consent.Id, actorUserId: user.Id);
        }

        (await RevokedAtAsync(consent.Id)).Should().Be(_clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Self_service_revoke_of_someone_elses_consent_is_refused()
    {
        var owner = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var attacker = await SeedUserAsync(TestDb.DefaultOrgId, "mallory@cronus.test");
        var consent = await SeedConsentAsync(owner);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeConsentAsync(consent.Id, actorUserId: attacker.Id);
        }

        (await RevokedAtAsync(consent.Id)).Should().BeNull("the refusal is silent, so the row is the only evidence");
    }

    [Fact]
    public async Task Org_admin_revokes_a_consent_inside_their_own_org()
    {
        var member = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var admin = await SeedUserAsync(TestDb.DefaultOrgId, "admin@cronus.test");
        var consent = await SeedConsentAsync(member);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeConsentAsync(
                consent.Id, actorUserId: admin.Id, ignoreOrgScope: true, expectedOrganizationId: TestDb.DefaultOrgId);
        }

        (await RevokedAtAsync(consent.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Org_admin_cannot_revoke_a_consent_in_another_org()
    {
        var stranger = await SeedUserAsync(TestDb.OtherOrgId, "bob@other.test");
        var admin = await SeedUserAsync(TestDb.DefaultOrgId, "admin@cronus.test");
        var consent = await SeedConsentAsync(stranger);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeConsentAsync(
                consent.Id, actorUserId: admin.Id, ignoreOrgScope: true, expectedOrganizationId: TestDb.DefaultOrgId);
        }

        (await RevokedAtAsync(consent.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Site_admin_mode_revokes_a_consent_in_any_org()
    {
        var stranger = await SeedUserAsync(TestDb.OtherOrgId, "bob@other.test");
        var siteAdmin = await SeedUserAsync(TestDb.DefaultOrgId, "root@cronus.test");
        var consent = await SeedConsentAsync(stranger);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeConsentAsync(
                consent.Id, actorUserId: siteAdmin.Id, ignoreOrgScope: true, expectedOrganizationId: null);
        }

        (await RevokedAtAsync(consent.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Re_revoking_an_already_revoked_consent_is_a_no_op()
    {
        var user = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var consent = await SeedConsentAsync(user);
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeConsentAsync(consent.Id, actorUserId: user.Id);
        }
        var firstStamp = await RevokedAtAsync(consent.Id);

        _clock.Advance(TimeSpan.FromHours(3));
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeConsentAsync(consent.Id, actorUserId: user.Id);
        }

        (await RevokedAtAsync(consent.Id)).Should().Be(firstStamp, "the original revocation time is the audit record");
    }

    [Fact]
    public async Task Revoking_an_unknown_consent_id_is_a_no_op()
    {
        var user = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");

        await using var ctx = _db.NewContext();
        var act = async () => await NewService(ctx).RevokeConsentAsync(999999, actorUserId: user.Id);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task List_for_user_returns_only_that_users_consents()
    {
        var alice = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var bob = await SeedUserAsync(TestDb.DefaultOrgId, "bob@cronus.test");
        var aliceConsent = await SeedConsentAsync(alice);
        await SeedConsentAsync(bob, "cursor");

        await using var ctx = _db.NewContext();
        var rows = await NewService(ctx).ListForUserAsync(alice.Id);

        rows.Should().ContainSingle().Which.ConsentId.Should().Be(aliceConsent.Id);
        // No OAuth application is registered for the client id, so the join
        // falls back to the placeholder rather than dropping the row.
        rows[0].DisplayName.Should().Be("Deleted client");
    }

    [Fact]
    public async Task List_for_organization_returns_only_that_orgs_consents()
    {
        var alice = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var stranger = await SeedUserAsync(TestDb.OtherOrgId, "bob@other.test");
        var aliceConsent = await SeedConsentAsync(alice);
        await SeedConsentAsync(stranger, "cursor");

        await using var ctx = _db.NewContext();
        var rows = await NewService(ctx).ListForOrganizationAsync(TestDb.DefaultOrgId);

        rows.Should().ContainSingle().Which.ConsentId.Should().Be(aliceConsent.Id);
        rows[0].UserEmail.Should().Be("alice@cronus.test");
    }

    [Fact]
    public async Task List_all_spans_every_org()
    {
        var alice = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var stranger = await SeedUserAsync(TestDb.OtherOrgId, "bob@other.test");
        await SeedConsentAsync(alice);
        await SeedConsentAsync(stranger, "cursor");

        await using var ctx = _db.NewContext();
        var rows = await NewService(ctx).ListAllAsync();

        rows.Select(r => r.OrganizationId).Should().BeEquivalentTo(new[] { TestDb.DefaultOrgId, TestDb.OtherOrgId });
    }

    [Fact]
    public async Task Revoking_a_consent_revokes_every_token_the_user_holds_for_that_client_and_no_other()
    {
        // One set-based revoke over (subject, client) replaced a walk over
        // every token the user holds (#1259); it must still reach them all.
        var user = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var other = await SeedUserAsync(TestDb.DefaultOrgId, "bob@cronus.test");
        var consent = await SeedConsentAsync(user);
        var claudeApp = await CreateClientAsync("claude-desktop");
        var cursorApp = await CreateClientAsync("cursor");
        var mine = new List<string>();
        for (var i = 0; i < 3; i++) mine.Add(await CreateTokenAsync(user.Id, claudeApp, _clock.GetUtcNow()));
        var otherClient = await CreateTokenAsync(user.Id, cursorApp, _clock.GetUtcNow());
        var otherUser = await CreateTokenAsync(other.Id, claudeApp, _clock.GetUtcNow());

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeConsentAsync(consent.Id, actorUserId: user.Id);
        }

        var statuses = await TokenStatusesAsync();
        mine.Should().AllSatisfy(id => statuses[id].Should().Be(OpenIddictConstants.Statuses.Revoked));
        statuses[otherClient].Should().Be(OpenIddictConstants.Statuses.Valid);
        statuses[otherUser].Should().Be(OpenIddictConstants.Statuses.Valid);
    }

    [Fact]
    public async Task The_prune_removes_dead_tokens_past_the_retention_window_and_keeps_the_rest()
    {
        var user = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var app = await CreateClientAsync("claude-desktop");
        var now = DateTimeOffset.UtcNow;
        var old = now - OAuthTokenPruneScheduler.RetentionWindow - TimeSpan.FromDays(1);
        var expired = await CreateTokenAsync(user.Id, app, old, expiresAt: old + TimeSpan.FromHours(1));
        var revoked = await CreateTokenAsync(user.Id, app, old, status: OpenIddictConstants.Statuses.Revoked);
        var recentRevoked = await CreateTokenAsync(user.Id, app, now - TimeSpan.FromDays(1), status: OpenIddictConstants.Statuses.Revoked);
        var live = await CreateTokenAsync(user.Id, app, old, expiresAt: now + TimeSpan.FromDays(30));

        var (tokens, _) = await OAuthTokenPruneScheduler.PruneAsync(
            _openIddict.GetRequiredService<IOpenIddictTokenManager>(),
            _openIddict.GetRequiredService<IOpenIddictAuthorizationManager>(),
            now - OAuthTokenPruneScheduler.RetentionWindow, default);

        tokens.Should().Be(2);
        var left = (await TokenStatusesAsync()).Keys;
        left.Should().BeEquivalentTo(new[] { recentRevoked, live });
        left.Should().NotContain(new[] { expired, revoked });
    }

    private async Task<string> CreateClientAsync(string clientId)
    {
        var apps = _openIddict.GetRequiredService<IOpenIddictApplicationManager>();
        var app = await apps.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = OpenIddictConstants.ClientTypes.Public,
        });
        return (await apps.GetIdAsync(app))!;
    }

    private async Task<string> CreateTokenAsync(
        int userId, string applicationId, DateTimeOffset createdAt,
        string status = OpenIddictConstants.Statuses.Valid, DateTimeOffset? expiresAt = null)
    {
        var tokens = _openIddict.GetRequiredService<IOpenIddictTokenManager>();
        var token = await tokens.CreateAsync(new OpenIddictTokenDescriptor
        {
            Subject = userId.ToString(),
            ApplicationId = applicationId,
            Type = OpenIddictConstants.TokenTypeHints.RefreshToken,
            Status = status,
            CreationDate = createdAt,
            ExpirationDate = expiresAt ?? createdAt + TimeSpan.FromDays(60),
        });
        return (await tokens.GetIdAsync(token))!;
    }

    private async Task<Dictionary<string, string?>> TokenStatusesAsync()
    {
        await using var ctx = _db.NewContext();
        return await ctx.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>()
            .AsNoTracking()
            .ToDictionaryAsync(t => t.Id!, t => t.Status);
    }
}
