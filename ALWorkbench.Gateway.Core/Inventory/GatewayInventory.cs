using System.Text.Json;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Policy;

namespace ALWorkbench.Gateway.Core.Inventory;

/// <summary>How one round of inventory went.</summary>
/// <param name="Read">Tiers read, at least in part.</param>
/// <param name="Failed">Tiers nothing could be read from (each is still reported, with why).</param>
/// <param name="Posts">Reports sent to AL Workbench.</param>
/// <param name="Refused">
/// One line per tier AL Workbench would not take (a refusal, a 5xx on that report, or
/// a conflict that did not clear), for the log. The round goes on to the next tier.
/// </param>
public sealed record InventoryRunSummary(int Read, int Failed, int Posts, IReadOnlyList<string> Refused)
{
    public InventoryRunSummary(int read, int failed, int posts) : this(read, failed, posts, []) { }

    public bool Equals(InventoryRunSummary? other) => other is not null
        && Read == other.Read && Failed == other.Failed && Posts == other.Posts && Refused.SequenceEqual(other.Refused);

    public override int GetHashCode() => HashCode.Combine(Read, Failed, Posts, Refused.Count);
}

/// <summary>
/// One round of inventory: read each tier the policy file lets the gateway read, and
/// send each to AL Workbench as soon as it is read, so one slow tier holds back no
/// other and a stop halfway loses only the tiers not reached. Runs on the gateway's
/// own schedule (<see cref="GatewayInventoryLimits.Interval"/>, at start, and after a
/// command) and for a <c>refresh-inventory</c> command. See
/// <c>.design/on-prem-gateway.md</c>, "Inventory, as built".
/// </summary>
public sealed class GatewayInventory
{
    /// <summary>Tries of one post that AL Workbench answers with 409 (two writes of a new row at once).</summary>
    public const int ConflictAttempts = 3;

    private readonly IInventoryReader _reader;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public GatewayInventory(IInventoryReader reader, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _reader = reader;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Reads and sends. With <paramref name="only"/> set, reads that one tier and
    /// sends no roster, so nothing else is marked missing; otherwise every tier that
    /// allows <c>inventory</c>, each report carrying the full roster. With no such tier,
    /// one report with an empty roster is sent, so AL Workbench marks the tiers it knew
    /// missing.
    ///
    /// <para>A tier AL Workbench refuses (4xx, or a 5xx it answered for that report)
    /// is listed in <see cref="InventoryRunSummary.Refused"/> and the round goes on, so
    /// one bad tier never holds back the others nor makes the round retry for ever. A
    /// 409 is a race between two writes of a new row and is tried again. Only an
    /// AL Workbench that cannot be reached at all, or that refuses the gateway
    /// itself (credentials, approval, version), ends the round: the caller decides
    /// what that means.</para>
    /// </summary>
    public async Task<InventoryRunSummary> RunAsync(
        GatewayPolicy policy,
        TierRef? only,
        Func<GatewayInventoryReport, CancellationToken, Task> post,
        Action<string>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(post);
        var readable = policy.Instances.Where(i => i.Allows(PolicyPermission.Inventory)).ToList();
        IReadOnlyList<GatewayPolicyTier>? roster = only is null
            ? readable.Take(GatewayInventoryLimits.MaxRosterTiers).Select(i => i.Tier.ToReport()).ToList()
            : null;
        var tiers = only is null ? readable : readable.Where(i => i.Tier.Equals(only)).ToList();

        int read = 0, failed = 0, posts = 0;
        var refused = new List<string>();
        if (tiers.Count == 0 && roster is not null)
        {
            var problem = await TryPostAsync(new GatewayInventoryReport { Roster = roster }, post, ct).ConfigureAwait(false);
            return problem is null
                ? new InventoryRunSummary(0, 0, 1)
                : new InventoryRunSummary(0, 0, 0, [$"The list of service tiers: {problem}"]);
        }
        foreach (var tier in tiers.Take(GatewayInventoryLimits.MaxRosterTiers))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke($"Reading {tier.Tier}...");
            var inventory = await _reader.ReadAsync(tier, null, ct).ConfigureAwait(false);
            if (inventory.Error is null)
            {
                read++;
                progress?.Invoke(Describe(inventory));
            }
            else
            {
                failed++;
                progress?.Invoke($"Could not read {tier.Tier}: {inventory.Error}");
            }
            foreach (var part in InventoryChunker.Split(inventory, GatewayInventoryLimits.TargetPostBytes))
            {
                var problem = await TryPostAsync(new GatewayInventoryReport { Roster = roster, Instances = [part] }, post, ct).ConfigureAwait(false);
                if (problem is not null)
                {
                    refused.Add($"{tier.Tier}: {problem}");
                    progress?.Invoke($"AL Workbench did not take what was read from {tier.Tier}: {problem}");
                    break;
                }
                posts++;
            }
        }
        return new InventoryRunSummary(read, failed, posts, refused);
    }

    /// <summary>
    /// A 5xx AL Workbench answered itself is about this report: sending it again gets
    /// the same answer. 502, 503 and 504 come from in front of it while it is down or
    /// restarting, so they end the round and it is tried again soon.
    /// </summary>
    private static bool FailsThisReport(System.Net.HttpStatusCode? status) =>
        status is >= System.Net.HttpStatusCode.InternalServerError
            and not System.Net.HttpStatusCode.BadGateway
            and not System.Net.HttpStatusCode.ServiceUnavailable
            and not System.Net.HttpStatusCode.GatewayTimeout;

    /// <summary>Null when the post was taken; otherwise why not, for a refusal that is about this report alone.</summary>
    private async Task<string?> TryPostAsync(GatewayInventoryReport report,
        Func<GatewayInventoryReport, CancellationToken, Task> post, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await post(report, ct).ConfigureAwait(false);
                return null;
            }
            catch (CommandClosedException ex)
            {
                // 409 on this route: another post created the same new row first.
                if (attempt >= ConflictAttempts) return ex.Message;
                await _delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
            }
            catch (WorkbenchRefusedException ex)
            {
                return $"{(int)ex.Status}: {ex.Message}";
            }
            catch (WorkbenchUnreachableException ex) when (FailsThisReport(ex.Status))
            {
                return ex.Message;
            }
        }
    }

    /// <summary>One line for the command's log: what was found, and what could not be read.</summary>
    internal static string Describe(GatewayInstanceInventory inventory)
    {
        var parts = new List<string> { inventory.State ?? "state unknown" };
        if (inventory.Version is not null) parts.Add("version " + inventory.Version);
        if (inventory.Tenants is { } tenants)
        {
            parts.Add(tenants.Count == 1 ? "1 tenant" : $"{tenants.Count} tenants");
            var apps = tenants.Sum(t => t.Apps?.Count ?? 0);
            parts.Add(apps == 1 ? "1 app" : $"{apps} apps");
        }
        var line = $"Read {inventory.Machine} / {inventory.Name}: {string.Join(", ", parts)}.";
        if (inventory.Unavailable.Count > 0)
        {
            line += " Not read: " + string.Join("; ", inventory.Unavailable.Select(u => $"{u.Key} ({u.Value})"));
        }
        return line;
    }
}

/// <summary>
/// Keeps each inventory post under a size by spreading a tier's apps over several
/// posts. Every part repeats the tier itself and its full tenant list; each tenant's
/// apps travel in exactly one part, and are null in the others, which AL Workbench
/// reads as "not in this post" and leaves alone. A tier that fits is sent as it is.
/// </summary>
public static class InventoryChunker
{
    public static IReadOnlyList<GatewayInstanceInventory> Split(GatewayInstanceInventory inventory, int targetBytes)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (inventory.Tenants is not { Count: > 0 } tenants || Size(inventory) <= targetBytes) return [inventory];

        var header = inventory with { Tenants = tenants.Select(t => t with { Apps = null }).ToList() };
        var headerSize = Size(header);
        var parts = new List<GatewayInstanceInventory>();
        var withApps = new HashSet<int>();
        var size = headerSize;
        for (var i = 0; i < tenants.Count; i++)
        {
            if (tenants[i].Apps is null) continue;
            var appsSize = JsonSerializer.SerializeToUtf8Bytes(tenants[i].Apps).Length;
            if (withApps.Count > 0 && size + appsSize > targetBytes)
            {
                parts.Add(Part(tenants, withApps));
                withApps.Clear();
                size = headerSize;
            }
            withApps.Add(i);
            size += appsSize;
        }
        parts.Add(Part(tenants, withApps));
        return parts;

        GatewayInstanceInventory Part(IReadOnlyList<GatewayTenantInventory> all, HashSet<int> keep) => inventory with
        {
            Tenants = all.Select((t, i) => keep.Contains(i) ? t : t with { Apps = null }).ToList(),
        };
    }

    private static int Size(GatewayInstanceInventory inventory) => JsonSerializer.SerializeToUtf8Bytes(inventory).Length;
}
