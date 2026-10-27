using ALDevToolbox.Components.Pages.Admin.Administration;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.OAuth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// <c>/admin/administration/oauth-clients</c>: an org Admin revokes a colleague's
/// assistant only after confirming, as on the site-admin page (#1431), and never sees
/// or reaches another organisation's consents.
/// </summary>
public sealed class AdminAdministrationOAuthClientsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    private const int AdminUserId = 9310;
    private const int MemberUserId = 9311;
    private const int OtherOrgUserId = 9312;

    public AdminAdministrationOAuthClientsTests()
    {
        using (var seed = _db.NewContext())
        {
            seed.Users.AddRange(
                NewUser(AdminUserId, TestDb.DefaultOrgId, "admin@cronus.test", UserRole.Admin),
                NewUser(MemberUserId, TestDb.DefaultOrgId, "alice@cronus.test", UserRole.User),
                NewUser(OtherOrgUserId, TestDb.OtherOrgId, "bob@other.test", UserRole.User));
            seed.SaveChanges();
        }
        _db.OrgContext.CurrentUserId = AdminUserId;

        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("admin@cronus.test");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AppDbContext>());
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddScoped<OAuthClientAdminService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private static User NewUser(int id, int orgId, string email, UserRole role) => new()
    {
        Id = id,
        OrganizationId = orgId,
        Email = email,
        PasswordHash = "x",
        DisplayName = email,
        Role = role,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };

    private async Task<int> SeedConsentAsync(int userId, int orgId)
    {
        await using var ctx = _db.NewContext();
        var consent = new OAuthConsent
        {
            UserId = userId,
            OrganizationId = orgId,
            ClientId = "claude-desktop",
            ScopesGranted = "mcp",
            GrantedAt = DateTime.UtcNow,
        };
        ctx.OAuthConsents.Add(consent);
        await ctx.SaveChangesAsync();
        return consent.Id;
    }

    private DateTime? RevokedAt(int consentId)
    {
        using var ctx = _db.NewContext();
        return ctx.OAuthConsents.IgnoreQueryFilters().AsNoTracking().Single(c => c.Id == consentId).RevokedAt;
    }

    [Fact]
    public async Task Revoke_asks_first_and_dismissing_leaves_the_assistant_connected()
    {
        var consentId = await SeedConsentAsync(MemberUserId, TestDb.DefaultOrgId);
        var cut = _ctx.Render<AdminAdministrationOAuthClients>();

        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Revoke").Click());
        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__body").TextContent.Should().Contain("alice@cronus.test");
            cut.FindAll(".confirm-dialog__actions .btn").First(b => b.TextContent.Trim() == "Cancel").Click();
        });

        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions").Should().BeEmpty());
        RevokedAt(consentId).Should().BeNull("dismissing the question must not sign anyone out");
    }

    [Fact]
    public async Task Confirming_revokes_the_consent()
    {
        var consentId = await SeedConsentAsync(MemberUserId, TestDb.DefaultOrgId);
        var cut = _ctx.Render<AdminAdministrationOAuthClients>();

        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Revoke").Click());
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions .btn--danger").Single().Click());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Consent revoked."));
        RevokedAt(consentId).Should().NotBeNull();
    }

    [Fact]
    public async Task Another_organisations_consent_is_not_listed()
    {
        await SeedConsentAsync(MemberUserId, TestDb.DefaultOrgId);
        await SeedConsentAsync(OtherOrgUserId, TestDb.OtherOrgId);
        var cut = _ctx.Render<AdminAdministrationOAuthClients>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("alice@cronus.test", "the page has loaded its own organisation's row");
            cut.Markup.Should().NotContain("bob@other.test");
        });
    }
}
