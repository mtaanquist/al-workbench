using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The Checks API half of the client (issue #627). Two things are worth pinning
/// and neither is "the JSON serialised": that check-run calls go out on the
/// <em>installation</em> token - a check run is written by the app and a webhook
/// build has no user - and that more than fifty annotations become more than one
/// request, because GitHub answers 422 to the fifty-first rather than truncating.
/// </summary>
public sealed class GitHubCheckRunTests : IDisposable
{
    private const long InstallationId = 42;
    private const string InstallationToken = "ghs_installation";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Creating_a_check_run_uses_the_installation_token_and_returns_its_id()
    {
        var api = ApiWithChecks();
        await ConfigureDeploymentAsync();
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);

        var token = await client.GetInstallationTokenAsync(InstallationId);
        var id = await client.CreateCheckRunAsync(
            token, "cronus-dk", "customer-app",
            name: "AL Workbench / CRONUS Retail",
            headSha: "abc123",
            status: "in_progress",
            detailsUrl: "https://workbench.example/solutions/9",
            externalId: "9");

        id.Should().Be(555);

        var call = api.Credentials.Single(c => c.Call.Contains("/check-runs") && c.Call.StartsWith("POST"));
        call.Token.Should().Be(InstallationToken,
            "a check run is written by the app, not by a person - a webhook build has no user");

        var body = JsonDocument.Parse(api.Bodies.Single(b => b.Call.Contains("/check-runs")).Body).RootElement;
        body.GetProperty("head_sha").GetString().Should().Be("abc123");
        body.GetProperty("name").GetString().Should().Be("AL Workbench / CRONUS Retail");
        body.GetProperty("status").GetString().Should().Be("in_progress");
        body.GetProperty("details_url").GetString().Should().Be("https://workbench.example/solutions/9");
    }

    [Fact]
    public async Task Completing_a_check_run_sends_the_conclusion_and_the_annotations()
    {
        var api = ApiWithChecks();
        await ConfigureDeploymentAsync();
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var token = await client.GetInstallationTokenAsync(InstallationId);

        await client.UpdateCheckRunAsync(
            token, "cronus-dk", "customer-app", checkRunId: 555,
            status: "completed",
            conclusion: GitHubCheckConclusion.Failure,
            title: "1 compile error",
            summary: "The compiler reported errors.",
            annotations: [new GitHubCheckAnnotation("App/My.al", 12, 12, GitHubCheckAnnotationLevel.Failure, "The name 'Foo' does not exist", "AL0118")]);

        var patches = api.Bodies.Where(b => b.Call.StartsWith("PATCH") && b.Call.Contains("/check-runs/555")).ToList();
        patches.Should().ContainSingle("one batch of annotations is one request");

        var body = JsonDocument.Parse(patches[0].Body).RootElement;
        body.GetProperty("status").GetString().Should().Be("completed");
        body.GetProperty("conclusion").GetString().Should().Be("failure");

        var annotation = body.GetProperty("output").GetProperty("annotations")[0];
        annotation.GetProperty("path").GetString().Should().Be("App/My.al");
        annotation.GetProperty("start_line").GetInt32().Should().Be(12);
        annotation.GetProperty("annotation_level").GetString().Should().Be("failure");
        annotation.GetProperty("title").GetString().Should().Be("AL0118");
    }

    [Fact]
    public async Task More_than_fifty_annotations_go_out_in_batches_and_only_the_first_carries_the_conclusion()
    {
        var api = ApiWithChecks();
        await ConfigureDeploymentAsync();
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var token = await client.GetInstallationTokenAsync(InstallationId);

        var annotations = Enumerable.Range(1, 120)
            .Select(i => new GitHubCheckAnnotation($"App/File{i}.al", i, i, GitHubCheckAnnotationLevel.Warning, "msg", "AA0005"))
            .ToList();

        await client.UpdateCheckRunAsync(
            token, "cronus-dk", "customer-app", checkRunId: 555,
            status: "completed", conclusion: GitHubCheckConclusion.Success,
            title: "ok", summary: "ok", annotations: annotations);

        var patches = api.Bodies.Where(b => b.Call.StartsWith("PATCH") && b.Call.Contains("/check-runs/555")).ToList();
        patches.Should().HaveCount(3, "120 annotations at GitHub's cap of 50 per request is three calls");

        var bodies = patches.Select(p => JsonDocument.Parse(p.Body).RootElement).ToList();
        bodies[0].TryGetProperty("conclusion", out _).Should().BeTrue();
        bodies[1].TryGetProperty("conclusion", out _).Should().BeFalse(
            "re-completing the run on every batch would re-stamp when it finished");
        bodies[2].TryGetProperty("conclusion", out _).Should().BeFalse();

        bodies[0].GetProperty("output").GetProperty("annotations").GetArrayLength().Should().Be(50);
        bodies[2].GetProperty("output").GetProperty("annotations").GetArrayLength().Should().Be(20);
    }

    [Fact]
    public async Task A_run_with_no_annotations_is_still_completed()
    {
        var api = ApiWithChecks();
        await ConfigureDeploymentAsync();
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var token = await client.GetInstallationTokenAsync(InstallationId);

        await client.UpdateCheckRunAsync(
            token, "cronus-dk", "customer-app", checkRunId: 555,
            status: "completed", conclusion: GitHubCheckConclusion.Neutral,
            title: "The build could not run", summary: "No symbols.", annotations: []);

        var patches = api.Bodies.Where(b => b.Call.StartsWith("PATCH")).ToList();
        patches.Should().ContainSingle();
        JsonDocument.Parse(patches[0].Body).RootElement
            .GetProperty("conclusion").GetString().Should().Be("neutral");
    }

    [Fact]
    public async Task Absent_optional_fields_are_left_out_rather_than_sent_as_null()
    {
        // GitHub's check-run schema tells "absent" from "null" and answers 422
        // for the second. A deployment that has not been told its own address
        // has no details URL, and a diagnostic with no code has no annotation
        // title - on a default install that is every check run there is.
        var api = ApiWithChecks();
        await ConfigureDeploymentAsync();
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var token = await client.GetInstallationTokenAsync(InstallationId);

        await client.CreateCheckRunAsync(
            token, "cronus-dk", "customer-app",
            name: "AL Workbench / CRONUS Retail",
            headSha: "abc123",
            status: "in_progress",
            detailsUrl: null,
            externalId: null);
        await client.UpdateCheckRunAsync(
            token, "cronus-dk", "customer-app", checkRunId: 555,
            status: "in_progress",
            conclusion: null,
            title: "Building",
            summary: "The build is running.",
            annotations: [new GitHubCheckAnnotation("App/My.al", 12, 12, GitHubCheckAnnotationLevel.Warning, "msg", null)]);

        var bodies = api.Bodies.Where(b => b.Call.Contains("/check-runs")).ToList();
        bodies.Should().HaveCount(2);
        foreach (var (call, body) in bodies)
        {
            HasNull(JsonDocument.Parse(body).RootElement).Should().BeFalse(
                "GitHub answers 422 for an explicit null, and {0} carried one", call);
        }

        var created = JsonDocument.Parse(bodies[0].Body).RootElement;
        created.TryGetProperty("details_url", out _).Should().BeFalse();
        created.TryGetProperty("external_id", out _).Should().BeFalse();

        var patched = JsonDocument.Parse(bodies[1].Body).RootElement;
        patched.TryGetProperty("conclusion", out _).Should().BeFalse();
        patched.TryGetProperty("completed_at", out _).Should().BeFalse();
        patched.GetProperty("output").GetProperty("annotations")[0]
            .TryGetProperty("title", out _).Should().BeFalse();
    }

    /// <summary>True when any property anywhere in the body was written as a JSON null.</summary>
    private static bool HasNull(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => true,
        JsonValueKind.Object => element.EnumerateObject().Any(p => HasNull(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasNull),
        _ => false,
    };

    [Fact]
    public async Task A_refused_check_run_surfaces_GitHubs_own_message()
    {
        // The commonest refusal is the organisation not having granted the app
        // permission to report build results; the caller renders that reason.
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson(InstallationToken))
            .On(HttpMethod.Post, "/repos/cronus-dk/customer-app/check-runs", HttpStatusCode.Forbidden,
                "{\"message\":\"Resource not accessible by integration\"}");
        await ConfigureDeploymentAsync();
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var token = await client.GetInstallationTokenAsync(InstallationId);

        Func<Task> act = () => client.CreateCheckRunAsync(
            token, "cronus-dk", "customer-app", "AL Workbench / CRONUS", "abc", "in_progress");

        var ex = await act.Should().ThrowAsync<GitHubApiException>();
        ex.Which.Message.Should().Contain("Resource not accessible by integration");
    }

    [Fact]
    public void The_check_run_name_says_which_solution_is_speaking() =>
        // A repository can be tracked by more than one solution, and each gets
        // its own run - the name is what tells them apart on the pull request.
        GitHubCheckRunService.CheckRunName("CRONUS Retail")
            .Should().Be("AL Workbench / CRONUS Retail");

    private static FakeGitHubApi ApiWithChecks() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson(InstallationToken))
            .On(HttpMethod.Post, "/repos/cronus-dk/customer-app/check-runs",
                HttpStatusCode.Created, "{\"id\":555}")
            .On(new HttpMethod("PATCH"), "/repos/cronus-dk/customer-app/check-runs/555",
                HttpStatusCode.OK, "{\"id\":555}");

    private async Task ConfigureDeploymentAsync()
    {
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: TestKeys.GitHubAppPem, ClearPrivateKey: false));
    }
}
