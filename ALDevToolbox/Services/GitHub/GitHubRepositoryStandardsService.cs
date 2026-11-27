using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Services.GitHub;

/// <summary>What an organisation wants in every repository the workbench creates.</summary>
/// <param name="Ruleset">The branch rules, or null when none are configured.</param>
/// <param name="Files">The files to commit, in the admin's own order.</param>
public sealed record GitHubRepositoryStandards(
    GitHubRepositoryRuleset? Ruleset,
    IReadOnlyList<GitHubRepositoryStandardFile> Files);

/// <summary>One row submitted by the repository-standards editor.</summary>
public sealed record GitHubStandardFileInput(int? Id, string Path, string Content);

/// <summary>
/// The per-organisation repository standards (issue #628): a set of files added
/// to every repository the workbench creates, and a branch ruleset applied to it.
///
/// <para>Reads are open to anyone in the organisation, because
/// <see cref="GitHubWorkspaceRepositoryService"/> and the New Workspace page both
/// need to know whether anything is configured. Writes belong to an Admin: the
/// only writer is <c>/admin/administration/repositories/standards</c>, which
/// carries <c>[Authorize(Roles = "Admin")]</c>. Every method is scoped by the
/// ordinary tenant query filter plus the acting organisation id, so no
/// <c>IgnoreQueryFilters()</c> call appears here.</para>
///
/// <para>See <c>.design/github-integration-phase2.md</c>.</para>
/// </summary>
public sealed class GitHubRepositoryStandardsService
{
    /// <summary>Error key for problems with the ruleset half of the form.</summary>
    public const string RulesetField = "Ruleset";

    /// <summary>
    /// The same path rule the always-included files use: forward slashes, no
    /// leading slash, no <c>..</c>. It already admits <c>.github/workflows/build.yml</c>
    /// and <c>CODEOWNERS</c>, which are the two paths this feature exists for.
    /// </summary>
    private static readonly Regex PathRegex =
        new(@"^[A-Za-z0-9._\-]+(?:/[A-Za-z0-9._\-]+)*$", RegexOptions.Compiled);

    /// <summary>
    /// The most approvals GitHub itself accepts on the pull-request rule. A
    /// number above it is a form the user can fix, not a round trip to be
    /// refused by GitHub after the repository already exists.
    /// </summary>
    public const int MaxRequiredApprovals = 10;

    private readonly AppDbContext _db;
    private readonly OrganizationConfigService _config;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<GitHubRepositoryStandardsService> _logger;

    public GitHubRepositoryStandardsService(
        AppDbContext db,
        OrganizationConfigService config,
        IOrganizationContext orgContext,
        ILogger<GitHubRepositoryStandardsService> logger)
    {
        _db = db;
        _config = config;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException(
            "No organization in scope; repository standards read outside an authenticated request.");

    /// <summary>Everything configured for the acting organisation.</summary>
    public async Task<GitHubRepositoryStandards> GetAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var ruleset = await _db.OrganizationSettings
            .AsNoTracking()
            .Where(s => s.OrganizationId == orgId)
            .Select(s => s.GitHubRepositoryRuleset)
            .FirstOrDefaultAsync(ct);
        var files = await _db.GitHubRepositoryStandardFiles
            .AsNoTracking()
            .Where(f => f.OrganizationId == orgId)
            .OrderBy(f => f.Ordering)
            .ToListAsync(ct);
        return new GitHubRepositoryStandards(ruleset, files);
    }

    /// <summary>
    /// One sentence for the pages that only need to say whether anything is
    /// configured - the Repositories tab's row and New Workspace's caption.
    /// Null when nothing is, so a caller can render nothing at all rather than
    /// "nothing configured" in a place that has no room for it.
    /// </summary>
    public async Task<string?> GetSummaryAsync(CancellationToken ct = default)
    {
        var standards = await GetAsync(ct);
        var parts = new List<string>();
        if (standards.Files.Count > 0)
        {
            parts.Add($"{standards.Files.Count} {(standards.Files.Count == 1 ? "file" : "files")}");
        }
        if (standards.Ruleset is { IsEmpty: false })
        {
            // Reads inside "Every new repository gets ..." on both callers, which
            // is why it is a plain noun phrase and not "a branch ruleset".
            parts.Add("your branch rules");
        }
        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => $"{parts[0]} and {parts[1]}",
        };
    }

    /// <summary>
    /// Replaces the whole configuration. Files are reconciled by primary key -
    /// a row whose id is missing from <paramref name="files"/> is deleted - the
    /// same shape <see cref="OrganizationConfigService.SaveFilesAsync"/> uses,
    /// so the editor can send its list and not a set of edits.
    ///
    /// <para>A null <paramref name="ruleset"/> clears the column, which is how
    /// an admin turns branch rules off again.</para>
    /// </summary>
    /// <exception cref="PlanValidationException">A path or an approval count the form must fix.</exception>
    public async Task SaveAsync(
        GitHubRepositoryRuleset? ruleset, IReadOnlyList<GitHubStandardFileInput> files,
        CancellationToken ct = default)
    {
        Validate(ruleset, files);
        var orgId = RequireOrganizationId();

        var settings = await _db.OrganizationSettings
            .FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (settings is null)
        {
            settings = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(settings);
        }
        settings.GitHubRepositoryRuleset = ruleset;
        settings.UpdatedAt = DateTime.UtcNow;

        var existing = await _db.GitHubRepositoryStandardFiles
            .Where(f => f.OrganizationId == orgId)
            .ToListAsync(ct);
        var existingById = existing.ToDictionary(e => e.Id);
        var keptIds = files.Where(f => f.Id is not null).Select(f => f.Id!.Value).ToHashSet();

        var now = DateTime.UtcNow;
        for (var i = 0; i < files.Count; i++)
        {
            var input = files[i];
            if (input.Id is int id && existingById.TryGetValue(id, out var row))
            {
                var path = input.Path.Trim();
                var content = input.Content ?? string.Empty;
                // Only a real change moves UpdatedAt, which the Repositories page reads.
                if (row.Path != path || row.Content != content) row.UpdatedAt = now;
                row.Path = path;
                row.Content = content;
                row.Ordering = i;
            }
            else
            {
                _db.GitHubRepositoryStandardFiles.Add(new GitHubRepositoryStandardFile
                {
                    OrganizationId = orgId,
                    Path = input.Path.Trim(),
                    Content = input.Content ?? string.Empty,
                    Ordering = i,
                    UpdatedAt = now,
                });
            }
        }

        foreach (var row in existing)
        {
            if (!keptIds.Contains(row.Id)) _db.GitHubRepositoryStandardFiles.Remove(row);
        }

        await _db.SaveChangesAsync(ct);
        // The settings row is cached by OrganizationConfigService, and the
        // ruleset now lives on it - a stale entry there would hand the next
        // reader the rules the admin just changed.
        _config.InvalidateCache(orgId);

        _logger.LogInformation(
            "Saved repository standards for org {OrgId}: {FileCount} file(s), ruleset {RulesetState}.",
            orgId, files.Count, ruleset is null ? "cleared" : "set");
    }

    private static void Validate(
        GitHubRepositoryRuleset? ruleset, IReadOnlyList<GitHubStandardFileInput> files)
    {
        var errors = new Dictionary<string, string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++)
        {
            var path = files[i].Path?.Trim() ?? string.Empty;
            if (path.Length == 0)
            {
                errors[$"Files[{i}].Path"] = "Give the file a path.";
            }
            else if (!PathRegex.IsMatch(path) || path.Contains(".."))
            {
                errors[$"Files[{i}].Path"] =
                    "Use a path inside the repository, like .github/workflows/build.yml. "
                    + "No leading slash and no '..' segments.";
            }
            else if (!seen.Add(path))
            {
                errors[$"Files[{i}].Path"] = $"Two files use the path '{path}'. Each path can only appear once.";
            }
        }

        if (ruleset is not null
            && (ruleset.RequiredApprovals < 0 || ruleset.RequiredApprovals > MaxRequiredApprovals))
        {
            errors[RulesetField] = $"Ask for between 0 and {MaxRequiredApprovals} approvals.";
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);
    }
}
