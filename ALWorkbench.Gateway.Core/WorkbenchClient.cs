using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ALWorkbench.Gateway.Contracts;

namespace ALWorkbench.Gateway.Core;

/// <summary>
/// The gateway's side of the protocol routes, each request signed with a fresh token
/// (<see cref="GatewayRequestToken"/>) bound to its method, path and body. Turns the
/// workbench's answers into results or the exceptions in <c>WorkbenchExceptions.cs</c>,
/// so the loop decides what to do from the kind of answer, not from status codes.
/// See <c>.design/on-prem-gateway.md</c>, "Registration and authentication" and "Commands".
/// </summary>
public sealed class WorkbenchClient : IDisposable
{
    /// <summary>How long an ordinary call may take before it counts as unreachable.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The long-poll is held up to 30 seconds by the workbench; this leaves room for a slow network.</summary>
    public static readonly TimeSpan LongPollRequestTimeout = GatewayProtocol.LongPollTimeout + TimeSpan.FromSeconds(30);

    /// <summary>A command's file may be tens of megabytes over a slow line.</summary>
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly GatewaySettings _settings;
    private readonly IGatewayClock _clock;
    private readonly ECDsa _key;
    private readonly string _audience;

    public WorkbenchClient(HttpClient http, GatewaySettings settings, ISigningKeyStore keys, IGatewayClock clock)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _http = http;
        _settings = settings;
        _clock = clock;
        _key = keys.Open(settings.KeyName);
        _audience = WorkbenchAddress.AudienceFor(settings.WorkbenchUrl);
    }

    public void Dispose() => _key.Dispose();

    /// <summary>Says the gateway is alive, which commands it is running, and which a restart cut short.</summary>
    public async Task<GatewayHeartbeatResponse> HeartbeatAsync(GatewayHeartbeatRequest request, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, GatewayProtocol.HeartbeatPath, Json(request), RequestTimeout, ct).ConfigureAwait(false);
        return await ReadJsonAsync<GatewayHeartbeatResponse>(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The long-poll: the next command, already claimed for this gateway, or null when
    /// none came while the workbench held the request open.
    /// </summary>
    public async Task<GatewayCommandMessage?> NextCommandAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, GatewayProtocol.NextCommandPath, null, LongPollRequestTimeout, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        return await ReadJsonAsync<GatewayCommandMessage>(response, ct).ConfigureAwait(false);
    }

    public async Task PostProgressAsync(long commandId, IReadOnlyList<string> lines, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, GatewayProtocol.CommandProgressPath(commandId),
            Json(new GatewayCommandProgressRequest { Lines = lines }), RequestTimeout, ct).ConfigureAwait(false);
    }

    public async Task PostResultAsync(long commandId, GatewayCommandResultRequest result, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, GatewayProtocol.CommandResultPath(commandId),
            Json(result), RequestTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads one of a command's files into <paramref name="destination"/>, stopping
    /// as soon as more than <paramref name="maxBytes"/> arrive so a wrong answer cannot
    /// fill the disk.
    /// </summary>
    /// <returns>The number of bytes written, or -1 when there were more than <paramref name="maxBytes"/>.</returns>
    public async Task<long> DownloadFileAsync(long commandId, long fileId, Stream destination, long maxBytes, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, GatewayProtocol.CommandFilePath(commandId, fileId), null,
            DownloadTimeout, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes) return -1;
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            return total;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException && !ct.IsCancellationRequested)
        {
            throw new WorkbenchUnreachableException($"The download was cut off: {ex.Message}", ex);
        }
    }

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, byte[]? body, TimeSpan timeout,
        CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var path = WorkbenchAddress.PathFor(_settings.WorkbenchUrl, route);
        var token = GatewayRequestToken.Create(_key, _settings.GatewayId, _audience, method.Method, path,
            body ?? [], hasBody: body is not null, _clock.UtcNow);
        using var request = new HttpRequestMessage(method, new Uri(new Uri(_settings.WorkbenchUrl), path));
        request.Headers.Authorization = new AuthenticationHeaderValue(GatewayRequestToken.AuthorizationScheme, token);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        return await SendAsync(_http, request, timeout, completion, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one request with its own timeout and maps the answer: success comes back,
    /// everything else becomes the matching exception. Shared with the anonymous
    /// registration exchange.
    /// </summary>
    internal static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, TimeSpan timeout,
        HttpCompletionOption completion, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, completion, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WorkbenchUnreachableException($"AL Workbench did not answer within {timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new WorkbenchUnreachableException($"Could not reach AL Workbench: {ex.Message}", ex);
        }

        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            var reason = await ReadReasonAsync(response, ct).ConfigureAwait(false);
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new GatewayCredentialRefusedException(reason),
                HttpStatusCode.Forbidden => new GatewayNotApprovedException(reason),
                HttpStatusCode.Conflict => new CommandClosedException(reason),
                HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or HttpStatusCode.ProxyAuthenticationRequired
                    => new WorkbenchUnreachableException($"AL Workbench answered {(int)response.StatusCode}: {reason}"),
                >= HttpStatusCode.InternalServerError
                    => new WorkbenchUnreachableException($"AL Workbench answered {(int)response.StatusCode}: {reason}"),
                _ => new WorkbenchRefusedException(response.StatusCode, reason),
            };
        }
    }

    internal static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var value = await JsonSerializer.DeserializeAsync<T>(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new JsonException("Empty answer.");
        }
        catch (JsonException ex)
        {
            // An unknown verb lands here too: the contracts refuse it while reading.
            throw new WorkbenchRefusedException(response.StatusCode,
                $"AL Workbench sent an answer this gateway does not understand ({ex.Message}). Update the gateway.");
        }
    }

    /// <summary>The workbench's own words from a <see cref="GatewayErrorResponse"/>, or the status when there are none.</summary>
    private static async Task<string> ReadReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var error = JsonSerializer.Deserialize<GatewayErrorResponse>(text);
            if (error is { Errors.Count: > 0 }) return string.Join(" ", error.Errors.Values);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            // A proxy's HTML error page, say: fall through to the status line.
        }
        return $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();
    }
}
