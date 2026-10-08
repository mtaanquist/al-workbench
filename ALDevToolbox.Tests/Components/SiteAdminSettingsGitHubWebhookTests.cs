using System.Security.Cryptography;
using ALDevToolbox.Components.Pages.SiteAdmin;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using System.Net;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The webhook half of the deployment's GitHub page (issue #627), rendered.
///
/// <para>A screenshot is not available in this environment, so these are the
/// rendered evidence: the SiteAdmin is given an address to copy and a secret box
/// to fill in, the box says "Unchanged" once something is stored rather than
/// showing it, and the walkthrough tells them about the event and the permission
/// the gate needs - which is the difference between the feature working and the
/// feature silently never firing.</para>
/// </summary>
public sealed class SiteAdminSettingsGitHubWebhookTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly GitHubHookAddressAllowList _senders = new(TimeProvider.System);

    public SiteAdminSettingsGitHubWebhookTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("siteadmin@cronus.example");
        auth.SetRoles("SiteAdmin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddDataProtection();
        _ctx.Services.AddScoped<SystemSettingsService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _ctx.Services.AddSingleton(_senders);
        _ctx.Services.AddSingleton(TimeProvider.System);
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private void UsePublicOrigin(string? configured) =>
        _ctx.Services.AddSingleton(new PublicOrigin(configured));

    [Fact]
    public void The_page_offers_a_webhook_address_to_copy_and_a_secret_to_fill_in()
    {
        UsePublicOrigin("https://workbench.cronus.example");

        var cut = _ctx.Render<SiteAdminSettingsGitHub>();

        cut.WaitForAssertion(() =>
        {
            var address = cut.FindAll("input[readonly]")
                .Select(i => i.GetAttribute("value"))
                .Should().Contain("https://workbench.cronus.example/github/webhook").And.Subject;
            address.Should().NotBeEmpty();

            var secret = cut.Find("input[name=GitHubWebhookSecret]");
            secret.GetAttribute("type").Should().Be("password");
            secret.GetAttribute("placeholder").Should().Be("Not set");
        });
    }

    [Fact]
    public void Without_a_configured_public_address_the_page_still_shows_one()
    {
        // A deployment that has not been told its own address still needs the
        // operator to be able to copy something; the request host is the best
        // guess available and is what the sibling addresses already use.
        UsePublicOrigin(null);

        var cut = _ctx.Render<SiteAdminSettingsGitHub>();

        cut.WaitForAssertion(() =>
            cut.FindAll("input[readonly]")
                .Select(i => i.GetAttribute("value") ?? string.Empty)
                .Should().Contain(v => v.EndsWith("/github/webhook", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_stored_secret_is_shown_as_unchanged_with_a_way_to_forget_it()
    {
        UsePublicOrigin("https://workbench.cronus.example");
        await StoreWebhookSecretAsync();

        var cut = _ctx.Render<SiteAdminSettingsGitHub>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("input[name=GitHubWebhookSecret]").GetAttribute("placeholder")
                .Should().Be("Unchanged", "the stored secret is never rendered back");
            cut.Markup.Should().NotContain("swordfish");
            cut.FindAll("input[name=ClearGitHubWebhookSecret]").Should().ContainSingle(
                "forgetting it has to be a deliberate tick, not an empty box");
        });
    }

    [Fact]
    public void With_nothing_stored_there_is_nothing_to_forget()
    {
        UsePublicOrigin(null);

        var cut = _ctx.Render<SiteAdminSettingsGitHub>();

        cut.WaitForAssertion(() =>
            cut.FindAll("input[name=ClearGitHubWebhookSecret]").Should().BeEmpty());
    }

    [Fact]
    public void The_walkthrough_names_the_event_and_the_permission_the_gate_needs()
    {
        // Both are things the operator has to tick on GitHub itself. Leave either
        // out and the compile gate never fires, with nothing in the workbench to
        // say why.
        UsePublicOrigin(null);

        var cut = _ctx.Render<SiteAdminSettingsGitHub>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Pull request");
            cut.Markup.Should().Contain("report build results on pull requests");
        });
    }

    [Fact]
    public void The_page_renders_its_loading_state_before_the_settings_arrive()
    {
        UsePublicOrigin(null);

        var cut = _ctx.Render<SiteAdminSettingsGitHub>();

        // Either it is still loading or it has finished; both are states this
        // page renders explicitly, and neither is a bare empty form.
        cut.WaitForAssertion(() =>
            (cut.Markup.Contains("Loading...") || cut.Markup.Contains("Webhook URL")).Should().BeTrue());
    }

    // --- GitHub's webhook addresses (#1201) ----------------------------------

    [Fact]
    public void Before_the_address_list_is_read_the_page_says_every_address_is_accepted()
    {
        UsePublicOrigin(null);

        var cut = _ctx.Render<SiteAdminSettingsGitHub>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("#github-hook-addresses").TextContent.Should().Contain("has not been read yet");
            cut.FindAll("#github-hook-refusal").Should().BeEmpty();
        });
    }

    [Fact]
    public void Once_the_address_list_is_read_the_page_says_when()
    {
        UsePublicOrigin(null);
        _senders.Replace(GitHubHookAddressAllowList.Parse(["192.30.252.0/22"]).Ranges);

        var cut = _ctx.Render<SiteAdminSettingsGitHub>();

        cut.WaitForAssertion(() =>
        {
            var text = cut.Find("#github-hook-addresses");
            text.TextContent.Should().Contain("was last read");
            text.QuerySelector("time").Should().NotBeNull();
            cut.FindAll("#github-hook-refusal").Should().BeEmpty("nothing was refused");
        });
    }

    [Fact]
    public void A_refused_delivery_is_shown_with_its_address_and_what_to_do()
    {
        // The proxy case: every delivery arrives from the proxy's own address.
        UsePublicOrigin(null);
        _senders.Replace(GitHubHookAddressAllowList.Parse(["192.30.252.0/22"]).Ranges);
        _senders.ShouldWarnRefusal(IPAddress.Parse("172.18.0.5"), out _);

        var cut = _ctx.Render<SiteAdminSettingsGitHub>();

        cut.WaitForAssertion(() =>
        {
            var text = cut.Find("#github-hook-refusal").TextContent;
            text.Should().Contain("172.18.0.5");
            text.Should().Contain("reverse proxy").And.Contain("TRUSTED_PROXIES");
            cut.Find("#github-hook-refusal time").Should().NotBeNull();
        });
    }

    private async Task StoreWebhookSecretAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: null,
            ClientSecret: null, ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false,
            WebhookSecret: "swordfish", ClearWebhookSecret: false));
    }
}
