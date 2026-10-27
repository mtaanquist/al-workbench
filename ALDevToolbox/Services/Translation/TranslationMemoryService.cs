using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Import;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Translation;

/// <summary>
/// Reads and writes the per-organisation translation memory
/// (<see cref="TranslationMemoryEntry"/>). Two writers feed it — the Object
/// Explorer XLIFF import and the Translator tool's exports — and one reader
/// serves it: <see cref="SuggestAsync"/>, which backs the editor's suggestion
/// chips with an exact match first and a <c>pg_trgm</c> trigram fuzzy fallback.
///
/// All reads are <c>AsNoTracking</c> and respect the tenant query filter on
/// <see cref="AppDbContext"/>; writes run inside an authenticated request.
/// See <c>.design/translator/</c> for the feature design.
/// </summary>
public sealed class TranslationMemoryService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<TranslationMemoryService> _logger;

    // Built from the same two dependencies rather than injected, so the many
    // places that construct this service (importers, tests) are unchanged. It
    // caches the caller's snapshot for this service's own lifetime, exactly as
    // the scoped one does for its scope.
    private readonly ProjectAccess _projectAccess;

    public TranslationMemoryService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ILogger<TranslationMemoryService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
        _projectAccess = new ProjectAccess(db, orgContext);
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; TranslationMemoryService called outside an authenticated request.");

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; TranslationMemoryService vote called outside an authenticated request.");

    /// <summary>
    /// Learns from a finished XLIFF: every translated unit in it becomes a
    /// memory pair, so the next file that says the same thing gets it offered.
    ///
    /// <para>Best effort by design, and both callers rely on that - the
    /// Translator's export (<c>TranslatorEndpoints</c>) and its save back to a
    /// repository (<see cref="ALDevToolbox.Services.GitHub.GitHubTranslationService"/>)
    /// have already given the user what they asked for by the time this runs,
    /// so a file this cannot parse, or one with no source language to pair
    /// against, is nothing to learn from rather than something to fail
    /// over.</para>
    /// </summary>
    /// <param name="xml">The finished file.</param>
    /// <param name="origin">
    /// What to credit the pairs to. Null falls back to the file's own
    /// <c>&lt;file original&gt;</c> name, then to "Translator".
    /// </param>
    /// <param name="unitIds">
    /// When given, only these units are learned - the ones the user changed.
    /// Every save used to re-learn the whole file, adding a hit to every unit
    /// in it each time and skewing the tie-break between suggestions. Null
    /// learns every translated unit, which is what a file from elsewhere wants.
    /// </param>
    /// <param name="sourceRepository">
    /// The repository (<c>owner/name</c>) the file was saved to, when it was.
    /// This is what hides the pairs from anyone who cannot see a solution
    /// tracking that repository (#1252): a pair learned without one is visible
    /// to the whole organisation, so a save to a Private solution's repository
    /// has to pass it (#1324). Null for an export or an upload, which belong
    /// to no repository.
    /// </param>
    /// <param name="sourcePath">The file's path in <paramref name="sourceRepository"/>.</param>
    /// <returns>How many pairs were new.</returns>
    public async Task<int> LearnFromXliffAsync(
        string xml, string? origin = null, CancellationToken ct = default, IReadOnlyCollection<string>? unitIds = null,
        string? sourceRepository = null, string? sourcePath = null)
    {
        if (unitIds is { Count: 0 }) return 0;
        XliffDocument parsed;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            parsed = AlXliffParser.Parse(stream);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            _logger.LogDebug(ex, "Nothing to learn from: the file did not parse as XLIFF.");
            return 0;
        }

        if (string.IsNullOrEmpty(parsed.SourceLanguage)) return 0;
        var label = origin
            ?? (string.IsNullOrEmpty(parsed.OriginalName) ? "Translator" : parsed.OriginalName);
        if (unitIds is not null)
        {
            var wanted = unitIds as IReadOnlySet<string> ?? new HashSet<string>(unitIds, StringComparer.Ordinal);
            parsed = parsed with { Units = parsed.Units.Where(u => wanted.Contains(u.Id)).ToList() };
        }
        return await UpsertAsync(PairsFrom(parsed, label, sourceRepository, sourcePath), ct);
    }

    /// <summary>
    /// Projects a parsed XLIFF's translated units into memory-upsert rows.
    /// Callers pass the <paramref name="origin"/> label - the
    /// <c>&lt;file original&gt;</c> name on export, the uploaded file name on
    /// import - so a pair's source is visible on the memory page. The caller
    /// must have confirmed <see cref="XliffDocument.SourceLanguage"/> is
    /// non-null.
    /// </summary>
    public static IEnumerable<TranslationMemoryUpsert> PairsFrom(
        XliffDocument parsed,
        string origin,
        string? sourceRepository = null,
        string? sourcePath = null) =>
        parsed.Units
            .Where(u => !string.IsNullOrEmpty(u.TargetText))
            .Select(u => new TranslationMemoryUpsert(
                parsed.SourceLanguage!,
                parsed.TargetLanguage,
                u.SourceText,
                u.TargetText,
                AlXliffParser.BucketKind(u.Hint),
                origin,
                sourceRepository,
                sourcePath));

    /// <summary>
    /// Inserts new <c>(source, target)</c> pairs and bumps
    /// <see cref="TranslationMemoryEntry.HitCount"/> / <c>LastSeenAt</c> on
    /// pairs already known. Empty source or target entries are skipped, as are
    /// pairs whose source equals target (no-op "translations"). Returns the
    /// number of brand-new rows inserted. Safe to call with thousands of
    /// entries — existing rows are loaded by source hash in chunks rather than
    /// one query per entry.
    /// </summary>
    public async Task<int> UpsertAsync(IEnumerable<TranslationMemoryUpsert> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var orgId = RequireOrganizationId();
        var now = DateTime.UtcNow;

        // Normalise + dedupe inside the batch so a single import that repeats a
        // pair doesn't fight the unique index or double-count a hit.
        var byKey = new Dictionary<string, PreparedEntry>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.SourceText) || string.IsNullOrWhiteSpace(e.TargetText)) continue;
            if (string.Equals(e.SourceText, e.TargetText, StringComparison.Ordinal)) continue;

            var srcLang = NormaliseLanguage(e.SourceLanguage);
            var tgtLang = NormaliseLanguage(e.TargetLanguage);
            if (string.IsNullOrEmpty(srcLang) || string.IsNullOrEmpty(tgtLang)) continue;

            var srcHash = Hash(e.SourceText);
            var tgtHash = Hash(e.TargetText);
            var key = $"{srcLang}{tgtLang}{srcHash}{tgtHash}";
            // Last write wins on collision within the batch.
            byKey[key] = new PreparedEntry(
                srcLang, tgtLang, e.SourceText, e.TargetText, srcHash, tgtHash, e.Kind,
                e.Origin, e.SourceRepository, e.SourcePath);
        }

        if (byKey.Count == 0) return 0;

        var inserted = 0;
        const int chunkSize = 500;
        foreach (var chunk in byKey.Values.Chunk(chunkSize))
        {
            ct.ThrowIfCancellationRequested();

            var srcHashes = chunk.Select(c => c.SourceHash).Distinct().ToList();
            var srcLangs = chunk.Select(c => c.SourceLanguage).Distinct().ToList();
            var tgtLangs = chunk.Select(c => c.TargetLanguage).Distinct().ToList();
            // Load existing rows that could match this chunk (same org via the
            // query filter, same source hashes and languages), keyed by full
            // natural key. Without the language predicates a common source such
            // as "Description" loaded its row for every language in the memory,
            // only to discard all but one (#1268, L49).
            var existing = await _db.TranslationMemory
                .Where(e => srcHashes.Contains(e.SourceHash)
                    && srcLangs.Contains(e.SourceLanguage)
                    && tgtLangs.Contains(e.TargetLanguage))
                .ToListAsync(ct).ConfigureAwait(false);
            var existingByKey = existing.ToDictionary(
                e => $"{e.SourceLanguage}{e.TargetLanguage}{e.SourceHash}{e.TargetHash}",
                StringComparer.Ordinal);

            foreach (var c in chunk)
            {
                var key = $"{c.SourceLanguage}{c.TargetLanguage}{c.SourceHash}{c.TargetHash}";
                if (existingByKey.TryGetValue(key, out var row))
                {
                    row.HitCount += 1;
                    row.LastSeenAt = now;
                    row.UpdatedAt = now;
                    // Most recent file wins - but only when the writer knows
                    // where the pair came from. A release import upserts the same
                    // pairs with no attribution at all, and letting its nulls
                    // through would erase the repository and path the repository
                    // sweep had recorded, leaving "where did this come from"
                    // blank for entries it can answer for.
                    if (c.Origin is not null) row.Origin = c.Origin;
                    if (c.SourceRepository is not null) row.SourceRepository = c.SourceRepository;
                    if (c.SourcePath is not null) row.SourcePath = c.SourcePath;
                    if (row.DeletedAt is not null) row.DeletedAt = null; // resurrect
                    continue;
                }

                _db.TranslationMemory.Add(new TranslationMemoryEntry
                {
                    OrganizationId = orgId,
                    SourceLanguage = c.SourceLanguage,
                    TargetLanguage = c.TargetLanguage,
                    SourceText = c.SourceText,
                    TargetText = c.TargetText,
                    SourceHash = c.SourceHash,
                    TargetHash = c.TargetHash,
                    Kind = string.IsNullOrEmpty(c.Kind) ? "other" : c.Kind,
                    Origin = c.Origin,
                    SourceRepository = c.SourceRepository,
                    SourcePath = c.SourcePath,
                    HitCount = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                    LastSeenAt = now,
                });
                inserted++;
            }

            try
            {
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                // Also on failure: two imports can add the same pair at once, and the
                // loser's rows must not stay tracked to fail every later save (#1137).
                _db.ChangeTracker.Clear();
            }
        }

        _logger.LogInformation(
            "Translation memory upsert: Org={OrgId} Pairs={Pairs} Inserted={Inserted}",
            orgId, byKey.Count, inserted);
        return inserted;
    }

    /// <summary>
    /// Suggests translations for <paramref name="sourceText"/> in
    /// <paramref name="targetLanguage"/>: exact matches first (similarity 1.0),
    /// then trigram-similar sources. Within each tier the ranking key is the
    /// net vote <see cref="TranslationMemoryEntry.Score"/> (a thumbs-up floats a
    /// good pair above a more-frequent but worse one), then <c>hit_count</c>,
    /// then recency. When <paramref name="sourceLanguage"/> is null the source
    /// locale isn't constrained. Results are de-duplicated by target text and
    /// capped at <paramref name="limit"/>; each carries the acting user's own
    /// vote so the chip can show its state.
    /// </summary>
    public async Task<List<TranslationSuggestion>> SuggestAsync(
        string sourceText,
        string? sourceLanguage,
        string targetLanguage,
        int limit = 9,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceText)) return new List<TranslationSuggestion>();
        RequireOrganizationId();

        var tgt = NormaliseLanguage(targetLanguage);
        if (string.IsNullOrEmpty(tgt)) return new List<TranslationSuggestion>();
        var src = string.IsNullOrWhiteSpace(sourceLanguage) ? null : NormaliseLanguage(sourceLanguage);
        var srcHash = Hash(sourceText);
        var memory = VisibleTo(await HiddenRepositoriesAsync(ct).ConfigureAwait(false));

        var exact = await memory
            .Where(e => e.DeletedAt == null && e.TargetLanguage == tgt
                && (src == null || e.SourceLanguage == src)
                && e.SourceHash == srcHash && e.SourceText == sourceText)
            .OrderByDescending(e => e.Score).ThenByDescending(e => e.HitCount).ThenByDescending(e => e.LastSeenAt)
            .Take(limit)
            .Select(e => new TranslationSuggestion(
                e.Id, e.TargetText, e.Origin, e.Kind, 1.0, e.Score, 0, false,
                e.SourceRepository, e.SourcePath))
            .ToListAsync(ct).ConfigureAwait(false);

        var results = new List<TranslationSuggestion>(exact);
        var seenTargets = new HashSet<string>(exact.Select(r => r.TargetText), StringComparer.Ordinal);

        if (results.Count < limit)
        {
            // Trigram fuzzy. TrigramsAreSimilar maps to the `%` operator, which
            // the GIN index accelerates (default threshold 0.3); we rank by
            // closeness first, then vote score.
            var fuzzy = await memory
                .Where(e => e.DeletedAt == null && e.TargetLanguage == tgt
                    && (src == null || e.SourceLanguage == src)
                    && e.SourceText != sourceText
                    && EF.Functions.TrigramsAreSimilar(e.SourceText, sourceText))
                .Select(e => new
                {
                    e.Id,
                    e.TargetText,
                    e.Origin,
                    e.Kind,
                    e.Score,
                    e.SourceRepository,
                    e.SourcePath,
                    Similarity = EF.Functions.TrigramsSimilarity(e.SourceText, sourceText),
                })
                .OrderByDescending(x => x.Similarity)
                .ThenByDescending(x => x.Score)
                .ThenByDescending(x => x.TargetText)
                .Take(limit * 3)
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (var f in fuzzy)
            {
                if (results.Count >= limit) break;
                if (!seenTargets.Add(f.TargetText)) continue;
                results.Add(new TranslationSuggestion(
                    f.Id, f.TargetText, f.Origin, f.Kind, f.Similarity, f.Score, 0, false,
                    f.SourceRepository, f.SourcePath));
            }
        }

        // Stamp the acting user's own vote on each result so the chips render
        // their up/down state (one extra query for the handful we return).
        var userId = _orgContext.CurrentUserId;
        if (userId is not null && results.Count > 0)
        {
            var ids = results.Select(r => r.EntryId).ToList();
            var mine = await _db.TranslationMemoryVotes.AsNoTracking()
                .Where(v => v.UserId == userId && ids.Contains(v.EntryId))
                .Select(v => new { v.EntryId, v.Value })
                .ToListAsync(ct).ConfigureAwait(false);
            if (mine.Count > 0)
            {
                var byEntry = mine.ToDictionary(v => v.EntryId, v => (int)v.Value);
                for (var i = 0; i < results.Count; i++)
                {
                    if (byEntry.TryGetValue(results[i].EntryId, out var mv))
                        results[i] = results[i] with { MyVote = mv };
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Bulk exact lookup for the "Pre-translate from memory" action: given many
    /// source strings, returns the best-known target for each (highest
    /// <c>hit_count</c>), in one query rather than one per unit. Sources with no
    /// exact memory hit are simply absent from the result.
    /// </summary>
    public async Task<Dictionary<string, TranslationSuggestion>> GetExactMatchesAsync(
        IReadOnlyCollection<string> sources,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, TranslationSuggestion>(StringComparer.Ordinal);
        if (sources.Count == 0) return result;
        RequireOrganizationId();

        var tgt = NormaliseLanguage(targetLanguage);
        if (string.IsNullOrEmpty(tgt)) return result;
        var src = string.IsNullOrWhiteSpace(sourceLanguage) ? null : NormaliseLanguage(sourceLanguage);

        // Map each distinct non-empty source to its hash; query by the bounded
        // hash set, then confirm the exact source text in memory.
        var byHash = new Dictionary<string, string>(StringComparer.Ordinal); // hash -> source text
        foreach (var s in sources)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            byHash[Hash(s)] = s;
        }
        if (byHash.Count == 0) return result;

        var hashes = byHash.Keys.ToList();
        var memory = VisibleTo(await HiddenRepositoriesAsync(ct).ConfigureAwait(false));
        foreach (var hashChunk in hashes.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            var set = hashChunk.ToList();
            var rows = await memory
                .Where(e => e.DeletedAt == null && e.TargetLanguage == tgt
                    && (src == null || e.SourceLanguage == src)
                    && set.Contains(e.SourceHash))
                .OrderByDescending(e => e.Score).ThenByDescending(e => e.HitCount).ThenByDescending(e => e.LastSeenAt)
                .Select(e => new { e.Id, e.SourceText, e.TargetText, e.Origin, e.Kind, e.Score, e.SourceRepository, e.SourcePath })
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (var r in rows)
            {
                // First row per source wins (best score, then hit_count, above).
                if (result.ContainsKey(r.SourceText)) continue;
                result[r.SourceText] = new TranslationSuggestion(
                    r.Id, r.TargetText, r.Origin, r.Kind, 1.0, r.Score, 0, false,
                    r.SourceRepository, r.SourcePath);
            }
        }

        return result;
    }

    // ── Curation: vote / delete / restore / search ─────────────────────────

    /// <summary>
    /// Records the acting user's vote on an entry. <paramref name="direction"/>
    /// &gt; 0 = up, &lt; 0 = down, 0 = clear. One vote per user (re-voting
    /// replaces it); the entry's denormalised <see cref="TranslationMemoryEntry.Score"/>
    /// is adjusted by the delta so suggestion ranking reflects it immediately.
    /// Returns the new score and the user's resulting vote.
    /// </summary>
    public async Task<VoteResult> VoteAsync(long entryId, int direction, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var userId = RequireUserId();
        var newValue = (short)(direction > 0 ? 1 : direction < 0 ? -1 : 0);
        var now = DateTime.UtcNow;

        // The denormalised Score drives suggestion ranking, so it must equal the
        // sum of the vote rows. The old read-modify-write (entry.Score += delta on
        // a tracked entity) lost increments when two *different* users voted on
        // one entry at once, and a concurrent double-insert from the *same* user
        // tripped the (EntryId, UserId) unique index as a raw 500. Fix both:
        //  * apply the Score change as an atomic UPDATE ... SET score = score + delta,
        //  * keep the vote-row write and the Score change in one transaction, and
        //  * retry when a concurrent same-user insert wins the unique index — the
        //    reload then sees their row and we re-derive the delta.
        // See #478.
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            // Existence + tenant scope (the query filter). AsNoTracking: Score is
            // never mutated through the tracked entity — the atomic UPDATE owns it.
            var entryExists = await _db.TranslationMemory.AsNoTracking()
                .AnyAsync(e => e.Id == entryId, ct).ConfigureAwait(false);
            if (!entryExists)
                throw new InvalidOperationException($"Translation memory entry {entryId} not found in this organisation.");

            var vote = await _db.TranslationMemoryVotes
                .FirstOrDefaultAsync(v => v.EntryId == entryId && v.UserId == userId, ct).ConfigureAwait(false);
            var oldValue = vote?.Value ?? 0;
            var delta = newValue - oldValue;

            if (newValue == 0)
            {
                if (vote is not null) _db.TranslationMemoryVotes.Remove(vote);
            }
            else if (vote is null)
            {
                _db.TranslationMemoryVotes.Add(new TranslationMemoryVote
                {
                    OrganizationId = orgId,
                    EntryId = entryId,
                    UserId = userId,
                    Value = newValue,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                vote.Value = newValue;
                vote.UpdatedAt = now;
            }

            await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                // The vote-row insert can violate the (EntryId, UserId) unique
                // index if a concurrent same-user request inserted first.
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                if (delta != 0)
                {
                    await _db.TranslationMemory
                        .Where(e => e.Id == entryId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(e => e.Score, e => e.Score + delta)
                            .SetProperty(e => e.UpdatedAt, now), ct)
                        .ConfigureAwait(false);
                }

                // Read our own write inside the transaction so the returned score
                // reflects concurrent increments that landed before us.
                var newScore = await _db.TranslationMemory.AsNoTracking()
                    .Where(e => e.Id == entryId)
                    .Select(e => e.Score)
                    .FirstAsync(ct).ConfigureAwait(false);

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return new VoteResult(entryId, newScore, newValue);
            }
            // A Postgres unique-violation here is the (entry_id, user_id) index
            // catching a concurrent vote from the same user.
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex) && attempt < maxAttempts)
            {
                // A concurrent vote from the same user inserted the row first.
                // Roll back, drop the failed insert from the tracker, and retry:
                // the reload sees their row and we re-derive the delta from it.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                _db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// Soft-deletes an entry (sets <c>deleted_at</c>) so it stops appearing in
    /// suggestions. Recoverable via <see cref="RestoreAsync"/>. Authorisation
    /// (Editor/Admin) is enforced by the caller — the management page and the
    /// MCP tool — matching how the rest of the content-authoring surface gates.
    /// </summary>
    public async Task DeleteAsync(long entryId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var entry = await _db.TranslationMemory.FirstOrDefaultAsync(e => e.Id == entryId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Translation memory entry {entryId} not found in this organisation.");
        if (entry.DeletedAt is null)
        {
            entry.DeletedAt = DateTime.UtcNow;
            entry.UpdatedAt = entry.DeletedAt.Value;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Translation memory entry {EntryId} soft-deleted.", entryId);
        }
    }

    /// <summary>Un-deletes a previously soft-deleted entry.</summary>
    public async Task RestoreAsync(long entryId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var entry = await _db.TranslationMemory.FirstOrDefaultAsync(e => e.Id == entryId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Translation memory entry {entryId} not found in this organisation.");
        if (entry.DeletedAt is not null)
        {
            entry.DeletedAt = null;
            entry.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Translation memory entry {EntryId} restored.", entryId);
        }
    }

    /// <summary>
    /// Browse / search the memory for the management page and the MCP tool:
    /// optional text (source or target substring), language / kind / origin
    /// filters, and an include-deleted toggle. Ranked the same way suggestions
    /// are (score, then hit_count, then recency) and paged. Returns the page
    /// plus the total match count.
    /// </summary>
    public async Task<MemorySearchResult> SearchAsync(MemorySearchQuery q, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var userId = _orgContext.CurrentUserId;

        var query = VisibleTo(await HiddenRepositoriesAsync(ct).ConfigureAwait(false));
        if (!q.IncludeDeleted) query = query.Where(e => e.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(q.TargetLanguage))
        {
            var t = NormaliseLanguage(q.TargetLanguage);
            query = query.Where(e => e.TargetLanguage == t);
        }
        if (!string.IsNullOrWhiteSpace(q.SourceLanguage))
        {
            var s = NormaliseLanguage(q.SourceLanguage);
            query = query.Where(e => e.SourceLanguage == s);
        }
        if (!string.IsNullOrWhiteSpace(q.Kind)) query = query.Where(e => e.Kind == q.Kind);
        if (!string.IsNullOrWhiteSpace(q.Origin))
        {
            // ILike instead of ToLower().Contains: the latter wraps the column in
            // a function, so no index can serve it, and it uses current-culture
            // casing. Escape %/_ so a literal wildcard in the term doesn't match
            // everything. #385, #686
            var originPattern = "%" + ObjectSearchService.EscapeLike(q.Origin.Trim()) + "%";
            query = query.Where(e => e.Origin != null && EF.Functions.ILike(e.Origin, originPattern, "\\"));
        }
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            // Same reason as above, and pg_trgm GIN indexes serve ILIKE — this is
            // what lets the planner use ix_translation_memory_{source,target}_trgm
            // instead of two sequential scans over the whole table. #686
            var needle = "%" + ObjectSearchService.EscapeLike(q.Text.Trim()) + "%";
            query = query.Where(e => EF.Functions.ILike(e.SourceText, needle, "\\")
                || EF.Functions.ILike(e.TargetText, needle, "\\"));
        }

        // Counted only up to a limit. An exact COUNT over an organisation's whole
        // memory reads every row (about 1.7M in production, most of the page's
        // load time), and nobody pages to the end of that: "more than 10,000" is
        // all the number is for once it gets that big.
        int? total = q.CountTotal
            ? await query.Take(MemorySearchQuery.CountLimit + 1).CountAsync(ct).ConfigureAwait(false)
            : null;
        var totalIsCapped = total > MemorySearchQuery.CountLimit;
        if (totalIsCapped) total = MemorySearchQuery.CountLimit;

        // Id last so the order is total: without it, rows that tie on everything
        // else can swap between pages. The full key matches ix_translation_memory_rank,
        // so a page is read straight off the index instead of sorting the match set.
        // One row past the page says whether there is a next one, which a capped
        // total can no longer answer.
        var take = Math.Clamp(q.Take, 1, 200);
        var rows = await query
            .OrderByDescending(e => e.Score).ThenByDescending(e => e.HitCount).ThenByDescending(e => e.LastSeenAt)
            .ThenByDescending(e => e.Id)
            .Skip(Math.Max(0, q.Skip)).Take(take + 1)
            .Select(e => new
            {
                e.Id, e.SourceLanguage, e.TargetLanguage, e.SourceText, e.TargetText,
                e.Kind, e.Origin, e.SourceRepository, e.SourcePath,
                e.HitCount, e.Score, e.CreatedAt, e.LastSeenAt, e.DeletedAt,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(take);

        var myVotes = new Dictionary<long, int>();
        if (userId is not null && rows.Count > 0)
        {
            var ids = rows.Select(r => r.Id).ToList();
            myVotes = (await _db.TranslationMemoryVotes.AsNoTracking()
                .Where(v => v.UserId == userId && ids.Contains(v.EntryId))
                .Select(v => new { v.EntryId, v.Value })
                .ToListAsync(ct).ConfigureAwait(false))
                .ToDictionary(v => v.EntryId, v => (int)v.Value);
        }

        var items = rows.Select(r => new MemoryEntryView(
            r.Id, r.SourceLanguage, r.TargetLanguage, r.SourceText, r.TargetText, r.Kind, r.Origin,
            r.HitCount, r.Score, myVotes.GetValueOrDefault(r.Id), r.CreatedAt, r.LastSeenAt, r.DeletedAt != null,
            r.SourceRepository, r.SourcePath)).ToList();
        return new MemorySearchResult(items, total, totalIsCapped, hasMore);
    }

    // ── Solution visibility ────────────────────────────────────────────────

    /// <summary>
    /// The memory as this caller may read it: every pair, less the ones learned
    /// from a repository that only solutions hidden from them track.
    /// </summary>
    private IQueryable<TranslationMemoryEntry> VisibleTo(List<string> hiddenRepositories)
    {
        var query = _db.TranslationMemory.AsNoTracking();
        if (hiddenRepositories.Count == 0) return query;
        return query.Where(e => e.SourceRepository == null
            || !hiddenRepositories.Contains(e.SourceRepository.ToLower()));
    }

    /// <summary>
    /// The repositories (<c>owner/name</c>, lower-cased) whose pairs the caller
    /// may not see (issue #1252). The ingest reads every tracked repository so
    /// a Private solution's own team gets its translations suggested; a pair
    /// carries the repository and file it came from, so showing it to anyone
    /// else would show them the solution's code and customer-specific strings.
    ///
    /// <para>A repository is hidden when a solution the caller cannot see
    /// tracks it - deleted ones included, since deleting a Private solution
    /// does not make its strings public - and no solution they can see
    /// tracks it too. One they can see already shows them the repository, and
    /// the pair is the same pair whichever repository taught it last. Admins
    /// see every solution, so nothing is hidden from them.</para>
    /// </summary>
    private async Task<List<string>> HiddenRepositoriesAsync(CancellationToken ct)
    {
        var snapshot = await _projectAccess.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot.BypassesVisibility) return [];

        var hidden = await RepositoryNamesAsync(
            _db.OeProjects.Where(ProjectAccess.LockedProjectPredicate(snapshot)), ct).ConfigureAwait(false);
        if (hidden.Count == 0) return [];

        var visible = await RepositoryNamesAsync(
            _db.OeProjects.Where(p => p.DeletedAt == null).Where(ProjectAccess.VisibleProjectPredicate(snapshot)),
            ct).ConfigureAwait(false);
        hidden.ExceptWith(visible);
        return hidden.ToList();
    }

    /// <summary>The GitHub repositories the given solutions track, named the way the ingest records them.</summary>
    private async Task<HashSet<string>> RepositoryNamesAsync(IQueryable<OeProject> projects, CancellationToken ct)
    {
        var projectIds = projects.Select(p => p.Id);
        var urls = await _db.OeProjectRepositories.AsNoTracking()
            .Where(r => r.Provider == RepositoryProvider.GitHub && projectIds.Contains(r.ProjectId))
            .Select(r => r.Url)
            .ToListAsync(ct).ConfigureAwait(false);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var url in urls)
        {
            var (owner, name) = TranslationMemoryIngestService.ParseOwnerAndName(url);
            if (owner is not null && name is not null) names.Add($"{owner}/{name}".ToLowerInvariant());
        }
        return names;
    }

    private sealed class PreparedEntry
    {
        public PreparedEntry(string sourceLanguage, string targetLanguage, string sourceText,
            string targetText, string sourceHash, string targetHash, string kind, string? origin,
            string? sourceRepository, string? sourcePath)
        {
            SourceLanguage = sourceLanguage;
            TargetLanguage = targetLanguage;
            SourceText = sourceText;
            TargetText = targetText;
            SourceHash = sourceHash;
            TargetHash = targetHash;
            Kind = kind;
            Origin = origin;
            SourceRepository = sourceRepository;
            SourcePath = sourcePath;
        }

        public string SourceLanguage { get; }
        public string TargetLanguage { get; }
        public string SourceText { get; }
        public string TargetText { get; }
        public string SourceHash { get; }
        public string TargetHash { get; }
        public string Kind { get; }
        public string? Origin { get; }
        public string? SourceRepository { get; }
        public string? SourcePath { get; }
    }

    /// <summary>MD5 hex of the text — a bounded key for the unique index, not a security primitive.</summary>
    private static string Hash(string text)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Normalises a language tag to BCP-47 <c>xx-XX</c> shape, matching the
    /// form <c>AlXliffParser</c> stores so memory keys line up with imported
    /// rows. Mirrors that parser's private normaliser (kept local to avoid
    /// widening its API for one caller).
    /// </summary>
    private static string NormaliseLanguage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim().Replace('_', '-');
        var dash = s.IndexOf('-');
        if (dash <= 0 || dash >= s.Length - 1) return s.ToLowerInvariant();
        var lang = s.Substring(0, dash).ToLowerInvariant();
        var region = s.Substring(dash + 1).ToUpperInvariant();
        return $"{lang}-{region}";
    }
}

/// <summary>A pair offered for insertion into the translation memory.</summary>
/// <param name="SourceRepository">The repository (<c>owner/name</c>) the pair was read from, when it came from one (#631).</param>
/// <param name="SourcePath">Where in that repository the file lives. Null unless the pair came from a repository.</param>
public sealed record TranslationMemoryUpsert(
    string SourceLanguage,
    string TargetLanguage,
    string SourceText,
    string TargetText,
    string Kind,
    string? Origin,
    string? SourceRepository = null,
    string? SourcePath = null);

/// <summary>
/// One suggested target for a source string: its memory-entry id (for voting /
/// removal), provenance, match strength (0–1), net vote score, and the acting
/// user's own vote (-1/0/1). <see cref="IsMachineTranslation"/> marks an
/// ephemeral provider-generated suggestion (<c>EntryId</c> 0, no vote/remove
/// controls, rendered with an "MT" badge) so the editor can tell it apart from
/// memory and in-file matches.
///
/// <para><see cref="SourceRepository"/> / <see cref="SourcePath"/> are set when
/// the pair was learned from a file in one of the organisation's repositories,
/// which is what lets the chip's caption link to that file (#631).</para>
/// </summary>
public sealed record TranslationSuggestion(
    long EntryId,
    string TargetText,
    string? Origin,
    string Kind,
    double Similarity,
    int Score,
    int MyVote,
    bool IsMachineTranslation = false,
    string? SourceRepository = null,
    string? SourcePath = null);

/// <summary>Result of a vote: the entry's new net score and the user's resulting vote (-1/0/1).</summary>
public sealed record VoteResult(long EntryId, int Score, int MyVote);

/// <summary>Filters for browsing the memory (management page + MCP search).</summary>
public sealed record MemorySearchQuery(
    string? Text = null,
    string? SourceLanguage = null,
    string? TargetLanguage = null,
    string? Kind = null,
    string? Origin = null,
    bool IncludeDeleted = false,
    int Skip = 0,
    int Take = 50,
    // The management page shows the total; the MCP search returns one page
    // and never reads it, so it skips the COUNT altogether.
    bool CountTotal = true)
{
    /// <summary>
    /// The most matches <see cref="TranslationMemoryService.SearchAsync"/> counts
    /// before it stops and reports "more than this" (<see cref="MemorySearchResult.TotalIsCapped"/>).
    /// </summary>
    public const int CountLimit = 10_000;
}

/// <summary>
/// One memory entry as shown in the management list.
/// <c>SourceRepository</c> / <c>SourcePath</c> are set for pairs learned from a
/// file in one of the organisation's repositories (#631), and null otherwise.
/// </summary>
public sealed record MemoryEntryView(
    long Id,
    string SourceLanguage,
    string TargetLanguage,
    string SourceText,
    string TargetText,
    string Kind,
    string? Origin,
    int HitCount,
    int Score,
    int MyVote,
    DateTime CreatedAt,
    DateTime LastSeenAt,
    bool IsDeleted,
    string? SourceRepository = null,
    string? SourcePath = null);

/// <summary>
/// A page of <see cref="MemoryEntryView"/> plus the total match count - null
/// when the query asked not to count (<see cref="MemorySearchQuery.CountTotal"/>).
/// The count stops at <see cref="MemorySearchQuery.CountLimit"/>: past it,
/// <see cref="Total"/> is the limit and <see cref="TotalIsCapped"/> is true.
/// <see cref="HasMore"/> says whether another page follows this one.
/// </summary>
public sealed record MemorySearchResult(
    IReadOnlyList<MemoryEntryView> Items,
    int? Total,
    bool TotalIsCapped = false,
    bool HasMore = false);
