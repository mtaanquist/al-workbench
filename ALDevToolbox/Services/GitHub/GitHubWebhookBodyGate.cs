namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// The few slots <c>POST /github/webhook</c> reads a push body past a megabyte in
/// (#1126). The body has to be read before its signature can be checked, so a
/// stranger can take a slot with nothing more than a well-formed header; this keeps
/// what one can cost bounded (#1174).
///
/// <para>Four bounds, each answering one way the slots could be held: only a body
/// that declares no length or more than a megabyte takes a slot at all, so an
/// ordinary push never waits here; one source address holds at most one slot, so
/// holding them all takes as many addresses as there are slots; a delivery that
/// cannot get a slot within <see cref="WaitTimeout"/> is answered 503, which
/// GitHub's delivery log and <c>GitHubWebhookRecoveryScheduler</c> both treat as
/// worth sending again; and a read that has not finished within
/// <see cref="ReadDeadline"/> is abandoned, so a body trickled in byte by byte gives
/// its slot back. Singleton, because every request has to see the same slots.</para>
/// </summary>
public sealed class GitHubWebhookBodyGate
{
    /// <summary>How many large push bodies may be read at once.</summary>
    public const int DefaultSlots = 4;

    private readonly SemaphoreSlim _slots;
    private readonly HashSet<string> _sources = new(StringComparer.Ordinal);
    private readonly Lock _sourcesLock = new();

    public GitHubWebhookBodyGate()
        // GitHub gives up on a delivery after ten seconds, so a read still running
        // past that is one GitHub has already logged as failed and will be resent.
        : this(DefaultSlots, waitTimeout: TimeSpan.FromSeconds(2), readDeadline: TimeSpan.FromSeconds(10))
    {
    }

    /// <summary>Test seam: the same gate with timings a test can wait out.</summary>
    internal GitHubWebhookBodyGate(int slots, TimeSpan waitTimeout, TimeSpan readDeadline)
    {
        _slots = new SemaphoreSlim(slots, slots);
        WaitTimeout = waitTimeout;
        ReadDeadline = readDeadline;
    }

    /// <summary>How long a delivery waits for a slot before it is answered 503.</summary>
    public TimeSpan WaitTimeout { get; }

    /// <summary>How long a gated body may take to arrive before the read is abandoned.</summary>
    public TimeSpan ReadDeadline { get; }

    /// <summary>Slots free right now. For tests and diagnostics.</summary>
    public int Available => _slots.CurrentCount;

    /// <summary>
    /// Takes a slot for <paramref name="source"/>, or returns <see langword="false"/>
    /// at once when that source already holds or is waiting for one, and after
    /// <see cref="WaitTimeout"/> when no slot came free. A <see langword="true"/>
    /// must be paired with <see cref="Release"/> for the same source.
    /// </summary>
    public async Task<bool> TryEnterAsync(string source, CancellationToken ct)
    {
        lock (_sourcesLock)
        {
            if (!_sources.Add(source)) return false;
        }

        var entered = false;
        try
        {
            entered = await _slots.WaitAsync(WaitTimeout, ct);
            return entered;
        }
        finally
        {
            if (!entered) Forget(source);
        }
    }

    /// <summary>Gives back the slot <paramref name="source"/> took with <see cref="TryEnterAsync"/>.</summary>
    public void Release(string source)
    {
        Forget(source);
        _slots.Release();
    }

    private void Forget(string source)
    {
        lock (_sourcesLock)
        {
            _sources.Remove(source);
        }
    }
}
