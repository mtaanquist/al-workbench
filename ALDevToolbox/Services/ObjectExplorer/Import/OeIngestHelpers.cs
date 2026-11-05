using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Persistence helpers shared by the Object Explorer ingest paths
/// (<see cref="ReleaseImportService"/> for AL <c>.app</c> packages and
/// <see cref="CalImportService"/> for legacy C/AL TXT exports). Factored out
/// once the second ingest path needed the same content-addressed blob store
/// and the same per-flush chunking discipline.
/// </summary>
internal static class OeIngestHelpers
{
    /// <summary>
    /// Rows-per-<c>SaveChanges</c> for source files. Kept small because the
    /// file loop also buffers the pending source blobs for
    /// <see cref="UpsertFileContentsAsync"/>, and Base App carries several
    /// thousand files with multi-KB content each — the chunk bounds that
    /// buffer, not the EF batch. The C/AL path shares the same envelope
    /// (a W1+DK export is ~5k-8k objects).
    /// </summary>
    public const int FileChunkSize = 50;

    /// <summary>
    /// Rows-per-<c>SaveChanges</c> for objects and their emitted symbols,
    /// variables and references. These are narrow rows and the change tracker
    /// is cleared after every flush, so the footprint is per-chunk and 50 was
    /// far below what the tracker comfortably holds; each flush also cost a
    /// round trip, which dominated a millions-of-rows ingest. See #688.
    /// The C/AL system-reference backfill pages the same number of objects
    /// with their source blobs attached, so this also sets that read's
    /// working set — a few MB per chunk, still comfortably bounded.
    /// </summary>
    public const int ObjectChunkSize = 300;

    /// <summary>SHA-256 of the UTF-8 bytes of <paramref name="content"/>, as uppercase hex.</summary>
    public static string HashHex(string content)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash);
    }

    /// <summary>1-based line count (a non-empty string has at least one line).</summary>
    public static int CountLines(string content)
    {
        if (string.IsNullOrEmpty(content)) return 0;
        int n = 1;
        foreach (var c in content) if (c == '\n') n++;
        return n;
    }

    /// <summary>
    /// Saves the change tracker's pending rows together with the source blobs they
    /// reference, in one transaction (or the caller's, when one is open): the blobs
    /// go into <c>oe_file_contents</c> through <see cref="UpsertFileContentsAsync"/>,
    /// which holds them until the rows that reference them are committed.
    /// </summary>
    public static async Task SaveWithFileContentsAsync(
        AppDbContext db,
        IReadOnlyDictionary<string, (string Content, int Length, int LineCount)> contents,
        CancellationToken ct)
    {
        if (contents.Count == 0 || db.Database.CurrentTransaction is not null)
        {
            await UpsertFileContentsAsync(db, contents, ct);
            await db.SaveChangesAsync(ct);
            return;
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await UpsertFileContentsAsync(db, contents, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Inserts a chunk's distinct source blobs into the shared, content-addressed
    /// <c>oe_file_contents</c> store, keyed by hash, and locks every one of them
    /// until the caller's transaction ends. <c>ON CONFLICT DO NOTHING</c> makes it
    /// idempotent: two orgs importing the same source concurrently both succeed and
    /// the blob is stored exactly once. Must run before the <c>ModuleFile</c> rows
    /// referencing these hashes are saved, so their <c>content_hash</c> FK resolves.
    /// Raw SQL because EF can't express a batch upsert and a duplicate-PK
    /// <c>Add</c> would throw.
    /// <para>
    /// A blob that already existed is the one a release delete or a retry's wipe may
    /// be reclaiming as unused at that moment, so it is only safe once it is locked:
    /// the <c>FOR KEY SHARE</c> lock stops the reclaim taking it, and does not hold
    /// up another import of the same blob. Reclaim skips locked rows rather than wait
    /// for them. A blob deleted between the insert and the lock is inserted again
    /// (#1181).
    /// </para>
    /// <para>
    /// <paramref name="insertVia"/>, when given, is a second context on its own
    /// connection that runs the insert outside <paramref name="db"/>'s transaction, so
    /// the new blobs commit at once and only the lock is held in that transaction.
    /// An amend holds one transaction across every chunk, and blobs it inserted
    /// uncommitted in an early chunk made another import of the same source wait on
    /// it while it waited on that import for a later chunk: a deadlock (#1549,
    /// OEI-6). A committed blob that nothing references yet is harmless; the reclaim
    /// removes it once nothing does.
    /// </para>
    /// </summary>
    public static async Task UpsertFileContentsAsync(
        AppDbContext db,
        IReadOnlyDictionary<string, (string Content, int Length, int LineCount)> contents,
        CancellationToken ct,
        AppDbContext? insertVia = null)
    {
        if (contents.Count == 0) return;
        var hashes = new string[contents.Count];
        var bodies = new string[contents.Count];
        var lengths = new int[contents.Count];
        var lines = new int[contents.Count];
        int i = 0;
        foreach (var (hash, v) in contents)
        {
            hashes[i] = hash;
            bodies[i] = v.Content;
            lengths[i] = v.Length;
            lines[i] = v.LineCount;
            i++;
        }
        for (var attempt = 1; ; attempt++)
        {
            // Sorted, so two imports running at once take the same hashes in the same
            // order and wait on each other rather than deadlock (#1137).
            await (insertVia ?? db).Database.ExecuteSqlRawAsync(
                "INSERT INTO oe_file_contents (content_hash, content, content_length, line_count) " +
                "SELECT * FROM unnest({0}::text[], {1}::text[], {2}::int[], {3}::int[]) ORDER BY 1 " +
                "ON CONFLICT (content_hash) DO NOTHING",
                new object[] { hashes, bodies, lengths, lines }, ct);
            var locked = await db.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM (SELECT 1 FROM oe_file_contents " +
                "WHERE content_hash = ANY({0}) ORDER BY content_hash FOR KEY SHARE) l",
                new object[] { hashes }).SingleAsync(ct);
            if (locked == hashes.Length) return;
            if (attempt == 3)
            {
                throw new InvalidOperationException(
                    $"Could not hold {hashes.Length - locked} source blob(s) against a concurrent delete.");
            }
        }
    }
}
