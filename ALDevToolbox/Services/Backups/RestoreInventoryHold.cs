using System.Text.Json.Nodes;

namespace ALDevToolbox.Services.Backups;

/// <summary>
/// The <c>backups</c> and <c>per_tenant_backups</c> rows as JSON arrays (one
/// object per row, keyed by column name), read before a restore drops them.
/// </summary>
internal sealed record BackupInventory(string Backups, string PerTenantBackups)
{
    /// <summary>
    /// <paramref name="kept"/>, plus every row of <paramref name="current"/>
    /// whose file it does not already list. Kept rows win, because they are the
    /// full ones from before a failed restore; current rows only add files that
    /// arrived since. A current row whose id a kept row already uses gets the
    /// next free id, since both lists go back into the same table.
    /// </summary>
    public static BackupInventory Merge(BackupInventory kept, BackupInventory current) =>
        new(MergeTable(kept.Backups, current.Backups), MergeTable(kept.PerTenantBackups, current.PerTenantBackups));

    private static string MergeTable(string keptJson, string currentJson)
    {
        var kept = JsonNode.Parse(keptJson)!.AsArray();
        var current = JsonNode.Parse(currentJson)!.AsArray();
        var files = kept.Select(r => (string?)r!["file_name"]).ToHashSet(StringComparer.Ordinal);
        var ids = kept.Select(r => (long)r!["id"]!).ToHashSet();
        var nextId = ids.Concat(current.Select(r => (long)r!["id"]!)).DefaultIfEmpty(0).Max() + 1;

        var merged = new JsonArray();
        foreach (var row in kept) merged.Add(row!.DeepClone());
        foreach (var row in current)
        {
            if (files.Contains((string?)row!["file_name"])) continue;
            var copy = row.DeepClone();
            if (ids.Contains((long)copy["id"]!)) copy["id"] = nextId++;
            merged.Add(copy);
        }
        return merged.ToJsonString();
    }
}

/// <summary>
/// Process-wide memory of the backup inventory across a failed full restore.
/// After a failure that empties the database, putting the inventory back has
/// to leave out per-tenant snapshots (their organisations are gone) and clear
/// missing creators; without this, the next successful restore would capture
/// that degraded list and write it back, losing those snapshots from the UI
/// for good. Held from the first failure until a restore succeeds. Like
/// <see cref="Operations.MaintenanceModeState"/> it is not persisted: a restart
/// forgets it, along with the maintenance hold itself. See #1321.
/// </summary>
public sealed class RestoreInventoryHold
{
    private volatile BackupInventory? _held;

    internal BackupInventory? Held => _held;

    internal void Hold(BackupInventory inventory) => _held = inventory;

    internal void Clear() => _held = null;
}
