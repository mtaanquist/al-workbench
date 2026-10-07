using System.Net;
using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The workbench's one inbound route (issue #627), booted end to end so the real
/// pipeline is in the picture: anonymous, antiforgery-disabled, and refusing
/// everything whose HMAC does not match the deployment's stored webhook secret.
///
/// <para>The status codes matter as much as the behaviour. GitHub shows the
/// operator what came back per delivery, and
/// <c>UseStatusCodePagesWithReExecute</c> rewrites a bare 4xx on a POST into a
/// 400 - which would turn "your signature is wrong" into "your request is
/// malformed". So each refusal is asserted for its own status, not merely for
/// not-success.</para>
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class GitHubWebhookEndpointTests : IDisposable
{
    private const string Secret = "swordfish";

    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    private readonly GitHubWebhookBodyGate _gate = new(
        GitHubWebhookBodyGate.DefaultSlots,
        waitTimeout: TimeSpan.FromMilliseconds(300),
        readDeadline: TimeSpan.FromSeconds(3));

    public GitHubWebhookEndpointTests()
    {
        // The real worker would drain the queue as fast as the endpoint fills it,
        // so "was this delivery queued?" would be a race. These tests are about the
        // endpoint - what it accepts, what it refuses, and what it hands on - so
        // the drain is taken out and the channel left to be read by the test.
        _factory = new EndpointFactory(_db, services =>
        {
            var worker = services.FirstOrDefault(d =>
                d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(GitHubPullRequestBuildWorker));
            if (worker is not null) services.Remove(worker);

            // The large-body slots with timings a test can wait out, and one gate
            // per test so holding its slots cannot leak into another test (#1174).
            services.AddSingleton(_gate);
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, SlowBodyFilter>();
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    /// <param name="authorAssociation">
    /// GitHub's verdict on who the author is to the repository. Null leaves the
    /// property out of the payload entirely, which is how a delivery from an old
    /// GitHub Enterprise or a shape we have not seen would arrive - and is read
    /// as "not a member".
    /// </param>
    /// <param name="headOwner">
    /// Who owns the head repository. Defaults to the owner half of
    /// <paramref name="headRepository"/>, which is what GitHub sends; a test that
    /// passes something else is describing a member opening a pull request from
    /// somebody else's fork.
    /// </param>
    private static string PullRequestPayload(
        string action = "opened",
        long installationId = 42,
        int number = 7,
        string headSha = "abc1234",
        string headRef = "feature/vat",
        string headRepository = "cronus-dk/customer-app",
        bool headIsFork = false,
        string authorLogin = "erik",
        string? authorAssociation = "MEMBER",
        string? headOwner = null,
        string updatedAt = "2026-10-07T09:00:00Z")
    {
        var owner = headOwner ?? headRepository.Split('/')[0];
        var association = authorAssociation is null
            ? string.Empty
            : $"""
                "author_association": "{authorAssociation}",
            """;
        return $$"""
        {
          "action": "{{action}}",
          "installation": { "id": {{installationId}} },
          "repository": {
            "full_name": "cronus-dk/customer-app",
            "clone_url": "https://github.com/cronus-dk/customer-app.git"
          },
          "pull_request": {
            "number": {{number}},
            "user": { "login": "{{authorLogin}}" },
            {{association}}
            "head": {
              "sha": "{{headSha}}",
              "ref": "{{headRef}}",
              "repo": {
                "full_name": "{{headRepository}}",
                "fork": {{(headIsFork ? "true" : "false")}},
                "owner": { "login": "{{owner}}" }
              }
            },
            "base": { "ref": "main" },
            "updated_at": "{{updatedAt}}"
          }
        }
        """;
    }

    private async Task StoreSecretAsync(string secret = Secret)
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsService>();
        await settings.SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: null,
            ClientSecret: null, ClearClientSecret: false,
            PrivateKeyPem: null, ClearPrivateKey: false,
            WebhookSecret: secret, ClearWebhookSecret: false));
    }

    private static HttpRequestMessage Delivery(string json, string? secret, string eventName = "pull_request")
    {
        var body = Encoding.UTF8.GetBytes(json);
        var request = new HttpRequestMessage(HttpMethod.Post, GitHubWebhookEndpoints.WebhookPath)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", eventName);
        request.Headers.Add("X-GitHub-Delivery", "11111111-2222-3333-4444-555555555555");
        if (secret is not null)
        {
            request.Headers.Add("X-Hub-Signature-256", Signature(secret, body));
        }
        return request;
    }

    /// <summary>
    /// GitHub's own header, computed here rather than through the production
    /// helper - a test that signs with the code under test would pass whatever
    /// that code did.
    /// </summary>
    private static string Signature(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    [Fact]
    public async Task A_delivery_with_a_valid_signature_is_accepted_and_queued()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(PullRequestPayload(), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync()).Should().NotBeEmpty(
            "every response writes a body so the status-pages middleware does not rewrite it");

        var queue = _factory.Services.GetRequiredService<GitHubWebhookQueue>();
        queue.Reader.TryRead(out var read).Should().BeTrue();
        var job = read.Should().BeOfType<GitHubPullRequestJob>().Subject;
        job.InstallationId.Should().Be(42);
        job.RepositoryFullName.Should().Be("cronus-dk/customer-app");
        job.PullRequestNumber.Should().Be(7);
        job.HeadSha.Should().Be("abc1234");
        job.HeadRef.Should().Be("feature/vat");
        job.BaseRef.Should().Be("main");
        job.DeliveryId.Should().Be("11111111-2222-3333-4444-555555555555");
    }

    [Fact]
    public async Task A_delivery_signed_with_the_wrong_secret_is_refused_as_401()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(PullRequestPayload(), "not-the-secret"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "GitHub's delivery log has to show what was wrong, not a generic 400");

        var queue = _factory.Services.GetRequiredService<GitHubWebhookQueue>();
        queue.Reader.TryRead(out _).Should().BeFalse("an unverified delivery must never reach the queue");
    }

    [Fact]
    public async Task A_delivery_with_no_signature_at_all_is_refused_as_401()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(PullRequestPayload(), secret: null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Without_a_stored_secret_every_delivery_is_refused()
    {
        // Nothing is configured, so nothing can be verified - and an unverifiable
        // delivery is refused rather than trusted.
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(PullRequestPayload(), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_ping_is_answered_so_the_operator_knows_the_address_and_secret_are_right()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery("{\"zen\":\"Keep it logically awesome.\"}", Secret, eventName: "ping"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("pong");
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("labeled")]
    [InlineData("assigned")]
    public async Task A_pull_request_action_that_is_not_a_new_head_is_ignored(string action)
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(PullRequestPayload(action: action), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("opened")]
    [InlineData("synchronize")]
    [InlineData("reopened")]
    public async Task The_three_actions_that_mean_a_new_head_are_queued(string action)
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(PullRequestPayload(action: action), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeTrue();
    }

    [Fact]
    public async Task An_event_we_do_not_act_on_is_answered_without_content()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery("{}", Secret, eventName: "issues"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // --- Push and merged pull requests (#963) -------------------------------

    [Fact]
    public async Task A_branch_push_is_accepted_and_queued_as_a_push()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(
            GitHubWebhookPayloads.Push(branch: "main", commitCount: 2), Secret, eventName: "push"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var queue = _factory.Services.GetRequiredService<GitHubWebhookQueue>();
        queue.Reader.TryRead(out var read).Should().BeTrue();
        var push = read.Should().BeOfType<GitHubPushJob>().Subject;
        push.InstallationId.Should().Be(42);
        push.Branch.Should().Be("main");
        push.HeadSha.Should().Be(GitHubWebhookPayloads.After);
        push.DefaultBranch.Should().Be("main");
        push.PusherLogin.Should().Be("erik");
        push.CommitCount.Should().Be(2);
        push.Forced.Should().BeFalse();
        push.Deleted.Should().BeFalse();
    }

    [Fact]
    public async Task A_push_over_a_megabyte_is_still_queued()
    {
        // Twenty commits each touching a few thousand files, as a large import or a
        // symbol refresh does. Refusing it was a build that never happened (#1126).
        await StoreSecretAsync();
        using var client = _factory.CreateClient();
        var payload = GitHubWebhookPayloads.Push(commitCount: 20, filesPerCommit: 3000);
        Encoding.UTF8.GetByteCount(payload).Should().BeGreaterThan(GitHubWebhookEndpoints.MaxRequestBodyBytes);

        using var response = await client.SendAsync(Delivery(payload, Secret, eventName: "push"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var queue = _factory.Services.GetRequiredService<GitHubWebhookQueue>();
        queue.Reader.TryRead(out var read).Should().BeTrue();
        read.Should().BeOfType<GitHubPushJob>().Which.CommitCount.Should().Be(20);
    }

    [Fact]
    public async Task Only_a_signed_push_may_be_larger_than_a_megabyte()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();
        var payload = GitHubWebhookPayloads.Push(commitCount: 20, filesPerCommit: 3000);

        using var unsigned = await client.SendAsync(Delivery(payload, secret: null, eventName: "push"));
        using var pullRequest = await client.SendAsync(Delivery(payload, Secret, eventName: "pull_request"));

        unsigned.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        pullRequest.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_large_push_with_a_forged_signature_is_refused_as_401()
    {
        // The attack the larger cap opens: it is read, then refused like any other forgery.
        await StoreSecretAsync();
        using var client = _factory.CreateClient();
        var payload = GitHubWebhookPayloads.Push(commitCount: 20, filesPerCommit: 3000);

        using var response = await client.SendAsync(Delivery(payload, "not-the-secret", eventName: "push"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>().Reader.TryRead(out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("x")]
    [InlineData("sha256=abc")]
    [InlineData("sha256=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task A_push_whose_signature_is_not_even_shaped_right_keeps_the_megabyte_cap(string header)
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();
        using var request = Delivery(GitHubWebhookPayloads.Push(commitCount: 20, filesPerCommit: 3000), secret: null, eventName: "push");
        request.Headers.Add("X-Hub-Signature-256", header);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    // --- The large-body slots (#1174) ----------------------------------------

    /// <summary>Takes every large-body slot, as four forged slow pushes would.</summary>
    private async Task HoldEverySlotAsync()
    {
        // One address holds at most one slot, so four slots take four addresses.
        for (var i = 0; i < GitHubWebhookBodyGate.DefaultSlots; i++)
        {
            (await _gate.TryEnterAsync($"203.0.113.{i}", TestContext.Current.CancellationToken)).Should().BeTrue();
        }
    }

    private void ReleaseEverySlot()
    {
        for (var i = 0; i < GitHubWebhookBodyGate.DefaultSlots; i++) _gate.Release($"203.0.113.{i}");
    }

    [Fact]
    public async Task One_address_holds_at_most_one_large_body_slot()
    {
        var ct = TestContext.Current.CancellationToken;

        (await _gate.TryEnterAsync("203.0.113.7", ct)).Should().BeTrue();
        (await _gate.TryEnterAsync("203.0.113.7", ct)).Should().BeFalse(
            "a second large body from an address already reading one would let one sender hold every slot");
        (await _gate.TryEnterAsync("203.0.113.8", ct)).Should().BeTrue();
        _gate.Available.Should().Be(GitHubWebhookBodyGate.DefaultSlots - 2);

        _gate.Release("203.0.113.7");
        (await _gate.TryEnterAsync("203.0.113.7", ct)).Should().BeTrue("the address gave its slot back");

        _gate.Release("203.0.113.7");
        _gate.Release("203.0.113.8");
        _gate.Available.Should().Be(GitHubWebhookBodyGate.DefaultSlots);
    }

    [Fact]
    public async Task A_second_large_push_from_an_address_already_reading_one_is_answered_503()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        // The first holds a slot by trickling; the second comes from the same address.
        var slow = client.SendAsync(TricklingLargePush(), ct);
        while (_gate.Available == GitHubWebhookBodyGate.DefaultSlots && !slow.IsCompleted)
        {
            await Task.Delay(20, ct);
        }
        using var second = await client.SendAsync(Delivery(
            GitHubWebhookPayloads.Push(commitCount: 20, filesPerCommit: 3000), Secret, eventName: "push"), ct);
        using var first = await slow;

        second.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        first.StatusCode.Should().Be(HttpStatusCode.RequestTimeout);
        _gate.Available.Should().Be(GitHubWebhookBodyGate.DefaultSlots);
    }

    [Fact]
    public async Task A_push_that_declares_no_length_takes_the_large_body_path()
    {
        // Without a declared length nothing says the body is small, so it waits
        // for a slot like a large one; with every slot held that is a 503.
        await StoreSecretAsync();
        using var client = _factory.CreateClient();
        var body = Encoding.UTF8.GetBytes(GitHubWebhookPayloads.Push(commitCount: 2));
        using var request = new HttpRequestMessage(HttpMethod.Post, GitHubWebhookEndpoints.WebhookPath)
        {
            Content = new StreamContent(new UnseekableStream(body)),
        };
        request.Content.Headers.ContentLength.Should().BeNull();
        request.Headers.Add("X-GitHub-Event", "push");
        request.Headers.Add("X-Hub-Signature-256", Signature(Secret, body));
        await HoldEverySlotAsync();
        try
        {
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            ReleaseEverySlot();
        }
    }

    [Fact]
    public async Task A_body_the_server_drops_for_arriving_too_slowly_is_answered_408()
    {
        // Kestrel's minimum data rate surfaces as its own exception, not as our
        // deadline; it is answered the same way rather than escaping as a 500.
        await StoreSecretAsync();
        using var client = _factory.CreateClient();
        using var request = Delivery(GitHubWebhookPayloads.Push(commitCount: 2), Secret, eventName: "push");
        request.Headers.Add(SlowBodyHeader, "1");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.RequestTimeout);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("too slowly");
    }

    private static HttpRequestMessage TricklingLargePush()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GitHubWebhookEndpoints.WebhookPath)
        {
            Content = new StreamContent(new StallingStream(Encoding.UTF8.GetBytes("{\"ref\":"))),
        };
        request.Content.Headers.ContentLength = 2 * GitHubWebhookEndpoints.MaxRequestBodyBytes;
        request.Headers.Add("X-GitHub-Event", "push");
        request.Headers.Add("X-Hub-Signature-256", "sha256=" + new string('a', 64));
        return request;
    }

    /// <summary>A request header that makes <see cref="SlowBodyFilter"/> stand in for Kestrel's data-rate check.</summary>
    private const string SlowBodyHeader = "X-Test-Slow-Body";

    /// <summary>
    /// The in-memory test server has no minimum data rate, so this puts in front of
    /// the pipeline the exception Kestrel raises when a body arrives too slowly.
    /// </summary>
    private sealed class SlowBodyFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
            Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(nextMiddleware => ctx =>
            {
                if (ctx.Request.Headers.ContainsKey(SlowBodyHeader))
                {
                    ctx.Request.Body = new ThrowingStream();
                }
                return nextMiddleware(ctx);
            });
            next(app);
        };
    }

    private sealed class ThrowingStream : UnseekableStream
    {
        public ThrowingStream() : base([]) { }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new Microsoft.AspNetCore.Http.BadHttpRequestException(
                "Reading the request body timed out due to data arriving too slowly.", 408);
    }

    /// <summary>A body with no length the client can work out, so it is sent without a Content-Length.</summary>
    private class UnseekableStream(byte[] content) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, content.Length - _position);
            Array.Copy(content, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = Math.Min(buffer.Length, content.Length - _position);
            content.AsSpan(_position, n).CopyTo(buffer.Span);
            _position += n;
            return ValueTask.FromResult(n);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    [Fact]
    public async Task An_ordinary_push_is_queued_even_while_every_large_body_slot_is_held()
    {
        // The attack: a stranger with a well-formed header holds the slots. A push
        // under a megabyte - nearly all of them - must not queue behind it.
        await StoreSecretAsync();
        using var client = _factory.CreateClient();
        await HoldEverySlotAsync();
        try
        {
            using var response = await client.SendAsync(Delivery(
                GitHubWebhookPayloads.Push(commitCount: 2), Secret, eventName: "push"));

            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }
        finally
        {
            ReleaseEverySlot();
        }
    }

    [Fact]
    public async Task A_large_push_that_cannot_get_a_slot_is_answered_with_a_retryable_503()
    {
        // Waiting without a limit would outlast GitHub's ten seconds and lose the
        // delivery; a 503 is one the recovery scheduler asks for again.
        await StoreSecretAsync();
        using var client = _factory.CreateClient();
        var payload = GitHubWebhookPayloads.Push(commitCount: 20, filesPerCommit: 3000);
        await HoldEverySlotAsync();
        try
        {
            using var response = await client.SendAsync(Delivery(payload, Secret, eventName: "push"));

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await response.Content.ReadAsStringAsync()).Should().Contain("requested again");
            _factory.Services.GetRequiredService<GitHubWebhookQueue>().Reader.TryRead(out _).Should().BeFalse();
        }
        finally
        {
            ReleaseEverySlot();
        }
    }

    [Fact]
    public async Task A_large_push_that_trickles_is_cut_off_at_the_deadline_and_gives_its_slot_back()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();
        using var request = TricklingLargePush();

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.RequestTimeout);
        _gate.Available.Should().Be(GitHubWebhookBodyGate.DefaultSlots);
    }

    [Fact]
    public async Task A_large_push_that_is_read_gives_its_slot_back()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();
        var payload = GitHubWebhookPayloads.Push(commitCount: 20, filesPerCommit: 3000);

        using var accepted = await client.SendAsync(Delivery(payload, Secret, eventName: "push"));
        using var forged = await client.SendAsync(Delivery(payload, "not-the-secret", eventName: "push"));

        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        forged.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _gate.Available.Should().Be(GitHubWebhookBodyGate.DefaultSlots);
    }

    [Fact]
    public async Task Reading_a_body_does_not_allocate_from_the_declared_length()
    {
        // A sender may declare 25 MB and send ten bytes. MemoryStream completes
        // every read synchronously, so the whole read runs on this thread and the
        // thread's allocation counter sees all of it.
        var sent = Encoding.UTF8.GetBytes("{\"a\":\"b\"}");
        using var stream = new MemoryStream(sent);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var body = await GitHubWebhookEndpoints.ReadBodyAsync(
            stream, GitHubWebhookEndpoints.MaxPushBodyBytes, GitHubWebhookEndpoints.MaxPushBodyBytes,
            TestContext.Current.CancellationToken);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        body.Should().Equal(sent);
        allocated.Should().BeLessThan(1_000_000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reading_a_body_returns_every_byte_past_the_first_buffer(bool declared)
    {
        var sent = new byte[300_000];
        Random.Shared.NextBytes(sent);
        using var stream = new MemoryStream(sent);

        var body = await GitHubWebhookEndpoints.ReadBodyAsync(
            stream, declared ? sent.Length : null, GitHubWebhookEndpoints.MaxRequestBodyBytes,
            TestContext.Current.CancellationToken);

        body.Should().Equal(sent);
    }

    [Fact]
    public async Task Reading_a_body_with_no_declared_length_refuses_one_byte_past_the_cap()
    {
        const int Cap = 100_000;
        using var exactly = new MemoryStream(new byte[Cap]);
        using var over = new MemoryStream(new byte[Cap + 1]);
        var ct = TestContext.Current.CancellationToken;

        (await GitHubWebhookEndpoints.ReadBodyAsync(exactly, null, Cap, ct)).Should().HaveCount(Cap);
        (await GitHubWebhookEndpoints.ReadBodyAsync(over, null, Cap, ct)).Should().BeNull();
        (await GitHubWebhookEndpoints.ReadBodyAsync(new MemoryStream(), Cap + 1, Cap, ct)).Should().BeNull();
    }

    [Fact]
    public async Task Reading_a_body_that_declares_exactly_the_cap_returns_all_of_it()
    {
        const int Cap = 100_000;
        var sent = new byte[Cap];
        Random.Shared.NextBytes(sent);
        using var stream = new MemoryStream(sent);

        var body = await GitHubWebhookEndpoints.ReadBodyAsync(stream, Cap, Cap, TestContext.Current.CancellationToken);

        body.Should().Equal(sent);
    }

    /// <summary>
    /// A body that sends its first bytes and then nothing more, as a forged push
    /// trickled at the server's minimum rate looks from inside one read.
    /// </summary>
    private sealed class StallingStream(byte[] first) : Stream
    {
        private bool _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                first.CopyTo(buffer);
                return first.Length;
            }
            // Stall well past the deadline, then end, so a client that waits for
            // its upload to finish still returns.
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    [Fact]
    public async Task A_tag_push_is_answered_and_dropped()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(
            GitHubWebhookPayloads.Push(reference: "refs/tags/v25.0.1"), Secret, eventName: "push"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>().Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_push_signed_with_the_wrong_secret_is_refused_like_any_other_delivery()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(GitHubWebhookPayloads.Push(), "not-the-secret", eventName: "push"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>().Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_merged_pull_request_is_queued_as_a_merge_and_never_as_a_build()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(GitHubWebhookPayloads.MergedPullRequest(number: 12), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var queue = _factory.Services.GetRequiredService<GitHubWebhookQueue>();
        queue.Reader.TryRead(out var read).Should().BeTrue();
        var merged = read.Should().BeOfType<GitHubMergedPullRequestJob>().Subject;
        merged.Number.Should().Be(12);
        merged.BaseBranch.Should().Be("main");
        merged.Title.Should().Be("Post VAT to the right account");
        merged.MergeSha.Should().Be(GitHubWebhookPayloads.MergeSha);
        merged.AuthorLogin.Should().Be("erik");
        queue.Reader.TryRead(out _).Should().BeFalse("a merged pull request is not built");
    }

    [Fact]
    public async Task A_pull_request_closed_without_merging_is_not_recorded()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(GitHubWebhookPayloads.MergedPullRequest(merged: false), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>().Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_pull_request_payload_missing_the_installation_is_not_queued()
    {
        // Without an installation there is no organisation to act for, so there
        // is nothing to do - and nothing to retry, hence not an error status.
        await StoreSecretAsync();
        using var client = _factory.CreateClient();
        const string NoInstallation = """
            {"action":"opened","repository":{"full_name":"a/b","clone_url":"https://github.com/a/b.git"},
             "pull_request":{"number":1,"head":{"sha":"abc","ref":"x"},"base":{"ref":"main"}}}
            """;

        using var response = await client.SendAsync(Delivery(NoInstallation, Secret));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_not_queued()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery("this is not json", Secret));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeFalse();
    }

    // --- Fork pull requests, and what may reach git (#627 review) ----------

    [Fact]
    public async Task A_pull_request_from_a_fork_is_not_built()
    {
        // Anybody on GitHub can fork a public repository and open a pull request
        // against it. Building one would clone and compile a stranger's code on
        // the customer's own installation token, so it is answered and dropped.
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(
            PullRequestPayload(
                headRepository: "stranger/customer-app", headIsFork: true,
                authorLogin: "stranger", authorAssociation: "CONTRIBUTOR"), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_head_repository_with_another_name_is_not_built_even_when_it_is_not_flagged_as_a_fork()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(
            PullRequestPayload(
                headRepository: "stranger/customer-app", headIsFork: false,
                authorLogin: "stranger", authorAssociation: "NONE"), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_branch_pull_request_from_the_repository_itself_is_built()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(
            PullRequestPayload(headRepository: "CRONUS-dk/Customer-App"), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "GitHub repository names are case-insensitive, so a differently-cased head is the same repository");
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("MEMBER")]
    [InlineData("OWNER")]
    [InlineData("member")]
    public async Task A_fork_pull_request_opened_by_a_member_from_their_own_fork_is_queued(string association)
    {
        // The one fork that is built: GitHub calls the author a member or an
        // owner of the organisation, and the fork is that person's own. The job
        // is marked so the worker asks GitHub the membership question again
        // before anything is cloned.
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(
            PullRequestPayload(
                headRepository: "erik/customer-app", headIsFork: true,
                authorLogin: "erik", authorAssociation: association), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var queue = _factory.Services.GetRequiredService<GitHubWebhookQueue>();
        queue.Reader.TryRead(out var read).Should().BeTrue();
        var job = read.Should().BeOfType<GitHubPullRequestJob>().Subject;
        job.IsMemberFork.Should().BeTrue();
        job.AuthorLogin.Should().Be("erik");
    }

    [Fact]
    public async Task A_member_opening_a_pull_request_from_somebody_elses_fork_is_not_built()
    {
        // A fork's owner can give push rights to anyone, so code arriving from a
        // third party's fork is a stranger's however good the author's standing
        // in the organisation is.
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(
            PullRequestPayload(
                headRepository: "somebody-else/customer-app", headIsFork: true,
                authorLogin: "erik", authorAssociation: "MEMBER"), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_fork_pull_request_with_no_author_association_at_all_is_not_built()
    {
        // A missing verdict is not a favourable one.
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(
            PullRequestPayload(
                headRepository: "erik/customer-app", headIsFork: true,
                authorLogin: "erik", authorAssociation: null), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_pull_request_from_the_repository_itself_is_never_marked_as_a_fork()
    {
        // Nothing about the same-repository path changed, and the marker is what
        // the worker keys the extra GitHub call off - so it has to stay false
        // even for a member's own branch.
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(PullRequestPayload(), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var queue = _factory.Services.GetRequiredService<GitHubWebhookQueue>();
        queue.Reader.TryRead(out var read).Should().BeTrue();
        var job = read.Should().BeOfType<GitHubPullRequestJob>().Subject;
        job.IsMemberFork.Should().BeFalse();
    }

    [Theory]
    [InlineData("--upload-pack=touch /tmp/pwned")]
    [InlineData("not-hex-at-all")]
    [InlineData("abc")]
    public async Task A_head_sha_that_is_not_a_git_object_name_is_refused(string headSha)
    {
        // The SHA goes on a git command line. Anything that is not hex of the
        // right length never gets there.
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(PullRequestPayload(headSha: headSha), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("--force")]
    [InlineData("feature/vat; rm -rf /")]
    [InlineData("feature\\vat")]
    public async Task A_head_branch_name_git_could_not_be_asked_for_is_refused(string headRef)
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(PullRequestPayload(headRef: headRef), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_full_queue_is_answered_with_a_retryable_503_and_nothing_is_announced()
    {
        // The request thread is GitHub's. Waiting on a full channel would hold that
        // request open behind a build backlog; the refusal is asked for again later
        // by GitHubWebhookRecoveryScheduler (#1121).
        await StoreSecretAsync();
        using var client = _factory.CreateClient();
        var queue = _factory.Services.GetRequiredService<GitHubWebhookQueue>();

        // Fill the channel: the endpoint's own capacity, written directly.
        var filler = new GitHubPullRequestJob(1, "a/b", "https://github.com/a/b.git", 1, "abc1234", "x", "main", "d");
        while (queue.TryEnqueue(filler)) { }

        using var response = await client.SendAsync(Delivery(PullRequestPayload(headSha: "deadbee"), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("requested again");
        queue.IsLatest("42:cronus-dk/customer-app:7", "something-else").Should().BeTrue(
            "a delivery that was never queued must not cancel the build that is running");
    }

    [Fact]
    public async Task A_delivery_for_an_older_head_arriving_late_does_not_replace_the_newer_one()
    {
        // GitHub does not promise delivery order; the pull request's own updated_at
        // says which head is newer (#1120).
        await StoreSecretAsync();
        using var client = _factory.CreateClient();
        var queue = _factory.Services.GetRequiredService<GitHubWebhookQueue>();

        using var newer = await client.SendAsync(Delivery(
            PullRequestPayload(action: "synchronize", headSha: "bbbbbbb", updatedAt: "2026-10-07T09:05:00Z"), Secret));
        using var older = await client.SendAsync(Delivery(
            PullRequestPayload(action: "synchronize", headSha: "aaaaaaa", updatedAt: "2026-10-07T09:00:00Z"), Secret));

        newer.StatusCode.Should().Be(HttpStatusCode.Accepted);
        older.StatusCode.Should().Be(HttpStatusCode.Accepted);
        queue.IsLatest("42:cronus-dk/customer-app:7", "bbbbbbb").Should().BeTrue();
        queue.IsLatest("42:cronus-dk/customer-app:7", "aaaaaaa").Should().BeFalse(
            "the older head is skipped when the worker reaches it");
    }

    [Fact]
    public void The_route_declares_a_body_cap()
    {
        // The in-memory test server does not implement the body-size feature, so
        // the cap cannot be exercised by sending a large body here - what can be
        // pinned is that the route asks for one, which is what makes an oversized
        // delivery a socket-level drop rather than a megabyte in memory.
        var endpoints = _factory.Services
            .GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints;
        var webhook = endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == GitHubWebhookEndpoints.WebhookPath);

        var limit = webhook.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>();
        limit.Should().NotBeNull();
        limit!.MaxRequestBodySize.Should().Be(GitHubWebhookEndpoints.MaxPushBodyBytes,
            "a push may be as large as GitHub sends; every other event is held to a megabyte as it is read");
    }

    [Fact]
    public void The_signature_check_rejects_anything_that_is_not_a_sha256_hex_digest()
    {
        var body = Encoding.UTF8.GetBytes("{}");

        GitHubWebhookEndpoints.SignatureMatches(Secret, body, null).Should().BeFalse();
        GitHubWebhookEndpoints.SignatureMatches(Secret, body, string.Empty).Should().BeFalse();
        GitHubWebhookEndpoints.SignatureMatches(Secret, body, "sha1=deadbeef").Should().BeFalse();
        GitHubWebhookEndpoints.SignatureMatches(Secret, body, "sha256=not-hex").Should().BeFalse();
        GitHubWebhookEndpoints.SignatureMatches(Secret, body, Signature(Secret, body)).Should().BeTrue();
        GitHubWebhookEndpoints.SignatureMatches("other", body, Signature(Secret, body)).Should().BeFalse();
    }
}
