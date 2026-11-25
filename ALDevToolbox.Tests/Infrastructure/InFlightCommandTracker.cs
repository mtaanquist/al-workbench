using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Counts EF Core commands that are still using their connection, so a test
/// fixture can wait for the connection to fall idle before tearing down the
/// scope that owns the <see cref="ALDevToolbox.Data.AppDbContext"/>.
///
/// <para>
/// The hazard this closes: a Blazor component's <c>OnInitializedAsync</c> runs
/// a chain of awaited queries, but a bUnit <c>WaitForAssertion</c> is satisfied
/// as soon as the asserted markup appears - which is often after the first
/// await, with later queries still on the wire. The test method then returns,
/// teardown disposes the DI scope, and Npgsql closes a connection mid-command.
/// It surfaces as "Received backend message BindComplete while expecting
/// ReadyForQueryMessage" or "A command is already in progress", from inside
/// <c>DbContext.Dispose</c>. bUnit 1.x happened to hide this; bUnit 2 does not.
/// </para>
///
/// <para>
/// Executing-to-executed is the wrong window: for a reader, the connection
/// stays busy until the reader is consumed and disposed. So reader commands are
/// held open until <see cref="DataReaderDisposing"/>, and only the reader-less
/// paths (scalar, non-query) settle on their executed callback.
/// </para>
/// </summary>
public sealed class InFlightCommandTracker : DbCommandInterceptor, IDbConnectionInterceptor
{
    private int _inFlight;
    private int _total;

    /// <summary>Commands currently holding a connection. Zero means idle.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Every command this tracker has seen. Exists so a test can prove the
    /// interceptor is actually reaching EF - a tracker that silently saw
    /// nothing would make the teardown wait a no-op that looks like a fix.
    /// </summary>
    public int Total => Volatile.Read(ref _total);

    /// <summary>What each counted command or opening connection is, for the timeout message.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<object, string> _open = new();

    /// <summary>
    /// The commands and connection opens still counted, one per line, so a
    /// settle timeout can name the query it waited on.
    /// </summary>
    public string DescribeInFlight() =>
        string.Join(Environment.NewLine, _open.Values.Select(v => "  " + v));

    private void Enter(DbCommand command)
    {
        _open[command] = command.CommandText.ReplaceLineEndings(" ");
        Enter();
    }

    private void Enter(DbConnection connection)
    {
        _open[connection] = "(opening a connection)";
        Enter();
    }

    private void Leave(object key)
    {
        _open.TryRemove(key, out _);
        Leave();
    }

    private void Enter()
    {
        Interlocked.Increment(ref _inFlight);
        Interlocked.Increment(ref _total);
    }

    // Never let the counter go negative: a command can fail after its reader
    // was already disposed, and a stuck-negative counter would make the wait
    // below return early for every later test in the class.
    private void Leave()
    {
        if (Interlocked.Decrement(ref _inFlight) < 0)
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Enter(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Enter(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    // Readers are released in DataReaderDisposing, not here.

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Enter(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Enter(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Leave(command);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
    {
        Leave(command);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Enter(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Enter(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Leave(command);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        Leave(command);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult DataReaderDisposing(
        DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        Leave(command);
        return base.DataReaderDisposing(command, eventData, result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        Leave(command);
        base.CommandFailed(command, eventData);
    }

    public override Task CommandFailedAsync(
        DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Leave(command);
        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    // A command cancelled through its token raises CommandCanceled, not
    // CommandFailed. Without these, every query a page cancelled on dispose (an
    // auto-refresh, a debounced search) stayed counted for ever, and teardown sat
    // on the full ten-second settle timeout waiting for it (#1552, TEST-7).
    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData)
    {
        Leave(command);
        base.CommandCanceled(command, eventData);
    }

    public override Task CommandCanceledAsync(
        DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Leave(command);
        return base.CommandCanceledAsync(command, eventData, cancellationToken);
    }

    // A connection that is still opening is just as unsafe to close as one
    // running a command - teardown during the open throws "Can't close,
    // connection is in state Connecting". EF opens the connection before the
    // first command executes, so command tracking alone leaves that window
    // uncovered.
    public InterceptionResult ConnectionOpening(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        Enter(connection);
        return result;
    }

    public ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        Enter(connection);
        return ValueTask.FromResult(result);
    }

    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Leave(connection);

    public Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Leave(connection);
        return Task.CompletedTask;
    }

    public void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData) => Leave(connection);

    public Task ConnectionFailedAsync(
        DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Leave(connection);
        return Task.CompletedTask;
    }

    // An open cancelled through its token raises ConnectionCanceled, not
    // ConnectionFailed: a page that polls on its own scope and is torn down
    // mid-open left every such open counted (#1552, TEST-7).
    public void ConnectionCanceled(DbConnection connection, ConnectionEndEventData eventData) => Leave(connection);

    public Task ConnectionCanceledAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Leave(connection);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Blocks until no command has held a connection for <paramref name="quietFor"/>,
    /// or until <paramref name="timeout"/> elapses. Returns true when it settled.
    ///
    /// <para>
    /// The quiet period is the point. Touching zero once is not enough: a
    /// component's render callback frequently starts a second wave of queries
    /// just after the first drains, so a wait that returned on the first zero
    /// would hand back a context that is about to be busy again. Requiring the
    /// count to stay at zero for a short while is what makes this a quiescence
    /// check rather than a sample.
    /// </para>
    ///
    /// <para>
    /// Deliberately bounded and non-throwing: if the count somehow never
    /// drains, the caller carries on and tears down exactly as it does today.
    /// A hung teardown would be a worse failure than the race it guards.
    /// </para>
    /// </summary>
    public bool WaitUntilIdle(TimeSpan timeout, TimeSpan? quietFor = null)
    {
        var quiet = quietFor ?? TimeSpan.FromMilliseconds(50);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (InFlight == 0)
            {
                var quietUntil = DateTime.UtcNow + quiet;
                while (InFlight == 0)
                {
                    if (DateTime.UtcNow >= quietUntil) return true;
                    Thread.Sleep(5);
                }
            }
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(5);
        }
    }
}
