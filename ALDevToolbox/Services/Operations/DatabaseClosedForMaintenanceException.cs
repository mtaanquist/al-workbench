namespace ALDevToolbox.Services.Operations;

/// <summary>
/// Thrown by <see cref="Data.MaintenanceDatabaseGuard"/> when ordinary work
/// reaches the database while a restore is replacing it (#1571).
/// </summary>
public sealed class DatabaseClosedForMaintenanceException : InvalidOperationException
{
    public DatabaseClosedForMaintenanceException()
        : base("AL Workbench is being restored from a backup, so nothing can be saved or loaded right now. Reload the page once it is back.")
    {
    }
}
