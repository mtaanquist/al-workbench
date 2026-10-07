using System.Net;
using System.Net.Sockets;
using IPNetwork = System.Net.IPNetwork;

namespace ALDevToolbox.Services.GitHub;

/// <summary>What <see cref="GitHubHookAddressAllowList.Check"/> made of one sender.</summary>
public enum GitHubHookAddressVerdict
{
    /// <summary>No list has been loaded yet, so the sender cannot be judged and is let through.</summary>
    NotLoaded,

    /// <summary>The sender is inside one of GitHub's published webhook ranges.</summary>
    Allowed,

    /// <summary>A list is loaded and the sender is not in it, or has no address at all.</summary>
    Refused,
}

/// <summary>A refused delivery: who sent it (when it had an address) and when.</summary>
public sealed record GitHubHookAddressRefusal(string? Sender, DateTimeOffset At);

/// <summary>
/// The address ranges GitHub sends webhook deliveries from (the <c>hooks</c> list of
/// <c>GET /meta</c>), held in memory so <c>POST /github/webhook</c> can turn a stranger
/// away before it reads a byte of the body or takes a large-body slot (#1201). The
/// slots of #1174 bound what one address can hold; this closes the gap that leaves,
/// someone with many addresses holding every slot with forged slow pushes.
///
/// <para>Filled by <see cref="GitHubHookAddressRefreshScheduler"/>, at start-up and
/// daily. Until the first load succeeds every sender is let through
/// (<see cref="GitHubHookAddressVerdict.NotLoaded"/>): refusing then would turn a
/// GitHub outage or a host with no outbound route into lost deliveries. A failed or
/// empty refresh never replaces a list that loaded.</para>
///
/// <para>The check is only as good as the client address the request carries, which
/// behind a reverse proxy is the forwarded one and only when the proxy is listed in
/// <c>TRUSTED_PROXIES</c>; see <c>.design/github-integration-phase2.md</c>.
/// Singleton, because every request reads the same list.</para>
/// </summary>
public sealed class GitHubHookAddressAllowList
{
    /// <summary>The least time between two warnings of one kind, so a flood of refusals is a line, not a log.</summary>
    internal static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _clock;

    // Swapped whole, never edited, so a reader sees one list or the other.
    private volatile Loaded? _current;

    private long _nextRefusalWarningTicks;
    private long _nextUnloadedWarningTicks;
    private long _refusalsSinceWarning;

    // The last refused sender, kept for the site admin's GitHub page: behind a proxy
    // that is not trusted, every delivery is refused from the proxy's own address,
    // and a rate-limited log line is easy to miss.
    private volatile GitHubHookAddressRefusal? _lastRefusal;

    private sealed record Loaded(IReadOnlyList<IPNetwork> Ranges, DateTimeOffset LoadedAt);

    public GitHubHookAddressAllowList(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>When the list in use was loaded, or <see langword="null"/> when none ever was.</summary>
    public DateTimeOffset? LoadedAt => _current?.LoadedAt;

    /// <summary>The ranges in use; empty when none was ever loaded.</summary>
    public IReadOnlyList<IPNetwork> Ranges => _current?.Ranges ?? [];

    /// <summary>The last delivery refused since start-up, or <see langword="null"/> when none was.</summary>
    public GitHubHookAddressRefusal? LastRefusal => _lastRefusal;

    /// <summary>
    /// Whether <paramref name="address"/> may deliver. An IPv4 address that arrived
    /// mapped into IPv6 (a dual-stack listener reports <c>::ffff:192.30.252.1</c>) is
    /// judged as the IPv4 address it is. A missing address is refused once a list is
    /// loaded: there is nothing to vouch for it.
    /// </summary>
    public GitHubHookAddressVerdict Check(IPAddress? address)
    {
        var loaded = _current;
        if (loaded is null) return GitHubHookAddressVerdict.NotLoaded;
        if (address is null) return GitHubHookAddressVerdict.Refused;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        foreach (var range in loaded.Ranges)
        {
            if (range.Contains(address)) return GitHubHookAddressVerdict.Allowed;
        }
        return GitHubHookAddressVerdict.Refused;
    }

    /// <summary>
    /// Puts <paramref name="ranges"/> in use. An empty list is refused rather than
    /// installed, since it would turn every delivery away; the caller keeps the list it had.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="ranges"/> is empty.</exception>
    public void Replace(IReadOnlyCollection<IPNetwork> ranges)
    {
        if (ranges.Count == 0)
        {
            throw new ArgumentException("An empty address list would refuse every delivery.", nameof(ranges));
        }
        _current = new Loaded(ranges.ToArray(), _clock.GetUtcNow());
    }

    /// <summary>
    /// Reads GitHub's CIDR strings. An entry that does not parse is set aside rather
    /// than failing the whole list, so one odd entry cannot cost the other ranges.
    /// </summary>
    public static (List<IPNetwork> Ranges, List<string> Invalid) Parse(IEnumerable<string> entries)
    {
        var ranges = new List<IPNetwork>();
        var invalid = new List<string>();
        foreach (var entry in entries)
        {
            var trimmed = entry.Trim();
            if (IPNetwork.TryParse(trimmed, out var range))
            {
                ranges.Add(range);
            }
            else if (IPAddress.TryParse(trimmed, out var single))
            {
                // A bare address is a range of one.
                ranges.Add(new IPNetwork(single, single.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
            }
            else
            {
                invalid.Add(entry);
            }
        }
        return (ranges, invalid);
    }

    /// <summary>
    /// Counts one refusal of <paramref name="sender"/> and says whether it should be
    /// logged, at most once per <see cref="WarningInterval"/>. When it should,
    /// <paramref name="suppressed"/> is how many refusals went unlogged since the last
    /// warning. The refusal is also kept as <see cref="LastRefusal"/>, which the site
    /// admin's GitHub page shows and which asks
    /// <see cref="GitHubHookAddressRefreshScheduler"/> for an early read of the list.
    /// </summary>
    public bool ShouldWarnRefusal(IPAddress? sender, out long suppressed)
    {
        _lastRefusal = new GitHubHookAddressRefusal(sender?.ToString(), _clock.GetUtcNow());
        Interlocked.Increment(ref _refusalsSinceWarning);
        if (!Claim(ref _nextRefusalWarningTicks))
        {
            suppressed = 0;
            return false;
        }
        // This refusal is the one logged; the rest of the count went unlogged.
        suppressed = Math.Max(0, Interlocked.Exchange(ref _refusalsSinceWarning, 0) - 1);
        return true;
    }

    /// <summary>Whether a delivery let through unchecked should be logged, at most once per <see cref="WarningInterval"/>.</summary>
    public bool ShouldWarnUnloaded() => Claim(ref _nextUnloadedWarningTicks);

    private bool Claim(ref long nextTicks)
    {
        var now = _clock.GetUtcNow().UtcTicks;
        var due = Interlocked.Read(ref nextTicks);
        if (now < due) return false;
        return Interlocked.CompareExchange(ref nextTicks, now + WarningInterval.Ticks, due) == due;
    }
}
