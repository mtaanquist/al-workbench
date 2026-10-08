using System.Diagnostics;
using System.Net;
using System.Net.Http;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Validation tests for <see cref="DvdDownloadService"/>. These cover the
/// user-correctable rejections that fire before any network call — blank URL,
/// non-https scheme, and a host that isn't on the SiteAdmin allow-list. The
/// happy download path needs a real server and the SSRF behaviour is covered
/// by SsrfGuard's own tests, so neither is re-exercised here.
/// </summary>
public sealed class DvdDownloadServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Rejects_blank_url()
    {
        var svc = NewService();
        var act = async () => await svc.DownloadToTempAsync("   ");
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("DvdUrl");
    }

    [Fact]
    public async Task Rejects_non_https_url()
    {
        var svc = NewService();
        await SetAllowlistAsync("download.microsoft.com");
        var act = async () => await svc.DownloadToTempAsync("http://download.microsoft.com/x.zip");
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("DvdUrl");
    }

    [Fact]
    public async Task Rejects_host_not_on_allowlist()
    {
        var svc = NewService();
        // No allow-list configured → no host permitted.
        var act = async () => await svc.DownloadToTempAsync("https://evil.example.com/x.zip");
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("DvdUrl");
    }

    private async Task SetAllowlistAsync(string hosts)
    {
        await using var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var settings = new SystemSettingsService(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
        await settings.SaveAsync(new SystemSettingsInput(
            SmtpHost: null, SmtpPort: null, SmtpUser: null,
            SmtpPassword: null, ClearSmtpPassword: false,
            SmtpFrom: null, SmtpFromName: null, SmtpUseStartTls: null, BannerText: null,
            BackupScheduleEnabled: true,
            BackupScheduleTimeUtc: new TimeOnly(2, 0),
            BackupRetentionCount: 14,
            PerTenantBackupRetentionCount: 30,
            DefaultStorageQuotaMb: null,
            IndexSizeMultiplier: 0.5m,
            McpEnabled: false,
            SignupEmailDomainAllowlist: null,
            ReleaseDownloadDomainAllowlist: hosts, DisabledTools: System.Array.Empty<ALDevToolbox.Domain.Tools.ToolKey>(),
            BuildConcurrency: null));
    }

    private DvdDownloadService NewService()
    {
        var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var settings = new SystemSettingsService(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
        return new DvdDownloadService(new ThrowingHttpClientFactory(), settings, NullLogger<DvdDownloadService>.Instance);
    }

    // The validation tests never reach the network; if CreateClient is called
    // the test has a bug, so fail loudly rather than silently hitting a server.
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Validation should reject before any HTTP call.");
    }
}

/// <summary>
/// Tests for the pure stream-copy helper — kept in their own class so they
/// don't pay the TestDb / Testcontainers fixture cost the validation tests
/// above need.
/// </summary>
public sealed class DvdDownloadServiceCopyTests
{
    [Fact]
    public async Task Copy_throws_TimeoutException_when_the_source_stalls()
    {
        // A CDN that returns headers + a few bytes and then stops sending
        // used to hang the worker forever — HttpClient.Timeout's
        // ResponseHeadersRead semantics don't reliably cover the body. The
        // per-read idle window should abort within the configured timeout.
        using var src = new StallingStream(new byte[] { 1, 2, 3, 4 });
        using var dst = new MemoryStream();
        var sw = Stopwatch.StartNew();

        var act = async () => await DvdDownloadService.CopyWithCapAsync(
            src, dst, TimeSpan.FromMilliseconds(200), startingBytesWritten: 0, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        // Bound generously — CancelAfter is wall-clock plus CI noise.
        // The point of the assertion is: not 20 minutes.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        // The initial bytes still made it through before the stall.
        dst.ToArray().Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task Copy_respects_caller_cancellation_over_idle_timeout()
    {
        // Worker shutdown cancels the caller's token. That must propagate
        // as OperationCanceledException, NOT get repackaged as a
        // TimeoutException — otherwise clean shutdown would log a
        // misleading "stalled" warning every time.
        using var src = new StallingStream(Array.Empty<byte>());
        using var dst = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = async () => await DvdDownloadService.CopyWithCapAsync(
            src, dst, TimeSpan.FromSeconds(30), startingBytesWritten: 0, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Copy_returns_cumulative_bytes_including_the_starting_offset()
    {
        // After a resume, the cap check has to look at *cumulative* bytes,
        // not just this attempt's slice. Verify the returned total includes
        // the starting baseline so the caller can keep using it as the next
        // Range-resume offset.
        using var src = new MemoryStream(new byte[] { 5, 6, 7 });
        using var dst = new MemoryStream();

        var total = await DvdDownloadService.CopyWithCapAsync(
            src, dst, TimeSpan.FromSeconds(10), startingBytesWritten: 100, CancellationToken.None);

        total.Should().Be(103);
        // Only the new bytes get written to dst — the caller owns the
        // dest stream's existing content (the 100 bytes from earlier
        // attempts already on disk).
        dst.ToArray().Should().Equal(5, 6, 7);
    }

    /// <summary>
    /// Returns the initial chunk on the first ReadAsync, then stalls
    /// indefinitely until the caller's CancellationToken fires — models a
    /// CDN edge that opens the body and stops sending bytes.
    /// </summary>
    private sealed class StallingStream : Stream
    {
        private readonly byte[] _initialChunk;
        private int _pos;

        public StallingStream(byte[] initialChunk) => _initialChunk = initialChunk;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < _initialChunk.Length)
            {
                var toCopy = Math.Min(buffer.Length, _initialChunk.Length - _pos);
                _initialChunk.AsSpan(_pos, toCopy).CopyTo(buffer.Span);
                _pos += toCopy;
                return ValueTask.FromResult(toCopy);
            }
            // Stall: complete only on cancellation. TaskCompletionSource
            // mirrors what a network stream does when the peer goes silent.
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(), tcs);
            return new ValueTask<int>(tcs.Task);
        }

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
    }
}

/// <summary>
/// End-to-end tests for the Range-resume retry behaviour in
/// <see cref="DvdDownloadService.CopyWithRetriesAsync"/>. Stubs the HTTP
/// pipeline with a custom <see cref="HttpMessageHandler"/> so we can script
/// stall-then-resume sequences and verify what the worker actually sees,
/// without standing up a real server or DB.
/// </summary>
public sealed class DvdDownloadServiceRetryTests
{
    private static readonly Uri SampleUri = new("https://download.microsoft.com/x.zip");
    private static readonly TimeSpan ShortIdle = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task First_attempt_stalls_then_Range_resume_completes_with_full_body()
    {
        // Attempt 1 hands back 4 bytes of body then stalls. Attempt 2 — with
        // Range: bytes=4- — returns 206 Partial Content carrying the rest of
        // the file. After both responses, dst should hold the full body and
        // bytesWritten should match.
        var stage1 = new StallingStream(new byte[] { 1, 2, 3, 4 });
        var stage2 = new MemoryStream(new byte[] { 5, 6, 7, 8 });
        var handler = new ScriptedHandler(
            (req, _) =>
            {
                if (req.Headers.Range is null)
                {
                    return Response(HttpStatusCode.OK, stage1);
                }
                // Range header present — return 206 with the next slice.
                req.Headers.Range!.Ranges.Single().From.Should().Be(4);
                return Response(HttpStatusCode.PartialContent, stage2);
            });
        using var client = new HttpClient(handler);
        using var dst = new MemoryStream();

        var total = await DvdDownloadService.CopyWithRetriesAsync(
            client, SampleUri, dst, maxAttempts: 4, ShortIdle, NullLogger.Instance, CancellationToken.None);

        total.Should().Be(8);
        dst.ToArray().Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Server_returns_416_on_resume_means_we_already_have_the_whole_body()
    {
        // If the resume's Range request lands at-or-past EOF, the server
        // returns 416 Requested Range Not Satisfiable — that means the
        // previous attempt actually delivered the whole body before the
        // timer tripped. Treat it as success.
        var stage1 = new StallingStream(new byte[] { 1, 2, 3 });
        var handler = new ScriptedHandler(
            (req, _) =>
            {
                if (req.Headers.Range is null)
                {
                    return Response(HttpStatusCode.OK, stage1);
                }
                return Response(HttpStatusCode.RequestedRangeNotSatisfiable, new MemoryStream());
            });
        using var client = new HttpClient(handler);
        using var dst = new MemoryStream();

        var total = await DvdDownloadService.CopyWithRetriesAsync(
            client, SampleUri, dst, maxAttempts: 4, ShortIdle, NullLogger.Instance, CancellationToken.None);

        total.Should().Be(3);
        dst.ToArray().Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Server_ignores_Range_and_returns_200_so_we_restart_from_zero()
    {
        // Belt-and-suspenders for misbehaving servers / proxies: if the
        // resume's GET comes back 200 (full body) instead of 206 (partial),
        // truncate dst and start writing from byte 0.
        var stage1 = new StallingStream(new byte[] { 1, 2, 3 });
        var stage2 = new MemoryStream(new byte[] { 9, 9, 9, 9, 9 });
        var handler = new ScriptedHandler(
            (req, _) =>
            {
                if (req.Headers.Range is null)
                {
                    return Response(HttpStatusCode.OK, stage1);
                }
                // Pretend the proxy stripped the Range header.
                return Response(HttpStatusCode.OK, stage2);
            });
        using var client = new HttpClient(handler);
        using var dst = new MemoryStream();

        var total = await DvdDownloadService.CopyWithRetriesAsync(
            client, SampleUri, dst, maxAttempts: 4, ShortIdle, NullLogger.Instance, CancellationToken.None);

        total.Should().Be(5);
        // dst was truncated at the start of attempt 2 and refilled.
        dst.ToArray().Should().Equal(9, 9, 9, 9, 9);
    }

    [Fact]
    public async Task Every_attempt_stalls_then_TimeoutException_propagates()
    {
        // Two attempts, both stalling. CopyWithRetriesAsync should bubble the
        // TimeoutException so DownloadValidatedAsync can translate it into the
        // friendly admin message.
        var handler = new ScriptedHandler(
            (req, _) =>
            {
                var stream = new StallingStream(new byte[] { 1, 2 });
                var status = req.Headers.Range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent;
                return Response(status, stream);
            });
        using var client = new HttpClient(handler);
        using var dst = new MemoryStream();

        var act = async () => await DvdDownloadService.CopyWithRetriesAsync(
            client, SampleUri, dst, maxAttempts: 2, ShortIdle, NullLogger.Instance, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Initial_GET_with_bad_status_fails_without_retrying()
    {
        // A 404 on the first attempt means the URL is wrong, not the CDN. No
        // Range-resume makes sense here — fail fast with the friendly message.
        var handler = new ScriptedHandler((_, _) => Response(HttpStatusCode.NotFound, new MemoryStream()));
        using var client = new HttpClient(handler);
        using var dst = new MemoryStream();

        var act = async () => await DvdDownloadService.CopyWithRetriesAsync(
            client, SampleUri, dst, maxAttempts: 4, ShortIdle, NullLogger.Instance, CancellationToken.None);

        await act.Should().ThrowAsync<PlanValidationException>();
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Clean_first_attempt_does_not_open_a_second_connection()
    {
        // Happy path: the body finishes on attempt 1, so only one request
        // hits the handler. The retry loop should exit cleanly without
        // touching the Range path.
        var handler = new ScriptedHandler(
            (_, _) => Response(HttpStatusCode.OK, new MemoryStream(new byte[] { 1, 2, 3 })));
        using var client = new HttpClient(handler);
        using var dst = new MemoryStream();

        var total = await DvdDownloadService.CopyWithRetriesAsync(
            client, SampleUri, dst, maxAttempts: 4, ShortIdle, NullLogger.Instance, CancellationToken.None);

        total.Should().Be(3);
        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].Headers.Range.Should().BeNull();
    }

    private static HttpResponseMessage Response(HttpStatusCode status, Stream body)
    {
        var response = new HttpResponseMessage(status) { Content = new StreamContent(body) };
        return response;
    }

    /// <summary>
    /// Programmable test handler: every request is fed through a caller-supplied
    /// callback that returns the canned response. Records each request so tests
    /// can assert on the Range header / attempt count.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _respond;
        public List<HttpRequestMessage> Requests { get; } = new();

        public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) =>
            _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request, cancellationToken));
        }
    }

    /// <summary>
    /// Mirrors <c>DvdDownloadServiceCopyTests.StallingStream</c> — separate
    /// copy here because both fixtures need it and they live in distinct
    /// classes for parallel-runner isolation.
    /// </summary>
    private sealed class StallingStream : Stream
    {
        private readonly byte[] _initialChunk;
        private int _pos;

        public StallingStream(byte[] initialChunk) => _initialChunk = initialChunk;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < _initialChunk.Length)
            {
                var toCopy = Math.Min(buffer.Length, _initialChunk.Length - _pos);
                _initialChunk.AsSpan(_pos, toCopy).CopyTo(buffer.Span);
                _pos += toCopy;
                return ValueTask.FromResult(toCopy);
            }
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(), tcs);
            return new ValueTask<int>(tcs.Task);
        }

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
    }
}
