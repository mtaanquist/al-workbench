namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// The few slots <c>POST /github/webhook</c> reads a push body past a megabyte in
/// (#1126). The body has to be read before its signature can be checked, so a
/// stranger can take a slot with nothing more than a well-formed header; this keeps
/// what one can cost bounded (#1174).
///
/// <para>Three bounds, each answering one way a slot could be held: only a body
/// that declares no length or more than a megabyte takes a slot at all, so an
/// ordinary push never waits here; a delivery that cannot get a slot within
/// <see cref="WaitTimeout"/> is answered 503, which GitHub's delivery log and
/// <c>GitHubWebhookRecoveryScheduler</c> both treat as worth sending again; and a
/// read that has not finished within <see cref="ReadDeadline"/> is abandoned, so a
/// body trickled in byte by byte gives its slot back. Singleton, because every
/// request has to see the same slots.</para>
/// </summary>
public sealed class GitHubWebhookBodyGate
{
    /// <summary>How many large push bodies may be read at once.</summary>
    public const int DefaultSlots = 4;

    private readonly SemaphoreSlim _slots;

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
    /// Takes a slot, or returns <see langword="false"/> when none came free within
    /// <see cref="WaitTimeout"/>. A <see langword="true"/> must be paired with
    /// <see cref="Release"/>.
    /// </summary>
    public Task<bool> TryEnterAsync(CancellationToken ct) => _slots.WaitAsync(WaitTimeout, ct);

    /// <summary>Gives back a slot taken by <see cref="TryEnterAsync"/>.</summary>
    public void Release() => _slots.Release();
}
