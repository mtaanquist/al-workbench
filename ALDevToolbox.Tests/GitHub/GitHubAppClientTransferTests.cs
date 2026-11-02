using System.Net;
using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The parts of the client that are about <em>where</em> a request goes and
/// <em>how long</em> it may take, rather than about what GitHub said.
///
/// <para>Both were review findings. The upload address comes back inside
/// GitHub's own answer and the installation token is attached to a request going
/// there, so the host is checked first. And the client's timeout used to be one
/// ceiling for every call, which meant thirty seconds for a Release asset as well
/// as for a metadata read - so the deadline is per call now, and the two calls
/// that move a file get a longer one.</para>
/// </summary>
public sealed class GitHubAppClientTransferTests : IDisposable
{
    private const long InstallationId = 42;

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("")]
    [InlineData("{?name,label}")]
    [InlineData("/repos/cronus-dk/customer-app/releases/1/assets")]
    [InlineData("https://uploads.example.com/assets")]
    [InlineData("http://uploads.github.com/assets")]
    public async Task An_upload_address_that_is_not_GitHubs_is_refused_before_the_token_is_attached(string uploadUrl)
    {
        await ConfigureDeploymentAsync();
        var api = new FakeGitHubApi();
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);

        var act = () => client.UploadReleaseAssetAsync("ghs_installation", uploadUrl, "Customer.app", [1, 2, 3]);

        await act.Should().ThrowAsync<GitHubApiException>();
        api.Calls.Should().BeEmpty("nothing may be sent - with a credential - to an address GitHub did not name");
    }

    [Fact]
    public async Task An_upload_to_GitHubs_own_host_goes_through()
    {
        await ConfigureDeploymentAsync();
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, "/repos/cronus-dk/customer-app/releases/900/assets", HttpStatusCode.Created,
                "{\"id\":7,\"name\":\"Customer.app\",\"size\":3}");
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);

        var asset = await client.UploadReleaseAssetAsync(
            "ghs_installation",
            "https://uploads.github.com/repos/cronus-dk/customer-app/releases/900/assets{?name,label}",
            "Customer.app",
            [1, 2, 3]);

        asset.Id.Should().Be(7);
        api.Calls.Single().Should().Contain("uploads.github.com");
    }

    [Fact]
    public async Task An_ordinary_call_that_hangs_is_given_up_on_rather_than_waited_out()
    {
        // The typed client no longer carries a timeout of its own, so the
        // per-call deadline is the only thing standing between a hung GitHub and
        // a request thread held forever.
        await ConfigureDeploymentAsync();
        var api = new SlowGitHubApi(TimeSpan.FromSeconds(30));
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        client.DefaultDeadline = TimeSpan.FromMilliseconds(100);

        var act = () => client.GetInstallationTokenAsync(InstallationId);

        // The handler waits far longer than the default deadline; if the deadline
        // were not applied this test would sit for its full delay.
        (await act.Should().ThrowAsync<GitHubApiException>())
            .Which.Message.Should().Contain("time");
    }

    [Fact]
    public async Task A_repository_with_no_commits_has_an_empty_tree_rather_than_an_error()
    {
        // GitHub answers every Git Data route on a repository with no commits
        // with a 409, and a sweep must not count that as a repository it failed
        // to read.
        await ConfigureDeploymentAsync();
        var api = new FakeGitHubApi().EmptyRepository("cronus-dk/brand-new");
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);

        var tree = await client.ListTreeAsync("ghs_installation", "cronus-dk", "brand-new", "main");

        tree.Entries.Should().BeEmpty();
        tree.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Paging_stops_on_what_GitHub_returned_and_says_when_it_hit_the_cap()
    {
        // Counted on the array GitHub sent, not on the rows this client could
        // read: one row without a full_name would otherwise end the paging early
        // and hide the rest of the installation.
        await ConfigureDeploymentAsync();
        var full = "{\"total_count\":100,\"repositories\":["
            + string.Join(',', Enumerable.Range(1, 100).Select(i => FakeGitHubApi.RepositoryJson($"cronus-dk/repo{i}")))
            + "]}";
        var api = new FakeGitHubApi()
            .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK, full);
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);

        var listing = await client.ListInstallationRepositoriesAsync("ghs_installation");

        listing.Repositories.Should().HaveCount(1000);
        listing.Truncated.Should().BeTrue("there are more than the ten pages this reads");
    }

    [Fact]
    public async Task A_short_last_page_is_not_truncated()
    {
        await ConfigureDeploymentAsync();
        var api = new FakeGitHubApi()
            .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson("cronus-dk/a", "cronus-dk/b"));
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);

        var listing = await client.ListInstallationRepositoriesAsync("ghs_installation");

        listing.Repositories.Should().HaveCount(2);
        listing.Truncated.Should().BeFalse();
    }

    /// <summary>A GitHub that never answers in time. Waits, then would reply - the deadline is what ends the call.</summary>
    private sealed class SlowGitHubApi(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private async Task ConfigureDeploymentAsync()
    {
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: TestKeys.GitHubAppPem, ClearPrivateKey: false));
    }
}
