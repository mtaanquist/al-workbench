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
    /// Inserts a chunk's distinct source blobs into the shared, content-addressed
    /// <c>oe_file_contents</c> store, keyed by hash. <c>ON CONFLICT DO NOTHING</c>
    /// makes it idempotent and race-safe: two orgs importing the same source
    /// concurrently both succeed and the blob is stored exactly once. Must run
    /// before the <c>ModuleFile</c> rows referencing these hashes are saved, so
    /// their <c>content_hash</c> FK resolves. Raw SQL because EF can't express a
    /// batch upsert and a duplicate-PK <c>Add</c> would throw.
    /// </summary>
    public static async Task UpsertFileContentsAsync(
        AppDbContext db,
        IReadOnlyDictionary<string, (string Content, int Length, int LineCount)> contents,
        CancellationToken ct)
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
        // Sorted, so two imports running at once take the same hashes in the same
        // order and wait on each other rather than deadlock (#1137).
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO oe_file_contents (content_hash, content, content_length, line_count) " +
            "SELECT * FROM unnest({0}::text[], {1}::text[], {2}::int[], {3}::int[]) ORDER BY 1 " +
            "ON CONFLICT (content_hash) DO NOTHING",
            new object[] { hashes, bodies, lengths, lines }, ct).ConfigureAwait(false);
    }
}
