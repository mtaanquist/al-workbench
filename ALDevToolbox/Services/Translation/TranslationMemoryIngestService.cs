using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer.Import;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Translation;

/// <summary>
/// What one pass over the organisation's repositories did.
/// </summary>
/// <param name="RepositoriesScanned">How many repositories were listed.</param>
/// <param name="FilesRead">How many translation files were read and parsed.</param>
/// <param name="FilesUnchanged">How many were skipped because the version already learned from is still the current one.</param>
/// <param name="PairsLearned">How many brand-new pairs the memory gained.</param>
/// <param name="RepositoriesFailed">How many repositories could not be read at all.</param>
public sealed record TranslationMemoryIngestSummary(
    int RepositoriesScanned,
    int FilesRead,
    int FilesUnchanged,
    int PairsLearned,
    int RepositoriesFailed)
{
    /// <summary>Nothing was scanned - the organisation has no tracked GitHub repositories, or none in the connected organisation.</summary>
    public bool FoundNothingToScan => RepositoriesScanned == 0 && RepositoriesFailed == 0;
}

/// <summary>
/// Fills the translation memory from every <c>.xlf</c> in the repositories the
/// organisation already tracks (issue #631), so a translator's suggestions
/// include what colleagues have translated in code without anyone uploading a
/// file.
///
/// <para><strong>Scope is tracked repositories inside the connected GitHub
/// organisation.</strong> Tracking a repository as a solution is the
/// organisation's own act, and it is the gate this read needs: the ingest goes
/// out on the <em>installation</em> token, which is authorised for every
/// repository the App was installed on, so what bounds it is the tracked list
/// and the connected organisation's login - never a repository name from
/// anywhere else.</para>
///
/// <para><strong>It never fails its caller.</strong> A repository that cannot be
/// read, a file that will not parse, a tree GitHub truncates: each is logged
/// and counted, and the sweep carries on. Nothing here is work a person is
/// waiting on - the button reports what happened and the nightly run tries
/// again.</para>
///
/// <para>See <c>.design/github-integration-phase2.md</c>.</para>
/// </summary>
public sealed class TranslationMemoryIngestService
{
    private readonly AppDbContext _db;
    private readonly GitHubConnectionService _connection;
    private readonly GitHubAppClient _github;
    private readonly TranslationMemoryService _memory;
    private readonly IOrganizationContext _orgContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<TranslationMemoryIngestService> _logger;

    /// <summary>
    /// The widths of the columns a file's provenance is recorded in
    /// (<c>translation_memory_sources</c> and the memory entries themselves).
    /// A value past them would fail the whole sweep on one file, so the file is
    /// skipped instead.
    /// </summary>
    private const int MaxPathLength = 1000;

    /// <inheritdoc cref="MaxPathLength"/>
    private const int MaxRepositoryNameLength = 300;

    public TranslationMemoryIngestService(
        AppDbContext db,
        GitHubConnectionService connection,
        GitHubAppClient github,
        TranslationMemoryService memory,
        IOrganizationContext orgContext,
        TimeProvider clock,
        ILogger<TranslationMemoryIngestService> logger)
    {
        _db = db;
        _connection = connection;
        _github = github;
        _memory = memory;
        _orgContext = orgContext;
        _clock = clock;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; the translation memory ingest ran outside an organisation.");

    /// <summary>
    /// Reads every tracked repository in the acting organisation and learns
    /// from the translation files that have changed since the last pass.
    ///
    /// <para>An organisation with no connected GitHub organisation, or no
    /// tracked repositories inside it, comes back as an empty summary rather
    /// than as a failure - there is simply nothing to read.</para>
    /// </summary>
    public async Task<TranslationMemoryIngestSummary> IngestCurrentOrganisationAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var connection = await _connection.GetStatusAsync(ct).ConfigureAwait(false);
        if (!connection.IsConnected || string.IsNullOrWhiteSpace(connection.OrgLogin))
        {
            _logger.LogDebug("Org {OrgId} has no connected GitHub organisation; nothing to ingest.", orgId);
            return new TranslationMemoryIngestSummary(0, 0, 0, 0, 0);
        }

        var repositories = await ResolveRepositoriesAsync(connection.OrgLogin!, ct).ConfigureAwait(false);
        if (repositories.Count == 0)
        {
            _logger.LogDebug(
                "Org {OrgId} tracks no repositories in {OrgLogin}; nothing to ingest.", orgId, connection.OrgLogin);
            return new TranslationMemoryIngestSummary(0, 0, 0, 0, 0);
        }

        var token = await _github.GetInstallationTokenAsync(connection.InstallationId!.Value, ct).ConfigureAwait(false);

        var scanned = 0;
        var read = 0;
        var unchanged = 0;
        var learned = 0;
        var failed = 0;

        foreach (var repository in repositories)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await IngestRepositoryAsync(token, repository, ct).ConfigureAwait(false);
                scanned++;
                read += result.FilesRead;
                unchanged += result.FilesUnchanged;
                learned += result.PairsLearned;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreachable repository is not a reason to leave the rest
                // of the organisation's translations unlearned.
                failed++;
                _logger.LogWarning(ex,
                    "Translation memory ingest could not read {RepoFullName} for org {OrgId}; carrying on.",
                    repository.FullName, orgId);
            }
        }

        var summary = new TranslationMemoryIngestSummary(scanned, read, unchanged, learned, failed);
        _logger.LogInformation(
            "Translation memory ingest for org {OrgId}: Repositories={Repositories} Read={FilesRead} "
            + "Unchanged={FilesUnchanged} Learned={PairsLearned} Failed={RepositoriesFailed}",
            orgId, scanned, read, unchanged, learned, failed);
        return summary;
    }

    /// <summary>
    /// One repository: list it once, read what has moved, and bring its source
    /// rows in line with what the tree now holds.
    /// </summary>
    private async Task<TranslationMemoryIngestSummary> IngestRepositoryAsync(
        string token, TrackedRepository repository, CancellationToken ct)
    {
        var tree = await _github.ListTreeAsync(token, repository.Owner, repository.Name, repository.TreeIsh, ct)
            .ConfigureAwait(false);
        if (tree.Truncated)
        {
            // GitHub caps a recursive listing rather than failing it. Learning
            // from the half we got is better than learning from none, and the
            // files we never saw keep their rows rather than being deleted as
            // "gone" - see the sweep below, which only removes what the listing
            // covered.
            _logger.LogWarning(
                "GitHub truncated the file list for {RepoFullName}; ingesting the part that came back.",
                repository.FullName);
        }

        var candidates = tree.Entries
            .Where(e => string.Equals(e.Type, "blob", StringComparison.Ordinal))
            .Where(e => TranslationFileRules.IsTranslationFile(e.Path))
            // The compiler-generated .g.xlf holds every string and no
            // translations, so reading one costs a call and teaches nothing.
            .Where(e => !TranslationFileRules.Describe(e.Path).IsSource)
            .ToList();

        // A path longer than the column that records it would throw mid-sweep and
        // lose every repository after this one. Nobody nests an AL project that
        // deep, so skipping the file and saying so is the honest answer.
        var tooLong = candidates.Where(e => e.Path.Length > MaxPathLength).ToList();
        if (repository.FullName.Length > MaxRepositoryNameLength)
        {
            _logger.LogWarning(
                "The repository name {RepoFullName} is longer than the ingest can record; it is skipped.",
                repository.FullName);
            return new TranslationMemoryIngestSummary(1, 0, 0, 0, 1);
        }
        if (tooLong.Count > 0)
        {
            _logger.LogWarning(
                "{Count} translation file(s) in {RepoFullName} have a path longer than the ingest can record; they are skipped.",
                tooLong.Count, repository.FullName);
            candidates = candidates.Except(tooLong).ToList();
        }

        var known = await _db.TranslationMemorySources
            .Where(s => s.Repository == repository.FullName)
            .ToListAsync(ct).ConfigureAwait(false);
        var knownByPath = known.ToDictionary(s => s.Path, StringComparer.Ordinal);

        var read = 0;
        var unchanged = 0;
        var learned = 0;
        var now = _clock.GetUtcNow().UtcDateTime;

        foreach (var entry in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var existing = knownByPath.GetValueOrDefault(entry.Path);
            if (existing is not null && string.Equals(existing.BlobSha, entry.Sha, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            var xml = await ReadFileAsync(token, repository, entry, ct).ConfigureAwait(false);
            if (xml is null) continue;

            var parsed = ParseOrNull(xml, repository.FullName, entry.Path);
            if (parsed is null || string.IsNullOrEmpty(parsed.SourceLanguage)) continue;

            var place = TranslationFileRules.Describe(entry.Path);
            var pairs = TranslationMemoryService.PairsFrom(
                parsed,
                Origin(repository, place.Folder),
                repository.FullName,
                entry.Path).ToList();

            learned += await _memory.UpsertAsync(pairs, ct).ConfigureAwait(false);
            read++;

            if (existing is null)
            {
                _db.TranslationMemorySources.Add(new TranslationMemorySource
                {
                    OrganizationId = repository.OrganizationId,
                    Repository = repository.FullName,
                    Path = entry.Path,
                    BlobSha = entry.Sha,
                    LastIngestedAt = now,
                    UnitCount = pairs.Count,
                });
            }
            else
            {
                existing.BlobSha = entry.Sha;
                existing.LastIngestedAt = now;
                existing.UnitCount = pairs.Count;
                // UpsertAsync clears the change tracker between chunks, so the
                // row loaded above is no longer tracked by the time we get here.
                _db.TranslationMemorySources.Update(existing);
            }
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // A file that is no longer in the repository has no state left to keep.
        // The pairs it taught stay: a translation is not wrong because the file
        // it came from was renamed.
        var present = candidates.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        var gone = known.Where(s => !present.Contains(s.Path)).ToList();
        if (gone.Count > 0 && !tree.Truncated)
        {
            _db.TranslationMemorySources.RemoveRange(gone);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "{Count} translation file(s) are no longer in {RepoFullName}; their ingest state was dropped.",
                gone.Count, repository.FullName);
        }

        return new TranslationMemoryIngestSummary(1, read, unchanged, learned, 0);
    }

    /// <summary>
    /// One file's text. The Contents API is asked first because it is one call
    /// and answers for most files; it declines to inline anything over 1 MB,
    /// and the blob endpoint - which has no such limit - is what covers the
    /// rest.
    /// </summary>
    private async Task<string?> ReadFileAsync(
        string token, TrackedRepository repository, GitHubTreeEntry entry, CancellationToken ct)
    {
        var file = await _github.GetFileAsync(
            token, repository.Owner, repository.Name, entry.Path, repository.TreeIsh, ct).ConfigureAwait(false);
        if (file is not null) return file.Text;

        if (string.IsNullOrEmpty(entry.Sha))
        {
            _logger.LogDebug("{Path} in {RepoFullName} could not be read and has no blob sha to fall back on.",
                entry.Path, repository.FullName);
            return null;
        }

        var blob = await _github.GetBlobAsync(token, repository.Owner, repository.Name, entry.Sha, ct)
            .ConfigureAwait(false);
        if (blob is null)
        {
            _logger.LogDebug("{Path} in {RepoFullName} is no longer readable.", entry.Path, repository.FullName);
        }
        return blob;
    }

    /// <summary>
    /// Parses a file, or logs and returns null. A hand-edited XLIFF that will
    /// not parse is one file's worth of nothing learned, not a failed sweep.
    /// </summary>
    private XliffDocument? ParseOrNull(string xml, string repoFullName, string path)
    {
        try
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
            return AlXliffParser.Parse(stream);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            _logger.LogWarning(ex, "{Path} in {RepoFullName} did not parse as XLIFF; skipping it.", path, repoFullName);
            return null;
        }
    }

    /// <summary>
    /// What a pair from this file is credited to: "{repository} / {folder}",
    /// where the folder is the extension the <c>Translations</c> folder sits
    /// in. A repository whose translations live at its root is credited to the
    /// repository alone, because there is no extension folder to name.
    /// </summary>
    private static string Origin(TrackedRepository repository, string folder) =>
        string.IsNullOrEmpty(folder) ? repository.Name : $"{repository.Name} / {folder}";

    /// <summary>
    /// The repositories this organisation tracks that sit inside the connected
    /// GitHub organisation. Org-scoped by the EF query filter - no
    /// <c>IgnoreQueryFilters()</c> - and narrowed again by the owner in the
    /// clone URL, so a solution pointing at a repository in somebody else's
    /// GitHub organisation is not read with this organisation's installation
    /// token.
    /// </summary>
    private async Task<List<TrackedRepository>> ResolveRepositoriesAsync(string orgLogin, CancellationToken ct)
    {
        var rows = await _db.OeProjectRepositories.AsNoTracking()
            .Where(r => r.Provider == RepositoryProvider.GitHub && r.Project!.DeletedAt == null)
            // A repository nobody confirmed they can read is not read with the installation token.
            .Where(r => r.AccessConfirmedAt != null)
            .Select(r => new { r.OrganizationId, r.Url })
            .ToListAsync(ct).ConfigureAwait(false);

        var resolved = new Dictionary<string, TrackedRepository>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var (owner, name) = ParseOwnerAndName(row.Url);
            if (owner is null || name is null) continue;
            if (!string.Equals(owner, orgLogin, StringComparison.OrdinalIgnoreCase)) continue;
            var fullName = $"{owner}/{name}";
            // One solution's repository can be tracked by several solutions;
            // reading it twice would only cost calls.
            resolved.TryAdd(fullName, new TrackedRepository(row.OrganizationId, fullName, owner, name));
        }

        return resolved.Values
            .OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// <c>owner</c> / <c>name</c> out of a GitHub clone URL, without the
    /// <c>.git</c> suffix. Null halves when the URL is not one we recognise -
    /// free-text entry is allowed on a solution's repositories, so this is an
    /// ordinary case rather than bad data.
    /// </summary>
    internal static (string? Owner, string? Name) ParseOwnerAndName(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return (null, null);
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return (null, null);
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && !uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return (null, null);
        var name = segments[1];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return string.IsNullOrEmpty(name) ? (null, null) : (segments[0], name);
    }

    /// <summary>One repository the ingest will read, resolved from a solution's clone URL.</summary>
    private sealed record TrackedRepository(int OrganizationId, string FullName, string Owner, string Name)
    {
        /// <summary>
        /// What the tree and the file reads are taken at: the default branch,
        /// named as <c>HEAD</c>. GitHub resolves <c>HEAD</c> to the repository's
        /// own default branch server-side, which is the branch a build clones -
        /// so naming it this way costs one call fewer than asking the repository
        /// what its default branch is called, and cannot go stale when somebody
        /// renames it. It is also the ref the "From" links on the memory page
        /// use, for the same reason.
        /// </summary>
        public string TreeIsh => "HEAD";
    }
}
