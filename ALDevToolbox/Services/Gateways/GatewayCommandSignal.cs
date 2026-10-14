using System.Collections.Concurrent;
using ALWorkbench.Gateway.Contracts;

namespace ALDevToolbox.Services.Gateways;

/// <summary>
/// The in-memory wake-up behind the long-poll: a gateway waiting on
/// <c>GET /gateway/commands/next</c> is woken the moment a command is queued for it,
/// instead of finding it on its next poll. A singleton, like the build queue and
/// the schedulers, which is why the app stays on one instance
/// (<c>.design/deployment.md</c>).
///
/// <para>It is a hint, never the truth: the table is. A waiter always re-reads the
/// table after waking, a lost signal costs at most one poll, and a spurious one
/// costs one query. See <c>.design/on-prem-gateway.md</c>, "Connection model".</para>
/// </summary>
public sealed class GatewayCommandSignal
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _waiters = new();

    /// <summary>
    /// How long the long-poll waits before answering 204. The protocol's 30 seconds;
    /// settable so a test does not have to wait that long.
    /// </summary>
    public TimeSpan LongPollTimeout { get; set; } = GatewayProtocol.LongPollTimeout;

    /// <summary>
    /// A task that completes the next time <see cref="Notify"/> is called for
    /// <paramref name="gatewayId"/>. Take it <em>before</em> reading the table, so a
    /// command queued between the read and the wait still wakes the caller.
    /// </summary>
    public Task WaitHandle(int gatewayId) =>
        _waiters.GetOrAdd(gatewayId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    /// <summary>Wakes everyone waiting for <paramref name="gatewayId"/>. Call after the command is committed.</summary>
    public void Notify(int gatewayId)
    {
        if (_waiters.TryRemove(gatewayId, out var waiter))
        {
            waiter.TrySetResult();
        }
    }
}
