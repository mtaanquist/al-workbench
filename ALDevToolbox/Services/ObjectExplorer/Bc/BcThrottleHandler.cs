using System.Net;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Does what Business Central asks when it says slow down. A read answered with
/// <c>429 Too Many Requests</c> (or a <c>503</c> that names a wait) is tried once more
/// after the <c>Retry-After</c> it carried; without this the nightly environment sweep
/// treated throttling as one more failure and went straight on to the next request,
/// which is the opposite of what was asked for.
/// <para>
/// Reads only. A write is never re-sent on our own initiative: a request that may have
/// been half-applied is for a person to look at, and its body has already been consumed.
/// One retry, and only when the wait asked for is short (<see cref="MaxWait"/>): a longer
/// one is not shortened to suit us but handed back unanswered, and the caller carries
/// the wait onward (<see cref="BcApiException.RetryAfter"/>) so the environment sweep
/// leaves that customer alone until it has passed. Microsoft documents no limits for the
/// admin center API, so there is nothing to pace against in advance; this is how we
/// find out.
/// </para>
/// </summary>
public sealed class BcThrottleHandler : DelegatingHandler
{
    internal static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _clock;
    private readonly ILogger<BcThrottleHandler> _logger;

    /// <summary>The wait itself, replaceable so a test does not sit through it.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public BcThrottleHandler(TimeProvider clock, ILogger<BcThrottleHandler> logger)
    {
        _clock = clock;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (request.Method != HttpMethod.Get || WaitFor(response) is not { } wait) return response;
        if (wait > MaxWait)
        {
            // Retrying sooner than asked would be ignoring the answer, and sitting out a
            // long wait here would hold a person's page or the whole sweep. Hand it back.
            _logger.LogWarning(
                "Business Central answered {Status} for {Host}{Path} and asked for {Seconds:N0}s; not trying again before then.",
                (int)response.StatusCode, request.RequestUri?.Host, request.RequestUri?.AbsolutePath, wait.TotalSeconds);
            return response;
        }

        _logger.LogWarning(
            "Business Central answered {Status} for {Host}{Path}; waiting {Seconds:N0}s before one more try.",
            (int)response.StatusCode, request.RequestUri?.Host, request.RequestUri?.AbsolutePath, wait.TotalSeconds);
        response.Dispose();

        await Delay(wait, ct).ConfigureAwait(false);

        // A request message cannot be sent twice; a GET has no body, so a copy is the whole of it.
        using var again = new HttpRequestMessage(HttpMethod.Get, request.RequestUri) { Version = request.Version };
        foreach (var header in request.Headers) again.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return await base.SendAsync(again, ct).ConfigureAwait(false);
    }

    /// <summary>How long Business Central asked us to wait, or null when the answer is not a request to slow down.</summary>
    internal TimeSpan? WaitFor(HttpResponseMessage response) => RequestedWait(response, _clock.GetUtcNow());

    /// <summary>
    /// How long a response asked us to wait before asking again: its <c>Retry-After</c>,
    /// as seconds or as a date, at least a second; <see cref="DefaultWait"/> for a 429 that
    /// names none. Null when the response is not a request to slow down at all - a 503
    /// without a wait is just down. Shared with the clients, which carry it on the
    /// <see cref="BcApiException"/> they throw.
    /// </summary>
    internal static TimeSpan? RequestedWait(HttpResponseMessage response, DateTimeOffset now)
    {
        var retryAfter = response.Headers.RetryAfter;
        var throttled = response.StatusCode == HttpStatusCode.TooManyRequests
            || (response.StatusCode == HttpStatusCode.ServiceUnavailable && retryAfter is not null);
        if (!throttled) return null;

        var asked = retryAfter?.Delta
            ?? (retryAfter?.Date is { } until ? until - now : (TimeSpan?)null)
            ?? DefaultWait;
        return asked < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : asked;
    }
}
