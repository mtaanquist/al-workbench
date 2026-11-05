using System.Net;
using System.Net.Http.Headers;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Downloads a Business Central DVD ZIP from a SiteAdmin-allowed URL to a temp
/// file so the release importer can walk it like an uploaded folder ZIP. Lets
/// admins paste Microsoft's multi-GB download link instead of fetching and
/// re-zipping the <c>Applications/</c> folder by hand.
///
/// <para>
/// Two guards apply to the admin-supplied URL: the host must match the
/// SiteAdmin allow-list (<see cref="SystemSettingsService.GetReleaseDownloadAllowedHostsAsync"/>),
/// and the named <see cref="System.Net.Http.HttpClient"/> dials through
/// <see cref="OAuth.SsrfGuard"/> so neither the URL nor any redirect hop can
/// reach an internal address. Redirects are followed (Microsoft download URLs
/// commonly 302 to a CDN) but only the pasted host is allow-list-checked; the
/// SSRF guard is what constrains redirect targets.
/// </para>
/// </summary>
public sealed class DvdDownloadService
{
    /// <summary>Temp-file prefix, mirroring the <c>oe-folder-</c> upload staging convention.</summary>
    public const string TempPrefix = "oe-dvd-";

    /// <summary>
    /// Hard cap on the downloaded body. A first-party DVD ZIP is 1–3 GB; 5 GB
    /// leaves margin while still refusing a runaway/bogus URL before it fills
    /// the container's scratch disk.
    /// </summary>
    public const long MaxDownloadBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// Per-<see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/> idle
    /// window: if the server delivers zero bytes for this long, abort. The named
    /// <see cref="HttpClient.Timeout"/> is the overall-request budget, but with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> it primarily
    /// covers the time-to-headers — a CDN edge that opens the body and stops
    /// sending can leave a worker thread stuck indefinitely. 60 s is generous
    /// compared to a healthy CDN's gap-between-packets (sub-second on a real
    /// transfer) and tight enough that a dead connection fails the job in
    /// minutes instead of holding the queue hostage forever.
    /// </summary>
    public static readonly TimeSpan IdleReadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Number of GET attempts (1 initial + N-1 resumes) before the download
    /// gives up. On a stall, the retry sends <c>Range: bytes=N-</c> so the body
    /// continues from where it stopped instead of restarting at zero. Microsoft's
    /// CDN supports range requests; the retry forces a fresh TCP connection
    /// (combined with <c>PooledConnectionLifetime</c> in the HttpClient config)
    /// so we don't reuse the half-dead one that just stalled.
    /// </summary>
    public const int MaxDownloadAttempts = 4;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SystemSettingsService _settings;
    private readonly ILogger<DvdDownloadService> _logger;

    public DvdDownloadService(
        IHttpClientFactory httpClientFactory,
        SystemSettingsService settings,
        ILogger<DvdDownloadService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Validates the URL against the allow-list, downloads it to a temp file,
    /// and returns the path. The caller owns the file and must delete it once
    /// the import has consumed it. Throws <see cref="PlanValidationException"/>
    /// (field key <c>DvdUrl</c>) for any user-correctable failure so the form
    /// can surface it inline.
    /// </summary>
    public async Task<string> DownloadToTempAsync(string? url, CancellationToken ct = default)
    {
        var uri = await ValidateUrlAsync(url, ct);
        return await DownloadValidatedAsync(uri, ct);
    }

    private async Task<string> DownloadValidatedAsync(Uri uri, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N") + ".zip");
        var client = _httpClientFactory.CreateClient(nameof(DvdDownloadService));

        try
        {
            // Keep the dest stream open across resume attempts so we can
            // append directly without re-opening / re-positioning between GETs.
            await using var dest = File.Create(tempPath);
            var bytesWritten = await CopyWithRetriesAsync(
                client, uri, dest, MaxDownloadAttempts, IdleReadTimeout, _logger, ct);

            _logger.LogInformation(
                "Downloaded DVD ZIP from {Host} ({Bytes} bytes) to staging.",
                uri.Host, bytesWritten);
            return tempPath;
        }
        catch (PlanValidationException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (TimeoutException ex) when (!ct.IsCancellationRequested)
        {
            // CopyWithRetriesAsync exhausted its attempts — every Range-resume
            // also stalled. Distinct from the connect-failure case below
            // because the URL itself isn't bad; the admin's retry will likely
            // hit a different CDN edge / fresh connection set.
            TryDelete(tempPath);
            _logger.LogWarning(ex, "DVD download from {Host} stalled after {Attempts} attempts.", uri.Host, MaxDownloadAttempts);
            throw Invalid("The download stalled even after retrying — the server keeps cutting the stream short. Try again; if it keeps happening the CDN may be having issues.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // SsrfGuard refuses internal targets with IOException; transient
            // network failures land here too. Surface as an inline form error
            // rather than a 500 — the admin can fix the URL or retry.
            TryDelete(tempPath);
            _logger.LogWarning(ex, "DVD download from {Host} failed.", uri.Host);
            throw Invalid("Couldn't download from that URL — it may be unreachable, blocked, or not a public address.");
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Sends the GET (or a Range-resume GET on retries) and copies the body to
    /// <paramref name="dest"/>. On a per-read idle-timeout stall it re-issues
    /// the request with <c>Range: bytes={written}-</c> and appends the new
    /// bytes — Microsoft's CDN supports range requests, and pairing this with
    /// the <c>PooledConnectionLifetime</c> setting on the named HttpClient
    /// gives the retry a fresh TCP connection rather than the half-dead one
    /// that just stalled.
    ///
    /// <para>Internal so the retry path can be tested directly with a
    /// stubbed <see cref="HttpClient"/> backed by a <see cref="DelegatingHandler"/>,
    /// without spinning up a real HTTP server or DB-backed SystemSettingsService.</para>
    ///
    /// <para>Returns the cumulative byte count actually written to
    /// <paramref name="dest"/>. Throws <see cref="TimeoutException"/> if every
    /// attempt stalled; the caller translates that to a friendly form error.</para>
    /// </summary>
    internal static async Task<long> CopyWithRetriesAsync(
        HttpClient client,
        Uri uri,
        Stream dest,
        int maxAttempts,
        TimeSpan idleReadTimeout,
        ILogger logger,
        CancellationToken ct)
    {
        long bytesWritten = 0;
        TimeoutException? lastStall = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (bytesWritten > 0)
            {
                request.Headers.Range = new RangeHeaderValue(bytesWritten, null);
            }

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (bytesWritten == 0)
            {
                // Initial attempt — must succeed with 2xx.
                if (!response.IsSuccessStatusCode)
                {
                    throw Invalid($"The server returned {(int)response.StatusCode} for that URL. Check the link and try again.");
                }
            }
            else
            {
                // Resume attempt — interpret status against the Range header
                // we just sent.
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    // Server ignored the Range header and gave us the whole
                    // body again. Truncate dest and start over.
                    logger.LogWarning(
                        "DVD server at {Host} ignored Range on resume; restarting from byte 0.",
                        uri.Host);
                    dest.SetLength(0);
                    dest.Position = 0;
                    bytesWritten = 0;
                }
                else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // Server says the offset is past EOF — we already have
                    // every byte. Treat as success.
                    return bytesWritten;
                }
                else if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    throw Invalid($"The server returned {(int)response.StatusCode} when resuming the download.");
                }
            }

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            try
            {
                bytesWritten = await CopyWithCapAsync(source, dest, idleReadTimeout, bytesWritten, ct);
                // Body ended cleanly.
                return bytesWritten;
            }
            catch (TimeoutException ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                lastStall = ex;
                // CopyWithCapAsync doesn't return its tally on throw, but every
                // successful read inside it advanced dest.Position via WriteAsync —
                // so dest.Position IS the partial progress we need the next Range
                // header to skip past. Read it back here.
                bytesWritten = dest.Position;
                logger.LogWarning(
                    "DVD download from {Host} stalled at byte {BytesWritten}; attempt {Attempt}/{Max} — retrying with Range resume.",
                    uri.Host, bytesWritten, attempt, maxAttempts);
                // Loop falls through to the next attempt.
            }
        }

        // Every attempt stalled.
        throw lastStall ?? new TimeoutException(
            $"The download stalled — {maxAttempts} attempts all gave up before the body finished.");
    }

    /// <summary>
    /// Validates the URL synchronously (https + allow-listed host) so the
    /// import endpoint can reject a bad URL inline before queuing a job, rather
    /// than surfacing it later as a failed release. Throws
    /// <see cref="PlanValidationException"/> (field key <c>DvdUrl</c>).
    /// </summary>
    public async Task ValidateUrlForQueueAsync(string? url, CancellationToken ct = default) =>
        await ValidateUrlAsync(url, ct);

    private async Task<Uri> ValidateUrlAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw Invalid("Enter a download URL.");
        }
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw Invalid("Enter an absolute https:// URL.");
        }

        var allowlist = await _settings.GetReleaseDownloadAllowedHostsAsync(ct);
        if (!SystemSettingsService.IsHostAllowed(uri.Host, allowlist))
        {
            throw Invalid(
                $"'{uri.Host}' isn't on the download allow-list. A SiteAdmin must add it under "
                + "Site administration → Settings before this host can be used.");
        }
        return uri;
    }

    /// <summary>
    /// Internal so tests can drive it directly with a stalled Stream without
    /// spinning up an HTTP server. <paramref name="idleReadTimeout"/> resets on
    /// every successful read — only a window of zero progress trips it. The
    /// caller's <paramref name="ct"/> always wins: if it fires first the
    /// <see cref="OperationCanceledException"/> bubbles up unchanged so shutdown
    /// handling stays clean (the worker's <c>stoppingToken</c> shouldn't be
    /// converted into a TimeoutException).
    ///
    /// <para>Returns the cumulative bytes written, including the
    /// <paramref name="startingBytesWritten"/> baseline. The cap is checked
    /// against the cumulative value so a resume that exceeds the 5 GB ceiling
    /// still trips it instead of being measured per-attempt.</para>
    /// </summary>
    internal static async Task<long> CopyWithCapAsync(Stream source, Stream dest, TimeSpan idleReadTimeout, long startingBytesWritten, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = startingBytesWritten;
        while (true)
        {
            int read;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(idleReadTimeout);
                try
                {
                    read = await source.ReadAsync(buffer.AsMemory(), idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"The download stalled — no data received within {idleReadTimeout.TotalSeconds:N0} seconds.");
                }
            }
            if (read <= 0) return total;
            total += read;
            if (total > MaxDownloadBytes)
            {
                throw Invalid("That download is larger than the 5 GB limit; it doesn't look like a BC DVD ZIP.");
            }
            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static PlanValidationException Invalid(string message) =>
        new(new Dictionary<string, string> { ["DvdUrl"] = message });

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* swallow — best-effort cleanup */ }
    }
}
