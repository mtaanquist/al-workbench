using ALWorkbench.Gateway.Core;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// The gateway's smaller pieces: batched progress, the run-once file, the settings
/// file, the rolling log, back-off bounds and the service SID.
/// </summary>
public sealed class GatewayCorePiecesTests : IDisposable
{
    private readonly FakeGatewayClock _clock = new();
    private readonly ScratchFolder _scratch = new();
    private readonly ListLogger _log = new();

    public void Dispose() => _scratch.Dispose();

    // ---------------------------------------------------------------- progress

    [Fact]
    public async Task Progress_goes_out_in_batches_of_at_most_200_lines_one_post_per_interval_in_order()
    {
        var posts = new List<(TimeSpan At, IReadOnlyList<string> Lines)>();
        using var batcher = new ProgressBatcher((lines, _, _) =>
        {
            posts.Add((_clock.UtcNow - _clock.Start, lines));
            return Task.CompletedTask;
        }, _clock, _log);
        for (var i = 0; i < 450; i++) batcher.Add($"line {i}");

        await batcher.DrainAsync(CancellationToken.None);

        posts.Select(p => p.Lines.Count).Should().Equal(200, 200, 50);
        posts.SelectMany(p => p.Lines).Should().Equal(Enumerable.Range(0, 450).Select(i => $"line {i}"));
        posts.Zip(posts.Skip(1), (a, b) => b.At - a.At).Should().OnlyContain(gap => gap >= ProgressBatcher.FlushInterval);
    }

    [Fact]
    public async Task While_a_verb_runs_progress_is_posted_at_most_once_per_interval()
    {
        var posts = new List<int>();
        using var batcher = new ProgressBatcher((lines, _, _) =>
        {
            posts.Add(lines.Count);
            return Task.CompletedTask;
        }, _clock, _log);
        using var stop = new CancellationTokenSource();
        for (var i = 0; i < 250; i++) batcher.Add("x");

        var pump = batcher.RunAsync(stop.Token, CancellationToken.None);
        while (posts.Count < 2) await Task.Yield();
        await stop.CancelAsync();
        await pump.WaitAsync(TimeSpan.FromSeconds(10));

        posts.Should().Equal(200, 50);
        _clock.Delays.Should().OnlyContain(d => d == ProgressBatcher.FlushInterval);
    }

    [Fact]
    public async Task Lines_that_could_not_be_sent_are_kept_for_the_next_post()
    {
        var attempt = 0;
        var delivered = new List<string>();
        using var batcher = new ProgressBatcher((lines, _, _) =>
        {
            if (++attempt == 1) throw new WorkbenchUnreachableException("reset");
            delivered.AddRange(lines);
            return Task.CompletedTask;
        }, _clock, _log);
        batcher.Add("one");
        batcher.Add("two");

        (await batcher.FlushOnceAsync(CancellationToken.None)).Should().BeFalse();
        batcher.Add("three");
        await batcher.DrainAsync(CancellationToken.None);

        delivered.Should().Equal("one", "two", "three");
    }

    [Fact]
    public async Task A_batch_whose_answer_was_lost_is_sent_again_unchanged_under_the_same_number()
    {
        var posts = new List<(long Sequence, IReadOnlyList<string> Lines)>();
        using var batcher = new ProgressBatcher((lines, sequence, _) =>
        {
            posts.Add((sequence, lines.ToList()));
            // The first post arrives, but its answer is lost on the way back.
            if (posts.Count == 1) throw new WorkbenchUnreachableException("connection reset");
            return Task.CompletedTask;
        }, _clock, _log);
        batcher.Add("one");
        batcher.Add("two");

        (await batcher.FlushOnceAsync(CancellationToken.None)).Should().BeFalse();
        batcher.Add("three");
        await batcher.DrainAsync(CancellationToken.None);

        posts.Select(p => p.Sequence).Should().Equal(1, 1, 2);
        posts[0].Lines.Should().Equal("one", "two");
        posts[1].Lines.Should().Equal("one", "two");
        posts[2].Lines.Should().Equal("three");
    }

    [Fact]
    public async Task A_closed_command_stops_the_progress_and_signals_the_verb()
    {
        using var batcher = new ProgressBatcher((_, _, _) => throw new CommandClosedException("Stop working on it."), _clock, _log);
        batcher.Add("one");

        await batcher.FlushOnceAsync(CancellationToken.None);
        batcher.Add("two");

        batcher.Closed.IsCancellationRequested.Should().BeTrue();
        batcher.Pending.Should().Be(0);
    }

    [Fact]
    public async Task A_flood_of_lines_is_capped_and_says_how_many_were_left_out()
    {
        var delivered = new List<string>();
        using var batcher = new ProgressBatcher((lines, _, _) =>
        {
            delivered.AddRange(lines);
            return Task.CompletedTask;
        }, _clock, _log);
        for (var i = 0; i < ProgressBatcher.MaxBufferedLines + 5; i++) batcher.Add(new string('x', i == 0 ? 5000 : 1));

        // DrainAsync gives up after three failures, not after a number of posts, so it sends everything.
        await batcher.DrainAsync(CancellationToken.None);

        delivered.Should().HaveCount(ProgressBatcher.MaxBufferedLines + 1);
        delivered[0].Length.Should().Be(ProgressBatcher.MaxLineLength + 3);
        delivered[^1].Should().Be("... 5 more lines were left out because they came faster than they could be sent.");
    }

    // ---------------------------------------------------------------- run once

    [Fact]
    public void The_run_once_list_survives_a_restart_and_refuses_a_second_start()
    {
        var path = Path.Combine(_scratch.Path, "state", "started-commands.json");
        var store = new FileRunOnceStore(path, _clock);
        store.MarkStarted(1);
        store.MarkStarted(2);
        store.MarkFinished(1);

        var reopened = new FileRunOnceStore(path, _clock);

        reopened.HasStarted(1).Should().BeTrue();
        reopened.HasStarted(3).Should().BeFalse();
        reopened.Unfinished().Should().Equal(2);
        var again = () => reopened.MarkStarted(1);
        again.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Finished_commands_are_forgotten_after_the_retention_but_unfinished_ones_never()
    {
        var path = Path.Combine(_scratch.Path, "started.json");
        var store = new FileRunOnceStore(path, _clock);
        store.MarkStarted(1);
        store.MarkFinished(1);
        store.MarkStarted(2);
        _clock.Advance(FileRunOnceStore.Retention + TimeSpan.FromDays(1));

        store.MarkStarted(3);

        var reopened = new FileRunOnceStore(path, _clock);
        reopened.HasStarted(1).Should().BeFalse();
        reopened.Unfinished().Should().Equal(2, 3);
    }

    [Fact]
    public void A_damaged_run_once_file_stops_the_gateway_rather_than_risk_running_a_command_twice()
    {
        var path = Path.Combine(_scratch.Path, "started.json");
        File.WriteAllText(path, "{ not json");

        var open = () => new FileRunOnceStore(path, _clock);

        open.Should().Throw<InvalidOperationException>().WithMessage("*damaged*");
    }

    // ---------------------------------------------------------------- settings

    [Fact]
    public void Settings_round_trip_through_the_file_and_delete_cleanly()
    {
        var store = new JsonFileGatewaySettingsStore(Path.Combine(_scratch.Path, "gateway.json"));
        var settings = new GatewaySettings
        {
            WorkbenchUrl = "https://workbench.cronus.example",
            GatewayId = 3,
            Name = "CRONUS-BC01",
            KeyName = "ALWorkbenchGateway-20261014120000",
            KeyFingerprint = "1A2B-3C4D-5E6F-7A8B-9C0D",
            RegisteredAt = _clock.UtcNow,
            ServiceAccount = @"NT SERVICE\ALWorkbenchGateway",
        };

        store.Load().Should().BeNull();
        store.Save(settings);
        store.Load().Should().Be(settings);
        store.Delete();
        store.Load().Should().BeNull();
    }

    // ---------------------------------------------------------------- log file

    [Fact]
    public void The_log_file_rolls_daily_and_drops_files_older_than_thirty_days()
    {
        var folder = Path.Combine(_scratch.Path, "logs");
        Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, "gateway-" + _clock.UtcNow.AddDays(-31).ToString("yyyyMMdd") + ".log");
        var recent = Path.Combine(folder, "gateway-" + _clock.UtcNow.AddDays(-29).ToString("yyyyMMdd") + ".log");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");
        using var provider = new RollingFileLoggerProvider(folder, _clock);
        var logger = provider.CreateLogger("test");

        logger.CommandStarted(5, "refresh-inventory", "Ida Consultant");
        logger.LogDebug("not written at the default level");
        var firstDay = provider.CurrentFile;
        _clock.Advance(TimeSpan.FromDays(1));
        logger.CommandSucceeded(5, "refresh-inventory", "Ida Consultant");

        File.Exists(old).Should().BeFalse();
        File.Exists(recent).Should().BeTrue();
        File.ReadAllText(firstDay).Should().Be(
            "2026-10-14 12:00:00Z INFO  Started command 5 (refresh-inventory), requested by Ida Consultant." + Environment.NewLine);
        File.ReadAllText(provider.CurrentFile).Should().Contain("succeeded");
    }

    // ---------------------------------------------------------------- back-off, SID

    [Fact]
    public void Back_off_doubles_from_two_seconds_to_five_minutes_and_resets()
    {
        var backoff = new Backoff(new Random(1));
        var waits = Enumerable.Range(0, 12).Select(_ => backoff.Next()).ToList();

        Backoff.Ceiling(1).Should().Be(TimeSpan.FromSeconds(2));
        Backoff.Ceiling(5).Should().Be(TimeSpan.FromSeconds(32));
        Backoff.Ceiling(100).Should().Be(Backoff.Maximum);
        waits.Select((w, i) => w >= Backoff.Ceiling(i + 1) / 2 && w <= Backoff.Ceiling(i + 1)).Should().OnlyContain(ok => ok);
        backoff.Reset();
        backoff.Next().Should().BeLessThanOrEqualTo(Backoff.Initial);
    }

    [Fact]
    public void The_service_sid_is_the_one_Windows_derives_from_the_service_name()
    {
        // The documented SID of SQL Server's default instance account, NT SERVICE\MSSQLSERVER.
        ServiceSid.For("MSSQLSERVER").Should().Be("S-1-5-80-3880718306-3832830129-1677859214-2598158968-1052248003");
        ServiceSid.For("mssqlserver").Should().Be(ServiceSid.For("MSSQLSERVER"));
    }

    // ---------------------------------------------------------------- review fixes

    [Fact]
    public void A_saved_result_survives_a_restart_and_is_not_reported_as_interrupted()
    {
        var path = Path.Combine(_scratch.Path, "state", "started-commands.json");
        var store = new FileRunOnceStore(path, _clock);
        store.MarkStarted(1);
        store.MarkStarted(2);
        store.SaveResult(2, new ALWorkbench.Gateway.Contracts.GatewayCommandResultRequest { Succeeded = false, FailureMessage = "Disk full." });

        var reopened = new FileRunOnceStore(path, _clock);

        reopened.Unfinished().Should().Equal(1);
        reopened.PendingResults().Should().ContainSingle().Which.Value.FailureMessage.Should().Be("Disk full.");
        reopened.MarkFinished(2);
        new FileRunOnceStore(path, _clock).PendingResults().Should().BeEmpty();
        new FileRunOnceStore(path, _clock).HasStarted(2).Should().BeTrue();
    }

    [Fact]
    public async Task Requests_to_a_workbench_under_a_path_with_a_space_sign_the_path_the_server_reads()
    {
        var url = WorkbenchAddress.TryNormalise("https://cronus.example/al work", out _)!;
        using var workbench = new FakeWorkbench { Respond = _ => FakeWorkbench.Heartbeat(approved: true) };
        var keys = new FakeKeyStore();
        keys.Create("key-1", null);
        using var http = workbench.Client();
        using var client = new WorkbenchClient(http, new GatewaySettings { WorkbenchUrl = url, GatewayId = 7, KeyName = "key-1" }, keys, _clock);

        await client.HeartbeatAsync(new ALWorkbench.Gateway.Contracts.GatewayHeartbeatRequest(), CancellationToken.None);

        var seen = workbench.Requests.Single();
        seen.Path.Should().Be("/al%20work/gateway/heartbeat");
        var payload = seen.Authorization!["Bearer ".Length..].Split('.')[1];
        using var claims = System.Text.Json.JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(payload));
        // ASP.NET Core hands the workbench PathBase + Path unescaped.
        claims.RootElement.GetProperty("htu").GetString().Should().Be("/al work/gateway/heartbeat");
    }

    [Fact]
    public async Task A_download_that_stops_sending_is_given_up_after_the_idle_timeout()
    {
        using var workbench = new FakeWorkbench { Respond = _ => Streaming(new StallingStream("first bytes"u8.ToArray(), trickle: null)) };
        using var client = DownloadClient(workbench, overall: TimeSpan.FromMinutes(5), idle: TimeSpan.FromMilliseconds(300));
        using var target = new MemoryStream();

        var download = () => client.DownloadFileAsync(1, 2, target, 1024, CancellationToken.None);

        (await download.Should().ThrowAsync<WorkbenchUnreachableException>().WaitAsync(TimeSpan.FromSeconds(30)))
            .Which.Message.Should().Contain("stalled");
        target.ToArray().Should().Equal("first bytes"u8.ToArray());
    }

    [Fact]
    public async Task A_download_that_trickles_forever_is_given_up_after_the_overall_timeout()
    {
        // A byte every 50 ms never trips the idle timeout; the overall one ends it.
        using var workbench = new FakeWorkbench { Respond = _ => Streaming(new StallingStream([], trickle: TimeSpan.FromMilliseconds(50))) };
        using var client = DownloadClient(workbench, overall: TimeSpan.FromMilliseconds(600), idle: TimeSpan.FromSeconds(10));
        using var target = new MemoryStream();

        var download = () => client.DownloadFileAsync(1, 2, target, long.MaxValue, CancellationToken.None);

        (await download.Should().ThrowAsync<WorkbenchUnreachableException>().WaitAsync(TimeSpan.FromSeconds(30)))
            .Which.Message.Should().Contain("did not finish");
        target.Length.Should().BeGreaterThan(0);
    }

    private WorkbenchClient DownloadClient(FakeWorkbench workbench, TimeSpan overall, TimeSpan idle)
    {
        var keys = new FakeKeyStore();
        keys.Create("key-1", null);
        return new WorkbenchClient(workbench.Client(), new GatewaySettings { WorkbenchUrl = FakeWorkbench.Url, GatewayId = 7, KeyName = "key-1" }, keys, _clock)
        {
            DownloadTimeout = overall,
            DownloadIdleTimeout = idle,
        };
    }

    private static HttpResponseMessage Streaming(Stream body) => new(System.Net.HttpStatusCode.OK) { Content = new StreamContent(body) };

    /// <summary>A response body that sends <c>first</c>, then either nothing ever again or one byte per <c>trickle</c>.</summary>
    private sealed class StallingStream : Stream
    {
        private readonly byte[] _first;
        private readonly TimeSpan? _trickle;
        private bool _sentFirst;

        public StallingStream(byte[] first, TimeSpan? trickle)
        {
            _first = first;
            _trickle = trickle;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentFirst && _first.Length > 0)
            {
                _sentFirst = true;
                _first.CopyTo(buffer);
                return _first.Length;
            }
            if (_trickle is null)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            await Task.Delay(_trickle.Value, cancellationToken);
            buffer.Span[0] = (byte)'x';
            return 1;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Reads are asynchronous.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
