using System.Data.Common;
using System.Runtime.CompilerServices;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Operations;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ALDevToolbox.Data;

/// <summary>
/// Keeps ordinary work off the database while a restore replaces it (#1571).
/// <see cref="MaintenanceModeState"/> stops HTTP requests (the middleware) and
/// background work (the worker bases), but an interactive page's click runs
/// inside a long-lived circuit: it can have started before the restore and
/// still be issuing commands after it, so no gate at the edge can see it
/// finish. This one sits on every <see cref="AppDbContext"/> command instead.
/// <list type="bullet">
/// <item>While the site is open, each command is counted on
/// <see cref="MaintenanceModeState.ActiveJobs"/> from the moment it is sent
/// until it completes (for a query, until its reader is disposed), so the
/// restore's wait sees a command in flight and does not drop the schema under
/// it.</item>
/// <item>While maintenance mode is on, a command is refused with
/// <see cref="DatabaseClosedForMaintenanceException"/> unless the flow is
/// allowed (<see cref="MaintenanceModeState.AllowDatabase"/>: the restore's
/// own work, a background job counted before the restore began, a request
/// that arrived during maintenance, which the maintenance middleware answers)
/// or the scope belongs to a SiteAdmin, whose pages keep working so they can
/// watch, or recover, the restore. So what is refused is an open page's
/// circuit, and a request or job that was already running uncounted when the
/// restore began.</item>
/// </list>
/// A guarantee per command, not per operation: something that issued its
/// first command before the restore began has its later ones refused. That is
/// deliberate, since the restore replaces whatever it would have written.
/// Commands sent on a raw connection outside EF are not seen.
/// </summary>
public sealed class MaintenanceDatabaseGuard : DbCommandInterceptor
{
    private static readonly object Counted = new();

    private readonly MaintenanceModeState _maintenance;
    private readonly IOrganizationContext _org;
    private readonly ConditionalWeakTable<DbCommand, object> _counted = new();

    public MaintenanceDatabaseGuard(MaintenanceModeState maintenance, IOrganizationContext org)
    {
        _maintenance = maintenance;
        _org = org;
    }

    private void Begin(DbCommand command)
    {
        if (_maintenance.TryBeginJob())
        {
            _counted.AddOrUpdate(command, Counted);
            return;
        }
        if (_maintenance.DatabaseAllowedHere || _org.IsSiteAdmin) return;
        throw new DatabaseClosedForMaintenanceException();
    }

    private void End(DbCommand command)
    {
        if (_counted.Remove(command)) _maintenance.EndJob();
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Begin(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Begin(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Begin(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Begin(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Begin(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Begin(command);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        End(command);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        End(command);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        End(command);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        End(command);
        return ValueTask.FromResult(result);
    }

    // A query stays counted until its reader is disposed: a batch's later
    // statements run while the reader moves to their results.
    public override InterceptionResult DataReaderDisposing(
        DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        End(command);
        return result;
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => End(command);

    public override Task CommandFailedAsync(
        DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        End(command);
        return Task.CompletedTask;
    }

    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData) => End(command);

    public override Task CommandCanceledAsync(
        DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
    {
        End(command);
        return Task.CompletedTask;
    }
}
