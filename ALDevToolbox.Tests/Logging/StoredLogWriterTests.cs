using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Logging;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Logging;

/// <summary>
/// The writer against a real database: lines land with their worker, the retention
/// sweep deletes only what is past it, and the page's search reads them back.
/// </summary>
public sealed class StoredLogWriterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly StoredLogBuffer _buffer = new();
    private readonly FakeTimeProvider _clock = new(Now);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Flush_writes_every_queued_line_with_its_worker_and_error()
    {
        _buffer.Add(new StoredLogLine(Now.UtcDateTime, LogLevel.Error, "A.B", "ProjectBuildWorker 3", "Build failed", "System.Exception: boom"));
        // PostgreSQL text cannot hold NUL; one would fail the whole batch.
        _buffer.Add(new StoredLogLine(Now.UtcDateTime, LogLevel.Information, "A.C", null, "with\0nul", null));
        await using var writer = NewWriter();

        (await writer.FlushAsync(CancellationToken.None)).Should().Be(2);

        await using var ctx = _db.NewContext();
        var rows = await ctx.AppLogEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].Worker.Should().Be("ProjectBuildWorker 3");
        rows[0].Level.Should().Be(LogLevel.Error);
        rows[0].Exception.Should().Be("System.Exception: boom");
        rows[1].Message.Should().Be("withnul");
        rows[1].Worker.Should().BeNull();
        _buffer.Pending.Should().Be(0);
    }

    [Fact]
    public async Task Sweep_deletes_only_lines_older_than_the_retention()
    {
        _buffer.Apply(LogLevel.Information, 3);
        await SeedAsync(
            Entry(Now.AddDays(-4), "old"),
            Entry(Now.AddDays(-3).AddMinutes(-1), "just past"),
            Entry(Now.AddDays(-2), "kept"));
        await using var writer = NewWriter();

        (await writer.SweepAsync(CancellationToken.None)).Should().Be((2, true));

        await using var ctx = _db.NewContext();
        (await ctx.AppLogEntries.Select(e => e.Message).ToListAsync()).Should().Equal("kept");
    }

    [Fact]
    public async Task A_large_sweep_stops_after_its_slices_and_finishes_on_the_next_poll()
    {
        // Lowering the retention can leave millions of rows to delete; one poll must not
        // hold the writer for all of them.
        _buffer.Apply(LogLevel.Information, 1);
        await SeedAsync(Enumerable.Range(0, 5).Select(i => Entry(Now.AddDays(-2).AddMinutes(i), $"old {i}"))
            .Append(Entry(Now, "kept")).ToArray());
        await using var writer = NewWriter(deleteBatchSize: 2);

        (await writer.SweepAsync(CancellationToken.None)).Should().Be((4, false), "two slices of two, and more is left");
        (await writer.SweepAsync(CancellationToken.None)).Should().Be((1, true));

        await using var ctx = _db.NewContext();
        (await ctx.AppLogEntries.Select(e => e.Message).ToListAsync()).Should().Equal("kept");
    }

    [Fact]
    public async Task A_batch_that_fails_is_kept_and_written_on_a_later_poll()
    {
        // A database blip should not cost the lines that explain it.
        _buffer.Add(new StoredLogLine(Now.UtcDateTime, LogLevel.Error, "A.B", null, "while the table was away", null));
        await using var writer = NewWriter();
        await RenameTableAsync("app_log_entries", "app_log_entries_away");
        try
        {
            (await writer.FlushAsync(CancellationToken.None)).Should().Be(0);
            _buffer.Dropped.Should().Be(0, "a failed batch is held, not dropped");
        }
        finally
        {
            await RenameTableAsync("app_log_entries_away", "app_log_entries");
        }

        (await writer.FlushAsync(CancellationToken.None)).Should().Be(1);

        await using var ctx = _db.NewContext();
        (await ctx.AppLogEntries.Select(e => e.Message).ToListAsync()).Should().Equal("while the table was away");
    }

    private async Task RenameTableAsync(string from, string to)
    {
        await using var conn = new Npgsql.NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {from} RENAME TO {to}";
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Search_filters_by_worker_family_level_and_text()
    {
        await SeedAsync(
            Entry(Now.AddMinutes(-5), "build one started", worker: "ProjectBuildWorker 1"),
            Entry(Now.AddMinutes(-4), "build two failed", worker: "ProjectBuildWorker 2", level: LogLevel.Error),
            Entry(Now.AddMinutes(-3), "backup done", worker: "BackupScheduler"),
            Entry(Now.AddMinutes(-2), "page request", worker: null),
            // Not a sibling: the family match needs the space before the number.
            Entry(Now.AddMinutes(-1), "other", worker: "ProjectBuildWorkerX"));
        var svc = NewService(siteAdmin: true);

        Messages(await svc.SearchAsync(Query("ProjectBuildWorker"))).Should().Equal("build two failed", "build one started");
        Messages(await svc.SearchAsync(Query(StoredLogService.NoWorker))).Should().Equal("page request");
        Messages(await svc.SearchAsync(Query(StoredLogService.AnyWorker))).Should().HaveCount(4);
        Messages(await svc.SearchAsync(Query(StoredLogService.AnySource))).Should().HaveCount(5);
        Messages(await svc.SearchAsync(Query(StoredLogService.AnySource, LogLevel.Warning))).Should().Equal("build two failed");
        Messages(await svc.SearchAsync(Query(StoredLogService.AnySource, text: "BACKUP"))).Should().Equal("backup done");
        // LIKE wildcards in the search box are matched literally.
        Messages(await svc.SearchAsync(Query(StoredLogService.AnySource, text: "%"))).Should().BeEmpty();
    }

    [Fact]
    public async Task Search_keeps_to_the_time_range()
    {
        await SeedAsync(
            Entry(Now.AddHours(-3), "before"),
            Entry(Now.AddHours(-2), "inside"),
            Entry(Now.AddHours(-1), "after"));

        var page = await NewService(siteAdmin: true).SearchAsync(
            Query(StoredLogService.AnySource) with
            {
                FromUtc = Now.AddHours(-2).AddMinutes(-5).UtcDateTime,
                ToUtc = Now.AddHours(-2).UtcDateTime,
            });

        Messages(page).Should().Equal("inside");
    }

    [Fact]
    public async Task Search_pages_newest_first_by_line()
    {
        var entries = Enumerable.Range(0, StoredLogService.PageSize + 3)
            .Select(i => Entry(Now.AddSeconds(i), $"line {i}", worker: "BackupScheduler"))
            .ToArray();
        await SeedAsync(entries);
        var svc = NewService(siteAdmin: true);

        var first = await svc.SearchAsync(Query(StoredLogService.AnyWorker));
        first.Rows.Should().HaveCount(StoredLogService.PageSize);
        first.HasOlder.Should().BeTrue();
        first.Rows[0].Message.Should().Be($"line {StoredLogService.PageSize + 2}");

        var second = await svc.SearchAsync(Query(StoredLogService.AnyWorker) with { BeforeId = first.Rows[^1].Id });
        Messages(second).Should().Equal("line 2", "line 1", "line 0");
        second.HasOlder.Should().BeFalse();
    }

    [Fact]
    public async Task Search_and_size_refuse_anyone_but_a_site_admin()
    {
        var svc = NewService(siteAdmin: false);

        await FluentActions.Awaiting(() => svc.SearchAsync(Query(StoredLogService.AnySource)))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => svc.TableSizeBytesAsync())
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Table_size_is_reported()
    {
        await SeedAsync(Entry(Now, "x"));
        (await NewService(siteAdmin: true).TableSizeBytesAsync()).Should().BePositive();
    }

    private StoredLogWriter NewWriter(string? connectionString = null, int deleteBatchSize = 20_000)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connectionString ?? _db.ConnectionString })
            .Build();
        return new StoredLogWriter(_buffer, config, _clock, NullLogger<StoredLogWriter>.Instance, new WorkerHeartbeatRegistry())
        {
            DeleteBatchSize = deleteBatchSize,
            DeleteSlicesPerPoll = 2,
        };
    }

    private StoredLogService NewService(bool siteAdmin)
    {
        var org = new TestOrganizationContext { CurrentOrganizationId = TestDb.DefaultOrgId, IsSiteAdmin = siteAdmin };
        var heartbeats = new WorkerHeartbeatRegistry();
        heartbeats.Register("ProjectBuildWorker 1", TimeSpan.FromHours(1), null);
        heartbeats.Register("ProjectBuildWorker 2", TimeSpan.FromHours(1), null);
        return new StoredLogService(_db.NewContext(org), org, heartbeats);
    }

    private static StoredLogQuery Query(string worker, LogLevel level = LogLevel.Information, string? text = null) =>
        new(worker, level, text, FromUtc: null, BeforeId: null);

    private static IEnumerable<string> Messages(StoredLogPage page) => page.Rows.Select(r => r.Message);

    private static AppLogEntry Entry(DateTimeOffset at, string message, string? worker = null, LogLevel level = LogLevel.Information) =>
        new() { Timestamp = at.UtcDateTime, Level = level, Category = "A.B", Worker = worker, Message = message };

    private async Task SeedAsync(params AppLogEntry[] entries)
    {
        await using var ctx = _db.NewContext();
        ctx.AppLogEntries.AddRange(entries);
        await ctx.SaveChangesAsync();
    }
}
