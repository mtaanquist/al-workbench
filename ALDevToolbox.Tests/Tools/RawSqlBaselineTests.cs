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
    /// The calls that run SQL text directly, including a command or batch
    /// built by hand (<c>new NpgsqlCommand(...)</c>, <c>NpgsqlCommand cmd = new(...)</c>).
    /// A helper that wraps one is counted once, at its body; its callers are
    /// kept to a closed list by <see cref="Raw_sql_helpers_are_called_only_from_reviewed_members"/>.
    /// </summary>
    private static readonly Regex RawSql = new(
        @"\.(?:SqlQuery\w*|FromSql\w*|ExecuteSql\w*)\s*[<(]" +
        @"|\bCreateCommand\s*\(" +
        @"|\bCreateBatch\s*\(" +
        @"|\bnew\s+Npgsql(?:Command|Batch)\b" +
        @"|\bNpgsql(?:Command|Batch)\s+\w+\s*=\s*new\s*\(" +
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
        ["ALDevToolbox/Services/Account/EmailMfaService.cs"] = new(
            "A row lock on the users row pinned to the user id the code is being issued for (the half-authenticated login state or the signed-in user); reads and writes no rows.",
            new() { ["IssueChallengeAsync"] = 1 }),
        ["ALDevToolbox/Services/Account/PasswordResetService.cs"] = new(
            "Row locks only: the user row by the id the typed email resolved to (issue), or by the id named on a reset token row looked up by its hash, plus that user's own organization_settings row held for share (consume); fence category 1.",
            new() { ["ConsumePasswordResetTokenAsync"] = 2, ["CreatePasswordResetTokenAsync"] = 1 }),
        ["ALDevToolbox/Services/Account/SiteAdminService.cs"] = new(
            "A single global advisory lock for the last-SiteAdmin guard; reads and writes no rows.",
            new() { ["LockSiteAdminGuardAsync"] = 1 }),
        ["ALDevToolbox/Services/Account/UserAdministrationService.cs"] = new(
            "Row locks only: the organisation row by the acting org id, and the user row by the id named on an email-change token row that was looked up by its hash (fence category 1).",
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
            "Run by a SiteAdmin (RequireSiteAdmin first) or by the scheduler for a pinned org id; the export and delete statements name the organization_id being snapshotted, and a restore re-inserts that snapshot's rows.",
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
            new() { ["TickOnceAsync"] = 1 }),
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
            "Storage figures: catalogue sizes, counts grouped by organization_id for the ids passed, and the snapshot row for the principal's organisation. The cross-org snapshot listing calls RequireSiteAdmin() first; the recompute runs from the scheduler with no request.",
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

    /// <summary>
    /// Helpers that run raw SQL for their callers, with the members allowed to
    /// call them. The helper's own statement is in the baseline above; a new
    /// caller would reach the same SQL without adding a raw call of its own, so
    /// it has to be added here, reviewed against the helper's claim.
    /// </summary>
    private static readonly IReadOnlyList<(string Helper, Regex Call, Dictionary<string, string[]> Allowed)> Helpers =
    [
        ("AuditAnonymization.AnonymiseActorAsync",
            new(@"\.AnonymiseActorAsync\s*\(", RegexOptions.Compiled),
            new(StringComparer.Ordinal)
            {
                ["ALDevToolbox/Services/Account/UserAdministrationService.cs"] = ["RejectSignupAsync"],
            }),
        ("BackupService.ExecuteSqlAsync",
            new(@"(?<![.\w])ExecuteSqlAsync\s*\(", RegexOptions.Compiled),
            new(StringComparer.Ordinal)
            {
                ["ALDevToolbox/Services/Backups/BackupService.cs"] = ["RestoreAsync"],
            }),
        ("ChainObjectResolution.ResolveObjectAsync / ResolveMemberSymbolIdAsync",
            new(@"\bChainObjectResolution\s*\.\s*Resolve(?:Object|MemberSymbolId)Async\s*\(", RegexOptions.Compiled),
            new(StringComparer.Ordinal)
            {
                ["ALDevToolbox/Services/ObjectExplorer/Explore/ObjectExplorerService.cs"] = ["ResolveChainObjectAsync"],
                ["ALDevToolbox/Services/ObjectExplorer/Explore/ReferenceResolver.cs"] =
                    ["ResolveExtractedReferenceAtAsync", "ResolveMemberSymbolAsync", "ResolveObjectByNameAsync"],
                ["ALDevToolbox/Services/ObjectExplorer/Explore/SourceViewerService.cs"] = ["GoToDefinitionAsync"],
            }),
        ("ReferenceQueryService.EnrichReferencesWithSnippetsAsync",
            new(@"\bEnrichReferencesWithSnippetsAsync\s*\(", RegexOptions.Compiled),
            new(StringComparer.Ordinal)
            {
                ["ALDevToolbox/Services/ObjectExplorer/Explore/ReferenceQueryService.cs"] =
                    ["FindReferencesAsync", "FindReferencesForSymbolAsync", "FindSystemReferencesAsync"],
                // Its file ids come from a filtered EF query, behind SeedVisibleAsync.
                ["ALDevToolbox/Services/ObjectExplorer/Explore/ReferenceSessionService.cs"] = ["CreateFromGlobalVariableAsync"],
            }),
    ];

    [Fact]
    public void Raw_sql_sites_match_the_reviewed_baseline()
    {
        var problems = FenceSourceScan.CompareWithBaseline(
            Scan(),
            path => Baseline.GetValueOrDefault(path)?.Members,
            Baseline.Keys,
            grewAdvice:
                "Raw SQL skips the tenant query filter, so a new or moved statement needs the maintainer's " +
                "confirmation, as a new IgnoreQueryFilters() call does (CLAUDE.md). Check it keeps to one " +
                "organisation in the way this file's line in the baseline says (or write a line for a new file), " +
                "then update this entry deliberately.",
            shrankAdvice: "Raw SQL was removed or moved - update this entry so the baseline stays honest.");

        problems.Should().BeEmpty("raw SQL is the other way past the tenant-isolation fence");
    }

    [Fact]
    public void Every_reviewed_file_says_how_its_sql_stays_in_one_organisation()
    {
        Baseline.Where(e => string.IsNullOrWhiteSpace(e.Value.Pinned))
            .Select(e => e.Key)
            .Should().BeEmpty();
    }

    [Fact]
    public void Raw_sql_helpers_are_called_only_from_reviewed_members()
    {
        var problems = new List<string>();
        foreach (var (helper, call, allowed) in Helpers)
        {
            var callers = HelperCalls(call).ToList();
            problems.AddRange(callers
                .Where(c => !(allowed.TryGetValue(c.Path, out var members) && members.Contains(c.Member)))
                .Select(c => $"{helper} called from {c.Path}:{c.Line} in {c.Member}. It runs raw SQL that skips the " +
                             "tenant query filter: check the caller passes only ids it read through the filter or " +
                             "pinned to its organisation, and with the maintainer's confirmation add it to Helpers."));
            problems.AddRange(allowed
                .SelectMany(e => e.Value.Select(m => (Path: e.Key, Member: m)))
                .Where(a => !callers.Any(c => c.Path == a.Path && c.Member == a.Member))
                .Select(a => $"{helper} is no longer called from {a.Path} in {a.Member} - drop that entry from Helpers."));
        }

        problems.Should().BeEmpty("a helper that runs raw SQL crosses the tenant fence for every caller");
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
        RawSql.IsMatch("await using NpgsqlCommand cmd = new(sql, conn);").Should().BeTrue();
        RawSql.IsMatch("await using var batch = conn.CreateBatch();").Should().BeTrue();
        RawSql.IsMatch("await using var writer = conn.BeginBinaryImport(copy);").Should().BeTrue();
        RawSql.IsMatch("var users = await _db.Users.Where(u => u.Id == id).ToListAsync(ct);").Should().BeFalse();
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
            "    /// <summary>Uses _db.Database.ExecuteSqlRaw( in prose only.</summary>",
            "    public async Task<List<long>> ModuleIdsAsync(int releaseId, CancellationToken ct)",
            "    {",
            "        // _db.Database.SqlQueryRaw<long>(...) would skip the filter.",
            "        var note = \"see https://example.test/\"; var ids = await _db.Database",
            "            .SqlQueryRaw<long>(\"SELECT id AS \\\"Value\\\" FROM oe_modules WHERE release_id = {0}\", releaseId)",
            "            .ToListAsync(ct);",
            "        return ids;",
            "    }",
            "}",
        ];

        ScanSource(FenceSourceScan.Read(source)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new KeyValuePair<string, List<int>>("ModuleIdsAsync", [8]));
    }

    private sealed record HelperCall(string Path, int Line, string Member);

    private static IEnumerable<HelperCall> HelperCalls(Regex call) =>
        FenceSourceScan.AppSources.SelectMany(f => f.Lines
            .Select((line, i) => (line, i))
            .Where(x => call.IsMatch(x.line.Code) && !FenceSourceScan.IsMethodDeclaration(x.line.Code))
            .Select(x => new HelperCall(f.Relative, x.i + 1, FenceSourceScan.EnclosingMember(f.Lines, x.i))));

    private static readonly Lazy<Dictionary<string, Dictionary<string, List<int>>>> Scanned = new(() =>
        FenceSourceScan.AppSources
            .Select(f => (f.Relative, Members: ScanSource(f.Lines)))
            .Where(f => f.Members.Count > 0)
            .ToDictionary(f => f.Relative, f => f.Members, StringComparer.Ordinal));

    private static Dictionary<string, Dictionary<string, List<int>>> Scan() => Scanned.Value;

    private static Dictionary<string, List<int>> ScanSource(FenceSourceScan.Line[] lines)
    {
        var members = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var n = RawSql.Matches(lines[i].Code).Count;
            if (n == 0) continue;
            var member = FenceSourceScan.EnclosingMember(lines, i);
            if (!members.TryGetValue(member, out var at)) members[member] = at = [];
            at.AddRange(Enumerable.Repeat(i + 1, n));
        }
        return members;
    }
}
