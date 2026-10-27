using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// The second way past the tenant query filter (#1426). Raw SQL -
/// <c>SqlQueryRaw</c>, <c>ExecuteSql*</c>, <c>FromSql*</c>, or a command built
/// on the connection - never sees the EF query filter, so each statement has to
/// keep itself inside one organisation, by naming <c>organization_id</c>, by
/// starting from an id already read through the filter, or by touching nothing
/// tenanted at all. <see cref="IgnoreQueryFiltersBaselineTests"/> could not see
/// any of it.
///
/// <para>Same posture as that baseline: a count per file and member, so a new
/// raw statement, or one moved to another method, is a deliberate diff on this
/// file; and for each file a line saying how its SQL stays in one organisation,
/// so the reviewer of that diff has the claim to check the new statement
/// against. A file whose statements no longer all fit its line needs a new
/// line, not a new count.</para>
/// </summary>
public sealed class RawSqlBaselineTests
{
    /// <summary>
    /// The calls that run SQL text directly. <c>ExecuteSqlAsync(</c> without a
    /// receiver is <c>BackupService</c>'s own helper, whose body is counted as
    /// a <c>CreateCommand</c>.
    /// </summary>
    private static readonly Regex RawSql = new(
        @"\.(?:SqlQuery\w*|FromSql\w*|ExecuteSql\w*)\s*[<(]" +
        @"|\bCreateCommand\s*\(" +
        @"|\bnew\s+Npgsql(?:Command|Batch)\b" +
        @"|\.Begin(?:Binary|Text|Raw)\w*\s*\(",
        RegexOptions.Compiled);

    private sealed record Reviewed(string Pinned, Dictionary<string, int> Members);

    /// <summary>
    /// Raw SQL per file and enclosing member, with how that file's statements
    /// stay inside one organisation. Generated from the tree; change it only
    /// with a review of the statement against the file's line.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Reviewed> Baseline =
        new Dictionary<string, Reviewed>(StringComparer.Ordinal)
    {
        ["ALDevToolbox/Services/Account/AccountService.cs"] = new(
            "An advisory lock keyed on the typed signup address; reads and writes no rows.",
            new() { ["SignupAsync"] = 1 }),
        ["ALDevToolbox/Services/Account/SiteAdminService.cs"] = new(
            "A single global advisory lock for the last-SiteAdmin guard; reads and writes no rows.",
            new() { ["LockSiteAdminGuardAsync"] = 1 }),
        ["ALDevToolbox/Services/Account/UserAdministrationService.cs"] = new(
            "Row locks only: the organisation row by the acting org id, and the user row by the id a filtered token row names.",
            new() { ["ClaimEmailChangeTokenAsync"] = 1, ["LockAdminGuardAsync"] = 1 }),
        ["ALDevToolbox/Services/AuditAnonymization.cs"] = new(
            "The one reviewed cross-org write: clears changed_by_user_id for exactly one deleted user's id, wherever the audit rows sit.",
            new() { ["AnonymiseActorAsync"] = 1 }),
        ["ALDevToolbox/Services/Backups/BackupCoordination.cs"] = new(
            "Advisory lock and unlock for the backup run; reads and writes no rows.",
            new() { ["AcquireAsync"] = 1, ["DisposeAsync"] = 1 }),
        ["ALDevToolbox/Services/Backups/BackupService.cs"] = new(
            "Whole-database backup and restore, run by the scheduler or a SiteAdmin; cross-org by design.",
            new() { ["CaptureInventoryAsync"] = 1, ["ExecuteSqlAsync"] = 1, ["RestoreInventoryAsync"] = 1 }),
        ["ALDevToolbox/Services/Backups/PerTenantBackupService.cs"] = new(
            "SiteAdmin-only (RequireSiteAdmin first); the export and delete statements name the organization_id being snapshotted, and a restore re-inserts that snapshot's rows.",
            new() { ["CreateAsync"] = 1, ["RestoreAsync"] = 4, ["WriteFileContentsAsJsonAsync"] = 1, ["WriteTableAsJsonAsync"] = 1 }),
        ["ALDevToolbox/Services/GitHub/GitHubAccessService.cs"] = new(
            "An advisory lock keyed on the user id of a token row already read through the filter; reads and writes no rows.",
            new() { ["ResolveTokenForAsync"] = 1 }),
        ["ALDevToolbox/Services/Notifications/NotificationPreferenceService.cs"] = new(
            "Upserts that write the signed-in user's id and organisation id from the principal.",
            new() { ["SetEmailForCurrentUserAsync"] = 1, ["SetInAppForCurrentUserAsync"] = 1 }),
        ["ALDevToolbox/Services/ObjectExplorer/Explore/ChainObjectResolution.cs"] = new(
            "Seeded with a release id the caller read through the filter; the release chain never leaves the seed's organisation.",
            new() { ["ResolveMemberSymbolIdAsync"] = 1, ["ResolveObjectAsync"] = 1 }),
        ["ALDevToolbox/Services/ObjectExplorer/Explore/ObjectSearchService.cs"] = new(
            "Release checked visible to the caller first, or the returned ids are fed back into a filtered query.",
            new() { ["ResolveWinningModuleIdsAsync"] = 1, ["SearchContentInReleaseAsync"] = 1 }),
        ["ALDevToolbox/Services/ObjectExplorer/Explore/ReferenceQueryService.cs"] = new(
            "Every public entry point checks the seed release is visible to the caller (ReleaseVisibleAsync) before the chain query; the snippet join names organization_id.",
            new()
            {
                ["BuildUsedByAsync"] = 1,
                ["BuildUsingAsync"] = 1,
                ["EnrichReferencesWithSnippetsAsync"] = 1,
                ["FindInterfaceImplementersAsync"] = 1,
                ["FindInterfaceMethodImplementationsAsync"] = 1,
                ["FindReferencesAsync"] = 1,
                ["FindReferencesForSymbolAsync"] = 1,
                ["FindSystemReferencesAsync"] = 1,
            }),
        ["ALDevToolbox/Services/ObjectExplorer/Import/CalImportService.cs"] = new(
            "Import work scoped by the module or release id the job owns, read through the filter in the job's organisation.",
            new() { ["BackfillSystemReferencesAsync"] = 1, ["ResolveTargetsByIdAsync"] = 8 }),
        ["ALDevToolbox/Services/ObjectExplorer/Import/CallSiteReferenceEmitter.cs"] = new(
            "Seeded with the release id being imported, read through the filter; same chain fence as ChainObjectResolution.",
            new() { ["GetVisibleChainModuleIdsAsync"] = 1 }),
        ["ALDevToolbox/Services/ObjectExplorer/Import/ObjectExplorerVacuumScheduler.cs"] = new(
            "VACUUM and ANALYZE maintenance from a scheduler; reads and writes no rows.",
            new() { ["TickAsync"] = 1 }),
        ["ALDevToolbox/Services/ObjectExplorer/Import/OeIngestHelpers.cs"] = new(
            "The content-addressed blob store (oe_file_contents), which carries no organisation and is shared by design.",
            new() { ["UpsertFileContentsAsync"] = 2 }),
        ["ALDevToolbox/Services/ObjectExplorer/Import/ReleaseImportService.cs"] = new(
            "Import work scoped by the release id the job owns, read through the filter in the job's organisation.",
            new()
            {
                ["DeleteExtractedCallSiteReferencesAsync"] = 1,
                ["DeleteSystemReferencesAsync"] = 1,
                ["PropagateSourceTableToPageExtensionsAsync"] = 1,
                ["ResolveNumericSourceTableNamesAsync"] = 2,
                ["ResolveVariableTargetsAsync"] = 1,
            }),
        ["ALDevToolbox/Services/ObjectExplorer/Import/ReleaseManagementService.cs"] = new(
            "Deletes by a release id validated against the caller's organisation, and reclaims shared blobs no module references any more.",
            new() { ["ClearIngestedDataAsync"] = 1, ["ReclaimOrphanedBlobsAsync"] = 1 }),
        ["ALDevToolbox/Services/ObjectExplorer/Projects/ProjectBuildImporter.cs"] = new(
            "Advisory locks keyed on a release or pipeline id already read through the filter; reads and writes no rows.",
            new() { ["BeginRebuildAsync"] = 1, ["LockPipelineForManualBuildAsync"] = 1 }),
        ["ALDevToolbox/Services/ObjectExplorer/Projects/ProjectFollowService.cs"] = new(
            "An upsert writing the principal's user and organisation ids, for a solution checked visible first.",
            new() { ["SetFollowingAsync"] = 1 }),
        ["ALDevToolbox/Services/Organizations/DatabaseUsageService.cs"] = new(
            "Storage figures: catalogue sizes, counts grouped by organization_id for the ids passed, and the snapshot row for the principal's organisation. The cross-org listings serve only the SiteAdmin storage page and scheduler.",
            new()
            {
                ["EstimateModuleShareRowsAsync"] = 1,
                ["GetSnapshotForCurrentOrgAsync"] = 1,
                ["ListFromSnapshotsAsync"] = 1,
                ["ReadOeSourceLogicalBytesAsync"] = 1,
                ["ReadPerOrgRowCountsAsync"] = 2,
                ["ReadTableSizesAsync"] = 1,
                ["RecomputeSnapshotsAsync"] = 1,
            }),
        ["ALDevToolbox/Services/Templates/ModuleService.cs"] = new(
            "A row lock that names organization_id from the principal.",
            new() { ["LockAsync"] = 1 }),
    };

    [Fact]
    public void Raw_sql_sites_match_the_reviewed_baseline()
    {
        var actual = Scan();

        var problems = new List<string>();
        foreach (var path in actual.Keys.Union(Baseline.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            var found = actual.GetValueOrDefault(path) ?? new Dictionary<string, int>();
            var reviewed = Baseline.GetValueOrDefault(path)?.Members ?? new Dictionary<string, int>();
            foreach (var member in found.Keys.Union(reviewed.Keys).OrderBy(m => m, StringComparer.Ordinal))
            {
                var now = found.GetValueOrDefault(member);
                var before = reviewed.GetValueOrDefault(member);
                if (now == before) continue;
                var where = $"{path} in {member}: baseline {before}, found {now}";
                problems.Add(now > before
                    ? where + ". Raw SQL skips the tenant query filter. Check the statement keeps to one " +
                      "organisation in the way this file's line in the baseline says (or write a new line " +
                      "for a new file), then update this entry deliberately."
                    : where + ". Raw SQL was removed or moved - update this entry so the baseline stays honest.");
            }
        }

        problems.Should().BeEmpty("raw SQL is the other way past the tenant-isolation fence");
    }

    [Fact]
    public void Every_reviewed_file_says_how_its_sql_stays_in_one_organisation()
    {
        Baseline.Where(e => string.IsNullOrWhiteSpace(e.Value.Pinned))
            .Select(e => e.Key)
            .Should().BeEmpty();
    }

    /// <summary>
    /// The scan must see every shape the baseline is about, or a statement in
    /// a shape it misses stays invisible. Pins one of each from the tree.
    /// </summary>
    [Fact]
    public void Each_kind_of_raw_sql_call_is_seen()
    {
        var actual = Scan();

        actual.Should().ContainKey("ALDevToolbox/Services/ObjectExplorer/Explore/ReferenceQueryService.cs",
            "SqlQueryRaw<T>(...) is a raw read");
        actual.Should().ContainKey("ALDevToolbox/Services/Notifications/NotificationPreferenceService.cs",
            "ExecuteSqlInterpolatedAsync is a raw write");
        actual.Should().ContainKey("ALDevToolbox/Services/Organizations/DatabaseUsageService.cs",
            "connection.CreateCommand() is a hand-built command");
        RawSql.IsMatch("_db.OeModules.FromSqlRaw(\"SELECT 1\")").Should().BeTrue();
        RawSql.IsMatch("await using var cmd = new NpgsqlCommand(sql, conn);").Should().BeTrue();
        RawSql.IsMatch("await using var writer = conn.BeginBinaryImport(copy);").Should().BeTrue();
    }

    /// <summary>
    /// A new raw read on a signed-in path, the case nothing caught before
    /// (#1426), lands under its member; prose about raw SQL does not count.
    /// </summary>
    [Fact]
    public void A_new_raw_query_is_counted_under_its_member()
    {
        string[] source =
        [
            "public sealed class Service",
            "{",
            "    /// <summary>Uses ExecuteSqlRaw( in prose only.</summary>",
            "    public async Task<List<long>> ModuleIdsAsync(int releaseId, CancellationToken ct)",
            "    {",
            "        // _db.Database.SqlQueryRaw<long>(...) would skip the filter.",
            "        return await _db.Database",
            "            .SqlQueryRaw<long>(\"SELECT id AS \\\"Value\\\" FROM oe_modules WHERE release_id = {0}\", releaseId)",
            "            .ToListAsync(ct);",
            "    }",
            "}",
        ];

        ScanSource(source).Should().Equal(new Dictionary<string, int> { ["ModuleIdsAsync"] = 1 });
    }

    private static Dictionary<string, Dictionary<string, int>> Scan()
    {
        var results = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var (relative, full) in FenceSourceScan.AppSourceFiles())
        {
            var members = ScanSource(File.ReadAllLines(full));
            if (members.Count > 0) results[relative] = members;
        }
        return results;
    }

    private static Dictionary<string, int> ScanSource(IReadOnlyList<string> raw)
    {
        var lines = FenceSourceScan.Read(raw);
        var members = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var n = RawSql.Matches(lines[i].Code).Count;
            if (n == 0) continue;
            var member = FenceSourceScan.EnclosingMember(lines, i);
            members[member] = members.GetValueOrDefault(member) + n;
        }
        return members;
    }
}
