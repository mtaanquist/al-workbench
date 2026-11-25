using System.Net;
using System.Net.Http.Headers;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Auth;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Doing what Business Central asks when it says slow down: a throttled read waits the
/// time it was given and is tried once more; a write is never re-sent for it.
/// </summary>
public sealed class BcThrottleHandlerTests
{
    private sealed class Scripted : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _answers;
        public List<HttpRequestMessage> Seen { get; } = new();
        public Scripted(params HttpResponseMessage[] answers) => _answers = new(answers);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(_answers.Dequeue());
        }
    }

    private static HttpResponseMessage Throttled(TimeSpan? retryAfter = null, HttpStatusCode status = HttpStatusCode.TooManyRequests)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is { } wait) response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        return response;
    }

    /// <summary>The fixed "now" every test runs at, so an HTTP-date Retry-After has a known distance.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 2, 0, 0, TimeSpan.Zero);

    private static HttpResponseMessage ThrottledUntil(DateTimeOffset until, HttpStatusCode status = HttpStatusCode.TooManyRequests)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(until);
        return response;
    }

    private static (HttpClient Client, Scripted Inner, List<TimeSpan> Waits) Build(params HttpResponseMessage[] answers)
    {
        var inner = new Scripted(answers);
        var waits = new List<TimeSpan>();
        var handler = new BcThrottleHandler(new FakeTimeProvider(Now), NullLogger<BcThrottleHandler>.Instance)
        {
            InnerHandler = inner,
            Delay = (wait, _) => { waits.Add(wait); return Task.CompletedTask; },
        };
        return (new HttpClient(handler), inner, waits);
    }

    private static HttpRequestMessage Get()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.businesscentral.dynamics.com/admin/v2.29/applications/environments");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");
        return request;
    }

    [Fact]
    public async Task A_throttled_read_waits_what_it_was_told_and_is_tried_once_more_with_its_token()
    {
        var (client, inner, waits) = Build(Throttled(TimeSpan.FromSeconds(12)), new HttpResponseMessage(HttpStatusCode.OK));

        var response = await client.SendAsync(Get());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        waits.Should().Equal(TimeSpan.FromSeconds(12));
        inner.Seen.Should().HaveCount(2);
        inner.Seen[1].Headers.Authorization!.Parameter.Should().Be("token");
    }

    [Fact]
    public async Task Still_throttled_after_one_more_try_is_handed_back_as_it_is()
    {
        var (client, inner, waits) = Build(Throttled(), Throttled());

        var response = await client.SendAsync(Get());

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "one retry, then it is an ordinary failed read");
        inner.Seen.Should().HaveCount(2);
        waits.Should().Equal(BcThrottleHandler.DefaultWait);
    }

    /// <summary>
    /// A long wait is not shortened to suit us: trying again after a minute when asked
    /// for thirty would be ignoring the answer. It is handed back for the caller to carry.
    /// </summary>
    [Fact]
    public async Task A_wait_longer_than_a_minute_is_not_cut_short_but_handed_back()
    {
        var (client, inner, waits) = Build(Throttled(TimeSpan.FromMinutes(30)), new HttpResponseMessage(HttpStatusCode.OK));

        var response = await client.SendAsync(Get());

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        inner.Seen.Should().ContainSingle("nothing is sent before the time Business Central named");
        waits.Should().BeEmpty("one customer's long wait must not hold up the caller either");
    }

    [Fact]
    public void The_wait_asked_for_is_reported_uncapped_for_the_clients_to_carry()
    {
        BcThrottleHandler.RequestedWait(Throttled(TimeSpan.FromMinutes(30)), Now).Should().Be(TimeSpan.FromMinutes(30));
        BcThrottleHandler.RequestedWait(Throttled(), Now).Should().Be(BcThrottleHandler.DefaultWait);
        BcThrottleHandler.RequestedWait(ThrottledUntil(Now.AddMinutes(2)), Now).Should().Be(TimeSpan.FromMinutes(2));
        BcThrottleHandler.RequestedWait(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), Now).Should().BeNull();
        BcThrottleHandler.RequestedWait(new HttpResponseMessage(HttpStatusCode.Forbidden), Now).Should().BeNull();
    }

    [Fact]
    public async Task A_write_is_never_sent_again_on_our_own_initiative()
    {
        var (client, inner, waits) = Build(Throttled(TimeSpan.FromSeconds(1)));

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://api.businesscentral.dynamics.com/x")
        {
            Content = new StringContent("{}"),
        });

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        inner.Seen.Should().HaveCount(1);
        waits.Should().BeEmpty();
    }

    [Fact]
    public async Task A_503_that_names_a_wait_is_waited_out_and_tried_once_more()
    {
        var (client, inner, waits) = Build(
            Throttled(TimeSpan.FromSeconds(7), HttpStatusCode.ServiceUnavailable), new HttpResponseMessage(HttpStatusCode.OK));

        var response = await client.SendAsync(Get());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        waits.Should().Equal(TimeSpan.FromSeconds(7));
        inner.Seen.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_retry_after_given_as_a_date_waits_until_that_time()
    {
        var (client, inner, waits) = Build(ThrottledUntil(Now.AddSeconds(20)), new HttpResponseMessage(HttpStatusCode.OK));

        await client.SendAsync(Get());

        waits.Should().Equal(TimeSpan.FromSeconds(20));
        inner.Seen.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_retry_after_date_already_past_still_waits_a_second()
    {
        var (client, _, waits) = Build(
            ThrottledUntil(Now.AddSeconds(-30), HttpStatusCode.ServiceUnavailable), new HttpResponseMessage(HttpStatusCode.OK));

        await client.SendAsync(Get());

        waits.Should().Equal(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task An_ordinary_failure_is_not_mistaken_for_being_asked_to_slow_down()
    {
        var (client, inner, waits) = Build(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var response = await client.SendAsync(Get());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "a 503 that names no wait is just down");
        inner.Seen.Should().HaveCount(1);
        waits.Should().BeEmpty();
    }
}
