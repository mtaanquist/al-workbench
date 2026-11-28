using ALDevToolbox.Components.Layout;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Piper and Diff work without an account, and their links get shared with
/// people outside the organisation. Signed out, the layout shows the tool and
/// a sign-in link only - none of the product's brand or navigation. Signed
/// in, it is the normal shell.
/// </summary>
public sealed class PublicToolLayoutTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly BunitAuthorizationContext _auth;
    private readonly MutableSingleTenant _singleTenant = new();

    public PublicToolLayoutTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _auth = _ctx.AddAuthorization();

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddSingleton<IDbContextFactory<ALDevToolbox.Data.AppDbContext>>(_db.NewContextFactory());
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddScoped<ALDevToolbox.Services.Notifications.InAppNotificationService>();
        _ctx.Services.AddScoped<DisplayTimeZone>();
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton<IToolAvailability>(new AllToolsOn());
        _ctx.Services.AddSingleton<ISingleTenantMode>(_singleTenant);
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(
            typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    private sealed class AllToolsOn : IToolAvailability
    {
        public bool IsSiteEnabled(ToolKey key) => true;
    }

    private sealed class MutableSingleTenant : ISingleTenantMode
    {
        public bool IsEnabled { get; set; }
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private IRenderedComponent<PublicToolLayout> RenderAt(string path)
    {
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(path);
        return _ctx.Render<PublicToolLayout>(p => p.Add(l => l.Body,
            (RenderFragment)(b => b.AddMarkupContent(0, "<div class=\"tool-body\">tool</div>"))));
    }

    [Fact]
    public void Signed_out_shows_the_tool_and_a_sign_in_link_without_the_shell()
    {
        var cut = RenderAt("/piper");

        cut.WaitForAssertion(() => cut.Find(".tool-body").TextContent.Should().Be("tool"));
        cut.FindAll(".app__brand").Should().BeEmpty();
        cut.FindAll(".app__nav").Should().BeEmpty();
        cut.FindAll(".app__top").Should().BeEmpty();
        cut.Markup.Should().NotContain("AL Workbench");

        var signIn = cut.Find("a.public-signin");
        signIn.TextContent.Trim().Should().Be("Sign in");
        signIn.GetAttribute("href").Should().Be("/login?return=%2Fpiper");
    }

    [Fact]
    public void Signed_in_gets_the_normal_shell()
    {
        _auth.SetAuthorized("user@example.com");

        var cut = RenderAt("/diff");

        cut.WaitForAssertion(() => cut.Find(".app__brand").Should().NotBeNull());
        cut.Find(".app__content .tool-body").TextContent.Should().Be("tool");
        cut.FindAll(".public-signin").Should().BeEmpty();
    }

    [Fact]
    public void Single_tenant_signed_out_makes_sign_in_a_button()
    {
        _singleTenant.IsEnabled = true;

        var cut = RenderAt("/piper");

        cut.WaitForAssertion(() => cut.Find(".tool-body").Should().NotBeNull());
        var signIn = cut.Find("a.public-signin--button");
        signIn.ClassList.Should().Contain("btn");
        signIn.TextContent.Trim().Should().Be("Sign in");
        signIn.GetAttribute("href").Should().Be("/login?return=%2Fpiper");
        cut.FindAll(".app__nav").Should().BeEmpty();
    }

    [Fact]
    public void Single_tenant_signed_out_drops_the_shell_from_every_page()
    {
        _singleTenant.IsEnabled = true;
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/");

        var cut = _ctx.Render<MainLayout>(p => p.Add(l => l.Body,
            (RenderFragment)(b => b.AddMarkupContent(0, "<div class=\"tool-body\">home</div>"))));

        cut.WaitForAssertion(() => cut.Find(".tool-body").TextContent.Should().Be("home"));
        cut.FindAll(".app__brand").Should().BeEmpty();
        cut.FindAll(".app__nav").Should().BeEmpty();
        cut.FindAll(".app__top").Should().BeEmpty();
        cut.Markup.Should().NotContain("AL Workbench");
        cut.Find("a.public-signin--button").GetAttribute("href").Should().Be("/login");
    }

    [Fact]
    public void Multi_tenant_signed_out_keeps_the_shell_on_other_pages()
    {
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/");

        var cut = _ctx.Render<MainLayout>(p => p.Add(l => l.Body,
            (RenderFragment)(b => b.AddMarkupContent(0, "<div class=\"tool-body\">home</div>"))));

        cut.WaitForAssertion(() => cut.Find(".app__brand").Should().NotBeNull());
        cut.FindAll(".public-frame").Should().BeEmpty();
    }

    [Fact]
    public void Single_tenant_signed_in_gets_the_normal_shell()
    {
        _singleTenant.IsEnabled = true;
        _auth.SetAuthorized("user@example.com");

        var cut = _ctx.Render<MainLayout>(p => p.Add(l => l.Body,
            (RenderFragment)(b => b.AddMarkupContent(0, "<div class=\"tool-body\">home</div>"))));

        cut.WaitForAssertion(() => cut.Find(".app__brand").Should().NotBeNull());
        cut.FindAll(".public-frame").Should().BeEmpty();
    }
}
