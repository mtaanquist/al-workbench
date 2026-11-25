using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The membership question the compile gate asks before it builds a member's
/// fork (issue #627).
///
/// <para>Two things matter and neither is the JSON: that the question goes out
/// on the <em>installation</em> token - a webhook build has no user, so there is
/// no personal token to ask with - and that only a 204 is a yes. GitHub answers
/// 302 rather than 404 when the caller is not itself in the organisation, and
/// the client does not follow redirects, so that hop stays visible instead of
/// turning into something else's error.</para>
/// </summary>
public sealed class GitHubAppClientMembersTests : IDisposable
{
    private const long InstallationId = 42;
    private const string InstallationToken = "ghs_installation";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_member_is_confirmed_and_the_question_goes_out_on_the_installation_token()
    {
        var api = ApiAnswering(HttpStatusCode.NoContent);
        await ConfigureDeploymentAsync();
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);

        var token = await client.GetInstallationTokenAsync(InstallationId);
        var isMember = await client.InstallationSeesOrgMemberAsync(token, "cronus-dk", "erik");

        isMember.Should().BeTrue();
        api.Credentials.Single(c => c.Call.Contains("/orgs/cronus-dk/members/erik")).Token
            .Should().Be(InstallationToken,
                "a webhook build has no user behind it, so the app is what asks");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)]
    public async Task Anything_that_is_not_a_204_is_read_as_not_a_member(HttpStatusCode answer)
    {
        var api = ApiAnswering(answer);
        await ConfigureDeploymentAsync();
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);

        var token = await client.GetInstallationTokenAsync(InstallationId);

        (await client.InstallationSeesOrgMemberAsync(token, "cronus-dk", "erik")).Should().BeFalse();
    }

    [Fact]
    public async Task A_GitHub_that_does_not_answer_at_all_throws_rather_than_saying_no()
    {
        // "We could not ask" and "the answer is no" are different, and only the
        // caller can decide what to do with the first - the compile gate treats
        // both as a refusal, but it does so knowingly.
        var api = ApiAnswering(FakeGitHubApi.Unreachable);
        await ConfigureDeploymentAsync();
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var token = await client.GetInstallationTokenAsync(InstallationId);

        var act = () => client.InstallationSeesOrgMemberAsync(token, "cronus-dk", "erik");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private static FakeGitHubApi ApiAnswering(HttpStatusCode membership) =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson(InstallationToken))
            .On(HttpMethod.Get, "/orgs/cronus-dk/members/erik", membership);

    private async Task ConfigureDeploymentAsync()
    {
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: TestKeys.GitHubAppPem, ClearPrivateKey: false));
    }
}
