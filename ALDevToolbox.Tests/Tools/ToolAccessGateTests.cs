using System.Net;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// End-to-end cover for <c>ToolAccessGate</c>: a tool disabled site-wide 404s
/// its end-user routes for a direct (anonymous) request. The gate runs *before*
/// authorization on purpose — an auth-gated tool like Projects must 404 rather
/// than redirect an anonymous visitor to /login, so a hidden tool can't be
/// probed by its address. Boots the real <c>Program.cs</c> so middleware order
/// is exactly production's.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class ToolAccessGateTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private async Task DisableToolsAsync(params ToolKey[] keys)
    {
        // Seed before the host boots so startup priming loads the disabled set.
        await using var ctx = _db.NewContext();
        var row = await ctx.SystemSettings.FirstOrDefaultAsync(s => s.Id == 1);
        if (row is null)
        {
            row = new SystemSettings { Id = 1, UpdatedAt = DateTime.UtcNow };
            ctx.SystemSettings.Add(row);
        }
        row.DisabledTools = ToolCatalog.Format(keys);
        await ctx.SaveChangesAsync();
    }

    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    private static System.Security.Claims.ClaimsPrincipal CookieUser(string stepUpTools, DateTime? strongAt, bool pat = false)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new(ALDevToolbox.Services.HttpOrganizationContext.UserIdClaim, "7"),
            new(ALDevToolbox.Endpoints.EndpointHelpers.StepUpToolsClaim, stepUpTools),
        };
        if (strongAt is { } at)
        {
            claims.Add(new(ALDevToolbox.Services.Account.StepUpAuth.StrongAuthAtClaim,
                at.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (pat) claims.Add(new("pat_id", "1"));
        return new(new System.Security.Claims.ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void Step_up_is_needed_for_a_marked_tool_when_the_session_is_not_fresh()
    {
        var tools = new[] { ToolKey.Releases };

        ALDevToolbox.Endpoints.ToolAccessGate.NeedsStepUp(tools, CookieUser("Releases", strongAt: null), Now)
            .Should().BeTrue("no second factor ever happened on this session");
        ALDevToolbox.Endpoints.ToolAccessGate.NeedsStepUp(tools, CookieUser("Releases", Now.AddMinutes(-30)), Now)
            .Should().BeTrue("the last one is outside the window");
        ALDevToolbox.Endpoints.ToolAccessGate.NeedsStepUp(tools, CookieUser("Releases", Now.AddMinutes(-2)), Now)
            .Should().BeFalse("it was just done");
    }

    [Fact]
    public void Step_up_is_not_needed_for_unmarked_tools_anonymous_visitors_or_tokens()
    {
        var tools = new[] { ToolKey.Releases };

        ALDevToolbox.Endpoints.ToolAccessGate.NeedsStepUp(tools, CookieUser("Templates", strongAt: null), Now)
            .Should().BeFalse("only the marked tools are gated");
        ALDevToolbox.Endpoints.ToolAccessGate.NeedsStepUp(tools, new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()), Now)
            .Should().BeFalse("authorization's own login redirect handles anonymous requests");
        ALDevToolbox.Endpoints.ToolAccessGate.NeedsStepUp(tools, CookieUser("Releases", strongAt: null, pat: true), Now)
            .Should().BeFalse("a token session cannot step up and never reaches page routes anyway");
    }

    [Fact]
    public void The_shared_pipelines_dashboard_is_gated_when_either_of_its_tools_is()
    {
        var tools = ALDevToolbox.Endpoints.ToolAccessGate.MatchTools("/pipelines");

        ALDevToolbox.Endpoints.ToolAccessGate.NeedsStepUp(tools, CookieUser("Releases", strongAt: null), Now)
            .Should().BeTrue();
    }

    [Fact]
    public async Task Disabled_tool_routes_404_for_anonymous_request()
    {
        // Piper is anonymous-accessible, Solutions requires auth — both must 404,
        // proving the gate runs ahead of the auth challenge.
        await DisableToolsAsync(ToolKey.Piper, ToolKey.Projects);

        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();

        (await client.GetAsync("/piper")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/solutions")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/solutions/new")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_legacy_projects_path_redirects_to_solutions_when_the_tool_is_on()
    {
        // Projects became Solutions while the tool was staging-only, but the links
        // above it already pointed at /projects, so the old path has to keep landing.
        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();

        var redirect = await client.GetAsync("/projects");
        redirect.StatusCode.Should().Be(HttpStatusCode.MovedPermanently);
        redirect.Headers.Location!.ToString().Should().Be("/solutions");
    }

    [Fact]
    public async Task The_legacy_projects_path_is_not_a_way_around_a_disabled_tool()
    {
        // ToolCatalog lists both paths for ToolKey.Projects, so the gate runs ahead of
        // the redirect and a disabled tool is invisible at its old address too. Without
        // that, the route rename would have quietly opened a hole: the gate matched
        // /projects only, and the page had moved to /solutions.
        await DisableToolsAsync(ToolKey.Projects);

        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();

        (await client.GetAsync("/solutions")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/projects")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the legacy path must not answer for a tool that is switched off");
    }

    [Fact]
    public async Task Deployment_pipelines_answer_to_their_own_toggle_inside_the_build_pipelines_prefix()
    {
        // /pipelines/deployments sits under the build pipelines' /pipelines prefix, so the
        // gate has to take the longest match: switching deployments off must hide them
        // (and their old /releases address) without taking the build pipelines with them.
        await DisableToolsAsync(ToolKey.Releases);

        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();

        (await client.GetAsync("/pipelines/deployments")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/pipelines/deployments/3")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/releases")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the legacy path must not answer for a tool that is switched off");
        (await client.GetAsync("/pipelines/builds")).StatusCode.Should().Be(HttpStatusCode.Redirect,
            "build pipelines are a different tool, still on: signed out, they send you to sign in");
    }

    [Fact]
    public async Task Switching_build_pipelines_off_leaves_deployment_pipelines_reachable()
    {
        await DisableToolsAsync(ToolKey.Pipelines);

        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();

        (await client.GetAsync("/pipelines/builds")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/pipelines/7")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/pipelines/deployments")).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task The_Pipelines_dashboard_answers_while_either_pipeline_tool_is_on()
    {
        // /pipelines sits above both lists (#955), so switching one tool off must not
        // take the dashboard with it; switching both off must.
        await DisableToolsAsync(ToolKey.Pipelines);
        using (var factory = new EndpointFactory(_db))
        using (var client = factory.CreateClient())
        {
            (await client.GetAsync("/pipelines")).StatusCode.Should().Be(HttpStatusCode.Redirect,
                "deployment pipelines are still on: signed out, it sends you to sign in");
            (await client.GetAsync("/pipelines/builds")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        await DisableToolsAsync(ToolKey.Pipelines, ToolKey.Releases);
        using (var factory = new EndpointFactory(_db))
        using (var client = factory.CreateClient())
        {
            (await client.GetAsync("/pipelines")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.GetAsync("/pipelines/")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Enabled_tool_routes_are_not_404d()
    {
        await DisableToolsAsync(ToolKey.Piper);

        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();

        // Translator requires auth, so it redirects to login — the point is it is
        // not 404'd. Its admin authoring sibling is never gated.
        (await client.GetAsync("/translator")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await client.GetAsync("/object-explorer")).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Disabling_a_tool_does_not_404_its_admin_pages()
    {
        await DisableToolsAsync(ToolKey.ObjectExplorer);

        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();

        // The end-user surface is gone...
        (await client.GetAsync("/object-explorer")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        // ...but the admin authoring page stays reachable (redirects to login,
        // not 404) so Editors can still manage its content.
        (await client.GetAsync("/admin/object-explorer")).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }
}
