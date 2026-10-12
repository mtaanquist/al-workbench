namespace ALDevToolbox.Services.Diff;

/// <summary>
/// The app-wide cap on how many Diff tool diffs run at once
/// (<c>Endpoints/CompareEndpoints.cs</c>). The per-IP rate limit meters each
/// caller, but a diff cannot be cancelled once it starts, so this is what keeps
/// cores free for everyone's pages and the background workers however many
/// callers there are. Registered as a singleton.
/// </summary>
public sealed class CompareDiffGate
{
    /// <summary>Half the cores, at least one.</summary>
    public static readonly int DefaultSlots = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>
    /// How long a request waits for a slot before it is told the server is
    /// busy. Short, because the Diff tool sends a fresh request on the next
    /// edit and a late diff has usually been superseded by then.
    /// </summary>
    public static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(2);

    public CompareDiffGate(int slots)
    {
        Slots = slots;
        Semaphore = new SemaphoreSlim(slots, slots);
    }

    public int Slots { get; }

    /// <summary>The slots themselves; public so a test can hold them.</summary>
    public SemaphoreSlim Semaphore { get; }
}
