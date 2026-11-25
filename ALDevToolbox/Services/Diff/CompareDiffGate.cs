namespace ALDevToolbox.Services.Diff;

/// <summary>
/// The app-wide cap on how many Diff tool diffs run at once
/// (<c>Endpoints/CompareEndpoints.cs</c>), and, as a separate keyed instance
/// (<see cref="FileCompareKey"/>), on the Object Explorer's file compare page. The per-IP rate limit meters each
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

    /// <summary>
    /// Service key of the second instance, for the Object Explorer's file
    /// compare page. Kept apart from the anonymous tool's so a burst of Diff
    /// tool requests cannot hold every slot a signed-in reader needs (#1339).
    /// </summary>
    public const string FileCompareKey = "object-explorer-file-compare";

    /// <summary>
    /// Slots for the file compare page's gate: a quarter of the cores, at
    /// least one. Kept small so this page and the Diff tool together leave
    /// most cores for every other page and the background workers.
    /// </summary>
    public static readonly int FileCompareSlots = Math.Max(1, Environment.ProcessorCount / 4);

    /// <summary>
    /// How long the file compare page waits for a slot. Longer than
    /// <see cref="SlotWait"/>: that page is one request per click, not one
    /// per keystroke, so waiting is better than a "busy" page.
    /// </summary>
    public static readonly TimeSpan FileCompareSlotWait = TimeSpan.FromSeconds(10);

    /// <param name="slots">How many diffs may run at once.</param>
    /// <param name="slotWait">How long a caller waits for a slot; <see cref="SlotWait"/> when omitted.</param>
    public CompareDiffGate(int slots, TimeSpan? slotWait = null)
    {
        Slots = slots;
        Wait = slotWait ?? SlotWait;
        Semaphore = new SemaphoreSlim(slots, slots);
    }

    public int Slots { get; }

    /// <summary>How long a caller of this gate waits for a slot before giving up.</summary>
    public TimeSpan Wait { get; }

    /// <summary>The slots themselves; public so a test can hold them.</summary>
    public SemaphoreSlim Semaphore { get; }
}
