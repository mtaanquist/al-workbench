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

        (await writer.SweepAsync(CancellationToken.None)).Should().Be(2);

        await using var ctx = _db.NewContext();
        (await ctx.AppLogEntries.Select(e => e.Message).ToListAsync()).Should().Equal("kept");
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

    private StoredLogWriter NewWriter()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString })
            .Build();
        return new StoredLogWriter(_buffer, config, _clock, NullLogger<StoredLogWriter>.Instance, new WorkerHeartbeatRegistry());
    }

    private StoredLogService NewService(bool siteAdmin)
    {
        var org = new TestOrganizationContext { CurrentOrganizationId = TestDb.DefaultOrgId, IsSiteAdmin = siteAdmin };
        return new StoredLogService(_db.NewContext(org), org);
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
