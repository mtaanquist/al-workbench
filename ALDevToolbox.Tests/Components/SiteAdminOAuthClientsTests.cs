using ALDevToolbox.Components.Pages.SiteAdmin;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.OAuth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// <c>/site-admin/connections/oauth-clients</c>: "Remove for everyone" deletes the
/// assistant's registration and signs out every person in every organisation, so it
/// asks first, and dismissing leaves the registration and every consent alone (#1438).
/// </summary>
public sealed class SiteAdminOAuthClientsTests : IAsyncDisposable
{
    private const string ClientId = "claude-desktop";
    private const int SiteAdminUserId = 9330;
    private const int AliceUserId = 9331;
    private const int BobUserId = 9332;

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public SiteAdminOAuthClientsTests()
    {
        using (var seed = _db.NewContext())
        {
            seed.Users.AddRange(
                NewUser(SiteAdminUserId, TestDb.DefaultOrgId, "siteadmin@cronus.test", isSiteAdmin: true),
                NewUser(AliceUserId, TestDb.DefaultOrgId, "alice@cronus.test"),
                NewUser(BobUserId, TestDb.OtherOrgId, "bob@other.test"));
            seed.SaveChanges();
        }
        _db.OrgContext.CurrentUserId = SiteAdminUserId;
        _db.OrgContext.IsSiteAdmin = true;

        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("siteadmin@cronus.test");
        auth.SetRoles("Admin", "SiteAdmin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
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

    public async ValueTask DisposeAsync()
    {
        await _ctx.Renderer.DisposeAsync();
        _db.WaitForQueriesToSettle();
        await _ctx.DisposeAsync();
        _db.Dispose();
    }

    private static User NewUser(int id, int orgId, string email, bool isSiteAdmin = false) => new()
    {
        Id = id, OrganizationId = orgId, Email = email, PasswordHash = "x", DisplayName = email,
        Role = isSiteAdmin ? UserRole.Admin : UserRole.User, IsSiteAdmin = isSiteAdmin,
        Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
    };

    /// <summary>The registration, and one consent on it in each organisation.</summary>
    private async Task SeedClientUsedInTwoOrganisationsAsync()
    {
        await using (var scope = _ctx.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(
                new OpenIddictApplicationDescriptor
                {
                    ClientId = ClientId,
                    DisplayName = "Claude",
                    ClientType = OpenIddictConstants.ClientTypes.Public,
                    RedirectUris = { new Uri("https://claude.ai/api/mcp/auth_callback") },
                });
        }
        await using var seed = _db.NewContext();
        seed.OAuthConsents.AddRange(
            new OAuthConsent { UserId = AliceUserId, OrganizationId = TestDb.DefaultOrgId, ClientId = ClientId, ScopesGranted = "mcp", GrantedAt = DateTime.UtcNow },
            new OAuthConsent { UserId = BobUserId, OrganizationId = TestDb.OtherOrgId, ClientId = ClientId, ScopesGranted = "mcp", GrantedAt = DateTime.UtcNow });
        await seed.SaveChangesAsync();
    }

    private async Task<(bool Registered, int OpenConsents)> StoredAsync()
    {
        await using var scope = _ctx.Services.CreateAsyncScope();
        var registered = await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>()
            .FindByClientIdAsync(ClientId) is not null;
        await using var ctx = _db.NewContext();
        var open = await ctx.OAuthConsents.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(c => c.ClientId == ClientId && c.RevokedAt == null);
        return (registered, open);
    }

    private IRenderedComponent<SiteAdminOAuthClients> RenderAndAskToRemove()
    {
        var cut = _ctx.Render<SiteAdminOAuthClients>();
        cut.WaitForAssertion(() =>
        {
            // Both organisations' rows have loaded before one of them is clicked.
            cut.Markup.Should().Contain("alice@cronus.test").And.Contain("bob@other.test");
            cut.FindAll("button").First(b => b.TextContent.Trim() == "Remove for everyone").Click();
        });
        cut.WaitForAssertion(() => cut.Find(".confirm-dialog__body").TextContent.Should().Contain(
            "every person in every organisation"));
        return cut;
    }

    [Fact]
    public async Task Dismissing_remove_for_everyone_keeps_the_registration_and_every_consent()
    {
        await SeedClientUsedInTwoOrganisationsAsync();
        var cut = RenderAndAskToRemove();

        cut.FindAll(".confirm-dialog__actions .btn").Single(b => b.TextContent.Trim() == "Cancel").Click();

        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions").Should().BeEmpty());
        await StillTrue.ForAsync(async () => await StoredAsync() == (true, 2),
            "saying no must leave the registration and both organisations' consents alone");
    }

    [Fact]
    public async Task Confirming_removes_the_registration_and_signs_out_both_organisations()
    {
        await SeedClientUsedInTwoOrganisationsAsync();
        var cut = RenderAndAskToRemove();

        cut.FindAll(".confirm-dialog__actions .btn").Single(b => b.TextContent.Trim() == "Remove for everyone").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Assistant removed for everyone."));
        (await StoredAsync()).Should().Be((false, 0));
    }
}
