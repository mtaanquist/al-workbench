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
        using var batcher = new ProgressBatcher((lines, _) =>
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
        using var batcher = new ProgressBatcher((lines, _) =>
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
        using var batcher = new ProgressBatcher((lines, _) =>
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
    public async Task A_closed_command_stops_the_progress_and_signals_the_verb()
    {
        using var batcher = new ProgressBatcher((_, _) => throw new CommandClosedException("Stop working on it."), _clock, _log);
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
        using var batcher = new ProgressBatcher((lines, _) =>
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
}
