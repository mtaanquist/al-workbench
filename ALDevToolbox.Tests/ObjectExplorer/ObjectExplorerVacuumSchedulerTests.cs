using ALDevToolbox.Data;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The nightly <see cref="ObjectExplorerVacuumScheduler"/>: when it runs, and that what
/// it runs works against the real schema. Its table names are hard-coded SQL, so a
/// renamed table would otherwise fail every night with nothing but a log line to show
/// for it. Each run resolves one <see cref="AppDbContext"/>, which is how these tests
/// count runs.
/// </summary>
public sealed class ObjectExplorerVacuumSchedulerTests : IDisposable
{
    // A Monday, early morning, before the default 03:00 slot.
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 1, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly ServiceProvider _services;
    private int _runs;

    public ObjectExplorerVacuumSchedulerTests()
    {
        _services = new ServiceCollection()
            .AddScoped(_ =>
            {
                Interlocked.Increment(ref _runs);
                return _db.NewContext();
            })
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task The_first_tick_after_a_start_vacuums_every_table_it_names()
    {
        var scheduler = NewScheduler(hour: null);

        // Throws if any table the scheduler names is missing from the migrated schema.
        await scheduler.TickOnceAsync(TestContext.Current.CancellationToken);

        _runs.Should().Be(1, "a fresh process has not run yesterday's slot, so it catches up at once");
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData(null, 3)]
    [InlineData("24", 3)]
    [InlineData("three", 3)]
    public async Task It_runs_once_a_day_at_the_configured_hour(string? configured, int expectedHour)
    {
        var scheduler = NewScheduler(configured);
        var ct = TestContext.Current.CancellationToken;
        await scheduler.TickOnceAsync(ct);
        _runs.Should().Be(1);

        _clock.Advance(Start.Date.AddHours(expectedHour).AddMinutes(-1) - Start.UtcDateTime);
        await scheduler.TickOnceAsync(ct);
        _runs.Should().Be(1, "a minute before the slot is still yesterday's");

        _clock.Advance(TimeSpan.FromMinutes(1));
        await scheduler.TickOnceAsync(ct);
        _runs.Should().Be(2, "the slot has come");

        _clock.Advance(TimeSpan.FromMinutes(1));
        await scheduler.TickOnceAsync(ct);
        _runs.Should().Be(2, "one run per slot");
    }

    [Fact]
    public async Task A_slot_missed_while_the_host_was_down_is_caught_up_once()
    {
        var scheduler = NewScheduler(hour: null);
        var ct = TestContext.Current.CancellationToken;
        _clock.Advance(TimeSpan.FromHours(2)); // 03:00, the slot.
        await scheduler.TickOnceAsync(ct);
        _runs.Should().Be(1);

        // Nothing ticks for two days, then the host is back mid-morning.
        _clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromHours(7));
        await scheduler.TickOnceAsync(ct);
        await scheduler.TickOnceAsync(ct);

        _runs.Should().Be(2, "the missed slots are made up for with a single run");
    }

    private ObjectExplorerVacuumScheduler NewScheduler(string? hour)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OE_VACUUM_HOUR_UTC"] = hour })
            .Build();
        return new ObjectExplorerVacuumScheduler(
            _services, _clock, config, NullLogger<ObjectExplorerVacuumScheduler>.Instance, new WorkerHeartbeatRegistry());
    }
}
