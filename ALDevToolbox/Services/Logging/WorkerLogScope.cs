namespace ALDevToolbox.Services.Logging;

/// <summary>
/// Names the background worker running in this async flow, so every line logged
/// under it is stored against that worker, including lines from the services the
/// worker calls. The worker bases enter it for their whole loop; see
/// <see cref="Workers.PolledScheduler"/> and <see cref="Workers.QueueDrainWorker{TJob}"/>.
/// </summary>
public static class WorkerLogScope
{
    private static readonly AsyncLocal<string?> _current = new();

    /// <summary>The worker's heartbeat name, or null outside a worker.</summary>
    public static string? Current => _current.Value;

    /// <summary>
    /// A worker's name without its number: "ProjectBuildWorker 3" is a ProjectBuildWorker.
    /// The Logs page filters by this, so numbered siblings share one choice.
    /// </summary>
    public static string Family(string worker)
    {
        var space = worker.IndexOf(' ');
        return space < 0 ? worker : worker[..space];
    }

    /// <summary>Names <paramref name="worker"/> until the returned scope is disposed.</summary>
    public static IDisposable Enter(string worker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        var previous = _current.Value;
        _current.Value = worker;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly string? _previous;

        public Restore(string? previous) => _previous = previous;

        public void Dispose() => _current.Value = _previous;
    }
}
