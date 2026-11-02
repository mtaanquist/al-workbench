using ALDevToolbox.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// The tracker's count has to come back to zero however a command ends. A
/// cancelled one used to stay counted, so every component test whose page
/// cancelled a query on dispose sat on the full settle timeout (#1552, TEST-7).
/// </summary>
public sealed class InFlightCommandTrackerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private AppDbContext NewTrackedContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_db.ConnectionString)
            .AddInterceptors(_db.CommandTracker)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options,
        _db.OrgContext);

    [Fact]
    public async Task A_cancelled_command_is_no_longer_counted()
    {
        await using (var ctx = NewTrackedContext())
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var slow = () => ctx.Database.ExecuteSqlRawAsync("SELECT pg_sleep(30)", cancel.Token);
            await slow.Should().ThrowAsync<OperationCanceledException>();
        }

        _db.CommandTracker.Total.Should().BeGreaterThan(0, "the tracker saw the command");
        _db.CommandTracker.InFlight.Should().Be(0);
        _db.WaitForQueriesToSettle().Should().BeTrue();
    }

    [Fact]
    public async Task A_cancelled_connection_open_is_no_longer_counted()
    {
        await using (var ctx = NewTrackedContext())
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            var open = () => ctx.Database.OpenConnectionAsync(cancel.Token);
            await open.Should().ThrowAsync<OperationCanceledException>();
        }

        _db.CommandTracker.Total.Should().BeGreaterThan(0, "the open reached the tracker before it was cancelled");
        _db.CommandTracker.InFlight.Should().Be(0, _db.CommandTracker.DescribeInFlight());
    }

    [Fact]
    public async Task Finished_and_failed_commands_are_no_longer_counted()
    {
        await using (var ctx = NewTrackedContext())
        {
            await ctx.Organizations.AsNoTracking().ToListAsync();
            var bad = () => ctx.Database.ExecuteSqlRawAsync("SELECT * FROM no_such_table");
            await bad.Should().ThrowAsync<Exception>();
        }

        _db.CommandTracker.InFlight.Should().Be(0);
    }
}
