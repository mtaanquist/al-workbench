using ALDevToolbox.Components.Pages.Admin.Administration;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// "Let AI assistants deploy to production environments" on Administration → Tools (#1122):
/// the setter persists and audits the change, and the page opens on the stored value
/// and saves a flip through the page's one Save button.
/// </summary>
public sealed class AdminToolsAgentDeployTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    private const string SwitchLabel = "Let AI assistants deploy to production environments";

    public AdminToolsAgentDeployTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddSingleton(_db.NewContextFactory());
        _ctx.Services.AddScoped(sp => _db.NewOrganizationAdminService(sp.GetRequiredService<AppDbContext>()));
        _ctx.Services.AddScoped(sp => _db.NewOrganizationBrandingService(sp.GetRequiredService<AppDbContext>()));
        TestDb.AddToolServices(_ctx.Services);
        _ctx.Services.AddScoped<DisplayTimeZone>();
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

    private async Task<bool> StoredAsync()
    {
        await using var read = _db.NewContext();
        return await read.OrganizationSettings.AsNoTracking()
            .Where(s => s.OrganizationId == TestDb.DefaultOrgId)
            .Select(s => s.AgentsMayDeployToProduction)
            .FirstOrDefaultAsync();
    }

    [Fact]
    public async Task The_setting_is_off_until_an_admin_turns_it_on()
    {
        (await StoredAsync()).Should().BeFalse();
        (await _db.NewOrganizationAdminService(_db.NewContext()).GetToolsViewAsync())
            .AgentsMayDeployToProduction.Should().BeFalse();
    }

    [Fact]
    public async Task Setting_it_persists_and_writes_an_audit_row()
    {
        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin@example.com")))
        {
            await _db.NewOrganizationAdminService(ctx).SetAgentsMayDeployToProductionAsync(true);
        }

        (await StoredAsync()).Should().BeTrue();
        (await _db.NewOrganizationAdminService(_db.NewContext()).GetToolsViewAsync())
            .AgentsMayDeployToProduction.Should().BeTrue();

        await using var verify = _db.NewContext();
        var settingsId = await verify.OrganizationSettings.AsNoTracking()
            .Where(s => s.OrganizationId == TestDb.DefaultOrgId).Select(s => s.Id).SingleAsync();
        var audit = await verify.AuditLog.AsNoTracking()
            .Where(a => a.EntityType == AuditEntityType.OrganizationSettings && a.EntityId == settingsId)
            .ToListAsync();
        audit.Should().ContainSingle(a => a.ChangedBy == "admin@example.com");
    }

    [Fact]
    public async Task Turning_it_off_again_is_stored()
    {
        var svc = _db.NewOrganizationAdminService(_db.NewContext());
        await svc.SetAgentsMayDeployToProductionAsync(true);
        await _db.NewOrganizationAdminService(_db.NewContext()).SetAgentsMayDeployToProductionAsync(false);

        (await StoredAsync()).Should().BeFalse();
    }

    [Fact]
    public void The_page_shows_the_switch_off_with_its_caption()
    {
        var cut = _ctx.Render<AdminAdministrationTools>();

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find($"input[aria-label='{SwitchLabel}']");
            input.HasAttribute("checked").Should().BeFalse();
            cut.Markup.Should().Contain("Off: AI assistants can only deploy to sandboxes.");
        });
    }

    [Fact]
    public async Task The_switch_is_disabled_while_the_ai_assistant_tool_is_off()
    {
        await _db.NewOrganizationAdminService(_db.NewContext()).SetMcpEnabledAsync(false);

        var cut = _ctx.Render<AdminAdministrationTools>();

        cut.WaitForAssertion(() =>
        {
            cut.Find($"input[aria-label='{SwitchLabel}']").HasAttribute("disabled").Should().BeTrue();
            cut.Markup.Should().Contain("Switch the tool on above to change this.");
        });
    }

    [Fact]
    public async Task Flipping_the_switch_and_saving_stores_it()
    {
        var cut = _ctx.Render<AdminAdministrationTools>();
        cut.WaitForAssertion(() => cut.Find($"input[aria-label='{SwitchLabel}']"));

        await cut.Find($"input[aria-label='{SwitchLabel}']").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        await cut.Find("button.btn--primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Tools saved."));
        (await StoredAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task A_refused_confirmation_saves_nothing_on_the_page()
    {
        // The organisation asks for a recent second factor, and this session has none,
        // so turning the switch on is refused. The tool switched off in the same save
        // must not be stored either (#1196).
        await using (var ctx = _db.NewContext())
        {
            var org = await ctx.Organizations.FirstAsync(o => o.Id == TestDb.DefaultOrgId);
            org.StepUpTools = ToolCatalog.Format(new[] { ToolKey.Releases });
            await ctx.SaveChangesAsync();
        }
        var cut = _ctx.Render<AdminAdministrationTools>();
        cut.WaitForAssertion(() => cut.Find($"input[aria-label='{SwitchLabel}']"));

        await cut.Find($"input[aria-label='{SwitchLabel}']").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        await cut.FindAll("input[type=checkbox]")[0].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = false });
        await cut.Find("button.btn--primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("you before changing this: open"));
        (await StoredAsync()).Should().BeFalse();
        await using var read = _db.NewContext();
        var stored = await read.Organizations.AsNoTracking().SingleAsync(o => o.Id == TestDb.DefaultOrgId);
        stored.DisabledTools.Should().BeEmpty();
        stored.McpEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task A_refused_window_change_does_not_save_a_tool_switched_off_with_it()
    {
        await using (var ctx = _db.NewContext())
        {
            var org = await ctx.Organizations.FirstAsync(o => o.Id == TestDb.DefaultOrgId);
            org.StepUpTools = ToolCatalog.Format(new[] { ToolKey.Releases });
            await ctx.SaveChangesAsync();
        }
        var cut = _ctx.Render<AdminAdministrationTools>();
        cut.WaitForAssertion(() => cut.Find("#step-up-window"));

        await cut.Find("#step-up-window").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "30" });
        await cut.FindAll("input[type=checkbox]")[0].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = false });
        await cut.Find("button.btn--primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("you before changing this: open"));
        await using var read = _db.NewContext();
        var stored = await read.Organizations.AsNoTracking().SingleAsync(o => o.Id == TestDb.DefaultOrgId);
        stored.DisabledTools.Should().BeEmpty();
        stored.StepUpWindowMinutes.Should().NotBe(30);
    }

    private static AuditInterceptor NewInterceptor(string name)
    {
        var http = new Microsoft.AspNetCore.Http.HttpContextAccessor();
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, name) }, "test")),
        };
        http.HttpContext = ctx;
        return new AuditInterceptor(http);
    }
}
