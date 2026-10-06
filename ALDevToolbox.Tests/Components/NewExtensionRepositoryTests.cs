using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Components.Pages;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The GitHub half of <c>/templates/extension</c>: what the page does once a
/// repository has been picked.
///
/// <para>The rule being pinned is the one a design review caught: the delivery
/// button lives in the aside, a column away from the fields it depends on, and
/// hydration deliberately leaves the extension name empty - so the very first
/// press is often on an invalid form. A refusal that only rendered next to the
/// fields would look, from where the user is standing, like a button that did
/// nothing.</para>
/// </summary>
public sealed class NewExtensionRepositoryTests : IDisposable
{
    private const int UserId = 851;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string Repo = "cronus-dk/solution-a";

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public NewExtensionRepositoryTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("dev@cronus.example");
        // The page calls into generate.js to take the user to the field that
        // has to be fixed; there is no browser here to run it.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<FolderTreeHydrator>();
        _ctx.Services.AddScoped<TemplateService>();
        _ctx.Services.AddScoped<ApplicationVersionService>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddScoped<WorkspaceConfigService>();
        _ctx.Services.AddSingleton<ALDevToolbox.Services.Generation.MustacheRenderer>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.Generation.WorkspaceZipBuilder>();
        _ctx.Services.AddScoped<GenerationService>();
        _ctx.Services.AddDataProtection();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));

        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "dev@cronus.example",
            DisplayName = "dev@cronus.example",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Adding_to_a_repository_with_nothing_typed_refuses_where_the_click_was()
    {
        var api = await ReadyPageAsync();
        var cut = _ctx.Render<NewExtension>();
        await PickTheRepositoryAsync(cut);

        cut.WaitForElement("button:contains('Add to repository')").Click();

        cut.WaitForAssertion(() =>
        {
            // Beside the button, not only in the form's banner a column away,
            // and naming the field rather than saying "something is wrong".
            var card = cut.Find(".ext-repo");
            card.TextContent.Should().Contain("Extension name");
        });
        api.Calls.Should().NotContain(c => c.Contains("git/blobs"),
            "an invalid form must not reach GitHub at all");
    }

    [Fact]
    public async Task The_forms_own_banner_names_the_button_that_was_pressed()
    {
        await ReadyPageAsync();
        var cut = _ctx.Render<NewExtension>();
        await PickTheRepositoryAsync(cut);

        cut.WaitForElement("button:contains('Add to repository')").Click();

        cut.WaitForAssertion(() =>
            cut.Find(".alert--danger").TextContent
                .Should().Contain("added to the repository").And.NotContain("can be generated"));
    }

    [Fact]
    public async Task Changing_the_repository_unlocks_the_template_it_locked()
    {
        await ReadyPageAsync(withSavedConfig: true);
        var cut = _ctx.Render<NewExtension>();
        await PickTheRepositoryAsync(cut);

        // The saved settings lock the template to the one the solution uses.
        cut.WaitForAssertion(() =>
            cut.Find("#ext-template").HasAttribute("disabled").Should().BeTrue());

        cut.WaitForElement("button:contains('Change')").Click();

        // Undoing the pick has to undo what it did, or the template stays locked
        // to a solution the page no longer claims to be working from.
        cut.WaitForAssertion(() =>
            cut.Find("#ext-template").HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public async Task The_locked_template_names_the_repository_it_came_from()
    {
        await ReadyPageAsync(withSavedConfig: true);
        var cut = _ctx.Render<NewExtension>();
        await PickTheRepositoryAsync(cut);

        // "the imported config" is not something this user did: they picked a
        // repository, and that is what the lock should name.
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain($"Locked by {Repo}");
            cut.Markup.Should().NotContain("imported config");
        });
    }

    [Fact]
    public async Task Picking_a_repository_says_what_it_replaced()
    {
        await ReadyPageAsync(withSavedConfig: true);
        var cut = _ctx.Render<NewExtension>();
        await PickTheRepositoryAsync(cut);

        // The ID range is the field a consultant guards most, and it changes in
        // a column they are not looking at.
        cut.WaitForAssertion(() =>
        {
            var outcome = cut.Find(".ext-repo__outcome").TextContent;
            outcome.Should().Contain(Repo);
            outcome.Should().Contain("ID range");
            outcome.Should().NotContain("(s)");
        });
    }

    [Fact]
    public async Task A_repository_whose_settings_cannot_be_read_says_so_in_plain_words()
    {
        var api = await ReadyPageAsync();
        api.On(HttpMethod.Get, $"/repos/{Repo}/contents/workspace.aldt.toml", HttpStatusCode.OK,
            FakeGitHubApi.FileContentsJson("workspace.aldt.toml", "this is not toml = = ="));
        var cut = _ctx.Render<NewExtension>();
        await PickTheRepositoryAsync(cut);

        // The parser's words are for someone who chose a file. This person chose
        // a repository, so TOML, schema versions and config kinds stay in the log.
        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".ext-repo").TextContent;
            card.Should().Contain("could not be read");
            card.Should().NotContain("TOML").And.NotContain("schema_version").And.NotContain("config kind");
        });
    }

    [Fact]
    public async Task A_picked_solution_names_the_extension_with_its_prefix()
    {
        await ReadyPageAsync(withSavedConfig: true);
        var cut = _ctx.Render<NewExtension>();
        await PickTheRepositoryAsync(cut);

        cut.WaitForElement("#ext-name").Input("Banking");

        // The prefix is added for the user, so the field says what it adds up
        // to rather than leaving them to guess whether to type it.
        cut.WaitForAssertion(() =>
        {
            var hint = cut.Find("#ext-name").ParentElement!.QuerySelector(".field__hint")!.TextContent;
            hint.Should().Contain("CRO Banking").And.Contain("Banking/");
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_picked_repository_turns_the_example_files_off(bool withSavedConfig)
    {
        await ReadyPageAsync(withSavedConfig);
        var cut = _ctx.Render<NewExtension>();
        await PickTheRepositoryAsync(cut);

        // Off and locked, not merely defaulted: the pull request leaves them out
        // either way, and a switch that is ignored is worse than none.
        cut.WaitForAssertion(() =>
        {
            var examples = cut.Find(".preview__head-tools input[type=checkbox]");
            examples.HasAttribute("checked").Should().BeFalse();
            examples.HasAttribute("disabled").Should().BeTrue();
            cut.Find("input[type=hidden][name=IncludeExamples]").GetAttribute("value").Should().Be("false");
        });
    }

    // --- helpers ------------------------------------------------------------

    /// <summary>Focuses the picker, waits for its one row, and clicks it.</summary>
    private static async Task PickTheRepositoryAsync(IRenderedComponent<NewExtension> cut)
    {
        await cut.WaitForElement("#ext-repo").FocusAsync(new());
        cut.WaitForElement("button.repo-picker__result").Click();
        cut.WaitForElement(".ext-deliver");
    }

    /// <summary>
    /// A deployment, organisation and user all in place, one template to
    /// generate from, and a GitHub offering exactly one repository - which
    /// either carries saved settings or does not.
    /// </summary>
    private async Task<FakeGitHubApi> ReadyPageAsync(bool withSavedConfig = false)
    {
        using (var rsa = RSA.Create(2048))
        {
            await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
                AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
                ClientSecret: "s3cr3t", ClearClientSecret: false,
                PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
        }

        await using (var ctx = _db.NewContext())
        {
            ctx.OrganizationSettings.Add(new OrganizationSettings
            {
                OrganizationId = TestDb.DefaultOrgId,
                DefaultPublisher = "CRONUS",
                GitHubInstallationId = InstallationId,
                GitHubOrgLogin = OrgLogin,
                GitHubConnectedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson());
        await using (var ctx = _db.NewContext())
        {
            await _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, api)).LinkAsync("the-code");
        }

        api.On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
           .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson(Repo))
           .On(HttpMethod.Get, $"/repos/{Repo}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(Repo))
           .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.NotFound);
        if (withSavedConfig)
        {
            api.On(HttpMethod.Get, $"/repos/{Repo}/contents/workspace.aldt.toml", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("workspace.aldt.toml", SavedWorkspaceToml));
        }

        await SeedTemplateAsync();
        _db.AddGitHubServices(_ctx.Services, api);
        return api;
    }

    private async Task SeedTemplateAsync()
    {
        var template = TemplateBuilder.Default();
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(template);
        await ctx.SaveChangesAsync();
    }

    /// <summary>A solution's saved settings, as the generator writes them.</summary>
    private const string SavedWorkspaceToml = """
        schema_version = 1
        kind = "workspace"

        [workspace]
        template = "runtime-test"
        name = "CRONUS Customer"
        brief = "CRONUS A/S solution"
        description = "The customer solution."
        application_version = "24.0.0.0"
        runtime_version = "15"
        core_id_range_from = 50000
        core_id_range_to = 50999
        include_examples = true
        extension_prefix = "CRO"
        selected_extensions = []
        modules = []
        tenant_id = ""

        [[workspace.extensions]]
        kind = "core"
        key = ""
        id = "1f2e3d4c-5b6a-4790-8123-456789abcdef"
        name = "CRONUS Customer Core"
        folder = "Core"
        publisher = "CRONUS"
        id_range_from = 50000
        id_range_to = 50999
        """;
}
