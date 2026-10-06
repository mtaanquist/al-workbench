using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Open on the host and Clone in VS Code on a solution's Repositories tab
/// (issue #1076). The named user is a consultant who opened the solution to
/// get at its code: the row should take them to the repository, or into VS
/// Code with it, without copying a URL by hand.
/// </summary>
public sealed class ProjectDetailRepositoryLinksTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public ProjectDetailRepositoryLinksTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<ProjectDetailRepositories> Render(bool canManage, params ProjectDetail.RepoRow[] rows) =>
        _ctx.Render<ProjectDetailRepositories>(p => p
            .Add(c => c.CanManage, canManage)
            .Add(c => c.Providers, new[] { RepositoryProvider.GitHub, RepositoryProvider.AzureDevOps })
            .Add(c => c.Edit, new ProjectDetail.EditModel { Repos = rows.ToList() }));

    private static ProjectDetail.RepoRow Saved(RepositoryProvider provider, string url) =>
        new() { Provider = provider, Url = url, SavedUrl = url, DisplayName = "Base app" };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_saved_github_repository_opens_on_github_and_clones_in_vs_code(bool canManage)
    {
        const string cloneUrl = "https://github.com/cronus-dk/base-app.git";
        var cut = Render(canManage, Saved(RepositoryProvider.GitHub, cloneUrl));

        var links = cut.FindAll(".pd-repo-links a");
        links.Should().HaveCount(2);
        links[0].TextContent.Should().Contain("Open in GitHub");
        links[0].GetAttribute("href").Should().Be("https://github.com/cronus-dk/base-app");
        links[0].GetAttribute("target").Should().Be("_blank");
        links[0].GetAttribute("rel").Should().Be("noopener");
        links[1].TextContent.Should().Contain("Clone in VS Code");
        links[1].GetAttribute("href").Should().Be($"vscode://vscode.git/clone?url={Uri.EscapeDataString(cloneUrl)}");
    }

    [Fact]
    public void An_azure_devops_repository_is_labelled_for_its_host()
    {
        var cut = Render(false, Saved(RepositoryProvider.AzureDevOps, "https://cronus@dev.azure.com/cronus/Base/_git/BaseApp"));

        var open = cut.Find(".pd-repo-links a[target='_blank']");
        open.TextContent.Should().Contain("Open in Azure DevOps");
        open.GetAttribute("href").Should().Be("https://dev.azure.com/cronus/Base/_git/BaseApp");
    }

    [Fact]
    public void A_row_being_edited_or_just_added_has_no_links()
    {
        var edited = Saved(RepositoryProvider.GitHub, "https://github.com/cronus-dk/base-app.git");
        edited.Url = "https://github.com/cronus-dk/base";
        var added = new ProjectDetail.RepoRow { Provider = RepositoryProvider.GitHub, Url = "https://github.com/cronus-dk/tools" };

        var cut = Render(true, edited, added);

        cut.FindAll(".pd-repo-links").Should().BeEmpty();
    }

    [Fact]
    public void A_saved_url_that_does_not_parse_has_no_links()
    {
        var cut = Render(false, Saved(RepositoryProvider.GitHub, "not a url"));

        cut.FindAll(".pd-repo-links").Should().BeEmpty();
    }
}
