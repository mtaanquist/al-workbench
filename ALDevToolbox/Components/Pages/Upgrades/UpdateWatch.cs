using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;

namespace ALDevToolbox.Components.Pages.Upgrades;

/// <summary>
/// Watching an update through to its end (#982), for a page that shows environments and
/// lets somebody start their updates: the fleet table, and an upgrade's own page (#984).
///
/// <para>After "Start update" the person often stays on the page to see it finish before
/// calling the customer back. So an update that is under way is re-read from Business
/// Central - that one environment, two requests - every ten seconds until it ends. It is a
/// timer of its own, not a page's refresh poll: that one reads our database for a few
/// minutes after a Refresh, this one asks the customer's tenant about one environment for
/// as long as its update runs, up to a cap. Ticks go through the host's
/// <c>InvokeAsync</c>, which puts the read on the renderer's sync context - the same thing
/// that keeps it from colliding with a click handler on the circuit's one
/// <c>AppDbContext</c> - and skip while the host says it is busy (a run, or a dialog
/// open). See .design/environment-updates.md, "The page".</para>
///
/// <para>A plain class rather than a component: it draws nothing. The host draws
/// <see cref="Watching"/> and <see cref="Ended"/> on its rows, and is told after every
/// tick that changed something so it can redraw or re-read.</para>
/// </summary>
public sealed class UpdateWatch : IDisposable
{
    /// <summary>One update being watched.</summary>
    public sealed class Watched
    {
        public required int ProjectId { get; init; }

        /// <summary>When the update started, as near as the page knows, for "started 6 minutes ago".</summary>
        public required DateTime StartedUtc { get; init; }

        /// <summary>When the watch gives up if the update still hasn't ended.</summary>
        public required DateTime UntilUtc { get; init; }

        /// <summary>The version the update goes to, to tell a finished update from one that came back unchanged.</summary>
        public string? TargetVersion { get; init; }

        /// <summary>
        /// True once Business Central has been seen working on it. Until then a running
        /// environment on the old version is an update not picked up yet, not a failed one.
        /// </summary>
        public bool SeenBusy { get; set; }

        /// <summary>Reads in a row that got no answer; a few in a row stops the watch.</summary>
        public int Misses { get; set; }

        /// <summary>When it was last read, so a tick with more than it may read takes the longest-waiting first.</summary>
        public DateTime LastReadUtc { get; set; }
    }

    /// <summary>
    /// How a watch ended, shown under the row until the row is watched again or the page
    /// is left. <paramref name="Stopped"/> marks a watch that gave up rather than one that
    /// saw the update end; only Refresh starts that one again.
    /// </summary>
    public sealed record Ending(string Text, string Tone, bool ShowOperations = false, bool Stopped = false)
    {
        public string CssClass => $"upg-note upg-note--{Tone}";
    }

    /// <summary>How an update looks from one re-read.</summary>
    public enum Verdict { StillUpdating, Updated, Failed }

    public static readonly TimeSpan Every = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan EveryWhenMany = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan For = TimeSpan.FromMinutes(45);

    /// <summary>
    /// The most environments one tick reads. Above it the tick also slows to
    /// <see cref="EveryWhenMany"/>, so fifty updates started at once cost at most
    /// twenty requests every thirty seconds rather than a hundred every ten.
    /// </summary>
    public const int PerTick = 10;

    /// <summary>Reads in a row without an answer before the watch stops.</summary>
    private const int MaxMisses = 3;

    private readonly ProjectConnectionService _connection;
    private readonly ILogger _log;
    private readonly Func<Func<Task>, Task> _invoke;
    private readonly Func<bool> _paused;
    private readonly Func<int, UpgradeFleetRow?> _find;
    private readonly Action<UpgradeFleetRow> _replace;
    private readonly Func<bool, Task> _afterTick;
    private readonly string _restartHint;
    private readonly SemaphoreSlim? _gate;

    private readonly Dictionary<int, Watched> _watched = new();
    private readonly Dictionary<int, Ending> _ended = new();
    private readonly CancellationTokenSource _cts = new();
    private System.Threading.Timer? _timer;
    private TimeSpan _period;
    private bool _busy;
    private bool _disposed;

    /// <param name="connection">The host's own (scoped) connection service, which does the re-read and re-mirrors the row.</param>
    /// <param name="invoke">The host component's <c>InvokeAsync</c>, which every tick runs through.</param>
    /// <param name="paused">True while the host must not have its rows move: a run in flight, a dialog open, no access.</param>
    /// <param name="find">The host's current row for an environment, or null once it has gone from the page.</param>
    /// <param name="replace">Hands the host a row as the re-read left it.</param>
    /// <param name="afterTick">Runs once after every tick that read anything, told whether a row changed; the host redraws there (the "started 6 minutes ago" moves on even when nothing else does).</param>
    /// <param name="restartHint">
    /// What a watch that gave up tells the person to do to start it again, in the host's own
    /// words: the fleet page has a Refresh button, an upgrade's page re-reads on every change.
    /// </param>
    /// <param name="gate">
    /// The host's one lock on its scoped <c>DbContext</c>, for a host whose own writes can
    /// land while a tick is between two awaits (an upgrade's page, #984). A tick takes it
    /// without waiting and simply skips when a write holds it, and keeps it through
    /// <paramref name="afterTick"/>, so the host's re-read after a tick cannot meet a write
    /// either. Null for a host whose ticks and commands never overlap on the context.
    /// </param>
    public UpdateWatch(
        ProjectConnectionService connection,
        ILogger log,
        Func<Func<Task>, Task> invoke,
        Func<bool> paused,
        Func<int, UpgradeFleetRow?> find,
        Action<UpgradeFleetRow> replace,
        Func<bool, Task> afterTick,
        string restartHint = "Refresh to start again.",
        SemaphoreSlim? gate = null)
    {
        _restartHint = restartHint;
        _gate = gate;
        _connection = connection;
        _log = log;
        _invoke = invoke;
        _paused = paused;
        _find = find;
        _replace = replace;
        _afterTick = afterTick;
    }

    /// <summary>The updates being watched, by environment id.</summary>
    public IReadOnlyDictionary<int, Watched> Watching => _watched;

    /// <summary>
    /// True while a tick is reading or its host is re-reading after it: the host's controls
    /// wait, so a click cannot start a second query on the same context.
    /// </summary>
    public bool IsTicking => _busy;

    /// <summary>How each finished watch ended, by environment id.</summary>
    public IReadOnlyDictionary<int, Ending> Ended => _ended;

    /// <summary>
    /// True when Business Central is busy with the environment: its state says so, or its
    /// next update says it is running. Either is enough, because the two are read from
    /// different places and do not always change together. The same test the planned
    /// upgrades' Running state uses (<c>EnvironmentUpgradeLineState.IsUpdating</c>).
    /// </summary>
    public static bool IsUpdating(UpgradeFleetRow row) =>
        BcEnvironmentStatus.Classify(row.Status) == BcEnvironmentReadiness.Busy
        || ProjectConnectionService.IsUpdateUnderWay(row.NextUpdateStatus);

    /// <summary>
    /// Whether an update has ended, and how, from one fresh read of its row. It has ended
    /// when the environment is running again and the update is no longer under way; it
    /// went through when the environment is now on the version it was going to (or on a
    /// later one), and otherwise it failed. An environment that is running on the old
    /// version before Business Central was ever seen busy has simply not picked the update
    /// up yet.
    /// </summary>
    public static Verdict Judge(UpgradeFleetRow row, string? targetVersion, bool seenBusy)
    {
        if (BcEnvironmentStatus.Classify(row.Status) == BcEnvironmentReadiness.Failed) return Verdict.Failed;
        if (IsUpdating(row) || !BcEnvironmentStatus.IsRunning(row.Status)) return Verdict.StillUpdating;

        var reached = !string.IsNullOrWhiteSpace(targetVersion) && !string.IsNullOrWhiteSpace(row.Version)
            && ProjectConnectionService.CompareVersions(row.Version, targetVersion) >= 0;
        if (reached) return Verdict.Updated;
        if (!seenBusy) return Verdict.StillUpdating;
        return string.IsNullOrWhiteSpace(targetVersion) ? Verdict.Updated : Verdict.Failed;
    }

    /// <summary>
    /// Starts watching the updates a run has just started, from the rows the run's reload
    /// brought back. Returns the environments now watched, so the host can drop the run's
    /// "Update started" from them: the watch line takes over.
    /// </summary>
    public List<int> WatchStarted(IReadOnlyDictionary<int, DateTime> started)
    {
        var begun = new List<int>();
        foreach (var (environmentId, sentAt) in started)
        {
            if (_find(environmentId) is not { } row) continue;
            Begin(row, sentAt, seenBusy: IsUpdating(row));
            begun.Add(environmentId);
        }
        return begun;
    }

    /// <summary>
    /// Joins every row whose update is already under way - one a booking fired at 20:00,
    /// or one somebody else started - so opening the page finds it watched. Only rows the
    /// person may act on: the re-read asks for the same grant the update did.
    /// </summary>
    public void JoinUnderWay(IEnumerable<UpgradeFleetRow> rows)
    {
        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            if (!row.CanAct || row.IsSoftDeleted || _watched.ContainsKey(row.EnvironmentId) || !IsUpdating(row)) continue;
            if (_ended.TryGetValue(row.EnvironmentId, out var ended) && ended.Stopped) continue;

            // Business Central does not say when it started; the date it was set to run is
            // the nearest thing, once that date has come.
            var started = row.NextUpdateDate is { } at && at <= now ? at : now;
            Begin(row, started, seenBusy: true);
        }
    }

    /// <summary>Starts (or restarts) watching one environment's update.</summary>
    public void Begin(UpgradeFleetRow row, DateTime startedUtc, bool seenBusy)
    {
        _ended.Remove(row.EnvironmentId);
        _watched[row.EnvironmentId] = new Watched
        {
            ProjectId = row.ProjectId,
            StartedUtc = startedUtc,
            UntilUtc = DateTime.UtcNow.Add(For),
            TargetVersion = row.NextUpdateVersion,
            SeenBusy = seenBusy,
            LastReadUtc = DateTime.MinValue,
        };
        Schedule();
    }

    /// <summary>"Refresh restarts it": forgets the watches that gave up, so a reload may join them again.</summary>
    public void ForgetStopped()
    {
        foreach (var id in _ended.Where(e => e.Value.Stopped).Select(e => e.Key).ToList())
        {
            _ended.Remove(id);
        }
    }

    /// <summary>
    /// Stops watching everything, and forgets how earlier watches ended, without ending
    /// the watch for good: for a host whose rows go off screen (the Upgrades page leaving
    /// its Fleet view) and come back later to join afresh from a re-read.
    /// </summary>
    public void Clear()
    {
        _watched.Clear();
        _ended.Clear();
        Stop();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        // A read in flight is abandoned with the page rather than finishing into it.
        _cts.Cancel();
    }

    /// <summary>Starts, re-paces or stops the timer to suit how many are being watched.</summary>
    private void Schedule()
    {
        if (_watched.Count == 0 || _disposed)
        {
            Stop();
            return;
        }

        var period = _watched.Count > PerTick ? EveryWhenMany : Every;
        if (_timer is null)
        {
            _timer = new System.Threading.Timer(_ => _ = OnTimerAsync(), null, period, period);
        }
        else if (period != _period)
        {
            _timer.Change(period, period);
        }
        _period = period;
    }

    private void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private async Task OnTimerAsync()
    {
        try
        {
            await _invoke(TickAsync);
        }
        catch (ObjectDisposedException)
        {
            Stop();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The page went while a read was in flight.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A page stopped watching updates under way.");
            Stop();
        }
    }

    /// <summary>
    /// One tick: re-reads up to <see cref="PerTick"/> watched environments, the
    /// longest-waiting first, lays each answer over its row and ends the watches whose
    /// update has ended. Public so a host's tests can drive it without waiting on the
    /// timer; the host's own renders happen in <c>afterTick</c> and its caller.
    /// </summary>
    public async Task TickAsync()
    {
        // Never while a run owns the rows, and never under an open dialog: the rows a
        // person is reading there must not move under them.
        if (_busy || _paused() || _disposed || _watched.Count == 0) return;
        // A write of the host's own holds the gate: skip this tick, the next one comes.
        if (_gate is not null && !_gate.Wait(0)) return;
        _busy = true;
        var changed = false;
        try
        {
            var now = DateTime.UtcNow;
            foreach (var id in _watched.Where(w => now > w.Value.UntilUtc).Select(w => w.Key).ToList())
            {
                End(id, new Ending(
                    $"Stopped watching after {(int)For.TotalMinutes} minutes. {_restartHint}", "muted", Stopped: true));
            }

            var due = _watched.OrderBy(w => w.Value.LastReadUtc).Take(PerTick).ToList();
            foreach (var (environmentId, watch) in due)
            {
                if (_paused() || _disposed) break;
                watch.LastReadUtc = DateTime.UtcNow;
                changed |= await ReadAsync(environmentId, watch);
            }

            Schedule();
            if (!_disposed) await _afterTick(changed);
        }
        finally
        {
            _busy = false;
            _gate?.Release();
        }
    }

    /// <summary>
    /// Reads one watched environment. True when its row changed; a watch that stops without
    /// an answer changes no row, so it says false and the host only redraws.
    /// </summary>
    private async Task<bool> ReadAsync(int environmentId, Watched watch)
    {
        BcEnvironmentReading reading;
        try
        {
            reading = await _connection.RefreshEnvironmentAsync(watch.ProjectId, environmentId, _cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            throw;
        }
        catch (PlanValidationException ex) when (ex.Errors.ContainsKey("Environment"))
        {
            // Gone, or the connection needs setting up: asking again will not help.
            End(environmentId, new Ending("Stopped watching - " + UpgradeActionRunner.FirstMessage(ex), "warn", Stopped: true));
            return false;
        }
        catch (ProjectAccessDeniedException)
        {
            End(environmentId, new Ending(
                "Stopped watching - you can no longer change this solution's updates.", "muted", Stopped: true));
            return false;
        }
        catch (Exception ex)
        {
            if (ex is not PlanValidationException)
            {
                _log.LogWarning(ex, "A page couldn't re-read environment {EnvironmentId} while watching its update.", environmentId);
            }
            if (++watch.Misses >= MaxMisses)
            {
                End(environmentId, new Ending(
                    $"Stopped watching - Business Central isn't answering. {_restartHint}", "warn", Stopped: true));
                return false;
            }
            return false;
        }

        watch.Misses = 0;
        if (_find(environmentId) is not { } current)
        {
            _watched.Remove(environmentId);
            return false;
        }

        var row = reading.ApplyTo(current);
        _replace(row);
        if (IsUpdating(row)) watch.SeenBusy = true;

        switch (Judge(row, watch.TargetVersion, watch.SeenBusy))
        {
            case Verdict.Updated:
                End(environmentId, new Ending($"Updated to {UpgradeActionRunner.ShortVersion(row.Version)}", "ok"));
                _log.LogInformation("Watched update on environment {EnvironmentId} finished on {Version}.", environmentId, row.Version);
                break;
            case Verdict.Failed:
                End(environmentId, new Ending("Update failed", "bad", ShowOperations: true));
                _log.LogInformation("Watched update on environment {EnvironmentId} ended without reaching {Version}.", environmentId, watch.TargetVersion);
                break;
        }
        return true;
    }

    private void End(int environmentId, Ending end)
    {
        _watched.Remove(environmentId);
        _ended[environmentId] = end;
    }
}
