using System.Security.Claims;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// <see cref="ToolEnablement"/> is the one place a surface that is not a
/// rendered page asks whether a tool is switched on, so what it must match is
/// the sidebar: a tool a SiteAdmin turned off is off for everyone, and an
/// organisation can only narrow that further (issue #772). The two ways it can
/// learn the organisation's own set - the auth claim a browser carries, and the
/// organisation row an MCP request has to be read from - are both covered.
/// </summary>
public sealed class ToolEnablementTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_tool_nobody_switched_off_is_enabled()
    {
        await using var ctx = _db.NewContext();
        var tools = _db.NewToolEnablement(ctx);

        (await tools.IsEnabledAsync(ToolKey.Projects)).Should().BeTrue();
    }

    [Fact]
    public async Task The_organisations_own_opt_out_narrows_a_site_enabled_tool()
    {
        await DisableForOrgAsync(ToolKey.Projects);
        await using var ctx = _db.NewContext();
        var tools = _db.NewToolEnablement(ctx);

        (await tools.IsEnabledAsync(ToolKey.Projects)).Should().BeFalse();
        (await tools.IsEnabledAsync(ToolKey.Translator)).Should().BeTrue("only the named tool is off");
    }

    [Fact]
    public async Task Site_disabled_wins_over_an_organisation_that_left_it_on()
    {
        var site = TestDb.EverythingEnabled();
        site.Set(new[] { ToolKey.Projects });
        await using var ctx = _db.NewContext();
        var tools = _db.NewToolEnablement(ctx, site);

        // The organisation has switched nothing off; the site toggle is still
        // the answer, exactly as the sidebar and the route gate have it.
        (await tools.IsEnabledAsync(ToolKey.Projects)).Should().BeFalse();
    }

    [Fact]
    public async Task The_signed_in_principals_claim_answers_without_a_query()
    {
        // The organisation row says nothing is off; the claim says Solutions
        // is. A browser request must follow the claim it was signed in with -
        // the same value the sidebar hid the link on.
        await using var ctx = _db.NewContext();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = PrincipalWithDisabled(ToolKey.Projects),
            },
        };
        var tools = new ToolEnablement(TestDb.EverythingEnabled(), accessor, ctx, _db.OrgContext, TimeProvider.System);

        (await tools.IsEnabledAsync(ToolKey.Projects)).Should().BeFalse();
        (await tools.IsEnabledAsync(ToolKey.Cookbook)).Should().BeTrue();
    }

    [Fact]
    public void The_page_overload_reads_the_principal_it_is_handed()
    {
        using var ctx = _db.NewContext();
        var site = TestDb.EverythingEnabled();
        var tools = new ToolEnablement(site, new HttpContextAccessor(), ctx, _db.OrgContext, TimeProvider.System);

        tools.IsEnabled(ToolKey.Projects, PrincipalWithDisabled(ToolKey.Projects)).Should().BeFalse();
        tools.IsEnabled(ToolKey.Projects, PrincipalWithDisabled(ToolKey.Piper)).Should().BeTrue();

        site.Set(new[] { ToolKey.Projects });
        tools.IsEnabled(ToolKey.Projects, PrincipalWithDisabled(ToolKey.Piper))
            .Should().BeFalse("site state wins here too");
    }

    private static ClaimsPrincipal PrincipalWithDisabled(params ToolKey[] disabled) =>
        new(EndpointHelpers.BuildIdentity(new User
        {
            Id = 7,
            OrganizationId = TestDb.DefaultOrgId,
            Organization = new Organization
            {
                Id = TestDb.DefaultOrgId,
                Name = "CRONUS",
                McpEnabled = true,
                DisabledTools = ToolCatalog.Format(disabled),
            },
            Email = "dev@cronus.example",
            DisplayName = "Dev Eloper",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        }));

    private async Task DisableForOrgAsync(params ToolKey[] keys)
    {
        await using var ctx = _db.NewContext();
        var org = await ctx.Organizations.SingleAsync(o => o.Id == TestDb.DefaultOrgId);
        org.DisabledTools = ToolCatalog.Format(keys);
        await ctx.SaveChangesAsync();
    }
}
