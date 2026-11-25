using System.Security.Claims;
using ALDevToolbox.Components.Layout;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Mcp;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the role / system-org branching in the sidebar. The component's own
/// header comment calls out four moving parts (Admin vs SiteAdmin, system
/// vs non-system org) that combine to a 2×2 visibility matrix — this test
/// covers all four corners.
///
/// <para>The sidebar is no longer DB-free: the Upgrades entry appears for
/// whoever may run Business Central update actions, and that grant is a
/// per-team flag deliberately kept out of the sign-in claims, so the component
/// has to ask <see cref="ProjectAccess"/> (issue #657). Hence the
/// <see cref="TestDb"/> here — the rest of the sidebar still branches on claims
/// alone.</para>
/// </summary>
public sealed class NavMenuTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly AmbientOrganizationContext _orgCtx = new();
    private readonly BunitAuthorizationContext _auth;
    private readonly FakeMcpAvailability _mcpAvailability = new();
    private readonly FakeToolAvailability _tools = new();
    private readonly FakeSingleTenantMode _singleTenant = new();

    private const int FlagUserId = 9600;
    private const int PlainUserId = 9601;

    public NavMenuTests()
    {
        _auth = _ctx.AddAuthorization();
        _ctx.Services.AddSingleton<IOrganizationContext>(_orgCtx);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton<IMcpAvailability>(_mcpAvailability);
        _ctx.Services.AddSingleton<IToolAvailability>(_tools);
        _ctx.Services.AddSingleton<ISingleTenantMode>(_singleTenant);
        // The nav reads the collapsed-groups cookie off the request so the first
        // render already has the right groups shut. No cookie here means every
        // group renders expanded, which is the default the tests assert against.
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _orgCtx.CurrentOrganizationId = TestDb.DefaultOrgId;
    }

    private sealed class FakeMcpAvailability : IMcpAvailability
    {
        public bool Enabled { get; set; }
        public bool IsEnabled => Enabled;
    }

    /// <summary>Every tool on by default; tests add keys to <see cref="Disabled"/> to switch one off site-wide.</summary>
    private sealed class FakeToolAvailability : IToolAvailability
    {
        public HashSet<ToolKey> Disabled { get; } = new();
        public bool IsSiteEnabled(ToolKey key) => !Disabled.Contains(key);
    }

    private sealed class FakeSingleTenantMode : ISingleTenantMode
    {
        public bool Enabled { get; set; }
        public bool IsEnabled => Enabled;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    /// <summary>
    /// Seeds a user, and (when <paramref name="managesUpdates"/>) a team membership
    /// carrying the environment-updates flag — the only thing that puts Upgrades in
    /// the sidebar for a non-admin.
    /// </summary>
    private async Task SeedUserAsync(int userId, string email, bool managesUpdates)
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new ALDevToolbox.Domain.Entities.User
        {
            Id = userId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            PasswordHash = "x",
            DisplayName = email,
            Role = ALDevToolbox.Domain.Entities.UserRole.User,
            Status = ALDevToolbox.Domain.Entities.UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        if (!managesUpdates) return;

        var team = new ALDevToolbox.Domain.Entities.Team
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "Upgrade team",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Teams.Add(team);
        await ctx.SaveChangesAsync();
        ctx.TeamMembers.Add(new ALDevToolbox.Domain.Entities.TeamMember
        {
            OrganizationId = TestDb.DefaultOrgId,
            TeamId = team.Id,
            UserId = userId,
            ManagesUpdates = true,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Upgrades_shows_for_someone_who_may_manage_environment_updates()
    {
        await SeedUserAsync(FlagUserId, "upgrade@example.com", managesUpdates: true);
        _orgCtx.CurrentUserId = FlagUserId;
        _auth.SetAuthorized("upgrade@example.com");

        var cut = _ctx.Render<NavMenu>();

        // The membership read is a round trip, so the entry arrives in a later render
        // than the rest of the sidebar. (In a real request the renderer waits for
        // quiescence before it emits any HTML, so nothing appears late on screen.)
        cut.WaitForAssertion(() => cut.FindAll("a[href='/upgrades']").Should().NotBeEmpty(
            "the grant is a per-team flag, so the sidebar asks the access service rather than a claim"));
    }

    [Fact]
    public async Task Upgrades_is_hidden_from_someone_without_the_grant()
    {
        await SeedUserAsync(PlainUserId, "pat@example.com", managesUpdates: false);
        _orgCtx.CurrentUserId = PlainUserId;
        _auth.SetAuthorized("pat@example.com");

        var cut = _ctx.Render<NavMenu>();

        cut.FindAll("a[href='/upgrades']").Should().BeEmpty();
    }

    [Fact]
    public void Upgrades_is_hidden_from_an_anonymous_visitor()
    {
        _auth.SetNotAuthorized();

        var cut = _ctx.Render<NavMenu>();

        cut.FindAll("a[href='/upgrades']").Should().BeEmpty();
    }

    [Fact]
    public void Anonymous_user_sees_only_the_tools_section()
    {
        _auth.SetNotAuthorized();

        var cut = _ctx.Render<NavMenu>();

        cut.Markup.Should().Contain("Solutions",
            "the Tools section is rendered to every visitor — it's outside the AuthorizeView");
        cut.FindAll("a[href='/admin']").Should().BeEmpty(
            "the Admin section is gated by AuthorizeView Roles=\"Admin\"");
        cut.FindAll("a[href^='/site-admin/']").Should().BeEmpty();
    }

    [Fact]
    public void Mcp_link_hidden_when_site_toggle_says_off()
    {
        _auth.SetAuthorized("user@example.com");
        _tools.Disabled.Add(ToolKey.Mcp);

        var cut = _ctx.Render<NavMenu>();
        cut.FindAll("a[href='/tools/mcp']").Should().BeEmpty();
    }

    [Fact]
    public void Mcp_link_hidden_for_anonymous_even_when_availability_is_on()
    {
        _auth.SetNotAuthorized();
        // Every tool on site-wide, but MCP still requires a signed-in user.

        var cut = _ctx.Render<NavMenu>();
        cut.FindAll("a[href='/tools/mcp']").Should().BeEmpty(
            "MCP is meaningless to an anonymous visitor, so it stays hidden");
    }

    [Fact]
    public void Mcp_link_shows_for_signed_in_user_when_availability_is_on()
    {
        _auth.SetAuthorized("user@example.com");

        var cut = _ctx.Render<NavMenu>();
        cut.FindAll("a[href='/tools/mcp']").Should().ContainSingle();
    }

    [Fact]
    public void Tool_hidden_when_disabled_site_wide()
    {
        _auth.SetAuthorized("user@example.com");
        _tools.Disabled.Add(ToolKey.Projects);

        var cut = _ctx.Render<NavMenu>();

        cut.FindAll("a[href='/solutions']").Should().BeEmpty(
            "a tool switched off site-wide leaves the sidebar for everyone");
        cut.FindAll("a[href='/piper']").Should().NotBeEmpty(
            "other tools are unaffected");
    }

    [Fact]
    public void Builds_and_Deployments_sit_under_a_Pipelines_parent_that_links_to_the_dashboard()
    {
        _auth.SetAuthorized("user@example.com");

        var cut = _ctx.Render<NavMenu>();

        var parent = cut.FindAll(".nav-parent").Single(p => p.QuerySelector("a[href='/pipelines/builds']") is not null);
        var head = parent.QuerySelector(":scope > a.nav-item")!;
        head.GetAttribute("href").Should().Be("/pipelines", "the parent is the Pipelines dashboard (#955)");
        head.TextContent.Trim().Should().Be("Pipelines");
        parent.QuerySelectorAll(".nav-sub a").Select(a => (a.GetAttribute("href"), a.TextContent.Trim()))
            .Should().Equal(("/pipelines/builds", "Builds"), ("/pipelines/deployments", "Deployments"));
        cut.FindAll("a[href='/releases']").Should().BeEmpty();
    }

    [Fact]
    public void On_the_dashboard_the_Pipelines_parent_is_the_current_page()
    {
        _auth.SetAuthorized("user@example.com");
        _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/pipelines");

        var cut = _ctx.Render<NavMenu>();

        cut.Find("a.nav-item[href='/pipelines']").ClassList.Should().Contain("is-active");
        cut.FindAll(".nav-sub a.is-active").Should().BeEmpty("neither list is open");
        cut.FindAll(".nav-parent.has-active-child").Should().BeEmpty();
    }

    [Theory]
    [InlineData("/pipelines/builds", "/pipelines/builds")]
    [InlineData("/pipelines/7", "/pipelines/builds")]
    [InlineData("/pipelines/deployments/3", "/pipelines/deployments")]
    public void The_child_that_holds_the_current_page_is_the_active_one(string at, string activeHref)
    {
        _auth.SetAuthorized("user@example.com");
        _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(at);

        var cut = _ctx.Render<NavMenu>();

        cut.FindAll(".nav-sub a.is-active").Select(a => a.GetAttribute("href")).Should().Equal(activeHref);
        cut.Find("a.nav-item[href='/pipelines']").ClassList.Should().NotContain("is-active",
            "the dashboard is not the page being shown");
        cut.FindAll(".nav-parent.has-active-child a.nav-item[href='/pipelines']").Should().ContainSingle(
            "the parent carries the current page by weight while one of its children is open");
    }

    [Fact]
    public void The_Pipelines_parent_stays_while_either_child_is_on_and_goes_with_both()
    {
        _auth.SetAuthorized("user@example.com");
        _tools.Disabled.Add(ToolKey.Pipelines);

        var withDeployments = _ctx.Render<NavMenu>();
        withDeployments.FindAll("a[href='/pipelines']").Should().ContainSingle();
        withDeployments.FindAll("a[href='/pipelines/builds']").Should().BeEmpty();
        withDeployments.FindAll("a[href='/pipelines/deployments']").Should().ContainSingle();

        _tools.Disabled.Add(ToolKey.Releases);
        var withNeither = _ctx.Render<NavMenu>();
        withNeither.FindAll("a[href='/pipelines']").Should().BeEmpty(
            "a parent over no children reads as a bug");
    }

    [Fact]
    public void Tool_hidden_when_org_opted_out_via_claim()
    {
        _auth.SetAuthorized("user@example.com");
        // Site keeps every tool on; the org has switched Translator off — the
        // opt-out rides on the org_disabled_tools claim.
        _auth.SetClaims(new Claim("org_disabled_tools", "Translator"));

        var cut = _ctx.Render<NavMenu>();

        cut.FindAll("a[href='/translator']").Should().BeEmpty();
        cut.FindAll("a[href='/solutions']").Should().NotBeEmpty(
            "only the org-disabled tool disappears");
    }

    [Fact]
    public void Plain_user_without_admin_role_sees_no_admin_section()
    {
        _auth.SetAuthorized("user@example.com");
        // No role — AuthorizeView Roles="Admin" excludes us.

        var cut = _ctx.Render<NavMenu>();

        cut.FindAll("a[href='/admin']").Should().BeEmpty();
        cut.FindAll("a[href='/admin/administration']").Should().BeEmpty();
    }

    [Fact]
    public void Admin_in_non_system_org_sees_per_org_administration_audit_and_template_pages()
    {
        _auth.SetAuthorized("admin@example.com");
        _auth.SetRoles("Admin");
        _orgCtx.CurrentOrganizationId = TestDb.DefaultOrgId;
        _orgCtx.IsSystemOrganization = false;

        var cut = _ctx.Render<NavMenu>();

        cut.FindAll("a[href='/admin']").Should().NotBeEmpty();
        cut.FindAll("a[href='/admin/administration']").Should().NotBeEmpty(
            "the consolidated Administration entry hosts Identity, MCP, Users, OAuth clients and Export — "
            + "only hidden in the system org");
        cut.FindAll("a[href='/admin/templates/defaults']").Should().NotBeEmpty(
            "Defaults lives under the Templates group");
        cut.FindAll("a[href='/admin/audit']").Should().NotBeEmpty(
            "non-SiteAdmin admins get the per-org audit log, not the cross-org one");

        cut.FindAll("a[href='/admin/users']").Should().BeEmpty(
            "Users moved under /admin/administration/users when the per-org admin pages were consolidated");
        cut.FindAll("a[href='/admin/configuration/identity']").Should().BeEmpty(
            "Configuration was renamed to Administration and the sub-pages moved under /admin/administration/*");
        cut.FindAll("a[href='/admin/export']").Should().BeEmpty(
            "Export is now a tab inside the Administration page rather than a separate sidebar entry");

        cut.FindAll("a[href^='/site-admin/']").Should().BeEmpty(
            "SiteAdmin-only entries must stay hidden from regular org admins");
        cut.FindAll("a[href='/site-admin/audit']").Should().BeEmpty();
    }

    [Fact]
    public void Site_admin_in_non_system_org_sees_both_per_org_and_site_admin_entries()
    {
        _auth.SetAuthorized("siteadmin@example.com");
        _auth.SetRoles("Admin", HttpOrganizationContext.SiteAdminRole);
        _orgCtx.CurrentOrganizationId = TestDb.DefaultOrgId;
        _orgCtx.IsSystemOrganization = false;

        var cut = _ctx.Render<NavMenu>();

        cut.FindAll("a[href='/admin/administration']").Should().NotBeEmpty(
            "the per-org Administration entry stays visible — SiteAdmin sees both");
        var siteUsers = cut.FindAll("a[href='/site-admin/users']");
        siteUsers.Should().NotBeEmpty();
        siteUsers[0].TextContent.Should().Contain("All users",
            "in a non-system org the cross-org users link is labelled \"All users\" to "
            + "distinguish it from the per-org one");

        cut.FindAll("a[href='/site-admin/audit']").Should().NotBeEmpty();
        cut.FindAll("a[href='/admin/audit']").Should().BeEmpty(
            "SiteAdmin's /site-admin/audit replaces the per-org one — see NavMenu's header comment");

        var siteBackups = cut.FindAll("a[href='/site-admin/backup-storage']").ToList();
        siteBackups.Should().NotBeEmpty("Backups, snapshots and storage merged behind one Backup & storage entry");
        siteBackups[0].TextContent.Should().Contain("Backup");
        cut.FindAll("a[href='/site-admin/connections']").Should().NotBeEmpty(
            "Access tokens and OAuth clients merged behind one Connections entry");
        cut.FindAll("a[href='/site-admin/settings']").Should().NotBeEmpty();
    }

    [Fact]
    public void Storage_bar_shown_for_admin_in_non_system_org_by_default()
    {
        _auth.SetAuthorized("admin@example.com");
        _auth.SetRoles("Admin");
        _orgCtx.CurrentOrganizationId = TestDb.DefaultOrgId;
        _orgCtx.IsSystemOrganization = false;

        var cut = _ctx.Render<NavMenu>();

        cut.FindComponents<StorageBar>().Should().ContainSingle(
            "the capacity indicator renders for org admins in the multi-tenant default");
    }

    [Fact]
    public void Storage_bar_hidden_in_single_tenant_mode()
    {
        _auth.SetAuthorized("admin@example.com");
        _auth.SetRoles("Admin");
        _orgCtx.CurrentOrganizationId = TestDb.DefaultOrgId;
        _orgCtx.IsSystemOrganization = false;
        _singleTenant.Enabled = true;

        var cut = _ctx.Render<NavMenu>();

        cut.FindComponents<StorageBar>().Should().BeEmpty(
            "single-tenant deployments hide storage quotas, so the bar isn't rendered");
    }

    [Fact]
    public void Site_admin_in_system_org_hides_per_org_pages_and_relabels_the_site_admin_links()
    {
        _auth.SetAuthorized("bootstrap@example.com");
        _auth.SetRoles("Admin", HttpOrganizationContext.SiteAdminRole);
        _orgCtx.CurrentOrganizationId = TestDb.SystemOrgId;
        _orgCtx.IsSystemOrganization = true;

        var cut = _ctx.Render<NavMenu>();

        cut.FindAll("a[href='/admin/administration']").Should().BeEmpty(
            "system org has no per-org configuration, users, or export — the Administration entry is hidden");
        cut.FindAll("a[href='/admin/users']").Should().BeEmpty();
        cut.FindAll("a[href='/admin/export']").Should().BeEmpty();

        var siteUsers = cut.FindAll("a[href='/site-admin/users']");
        siteUsers.Should().NotBeEmpty();
        siteUsers[0].TextContent.Should().Contain("Users")
            .And.NotContain("All users",
                "inside the system org there is no \"per-org\" users page to disambiguate from");

        var siteBackups = cut.FindAll("a[href='/site-admin/backup-storage']").ToList();
        siteBackups.Should().NotBeEmpty();
        siteBackups[0].TextContent.Should().Contain("Backup");

        cut.FindAll("a[href='/site-admin/settings']").Should().NotBeEmpty();
        cut.FindAll("a[href='/site-admin/audit']").Should().NotBeEmpty();
        cut.FindAll("a[href='/site-admin/connections']").Should().NotBeEmpty();
    }
}
