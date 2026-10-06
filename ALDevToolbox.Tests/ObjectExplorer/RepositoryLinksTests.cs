using ALDevToolbox.Domain.ValueObjects;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The page and clone links a solution repository's stored clone URL turns
/// into (issue #1076). The stored URL is whatever the clone needs, so the web
/// link has to shed the clone-only parts to land on the repository page.
/// </summary>
public sealed class RepositoryLinksTests
{
    [Theory]
    [InlineData("https://github.com/cronus-dk/base-app.git", "https://github.com/cronus-dk/base-app")]
    [InlineData("https://github.com/cronus-dk/base-app", "https://github.com/cronus-dk/base-app")]
    [InlineData("https://github.com/cronus-dk/base-app/", "https://github.com/cronus-dk/base-app")]
    [InlineData("  https://github.com/cronus-dk/base-app.GIT  ", "https://github.com/cronus-dk/base-app")]
    [InlineData("https://x-access-token@github.com/cronus-dk/base-app.git", "https://github.com/cronus-dk/base-app")]
    public void A_github_clone_url_links_to_the_repository_page(string cloneUrl, string expected) =>
        RepositoryLinks.WebUrl(RepositoryProvider.GitHub, cloneUrl).Should().Be(expected);

    [Theory]
    [InlineData("https://cronus@dev.azure.com/cronus/Base/_git/BaseApp", "https://dev.azure.com/cronus/Base/_git/BaseApp")]
    [InlineData("https://dev.azure.com/cronus/Base/_git/BaseApp", "https://dev.azure.com/cronus/Base/_git/BaseApp")]
    [InlineData("https://cronus.visualstudio.com/Base/_git/BaseApp", "https://cronus.visualstudio.com/Base/_git/BaseApp")]
    [InlineData("https://dev.azure.com/cronus/Base%20App/_git/Base%20App", "https://dev.azure.com/cronus/Base%20App/_git/Base%20App")]
    public void An_azure_devops_clone_url_drops_the_user_name(string cloneUrl, string expected) =>
        RepositoryLinks.WebUrl(RepositoryProvider.AzureDevOps, cloneUrl).Should().Be(expected);

    [Fact]
    public void An_azure_devops_repository_named_dot_git_keeps_its_name() =>
        RepositoryLinks.WebUrl(RepositoryProvider.AzureDevOps, "https://dev.azure.com/cronus/Base/_git/tools.git")
            .Should().Be("https://dev.azure.com/cronus/Base/_git/tools.git");

    [Theory]
    [InlineData(RepositoryProvider.GitHub, "")]
    [InlineData(RepositoryProvider.GitHub, "not a url")]
    [InlineData(RepositoryProvider.GitHub, "http://github.com/cronus-dk/base-app")]
    [InlineData(RepositoryProvider.GitHub, "git@github.com:cronus-dk/base-app.git")]
    [InlineData(RepositoryProvider.GitHub, "https://dev.azure.com/cronus/Base/_git/BaseApp")]
    [InlineData(RepositoryProvider.AzureDevOps, "https://github.com/cronus-dk/base-app")]
    [InlineData(RepositoryProvider.GitHub, "https://github.com/")]
    [InlineData(RepositoryProvider.GitHub, "javascript:alert(1)")]
    public void A_url_that_is_not_on_the_providers_host_gets_no_link(RepositoryProvider provider, string url) =>
        RepositoryLinks.WebUrl(provider, url).Should().BeNull();

    [Fact]
    public void The_vs_code_link_carries_the_clone_url_escaped() =>
        RepositoryLinks.VsCodeCloneUrl("https://cronus@dev.azure.com/cronus/Base/_git/BaseApp")
            .Should().Be("vscode://vscode.git/clone?url=https%3A%2F%2Fcronus%40dev.azure.com%2Fcronus%2FBase%2F_git%2FBaseApp");
}
