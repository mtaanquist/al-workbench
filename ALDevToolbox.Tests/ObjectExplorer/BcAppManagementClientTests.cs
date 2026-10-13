using System.Net;
using System.Net.Http.Headers;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Contract for <see cref="BcAppManagementClient"/>, the Admin Center App Management
/// surface that replaces the automation API's <c>extensionUpload</c>. Two bug classes
/// are pinned here because they only show up against the live API: the exact shape of
/// the multipart upload (part names, the required <c>.app</c> file name, booleans as
/// strings, the EULA field), and the response parsing — status words whose casing
/// differs per endpoint, and failure codes that are only available as a JSON fragment
/// embedded in a message localized to the customer's language.
/// See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class BcAppManagementClientTests
{
    private const string Token = "tok";
    private const string Family = "BusinessCentral";
    private const string Environment = "Test";

    /// <summary>The real redacted payload of a failed install, captured against a live tenant.</summary>
    private const string FailedOperationJson = """
    {"id":"9ebfd370-eb91-4e3e-8cca-f78f4812b1a8","type":"environmentAppUpdate","status":"failed","aadTenantId":"00000000-0000-0000-0000-000000000000","createdOn":"2026-08-28T11:26:29.86Z","startedOn":"2026-08-28T11:26:29.86Z","completedOn":"2026-08-28T11:27:16.353Z","createdBy":"00000000-0000-0000-0000-000000000000","canceledBy":"","creatorPrincipalType":"app","errorMessage":"A request to the Data Plane Admin Service failed.\r\nHttp status code: BadRequest\r\nError:\r\n{\r\n  \"code\": \"ExtensionChangeFailed\",\r\n  \"message\": \"Localized message in the environment language.\",\r\n  \"innerError\": {\r\n    \"code\": \"TenantSyncFailure\"\r\n  }\r\n}","parameters":{"appId":"00000000-0000-0000-0000-000000000000","targetAppVersion":"27.5.5.15","sourceAppVersion":"27.5.4.18","countryCode":"DK","allowPreviewVersion":false,"ignoreUpgradeWindow":true,"allowDependencyUpdate":true},"environmentName":"Test","environmentType":"Sandbox","productFamily":"BusinessCentral","canBeCanceled":false}
    """;

    // ── Test doubles ──────────────────────────────────────────────────────

    /// <summary>
    /// Answers with a canned response and records what was sent. Multipart parts are read
    /// inside <c>SendAsync</c> because the client disposes the content once the call returns.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public RecordingHandler(HttpStatusCode status, string body) { _status = status; _body = body; }

        public int Calls { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? Url { get; private set; }
        public string? JsonBody { get; private set; }
        public List<MultipartPart> Parts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Method = request.Method;
            Url = request.RequestUri;

            switch (request.Content)
            {
                case MultipartFormDataContent multipart:
                    foreach (var part in multipart)
                    {
                        var disposition = part.Headers.ContentDisposition;
                        var isFile = disposition?.FileName is { Length: > 0 };
                        Parts.Add(new MultipartPart(
                            Name: Unquote(disposition?.Name),
                            FileName: Unquote(disposition?.FileName),
                            Value: isFile ? null : await part.ReadAsStringAsync(ct),
                            ByteLength: isFile ? (await part.ReadAsByteArrayAsync(ct)).Length : null));
                    }
                    break;
                case { } content:
                    JsonBody = await content.ReadAsStringAsync(ct);
                    break;
            }

            return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
        }

        private static string? Unquote(string? value) => value?.Trim('"');
    }

    private sealed record MultipartPart(string? Name, string? FileName, string? Value, int? ByteLength);

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static (BcAppManagementClient Client, RecordingHandler Handler) Client(
        HttpStatusCode status = HttpStatusCode.OK, string body = "{}")
    {
        var handler = new RecordingHandler(status, body);
        return (new BcAppManagementClient(new StubFactory(handler), NullLogger<BcAppManagementClient>.Instance), handler);
    }

    private static byte[] Package(int bytes = 16) => new byte[bytes];

    private static Task<BcAppOperation> Install(
        BcAppManagementClient client,
        string fileName = "CRONUS.Workbench.app",
        byte[]? appBytes = null,
        string schedule = BcDeploymentSchedule.Immediate,
        string syncMode = BcSyncMode.Add,
        bool dependencies = true)
        => client.InstallPteAsync(Token, Family, Environment, appBytes ?? Package(), fileName,
            schedule, syncMode, "en-US", dependencies);

    // ── Multipart assembly ────────────────────────────────────────────────

    [Fact]
    public async Task InstallPte_sends_the_package_as_the_extensionFile_part()
    {
        var (client, handler) = Client(body: """{"id":"11111111-1111-1111-1111-111111111111","status":"running"}""");

        await Install(client, appBytes: Package(64));

        var file = handler.Parts.Should().ContainSingle(p => p.Name == "extensionFile").Subject;
        file.FileName.Should().Be("CRONUS.Workbench.app", "BC reads the app id and version out of the named package");
        file.ByteLength.Should().Be(64);
        handler.Method.Should().Be(HttpMethod.Post);
        handler.Url!.AbsoluteUri.Should().Be(
            "https://api.businesscentral.dynamics.com/admin/v2.29/applications/BusinessCentral/environments/Test/apps/pteInstall");
    }

    [Fact]
    public async Task InstallPte_sends_the_schedule_sync_mode_and_language_verbatim()
    {
        var (client, handler) = Client(body: """{"id":"11111111-1111-1111-1111-111111111111","status":"scheduled"}""");

        await Install(client, schedule: BcDeploymentSchedule.NextMinorUpdate, syncMode: BcSyncMode.ForceSync);

        Value(handler, "deploymentSchedule").Should().Be("NextMinorUpdate");
        Value(handler, "syncMode").Should().Be("ForceSync", "the API dropped the space the automation API used");
        Value(handler, "languageId").Should().Be("en-US");
    }

    [Fact]
    public async Task InstallPte_sends_booleans_as_strings_and_always_accepts_the_eula()
    {
        var (client, handler) = Client(body: """{"id":"11111111-1111-1111-1111-111111111111","status":"running"}""");

        await Install(client, dependencies: true);

        Value(handler, "installOrUpdateNeededDependencies").Should().Be("true");
        Value(handler, "acceptIsvEula").Should().Be("true", "the API refuses the install without it, and there is no interactive surface to show the terms on");
    }

    [Fact]
    public async Task InstallPte_sends_false_for_dependencies_when_asked_not_to_resolve_them()
    {
        var (client, handler) = Client(body: """{"id":"11111111-1111-1111-1111-111111111111","status":"running"}""");

        await Install(client, dependencies: false);

        Value(handler, "installOrUpdateNeededDependencies").Should().Be("false");
    }

    private static string? Value(RecordingHandler handler, string partName) =>
        handler.Parts.Should().ContainSingle(p => p.Name == partName).Subject.Value;

    // ── Local guards (no HTTP call) ───────────────────────────────────────

    [Theory]
    [InlineData("CRONUS.Workbench.zip")]
    [InlineData("CRONUS.Workbench")]
    [InlineData("")]
    public async Task InstallPte_refuses_a_file_name_that_isnt_a_dot_app(string fileName)
    {
        var (client, handler) = Client();

        var install = () => Install(client, fileName: fileName);

        await install.Should().ThrowAsync<ArgumentException>();
        handler.Calls.Should().Be(0, "a bad file name is refused before the upload costs a round trip");
    }

    [Fact]
    public async Task InstallPte_accepts_a_dot_app_name_whatever_its_casing()
    {
        var (client, handler) = Client(body: """{"id":"11111111-1111-1111-1111-111111111111","status":"running"}""");

        await Install(client, fileName: "CRONUS.Workbench.APP");

        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task InstallPte_refuses_a_package_over_fifty_megabytes_without_calling_the_api()
    {
        var (client, handler) = Client();

        var install = () => Install(client, appBytes: Package((50 * 1024 * 1024) + 1));

        (await install.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("50 MB");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task InstallPte_refuses_an_empty_package()
    {
        var (client, handler) = Client();

        var install = () => Install(client, appBytes: []);

        await install.Should().ThrowAsync<ArgumentException>();
        handler.Calls.Should().Be(0);
    }

    // ── Operation parsing ─────────────────────────────────────────────────

    [Fact]
    public void ParseOperation_reads_the_real_failed_install_payload()
    {
        var operation = BcAppManagementClient.ParseOperation(FailedOperationJson)!;

        operation.Id.Should().Be(Guid.Parse("9ebfd370-eb91-4e3e-8cca-f78f4812b1a8"));
        operation.Status.Should().Be(BcAppOperationStatus.Failed, "the payload spells it lowercase");
        operation.RawStatus.Should().Be("failed");
        operation.IsTerminal.Should().BeTrue();
        operation.CanBeCanceled.Should().BeFalse();
        operation.TargetAppVersion.Should().Be("27.5.5.15", "the version is only inside parameters on this payload");
        operation.SourceAppVersion.Should().Be("27.5.4.18");
        operation.CreatorPrincipalType.Should().Be("app", "the docs say \"App\"; the tenant returned lowercase");
        operation.CompletedOn.Should().NotBeNull();
    }

    [Fact]
    public void ParseOperation_lifts_the_structured_codes_out_of_the_localized_message()
    {
        var operation = BcAppManagementClient.ParseOperation(FailedOperationJson)!;

        operation.ErrorCode.Should().Be("ExtensionChangeFailed");
        operation.InnerErrorCode.Should().Be("TenantSyncFailure");
        // The message is localized to the environment's language, so it is display text
        // only: it is carried through untouched and nothing branches on it.
        operation.ErrorMessage.Should().NotBeEmpty();
        operation.ErrorMessage.Should().Contain("ExtensionChangeFailed");
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("Succeeded")]
    [InlineData("SUCCEEDED")]
    public void ParseOperation_reads_a_status_whatever_its_casing(string status)
    {
        var json = $$"""{"id":"11111111-1111-1111-1111-111111111111","status":"{{status}}"}""";

        var operation = BcAppManagementClient.ParseOperation(json)!;

        operation.Status.Should().Be(BcAppOperationStatus.Succeeded);
        operation.RawStatus.Should().Be(status, "the raw word is kept for display and logs");
    }

    [Fact]
    public void ParseOperation_reads_the_install_response_field_names()
    {
        const string json = """
        {"id":"22222222-2222-2222-2222-222222222222","type":"install","status":"scheduled",
         "appId":"33333333-3333-3333-3333-333333333333","targetAppVersion":"1.2.3.4",
         "sourceAppVersion":"","scheduleKind":"nextminorupdate","errorMessage":""}
        """;

        var operation = BcAppManagementClient.ParseOperation(json)!;

        operation.AppId.Should().Be(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        operation.Status.Should().Be(BcAppOperationStatus.Scheduled);
        operation.IsTerminal.Should().BeFalse("a scheduled install never goes terminal while we watch");
        operation.TargetAppVersion.Should().Be("1.2.3.4");
        operation.ScheduleKind.Should().Be(BcDeploymentSchedule.NextMinorUpdate, "the schedule is normalized to the wire spelling");
        operation.ErrorCode.Should().BeEmpty();
    }

    [Fact]
    public void ParseOperation_reads_the_operations_endpoint_field_names_and_envelope()
    {
        // The by-id operations endpoint spells the versions differently and may wrap the
        // operation in the list envelope.
        const string json = """
        {"value":[{"id":"44444444-4444-4444-4444-444444444444","status":"Running",
                   "sourceVersion":"1.0.0.0","targetVersion":"2.0.0.0","type":"install"}]}
        """;

        var operation = BcAppManagementClient.ParseOperation(json)!;

        operation.Status.Should().Be(BcAppOperationStatus.Running);
        operation.SourceAppVersion.Should().Be("1.0.0.0");
        operation.TargetAppVersion.Should().Be("2.0.0.0");
    }

    [Fact]
    public void ParseOperation_returns_null_for_an_empty_envelope()
    {
        BcAppManagementClient.ParseOperation("""{"value":[]}""").Should().BeNull();
    }

    [Fact]
    public async Task GetAppOperation_asks_for_the_operation_by_app_and_operation_id()
    {
        var appId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var operationId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var (client, handler) = Client(body: FailedOperationJson);

        var operation = await client.GetAppOperationAsync(Token, Family, Environment, appId, operationId);

        handler.Url!.AbsolutePath.Should().EndWith($"/apps/{appId}/operations/{operationId}");
        operation!.Status.Should().Be(BcAppOperationStatus.Failed);
    }

    // ── List parsing ──────────────────────────────────────────────────────

    [Fact]
    public async Task ListInstalledApps_reads_the_app_type_and_state()
    {
        const string json = """
        {"value":[
          {"appId":"55555555-5555-5555-5555-555555555555","name":"CRONUS Workbench","publisher":"CRONUS A/S",
           "version":"1.2.3.4","state":"Installed","appType":"tenant","canBeUninstalled":true,
           "lastOperationId":"66666666-6666-6666-6666-666666666666","lastUpdateAttemptResult":"Succeeded"},
          {"appId":"77777777-7777-7777-7777-777777777777","name":"Base Application","publisher":"Microsoft",
           "version":"27.5.0.0","state":"Installed","appType":"global","canBeUninstalled":false}
        ]}
        """;
        var (client, handler) = Client(body: json);

        var apps = await client.ListInstalledAppsAsync(Token, Family, Environment);

        handler.Url!.AbsolutePath.Should().EndWith("/environments/Test/apps");
        apps.Should().HaveCount(2);
        var pte = apps.Single(a => a.Name == "CRONUS Workbench");
        pte.IsPerTenant.Should().BeTrue();
        pte.Version.Should().Be("1.2.3.4");
        pte.LastOperationId.Should().Be(Guid.Parse("66666666-6666-6666-6666-666666666666"));
        apps.Single(a => a.Name == "Base Application").IsPerTenant.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateApp_says_whether_the_prerequisites_come_along(bool withDependencies)
    {
        var (client, handler) = Client(body: """{"id":"66666666-6666-6666-6666-666666666666","status":"scheduled"}""");
        var appId = Guid.Parse("55555555-5555-5555-5555-555555555555");

        await client.UpdateAppAsync(Token, Family, Environment, appId, "28.5.0.1",
            useEnvironmentUpdateWindow: true, installOrUpdateNeededDependencies: withDependencies);

        handler.Url!.AbsolutePath.Should().EndWith($"/apps/{appId}/update");
        handler.JsonBody.Should().Contain($"\"installOrUpdateNeededDependencies\":{(withDependencies ? "true" : "false")}")
            .And.Contain("\"allowPreviewVersion\":false");
    }

    [Fact]
    public async Task ListInstalledApps_keeps_a_row_that_calls_its_app_id_plain_id()
    {
        const string json = """
        {"value":[{"id":"55555555-5555-5555-5555-555555555555","name":"CRONUS Workbench","version":"1.2.3.4"}]}
        """;
        var (client, _) = Client(body: json);

        var apps = await client.ListInstalledAppsAsync(Token, Family, Environment);

        apps.Should().ContainSingle().Which.AppId.Should().Be(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    }

    [Fact]
    public async Task ListScheduledPteOperations_reads_the_name_and_sync_mode_from_parameters()
    {
        const string json = """
        {"value":[{
          "id":"88888888-8888-8888-8888-888888888888","type":"Install","status":"scheduled",
          "targetAppVersion":"2.0.0.0","appId":"99999999-9999-9999-9999-999999999999","scheduleKind":"UpdateWindow",
          "parameters":{"name":"CRONUS Workbench","publisher":"CRONUS A/S","syncMode":"ForceSync","languageId":"en-US"}
        }]}
        """;
        var (client, handler) = Client(body: json);

        var scheduled = await client.ListScheduledPteOperationsAsync(Token, Family, Environment);

        handler.Url!.AbsolutePath.Should().EndWith("/apps/scheduledPteOperations");
        var op = scheduled.Should().ContainSingle().Subject;
        op.Status.Should().Be(BcAppOperationStatus.Scheduled);
        op.Name.Should().Be("CRONUS Workbench");
        op.SyncMode.Should().Be(BcSyncMode.ForceSync);
        op.ScheduleKind.Should().Be(BcDeploymentSchedule.UpdateWindow);
        op.TargetAppVersion.Should().Be("2.0.0.0");
    }

    [Fact]
    public async Task RemoveScheduledPteVersion_posts_the_version_and_schedule_kind()
    {
        var appId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var (client, handler) = Client(body: """
        {"id":"88888888-8888-8888-8888-888888888888","status":"canceled","targetAppVersion":"2.0.0.0","scheduleKind":"UpdateWindow"}
        """);

        var operation = await client.RemoveScheduledPteVersionAsync(
            Token, Family, Environment, appId, "2.0.0.0", BcDeploymentSchedule.UpdateWindow);

        handler.Method.Should().Be(HttpMethod.Post);
        handler.Url!.AbsolutePath.Should().EndWith($"/apps/{appId}/removeScheduledPteVersion");
        handler.JsonBody.Should().Contain("\"targetVersion\":\"2.0.0.0\"").And.Contain("\"scheduleKind\":\"UpdateWindow\"");
        operation.Status.Should().Be(BcAppOperationStatus.Canceled);
    }

    // ── Error mapping ─────────────────────────────────────────────────────

    [Fact]
    public async Task InstallPte_names_the_missing_dependencies_from_a_400()
    {
        const string body = """
        {"code":"AppDependenciesNotSatisfied","message":"Dependencies are missing.",
         "data":{"requirements":[
           {"appId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","name":"CRONUS Base","publisher":"CRONUS A/S","version":"1.0.0.0","type":"install"},
           {"appId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","name":"CRONUS Shared","publisher":"CRONUS A/S","version":"2.0.0.0","type":"update"}
         ]}}
        """;
        var (client, _) = Client(HttpStatusCode.BadRequest, body);

        var install = () => Install(client);

        var thrown = (await install.Should().ThrowAsync<BcApiException>()).Which;
        thrown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        thrown.Message.Should().Contain("Install these first")
            .And.Contain("CRONUS Base by CRONUS A/S 1.0.0.0")
            .And.Contain("CRONUS Shared by CRONUS A/S 2.0.0.0");
    }

    [Fact]
    public async Task InstallPte_falls_back_to_the_error_code_and_message_when_there_are_no_requirements()
    {
        var (client, _) = Client(HttpStatusCode.BadRequest,
            """{"code":"PteVersionAlreadyScheduled","message":"A version is already scheduled."}""");

        var install = () => Install(client);

        var thrown = (await install.Should().ThrowAsync<BcApiException>()).Which;
        thrown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        thrown.Message.Should().Contain("400").And.Contain("PteVersionAlreadyScheduled");
    }

    [Fact]
    public async Task ListInstalledApps_maps_a_403_to_a_status_carrying_exception()
    {
        var (client, _) = Client(HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden","message":"No access."}}""");

        var list = () => client.ListInstalledAppsAsync(Token, Family, Environment);

        var thrown = (await list.Should().ThrowAsync<BcApiException>()).Which;
        thrown.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        thrown.Message.Should().Contain("Forbidden");
    }

    // ── Timeouts (#1233) ──────────────────────────────────────────────────

    /// <summary>Throws what HttpClient throws when its own timeout runs out.</summary>
    private sealed class TimingOutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.",
                new TimeoutException());
    }

    [Fact]
    public async Task A_call_that_times_out_is_business_central_not_answering_rather_than_a_cancel()
    {
        var client = new BcAppManagementClient(new StubFactory(new TimingOutHandler()), NullLogger<BcAppManagementClient>.Instance);

        var poll = () => client.GetAppOperationAsync(Token, Family, Environment, Guid.NewGuid(), Guid.NewGuid());

        (await poll.Should().ThrowAsync<BcApiException>()).Which.StatusCode.Should().BeNull();
    }

    /// <summary>Waits until the call is cancelled, as a slow Business Central would.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    [Fact]
    public async Task A_call_the_caller_cancels_stays_a_cancel()
    {
        var client = new BcAppManagementClient(new StubFactory(new HangingHandler()), NullLogger<BcAppManagementClient>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var poll = () => client.GetAppOperationAsync(Token, Family, Environment, Guid.NewGuid(), Guid.NewGuid(), cts.Token);

        await poll.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Wire-value constants ──────────────────────────────────────────────

    [Fact]
    public void Wire_values_normalize_case_insensitively_and_reject_the_legacy_spelling()
    {
        BcDeploymentSchedule.Normalize("immediate").Should().Be(BcDeploymentSchedule.Immediate);
        BcDeploymentSchedule.Normalize("Current Version").Should().BeNull("that is the retired automation-API value");
        BcSyncMode.Normalize("forcesync").Should().Be(BcSyncMode.ForceSync);
        BcSyncMode.Normalize("Force Sync").Should().BeNull("the App Management API dropped the space");
    }
}
