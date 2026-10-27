using ALDevToolbox.Services.Backups;
using AwesomeAssertions;
using Npgsql;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Holds the backup/restore advisory lock from a session of its own, as a second app
/// instance or a concurrent request would. Dispose unlocks before closing, the way
/// <see cref="BackupCoordination"/> does: closing alone leaves the release to the
/// server once the old backend exits, which can be after the test has moved on.
/// </summary>
internal sealed class BackupLockHolder : IAsyncDisposable
{
    private readonly NpgsqlConnection _conn;

    private BackupLockHolder(NpgsqlConnection conn) => _conn = conn;

    public static async Task<BackupLockHolder> AcquireAsync(string connectionString)
    {
        var conn = new NpgsqlConnection(connectionString);
        try
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_try_advisory_lock(@k)";
            cmd.Parameters.AddWithValue("@k", BackupCoordination.BackupLockKey);
            ((bool)(await cmd.ExecuteScalarAsync())!).Should().BeTrue("nothing else holds the lock yet");
            return new BackupLockHolder(conn);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT pg_advisory_unlock(@k)";
            cmd.Parameters.AddWithValue("@k", BackupCoordination.BackupLockKey);
            await cmd.ExecuteScalarAsync();
        }
        await _conn.DisposeAsync();
    }
}
