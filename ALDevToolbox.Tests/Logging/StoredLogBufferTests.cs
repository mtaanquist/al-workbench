using ALDevToolbox.Services.Logging;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;

namespace ALDevToolbox.Tests.Logging;

/// <summary>
/// The queue between every logger and the database: which levels it keeps, what it
/// does when full, and how the provider tags lines with the running worker.
/// </summary>
public sealed class StoredLogBufferTests
{
    [Theory]
    [InlineData(LogLevel.Information, LogLevel.Information, true)]
    [InlineData(LogLevel.Information, LogLevel.Debug, false)]
    [InlineData(LogLevel.Warning, LogLevel.Information, false)]
    [InlineData(LogLevel.Warning, LogLevel.Error, true)]
    [InlineData(LogLevel.Error, LogLevel.Critical, true)]
    [InlineData(LogLevel.None, LogLevel.Critical, false)]
    public void Keeps_only_lines_at_or_above_the_setting(LogLevel setting, LogLevel line, bool kept)
    {
        var buffer = new StoredLogBuffer();
        buffer.Apply(setting, 7);
        buffer.IsEnabled(line).Should().Be(kept);
    }

    [Theory]
    [InlineData(LogLevel.Trace, LogLevel.Information)]
    [InlineData(LogLevel.Debug, LogLevel.Information)]
    [InlineData(LogLevel.Critical, LogLevel.Error)]
    [InlineData(LogLevel.None, LogLevel.None)]
    public void Levels_the_app_never_logs_are_raised_to_the_nearest_it_does(LogLevel given, LogLevel applied)
    {
        var buffer = new StoredLogBuffer();
        buffer.Apply(given, 7);
        buffer.MinimumLevel.Should().Be(applied);
    }

    [Theory]
    [InlineData(0, StoredLogBuffer.MinRetentionDays)]
    [InlineData(400, StoredLogBuffer.MaxRetentionDays)]
    [InlineData(10, 10)]
    public void Retention_is_held_inside_its_range(int given, int applied)
    {
        var buffer = new StoredLogBuffer();
        buffer.Apply(LogLevel.Information, given);
        buffer.RetentionDays.Should().Be(applied);
    }

    [Fact]
    public void A_full_queue_drops_and_counts_rather_than_blocking()
    {
        var buffer = new StoredLogBuffer();
        for (var i = 0; i < StoredLogBuffer.Capacity + 5; i++) buffer.Add(Line(LogLevel.Information));

        buffer.Pending.Should().Be(StoredLogBuffer.Capacity);
        buffer.Dropped.Should().Be(5);
    }

    [Fact]
    public void Take_leaves_out_lines_queued_before_the_level_was_raised()
    {
        // Startup lines are queued before the settings row is read.
        var buffer = new StoredLogBuffer();
        buffer.Add(Line(LogLevel.Information));
        buffer.Add(Line(LogLevel.Error));
        buffer.Apply(LogLevel.Warning, 7);

        var into = new List<StoredLogLine>();
        buffer.Take(into, 10).Should().Be(2);

        into.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Error);
        buffer.Pending.Should().Be(0);
    }

    [Fact]
    public async Task Provider_tags_lines_with_the_worker_running_them()
    {
        var buffer = new StoredLogBuffer();
        using var provider = new StoredLogProvider(buffer, TimeProvider.System);
        var logger = provider.CreateLogger("ALDevToolbox.Services.Backups.BackupService");

        logger.LogInformation("Outside");
        using (WorkerLogScope.Enter("BackupScheduler"))
        {
            // A service the worker awaits writes under the worker's name too.
            await Task.Yield();
            logger.LogWarning("Inside {Thing}", "CRONUS");
        }

        var lines = new List<StoredLogLine>();
        buffer.Take(lines, 10);
        lines.Should().HaveCount(2);
        lines[0].Worker.Should().BeNull();
        lines[1].Worker.Should().Be("BackupScheduler");
        lines[1].Message.Should().Be("Inside CRONUS");
        lines[1].Category.Should().Be("ALDevToolbox.Services.Backups.BackupService");
    }

    [Fact]
    public void Provider_keeps_the_exception_and_cuts_long_messages()
    {
        var buffer = new StoredLogBuffer();
        using var provider = new StoredLogProvider(buffer, TimeProvider.System);
        var logger = provider.CreateLogger("Some.Category");

        logger.LogError(new InvalidOperationException("boom"), "{Text}", new string('x', StoredLogBuffer.MaxMessageLength + 50));

        var lines = new List<StoredLogLine>();
        buffer.Take(lines, 10);
        var line = lines.Should().ContainSingle().Subject;
        line.Message.Should().HaveLength(StoredLogBuffer.MaxMessageLength + " [cut]".Length).And.EndWith(" [cut]");
        line.Exception.Should().Contain("InvalidOperationException").And.Contain("boom");
    }

    [Fact]
    public void Provider_never_stores_the_log_writers_own_lines()
    {
        // A failing insert logs a warning; storing that warning would feed the failure back.
        var buffer = new StoredLogBuffer();
        using var provider = new StoredLogProvider(buffer, TimeProvider.System);

        provider.CreateLogger(typeof(StoredLogWriter).FullName!).LogWarning("Could not store");

        buffer.Pending.Should().Be(0);
    }

    [Fact]
    public void Http_client_request_chatter_is_kept_only_from_warning_up()
    {
        var buffer = new StoredLogBuffer();
        using var provider = new StoredLogProvider(buffer, TimeProvider.System);
        var logger = provider.CreateLogger("System.Net.Http.HttpClient.Default.LogicalHandler");

        logger.LogInformation("Start processing HTTP request");
        logger.LogWarning("Request failed");

        var lines = new List<StoredLogLine>();
        buffer.Take(lines, 10);
        lines.Should().ContainSingle().Which.Message.Should().Be("Request failed");
    }

    [Fact]
    public void Nothing_is_queued_while_storing_is_off()
    {
        var buffer = new StoredLogBuffer();
        buffer.Apply(LogLevel.None, 7);
        using var provider = new StoredLogProvider(buffer, TimeProvider.System);

        provider.CreateLogger("Some.Category").LogCritical("Down");

        buffer.Pending.Should().Be(0);
    }

    internal static StoredLogLine Line(LogLevel level, string message = "m", string? worker = null, DateTime? at = null) =>
        new(at ?? DateTime.UtcNow, level, "Some.Category", worker, message, null);
}
