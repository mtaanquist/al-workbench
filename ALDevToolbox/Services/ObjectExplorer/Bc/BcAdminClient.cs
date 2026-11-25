using System.Net;
using System.Text.Json;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <inheritdoc cref="IBcAdminClient"/>
public sealed class BcAdminClient : IBcAdminClient
{
    /// <summary>Stands in for the environment name in logs for the tenant-wide calls.</summary>
    private const string TenantScope = "the tenant";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BcAdminClient> _logger;

    public BcAdminClient(IHttpClientFactory httpFactory, ILogger<BcAdminClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BcConstants.AdminEnvironmentsUrl);
        request.UseBearer(accessToken);

        // A 404 on the tenant-wide list is not "this tenant has no environments" — that
        // answer comes back as an empty envelope. It means the route or the token's tenant
        // is wrong, and reporting it as an empty fleet would hide a broken connection.
        var body = await SendAsync(request, "reading the environments", TenantScope, NotFoundPolicy.Error, ct)
            .ConfigureAwait(false);
        return ParseEnvironments(body!);
    }

    public async Task<BcEnvironment?> GetEnvironmentAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
    {
        var url = BcConstants.AdminEnvironmentUrl(applicationFamily, environmentName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.UseBearer(accessToken);

        // A hard-deleted environment is a 404, which is an answer, not a fault:
        // the caller turns "no longer there" into its own message.
        var body = await SendAsync(request, "reading the environment", environmentName, NotFoundPolicy.Absent, ct)
            .ConfigureAwait(false);
        return body is null ? null : ParseEnvironment(body);
    }

    public async Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
    {
        var url = BcConstants.EnvironmentUpdatesUrl(applicationFamily, environmentName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.UseBearer(accessToken);

        // Same reading of a 404 as GetEnvironmentAsync: the environment is gone, so it has
        // no updates on offer. Both readers — the nightly mirror and the environment panel —
        // want that as "nothing scheduled" rather than as an error they can't act on.
        var body = await SendAsync(
                request, "reading the Business Central updates", environmentName, NotFoundPolicy.Absent, ct)
            .ConfigureAwait(false);
        return body is null ? Array.Empty<BcEnvironmentUpdate>() : ParseEnvironmentUpdates(body);
    }

    public async Task<BcTenantStorage> GetTenantStorageAsync(string accessToken, CancellationToken ct = default)
    {
        using var used = new HttpRequestMessage(HttpMethod.Get, BcConstants.AdminUsedStorageUrl);
        used.UseBearer(accessToken);
        var usedBody = await SendSettingsAsync(used, "reading the database sizes", TenantScope, ct).ConfigureAwait(false);

        using var quotas = new HttpRequestMessage(HttpMethod.Get, BcConstants.AdminQuotasUrl);
        quotas.UseBearer(accessToken);
        var quotaBody = await SendSettingsAsync(quotas, "reading the storage allowance", TenantScope, ct).ConfigureAwait(false);

        return ParseTenantStorage(usedBody, quotaBody);
    }

    internal static BcTenantStorage ParseTenantStorage(string usedJson, string quotasJson)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long? total = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(usedJson))
            {
                using var doc = JsonDocument.Parse(usedJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || Text(item, "environmentName") is not { } name) continue;
                        // -1 is Business Central saying it could not work the size out.
                        if (item.TryGetProperty("databaseStorageInKilobytes", out var kb)
                            && kb.ValueKind == JsonValueKind.Number && kb.TryGetInt64(out var size) && size >= 0)
                        {
                            sizes[name] = size;
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(quotasJson))
            {
                using var doc = JsonDocument.Parse(quotasJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("storageInKilobytes", out var storage) && storage.ValueKind == JsonValueKind.Object
                    && storage.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number
                    && t.TryGetInt64(out var allowed) && allowed > 0)
                {
                    total = allowed;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new BcApiException(null, "Business Central returned storage figures we couldn't read.", ex);
        }

        return new BcTenantStorage(sizes, total);
    }

    public async Task<IReadOnlyList<BcEnvironmentOperation>> ListEnvironmentOperationsAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
    {
        var url = BcConstants.EnvironmentOperationsUrl(applicationFamily, environmentName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.UseBearer(accessToken);

        var body = await SendAsync(
                request, "reading what Business Central has done to the environment", environmentName, NotFoundPolicy.Absent, ct)
            .ConfigureAwait(false);
        return body is null ? Array.Empty<BcEnvironmentOperation>() : ParseEnvironmentOperations(body);
    }

    public async Task<IReadOnlyList<BcSession>> ListSessionsAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
    {
        var url = BcConstants.EnvironmentSessionsUrl(applicationFamily, environmentName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.UseBearer(accessToken);

        // An environment that is gone has nobody signed in to it, which is an answer
        // rather than a fault - the same reading the operations list gives a 404.
        var body = await SendAsync(
                request, "reading who is signed in to the environment", environmentName, NotFoundPolicy.Absent, ct)
            .ConfigureAwait(false);
        return body is null ? Array.Empty<BcSession>() : ParseSessions(body);
    }

    public async Task CancelSessionAsync(
        string accessToken, string? applicationFamily, string environmentName, int sessionId,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, BcConstants.EnvironmentSessionUrl(applicationFamily, environmentName, sessionId));
        request.UseBearer(accessToken);

        // A 404 is a refusal here, not an absence: the session was there a moment ago and
        // "it has already ended" is the one thing the person needs to be told.
        await SendAsync(request, "ending the session", environmentName, NotFoundPolicy.Error, ct,
            DescribeCancelSessionFailure).ConfigureAwait(false);
        _logger.LogInformation("Ended session {SessionId} on {Environment}.", sessionId, environmentName);
    }

    /// <summary>
    /// Turns a refused cancel into a sentence. Microsoft documents no error codes of its
    /// own for this endpoint, so the one case worth telling apart is the status: a session
    /// that ended between the list and the click is a 404, and that is the likeliest
    /// failure of all because the list is minutes - sometimes seconds - old.
    /// </summary>
    internal static string DescribeCancelSessionFailure(HttpStatusCode status, string body) =>
        status == HttpStatusCode.NotFound
            ? "That session had already ended. Refresh the list to see who is signed in now."
            : DescribeSettingsFailure(status, body, "ending the session");

    /// <summary>
    /// Parses the sessions envelope. Forgiving in the same three ways the neighbouring
    /// parsers are: a body that is not JSON is a <see cref="BcApiException"/>, an answer
    /// with no list is an empty list, and a row with no session id is skipped - it could
    /// not be cancelled anyway, and it is the one field the rest of the feature rests on.
    /// </summary>
    internal static IReadOnlyList<BcSession> ParseSessions(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<BcSession>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BcApiException(null, "Business Central returned a session list we couldn't read.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<BcSession>();
            }

            var result = new List<BcSession>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (Count(item, "sessionId") is not { } id) continue;

                result.Add(new BcSession(
                    SessionId: id,
                    UserId: Text(item, "userId") ?? string.Empty,
                    ClientType: Text(item, "clientType") ?? string.Empty,
                    LogOnDate: Instant(item, "logOnDate"),
                    EntryPointOperation: Text(item, "entryPointOperation") ?? string.Empty,
                    EntryPointObjectName: Text(item, "entryPointObjectName") ?? string.Empty,
                    // Microsoft types this one as a string and its neighbour as an int,
                    // so both are read whichever way they arrive.
                    EntryPointObjectId: Text(item, "entryPointObjectId") ?? Count(item, "entryPointObjectId")?.ToString() ?? string.Empty,
                    EntryPointObjectType: Text(item, "entryPointObjectType") ?? string.Empty,
                    CurrentObjectName: Text(item, "currentObjectName") ?? string.Empty,
                    CurrentObjectId: Count(item, "currentObjectId"),
                    CurrentObjectType: Text(item, "currentObjectType") ?? string.Empty,
                    CurrentOperationDuration: Span(item, "currentOperationDuration")));
            }
            return result;
        }
    }

    public async Task<IReadOnlyList<BcTimeZone>> ListTimezonesAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BcConstants.AdminTimezonesUrl);
        request.UseBearer(accessToken);
        var body = await SendSettingsAsync(request, "reading the time zones", TenantScope, ct).ConfigureAwait(false);
        return ParseTimezones(body);
    }

    public async Task SetAppUpdateCadenceAsync(
        string accessToken, string? applicationFamily, string environmentName, string cadence, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cadence))
        {
            throw new ArgumentException("Choose how often Marketplace apps should update.", nameof(cadence));
        }

        var payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["value"] = cadence.Trim() });
        using var request = new HttpRequestMessage(
            HttpMethod.Put, BcConstants.EnvironmentAppCadenceUrl(applicationFamily, environmentName))
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        request.UseBearer(accessToken);

        await SendSettingsAsync(request, "setting the app update cadence", environmentName, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Set the Marketplace app update cadence on {Environment} to {Cadence}.", environmentName, cadence);
    }

    public async Task<bool?> GetM365AccessAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, BcConstants.EnvironmentM365AccessUrl(applicationFamily, environmentName));
        request.UseBearer(accessToken);

        var body = await SendSettingsAsync(request, "reading Microsoft 365 licence access", environmentName, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Flag(doc.RootElement, "enabled") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SetM365AccessAsync(
        string accessToken, string? applicationFamily, string environmentName, bool enabled, CancellationToken ct = default)
    {
        // The documented body sends the boolean as a string.
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["enabled"] = enabled ? "true" : "false",
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Post, BcConstants.EnvironmentM365AccessUrl(applicationFamily, environmentName))
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        request.UseBearer(accessToken);

        await SendSettingsAsync(request, "changing Microsoft 365 licence access", environmentName, ct).ConfigureAwait(false);
        _logger.LogInformation("Set Microsoft 365 licence access on {Environment} to {Enabled}.", environmentName, enabled);
    }

    public async Task RecoverEnvironmentAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
    {
        // No body: the environment is named by the route, and everything else about the
        // recovery is Microsoft's to decide.
        using var request = new HttpRequestMessage(
            HttpMethod.Post, BcConstants.EnvironmentRecoverUrl(applicationFamily, environmentName));
        request.UseBearer(accessToken);

        await SendAsync(request, "recovering the environment", environmentName, NotFoundPolicy.Error, ct,
            DescribeRecoverFailure).ConfigureAwait(false);
        _logger.LogInformation("Asked Business Central to recover {Environment}.", environmentName);
    }

    /// <summary>
    /// Turns a refused recovery into a sentence. The two codes Microsoft documents for
    /// this endpoint are different situations with different next steps, so they are told
    /// apart here rather than both reaching a consultant as "the API refused it"; anything
    /// else falls through to the shared settings wording.
    /// </summary>
    internal static string DescribeRecoverFailure(HttpStatusCode status, string body) => ErrorCode(body) switch
    {
        "deletedEnvironmentRecoveryInProgress" =>
            "Business Central is already bringing this environment back. Refresh in a few minutes to see it return.",
        "invalidStatusCannotRecoverDeletedEnvironment" =>
            "Business Central won't bring this environment back in the state it is in now. Check it in the admin centre, then try again.",
        _ => DescribeSettingsFailure(status, body, "recovering the environment"),
    };

    public async Task<BcEnvironmentCopy> CopyEnvironmentAsync(
        string accessToken, string? applicationFamily, string sourceEnvironmentName,
        string newEnvironmentName, string newEnvironmentType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newEnvironmentName))
        {
            throw new ArgumentException("Name the environment the copy becomes.", nameof(newEnvironmentName));
        }
        if (BcEnvironmentTypes.Normalize(newEnvironmentType) is not { } type)
        {
            throw new ArgumentException(
                "A copy is either a Sandbox or a Production environment.", nameof(newEnvironmentType));
        }

        // The documented body: the new environment's name and what it should be. The
        // source is named by the route, so it is never repeated here.
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["environmentName"] = newEnvironmentName.Trim(),
            ["type"] = type,
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Post, BcConstants.EnvironmentCopyUrl(applicationFamily, sourceEnvironmentName))
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        request.UseBearer(accessToken);

        var body = await SendAsync(request, "copying the environment", sourceEnvironmentName, NotFoundPolicy.Error, ct,
            DescribeCopyFailure).ConfigureAwait(false);

        var copy = ParseCopyOperation(body);
        _logger.LogInformation(
            "Asked Business Central to copy {Environment} to {NewEnvironment} ({Type}); operation {OperationId} is {Status}.",
            sourceEnvironmentName, newEnvironmentName, type, copy.OperationId, copy.Status);
        return copy;
    }

    /// <summary>
    /// Reads the operation out of a 202. Deliberately forgiving: the copy has been
    /// accepted by the time this runs, so a body we can't read costs the caller an
    /// operation id, never the write.
    /// </summary>
    internal static BcEnvironmentCopy ParseCopyOperation(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new BcEnvironmentCopy(null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? new BcEnvironmentCopy(Text(doc.RootElement, "id"), Text(doc.RootElement, "status"))
                : new BcEnvironmentCopy(null, null);
        }
        catch (JsonException)
        {
            return new BcEnvironmentCopy(null, null);
        }
    }

    /// <summary>
    /// Turns a refused copy into a sentence a consultant can act on. Every code Microsoft
    /// documents for this endpoint is a different situation with a different next step -
    /// a name already taken, a name against the rules, a tenant out of environments or out
    /// of storage, a tenant already making one - so they are told apart here rather than
    /// all arriving as "the API refused it". Anything else falls through to the shared
    /// settings wording.
    /// </summary>
    internal static string DescribeCopyFailure(HttpStatusCode status, string body) => ErrorCode(body) switch
    {
        "resourceExists" =>
            "An environment with that name already exists in the customer's Business Central. Pick another name.",
        "environmentNameNotValid" =>
            $"Business Central won't take that name. {BcEnvironmentName.Rule}",
        "maximumNumberOfEnvironmentsAllowedReached" =>
            "The customer has as many environments as their licences allow. Delete one they no longer use, or add capacity, then try again.",
        "maximumStorageCapacityUsageReached" =>
            "The customer's Business Central has no room left for another environment. Free some up, or add capacity, then try again.",
        "tenantAlreadyProvisioning" =>
            "Business Central is already creating an environment for this customer. Try again once that one has finished.",
        "environmentNotFound" =>
            "Business Central no longer has the environment you're copying. Refresh the environments and try again.",
        "conflictingDeveloperExtensions" =>
            "The environment you're copying has uploaded extensions that clash with developer extensions in the copy. "
            + "Sort the clash out in the admin centre, then copy it again.",
        _ => DescribeSettingsFailure(status, body, "copying the environment"),
    };

    public async Task SelectTargetVersionAsync(
        string accessToken, string? applicationFamily, string environmentName,
        string targetVersion, string? targetVersionType,
        DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            throw new ArgumentException("Choose the version to update to.", nameof(targetVersion));
        }

        var payload = new Dictionary<string, object> { ["selected"] = true };
        if (!string.IsNullOrWhiteSpace(targetVersionType)) payload["targetVersionType"] = targetVersionType.Trim();
        // Both scheduling fields travel inside `scheduleDetails`, the same place the updates
        // read hands them back. At the top level Business Central answers 200 and ignores
        // them, so the date silently stays where it was. The block is only sent when the
        // caller is actually moving something: a PATCH that omits it leaves the customer's
        // existing slot alone.
        var schedule = new Dictionary<string, object>();
        // ISO-8601 in UTC, the form the documented body shows.
        if (selectedDateTime is { } when)
        {
            schedule["selectedDateTime"] = when.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ",
                System.Globalization.CultureInfo.InvariantCulture);
        }
        // A real JSON boolean, as the read returns it. If Business Central ever refuses it,
        // the string form the Microsoft 365 licence endpoint documents is the first thing to try.
        if (ignoreUpdateWindow is { } ignore) schedule["ignoreUpdateWindow"] = ignore;
        if (schedule.Count > 0) payload["scheduleDetails"] = schedule;

        using var request = new HttpRequestMessage(
            HttpMethod.Patch, BcConstants.EnvironmentUpdateUrl(applicationFamily, environmentName, targetVersion))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"),
        };
        request.UseBearer(accessToken);

        await SendSettingsAsync(request, "choosing the next update", environmentName, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Selected Business Central {Version} as the next update for {Environment} (date {SelectedDateTime}, ignoreUpdateWindow {IgnoreUpdateWindow}).",
            targetVersion, environmentName, selectedDateTime, ignoreUpdateWindow);
    }

    /// <summary>
    /// Sends one environment-settings request and maps a refusal to a message keyed on the
    /// error <em>code</em>. Returns the body so a reader can parse it.
    /// </summary>
    private async Task<string> SendSettingsAsync(
        HttpRequestMessage request, string action, string environmentName, CancellationToken ct) =>
        // Never null: a 404 is a refusal here, so the send has already thrown.
        (await SendAsync(request, action, environmentName, NotFoundPolicy.Error, ct,
            (status, body) => DescribeSettingsFailure(status, body, action)).ConfigureAwait(false))!;

    /// <summary>What a <c>404</c> means for one Admin Center call.</summary>
    private enum NotFoundPolicy
    {
        /// <summary>A 404 is a fault like any other status and becomes a <see cref="BcApiException"/>.</summary>
        Error,

        /// <summary>
        /// A 404 means Business Central no longer has the thing being asked about, which is an
        /// answer rather than a fault: the send returns null and the caller turns that into its
        /// own empty result.
        /// </summary>
        Absent,
    }

    /// <summary>
    /// Sends one Admin Center request and maps transport faults and non-success statuses to
    /// <see cref="BcApiException"/> with a short, secret-free detail.
    /// <paramref name="action"/> is a gerund for the message ("reading the environments").
    /// <paramref name="environmentName"/> names the environment in the log, or
    /// <see cref="TenantScope"/> for a call that isn't about one environment.
    /// <paramref name="describeFailure"/> replaces the generic refusal wording for the calls
    /// whose error codes are worth translating into something a consultant can act on.
    /// </summary>
    /// <returns>
    /// The response body, or <c>null</c> for a 404 the caller asked to read as absent.
    /// </returns>
    private async Task<string?> SendAsync(
        HttpRequestMessage request, string action, string environmentName, NotFoundPolicy notFound,
        CancellationToken ct, Func<HttpStatusCode, string, string>? describeFailure = null)
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
            if (notFound == NotFoundPolicy.Absent && response.StatusCode == HttpStatusCode.NotFound) return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return body;

            // Read the error envelope, don't just log the status. Microsoft returns a
            // stable `code` and a diagnostic `message` here, and without them a 401 is
            // indistinguishable from a 403 in the logs — which is how "the app isn't on
            // the authorized-apps list" got misread as "GDAP is missing" for a customer
            // who had no GDAP relationship in the first place.
            var detail = ExtractError(body);
            _logger.LogWarning("BC admin call ({Action}) for {Environment} returned {Status}. {Detail}",
                action, environmentName, response.StatusCode, detail.Length > 0 ? detail : "(no error body)");
            throw new BcApiException(response.StatusCode, describeFailure is null
                ? $"The Admin Center API returned {(int)response.StatusCode}. {detail}".TrimEnd()
                : describeFailure(response.StatusCode, body))
            {
                RetryAfter = BcThrottleHandler.RequestedWait(response, TimeProvider.System.GetUtcNow()),
            };
        }
    }

    /// <summary>
    /// Turns a refused settings write into something a consultant can act on, keyed on the
    /// error code rather than Microsoft's prose.
    /// </summary>
    internal static string DescribeSettingsFailure(HttpStatusCode status, string body, string action)
    {
        var code = ErrorCode(body);
        var detail = ExtractError(body);
        return code switch
        {
            "environmentNotFound" =>
                "Business Central no longer has this environment. Refresh the environments and try again.",
            "applicationTypeDoesNotExist" =>
                "Business Central didn't recognise this environment's application family. Refresh the environments and try again.",
            "cannotSetAppInsightsKey" or "EnvironmentNotActive" =>
                "The environment has to be active before this can change. Wait for Business Central to finish what it's doing, then try again.",
            _ => $"Business Central refused the change while {action}. {detail}".TrimEnd(),
        };
    }

    /// <summary>Parses the tenant-wide time-zone list.</summary>
    internal static IReadOnlyList<BcTimeZone> ParseTimezones(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<BcTimeZone>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BcApiException(null, "Business Central returned a time-zone list we couldn't read.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<BcTimeZone>();
            }

            var result = new List<BcTimeZone>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = Text(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                result.Add(new BcTimeZone(id, Text(item, "displayName") ?? id, Text(item, "currentUtcOffset") ?? string.Empty));
            }
            return result;
        }
    }

    public async Task<BcUpdateSettings?> GetUpdateSettingsAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
    {
        var url = BcConstants.EnvironmentUpdateSettingsUrl(applicationFamily, environmentName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.UseBearer(accessToken);

        // An environment that has since been removed is an answer, not a fault - the
        // same treatment GetEnvironmentAsync gives a 404.
        var body = await SendAsync(request, "reading the update window", environmentName, NotFoundPolicy.Absent, ct)
            .ConfigureAwait(false);
        return body is null ? null : ParseUpdateSettings(body);
    }

    public async Task SetUpdateSettingsAsync(
        string accessToken, string? applicationFamily, string environmentName,
        TimeOnly start, TimeOnly end, string windowsTimeZoneId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(windowsTimeZoneId))
        {
            throw new ArgumentException(
                "Business Central needs a Windows time-zone id for an update window.", nameof(windowsTimeZoneId));
        }

        // The wall-time + timezone parameter set, never the UTC one: the UTC set resets
        // the time zone the admin center displays to the country default.
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["preferredStartTime"] = start.ToString("HH\\:mm"),
            ["preferredEndTime"] = end.ToString("HH\\:mm"),
            ["timeZoneId"] = windowsTimeZoneId.Trim(),
        });

        var url = BcConstants.EnvironmentUpdateSettingsUrl(applicationFamily, environmentName);
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        request.UseBearer(accessToken);

        // A 404 on a write is a refusal, not an absence: the environment has to exist for
        // the window to be worth setting, and DescribeUpdateSettingsFailure turns the
        // environmentNotFound code into "refresh the environments and try again".
        await SendAsync(request, "setting the update window", environmentName, NotFoundPolicy.Error, ct,
            DescribeUpdateSettingsFailure).ConfigureAwait(false);

        _logger.LogInformation(
            "Set the Business Central update window for {Environment} to {Start}-{End} ({TimeZone}).",
            environmentName, start, end, windowsTimeZoneId);
    }

    /// <summary>
    /// Turns a rejected update-window write into something a consultant can act on. Keyed
    /// on the error <em>code</em>, because the accompanying message is Microsoft's prose
    /// and not guaranteed stable.
    /// </summary>
    internal static string DescribeUpdateSettingsFailure(HttpStatusCode status, string body)
    {
        var code = ErrorCode(body);
        var detail = ExtractError(body);
        return code switch
        {
            "ScheduledUpgradeConstraintViolation" =>
                "Business Central refused this window because it clashes with the update already scheduled for this environment - "
                + "the update would fall outside the window, or it is due today and the window has passed. "
                + "Change the window, or move the update date in the admin center.",
            "invalidRange" =>
                "Business Central refused this window as too small. Give the update more room between the start and end times.",
            "environmentNotFound" =>
                "Business Central no longer has this environment. Refresh the environments and try again.",
            _ => $"The Admin Center API returned {(int)status}. {detail}".TrimEnd(),
        };
    }

    /// <summary>Reads the <c>code</c> from an Admin Center error envelope, flat or nested under <c>error</c>.</summary>
    private static string ErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;
            if (root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object) root = nested;
            return root.TryGetProperty("code", out var c) ? c.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Parses the environment <c>updates</c> envelope. An unreleased version carries only
    /// <c>expectedAvailability</c>; a released one carries <c>scheduleDetails</c>. Both
    /// shapes appear in the same list, so every nested field is optional.
    /// </summary>
    internal static IReadOnlyList<BcEnvironmentUpdate> ParseEnvironmentUpdates(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<BcEnvironmentUpdate>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BcApiException(null, "Business Central returned an update list we couldn't read.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<BcEnvironmentUpdate>();
            }

            var result = new List<BcEnvironmentUpdate>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var version = Text(item, "targetVersion");
                if (string.IsNullOrWhiteSpace(version)) continue;

                // Both blocks are optional and appear on different kinds of row, so
                // neither may be read unless it is actually an object: the shared Text
                // helper throws on an element that was never assigned.
                var hasSchedule = item.TryGetProperty("scheduleDetails", out var schedule)
                    && schedule.ValueKind == JsonValueKind.Object;
                var hasExpected = item.TryGetProperty("expectedAvailability", out var expected)
                    && expected.ValueKind == JsonValueKind.Object;

                result.Add(new BcEnvironmentUpdate(
                    TargetVersion: version,
                    Available: Flag(item, "available") ?? false,
                    Selected: Flag(item, "selected") ?? false,
                    UpdateStatus: Text(item, "updateStatus") ?? string.Empty,
                    TargetVersionType: Text(item, "targetVersionType") ?? string.Empty,
                    SelectedDateTime: hasSchedule ? Moment(schedule, "selectedDateTime") : null,
                    LatestSelectableDateTime: hasSchedule ? Moment(schedule, "latestSelectableDateTime") : null,
                    IgnoreUpdateWindow: hasSchedule && (Flag(schedule, "ignoreUpdateWindow") ?? false),
                    RolloutStatus: hasSchedule ? Text(schedule, "rolloutStatus") ?? string.Empty : string.Empty,
                    ExpectedMonth: hasExpected ? Number(expected, "month") : null,
                    ExpectedYear: hasExpected ? Number(expected, "year") : null));
            }
            return result;
        }
    }

    internal static IReadOnlyList<BcEnvironmentOperation> ParseEnvironmentOperations(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<BcEnvironmentOperation>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BcApiException(null, "Business Central returned an operations list we couldn't read.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<BcEnvironmentOperation>();
            }

            var result = new List<BcEnvironmentOperation>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var type = Text(item, "type");
                if (string.IsNullOrWhiteSpace(type)) continue;

                // The parameters differ for every kind of operation, so they are kept
                // as flat text and the display picks out the ones it knows.
                var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (item.TryGetProperty("parameters", out var bag) && bag.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in bag.EnumerateObject())
                    {
                        var text = p.Value.ValueKind switch
                        {
                            JsonValueKind.String => p.Value.GetString(),
                            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => p.Value.GetRawText(),
                            _ => null,
                        };
                        if (!string.IsNullOrWhiteSpace(text)) parameters[p.Name] = text;
                    }
                }

                result.Add(new BcEnvironmentOperation(
                    Id: Text(item, "id") ?? string.Empty,
                    Type: type,
                    Status: Text(item, "status") ?? string.Empty,
                    CreatedOn: Moment(item, "createdOn"),
                    StartedOn: Moment(item, "startedOn"),
                    CompletedOn: Moment(item, "completedOn"),
                    CreatedBy: Text(item, "createdBy") ?? string.Empty,
                    ErrorMessage: Text(item, "errorMessage") ?? string.Empty,
                    Parameters: parameters));
            }
            return result;
        }
    }

    private static bool? Flag(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(v.GetString(), out var parsed) => parsed,
                _ => null,
            }
            : null;

    private static int? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
                _ => null,
            }
            : null;

    /// <summary>
    /// A whole number by name, tolerating Microsoft's casing and a number that arrived as
    /// a string. <see cref="Number"/>'s case-sensitive sibling exists for the update
    /// payload, whose shape is known; this one reads a session, where the casing is not.
    /// </summary>
    private static int? Count(JsonElement element, string property) =>
        TryProperty(element, property, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var n) => n,
                _ => null,
            }
            : null;

    /// <summary>A moment by name, tolerating Microsoft's casing. <see cref="Moment"/>'s case-insensitive sibling.</summary>
    private static DateTimeOffset? Instant(JsonElement element, string property) =>
        TryProperty(element, property, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var when)
            ? when
            : null;

    /// <summary>
    /// A duration by name. <b>Microsoft documents <c>currentOperationDuration</c> as a
    /// <c>long</c> and names no unit</b>, so both readings it could plausibly be are
    /// accepted: a number is read as milliseconds, and a string as a time span
    /// (<c>00:05:12.34</c>), which is the form the .NET server behind this API writes one
    /// in. A negative number is nonsense and reads as "not said".
    /// </summary>
    private static TimeSpan? Span(JsonElement element, string property)
    {
        if (!TryProperty(element, property, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var ms) && ms >= 0 => TimeSpan.FromMilliseconds(ms),
            JsonValueKind.String => ReadSpanText(v.GetString()),
            _ => null,
        };
    }

    private static TimeSpan? ReadSpanText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // A bare number first, and milliseconds as it would be as a JSON number. It has
        // to come first: TimeSpan.TryParse reads "2500" happily - as 2500 *days*.
        if (long.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var ms))
        {
            return ms >= 0 ? TimeSpan.FromMilliseconds(ms) : null;
        }
        return TimeSpan.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var span)
            && span >= TimeSpan.Zero
            ? span
            : null;
    }

    private static DateTimeOffset? Moment(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var when)
            ? when
            : null;

    /// <summary>
    /// Parses <c>settings/upgrade</c>. The API answers a literal <c>null</c> body for an
    /// environment with no window, so that is a normal result rather than a parse failure.
    /// Only the wall-time trio is read: the UTC pair the response also carries names the
    /// next occurrence and moves, so it would go stale in the cache within a day.
    /// </summary>
    internal static BcUpdateSettings? ParseUpdateSettings(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BcApiException(null, "Business Central returned an update window we couldn't read.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var start = ReadWallTime(root, "preferredStartTime");
            var end = ReadWallTime(root, "preferredEndTime");
            var tz = Text(root, "timeZoneId");
            if (start is null && end is null && string.IsNullOrWhiteSpace(tz)) return null;
            return new BcUpdateSettings(start, end, tz);
        }
    }

    /// <summary>Reads an <c>HH:mm</c> wall time; tolerates <c>HH:mm:ss</c> in case the API grows seconds.</summary>
    private static TimeOnly? ReadWallTime(JsonElement element, string property)
    {
        var raw = Text(element, property);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return TimeOnly.TryParseExact(raw, "HH\\:mm", System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.None, out var exact)
            ? exact
            : TimeOnly.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var loose) ? loose : null;
    }

    /// <summary>
    /// Pulls a short, secret-free summary out of an Admin Center error envelope
    /// (<c>{ "code": ..., "message": ... }</c>), falling back to the OData <c>error.message</c>
    /// shape the automation API uses. Empty when the body isn't JSON or carries neither.
    /// </summary>
    internal static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;

            // The automation API nests the same two fields under "error".
            if (root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                root = nested;
            }

            var code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var detail = (code, message) switch
            {
                ({ Length: > 0 }, { Length: > 0 }) => $"{code}: {message}",
                ({ Length: > 0 }, _) => code!,
                (_, { Length: > 0 }) => message!,
                _ => string.Empty,
            };
            return detail.Length > 300 ? detail[..300] : detail;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>Parses the Admin Center <c>{ "value": [ ... ] }</c> environments envelope. Internal for the client test.</summary>
    internal static IReadOnlyList<BcEnvironment> ParseEnvironments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<BcEnvironment>();
        }

        var result = new List<BcEnvironment>();
        foreach (var item in value.EnumerateArray())
        {
            if (ReadEnvironment(item) is { } env) result.Add(env);
        }
        return result;
    }

    /// <summary>
    /// Parses the by-name environment response. That endpoint returns the environment
    /// object directly, but tolerates the list envelope too (Microsoft has shipped both
    /// shapes on neighbouring routes). Internal for the client test.
    /// </summary>
    internal static BcEnvironment? ParseEnvironment(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (ReadEnvironment(item) is { } fromList) return fromList;
            }
            return null;
        }
        return ReadEnvironment(root);
    }

    /// <summary>
    /// Reads one environment object. Every field beyond name/type is optional — a
    /// payload without <c>versionDetails</c>, or with it null, must not throw — and
    /// enum-ish strings are kept verbatim because Microsoft's casing varies per
    /// endpoint. Returns null for an entry with no usable name.
    /// </summary>
    private static BcEnvironment? ReadEnvironment(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var name = Text(item, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        return new BcEnvironment(name, Text(item, "type") ?? string.Empty)
        {
            FriendlyName = Text(item, "friendlyName"),
            ApplicationFamily = Text(item, "applicationFamily"),
            Status = Text(item, "status"),
            CountryCode = Text(item, "countryCode"),
            AadTenantId = Guid.TryParse(Text(item, "aadTenantId"), out var tenant) ? tenant : null,
            WebClientLoginUrl = Text(item, "webClientLoginUrl"),
            LocationName = Text(item, "locationName"),
            GeoName = Text(item, "geoName"),
            RingName = Text(item, "ringName"),
            AppSourceAppsUpdateCadence = Text(item, "appSourceAppsUpdateCadence"),
            Version = ReadVersion(item),
            GracePeriodStartDate = Timestamp(item, "gracePeriodStartDate"),
            EnforcedUpdatePeriodStartDate = Timestamp(item, "enforcedUpdatePeriodStartDate"),
            SoftDeletedOn = Timestamp(item, "softDeletedOn"),
            HardDeletePendingOn = Timestamp(item, "hardDeletePendingOn"),
            DeleteReason = Text(item, "deleteReason"),
        };
    }

    /// <summary>The version lives under <c>versionDetails</c>, which can be absent or null.</summary>
    private static string? ReadVersion(JsonElement item)
    {
        if (!TryProperty(item, "versionDetails", out var details) || details.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return Text(details, "version") ?? Text(details, "applicationVersion");
    }

    /// <summary>
    /// One property of a JSON object by name, tolerating Microsoft's casing. The exact
    /// spelling is tried first and a case-insensitive scan only when that misses, so the
    /// common path costs nothing.
    /// <para>
    /// Needed because this payload mixes the two: the environments response returns
    /// <c>SoftDeletedOn</c>, <c>HardDeletePendingOn</c> and <c>DeleteReason</c> in
    /// PascalCase beside camelCase neighbours, and a case-sensitive lookup read every
    /// deleted environment as one with no deletion dates at all. The same reason every
    /// enum-ish value here is compared case-insensitively — see <see cref="BcEnvironment"/>.
    /// </para>
    /// </summary>
    private static bool TryProperty(JsonElement item, string property, out JsonElement value)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }
        if (item.TryGetProperty(property, out value)) return true;

        foreach (var candidate in item.EnumerateObject())
        {
            if (string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate.Value;
                return true;
            }
        }
        return false;
    }

    private static string? Text(JsonElement item, string property)
    {
        if (!TryProperty(item, property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static DateTime? Timestamp(JsonElement item, string property)
    {
        if (!TryProperty(item, property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        return value.TryGetDateTime(out var when)
            ? DateTime.SpecifyKind(when.ToUniversalTime(), DateTimeKind.Utc)
            : null;
    }
}
