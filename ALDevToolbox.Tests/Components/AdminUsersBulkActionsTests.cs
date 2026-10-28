using ALDevToolbox.Components.Pages.Admin.Administration;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// <c>/admin/administration/users</c>, the bulk bar and the locked row: a bulk disable,
/// promote or demote asks first and names who it will touch, a batch where some rows are
/// refused says how many went through and why the rest did not, and a site
/// administrator's row offers a plain Admin nothing to change (#1438).
/// </summary>
public sealed class AdminUsersBulkActionsTests : IAsyncDisposable
{
    private const int AdaUserId = 9360;    // the Admin using the page
    private const int SophieUserId = 9361; // a site administrator in the same organisation
    private const int UnaUserId = 9362;
    private const int UlfUserId = 9363;

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public AdminUsersBulkActionsTests()
    {
        _db.OrgContext.CurrentUserId = AdaUserId;

        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("ada@cronus.test");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddSingleton(_db.OpenIddictTokens);
        _ctx.Services.AddScoped<UserAdministrationService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _ctx.Services.AddSingleton<IEmailService, NoEmail>();
        _ctx.Services.AddSingleton(_db.DataProtectionProvider);
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.Renderer.DisposeAsync();
        _db.WaitForQueriesToSettle();
        await _ctx.DisposeAsync();
        _db.Dispose();
    }

    private sealed class NoEmail : IEmailService
    {
        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task SendAsync(string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private async Task SeedAsync(UserRole unaAndUlf)
    {
        await using var seed = _db.NewContext();
        seed.Users.AddRange(
            NewUser(AdaUserId, "ada@cronus.test", "Ada Admin", UserRole.Admin),
            NewUser(SophieUserId, "sophie@cronus.test", "Sophie Site", UserRole.Admin, isSiteAdmin: true),
            NewUser(UnaUserId, "una@cronus.test", "Una User", unaAndUlf),
            NewUser(UlfUserId, "ulf@cronus.test", "Ulf User", unaAndUlf));
        await seed.SaveChangesAsync();
    }

    private static User NewUser(int id, string email, string name, UserRole role, bool isSiteAdmin = false) => new()
    {
        Id = id, OrganizationId = TestDb.DefaultOrgId, Email = email, PasswordHash = "x", DisplayName = name,
        Role = role, IsSiteAdmin = isSiteAdmin, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
    };

    private Dictionary<int, (UserStatus Status, UserRole Role)> Stored()
    {
        using var ctx = _db.NewContext();
        return ctx.Users.AsNoTracking().ToDictionary(u => u.Id, u => (u.Status, u.Role));
    }

    private IRenderedComponent<AdminAdministrationUsers> RenderLoaded()
    {
        var cut = _ctx.Render<AdminAdministrationUsers>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("ulf@cronus.test"));
        return cut;
    }

    private static void Select(IRenderedComponent<AdminAdministrationUsers> cut, params string[] names)
    {
        foreach (var name in names) cut.Find($"input[aria-label='Select {name}']").Change(true);
    }

    private static void Bulk(IRenderedComponent<AdminAdministrationUsers> cut, string action) =>
        cut.Find(".bulk-bar").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == action).Click();

    private static void Answer(IRenderedComponent<AdminAdministrationUsers> cut, string button) =>
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions .btn")
            .Single(b => b.TextContent.Trim() == button).Click());

    private static AngleSharp.Dom.IElement RowOf(IRenderedComponent<AdminAdministrationUsers> cut, string email) =>
        cut.FindAll("tbody tr").Single(r => r.TextContent.Contains(email));

    [Fact]
    public async Task A_site_administrators_row_offers_a_plain_admin_nothing_to_change()
    {
        await SeedAsync(UserRole.User);
        var cut = RenderLoaded();

        var sophie = RowOf(cut, "sophie@cronus.test");
        var una = RowOf(cut, "una@cronus.test");

        sophie.QuerySelector("select")!.HasAttribute("disabled").Should().BeTrue();
        sophie.QuerySelectorAll(".data-table__actions button").Should().BeEmpty(
            "disable, edit name and change email would all be refused for this account");
        una.QuerySelector("select")!.HasAttribute("disabled").Should().BeFalse();
        una.QuerySelectorAll(".data-table__actions button").Select(b => b.TextContent.Trim())
            .Should().Equal("Disable", "Edit name", "Change email");
    }

    [Fact]
    public async Task Bulk_disable_names_who_it_touches_and_does_nothing_until_confirmed()
    {
        await SeedAsync(UserRole.User);
        var cut = RenderLoaded();
        Select(cut, "Una User", "Ulf User");

        Bulk(cut, "Disable");
        cut.WaitForAssertion(() => cut.Find(".confirm-dialog__body").TextContent
            .Should().StartWith("This will disable: ").And.Contain("Una User").And.Contain("Ulf User"));
        Answer(cut, "Cancel");
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions").Should().BeEmpty());
        StillTrue.For(() => Stored()[UnaUserId].Status == UserStatus.Active && Stored()[UlfUserId].Status == UserStatus.Active,
            "saying no must leave both accounts working");

        Bulk(cut, "Disable");
        Answer(cut, "Disable");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Bulk disable applied to 2 user(s)."));
        Stored()[UnaUserId].Status.Should().Be(UserStatus.Disabled);
        Stored()[UlfUserId].Status.Should().Be(UserStatus.Disabled);
    }

    [Fact]
    public async Task Confirmed_bulk_promote_makes_both_admins()
    {
        await SeedAsync(UserRole.User);
        var cut = RenderLoaded();
        Select(cut, "Una User", "Ulf User");

        Bulk(cut, "Promote to Admin");
        Answer(cut, "Promote to Admin");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Bulk promote to Admin applied to 2 user(s)."));
        Stored()[UnaUserId].Role.Should().Be(UserRole.Admin);
        Stored()[UlfUserId].Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task A_bulk_demote_that_includes_the_site_administrator_reports_the_one_it_could_not_change()
    {
        await SeedAsync(UserRole.Admin);
        var cut = RenderLoaded();
        Select(cut, "Ulf User", "Sophie Site");

        Bulk(cut, "Demote to User");
        Answer(cut, "Demote to User");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Bulk demote to User applied to 1 of 2 user(s).");
            cut.Find("[role=alert] li").TextContent.Should().Be("Sophie Site: Only a site administrator can change this account.");
        });
        Stored()[UlfUserId].Role.Should().Be(UserRole.User);
        Stored()[SophieUserId].Role.Should().Be(UserRole.Admin);
    }
}
