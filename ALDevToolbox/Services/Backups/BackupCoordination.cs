using Npgsql;

namespace ALDevToolbox.Services.Backups;

/// <summary>
/// Process- (and container-) wide mutual exclusion for backup and restore
/// operations, built on a Postgres session-level advisory lock. Every
/// full-database backup create + prune, full-database restore, and per-tenant
/// restore acquires the same key, so none of them can overlap: a scheduled
/// <c>pg_dump</c> can't read a schema an in-place restore is mid-drop on, two
/// concurrent backups can't both run the retention prune (double-delete /
/// filename collision), and a duplicate-submitted restore is rejected.
///
/// <para>The lock is held on a dedicated connection for the duration of the
/// operation and released on dispose; if the process dies the session ends and
/// Postgres drops the lock automatically. See issues #370 and #371.</para>
/// </summary>
internal static class BackupCoordination
{
    /// <summary>
    /// Fixed advisory-lock key shared by all backup/restore operations. The
    /// value is arbitrary but stable ("BKUP" in ASCII); what matters is that
    /// every operation uses the same one.
    /// </summary>
    public const long BackupLockKey = 0x42_4B_55_50; // "BKUP"

    /// <summary>
    /// Tries to take the backup/restore advisory lock. Throws
    /// <see cref="InvalidOperationException"/> immediately when another
    /// operation already holds it — callers (web endpoints, the scheduler)
    /// surface a "try again" message rather than blocking a request thread.
    /// </summary>
    public static async Task<IAsyncDisposable> AcquireAsync(
        string connectionString, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_try_advisory_lock(@k)";
            cmd.Parameters.AddWithValue("@k", BackupLockKey);
            var acquired = (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            if (!acquired)
            {
                throw new InvalidOperationException(
                    "Another backup or restore is already in progress. Try again once it finishes.");
            }
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new Handle(conn);
    }

    /// <summary>
    /// Takes the same lock as <see cref="AcquireAsync"/>, waiting for it rather
    /// than refusing. For the off-site download's final <c>backups</c> insert:
    /// the file has already been fetched, so being refused because a backup or
    /// restore is running would throw that work away. A restore holds the lock
    /// from before it reads the backup inventory until it has put it back, so an
    /// insert made under it can never land inside one and be wiped (#1579).
    /// </summary>
    public static async Task<IAsyncDisposable> WaitAsync(
        string connectionString, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_advisory_lock(@k)";
            cmd.Parameters.AddWithValue("@k", BackupLockKey);
            // A restore can hold the lock for as long as its pg_restore runs.
            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new Handle(conn);
    }

    private sealed class Handle : IAsyncDisposable
    {
        private readonly NpgsqlConnection _conn;
        public Handle(NpgsqlConnection conn) => _conn = conn;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT pg_advisory_unlock(@k)";
                cmd.Parameters.AddWithValue("@k", BackupLockKey);
                await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            }
            catch
            {
                // Closing the connection ends the session, which releases the
                // lock anyway — an unlock failure is not worth surfacing.
            }
            await _conn.DisposeAsync().ConfigureAwait(false);
        }
    }
}
