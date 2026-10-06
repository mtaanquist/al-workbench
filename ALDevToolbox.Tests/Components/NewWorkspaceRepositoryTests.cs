using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Components.Pages;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Templates;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The GitHub half of <c>/templates/workspace</c> once the button has been
/// pressed: what the success state says.
///
/// <para>The rule being pinned is the one the customer-naming milestone added
/// (issue #759): creating the repository is also what registers the customer as
/// a solution, so the card has to say which solution that was and link to it.
/// Without the link the consultant has just made two things and been told about
/// one of them.</para>
/// </summary>
public sealed class NewWorkspaceRepositoryTests : IDisposable
{
    private const int UserId = 861;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string RepoName = "cronus-customer";
    private const string Repo = $"{OrgLogin}/{RepoName}";

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly FakeGitHubApi _api = WritableApi();

    public NewWorkspaceRepositoryTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("dev@cronus.example");
        // The page hands off to generate.js on submit; there is no browser here.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _ctx.Services.AddScoped<FolderTreeHydrator>();
        _ctx.Services.AddScoped<TemplateService>();
        _ctx.Services.AddScoped<ApplicationVersionService>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddScoped<WorkspaceConfigService>();
        _ctx.Services.AddSingleton<MustacheRenderer>();
        _ctx.Services.AddScoped<WorkspaceZipBuilder>();
        _ctx.Services.AddScoped<GenerationService>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ProjectAccess>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectService>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Delivery.PipelineService>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryService>();
        _ctx.Services.AddSingleton(new ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryQueue());
        _db.AddGitHubServices(_ctx.Services, _api);
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
    public async Task The_success_card_links_to_the_solution_the_customer_became()
    {
        await ReadyAsync();

        var cut = _ctx.Render<NewWorkspace>();
        cut.WaitForElement("input[name='WorkspaceName']").Input("CRONUS Customer");
        cut.WaitForElement("button:contains('Create repository')").Click();

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".ws-repo");
            card.TextContent.Should().Contain(Repo);
            // Created rather than picked, and reachable from here - a customer
            // registered somewhere the consultant cannot get to is half a
            // feature.
            card.TextContent.Should().Contain("Created the solution");
            card.TextContent.Should().Contain("CRONUS Customer");
            card.InnerHtml.Should().Contain("/solutions/");
        }, TimeSpan.FromSeconds(10));

        await using var read = _db.NewContext();
        var solution = await read.OeProjects.AsNoTracking()
            .Include(p => p.Repositories)
            .SingleAsync(p => p.Name == "CRONUS Customer");
        cut.Find(".ws-repo").InnerHtml.Should().Contain($"/solutions/{solution.Id}");
        solution.Repositories.Should().ContainSingle()
            .Which.Url.Should().Be($"https://github.com/{Repo}.git");
    }

    [Fact]
    public async Task The_success_card_names_the_new_branches_and_their_build_pipelines()
    {
        await ReadyAsync();

        var cut = _ctx.Render<NewWorkspace>();
        cut.WaitForElement("input[name='WorkspaceName']").Input("CRONUS Customer");
        cut.WaitForElement("button:contains('Create repository')").Click();

        cut.WaitForAssertion(() =>
        {
            var text = System.Text.RegularExpressions.Regex.Replace(cut.Find(".ws-repo").TextContent, @"\s+", " ");
            text.Should().Contain("Also created the test and staging branches from main.");
            text.Should().Contain("Build pipelines for main, test and staging are on the solution's Pipelines tab.");
            cut.Find(".ws-repo").InnerHtml.Should().Contain("?tab=pipelines");
        }, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The bug in #812: the busy flag was set after the handler's first await,
    /// so the render Blazor does at that yield still showed an idle button and
    /// the whole create looked like nothing had happened. Pinned by holding
    /// GitHub's create call open and looking at the button while it is in
    /// flight.
    /// </summary>
    [Fact]
    public async Task The_create_button_is_busy_while_the_repository_is_being_created()
    {
        await ReadyAsync();
        var gate = _api.PauseUntilReleased(HttpMethod.Post, $"/orgs/{OrgLogin}/repos");

        var cut = _ctx.Render<NewWorkspace>();
        cut.WaitForElement("input[name='WorkspaceName']").Input("CRONUS Customer");
        cut.WaitForElement("button:contains('Create repository')").Click();

        cut.WaitForAssertion(() =>
        {
            var button = cut.Find(".ws-repo button.btn--loading");
            button.HasAttribute("disabled").Should().BeTrue("a second press starts a second create");
            // Not only disabled - disabled on its own reads as broken.
            button.QuerySelector(".btn__spinner").Should().NotBeNull();
            button.TextContent.Should().Contain("Creating repository...");
        }, TimeSpan.FromSeconds(10));

        gate.SetResult();

        cut.WaitForAssertion(
            () => cut.Find(".ws-repo").TextContent.Should().Contain("is ready"),
            TimeSpan.FromSeconds(10));
        cut.FindAll(".ws-repo button.btn--loading").Should().BeEmpty();
    }

    /// <summary>
    /// What the consultant does next once the repository exists: open it in VS
    /// Code in one click, or take the clone command with one more (#812). The
    /// command stays on the card because the VS Code link does nothing on a
    /// machine without VS Code.
    /// </summary>
    [Fact]
    public async Task The_success_card_offers_a_copyable_clone_command_and_a_vs_code_link()
    {
        await ReadyAsync();

        var cut = _ctx.Render<NewWorkspace>();
        cut.WaitForElement("input[name='WorkspaceName']").Input("CRONUS Customer");
        cut.WaitForElement("button:contains('Create repository')").Click();

        cut.WaitForAssertion(
            () => cut.Find(".ws-repo").TextContent.Should().Contain("is ready"),
            TimeSpan.FromSeconds(10));

        var cloneUrl = $"https://github.com/{Repo}.git";

        // The copy affordance is the design system's: a delegated listener in
        // copy-to-clipboard.js reads the element the selector names, so what
        // this pins is the pairing of the two.
        var command = cut.Find("#ws-repo-clone-command");
        command.TextContent.Trim().Should().Be($"git clone {cloneUrl}");
        var copy = cut.Find(".ws-repo [data-copy-target]");
        copy.GetAttribute("data-copy-target").Should().Be("#ws-repo-clone-command");
        copy.QuerySelector("[data-copy-label]").Should().NotBeNull();

        var vscode = cut.Find(".ws-repo-actions a[href^='vscode://']");
        vscode.GetAttribute("href").Should()
            .Be($"vscode://vscode.git/clone?url={Uri.EscapeDataString(cloneUrl)}");
        vscode.TextContent.Should().Contain("Clone in VS Code");
        // Download ZIP is still the only primary button; these are outlines.
        cut.FindAll(".ws-repo .btn--primary").Should().BeEmpty();
    }

    [Fact]
    public async Task A_branch_rule_the_app_cannot_bypass_is_shown_beside_the_card_with_what_to_ask_for()
    {
        await ReadyAsync();
        // The organisation ruleset governs the default branch and the GitHub
        // App is not on its bypass list, so the push is a rule violation.
        _api.On(HttpMethod.Patch, $"/repos/{Repo}/git/refs/heads/", HttpStatusCode.UnprocessableEntity,
            FakeGitHubApi.RuleViolationJson());

        var cut = _ctx.Render<NewWorkspace>();
        cut.WaitForElement("input[name='WorkspaceName']").Input("CRONUS Customer");
        cut.WaitForElement("button:contains('Create repository')").Click();

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".ws-repo");
            // Not a success: no clone command for a repository holding one
            // placeholder file. The refusal says what an owner has to allow
            // and names the repository so the person can find it.
            card.TextContent.Should().NotContain("is ready");
            card.QuerySelectorAll(".ws-repo-clone").Should().BeEmpty();
            card.TextContent.Should().Contain("bypass");
            card.TextContent.Should().Contain(Repo);
            card.TextContent.Should().NotContain("rule violations found");
        }, TimeSpan.FromSeconds(10));
    }

    // --- helpers ------------------------------------------------------------

    /// <summary>
    /// A GitHub that answers every call creating and filling a repository
    /// makes, mirroring <c>GitHubWorkspaceRepositoryTests</c>.
    /// </summary>
    private static FakeGitHubApi WritableApi() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson())
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, $"/orgs/{OrgLogin}/members/", HttpStatusCode.NoContent)
            .On(HttpMethod.Post, $"/orgs/{OrgLogin}/repos", HttpStatusCode.Created,
                FakeGitHubApi.RepositoryJson(Repo))
            .On(HttpMethod.Put, $"/repos/{Repo}/contents/", HttpStatusCode.Created, FakeGitHubApi.FileWriteJson())
            .On(HttpMethod.Post, $"/repos/{Repo}/git/blobs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("blob-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/trees", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-tree-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/commits", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit-sha"))
            // The default branch is moved on to the workspace commit; the
            // installation bypasses the organisation's branch rules.
            .On(HttpMethod.Patch, $"/repos/{Repo}/git/refs/heads/", HttpStatusCode.OK, FakeGitHubApi.ShaJson("new-commit-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/refs", HttpStatusCode.Created, """{"ref":"refs/heads/x"}""")
            .EmptyRepository(Repo);

    /// <summary>Deployment configured, organisation connected, user linked, one template.</summary>
    private async Task ReadyAsync()
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
                GitHubInstallationId = InstallationId,
                GitHubOrgLogin = OrgLogin,
                GitHubConnectedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            await _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, _api)).LinkAsync("the-code");
        }

        await using (var ctx = _db.NewContext())
        {
            ctx.RuntimeTemplates.Add(TemplateBuilder.Default());
            await ctx.SaveChangesAsync();
        }
    }
}
