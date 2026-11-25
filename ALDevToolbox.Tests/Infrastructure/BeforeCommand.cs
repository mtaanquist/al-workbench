using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Runs a callback once, just before the first command whose SQL matches, whether
/// EF sends it as a statement or as a query. For a race that can only be shown by
/// landing another write in the gap between two of the code's own commands: give the
/// context under test this interceptor (<c>TestDb.NewContext(interceptor)</c>) and do
/// the other write in the callback, on a context of its own.
/// </summary>
public sealed class BeforeCommand : DbCommandInterceptor
{
    private readonly Func<string, bool> _matches;
    private readonly Func<Task> _callback;

    public BeforeCommand(Func<string, bool> matches, Func<Task> callback)
    {
        _matches = matches;
        _callback = callback;
    }

    /// <summary>Whether the callback ran, so a test can show its write landed in the window under test.</summary>
    public bool Fired { get; private set; }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await FireAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await FireAsync(command);
        return result;
    }

    private async Task FireAsync(DbCommand command)
    {
        if (Fired || !_matches(command.CommandText)) return;
        Fired = true;
        await _callback();
    }
}
