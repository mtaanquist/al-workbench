namespace ALDevToolbox.Services.Configuration;

/// <summary>
/// Where backups are written and which Postgres client binaries produce them.
///
/// <para>These used to be read straight from the process environment inside
/// <see cref="BackupService"/> and <see cref="PerTenantBackupService"/>. The
/// environment is process-wide, so a test wanting its own directory had to set
/// the variable globally and restore it afterwards, and two such tests running
/// at once wrote into each other's directory (#733). Reading them once at
/// startup and passing the result in means a caller can hand each instance the
/// directory it should use.</para>
///
/// <para>The variable names are unchanged: they are the documented deployment
/// interface in <c>compose.yaml</c>, the README and
/// <c>.design/deployment.md</c>.</para>
/// </summary>
public sealed record BackupOptions
{
    /// <summary>Root for both site-wide dumps and the per-tenant subtree. <c>BACKUPS_DIR</c>.</summary>
    public string Directory { get; init; } = "/var/lib/aldevtoolbox/backups";

    /// <summary><c>pg_dump</c> binary, resolved on PATH unless overridden. <c>PG_DUMP_PATH</c>.</summary>
    public string PgDumpPath { get; init; } = "pg_dump";

    /// <summary><c>pg_restore</c> binary, resolved on PATH unless overridden. <c>PG_RESTORE_PATH</c>.</summary>
    public string PgRestorePath { get; init; } = "pg_restore";

    /// <summary>
    /// How long a full restore waits, with the site closed, for imports, builds and
    /// background sweeps that are already running to finish before it refuses
    /// (#1538). Not read from configuration; tests shorten it.
    /// </summary>
    public TimeSpan ActiveJobsWait { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Reads the deployment's values. ASP.NET's default configuration already
    /// includes environment variables, so the operator-facing names keep
    /// working and an <c>appsettings</c> entry now works too.
    /// </summary>
    public static BackupOptions FromConfiguration(IConfiguration configuration)
    {
        var defaults = new BackupOptions();
        return new BackupOptions
        {
            Directory = Blank(configuration["BACKUPS_DIR"]) ?? defaults.Directory,
            PgDumpPath = Blank(configuration["PG_DUMP_PATH"]) ?? defaults.PgDumpPath,
            PgRestorePath = Blank(configuration["PG_RESTORE_PATH"]) ?? defaults.PgRestorePath,
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
