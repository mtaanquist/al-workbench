using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Components.Pages;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
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
using ALDevToolbox.Services.Cookbook;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The GitHub half of <c>/cookbook/{id}</c> (issue #626): the second way out of
/// the download dialog.
///
/// <para>Named user: a consultant who has found the right recipe and wants it in
/// the customer's repository as a reviewable change rather than a ZIP on their
/// desktop. The rules pinned here are the ones the markup alone would not show -
/// that the section is not offered at all to somebody who could not use it, that
/// the download stays the dialog's only primary action, and that a refusal is
/// worded where the person is standing rather than swallowed.</para>
///
/// <para>These renders are this feature's evidence: there is no browser in this
/// environment to screenshot.</para>
/// </summary>
public sealed class RecipeDetailRepositoryTests : IDisposable
{
    private const int UserId = 861;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string Repo = "cronus-dk/solution-a";
    private const string BaseSha = "base-commit-sha";
    private const string Branch = "aldt/recipe-doc-attachments";

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public RecipeDetailRepositoryTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("dev@cronus.example");
        // The download hands off through location.assign; there is no browser here.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<RecipeService>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ProjectAccess>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddSingleton<MarkdownRenderer>();
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
    public async Task With_no_github_connected_the_dialog_is_only_the_download()
    {
        // Nothing is set up on this server, so the section would only ever
        // explain a door this person cannot open. The download is unchanged.
        var recipeId = await SeedRecipeAsync();
        _db.AddGitHubServices(_ctx.Services);

        var cut = _ctx.Render<RecipeDetail>(p => p.Add(c => c.Id, recipeId));
        cut.WaitForElement("button:contains('Download ZIP...')", TimeSpan.FromSeconds(5)).Click();

        cut.WaitForAssertion(timeout: TimeSpan.FromSeconds(5), assertion: () =>
        {
            cut.Find(".confirm-dialog").TextContent.Should().Contain("Customer");
            cut.FindAll(".rd-repo").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task With_github_ready_the_dialog_offers_a_repository_beside_the_download()
    {
        var recipeId = await SeedRecipeAsync();
        await ReadyAsync();

        var cut = _ctx.Render<RecipeDetail>(p => p.Add(c => c.Id, recipeId));
        cut.WaitForElement("button:contains('Download ZIP...')", TimeSpan.FromSeconds(5)).Click();

        cut.WaitForAssertion(timeout: TimeSpan.FromSeconds(5), assertion: () =>
        {
            var section = cut.Find(".rd-repo").TextContent;
            section.Should().Contain("Add this recipe's files to a repository instead");
            // The download stays the one primary action in the dialog.
            cut.FindAll(".confirm-dialog .btn--primary").Count.Should().Be(1);
        });
    }

    [Fact]
    public async Task Opening_a_pull_request_closes_the_dialog_and_links_to_it()
    {
        var recipeId = await SeedRecipeAsync();
        await ReadyAsync();

        var cut = _ctx.Render<RecipeDetail>(p => p.Add(c => c.Id, recipeId));
        cut.WaitForElement("button:contains('Download ZIP...')", TimeSpan.FromSeconds(5)).Click();
        await PickTheRepositoryAsync(cut);
        cut.WaitForElement("button:contains('Open pull request')", TimeSpan.FromSeconds(5)).Click();

        cut.WaitForAssertion(timeout: TimeSpan.FromSeconds(5), assertion: () =>
        {
            // The dialog is done with; what is left on the page is the link the
            // consultant has to send on.
            cut.FindAll(".confirm-dialog").Should().BeEmpty();
            var alert = cut.Find(".alert--success");
            alert.TextContent.Should().Contain("Pull request #11").And.Contain(Repo).And.Contain(Branch);
            alert.QuerySelector("a")!.GetAttribute("href")
                .Should().Be($"https://github.com/{Repo}/pull/11");
        });

        await using var verify = _db.NewContext();
        var row = await verify.RecipeDownloads.SingleAsync(d => d.RecipeId == recipeId);
        row.Repository.Should().Be(Repo);
    }

    [Fact]
    public async Task A_refusal_is_worded_beside_the_picker_and_the_dialog_stays_open()
    {
        var recipeId = await SeedRecipeAsync();
        var api = await ReadyAsync();
        // The repository has no commits yet, which is a refusal the person can
        // do something about.
        api.On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/main", HttpStatusCode.NotFound);

        var cut = _ctx.Render<RecipeDetail>(p => p.Add(c => c.Id, recipeId));
        cut.WaitForElement("button:contains('Download ZIP...')", TimeSpan.FromSeconds(5)).Click();
        await PickTheRepositoryAsync(cut);
        cut.WaitForElement("button:contains('Open pull request')", TimeSpan.FromSeconds(5)).Click();

        cut.WaitForAssertion(timeout: TimeSpan.FromSeconds(5), assertion: () =>
        {
            cut.Find(".rd-repo .field-error").TextContent.Should().Contain("no commits");
            cut.FindAll(".confirm-dialog").Should().NotBeEmpty("the person is still deciding what to do");
        });
    }

    // --- helpers ------------------------------------------------------------

    /// <summary>Focuses the picker, waits for its one row, and clicks it.</summary>
    private static async Task PickTheRepositoryAsync(IRenderedComponent<RecipeDetail> cut)
    {
        await cut.WaitForElement("#dl-repo", TimeSpan.FromSeconds(5)).FocusAsync(new());
        cut.WaitForElement("button.repo-picker__result", TimeSpan.FromSeconds(5)).Click();
        // The pick reaches the page through SelectedChanged on a later render;
        // clicking "Open pull request" before the chip is there races it.
        cut.WaitForElement(".repo-picker__chosen", TimeSpan.FromSeconds(5));
    }

    private async Task<int> SeedRecipeAsync()
    {
        await using var ctx = _db.NewContext();
        var recipe = RecipeBuilder.Default("Doc attachments").WithFile("Attach.Codeunit.al", "// attach");
        ctx.Recipes.Add(recipe);
        await ctx.SaveChangesAsync();
        return recipe.Id;
    }

    /// <summary>
    /// A deployment, an organisation and a user all in place, and a GitHub
    /// offering exactly one repository that will take a commit.
    /// </summary>
    private async Task<FakeGitHubApi> ReadyAsync()
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
           .On(HttpMethod.Get, $"/repos/{Repo}/pulls", HttpStatusCode.OK, "[]")
           .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/{Branch}", HttpStatusCode.NotFound)
           .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/main", HttpStatusCode.OK,
                $"{{\"ref\":\"refs/heads/main\",\"object\":{{\"sha\":\"{BaseSha}\"}}}}")
           .On(HttpMethod.Get, $"/repos/{Repo}/git/commits/{BaseSha}", HttpStatusCode.OK,
                $"{{\"sha\":\"{BaseSha}\",\"tree\":{{\"sha\":\"base-tree-sha\"}}}}")
           .On(HttpMethod.Post, $"/repos/{Repo}/git/blobs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("blob-sha"))
           .On(HttpMethod.Post, $"/repos/{Repo}/git/trees", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-tree-sha"))
           .On(HttpMethod.Post, $"/repos/{Repo}/git/commits", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit-sha"))
           .On(HttpMethod.Post, $"/repos/{Repo}/git/refs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit-sha"))
           .On(HttpMethod.Post, $"/repos/{Repo}/pulls", HttpStatusCode.Created,
                $"{{\"number\":11,\"html_url\":\"https://github.com/{Repo}/pull/11\"}}");

        _db.AddGitHubServices(_ctx.Services, api);
        return api;
    }
}
