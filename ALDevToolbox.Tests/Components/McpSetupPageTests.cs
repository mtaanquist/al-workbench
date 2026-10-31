using ALDevToolbox.Components.Pages;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Mcp;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The setup page at <c>/tools/mcp</c>, after the fresh-eyes review of PR 12.
///
/// Every rule here exists because the review found the page breaking it. Step 1
/// offered a genuine either/or where both buttons went to the same URL and only
/// one of the two paths continued into steps 2 and 3; the snippet carried a
/// placeholder the reader was told to swap by hand, having just lost their token
/// to the clipboard; and the tool table rendered flat, wire-name first, on a
/// page whose own caption says you never need to name them.
/// </summary>
public sealed class McpSetupPageTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly Bunit.TestDoubles.BunitAuthorizationContext _auth;

    public McpSetupPageTests()
    {
        _auth = _ctx.AddAuthorization();
        _auth.SetAuthorized("kirsten.jensen@cronus.example");
        _db.McpAvailability.Set(true);
        _db.OrgContext.CurrentUserId = 1;

        _ctx.Services.AddSingleton<IMcpAvailability>(_db.McpAvailability);
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<AppDbContext>(opts => opts
            .UseNpgsql(_db.ConnectionString)
            .AddInterceptors(_db.CommandTracker)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        _ctx.Services.AddScoped<PersonalAccessTokenService>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.OAuth.OAuthConsentService>();
        _ctx.Services.AddScoped(sp => _db.NewToolEnablement(sp.GetRequiredService<AppDbContext>()));
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddSingleton(_db.DataProtectionProvider);
        _ctx.Services.AddSingleton<IHttpContextAccessor>(
            new HttpContextAccessor { HttpContext = Request("https", "workbench.cronus.example") });
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(new ALDevToolbox.Endpoints.PublicOrigin(null));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private static DefaultHttpContext Request(string scheme, string host)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = scheme;
        ctx.Request.Host = new HostString(host);
        return ctx;
    }

    private async Task GiveTokenAsync()
    {
        await using var db = _db.NewContext();
        db.Users.Add(new User
        {
            Id = 1,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "kirsten.jensen@cronus.example",
            PasswordHash = "x",
            DisplayName = "Kirsten Jensen",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        db.PersonalAccessTokens.Add(new PersonalAccessToken
        {
            UserId = 1,
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS laptop",
            TokenHash = "not-a-real-hash",
            TokenPrefix = "aldt_pat_test",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The connector path has to end in step 1. It used to hand the reader on to
    /// "pick your client" - four desktop clients, none of them theirs - and then
    /// tell them to paste a token step 1 never gave them.
    /// </summary>
    [Fact]
    public void The_permission_screen_path_carries_the_address_and_says_it_is_finished()
    {
        var page = _ctx.Render<ALDevToolbox.Components.Pages.Mcp>();

        var connector = page.FindAll(".mcp-choice .card")[1];
        connector.TextContent.Should().Contain("https://workbench.cronus.example/mcp",
            "this path never reaches a snippet, so step 1 is the only place the address can appear");
        connector.TextContent.Should().Contain("steps 2 and 3 are not yours");
        connector.QuerySelector("[data-copy-target]").Should().NotBeNull();
    }

    /// <summary>
    /// With PUBLIC_BASE_URL set, the address is the configured one, not the host the
    /// request arrived on: behind a proxy that does not forward the public host, the
    /// request names an address the reader's assistant cannot reach (#1547).
    /// </summary>
    [Fact]
    public void The_address_uses_the_configured_public_origin_when_there_is_one()
    {
        _ctx.Services.AddSingleton(new ALDevToolbox.Endpoints.PublicOrigin("https://bc-tools.cronus.example"));

        var page = _ctx.Render<ALDevToolbox.Components.Pages.Mcp>();

        page.Find("#mcp-server-url").TextContent.Should().Be("https://bc-tools.cronus.example/mcp");
    }

    /// <summary>One primary button, on the path most arrivals need.</summary>
    [Fact]
    public void There_is_exactly_one_primary_action_and_it_is_the_token()
    {
        var page = _ctx.Render<ALDevToolbox.Components.Pages.Mcp>();

        page.WaitForAssertion(() =>
        {
            var primaries = page.FindAll(".btn--primary");
            primaries.Should().HaveCount(1);
            primaries[0].TextContent.Trim().Should().Be("Create a token");
        });
    }

    /// <summary>
    /// Pasting the token fills the snippet. Without it the reader copies the
    /// snippet, which overwrites the token sitting in their clipboard - and the
    /// token is shown only once, so it is now gone from the clipboard and the
    /// screen both.
    /// </summary>
    [Fact]
    public async Task Pasting_a_token_fills_it_into_the_snippet()
    {
        var page = _ctx.Render<ALDevToolbox.Components.Pages.Mcp>();

        page.Find(".step:nth-of-type(2) .code-block pre").TextContent
            .Should().Contain("Bearer PASTE-YOUR-TOKEN-HERE");

        // Find and trigger inside one InvokeAsync. The page's OnInitializedAsync
        // reads a real database, so a second render can land between finding the
        // element and dispatching to it, which invalidates the handler id bUnit
        // captured — an UnknownEventHandlerIdException that only shows up when
        // the suite is loaded enough to interleave the two.
        await page.InvokeAsync(() =>
            page.Find("#mcp-token").Input("aldt_pat_7f3c_9K2mQx4LbT8vRn1sWd6Ey0Ap"));

        var snippet = page.Find(".step:nth-of-type(2) .code-block pre").TextContent;
        snippet.Should().Contain("Bearer aldt_pat_7f3c_9K2mQx4LbT8vRn1sWd6Ey0Ap");
        snippet.Should().NotContain("PASTE-YOUR-TOKEN-HERE");
    }

    [Fact]
    public async Task Every_client_tab_produces_a_snippet_carrying_the_pasted_token()
    {
        var page = _ctx.Render<ALDevToolbox.Components.Pages.Mcp>();
        await page.InvokeAsync(() => page.Find("#mcp-token").Input("aldt_pat_abc"));

        // Let the page's own OnInitializedAsync read finish before touching a
        // tab. bUnit 2's FindAll hands back a plain snapshot rather than the
        // refreshable collection 1.x had, so an element captured while a render
        // is still pending carries an event-handler id that is dead by the time
        // it is dispatched - UnknownEventHandlerIdException.
        _db.WaitForQueriesToSettle();

        var tabCount = page.FindAll(".pill-tab").Count;
        tabCount.Should().BeGreaterThan(1);

        for (var i = 0; i < tabCount; i++)
        {
            // Same reason, once per pass: the previous click re-rendered, and
            // anything it kicked off has to land before the next lookup.
            _db.WaitForQueriesToSettle();
            await ClickTabAsync(page, i);
            // WaitForAssertion rather than a bare Find: it doubles as the
            // render-settle barrier before the next pass takes its snapshot.
            page.WaitForAssertion(() =>
                page.Find(".step:nth-of-type(2) .code-block pre").TextContent
                    .Should().Contain("aldt_pat_abc")
                    .And.NotContain("${", "that is live variable syntax in a real mcp.json"));
        }
    }

    /// <summary>
    /// Step 1 shows as done once the reader has actually done it, either way.
    /// The archetype ships `.step.is-done`; without using it a returning user
    /// re-reads the fork on every visit.
    /// </summary>
    /// <summary>
    /// Weakly ordered on purpose, and worth saying so: the page reads its state
    /// in OnInitializedAsync, so "not done" is also what the first render shows
    /// before that read lands. This pins the copy of the un-started state rather
    /// than the mechanism - the test below is the one that proves the mechanism.
    /// </summary>
    [Fact]
    public void Step_one_is_not_done_before_anything_is_set_up()
    {
        var page = _ctx.Render<ALDevToolbox.Components.Pages.Mcp>();

        page.Find(".step").ClassName.Should().NotContain("is-done");
        page.FindAll(".step .badge").Should().BeEmpty();
    }

    [Fact]
    public async Task Step_one_is_done_once_the_reader_has_a_token()
    {
        await GiveTokenAsync();

        var page = _ctx.Render<ALDevToolbox.Components.Pages.Mcp>();

        // WaitForAssertion, not a bare assert: the token and consent reads run in
        // OnInitializedAsync against a real database, so the first render lands
        // before they answer. Asserting straight away passed alone and failed in
        // the full suite, which is the worst way to find this out.
        page.WaitForAssertion(() =>
        {
            page.Find(".step").ClassName.Should().Contain("is-done");
            page.Find(".step .badge").TextContent.Trim().Should().Be("You have a token");
            page.Find(".btn--primary").TextContent.Trim().Should().Be("Manage your tokens");
        });
    }

    /// <summary>
    /// The reference is grouped and folded. Flat, wire-name-first and open by
    /// default, thirty-eight rows sat between the setup steps and the
    /// troubleshooting a stuck reader is scrolling for.
    /// </summary>
    [Fact]
    public void The_tool_reference_is_grouped_and_folded_away()
    {
        var page = _ctx.Render<ALDevToolbox.Components.Pages.Mcp>();

        var details = page.Find("details.mcp-tools");
        details.HasAttribute("open").Should().BeFalse("it is lookup material, not the task");

        var groups = page.FindAll(".mcp-tools__group").Select(e => e.TextContent.Trim());
        groups.Should().BeEquivalentTo(McpToolCatalog.Groups);

        var listed = page.FindAll(".mcp-tools table code").Select(e => e.TextContent.Trim());
        listed.Should().BeEquivalentTo(McpToolCatalog.All.Select(t => t.Name));
    }

    /// <summary>
    /// The assistant is not offered a tool its organisation switched off (#1245),
    /// so the page does not list one either (#1344): a consultant reading the list
    /// would otherwise ask for something the assistant cannot do. The page reads
    /// the switch off the signed-in user's claim, as the sidebar does.
    /// </summary>
    [Fact]
    public void A_switched_off_tool_is_left_out_of_the_reference_and_its_count()
    {
        _auth.SetClaims(new System.Security.Claims.Claim(
            ALDevToolbox.Endpoints.EndpointHelpers.DisabledToolsClaim,
            nameof(ToolKey.Translator)));

        var page = _ctx.Render<ALDevToolbox.Components.Pages.Mcp>();

        var offered = McpToolCatalog.All.Where(t => t.Tool != ToolKey.Translator).ToList();
        page.WaitForAssertion(() =>
        {
            var listed = page.FindAll(".mcp-tools table code").Select(e => e.TextContent.Trim());
            listed.Should().BeEquivalentTo(offered.Select(t => t.Name));
            page.FindAll(".mcp-tools__group").Select(e => e.TextContent.Trim())
                .Should().NotContain(McpToolCatalog.Translator, "every tool in it is switched off");
            page.Find("details.mcp-tools summary").TextContent
                .Should().Contain($"{offered.Count} tools across {McpToolCatalog.Groups.Count - 1} areas");
        });
    }

    /// <summary>
    /// Clicks the tab at <paramref name="index"/>, re-finding it if a render
    /// lands between the lookup and the dispatch.
    ///
    /// <para>
    /// bUnit 2's <c>FindAll</c> returns a plain snapshot - the refreshable
    /// collection from 1.x is gone - so the element carries the event-handler
    /// id it had when the snapshot was taken. Any re-render in the gap retires
    /// that id and the dispatch throws
    /// <see cref="Bunit.Rendering.UnknownEventHandlerIdException"/>. The window
    /// is small, so this only ever retries once or twice in practice, but under
    /// a loaded machine it is hit often enough to make the test flaky without
    /// this.
    /// </para>
    /// </summary>
    private static async Task ClickTabAsync(IRenderedComponent<ALDevToolbox.Components.Pages.Mcp> page, int index)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await page.FindAll(".pill-tab")[index]
                    .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
                return;
            }
            catch (Bunit.Rendering.UnknownEventHandlerIdException) when (attempt < maxAttempts)
            {
                // Stale handle from a render we raced; take a fresh snapshot.
            }
        }
    }

}
