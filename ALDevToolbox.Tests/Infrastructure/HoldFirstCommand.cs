using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Holds the first query whose SQL matches until the test releases it; every later one
/// runs straight through. For asserting on a state that only lasts while a read is out,
/// such as a page's loading skeleton: without the hold, how long that state lasts is up
/// to the database and the runner, and a test that looks for it races them.
/// </summary>
public sealed class HoldFirstCommand : DbCommandInterceptor
{
    private readonly Func<string, bool> _matches;
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _seen;

    public HoldFirstCommand(Func<string, bool> matches) => _matches = matches;

    /// <summary>Completes once the matching query has reached the hold.</summary>
    public Task Held => _held.Task;

    public void Release() => _released.TrySetResult();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (_matches(command.CommandText) && Interlocked.Increment(ref _seen) == 1)
        {
            _held.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
        }
        return result;
    }
}
