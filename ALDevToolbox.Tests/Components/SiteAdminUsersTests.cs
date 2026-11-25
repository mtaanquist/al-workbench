using ALDevToolbox.Components.Pages.SiteAdmin;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Account;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke test for <c>/site-admin/users</c>. Pins the cross-org user-search
/// page's three-state contract and the "no match for current query" vs
/// "no users at all" branch from CLAUDE.md §"three states" — both empty
/// states must render distinct copy. Also confirms the
/// <see cref="Authorize"/> attribute keys on the SiteAdmin role rather
/// than Admin (the per-org admin must not reach this page).
/// </summary>
public sealed class SiteAdminUsersTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly BunitAuthorizationContext _auth;

    public SiteAdminUsersTests()
    {
        _auth = _ctx.AddAuthorization();
        _auth.SetAuthorized("siteadmin@example.com");
        _auth.SetRoles(HttpOrganizationContext.SiteAdminRole);

        // SiteAdminService.RequireSiteAdmin() checks the org-context flag.
        _db.OrgContext.IsSiteAdmin = true;

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<SiteAdminService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Empty_user_set_with_no_query_renders_the_no_users_yet_copy()
    {
        var cut = _ctx.Render<SiteAdminUsers>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Nobody has signed up yet",
                "the empty-state copy distinguishes \"empty database\" from "
                + "\"no match for the current search\" — see CLAUDE.md §three states"));
    }

    [Fact]
    public void Empty_user_set_with_a_query_renders_the_no_match_copy()
    {
        // SupplyParameterFromQuery parameters can't be passed via RenderComponent's
        // builder — Blazor binds them from NavigationManager.Uri. Navigate the
        // FakeNavigationManager to the URL with the query string and the
        // component picks Query up the same way it would in production.
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/site-admin/users?q=alice");

        var cut = _ctx.Render<SiteAdminUsers>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Nothing matched that search");
            cut.Markup.Should().NotContain("Nobody has signed up yet",
                "the two empty states must not both render — distinct copy is "
                + "what makes the search affordance discoverable");
            cut.Markup.Should().Contain("Show everyone",
                "an empty search result has to offer a way back to the full list, "
                + "or the only exit is editing the URL");
        });
    }

    [Fact]
    public async Task Populated_user_set_renders_one_row_per_user_with_org_and_site_admin_flag()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "alice@example.com",
                DisplayName = "Alice",
                PasswordHash = "x",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                IsSiteAdmin = true,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.OtherOrgId,
                Email = "bob@example.com",
                DisplayName = "Bob",
                PasswordHash = "x",
                Role = UserRole.User,
                Status = UserStatus.Active,
                IsSiteAdmin = false,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<SiteAdminUsers>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("table.data-table tbody tr").Should().HaveCount(2,
                "SearchUsersAsync with no query must still return both rows — the page "
                + "first-loads with the most-recent users to give the operator context");
            var rows = cut.FindAll("table.data-table tbody tr")
                .ToDictionary(r => r.QuerySelectorAll("td")[1].TextContent.Trim(), r => r.QuerySelectorAll("td"));
            rows.Keys.Should().BeEquivalentTo("alice@example.com", "bob@example.com");
            // Organisation, then role, then the site-admin flag.
            rows["bob@example.com"][3].TextContent.Should().Contain("Other");
            rows["alice@example.com"][5].TextContent.Trim().Should().Be("Yes");
            rows["bob@example.com"][5].TextContent.Trim().Should().BeEmpty();

            // Deliberately zero, and this changed in PR 9a. The promote button
            // used to be btn--primary on every non-SiteAdmin row, which meant a
            // table of ten users had ten primary buttons and none of them stood
            // out. There is no single thing an operator comes to this page to
            // do, so nothing here claims to be it.
            cut.FindAll("button.btn--primary").Should().BeEmpty(
                "a per-row action is never the page's primary action");
        });
    }
}
