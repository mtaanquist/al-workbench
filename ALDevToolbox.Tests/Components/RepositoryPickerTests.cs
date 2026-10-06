using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The shared repository picker's states (issue #623). Each not-ready state has
/// to offer the next step rather than report a fact, and which step depends on
/// who is looking - so these assert the copy and the link, not only that
/// something rendered.
/// </summary>
public sealed class RepositoryPickerTests : IDisposable
{
    private const int UserId = 801;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly BunitAuthorizationContext _auth;

    public RepositoryPickerTests()
    {
        _auth = _ctx.AddAuthorization();
        _auth.SetAuthorized("dev@cronus.example");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<OrganizationConfigService>();
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
    public void With_no_app_on_the_server_it_says_so_without_sending_anyone_anywhere()
    {
        _db.AddGitHubServices(_ctx.Services);

        var cut = _ctx.Render<RepositoryPicker>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("GitHub is not set up on this server yet");
            // Nothing an ordinary user or even an org Admin can act on, so no
            // button pretending otherwise.
            cut.FindAll("a.empty-state__action").Should().BeEmpty();
        });
    }

    [Fact]
    public void A_site_admin_is_offered_the_page_that_sets_github_up()
    {
        _auth.SetRoles("SiteAdmin");
        _db.AddGitHubServices(_ctx.Services);

        var cut = _ctx.Render<RepositoryPicker>();

        // The person who can fix this is the one reading it; telling them to ask
        // whoever runs the server would be telling them to ask themselves.
        cut.WaitForAssertion(() =>
            cut.Find("a.empty-state__action").GetAttribute("href")
                .Should().Be("/site-admin/settings/github"));
    }

    [Fact]
    public void The_label_is_only_a_label_when_there_is_something_to_label()
    {
        _db.AddGitHubServices(_ctx.Services);

        var cut = _ctx.Render<RepositoryPicker>();

        // No input in this state, so a <label for> would point at nothing.
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("label").Should().BeEmpty();
            cut.Markup.Should().Contain("Repository");
        });
    }

    [Fact]
    public async Task The_hosts_hint_is_held_back_until_it_is_true()
    {
        await ConfigureDeploymentAsync();
        _db.AddGitHubServices(_ctx.Services);

        var cut = _ctx.Render<RepositoryPicker>(ps => ps
            .Add(p => p.Hint, "Picking one fills this form in."));

        // Promising what picking a repository leads to, under a state that
        // cannot offer one, is a promise the page cannot keep.
        cut.WaitForAssertion(() =>
            cut.Markup.Should().NotContain("Picking one fills this form in."));
    }

    [Fact]
    public async Task A_ready_picker_does_show_the_hosts_hint()
    {
        var api = ListableApi("cronus-dk/solution-a");
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);

        var cut = _ctx.Render<RepositoryPicker>(ps => ps
            .Add(p => p.Hint, "Picking one fills this form in."));

        cut.WaitForAssertion(() =>
        {
            cut.Find("label").GetAttribute("for").Should().Be("repo-picker");
            cut.Markup.Should().Contain("Picking one fills this form in.");
        });
    }

    [Fact]
    public async Task Having_access_to_nothing_is_a_dead_end_with_a_way_out()
    {
        var api = ListableApi();
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);

        var cut = _ctx.Render<RepositoryPicker>();
        await cut.WaitForElement("input").FocusAsync(new());

        // A full stop, not a footnote: the same quiet empty state the guidance
        // states use, and the retry this control already has.
        cut.WaitForAssertion(() =>
        {
            cut.Find(".empty-state").TextContent.Should().Contain("cannot open any");
            cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Try again");
        });
    }

    [Fact]
    public async Task The_chosen_repositorys_branch_is_named_rather_than_left_bare()
    {
        var api = ListableApi("cronus-dk/solution-a");
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);

        var cut = _ctx.Render<RepositoryPicker>();
        await cut.WaitForElement("input").FocusAsync(new());
        cut.WaitForElement("button.repo-picker__result").Click();

        // An unlabelled branch beside a repository reads as the branch about to
        // be written to, which is the one thing it is not.
        cut.WaitForAssertion(() =>
            cut.Find(".repo-picker__branch").TextContent.Should().Contain("default branch"));
    }

    [Fact]
    public async Task An_admin_of_an_unconnected_organisation_is_offered_the_page_that_connects_one()
    {
        _auth.SetRoles("Admin");
        await ConfigureDeploymentAsync();
        _db.AddGitHubServices(_ctx.Services);

        var cut = _ctx.Render<RepositoryPicker>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No GitHub organisation is connected yet");
            cut.Find("a.empty-state__action").GetAttribute("href")
                .Should().Be("/admin/administration/repositories");
        });
    }

    [Fact]
    public async Task A_member_of_an_unconnected_organisation_is_told_who_can_fix_it()
    {
        await ConfigureDeploymentAsync();
        _db.AddGitHubServices(_ctx.Services);

        var cut = _ctx.Render<RepositoryPicker>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Ask one of your administrators");
            cut.FindAll("a.empty-state__action").Should().BeEmpty(
                "sending someone to a page they cannot use is worse than telling them who can");
        });
    }

    [Fact]
    public async Task An_unlinked_user_is_offered_the_way_to_connect_their_own_account()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        _db.AddGitHubServices(_ctx.Services);

        var cut = _ctx.Render<RepositoryPicker>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Connect your GitHub account");
            cut.Find("a.empty-state__action").GetAttribute("href").Should().Be("/account/repos");
        });
    }

    [Fact]
    public async Task A_ready_picker_lists_the_repositories_once_the_user_reaches_for_it()
    {
        var api = ListableApi("cronus-dk/solution-a", "cronus-dk/solution-b");
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);

        var cut = _ctx.Render<RepositoryPicker>();

        // Nothing is fetched until it is asked for: the list costs a call to
        // GitHub per repository, and most visits never open it.
        api.Calls.Should().BeEmpty();

        await cut.WaitForElement("input").FocusAsync(new());

        cut.WaitForAssertion(() =>
            cut.FindAll("button.repo-picker__result").Select(b => b.TextContent.Trim())
                .Should().Contain(t => t.Contains("solution-a")));
    }

    [Fact]
    public async Task Picking_one_hands_the_whole_repository_back_to_the_caller()
    {
        var api = ListableApi("cronus-dk/solution-a");
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);

        GitHubRepositorySummary? picked = null;
        var cut = _ctx.Render<RepositoryPicker>(ps => ps
            .Add(p => p.SelectedChanged, (GitHubRepositorySummary? r) => picked = r));

        await cut.WaitForElement("input").FocusAsync(new());
        cut.WaitForElement("button.repo-picker__result").Click();

        // The default branch travels with it: it is what a pull request targets
        // here, and what issue #624 suggests as a pipeline's branch.
        picked.Should().NotBeNull();
        picked!.FullName.Should().Be("cronus-dk/solution-a");
        picked.DefaultBranch.Should().Be("main");
    }

    [Fact]
    public async Task A_github_that_will_not_answer_offers_a_retry_rather_than_breaking_the_page()
    {
        var api = ListableApi("cronus-dk/solution-a");
        await ReadyAsync(api);
        // Everything is in place; GitHub itself is the thing that is down.
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.ServiceUnavailable, "{\"message\":\"Server Error\"}"));

        var cut = _ctx.Render<RepositoryPicker>();
        await cut.WaitForElement("input").FocusAsync(new());

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("could not reach GitHub");
            cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Try again");
        });
    }

    [Fact]
    public async Task A_caller_filter_narrows_what_is_offered()
    {
        var api = ListableApi("cronus-dk/solution-a", "cronus-dk/tooling");
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);

        var cut = _ctx.Render<RepositoryPicker>(ps => ps
            .Add(p => p.Filter, (Func<GitHubRepositorySummary, bool>)(r => r.Name.StartsWith("solution"))));

        await cut.WaitForElement("input").FocusAsync(new());

        cut.WaitForAssertion(() =>
            cut.FindAll("button.repo-picker__result").Should().HaveCount(1));
    }

    [Fact]
    public void A_chosen_repository_replaces_the_search_box_with_the_way_back()
    {
        _db.AddGitHubServices(_ctx.Services);

        var cut = _ctx.Render<RepositoryPicker>(ps => ps
            .Add(p => p.Selected, new GitHubRepositorySummary(
                "cronus-dk/solution-a", "cronus-dk", "solution-a", "main", true, null,
                "https://github.com/cronus-dk/solution-a", "https://github.com/cronus-dk/solution-a.git")));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("cronus-dk/solution-a");
            cut.FindAll("input").Should().BeEmpty();
            cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Change");
        });
    }

    [Fact]
    public async Task A_repository_the_caller_already_has_cannot_be_picked_again()
    {
        var api = ListableApi("cronus-dk/solution-a", "cronus-dk/solution-b");
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);

        var picked = 0;
        var cut = _ctx.Render<RepositoryPicker>(ps => ps
            .Add(p => p.IsAlreadyAdded,
                (Func<GitHubRepositorySummary, bool>)(r => r.Name == "solution-a"))
            .Add(p => p.SelectedChanged, (GitHubRepositorySummary? _) => picked++));

        await cut.WaitForElement("input").FocusAsync(new());

        // Refusing the second click after the fact reads as "you added two" to
        // anyone who does not stop to read the note, so the click cannot happen.
        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("button.repo-picker__result");
            rows.Should().HaveCount(2);
            var alreadyAdded = rows.Single(r => r.TextContent.Contains("solution-a"));
            alreadyAdded.HasAttribute("disabled").Should().BeTrue();
            alreadyAdded.TextContent.Should().Contain("Added");
            rows.Single(r => r.TextContent.Contains("solution-b"))
                .HasAttribute("disabled").Should().BeFalse();
        });
        picked.Should().Be(0);
    }

    [Fact]
    public async Task A_caller_adding_several_in_a_row_gets_the_search_box_straight_back()
    {
        var api = ListableApi("cronus-dk/solution-a");
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);

        var seen = new List<GitHubRepositorySummary?>();
        var cut = _ctx.Render<RepositoryPicker>(ps => ps
            .Add(p => p.ClearAfterSelect, true)
            .Add(p => p.SelectedChanged, (GitHubRepositorySummary? r) => seen.Add(r)));

        await cut.WaitForElement("input").FocusAsync(new());
        cut.WaitForElement("button.repo-picker__result").Click();

        // The caller is handed the pick and then given the control back, so a
        // chosen row it has already moved past never flashes on screen.
        seen.Select(r => r?.FullName).Should().Equal("cronus-dk/solution-a", null);
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".repo-picker__chosen").Should().BeEmpty();
            cut.FindAll("input").Should().HaveCount(1);
        });
    }

    [Fact]
    public async Task Mounting_it_inside_a_page_that_is_still_loading_does_not_break_the_page()
    {
        var api = ListableApi("cronus-dk/solution-a");
        await ReadyAsync(api);
        _db.AddGitHubServices(_ctx.Services, api);

        // The host is every page that carries this control: its own initialiser
        // is still awaiting the database when Blazor renders the first time, and
        // that first render is what mounts the picker. If the picker reads the
        // database as it mounts, the two reads meet on one scoped DbContext and
        // the page dies with "A second operation was started on this context
        // instance" - which is a crash in the host, caused by a child it only
        // placed on the page.
        var cut = _ctx.Render<SlowLoadingHost>();

        cut.WaitForAssertion(() =>
            cut.Find("input.repo-picker__input").Should().NotBeNull());
        cut.Instance.Failure.Should().BeNull();
    }

    /// <summary>
    /// A stand-in for New Extension, Translator and the solution detail page:
    /// several awaited reads in <c>OnInitializedAsync</c>, with the picker on
    /// the page from the first render. Written in C# rather than markup because
    /// the test project compiles no Razor.
    /// </summary>
    private sealed class SlowLoadingHost : ComponentBase
    {
        [Inject] public ALDevToolbox.Data.AppDbContext Db { get; set; } = default!;

        /// <summary>What the host's own load died of, if it died.</summary>
        public Exception? Failure { get; private set; }

        protected override async Task OnInitializedAsync()
        {
            try
            {
                // New Extension awaits four of these in a row, deliberately, so
                // the page paints while they run. That is the window.
                for (var i = 0; i < 4; i++)
                {
                    await Db.Users.AsNoTracking().Select(u => u.Id).ToListAsync();
                }
            }
            catch (Exception ex)
            {
                Failure = ex;
            }
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<RepositoryPicker>(0);
            builder.CloseComponent();
        }
    }

    // --- helpers ------------------------------------------------------------

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

    private async Task ReadyAsync(FakeGitHubApi api)
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        api.On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
           .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson());
        await using var ctx = _db.NewContext();
        var access = _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, api));
        await access.LinkAsync("the-code");
        api.Calls.Clear();
        api.Bodies.Clear();
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
}
