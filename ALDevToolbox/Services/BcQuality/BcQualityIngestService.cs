using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.BcQuality;

/// <summary>What one ingest pass did. Returned to the caller and logged.</summary>
public sealed record BcQualityIngestResult(
    int Added,
    int Updated,
    int Unchanged,
    int Pruned,
    string CommitSha,
    IReadOnlyList<BcQualitySkippedFile> Skipped)
{
    /// <summary>Articles in the table after the pass.</summary>
    public int Total => Added + Updated + Unchanged;
}

/// <summary>
/// Mirrors Microsoft's BCQuality knowledge base
/// (https://github.com/microsoft/BCQuality, MIT) into Postgres so MCP clients
/// can search it without a local clone. Behaviour is specified in
/// <c>.design/bcquality.md</c>.
///
/// <para>
/// Two entry points, deliberately split: <see cref="RefreshAsync"/> does the
/// git side (shallow clone or fetch of the default branch, then read the
/// commit) and hands off to <see cref="IngestFromDirectoryAsync"/>, which is
/// the whole walker/upsert contract and takes nothing but a directory. Tests
/// drive the second one against a fixture tree and never touch the network.
/// </para>
///
/// <para>
/// The rows carry no organisation: the content is public and identical for
/// every tenant, so the tables have no <c>organization_id</c> and no query
/// filter. See the note in <c>AppDbContext.OnModelCreating</c>.
/// </para>
/// </summary>
public sealed class BcQualityIngestService
{
    /// <summary>
    /// The repository we mirror. Hardcoded rather than configurable: there is
    /// exactly one BCQuality, and a host that cannot reach GitHub turns the
    /// refresh off with <c>DISABLE_BCQUALITY_REFRESH=1</c> instead.
    /// </summary>
    public const string RepositoryUrl = "https://github.com/microsoft/BCQuality.git";

    /// <summary>The three authority layers BCQuality defines. Anything else in the tree is not knowledge content.</summary>
    private static readonly string[] Layers = ["microsoft", "community", "custom"];

    /// <summary>
    /// Ceiling on one sample file. Samples are short demonstration snippets;
    /// anything larger is a sign the naming convention matched something it
    /// should not have, and we skip it rather than pull a blob into the row.
    /// </summary>
    private const int MaxSampleBytes = 256 * 1024;

    /// <summary>Mirrors the discovery-clone ceiling: enough for a 4 MB shallow clone, short enough to catch a stalled transport.</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(3);

    private readonly AppDbContext _db;
    private readonly IProcessRunner _processRunner;
    private readonly TimeProvider _clock;
    private readonly ILogger<BcQualityIngestService> _logger;

    public BcQualityIngestService(
        AppDbContext db,
        IProcessRunner processRunner,
        TimeProvider clock,
        ILogger<BcQualityIngestService> logger)
    {
        _db = db;
        _processRunner = processRunner;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The provenance row, or null before the first ingest has ever run.</summary>
    public Task<BcQualityIngestState?> GetStateAsync(CancellationToken ct = default) =>
        _db.BcQualityIngestState.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == BcQualityIngestState.SingletonId, ct);

    /// <summary>
    /// Pulls the repository's default branch into the local scratch clone and
    /// ingests whatever it finds. Records the commit SHA it read, so an MCP
    /// answer can be traced back to an exact upstream revision — BCQuality's
    /// citation contract asks consumers to carry the SHA alongside the path.
    /// </summary>
    /// <remarks>
    /// Failures are recorded on the provenance row and rethrown: a refresh
    /// that cannot reach GitHub leaves the previously ingested articles in
    /// place rather than emptying the table.
    /// </remarks>
    public async Task<BcQualityIngestResult> RefreshAsync(CancellationToken ct = default)
    {
        var attemptedAt = _clock.GetUtcNow().UtcDateTime;
        try
        {
            var (directory, sha, commitDate) = await SyncCloneAsync(ct).ConfigureAwait(false);
            var result = await IngestFromDirectoryAsync(directory, sha, commitDate, ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordAttemptFailureAsync(attemptedAt, ex.Message, ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Walks <paramref name="root"/> as a BCQuality checkout and reconciles the
    /// tables with what it finds: insert new articles, update changed ones,
    /// leave unchanged ones alone, and delete rows whose file has disappeared
    /// upstream. Idempotent — running it twice over the same tree writes
    /// nothing the second time.
    /// </summary>
    /// <param name="root">A BCQuality checkout (or a fixture tree shaped like one).</param>
    /// <param name="commitSha">The commit the tree is at, recorded for provenance. May be empty for a fixture.</param>
    /// <param name="commitDate">Committer date of <paramref name="commitSha"/>, when known.</param>
    /// <exception cref="PlanValidationException">
    /// The directory does not exist, or holds no valid knowledge article. The
    /// second case is a refusal on purpose: a half-finished clone must not be
    /// allowed to prune a good mirror down to nothing.
    /// </exception>
    public async Task<BcQualityIngestResult> IngestFromDirectoryAsync(
        string root,
        string commitSha,
        DateTime? commitDate,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["root"] = "The BCQuality checkout directory does not exist.",
            });
        }

        var sw = Stopwatch.StartNew();
        var skipped = new List<BcQualitySkippedFile>();
        var parsed = await ReadArticlesAsync(root, skipped, ct).ConfigureAwait(false);

        if (parsed.Count == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["root"] = "No BCQuality knowledge articles were found under this directory. "
                    + "Expected markdown files under microsoft/knowledge, community/knowledge, or custom/knowledge.",
            });
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        // Loaded without their samples on purpose: the daily refresh usually
        // changes nothing, and pulling every sample body just to compare
        // hashes would read a couple of megabytes to do no work. The samples
        // of the few articles that did change are fetched below.
        var existing = await _db.BcQualityArticles
            .ToDictionaryAsync(a => a.ArticleKey, ct)
            .ConfigureAwait(false);

        int added = 0, unchanged = 0;
        var changed = new List<(BcQualityArticle Row, BcQualityParsedArticle Article)>();
        foreach (var article in parsed)
        {
            var hash = ComputeHash(article);
            if (existing.TryGetValue(article.ArticleKey, out var row))
            {
                if (row.ContentHash == hash)
                {
                    unchanged++;
                    continue;
                }
                Apply(row, article, hash, now);
                changed.Add((row, article));
            }
            else
            {
                var fresh = new BcQualityArticle { FirstSeenAt = now };
                Apply(fresh, article, hash, now);
                fresh.Samples = article.Samples.Select(ToEntity).ToList();
                _db.BcQualityArticles.Add(fresh);
                added++;
            }
        }

        if (changed.Count > 0)
        {
            // Samples are replaced wholesale rather than diffed: a handful of
            // short files per article, and the hash already told us something
            // in the set changed.
            var changedIds = changed.Select(c => c.Row.Id).ToList();
            var oldSamples = await _db.BcQualityArticleSamples
                .Where(s => changedIds.Contains(s.ArticleId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            _db.BcQualityArticleSamples.RemoveRange(oldSamples);
            foreach (var (row, article) in changed)
            {
                // Clear rather than reassign: loading the old samples ran
                // relationship fixup, so this collection is the tracked one.
                row.Samples.Clear();
                foreach (var sample in article.Samples) row.Samples.Add(ToEntity(sample));
            }
        }
        var updated = changed.Count;

        var seen = parsed.Select(a => a.ArticleKey).ToHashSet(StringComparer.Ordinal);
        var stale = existing.Values.Where(a => !seen.Contains(a.ArticleKey)).ToList();
        // Hard delete, not soft: these rows are a mirror of an upstream file,
        // not something a user authored. When the file is gone upstream the
        // guidance has been withdrawn and should stop being cited.
        if (stale.Count > 0) _db.BcQualityArticles.RemoveRange(stale);

        await UpsertStateAsync(commitSha, commitDate, now, parsed.Count, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var result = new BcQualityIngestResult(added, updated, unchanged, stale.Count, commitSha, skipped);
        _logger.LogInformation(
            "BCQuality ingest finished at {CommitSha}: {Added} added, {Updated} updated, {Unchanged} unchanged, "
            + "{Pruned} pruned, {Skipped} skipped in {ElapsedMs}ms.",
            string.IsNullOrEmpty(commitSha) ? "(no commit)" : commitSha,
            added, updated, unchanged, stale.Count, skipped.Count, sw.ElapsedMilliseconds);
        foreach (var skip in skipped)
        {
            _logger.LogWarning("BCQuality file skipped: {Path} ({Reason}).", skip.Path, skip.Reason);
        }
        return result;
    }

    // ---- walking -----------------------------------------------------------

    /// <summary>
    /// Collects every article under <c>&lt;layer&gt;/knowledge/**</c>. Nothing
    /// else in the repository is ingested: the <c>skills/</c> trees are agent
    /// process instructions rather than guidance, and they do not carry the
    /// six-field schema the filters depend on.
    /// </summary>
    private static async Task<List<BcQualityParsedArticle>> ReadArticlesAsync(
        string root, List<BcQualitySkippedFile> skipped, CancellationToken ct)
    {
        var articles = new List<BcQualityParsedArticle>();
        foreach (var layer in Layers)
        {
            var layerRoot = Path.Combine(root, layer, "knowledge");
            if (!Directory.Exists(layerRoot)) continue;
            if (IsLink(Path.Combine(root, layer)) || IsLink(layerRoot))
            {
                skipped.Add(new BcQualitySkippedFile(ToArticleKey(root, layerRoot), "a link, not a folder in the repository"));
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(layerRoot, "*.md", WithoutLinks(recurse: true)).Order())
            {
                ct.ThrowIfCancellationRequested();
                var key = ToArticleKey(root, path);

                // README-style files sit alongside articles in some layers and
                // are documentation about the folder, not guidance in it.
                if (Path.GetFileName(path).Equals("README.md", StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(new BcQualitySkippedFile(key, "folder README, not a knowledge article"));
                    continue;
                }

                var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                var samples = await ReadSamplesAsync(path, skipped, root, ct).ConfigureAwait(false);
                var article = BcQualityMarkdown.TryParse(key, text, samples, out var reason);
                if (article is null)
                {
                    skipped.Add(new BcQualitySkippedFile(key, reason));
                    continue;
                }
                articles.Add(article);
            }
        }
        return articles;
    }

    /// <summary>
    /// Reads the <c>&lt;slug&gt;.&lt;kind&gt;.&lt;ext&gt;</c> siblings of an
    /// article. BCQuality forbids fenced code blocks inside an article, so
    /// without these the "see sample: x.bad.al" pointers would dead-end for
    /// any agent that has no clone. Unknown kinds are carried rather than
    /// rejected, as the contract requires.
    /// </summary>
    private static async Task<List<BcQualityParsedSample>> ReadSamplesAsync(
        string articlePath, List<BcQualitySkippedFile> skipped, string root, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(articlePath)!;
        var slug = Path.GetFileNameWithoutExtension(articlePath);
        var samples = new List<BcQualityParsedSample>();

        foreach (var path in Directory.EnumerateFiles(directory, slug + ".*", WithoutLinks(recurse: false)).Order())
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            if (fileName.Equals(slug + ".md", StringComparison.Ordinal)) continue;

            // slug + '.' + kind + '.' + ext — anything else shares a prefix by
            // accident and is not this article's sample.
            var suffix = fileName[(slug.Length + 1)..];
            var parts = suffix.Split('.');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) continue;

            var info = new FileInfo(path);
            if (info.Length > MaxSampleBytes)
            {
                skipped.Add(new BcQualitySkippedFile(
                    ToArticleKey(root, path), $"sample is larger than {MaxSampleBytes / 1024} KB"));
                continue;
            }

            samples.Add(new BcQualityParsedSample(
                parts[0].ToLowerInvariant(),
                fileName,
                parts[1].ToLowerInvariant(),
                await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)));
        }
        return samples;
    }

    /// <summary>
    /// How the upstream checkout is walked: links are skipped, files and folders
    /// alike. A symlinked sample would otherwise be read through to whatever it
    /// points at (<c>/proc/self/environ</c> reports a length of 0, so the size
    /// check passes) and stored in rows every organisation can read. The clone
    /// is also made with <c>core.symlinks=false</c>; this covers a cache cloned
    /// before that, and a directory handed to <see cref="IngestFromDirectoryAsync"/>.
    /// Otherwise the same as the default enumeration: hidden files included,
    /// Windows-style wildcard matching.
    /// </summary>
    private static EnumerationOptions WithoutLinks(bool recurse) => new()
    {
        RecurseSubdirectories = recurse,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchType = MatchType.Win32,
        IgnoreInaccessible = false,
    };

    private static bool IsLink(string path) => new DirectoryInfo(path).LinkTarget is not null;

    /// <summary>Repo-relative path with forward slashes — the citation key, stable across platforms.</summary>
    private static string ToArticleKey(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    // ---- persistence -------------------------------------------------------

    private static void Apply(BcQualityArticle row, BcQualityParsedArticle article, string hash, DateTime now)
    {
        row.ArticleKey = article.ArticleKey;
        row.Layer = article.Layer;
        row.Domain = article.Domain;
        row.Slug = article.Slug;
        row.Title = article.Title;
        row.Summary = article.Summary;
        row.Content = article.Content;
        row.Keywords = article.Keywords.ToList();
        row.KeywordsText = string.Join(' ', article.Keywords);
        row.Technologies = article.Technologies.ToList();
        row.Countries = article.Countries.ToList();
        row.ApplicationAreas = article.ApplicationAreas.ToList();
        row.BcVersionRaw = article.BcVersionRaw;
        row.BcVersionAll = article.BcVersionAll;
        row.BcVersions = article.BcVersions.ToList();
        row.BcVersionFrom = article.BcVersionFrom;
        row.ContentHash = hash;
        row.UpdatedAt = now;
    }

    private static BcQualityArticleSample ToEntity(BcQualityParsedSample sample) => new()
    {
        Kind = sample.Kind,
        FileName = sample.FileName,
        Language = sample.Language,
        Content = sample.Content,
    };

    /// <summary>
    /// Hashes the article body, its frontmatter-derived fields, and every
    /// sample, so "unchanged" means the whole rendered row is unchanged — a
    /// sample edited without touching the article still triggers an update.
    /// </summary>
    private static string ComputeHash(BcQualityParsedArticle article)
    {
        var sb = new StringBuilder();
        sb.Append(article.ArticleKey).Append('\n')
            .Append(article.Title).Append('\n')
            .Append(article.Domain).Append('\n')
            .Append(article.BcVersionRaw).Append('\n')
            .Append(string.Join(',', article.Keywords)).Append('\n')
            .Append(string.Join(',', article.Technologies)).Append('\n')
            .Append(string.Join(',', article.Countries)).Append('\n')
            .Append(string.Join(',', article.ApplicationAreas)).Append('\n')
            .Append(article.Content).Append('\n');
        foreach (var sample in article.Samples.OrderBy(s => s.FileName, StringComparer.Ordinal))
        {
            sb.Append(sample.FileName).Append('\n').Append(sample.Content).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private async Task UpsertStateAsync(
        string commitSha, DateTime? commitDate, DateTime now, int articleCount, CancellationToken ct)
    {
        var state = await _db.BcQualityIngestState
            .FirstOrDefaultAsync(s => s.Id == BcQualityIngestState.SingletonId, ct)
            .ConfigureAwait(false);
        if (state is null)
        {
            state = new BcQualityIngestState { Id = BcQualityIngestState.SingletonId };
            _db.BcQualityIngestState.Add(state);
        }
        state.CommitSha = commitSha;
        state.CommitDate = commitDate;
        state.LastSuccessAt = now;
        state.LastAttemptAt = now;
        state.ArticleCount = articleCount;
        state.LastError = string.Empty;
    }

    /// <summary>
    /// Stamps a failed attempt on the provenance row without disturbing the
    /// articles or the last-success marker, so an operator can see that the
    /// mirror has gone stale and why.
    /// </summary>
    private async Task RecordAttemptFailureAsync(DateTime attemptedAt, string message, CancellationToken ct)
    {
        try
        {
            var state = await _db.BcQualityIngestState
                .FirstOrDefaultAsync(s => s.Id == BcQualityIngestState.SingletonId, ct)
                .ConfigureAwait(false);
            if (state is null)
            {
                state = new BcQualityIngestState { Id = BcQualityIngestState.SingletonId };
                _db.BcQualityIngestState.Add(state);
            }
            state.LastAttemptAt = attemptedAt;
            state.LastError = Truncate(message, 1000);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The original failure is the one worth surfacing; don't let the
            // bookkeeping write mask it.
            _logger.LogWarning(ex, "Could not record the failed BCQuality refresh attempt.");
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    // ---- git ---------------------------------------------------------------

    /// <summary>
    /// Where the shallow clone lives. A scratch cache, not persisted state:
    /// losing it costs one clone of a few megabytes, which is why it sits under
    /// the temp directory rather than claiming a named volume.
    /// </summary>
    internal static string CacheDirectory() =>
        Path.Combine(Path.GetTempPath(), "aldt-bcquality");

    /// <summary>
    /// Brings the scratch clone up to the default branch's tip and reads the
    /// commit. A shallow clone (then a shallow fetch plus hard reset) keeps
    /// this cheap on every daily run. Runs through <see cref="IProcessRunner"/>
    /// like the rest of the codebase's git work — no git library dependency.
    /// </summary>
    private async Task<(string Directory, string Sha, DateTime? CommitDate)> SyncCloneAsync(CancellationToken ct)
    {
        var gitPath = NullIfBlank(Environment.GetEnvironmentVariable("GIT_PATH")) ?? "git";
        var dir = CacheDirectory();

        if (Directory.Exists(Path.Combine(dir, ".git")))
        {
            var fetch = await RunGitAsync(gitPath, ["-C", dir, "fetch", "--depth", "1", "origin", "HEAD"], ct)
                .ConfigureAwait(false);
            if (fetch.Succeeded)
            {
                // Links checked out as plain files holding their target, here as
                // well as at clone: a cache cloned before the setting existed
                // does not carry it in its own config.
                var reset = await RunGitAsync(gitPath, ["-c", "core.symlinks=false", "-C", dir, "reset", "--hard", "FETCH_HEAD"], ct)
                    .ConfigureAwait(false);
                if (reset.Succeeded) return await DescribeAsync(gitPath, dir, ct).ConfigureAwait(false);
            }
            // A wedged or half-written cache (an interrupted clone, a shallow
            // history git will not fast-forward) is cheaper to throw away than
            // to repair.
            _logger.LogWarning(
                "BCQuality scratch clone could not be refreshed ({StdErr}); re-cloning.", fetch.StdErr.Trim());
            TryDelete(dir);
        }
        else if (Directory.Exists(dir))
        {
            // Present but not a repository — a half-written clone from an
            // interrupted run. git refuses to clone into a non-empty directory,
            // so clear it rather than wedging every future refresh.
            TryDelete(dir);
        }

        var parent = Path.GetDirectoryName(dir);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        // core.symlinks=false: a link in the upstream repository is checked out
        // as a small text file naming its target, never as a link the ingest
        // could read through. See WithoutLinks.
        var clone = await RunGitAsync(
            gitPath, ["clone", "-c", "core.symlinks=false", "--depth", "1", RepositoryUrl, dir], ct).ConfigureAwait(false);
        if (!clone.Succeeded)
        {
            throw new InvalidOperationException(
                $"git clone of the BCQuality repository failed (exit {clone.ExitCode}): {clone.StdErr.Trim()}");
        }
        return await DescribeAsync(gitPath, dir, ct).ConfigureAwait(false);
    }

    private async Task<(string Directory, string Sha, DateTime? CommitDate)> DescribeAsync(
        string gitPath, string dir, CancellationToken ct)
    {
        var result = await RunGitAsync(gitPath, ["-C", dir, "show", "-s", "--format=%H%x09%cI", "HEAD"], ct)
            .ConfigureAwait(false);
        if (!result.Succeeded) return (dir, string.Empty, null);

        var parts = result.StdOut.Trim().Split('\t');
        var sha = parts.Length > 0 ? parts[0].Trim() : string.Empty;
        DateTime? date = parts.Length > 1
            && DateTimeOffset.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsedDate)
            ? parsedDate.UtcDateTime
            : null;
        return (dir, sha, date);
    }

    private Task<ProcessRunResult> RunGitAsync(string gitPath, string[] args, CancellationToken ct) =>
        _processRunner.RunAsync(new ProcessRunRequest(gitPath, args, Timeout: GitTimeout), ct);

    private void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not clear the BCQuality scratch clone at {Dir}.", dir); }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
