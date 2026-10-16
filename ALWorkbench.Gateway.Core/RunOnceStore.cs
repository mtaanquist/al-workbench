using System.Text.Json;
using System.Text.Json.Serialization;
using ALWorkbench.Gateway.Contracts;

namespace ALWorkbench.Gateway.Core;

/// <summary>
/// The ids of commands this gateway has started, kept across restarts, so it never
/// runs one twice and can say which ones a restart cut short. A command is recorded
/// as started before anything is done for it, and as finished once its result is
/// reported. See <c>.design/on-prem-gateway.md</c>, "Run once, across restarts".
/// </summary>
public interface IRunOnceStore
{
    /// <summary>True when <paramref name="commandId"/> was ever started here, finished or not.</summary>
    bool HasStarted(long commandId);

    /// <summary>Records the start. On disk when this returns.</summary>
    void MarkStarted(long commandId);

    /// <summary>Records that the command needs nothing more from this gateway. Forgets a saved result.</summary>
    void MarkFinished(long commandId);

    /// <summary>
    /// Keeps the command's result until <see cref="MarkFinished"/>, so a stop between
    /// the verb finishing and the result arriving is reported with the real result on
    /// the next start, not as interrupted. On disk when this returns.
    /// </summary>
    void SaveResult(long commandId, GatewayCommandResultRequest result);

    /// <summary>Results saved and not yet confirmed as reported, by command id.</summary>
    IReadOnlyDictionary<long, GatewayCommandResultRequest> PendingResults();

    /// <summary>
    /// Commands started and never finished, without a saved result: read at start-up,
    /// these are the ones a stop cut short.
    /// </summary>
    IReadOnlyList<long> Unfinished();
}

/// <summary>
/// <see cref="IRunOnceStore"/> as a JSON file in the gateway's state folder. Finished
/// entries are forgotten after <see cref="Retention"/>; by then the workbench has long
/// stopped being able to hand the command out (it is claimed once, and expires).
/// </summary>
public sealed class FileRunOnceStore : IRunOnceStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly IGatewayClock _clock;
    private readonly Lock _lock = new();
    private readonly Dictionary<long, Entry> _entries;

    public FileRunOnceStore(string path, IGatewayClock clock)
    {
        _path = path;
        _clock = clock;
        _entries = Read(path);
    }

    public bool HasStarted(long commandId)
    {
        lock (_lock) return _entries.ContainsKey(commandId);
    }

    public void MarkStarted(long commandId)
    {
        lock (_lock)
        {
            if (_entries.ContainsKey(commandId)) throw new InvalidOperationException($"Command {commandId} was already started.");
            _entries[commandId] = new Entry { StartedAt = _clock.UtcNow };
            Save();
        }
    }

    public void MarkFinished(long commandId)
    {
        lock (_lock)
        {
            var entry = _entries.GetValueOrDefault(commandId) ?? new Entry { StartedAt = _clock.UtcNow };
            _entries[commandId] = entry with { FinishedAt = _clock.UtcNow, Result = null };
            Save();
        }
    }

    public void SaveResult(long commandId, GatewayCommandResultRequest result)
    {
        lock (_lock)
        {
            var entry = _entries.GetValueOrDefault(commandId) ?? new Entry { StartedAt = _clock.UtcNow };
            _entries[commandId] = entry with { Result = result };
            Save();
        }
    }

    public IReadOnlyDictionary<long, GatewayCommandResultRequest> PendingResults()
    {
        lock (_lock)
        {
            return _entries.Where(e => e.Value is { FinishedAt: null, Result: not null })
                .OrderBy(e => e.Key)
                .ToDictionary(e => e.Key, e => e.Value.Result!);
        }
    }

    public IReadOnlyList<long> Unfinished()
    {
        lock (_lock) return _entries.Where(e => e.Value is { FinishedAt: null, Result: null }).Select(e => e.Key).Order().ToList();
    }

    private void Save()
    {
        var cutoff = _clock.UtcNow - Retention;
        foreach (var stale in _entries.Where(e => e.Value.FinishedAt < cutoff).Select(e => e.Key).ToList())
        {
            _entries.Remove(stale);
        }
        AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(new FileShape { Commands = _entries }, Options));
    }

    private static Dictionary<long, Entry> Read(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<FileShape>(File.ReadAllBytes(path), Options)?.Commands ?? [];
        }
        catch (JsonException ex)
        {
            // Starting with an empty list would let a command run twice. Refuse instead,
            // and say what to do.
            throw new InvalidOperationException(
                $"The gateway's list of started commands ({path}) is damaged, so it cannot tell which commands it already ran. " +
                "Check it, or move it aside if you are sure nothing was running, and start the service again.", ex);
        }
    }

    private sealed record FileShape
    {
        [JsonPropertyName("commands")]
        public Dictionary<long, Entry> Commands { get; init; } = [];
    }

    private sealed record Entry
    {
        [JsonPropertyName("startedAt")]
        public DateTimeOffset StartedAt { get; init; }

        [JsonPropertyName("finishedAt")]
        public DateTimeOffset? FinishedAt { get; init; }

        /// <summary>The result, kept from when the verb finished until AL Workbench has it.</summary>
        [JsonPropertyName("result")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public GatewayCommandResultRequest? Result { get; init; }
    }
}
