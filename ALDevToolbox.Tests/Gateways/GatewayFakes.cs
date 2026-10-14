using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// An in-memory <see cref="ISigningKeyStore"/>: the real P-256 maths, without the
/// Windows key store. Records which account each key was granted to.
/// </summary>
internal sealed class FakeKeyStore : ISigningKeyStore
{
    private readonly Dictionary<string, byte[]> _keys = [];

    public Dictionary<string, string?> Granted { get; } = [];

    public IReadOnlyCollection<string> Names => _keys.Keys;

    public bool Exists(string name) => _keys.ContainsKey(name);

    public byte[] Create(string name, string? serviceAccount)
    {
        if (_keys.ContainsKey(name)) throw new InvalidOperationException("taken");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _keys[name] = key.ExportPkcs8PrivateKey();
        Granted[name] = serviceAccount;
        return key.ExportSubjectPublicKeyInfo();
    }

    public ECDsa Open(string name)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(_keys[name], out _);
        return key;
    }

    public byte[] ExportPublicKey(string name)
    {
        using var key = Open(name);
        return key.ExportSubjectPublicKeyInfo();
    }

    public void GrantAccess(string name, string serviceAccount) => Granted[name] = serviceAccount;

    public void Delete(string name) => _keys.Remove(name);

    /// <summary>Puts a key made elsewhere (by the e2e test's registration) in the store.</summary>
    public string Add(string name, ECDsa key)
    {
        _keys[name] = key.ExportPkcs8PrivateKey();
        return name;
    }
}

internal sealed class InMemorySettingsStore : IGatewaySettingsStore
{
    public GatewaySettings? Value { get; set; }

    public GatewaySettings? Load() => Value;

    public void Save(GatewaySettings settings) => Value = settings;

    public void Delete() => Value = null;
}

/// <summary>
/// A clock whose waits return at once, moving time on by what was asked: the loop's
/// whole schedule (back-off, heartbeat spacing, progress batching) runs in
/// milliseconds and every wait is recorded. Stops the test after
/// <see cref="MaxDelays"/> waits so a loop that never ends fails rather than hangs.
/// </summary>
internal sealed class FakeGatewayClock : IGatewayClock
{
    private readonly Lock _lock = new();
    private TimeSpan _elapsed;

    public FakeGatewayClock(DateTimeOffset? start = null)
    {
        Start = start ?? new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset Start { get; }

    public int MaxDelays { get; set; } = 50_000;

    /// <summary>
    /// A real pause on each wait. While a command runs, the loop's heartbeat waits
    /// return at once, so without it the loop would spin through
    /// <see cref="MaxDelays"/> before slow work (disk, the thread pool) finishes.
    /// </summary>
    public TimeSpan Pause { get; set; } = TimeSpan.FromMilliseconds(1);

    public ConcurrentQueue<TimeSpan> Delays { get; } = new();

    public DateTimeOffset UtcNow
    {
        get { lock (_lock) return Start + _elapsed; }
    }

    public long Timestamp()
    {
        lock (_lock) return _elapsed.Ticks;
    }

    public TimeSpan Elapsed(long timestamp)
    {
        lock (_lock) return _elapsed - TimeSpan.FromTicks(timestamp);
    }

    public void Advance(TimeSpan by)
    {
        lock (_lock) _elapsed += by;
    }

    public async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Delays.Count >= MaxDelays) throw new InvalidOperationException("The loop waited more often than the test allows; it is probably spinning.");
        Delays.Enqueue(delay);
        if (delay > TimeSpan.Zero) Advance(delay);
        if (Pause > TimeSpan.Zero) await Task.Delay(Pause, ct);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
    }
}

/// <summary>One request the gateway sent, as the fake workbench saw it.</summary>
internal sealed record SeenRequest(string Method, string Path, string? Body, string? Authorization)
{
    public T Json<T>() => JsonSerializer.Deserialize<T>(Body!)!;
}

/// <summary>
/// A fake AL Workbench: answers each request from <see cref="Respond"/> and records
/// what arrived. Helpers build the usual answers.
/// </summary>
internal sealed class FakeWorkbench : HttpMessageHandler
{
    public const string Url = "https://workbench.cronus.example";

    public ConcurrentQueue<SeenRequest> Requests { get; } = new();

    /// <summary>The answer to one request; throw <see cref="HttpRequestException"/> to play "unreachable".</summary>
    public Func<SeenRequest, HttpResponseMessage> Respond { get; set; } = _ => NoContent();

    public HttpClient Client() => new(this) { Timeout = Timeout.InfiniteTimeSpan };

    public IEnumerable<SeenRequest> To(string path) => Requests.Where(r => r.Path == path);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var seen = new SeenRequest(request.Method.Method, request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.ToString());
        Requests.Enqueue(seen);
        return Respond(seen);
    }

    public static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);

    public static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Bytes(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    public static HttpResponseMessage Refused(HttpStatusCode status, string reason) =>
        Json(new GatewayErrorResponse { Errors = new Dictionary<string, string> { ["authorization"] = reason } }, status);

    public static HttpResponseMessage Heartbeat(bool approved) => Json(new GatewayHeartbeatResponse { Approved = approved });
}

/// <summary>An executor that records what it was given and answers as told.</summary>
internal sealed class RecordingExecutor : ICommandExecutor
{
    public List<GatewayCommandContext> Calls { get; } = [];

    public Dictionary<long, byte[]> FileContents { get; } = [];

    public Func<GatewayCommandContext, CancellationToken, Task<GatewayCommandOutcome>> Run { get; set; } =
        (_, _) => Task.FromResult(GatewayCommandOutcome.Success());

    public async Task<GatewayCommandOutcome> ExecuteAsync(GatewayCommandContext context, CancellationToken ct)
    {
        Calls.Add(context);
        foreach (var (id, path) in context.Files) FileContents[id] = await File.ReadAllBytesAsync(path, ct);
        return await Run(context, ct);
    }
}

/// <summary>A scratch folder per test, removed afterwards.</summary>
internal sealed class ScratchFolder : IDisposable
{
    public ScratchFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aldt-gateway-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

/// <summary>Keeps every formatted log line, for asserting what customer IT would read.</summary>
internal sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
{
    public ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, int EventId, string Message)> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue((logLevel, eventId.Id, formatter(state, exception)));

    public IEnumerable<string> Messages(int eventId) => Lines.Where(l => l.EventId == eventId).Select(l => l.Message);
}

/// <summary>
/// A policy that allows every command and reports one valid tier, for the loop's
/// tests that are about something else. Records what it was asked.
/// </summary>
internal sealed class AllowEveryCommandPolicy : ALWorkbench.Gateway.Core.Policy.IGatewayPolicy
{
    public List<GatewayCommandMessage> Checked { get; } = [];

    public IReadOnlyList<string> StartProblems { get; set; } = [];

    public Func<GatewayCommandMessage, ALWorkbench.Gateway.Core.Policy.PolicyDecision> Decide { get; set; } =
        _ => ALWorkbench.Gateway.Core.Policy.PolicyDecision.Allow;

    public ALWorkbench.Gateway.Core.Policy.PolicySnapshot Snapshot { get; set; } = new(new GatewayPolicyReport { State = GatewayPolicyStates.Valid }, 1);

    public IReadOnlyList<string> Start() => StartProblems;

    public ALWorkbench.Gateway.Core.Policy.PolicySnapshot Refresh() => Snapshot;

    public ALWorkbench.Gateway.Core.Policy.PolicyDecision Check(GatewayCommandMessage command)
    {
        lock (Checked) Checked.Add(command);
        return Decide(command);
    }
}

/// <summary>An <see cref="ALWorkbench.Gateway.Core.Policy.IFileAccessCheck"/> told which paths are unsafe and which exist.</summary>
internal sealed class FakeFileAccess : ALWorkbench.Gateway.Core.Policy.IFileAccessCheck
{
    public Dictionary<string, string> Problems { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Asked { get; } = [];

    public string? Problem(string path)
    {
        Asked.Add(path);
        return Problems.GetValueOrDefault(path);
    }

    public bool Exists(string path) => Existing.Contains(path) || File.Exists(path);

    /// <summary>What <see cref="Contents"/> lists for a folder; a folder not given lists nothing.</summary>
    public Dictionary<string, List<string>> FolderContents { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Contents(string folder) => FolderContents.GetValueOrDefault(folder) ?? [];
}
