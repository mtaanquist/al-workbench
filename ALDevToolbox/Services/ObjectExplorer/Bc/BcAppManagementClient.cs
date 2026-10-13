using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <inheritdoc cref="IBcAppManagementClient"/>
public sealed class BcAppManagementClient : IBcAppManagementClient
{
    /// <summary>The largest extension package Business Central accepts. Checked locally so an oversized build fails fast instead of after a long upload.</summary>
    public const int MaxAppBytes = 50 * 1024 * 1024;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BcAppManagementClient> _logger;

    public BcAppManagementClient(IHttpClientFactory httpFactory, ILogger<BcAppManagementClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<BcAppOperation> InstallPteAsync(
        string accessToken, string applicationFamily, string environmentName,
        byte[] appBytes, string fileName, string deploymentSchedule, string syncMode,
        string languageId, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(appBytes);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("The extension package needs a file name ending in '.app'.", nameof(fileName));
        }
        if (!fileName.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Business Central only accepts a '.app' package; '{fileName}' isn't one.", nameof(fileName));
        }
        if (appBytes.Length == 0)
        {
            throw new ArgumentException($"'{fileName}' is empty.", nameof(appBytes));
        }
        if (appBytes.Length > MaxAppBytes)
        {
            throw new ArgumentException(
                $"'{fileName}' is {appBytes.Length / (1024 * 1024)} MB. Business Central refuses extension packages over 50 MB.",
                nameof(appBytes));
        }

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(appBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // The part name is fixed by the API; the file name rides along and must keep its
        // .app extension, because BC reads the package metadata (app id, version) from it.
        content.Add(file, "extensionFile", fileName);

        AddIfPresent(content, "deploymentSchedule", deploymentSchedule);
        AddIfPresent(content, "syncMode", syncMode);
        AddIfPresent(content, "languageId", languageId);
        // Multipart carries no types: booleans go over the wire as the literal strings.
        content.Add(new StringContent(installOrUpdateNeededDependencies ? "true" : "false"), "installOrUpdateNeededDependencies");
        // Required by the API, and always true: there is no interactive surface to show the
        // Marketplace terms on, so sending this accepts them on the customer's behalf. That
        // is a deliberate product decision, not an oversight — it is called out in the setup
        // copy and in .design/saas-delivery.md so nobody is surprised by it.
        content.Add(new StringContent("true"), "acceptIsvEula");

        var url = $"{AppsBase(applicationFamily, environmentName)}/pteInstall";
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.UseBearer(accessToken);

        var body = await SendAsync(request, "uploading the extension", environmentName, ct).ConfigureAwait(false);
        var operation = ParseOperation(body)
            ?? throw new BcApiException(null, "Business Central accepted the upload but didn't return an operation to track.");

        _logger.LogInformation(
            "Uploaded {FileName} to BC environment {Environment} ({Family}): operation {OperationId} for app {AppId} is {Status}.",
            fileName, environmentName, applicationFamily, operation.Id, operation.AppId, operation.RawStatus);
        return operation;
    }

    public async Task<BcAppOperation?> GetAppOperationAsync(
        string accessToken, string applicationFamily, string environmentName,
        Guid appId, Guid operationId, CancellationToken ct = default)
    {
        var url = $"{AppsBase(applicationFamily, environmentName)}/{appId}/operations/{operationId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.UseBearer(accessToken);

        var body = await SendAsync(request, "reading the install operation", environmentName, ct).ConfigureAwait(false);
        return ParseOperation(body);
    }

    public async Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(
        string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
    {
        var url = AppsBase(applicationFamily, environmentName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.UseBearer(accessToken);

        var body = await SendAsync(request, "listing installed apps", environmentName, ct).ConfigureAwait(false);
        var apps = ParseInstalledApps(body);
        if (apps.Count == 0)
        {
            // An environment always has the base application, so an empty list means the
            // answer wasn't shaped the way we read it. Field names only: enough to fix the
            // parser from a log line, and nothing of the customer's in it.
            _logger.LogWarning(
                "BC environment {Environment} listed no installed apps we could read; the response was shaped {Shape}.",
                environmentName, DescribeShape(body));
        }
        return apps;
    }

    public async Task<IReadOnlyList<BcAvailableAppUpdate>> ListAvailableUpdatesAsync(
        string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
    {
        var url = $"{AppsBase(applicationFamily, environmentName)}/availableUpdates";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.UseBearer(accessToken);

        var body = await SendAsync(request, "listing available app updates", environmentName, ct).ConfigureAwait(false);
        return ParseAvailableUpdates(body);
    }

    public async Task<IReadOnlyList<BcScheduledPteOperation>> ListScheduledPteOperationsAsync(
        string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
    {
        var url = $"{AppsBase(applicationFamily, environmentName)}/scheduledPteOperations";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.UseBearer(accessToken);

        var body = await SendAsync(request, "listing scheduled installs", environmentName, ct).ConfigureAwait(false);
        return ParseScheduledPteOperations(body);
    }

    public async Task<BcAppOperation> RemoveScheduledPteVersionAsync(
        string accessToken, string applicationFamily, string environmentName,
        Guid appId, string targetVersion, string scheduleKind, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            throw new ArgumentException("A scheduled install is identified by its version; none was given.", nameof(targetVersion));
        }
        if (string.IsNullOrWhiteSpace(scheduleKind))
        {
            throw new ArgumentException("A scheduled install is identified by the schedule it's waiting on; none was given.", nameof(scheduleKind));
        }

        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["targetVersion"] = targetVersion,
            ["scheduleKind"] = scheduleKind,
        });
        var url = $"{AppsBase(applicationFamily, environmentName)}/{appId}/removeScheduledPteVersion";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.UseBearer(accessToken);

        var body = await SendAsync(request, "canceling the scheduled install", environmentName, ct).ConfigureAwait(false);
        var operation = ParseOperation(body)
            ?? throw new BcApiException(null, "Business Central canceled the scheduled install but didn't return the operation.");

        _logger.LogInformation(
            "Canceled scheduled install of app {AppId} version {Version} ({ScheduleKind}) on BC environment {Environment}.",
            appId, targetVersion, scheduleKind, environmentName);
        return operation;
    }

    public async Task<BcAppOperation> UpdateAppAsync(
        string accessToken, string applicationFamily, string environmentName,
        Guid appId, string targetVersion, bool useEnvironmentUpdateWindow,
        bool installOrUpdateNeededDependencies, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            throw new ArgumentException("An app update names the version it moves to; none was given.", nameof(targetVersion));
        }

        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["useEnvironmentUpdateWindow"] = useEnvironmentUpdateWindow,
            ["targetVersion"] = targetVersion,
            ["allowPreviewVersion"] = false,
            ["installOrUpdateNeededDependencies"] = installOrUpdateNeededDependencies,
        });
        var url = $"{AppsBase(applicationFamily, environmentName)}/{appId}/update";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.UseBearer(accessToken);

        var body = await SendAsync(request, "updating the app", environmentName, ct).ConfigureAwait(false);
        var operation = ParseOperation(body)
            ?? throw new BcApiException(null, "Business Central accepted the app update but didn't return the operation.");

        _logger.LogInformation(
            "Asked for app {AppId} to be updated to {Version} on BC environment {Environment} (in the update window: {InWindow}, with its prerequisites: {WithDependencies}); operation {OperationId} is {Status}.",
            appId, targetVersion, environmentName, useEnvironmentUpdateWindow, installOrUpdateNeededDependencies, operation.Id, operation.RawStatus);
        return operation;
    }

    // ── URL helpers ───────────────────────────────────────────────────────────

    private static string AppsBase(string applicationFamily, string environmentName) =>
        BcConstants.AppManagementBaseUrl(applicationFamily, environmentName);

    private static void AddIfPresent(MultipartFormDataContent content, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            content.Add(new StringContent(value.Trim()), name);
        }
    }

    // ── Shared send ───────────────────────────────────────────────────────────

    /// <summary>
    /// Sends the request and maps transport faults and non-success statuses to
    /// <see cref="BcApiException"/> with a short, secret-free detail.
    /// <paramref name="action"/> is a gerund for the message ("uploading the extension").
    /// </summary>
    private async Task<string> SendAsync(HttpRequestMessage request, string action, string environmentName, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(BcConstants.HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        // HttpClient's own timeout surfaces as a cancellation too; only the caller's
        // token stopping is a real cancel, anything else is BC not answering (#1233).
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new BcApiException(null, $"Couldn't reach the Business Central Admin Center API while {action}.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("BC app management call ({Action}) for environment {Environment} returned {Status}.",
                    action, environmentName, response.StatusCode);
                // A missing-dependency 400 says more in its requirements list than in its
                // message, so prefer that; otherwise fall back to the shared extractor.
                var detail = DescribeRequirements(body);
                if (detail.Length == 0)
                {
                    detail = BcAdminClient.ExtractError(body);
                }
                throw new BcApiException(response.StatusCode,
                    $"The Admin Center API returned {(int)response.StatusCode} while {action}. {detail}".TrimEnd());
            }
            return body;
        }
    }

    // ── Parsers (internal for the client tests) ───────────────────────────────

    /// <summary>
    /// Turns a <c>400</c> body carrying <c>data.requirements[]</c> into a readable list of
    /// what has to be installed first. Returns empty for any other body — including one
    /// that isn't JSON — so the caller can fall back to the generic error detail.
    /// </summary>
    internal static string DescribeRequirements(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return string.Empty;
            if (!data.TryGetProperty("requirements", out var reqs) || reqs.ValueKind != JsonValueKind.Array) return string.Empty;

            var parts = new List<string>();
            foreach (var req in reqs.EnumerateArray())
            {
                var name = Str(req, "name");
                var publisher = Str(req, "publisher");
                var version = Str(req, "version");
                if (name.Length == 0) name = Str(req, "appId");
                if (name.Length == 0) continue;

                var label = publisher.Length > 0 ? $"{name} by {publisher}" : name;
                parts.Add(version.Length > 0 ? $"{label} {version}" : label);
            }
            return parts.Count == 0
                ? string.Empty
                : $"Install these first: {string.Join("; ", parts)}.";
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Parses one app operation, from either a bare object or a <c>{ "value": [...] }</c>
    /// envelope (the operations endpoint returns a list when no operation id is given).
    /// Field names differ between endpoints for the same value — <c>targetAppVersion</c>
    /// versus <c>targetVersion</c>, and sometimes only inside <c>parameters</c> — so each
    /// is read from every spelling seen in the wild. Returns <c>null</c> when there is no
    /// operation in the body.
    /// </summary>
    internal static BcAppOperation? ParseOperation(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BcApiException(null, "Business Central returned a response we couldn't read.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.Array)
            {
                var first = value.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object) return null;
                root = first;
            }
            if (root.ValueKind != JsonValueKind.Object) return null;
            return ReadOperation(root);
        }
    }

    private static BcAppOperation ReadOperation(JsonElement root)
    {
        root.TryGetProperty("parameters", out var parameters);

        var errorMessage = Str(root, "errorMessage");
        var (errorCode, innerErrorCode) = ExtractErrorCodes(root, errorMessage);
        var rawStatus = Str(root, "status");

        return new BcAppOperation(
            Id: Guid(root, "id") ?? System.Guid.Empty,
            AppId: Guid(root, "appId") ?? Guid(parameters, "appId"),
            Type: Str(root, "type"),
            Status: ParseStatus(rawStatus),
            RawStatus: rawStatus,
            SourceAppVersion: FirstNonEmpty(
                Str(root, "sourceAppVersion"), Str(root, "sourceVersion"), Str(parameters, "sourceAppVersion")),
            TargetAppVersion: FirstNonEmpty(
                Str(root, "targetAppVersion"), Str(root, "targetVersion"), Str(parameters, "targetAppVersion")),
            ScheduleKind: BcDeploymentSchedule.Normalize(
                FirstNonEmpty(Str(root, "scheduleKind"), Str(parameters, "scheduleKind"))),
            ErrorMessage: errorMessage,
            ErrorCode: errorCode,
            InnerErrorCode: innerErrorCode,
            CanBeCanceled: Bool(root, "canBeCanceled") ?? false,
            CreatorPrincipalType: Str(root, "creatorPrincipalType"),
            CreatedOn: Time(root, "createdOn"),
            StartedOn: Time(root, "startedOn"),
            CompletedOn: Time(root, "completedOn"));
    }

    /// <summary>Parses the <c>{ "value": [ { appId, name, version, state, ... } ] }</c> envelope of the installed-apps call.</summary>
    internal static IReadOnlyList<BcInstalledApp> ParseInstalledApps(string json)
    {
        var result = new List<BcInstalledApp>();
        foreach (var item in EnumerateValue(json))
        {
            // Documented as appId; the operations endpoints call the same thing id, so
            // take either rather than drop every row over a spelling.
            var appId = Guid(item, "appId") ?? Guid(item, "id");
            if (appId is null) continue;
            result.Add(new BcInstalledApp(
                AppId: appId.Value,
                Name: Str(item, "name"),
                Publisher: Str(item, "publisher"),
                Version: Str(item, "version"),
                State: Str(item, "state"),
                AppType: Str(item, "appType"),
                CanBeUninstalled: Bool(item, "canBeUninstalled") ?? false,
                LastOperationId: Guid(item, "lastOperationId"),
                LastUpdateAttemptResult: Str(item, "lastUpdateAttemptResult")));
        }
        return result;
    }

    /// <summary>Parses the <c>availableUpdates</c> envelope, including each entry's prerequisite list.</summary>
    internal static IReadOnlyList<BcAvailableAppUpdate> ParseAvailableUpdates(string json)
    {
        var result = new List<BcAvailableAppUpdate>();
        foreach (var item in EnumerateValue(json))
        {
            var appId = Guid(item, "appId");
            if (appId is null) continue;

            var requirements = new List<BcAppUpdateRequirement>();
            if (item.TryGetProperty("requirements", out var reqs) && reqs.ValueKind == JsonValueKind.Array)
            {
                foreach (var req in reqs.EnumerateArray())
                {
                    if (req.ValueKind != JsonValueKind.Object) continue;
                    requirements.Add(new BcAppUpdateRequirement(
                        Guid(req, "appId"), Str(req, "name"), Str(req, "publisher"),
                        Str(req, "version"), Str(req, "type")));
                }
            }

            result.Add(new BcAvailableAppUpdate(
                appId.Value, Str(item, "name"), Str(item, "publisher"), Str(item, "version"), requirements));
        }
        return result;
    }

    /// <summary>Parses the <c>scheduledPteOperations</c> envelope; the extension's name and publisher only appear inside <c>parameters</c>.</summary>
    internal static IReadOnlyList<BcScheduledPteOperation> ParseScheduledPteOperations(string json)
    {
        var result = new List<BcScheduledPteOperation>();
        foreach (var item in EnumerateValue(json))
        {
            item.TryGetProperty("parameters", out var parameters);
            var rawStatus = Str(item, "status");
            result.Add(new BcScheduledPteOperation(
                Id: Guid(item, "id") ?? System.Guid.Empty,
                AppId: Guid(item, "appId") ?? Guid(parameters, "appId"),
                Type: Str(item, "type"),
                Status: ParseStatus(rawStatus),
                RawStatus: rawStatus,
                TargetAppVersion: FirstNonEmpty(Str(item, "targetAppVersion"), Str(parameters, "targetAppVersion")),
                ScheduleKind: BcDeploymentSchedule.Normalize(
                    FirstNonEmpty(Str(item, "scheduleKind"), Str(parameters, "scheduleKind"))),
                Name: Str(parameters, "name"),
                Publisher: Str(parameters, "publisher"),
                SyncMode: BcSyncMode.Normalize(Str(parameters, "syncMode")) ?? Str(parameters, "syncMode"),
                LanguageId: Str(parameters, "languageId"),
                CreatedOn: Time(item, "createdOn")));
        }
        return result;
    }

    /// <summary>
    /// Maps the status word to <see cref="BcAppOperationStatus"/> ignoring case: the same
    /// value comes back lowercase from the upload endpoint and capitalised from the
    /// operations endpoints.
    /// </summary>
    internal static BcAppOperationStatus ParseStatus(string? raw) =>
        Enum.TryParse<BcAppOperationStatus>(raw?.Trim(), ignoreCase: true, out var parsed)
            && parsed != BcAppOperationStatus.Unknown
            ? parsed
            : BcAppOperationStatus.Unknown;

    /// <summary>
    /// Digs the structured failure codes out of an operation. Business Central reports them
    /// as a JSON fragment embedded in the (localized) <c>errorMessage</c> text, so the codes
    /// — not the prose — are the only thing safe to branch on. The embedded fragment is read
    /// by <see cref="ALDevToolbox.Services.ObjectExplorer.Delivery.BcFailureText.Parse"/>, the
    /// same parser the release page uses on the stored text.
    /// </summary>
    private static (string Code, string InnerCode) ExtractErrorCodes(JsonElement root, string errorMessage)
    {
        var code = Str(root, "code");
        var inner = root.TryGetProperty("innerError", out var innerEl) ? Str(innerEl, "code") : string.Empty;
        if (code.Length > 0 || inner.Length > 0) return (code, inner);

        var parsed = ALDevToolbox.Services.ObjectExplorer.Delivery.BcFailureText.Parse(errorMessage);
        return (parsed.Code, parsed.InnerCode);
    }

    // ── JSON readers ──────────────────────────────────────────────────────────

    private static IEnumerable<JsonElement> EnumerateValue(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BcApiException(null, "Business Central returned a response we couldn't read.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object) yield return item;
            }
        }
    }

    /// <summary>The property names of a response and of its first row, for a log line that says why nothing parsed.</summary>
    private static string DescribeShape(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return root.ValueKind.ToString();
            var names = string.Join(",", root.EnumerateObject().Select(p => p.Name));
            if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array) return $"{{{names}}}";
            var first = value.EnumerateArray().FirstOrDefault();
            var rowNames = first.ValueKind == JsonValueKind.Object
                ? string.Join(",", first.EnumerateObject().Select(p => p.Name))
                : first.ValueKind.ToString();
            return $"{{{names}}} with {value.GetArrayLength()} rows of {{{rowNames}}}";
        }
        catch (JsonException)
        {
            return "not JSON";
        }
    }

    private static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static Guid? Guid(JsonElement element, string name) =>
        System.Guid.TryParse(Str(element, name), out var id) && id != System.Guid.Empty ? id : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                // Multipart-style bodies sometimes echo booleans back as strings.
                JsonValueKind.String when bool.TryParse(v.GetString(), out var parsed) => parsed,
                _ => null,
            }
            : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        DateTimeOffset.TryParse(Str(element, name), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var when)
            ? when
            : null;

    private static string FirstNonEmpty(params string[] candidates) =>
        candidates.FirstOrDefault(c => c.Length > 0) ?? string.Empty;
}
