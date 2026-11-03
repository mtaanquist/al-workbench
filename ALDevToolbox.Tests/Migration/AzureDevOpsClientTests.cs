using System.Net;
using System.Text;
using ALDevToolbox.Services.Migration;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Migration;

/// <summary>
/// Listing an Azure DevOps project's repositories for Migrate repositories
/// (#1699): the token goes in a basic header, the answer becomes repositories
/// whose clone address always points at dev.azure.com, and each way Azure DevOps
/// refuses becomes an answer the page can show.
/// </summary>
public sealed class AzureDevOpsClientTests
{
    private static readonly AzureDevOpsProject Sales = new("cronus", "Sales and Service");

    private const string TwoRepositories = """
        {
          "count": 3,
          "value": [
            { "id": "b", "name": "Payroll", "defaultBranch": "refs/heads/main", "size": 2048,
              "remoteUrl": "https://evil.example/steal" },
            { "id": "a", "name": "base app", "defaultBranch": "refs/heads/master", "size": 9000000000, "isDisabled": true },
            { "id": "c", "name": "Empty", "size": 0 },
            { "name": "no id" }
          ]
        }
        """;

    [Fact]
    public async Task Lists_repositories_sorted_with_clone_addresses_built_from_the_project()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoRepositories);
        var client = NewClient(handler);

        var repos = await client.ListRepositoriesAsync(Sales, "pat-1");

        repos.Select(r => r.Name).Should().Equal("base app", "Empty", "Payroll");
        var payroll = repos.Single(r => r.Name == "Payroll");
        payroll.CloneUrl.Should().Be("https://dev.azure.com/cronus/Sales%20and%20Service/_git/Payroll",
            "the address comes from the project, never from the answer");
        payroll.DefaultBranch.Should().Be("main");
        payroll.Size.Should().Be(2048);
        repos.Single(r => r.Name == "base app").IsDisabled.Should().BeTrue();
        repos.Single(r => r.Name == "Empty").DefaultBranch.Should().BeNull();

        handler.Request!.RequestUri!.AbsoluteUri.Should().Be(
            "https://dev.azure.com/cronus/Sales%20and%20Service/_apis/git/repositories?api-version=7.1");
        handler.Request.Headers.Authorization!.Scheme.Should().Be("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(handler.Request.Headers.Authorization.Parameter!))
            .Should().Be(":pat-1");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NonAuthoritativeInformation)]
    [InlineData(HttpStatusCode.Found)]
    public async Task A_refused_token_says_so(HttpStatusCode status)
    {
        var client = NewClient(new StubHandler(status, "<html>sign in</html>"));

        var act = () => client.ListRepositoriesAsync(Sales, "pat");

        var ex = (await act.Should().ThrowAsync<AzureDevOpsApiException>()).Which;
        ex.Failure.Should().Be(AzureDevOpsFailure.Unauthorized);
        ex.Message.Should().Contain("Account → Repository access");
    }

    [Fact]
    public async Task A_missing_project_names_it()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.NotFound, "{}"));

        var act = () => client.ListRepositoriesAsync(Sales, "pat");

        var ex = (await act.Should().ThrowAsync<AzureDevOpsApiException>()).Which;
        ex.Failure.Should().Be(AzureDevOpsFailure.NotFound);
        ex.Message.Should().Contain("Sales and Service").And.Contain("cronus");
    }

    [Fact]
    public async Task A_server_error_or_an_unreadable_answer_is_unavailable()
    {
        var failing = NewClient(new StubHandler(HttpStatusCode.InternalServerError, "oops"));
        (await ((Func<Task>)(() => failing.ListRepositoriesAsync(Sales, "pat"))).Should().ThrowAsync<AzureDevOpsApiException>())
            .Which.Failure.Should().Be(AzureDevOpsFailure.Unavailable);

        var garbled = NewClient(new StubHandler(HttpStatusCode.OK, "not json"));
        (await ((Func<Task>)(() => garbled.ListRepositoriesAsync(Sales, "pat"))).Should().ThrowAsync<AzureDevOpsApiException>())
            .Which.Failure.Should().Be(AzureDevOpsFailure.Unavailable);
    }

    internal static AzureDevOpsClient NewClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com") }, NullLogger<AzureDevOpsClient>.Instance);

    internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
