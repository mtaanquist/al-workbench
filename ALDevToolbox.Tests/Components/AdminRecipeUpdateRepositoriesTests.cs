using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Components.Pages.Admin;
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
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// "Update the repositories that use this recipe" on <c>/admin/cookbook/{id}</c>
/// (issue #626).
///
/// <para>Named user: an admin who has just fixed a bug in a recipe and wants the
/// fix in every repository that took it. The rules pinned here are that the card
/// appears only when it could act, that each row reports for itself, and that one
/// repository refusing does not stop the others - the point of the feature is
/// reach.</para>
///
/// <para>These renders are this card's evidence: there is no browser in this
/// environment to screenshot.</para>
/// </summary>
public sealed class AdminRecipeUpdateRepositoriesTests : IDisposable
{
    private const int UserId = 871;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string FirstRepo = "cronus-dk/solution-a";
    private const string SecondRepo = "cronus-dk/solution-b";
    private const string BaseSha = "base-commit-sha";
    private const string Branch = "aldt/recipe-doc-attachments";

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public AdminRecipeUpdateRepositoriesTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("admin@cronus.example");
        auth.SetRoles("Admin");
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<RecipeService>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ProjectAccess>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddScoped<ApplicationVersionService>();
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
            Email = "admin@cronus.example",
            DisplayName = "admin@cronus.example",
            PasswordHash = "x",
            Role = UserRole.Admin,
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
    public async Task A_recipe_nobody_has_put_in_a_repository_gets_no_card()
    {
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            await _db.NewRecipeService(ctx).RecordDownloadAsync(recipeId, "CRONUS A/S", UserId);
        }
        await ReadyAsync();

        var cut = _ctx.Render<AdminRecipeEdit>(p => p.Add(c => c.Id, recipeId));

        cut.WaitForAssertion(() =>
        {
            // The history is still there; the card that acts on it is not,
            // because there is nowhere for it to act.
            cut.Markup.Should().Contain("Where this recipe has been used");
            cut.Markup.Should().NotContain("Update the repositories that use this recipe");
        });
    }

    [Fact]
    public async Task The_history_names_the_repository_a_recipe_was_sent_to()
    {
        var recipeId = await SeedRecipeAsync();
        await ApplyToAsync(recipeId, FirstRepo);
        await ReadyAsync();

        var cut = _ctx.Render<AdminRecipeEdit>(p => p.Add(c => c.Id, recipeId));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Pull request");
            cut.Find($"a[href='https://github.com/{FirstRepo}']").Should().NotBeNull();
        });
    }

    [Fact]
    public async Task Saving_an_existing_recipe_stays_on_the_page_so_the_update_buttons_can_be_used()
    {
        // "Save your changes to enable" was unfollowable: saving navigated away
        // to the Cookbook list, so nobody could ever reach the buttons it was
        // pointing at.
        var recipeId = await SeedRecipeAsync();
        await ApplyToAsync(recipeId, FirstRepo);
        await ReadyAsync();
        var nav = _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var before = nav.Uri;

        var cut = _ctx.Render<AdminRecipeEdit>(p => p.Add(c => c.Id, recipeId));
        // The card only renders once the whole load has finished; clicking Save
        // before then would run a second query on the page's own DbContext.
        cut.WaitForElement("button:contains('Open pull request')");
        await cut.Find("button.btn--primary").ClickAsync(new());

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Saved.");
            cut.Find("#recipe-title").Should().NotBeNull("the person is still on the recipe they saved");
            cut.Find("button:contains('Open pull request')").HasAttribute("disabled").Should().BeFalse(
                "the form matches the saved recipe, so the update can be sent from here");
        });
        nav.Uri.Should().Be(before, "saving an existing recipe does not navigate away");
    }

    [Fact]
    public async Task One_row_opens_one_pull_request_and_says_which()
    {
        var recipeId = await SeedRecipeAsync();
        await ApplyToAsync(recipeId, FirstRepo);
        await ReadyAsync();

        var cut = _ctx.Render<AdminRecipeEdit>(p => p.Add(c => c.Id, recipeId));
        cut.WaitForElement("button:contains('Open pull request')").Click();

        cut.WaitForAssertion(() =>
            cut.Find("a[href='https://github.com/cronus-dk/solution-a/pull/11']").TextContent
                .Should().Contain("Pull request #11"));
    }

    [Fact]
    public async Task Open_all_reports_per_repository_and_one_refusal_does_not_stop_the_rest()
    {
        var recipeId = await SeedRecipeAsync();
        await ApplyToAsync(recipeId, FirstRepo);
        await ApplyToAsync(recipeId, SecondRepo);
        var api = await ReadyAsync();
        // The second repository has been archived since; GitHub says no to the
        // pull request. The first one still has to get its fix.
        api.On(HttpMethod.Post, $"/repos/{SecondRepo}/pulls", HttpStatusCode.Forbidden,
            "{\"message\":\"Repository was archived so is read-only\"}");

        var cut = _ctx.Render<AdminRecipeEdit>(p => p.Add(c => c.Id, recipeId));
        cut.WaitForElement("button:contains('Open a pull request in each')").Click();
        // Reaching every repository at once asks first.
        cut.WaitForElement(".confirm-dialog__actions .btn--primary").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("a[href='https://github.com/cronus-dk/solution-a/pull/11']").Should().NotBeNull();
            cut.Find(".field-error").TextContent.Should().Contain("archived");
        });
    }

    // --- helpers ------------------------------------------------------------

    private async Task<int> SeedRecipeAsync()
    {
        await using var ctx = _db.NewContext();
        var recipe = RecipeBuilder.Default("Doc attachments").WithFile("Attach.Codeunit.al", "// attach");
        ctx.Recipes.Add(recipe);
        await ctx.SaveChangesAsync();
        return recipe.Id;
    }

    private async Task ApplyToAsync(int recipeId, string repository)
    {
        await using var ctx = _db.NewContext();
        await _db.NewRecipeService(ctx).RecordRepositoryApplyAsync(recipeId, repository, null, UserId);
    }

    /// <summary>
    /// A deployment, an organisation and this admin's own GitHub account all in
    /// place, with both repositories ready to take a commit.
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
            HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson());
        foreach (var repo in new[] { FirstRepo, SecondRepo })
        {
            var number = repo == FirstRepo ? 11 : 12;
            api.On(HttpMethod.Get, $"/repos/{repo}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(repo))
               .On(HttpMethod.Get, $"/repos/{repo}/pulls", HttpStatusCode.OK, "[]")
               .On(HttpMethod.Get, $"/repos/{repo}/git/ref/heads/{Branch}", HttpStatusCode.NotFound)
               .On(HttpMethod.Get, $"/repos/{repo}/git/ref/heads/main", HttpStatusCode.OK,
                    $"{{\"ref\":\"refs/heads/main\",\"object\":{{\"sha\":\"{BaseSha}\"}}}}")
               .On(HttpMethod.Get, $"/repos/{repo}/git/commits/{BaseSha}", HttpStatusCode.OK,
                    $"{{\"sha\":\"{BaseSha}\",\"tree\":{{\"sha\":\"base-tree-sha\"}}}}")
               .On(HttpMethod.Post, $"/repos/{repo}/git/blobs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("blob-sha"))
               .On(HttpMethod.Post, $"/repos/{repo}/git/trees", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-tree-sha"))
               .On(HttpMethod.Post, $"/repos/{repo}/git/commits", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit-sha"))
               .On(HttpMethod.Post, $"/repos/{repo}/git/refs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit-sha"))
               .On(HttpMethod.Post, $"/repos/{repo}/pulls", HttpStatusCode.Created,
                    $"{{\"number\":{number},\"html_url\":\"https://github.com/{repo}/pull/{number}\"}}");
        }

        _db.AddGitHubServices(_ctx.Services, api);
        return api;
    }
}
