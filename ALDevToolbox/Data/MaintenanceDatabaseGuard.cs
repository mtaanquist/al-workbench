using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
/// <see cref="DatabaseClosedForMaintenanceException"/>, except:
/// <list type="bullet">
/// <item>in a flow given <see cref="MaintenanceModeState.AllowDatabase"/>:
/// the restore's own work, and a background job counted before the restore
/// began;</item>
/// <item>a read (a plain <c>SELECT</c>) in a flow given
/// <see cref="MaintenanceModeState.AllowDatabaseReads"/>: a request that
/// arrived during maintenance, on its way to the maintenance page or past
/// it;</item>
/// <item>a SiteAdmin on the SiteAdmin console (a <c>/site-admin</c> request,
/// or a circuit currently showing a <c>/site-admin</c> page), so they can
/// watch, or recover, the restore. These commands are not counted, so a
/// SiteAdmin saving a console setting during a restore can still collide
/// with it.</item>
/// </list>
/// So an open page's circuit, a request or job that was already running
/// uncounted when the restore began, and any write by a request that arrived
/// during the restore are refused.</item>
/// </list>
/// A guarantee per command, not per operation: something that issued its
/// first command before the restore began has its later ones refused. That is
/// deliberate, since the restore replaces whatever it would have written.
/// Commands sent on a raw connection outside EF are not seen.
/// </summary>
public sealed class MaintenanceDatabaseGuard : DbCommandInterceptor, IDisposable
{
    private static readonly object Counted = new();

    private readonly MaintenanceModeState _maintenance;
    private readonly IOrganizationContext _org;
    private readonly IHttpContextAccessor? _http;
    private readonly ConditionalWeakTable<DbCommand, object> _counted = new();

    public MaintenanceDatabaseGuard(MaintenanceModeState maintenance, IOrganizationContext org, IHttpContextAccessor? http = null)
    {
        _maintenance = maintenance;
        _org = org;
        _http = http;
    }

    /// <summary>
    /// Set by the circuit handler for a circuit's scope: whether the page the
    /// circuit shows right now is on the SiteAdmin console. Null outside a circuit.
    /// </summary>
    internal Func<bool>? CircuitOnSiteAdminConsole { get; set; }

    private void Begin(DbCommand command)
    {
        if (_maintenance.TryBeginJob())
        {
            _counted.AddOrUpdate(command, Counted);
            return;
        }
        if (_maintenance.DatabaseAllowedHere) return;
        if (_maintenance.DatabaseReadsAllowedHere && IsRead(command.CommandText)) return;
        if (OnSiteAdminConsole()) return;
        throw new DatabaseClosedForMaintenanceException();
    }

    private bool OnSiteAdminConsole() =>
        _org.IsSiteAdmin
        && (_http?.HttpContext?.Request.Path.StartsWithSegments(HttpOrganizationContext.SiteAdminPathPrefix) == true
            || CircuitOnSiteAdminConsole?.Invoke() == true);

    /// <summary>
    /// Whether a command only reads. Deliberately conservative, since a wrong
    /// "yes" lets a write into the database being restored and a wrong "no"
    /// only refuses a read: comments are stripped (nested block comments
    /// included; SQL whose comments or dollar quotes cannot be parsed is not a
    /// read), and what is left must be one <c>SELECT</c> statement (a trailing
    /// <c>;</c> at most) with no row-lock clause, no <c>INTO</c>, and none of
    /// the functions that change state from inside a query (sequences,
    /// advisory locks, settings, notifications, large objects). Anything else,
    /// including a data-modifying CTE, counts as a write.
    /// </summary>
    internal static bool IsRead(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || DollarQuote.IsMatch(sql)) return false;
        if (StripComments(sql) is not { } code) return false;

        var statement = code.Trim();
        if (statement.EndsWith(';')) statement = statement[..^1].TrimEnd();
        return statement.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && !statement.Contains(';')
            && !LockClause.IsMatch(statement)
            && !Into.IsMatch(statement)
            && !StateChangingFunction.IsMatch(statement);
    }

    private static readonly Regex DollarQuote = new(@"\$[A-Za-z_][A-Za-z0-9_]*\$|\$\$", RegexOptions.Compiled);
    private static readonly Regex LockClause = new(
        @"\bFOR\s+(UPDATE|SHARE|NO\s+KEY\s+UPDATE|KEY\s+SHARE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Into = new(@"\bINTO\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StateChangingFunction = new(
        @"\b(nextval|setval|pg_advisory\w*|pg_try_advisory\w*|set_config|pg_notify|pg_terminate_backend|pg_cancel_backend|dblink\w*)\b|\blo_\w+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The SQL with its comments replaced by spaces, or null when a comment is
    /// left open. String literals and quoted identifiers are kept as they are
    /// and skipped over, so a comment marker inside one is not taken for a
    /// comment; a <c>;</c> or a function name inside one makes the classifier
    /// say "not a read", which is the safe side. A literal with a backslash in
    /// it is not parsed at all.
    /// </summary>
    private static string? StripComments(string sql)
    {
        var result = new System.Text.StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c is '\'' or '"')
            {
                var close = i + 1;
                while (true)
                {
                    close = sql.IndexOf(c, close);
                    if (close < 0) return null;
                    // A doubled quote is an escaped one, not the end.
                    if (close + 1 < sql.Length && sql[close + 1] == c) { close += 2; continue; }
                    break;
                }
                // A backslash may be an escape (an E'' string), which would move the
                // end of the literal; rather than parse those, it is not a read.
                if (sql.AsSpan(i, close - i + 1).Contains('\\')) return null;
                result.Append(sql, i, close - i + 1);
                i = close + 1;
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var newline = sql.IndexOf('\n', i);
                if (newline < 0) return null;
                result.Append(' ');
                i = newline + 1;
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                // Postgres block comments nest.
                var depth = 1;
                i += 2;
                while (depth > 0)
                {
                    if (i + 1 >= sql.Length) return null;
                    if (sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; }
                    else if (sql[i] == '*' && sql[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                result.Append(' ');
            }
            else
            {
                result.Append(c);
                i++;
            }
        }
        return result.ToString();
    }

    private void End(DbCommand command)
    {
        if (_counted.Remove(command)) _maintenance.EndJob();
    }

    /// <summary>
    /// Releases any count a command still holds when the scope ends. A reader
    /// whose close threw before <see cref="DataReaderDisposing"/> ran would
    /// otherwise hold its count forever, and every later restore would wait
    /// it out and refuse.
    /// </summary>
    public void Dispose()
    {
        foreach (var (command, _) in _counted.ToList())
        {
            End(command);
        }
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
