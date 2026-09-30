using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AngleSharp.Dom;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Assisted repository entry on the Repositories tab (issue #624). The named
/// user is a BC consultant setting up a build for a customer whose code sits in
/// their company's GitHub organisation: today they paste a clone URL and find
/// out twenty minutes later, when the build fails, that they got it wrong.
///
/// <para>So the thing under test is not "a picker rendered" - it is that the
/// URL arrives from GitHub rather than from a keyboard, that picking the same
/// repository twice cannot quietly duplicate it, and that everyone the picker
/// cannot help (an Azure DevOps organisation, an unconnected one, an unlinked
/// user) is left with exactly the field they had before rather than with
/// machinery they cannot use.</para>
/// </summary>
public sealed class ProjectDetailRepositoryPickerTests : IDisposable
{
    private const int OwnerUserId = 9640;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public ProjectDetailRepositoryPickerTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("consultant@cronus.example");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ArtifactService>();
        _ctx.Services.AddScoped<ProjectService>();
        _ctx.Services.AddScoped<ProjectCustomerInfoService>();
        _ctx.Services.AddScoped<CustomerModuleService>();
        _ctx.Services.AddScoped<ProjectDiscoveryService>();
        _ctx.Services.AddScoped<PipelineService>();
        _ctx.Services.AddScoped<ReleasePipelineService>();
        _ctx.Services.AddScoped<TeamService>();
        // The Business Central tab's chain has to resolve for the page to render,
        // even though nothing here opens it. None of these clients are called.
        _ctx.Services.AddHttpClient();
        _ctx.Services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Bc.BcTokenService>();
        _ctx.Services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Bc.BcPanelCache>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.IBcAdminClient,
            ALDevToolbox.Services.ObjectExplorer.Bc.BcAdminClient>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.IBcAppManagementClient,
            ALDevToolbox.Services.ObjectExplorer.Bc.BcAppManagementClient>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.ProjectConnectionService>();
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.UpgradeActionService>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddScoped<RepositoryProviderPolicyService>();
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddSingleton(new ProjectDiscoveryQueue());
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));

        using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            Id = OwnerUserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "consultant@cronus.example",
            DisplayName = "consultant@cronus.example",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    /// <summary>
    /// These are about the settings tabs. An existing solution opens on Customer and the
    /// page takes its tab from the address - set here, not in the constructor, because
    /// several tests register more services first and resolving one closes the container.
    /// </summary>
    private IRenderedComponent<ProjectDetail> RenderOnSettings(int projectId)
    {
        var nav = _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo(Microsoft.AspNetCore.Components.NavigationManagerExtensions.GetUriWithQueryParameter(nav, "tab", "general"));
        return _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    // --- The reason this exists ---------------------------------------------

    [Fact]
    public async Task Picking_a_repository_fills_the_row_from_what_github_says_it_is()
    {
        var projectId = await SeedProjectAsync();
        var api = ListableApi("cronus-dk/base-app");
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);

        var cut = RenderOnSettings(projectId);
        await OpenReposTabAsync(cut);
        await PickAsync(cut, "base-app");

        // The clone URL is GitHub's own, not one anyone typed - which is the
        // whole point of the control.
        Url(cut, 0).GetAttribute("value").Should().Be("https://github.com/cronus-dk/base-app.git");
        DisplayName(cut, 0).GetAttribute("value").Should().Be("base-app");
        // The branch a build will use, named where the choice was made. A
        // solution repository has no branch of its own to set.
        cut.Markup.Should().Contain("Builds use its main branch");
    }

    [Fact]
    public async Task A_picked_repository_saves_as_an_ordinary_repository_row()
    {
        var projectId = await SeedProjectAsync();
        var api = ListableApi("cronus-dk/base-app");
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);

        var cut = RenderOnSettings(projectId);
        await OpenReposTabAsync(cut);
        await PickAsync(cut, "base-app");
        await SaveAsync(cut);

        await using var verify = _db.NewContext();
        var saved = await verify.OeProjectRepositories.AsNoTracking()
            .Where(r => r.ProjectId == projectId).ToListAsync();
        saved.Should().ContainSingle();
        saved[0].Provider.Should().Be(RepositoryProvider.GitHub);
        saved[0].Url.Should().Be("https://github.com/cronus-dk/base-app.git");
        saved[0].DisplayName.Should().Be("base-app");
    }

    [Fact]
    public async Task The_same_repository_picked_twice_is_refused_rather_than_listed_twice()
    {
        var projectId = await SeedProjectAsync();
        var api = ListableApi("cronus-dk/base-app");
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);

        var cut = RenderOnSettings(projectId);
        await OpenReposTabAsync(cut);
        await PickAsync(cut, "base-app");
        await PickAsync(cut, "base-app");

        cut.FindAll("input[aria-label='Repository URL']").Should().ContainSingle();
        cut.Markup.Should().Contain("base-app is already in this solution");
    }

    /// <summary>
    /// GitHub's clone URL ends in <c>.git</c> and a pasted one usually does not,
    /// so the two spellings of one repository have to be recognised as the same
    /// repository - otherwise the picker's first act is to duplicate a row the
    /// consultant added last month.
    /// </summary>
    [Fact]
    public async Task A_repository_already_added_by_hand_is_not_added_again_over_a_git_suffix()
    {
        var projectId = await SeedProjectAsync("https://github.com/cronus-dk/base-app");
        var api = ListableApi("cronus-dk/base-app");
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);

        var cut = RenderOnSettings(projectId);
        await OpenReposTabAsync(cut);
        await PickAsync(cut, "base-app");

        cut.FindAll("input[aria-label='Repository URL']").Should().ContainSingle();
        cut.Markup.Should().Contain("already in this solution");
    }

    [Fact]
    public async Task Typing_a_url_by_hand_still_works_beside_the_picker()
    {
        var projectId = await SeedProjectAsync();
        var api = ListableApi("cronus-dk/base-app");
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);

        var cut = RenderOnSettings(projectId);
        await OpenReposTabAsync(cut);

        // The escape hatch for Azure DevOps and for repositories outside the
        // connected organisation: an empty row you fill in yourself.
        await ClickAsync(cut, "Add a repository URL");
        await cut.InvokeAsync(() => Url(cut, 0).Input("https://dev.azure.com/cronus/al/_git/legacy"));
        await cut.InvokeAsync(() => Provider(cut, 0).Change(nameof(RepositoryProvider.AzureDevOps)));
        await SaveAsync(cut);

        await using var verify = _db.NewContext();
        var saved = await verify.OeProjectRepositories.AsNoTracking()
            .Where(r => r.ProjectId == projectId).ToListAsync();
        saved.Should().ContainSingle();
        saved[0].Provider.Should().Be(RepositoryProvider.AzureDevOps);
        saved[0].Url.Should().Be("https://dev.azure.com/cronus/al/_git/legacy");
    }

    // --- Everyone the picker cannot help ------------------------------------

    [Fact]
    public async Task An_organisation_on_azure_devops_is_shown_no_github_machinery()
    {
        var projectId = await SeedProjectAsync();
        var api = ListableApi("cronus-dk/base-app");
        await ReadyAsync(api);
        // Everything on the GitHub side is in place; this organisation simply
        // does not build from GitHub.
        await using (var ctx = _db.NewContext())
        {
            await _db.NewRepositoryProviderPolicyService(ctx)
                .SaveAllowedProvidersAsync(new[] { RepositoryProvider.AzureDevOps });
        }
        _db.AddGitHubServices(_ctx.Services, api);

        var cut = RenderOnSettings(projectId);
        await OpenReposTabAsync(cut);

        cut.FindAll(".repo-picker").Should().BeEmpty();
        cut.Markup.Should().NotContain("GitHub");
        ButtonLabels(cut).Should().Contain("Add repository", "the tab is exactly what it was before");
    }

    [Fact]
    public async Task An_unconnected_organisation_gets_the_tab_it_had_before_rather_than_a_blocker()
    {
        var projectId = await SeedProjectAsync();
        _db.AddGitHubServices(_ctx.Services);

        var cut = RenderOnSettings(projectId);
        await OpenReposTabAsync(cut);

        cut.FindAll(".repo-picker").Should().BeEmpty();
        ButtonLabels(cut).Should().Contain("Add repository");
    }

    [Fact]
    public async Task A_user_who_has_not_connected_their_github_account_is_not_stopped_from_typing_a_url()
    {
        var projectId = await SeedProjectAsync();
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        // Connected organisation, unlinked person: this is assistance they
        // cannot have yet, not a step they have to complete first.
        _db.AddGitHubServices(_ctx.Services, ListableApi("cronus-dk/base-app"));

        var cut = RenderOnSettings(projectId);
        await OpenReposTabAsync(cut);

        cut.FindAll(".repo-picker").Should().BeEmpty();
        await ClickAsync(cut, "Add repository");
        cut.FindAll("input[aria-label='Repository URL']").Should().ContainSingle();
    }

    /// <summary>
    /// The picker's readiness is read from the database, so an unreachable
    /// GitHub cannot hold up or break the page it sits on.
    /// </summary>
    [Fact]
    public async Task An_unreachable_github_still_renders_the_tab()
    {
        var projectId = await SeedProjectAsync();
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        await LinkAsync(ListableApi());
        // No handler at all: every call to GitHub throws.
        _db.AddGitHubServices(_ctx.Services);

        var cut = RenderOnSettings(projectId);
        await OpenReposTabAsync(cut);

        cut.Find(".repo-picker").Should().NotBeNull();
        ButtonLabels(cut).Should().Contain("Add a repository URL");
    }

    [Fact]
    public async Task Someone_who_cannot_manage_the_solution_is_offered_nothing_to_pick()
    {
        var projectId = await SeedProjectAsync();
        var api = ListableApi("cronus-dk/base-app");
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);
        _db.OrgContext.CurrentUserId = OwnerUserId + 1;

        var cut = RenderOnSettings(projectId);
        await OpenReposTabAsync(cut);

        cut.FindAll(".repo-picker").Should().BeEmpty();
    }

    /// <summary>Generate stays the app's only primary button; this tab adds none.</summary>
    [Fact]
    public async Task The_picker_adds_no_second_primary_button()
    {
        var projectId = await SeedProjectAsync();
        var api = ListableApi("cronus-dk/base-app");
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);

        var cut = RenderOnSettings(projectId);
        await OpenReposTabAsync(cut);

        cut.FindAll(".settings__body .btn--primary").Should().BeEmpty();
    }

    // --- helpers ------------------------------------------------------------

    private async Task<int> SeedProjectAsync(string? existingRepoUrl = null)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS Denmark",
            DefaultArtifactCountry = "dk",
            CreatedByUserId = OwnerUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        if (existingRepoUrl is not null)
        {
            ctx.OeProjectRepositories.Add(new OeProjectRepository
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = project.Id,
                Provider = RepositoryProvider.GitHub,
                Url = existingRepoUrl,
                DisplayName = "base-app",
            });
            await ctx.SaveChangesAsync();
        }
        return project.Id;
    }

    private async Task ConfigureDeploymentAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
    }

    private async Task ConnectOrganisationAsync()
    {
        await using var ctx = _db.NewContext();
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

    private async Task LinkAsync(FakeGitHubApi api)
    {
        api.On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
           .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson());
        await using var ctx = _db.NewContext();
        var access = _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, api));
        await access.LinkAsync("the-code");
        api.Calls.Clear();
        api.Bodies.Clear();
    }

    /// <summary>Deployment configured, organisation connected, this person linked.</summary>
    private async Task ReadyAsync(FakeGitHubApi api)
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        await LinkAsync(api);
    }

    private static FakeGitHubApi ListableApi(params string[] fullNames)
    {
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson(fullNames));
        foreach (var name in fullNames)
        {
            api.On(HttpMethod.Get, $"/repos/{name}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(name));
        }
        return api;
    }

    private static async Task OpenReposTabAsync(IRenderedComponent<ProjectDetail> cut)
    {
        cut.WaitForState(() => cut.FindAll(".settings__tabs button")
            .Any(t => t.TextContent.Trim() == "Repositories"));
        var tab = cut.FindAll(".settings__tabs button").First(t => t.TextContent.Trim() == "Repositories");
        await cut.InvokeAsync(() => tab.Click());
    }

    /// <summary>Reaches for the picker, waits for the list, and clicks one row.</summary>
    private static async Task PickAsync(IRenderedComponent<ProjectDetail> cut, string name)
    {
        await cut.WaitForElement("input#pd-repo-pick").FocusAsync(new());
        cut.WaitForState(() => cut.FindAll("button.repo-picker__result")
            .Any(b => b.TextContent.Contains(name)));
        var row = cut.FindAll("button.repo-picker__result").First(b => b.TextContent.Contains(name));
        await cut.InvokeAsync(() => row.Click());
    }

    private static async Task ClickAsync(IRenderedComponent<ProjectDetail> cut, string label)
    {
        var button = cut.FindAll("button").First(b => b.TextContent.Trim() == label);
        await cut.InvokeAsync(() => button.Click());
    }

    private static async Task SaveAsync(IRenderedComponent<ProjectDetail> cut)
    {
        var save = cut.FindAll("button").First(b => b.TextContent.Contains("Save solution"));
        await cut.InvokeAsync(() => save.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));
        cut.WaitForState(() => cut.FindAll(".alert--success").Any() || cut.FindAll(".field-error").Any());
    }

    private static IElement Url(IRenderedComponent<ProjectDetail> cut, int row) =>
        cut.FindAll("input[aria-label='Repository URL']")[row];

    private static IElement DisplayName(IRenderedComponent<ProjectDetail> cut, int row) =>
        cut.FindAll("input[aria-label='Display name']")[row];

    private static IElement Provider(IRenderedComponent<ProjectDetail> cut, int row) =>
        cut.FindAll("select[aria-label='Host']")[row];

    private static List<string> ButtonLabels(IRenderedComponent<ProjectDetail> cut) =>
        cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();
}
