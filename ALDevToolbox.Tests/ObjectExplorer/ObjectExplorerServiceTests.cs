using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// End-to-end tests for <see cref="ObjectExplorerService"/>. Each test seeds
/// the DB by running real <c>.app</c> fixtures through
/// <see cref="ReleaseImportService"/> first — that keeps the fixture data
/// honest (no hand-rolled rows that drift from what production writes) and
/// also exercises the import-then-query interaction in one go.
///
/// The find-references tests are the headline: they pin the recursive-CTE
/// chain walk + shadowing behaviour that's the whole point of the new schema.
/// </summary>
public sealed class ObjectExplorerServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    private static readonly Guid BaseAppId = Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972");

    private ReleaseImportService NewImporter(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext, new ALDevToolbox.Services.Translation.TranslationMemoryService(ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance), NullLogger<TranslationImportService>.Instance),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance);

    private ProjectAccess NewAccess(Data.AppDbContext ctx) => new(ctx, _db.OrgContext);

    private ObjectExplorerService NewQuery(Data.AppDbContext ctx) =>
        new(ctx, NewReferences(ctx), NewAccess(ctx), NullLogger<ObjectExplorerService>.Instance);

    private SourceViewerService NewSourceViewer(Data.AppDbContext ctx) =>
        new(ctx, NewReferences(ctx), NewVisibility(ctx));

    private SourceVisibility NewVisibility(Data.AppDbContext ctx) => new(ctx, NewAccess(ctx));

    private ReleaseComparisonService NewComparison(Data.AppDbContext ctx) =>
        new(ctx, NewAccess(ctx), NullLogger<ReleaseComparisonService>.Instance);

    private ObjectSearchService NewSearch(Data.AppDbContext ctx) =>
        new(ctx, NewAccess(ctx));

    private ReferenceQueryService NewReferences(Data.AppDbContext ctx) =>
        new(ctx, NewAccess(ctx), _db.OrgContext, NullLogger<ReferenceQueryService>.Instance);

    private ReferenceSessionService NewSessions(Data.AppDbContext ctx) =>
        new(new Microsoft.Extensions.Caching.Memory.MemoryCache(
                Microsoft.Extensions.Options.Options.Create(
                    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            NewReferences(ctx), ctx, new ReferenceResolver(ctx), NewAccess(ctx),
            NullLogger<ReferenceSessionService>.Instance);

    /// <summary>
    /// Imports the two fixtures into one Release. Returns the Release id so
    /// follow-up assertions can navigate it without re-seeding.
    /// </summary>
    private async Task<int> SeedSingleReleaseAsync()
    {
        await using var ctx = _db.NewContext();
        var svc = NewImporter(ctx);
        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[]
            {
                new AppFileUpload("Microsoft_DK_Core.app", s1, null),
                new AppFileUpload("Microsoft_OIOUBL.app", s2, null),
            }));
        return summary.ReleaseId;
    }

    // ── List Releases / Module / Object surfaces ────────────────────────

    [Fact]
    public async Task ListReleasesAsync_returns_active_releases_sorted_by_label()
    {
        await SeedSingleReleaseAsync();

        await using var read = _db.NewContext();
        var rows = await NewQuery(read).ListReleasesAsync();
        rows.Should().ContainSingle(r => r.Label == "BC 25.18 DK")
            .Which.Kind.Should().Be("first_party");
    }

    [Fact]
    public async Task ListReleasesAsync_includes_source_file_count_and_content_length()
    {
        await SeedSingleReleaseAsync();

        await using var read = _db.NewContext();
        var row = (await NewQuery(read).ListReleasesAsync()).Single();
        row.SourceFileCount.Should().BeGreaterThan(0,
            because: "both DK Core and OIOUBL ship .al source embedded in their .apps");
        row.SourceContentLength.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetReleaseAsync_returns_module_count_and_parent_label()
    {
        await using (var ctx = _db.NewContext())
        {
            var imp = NewImporter(ctx);
            await using var sParent = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
            var parent = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "BC 25.18 DK Parent", "first_party", null, null,
                new[] { new AppFileUpload("dk.app", sParent, null) }));

            await using var sChild = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
            await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "Customer X on BC 25.18", "project", parent.ReleaseId, null,
                new[] { new AppFileUpload("oioubl.app", sChild, null) }));
        }

        await using var read = _db.NewContext();
        var query = NewQuery(read);
        // The child is a project-kind release, which the global list excludes by
        // default now (it lives in the Artifacts tool) — opt in to find it here.
        var releases = await query.ListReleasesAsync(includeProjectBuilds: true);
        var child = releases.Single(r => r.Label == "Customer X on BC 25.18");
        var detail = await query.GetReleaseAsync(child.Id);
        detail.Should().NotBeNull();
        detail!.ParentLabel.Should().Be("BC 25.18 DK Parent");
        detail.ModuleCount.Should().Be(1);
    }

    [Fact]
    public async Task GetProjectBuildResultsForReleasesAsync_groups_rows_by_release()
    {
        // Two project releases, each with its own build report. The batch read
        // the Object Explorer project drill-down uses must return both keyed by
        // release id, ordered by app name within a release.
        int relA, relB;
        await using (var write = _db.NewContext())
        {
            relA = await SeedProjectReleaseAsync(write, "Acme on BC 26.0");
            relB = await SeedProjectReleaseAsync(write, "Acme on BC 26.0");

            write.OeProjectBuildResults.AddRange(
                NewBuildResult(relA, "Zeta Extension", ProjectBuildResultStatus.Ingested, "zzz1111222233334444", DateTime.UtcNow.AddDays(-1)),
                NewBuildResult(relA, "Alpha Extension", ProjectBuildResultStatus.Compiled, "aaa1111222233334444", DateTime.UtcNow.AddDays(-1)),
                NewBuildResult(relB, "Alpha Extension", ProjectBuildResultStatus.Ingested, "bbb5555666677778888", DateTime.UtcNow));
            await write.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var byRelease = await NewQuery(read).GetProjectBuildResultsForReleasesAsync(new[] { relA, relB });

        byRelease.Should().ContainKeys(relA, relB);
        byRelease[relA].Should().HaveCount(2);
        byRelease[relA].Select(r => r.AppName).Should().Equal(new[] { "Alpha Extension", "Zeta Extension" },
            because: "rows within a release are ordered by app name");
        byRelease[relB].Should().ContainSingle()
            .Which.CommitSha.Should().Be("bbb5555666677778888");
    }

    [Fact]
    public async Task GetProjectBuildResultsForReleasesAsync_returns_empty_for_empty_input()
    {
        await using var read = _db.NewContext();
        var result = await NewQuery(read).GetProjectBuildResultsForReleasesAsync(Array.Empty<int>());
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListReleasesAsync_excludes_project_builds_by_default_but_includes_them_on_request()
    {
        // Project builds are unlisted in the global Object Explorer surfaces; they
        // belong to the Artifacts tool. See .design/artifacts.md.
        await SeedSingleReleaseAsync();
        await using (var seed = _db.NewContext())
        {
            await SeedProjectReleaseAsync(seed, "CRONUS on BC 26.0");
        }

        await using var read = _db.NewContext();
        var query = NewQuery(read);

        var defaultList = await query.ListReleasesAsync();
        defaultList.Should().NotContain(r => r.Kind == "project",
            "project builds are excluded from the global release list by default");

        var withProjects = await query.ListReleasesAsync(includeProjectBuilds: true);
        withProjects.Should().Contain(r => r.Label == "CRONUS on BC 26.0",
            "the project-scoped callers can still opt in");
    }

    /// <summary>
    /// The releases page and the compare picker ask for browsable releases only:
    /// ready ones, without the symbol packages a build pulled from the feeds with no
    /// files (#1092). The filter runs in the query; an admin import without source and
    /// a feed package that is still importing both stay.
    /// </summary>
    [Fact]
    public async Task ListReleasesAsync_browsable_only_leaves_out_empty_feed_symbol_packages_and_unready_rows()
    {
        await using (var seed = _db.NewContext())
        {
            seed.OeReleases.AddRange(
                BareRelease("Feed with files", "symbols:a:1.0", "ready", files: 4),
                BareRelease("Feed without files", "symbols:b:1.0", "ready", files: 0),
                BareRelease("Feed still importing", "symbols:c:1.0", "importing", files: 0),
                BareRelease("Admin import without source", null, "ready", files: 0),
                BareRelease("Failed import", null, "failed", files: 3));
            await seed.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var query = NewQuery(read);

        (await query.ListReleasesAsync(browsableOnly: true)).Select(r => r.Label).Should()
            .BeEquivalentTo("Feed with files", "Admin import without source");
        (await query.ListReleasesAsync()).Should().HaveCount(5,
            "the admin lists still see every row");
    }

    private static OeRelease BareRelease(string label, string? dedupKey, string status, int files) =>
        new()
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = label,
            Kind = "third_party",
            Status = status,
            DedupKey = dedupKey,
            SourceFileCount = files,
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    /// <summary>Inserts a bare project-kind Release (no modules) and returns its id.</summary>
    private static async Task<int> SeedProjectReleaseAsync(Data.AppDbContext ctx, string label)
    {
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = label,
            Kind = "project",
            Status = "ready",
            ProjectName = "Acme",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    private static OeProjectBuildResult NewBuildResult(int releaseId, string appName, string status, string commitSha, DateTime commitDate) =>
        new()
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = releaseId,
            AppName = appName,
            AppId = Guid.NewGuid().ToString(),
            Status = status,
            RepoUrl = "https://github.com/acme/" + appName.Replace(' ', '-').ToLowerInvariant(),
            CommitSha = commitSha,
            CommitDate = commitDate,
            CreatedAt = DateTime.UtcNow,
        };

    [Fact]
    public async Task GetModuleHeaderAsync_returns_the_module_identity_and_its_release()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewQuery(read);
        var module = (await query.ListModulesAsync(releaseId, new ModuleListFilter()))
            .Single(m => m.Name == "OIOUBL");

        var header = await query.GetModuleHeaderAsync(module.Id);

        header.Should().NotBeNull();
        header!.Name.Should().Be("OIOUBL");
        header.AppId.Should().Be(module.AppId);
        header.ReleaseId.Should().Be(releaseId);
        header.ReleaseLabel.Should().Be("BC 25.18 DK");
    }

    [Fact]
    public async Task GetModuleHeaderAsync_is_null_for_a_module_that_does_not_exist()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        (await NewQuery(read).GetModuleHeaderAsync(987654)).Should().BeNull();
    }

    [Fact]
    public async Task GetReleaseLabelAsync_returns_the_label_and_null_for_an_unknown_release()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewQuery(read);

        (await query.GetReleaseLabelAsync(releaseId)).Should().Be("BC 25.18 DK");
        (await query.GetReleaseLabelAsync(987654)).Should().BeNull();
    }

    [Fact]
    public async Task ListModulesAsync_filters_by_search_substring()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var modules = await NewQuery(read).ListModulesAsync(releaseId, new ModuleListFilter(Search: "oioubl"));
        modules.Should().ContainSingle().Which.Name.Should().Be("OIOUBL");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_finds_objects_across_modules()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewSearch(read);

        // OIOUBL declares "OIOUBL-Profile" (table 13630). DK Core has no table
        // named like that. A release-wide search for "OIOUBL-Profile" should
        // return the OIOUBL hit only, with the module name joined inline.
        var hits = await query.SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "OIOUBL-Profile"));
        hits.Should().NotBeEmpty();
        hits.Should().Contain(h => h.Name == "OIOUBL-Profile" && h.ModuleName == "OIOUBL");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_includeInherited_surfaces_parent_objects()
    {
        // Browsing a project Release must be able to reach base objects that
        // live in the parent Release — the whole point of the inherited toggle.
        int parentId, childId;
        await using (var ctx = _db.NewContext())
        {
            var imp = NewImporter(ctx);
            await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
            var parent = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "BC 25.18 Parent", "first_party", null, null,
                new[] { new AppFileUpload("dk.app", s1, null) }));
            parentId = parent.ReleaseId;

            await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
            var child = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "Customer X", "project", parent.ReleaseId, null,
                new[] { new AppFileUpload("oioubl.app", s2, null) }));
            childId = child.ReleaseId;
        }

        await using var read = _db.NewContext();
        var search = NewSearch(read);

        // An object whose home module is the parent's DK Core — i.e. inherited
        // from the child's point of view.
        var parentObjectName = await read.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == parentId && o.Module!.Name == "DK Core")
            .Select(o => o.Name)
            .FirstAsync();

        var filter = new ObjectListFilter(Search: parentObjectName);

        var ownReleaseOnly = await search.SearchObjectsInReleaseAsync(
            childId, filter, take: 500, includeInherited: false);
        ownReleaseOnly.Should().NotContain(o => o.ModuleName == "DK Core",
            because: "without the toggle the child search sees only its own modules");

        var withInherited = await search.SearchObjectsInReleaseAsync(
            childId, filter, take: 500, includeInherited: true);
        withInherited.Should().Contain(o => o.Name == parentObjectName && o.ModuleName == "DK Core",
            because: "include-inherited widens the search to the parent (base) Release");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_filters_by_kind_across_modules()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewSearch(read);

        // Both modules contribute codeunits. With kind=codeunit and no name
        // filter, we should get every codeunit in the Release.
        var all = await query.SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Kinds: new[] { "codeunit" }));
        all.Should().OnlyContain(o => o.Kind == "codeunit");
        all.Select(o => o.ModuleName).Distinct().Should().Contain(new[] { "DK Core", "OIOUBL" });
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_filters_by_multiple_kinds()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        // The multi-select "Object type" filter passes several kinds; results
        // must be the union (any of them), not the intersection.
        var hits = await NewSearch(read).SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Kinds: new[] { "codeunit", "table" }), take: 1000);

        hits.Should().NotBeEmpty();
        hits.Should().OnlyContain(o => o.Kind == "codeunit" || o.Kind == "table");
        hits.Select(o => o.Kind).Distinct().Should().Contain("codeunit").And.Contain("table");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_kind_prefix_scopes_search_to_that_kind()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewSearch(read);

        // `t:OIOUBL-Profile` is a table-scoped search for the name; only the
        // OIOUBL-Profile table comes back, never a like-named codeunit/page.
        var named = await query.SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "t:OIOUBL-Profile"));
        named.Should().NotBeEmpty();
        named.Should().OnlyContain(o => o.Kind == "table");
        named.Should().Contain(o => o.Name == "OIOUBL-Profile");

        // Bare `t:` (no name remainder) returns every table in the release.
        var bare = await query.SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "t:"), take: 1000);
        bare.Should().NotBeEmpty();
        bare.Should().OnlyContain(o => o.Kind == "table");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_kind_prefix_ands_with_dropdown_kinds()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        // Prefix kind (codeunit) and an explicit dropdown kind (table) are
        // disjoint, so ANDing them yields nothing — the prefix narrows, it
        // doesn't replace, the dropdown selection.
        var hits = await NewSearch(read).SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Kinds: new[] { "table" }, Search: "c:"), take: 1000);
        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchObjectsPageInReleaseAsync_returns_contiguous_sorted_windows()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var search = NewSearch(read);

        // Fetch the whole name-sorted set, then prove the lazy-load windows are
        // exact contiguous slices of it (no overlap, no gaps) with a stable
        // total. Compared by id against the service's own order so the test is
        // agnostic to the database collation.
        var all = await search.SearchObjectsPageInReleaseAsync(
            releaseId, new ObjectListFilter(), moduleId: null, namespacePrefix: null,
            ObjectSortColumn.Name, descending: false, skip: 0, take: 100_000);
        all.Rows.Count.Should().BeGreaterThan(20);
        all.TotalCount.Should().Be(all.Rows.Count);

        var firstTen = await search.SearchObjectsPageInReleaseAsync(
            releaseId, new ObjectListFilter(), null, null,
            ObjectSortColumn.Name, descending: false, skip: 0, take: 10);
        var nextTen = await search.SearchObjectsPageInReleaseAsync(
            releaseId, new ObjectListFilter(), null, null,
            ObjectSortColumn.Name, descending: false, skip: 10, take: 10);

        firstTen.Rows.Select(r => r.Id).Should().Equal(all.Rows.Take(10).Select(r => r.Id));
        nextTen.Rows.Select(r => r.Id).Should().Equal(all.Rows.Skip(10).Take(10).Select(r => r.Id));
        firstTen.TotalCount.Should().Be(all.TotalCount);
    }

    [Fact]
    public async Task DependencyCount_is_stamped_from_manifest_at_import()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var modules = await read.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .Select(m => new { m.Name, m.DependenciesJson, m.DependencyCount })
            .ToListAsync();

        modules.Should().NotBeEmpty();
        foreach (var m in modules)
        {
            var expected = System.Text.Json.JsonDocument.Parse(m.DependenciesJson).RootElement.GetArrayLength();
            m.DependencyCount.Should().Be(expected, "dependency_count mirrors the JSON deps for {0}", m.Name);
        }
    }

    [Fact]
    public async Task Default_sort_floats_fewest_dependency_module_first_on_ties()
    {
        // Two modules in one release defining the same (kind, id); the default
        // grid order should surface the foundational (fewer-dependency) one
        // first so System/Base content beats partner extensions on a tie.
        int releaseId;
        await using (var write = _db.NewContext())
        {
            var release = new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId,
                Label = "Dep-sort fixture",
                Kind = "first_party",
                Status = "ready",
                ImportedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            write.OeReleases.Add(release);
            await write.SaveChangesAsync();
            releaseId = release.Id;

            var baseApp = new OeModule
            {
                OrganizationId = TestDb.DefaultOrgId,
                ReleaseId = releaseId,
                AppId = Guid.NewGuid(),
                Name = "Base Application",
                Publisher = "Microsoft",
                Version = "1.0.0.0",
                CreatedAt = DateTime.UtcNow,
                DependencyCount = 1,
            };
            var partner = new OeModule
            {
                OrganizationId = TestDb.DefaultOrgId,
                ReleaseId = releaseId,
                AppId = Guid.NewGuid(),
                Name = "Partner Extension",
                Publisher = "Acme",
                Version = "1.0.0.0",
                CreatedAt = DateTime.UtcNow,
                DependencyCount = 6,
            };
            write.OeModules.AddRange(baseApp, partner);
            await write.SaveChangesAsync();

            write.OeModuleObjects.Add(new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = partner.Id,
                Kind = "table",
                ObjectId = 18,
                Name = "Customer (extended)",
                LineNumber = 1,
            });
            write.OeModuleObjects.Add(new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = baseApp.Id,
                Kind = "table",
                ObjectId = 18,
                Name = "Customer",
                LineNumber = 1,
            });
            await write.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var page = await NewSearch(read).SearchObjectsPageInReleaseAsync(
            releaseId, new ObjectListFilter(), moduleId: null, namespacePrefix: null,
            ObjectSortColumn.Default, descending: false, skip: 0, take: 10);

        page.Rows.Where(r => r.ObjectId == 18).Select(r => r.ModuleName)
            .Should().Equal("Base Application", "Partner Extension");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_orders_results_by_kind_then_id()
    {
        // The legacy VersionBrowser sorted by (Type, ID) so a release-wide
        // browse groups objects by AL kind first, then by object number —
        // the order BC devs read object lists in. Pin that contract:
        // result list must be strictly non-decreasing on Kind, and within
        // a kind block strictly non-decreasing on ObjectId.
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var hits = await NewSearch(read).SearchObjectsInReleaseAsync(
            releaseId,
            new ObjectListFilter(),
            moduleId: null,
            namespacePrefix: null,
            take: 500);

        hits.Should().NotBeEmpty();
        for (var i = 1; i < hits.Count; i++)
        {
            var prev = hits[i - 1];
            var cur = hits[i];
            var kindCmp = string.CompareOrdinal(prev.Kind, cur.Kind);
            kindCmp.Should().BeLessThanOrEqualTo(0,
                "results are ordered by Kind first");
            if (kindCmp == 0 && prev.ObjectId is { } pid && cur.ObjectId is { } cid)
            {
                pid.Should().BeLessThanOrEqualTo(cid,
                    "within a Kind block, results are ordered by ObjectId");
            }
        }
    }

    [Fact]
    public async Task Object_lookup_by_name_is_case_insensitive_and_literal()
    {
        // #690 swapped LOWER(name) = @p for a wildcard-free ILike so the name
        // trigram index stays usable. That must not change behaviour: the match
        // is still case-insensitive, and a name containing the SQL wildcards
        // %/_ (underscores are common in AL object names) has to match
        // literally rather than acting as a pattern.
        var releaseId = await SeedSingleReleaseAsync();
        await SeedObjectsAsync(releaseId,
            ("Sales_Header", 9000190),
            ("SalesXHeader", 9000191),
            ("Discount % Setup", 9000192),
            ("Discount ZZ Setup", 9000193));
        await using var read = _db.NewContext();
        var query = NewQuery(read);

        // Case-insensitive on both the exact-lookup entry points.
        (await query.GetObjectByNameAsync(releaseId, "table", "sales_header"))!
            .Name.Should().Be("Sales_Header");
        (await query.GetObjectOutlineAsync(releaseId, "TABLE", "SALES_HEADER"))!
            .Name.Should().Be("Sales_Header");

        // The '_' is a literal, not the single-character wildcard, so the
        // lookup must not resolve to "SalesXHeader"...
        (await query.GetObjectByNameAsync(releaseId, "table", "SalesXHeader"))!
            .Name.Should().Be("SalesXHeader");
        // ...and '%' must not match any run of characters.
        (await query.GetObjectByNameAsync(releaseId, "table", "Discount % Setup"))!
            .Name.Should().Be("Discount % Setup");
        (await query.GetObjectOutlineAsync(releaseId, "table", "Discount % Setup"))!
            .Name.Should().Be("Discount % Setup");

        // A name that only differs by the wildcard interpretation is not found.
        (await query.GetObjectByNameAsync(releaseId, "table", "Sales%Header"))
            .Should().BeNull("'%' is escaped to a literal, so it matches nothing here");
    }

    [Fact]
    public async Task ListObjectsAsync_search_treats_wildcards_literally()
    {
        // Companion to the lookup test: the module drill-down search is a
        // substring ILike now, so a term containing %/_ must still narrow.
        var releaseId = await SeedSingleReleaseAsync();
        await SeedObjectsAsync(releaseId,
            ("Sales_Header", 9000194),
            ("SalesXHeader", 9000195));
        await using var read = _db.NewContext();
        long moduleId = await read.OeModules
            .Where(m => m.ReleaseId == releaseId).Select(m => m.Id).FirstAsync();

        var page = await NewQuery(read).ListObjectsAsync(moduleId,
            new ObjectListFilter(Search: "sales_head"), skip: 0, take: 50);
        page.Rows.Should().Contain(o => o.Name == "Sales_Header");
        page.Rows.Should().NotContain(o => o.Name == "SalesXHeader",
            because: "'_' is escaped, so it is not the single-character wildcard");
    }

    /// <summary>
    /// Adds synthetic objects to the first module of <paramref name="releaseId"/>
    /// so the search-ranking tests have deterministic names to assert on
    /// rather than leaning on fixture-specific object naming.
    /// </summary>
    private async Task SeedObjectsAsync(int releaseId, params (string Name, int ObjectId)[] objects)
    {
        await using var write = _db.NewContext();
        var moduleId = await write.OeModules
            .Where(m => m.ReleaseId == releaseId)
            .Select(m => m.Id)
            .FirstAsync();
        foreach (var (name, objectId) in objects)
        {
            write.OeModuleObjects.Add(new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = moduleId,
                Kind = "table",
                ObjectId = objectId,
                Name = name,
                LineNumber = 1,
            });
        }
        await write.SaveChangesAsync();
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_matches_all_whitespace_separated_tokens()
    {
        // BC "Tell Me" style: the search box splits on whitespace and every
        // token has to appear (case-insensitive substring) in the object
        // name. "sal set" finds "Sales & Receivables Setup"; flipping one
        // token to garbage drops the row from the result set.
        var releaseId = await SeedSingleReleaseAsync();
        await SeedObjectsAsync(releaseId, ("Sales & Receivables Setup", 9000050));
        await using var read = _db.NewContext();
        var query = NewSearch(read);

        var hits = await query.SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "sal set"));
        hits.Should().Contain(h => h.Name == "Sales & Receivables Setup");

        var miss = await query.SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "sal xyz"));
        miss.Should().NotContain(h => h.Name == "Sales & Receivables Setup");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_single_numeric_token_still_matches_by_id()
    {
        // The numeric-id path is the long-standing contract for the search
        // box: typing an object number finds the row with that ObjectId
        // even when no name token matches. The token parser must keep
        // routing single-number queries through that path.
        var releaseId = await SeedSingleReleaseAsync();
        await SeedObjectsAsync(releaseId, ("Posted Sales Invoice", 9000060));
        await using var read = _db.NewContext();

        var hits = await NewSearch(read).SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "9000060"));
        hits.Should().Contain(h => h.ObjectId == 9000060 && h.Name == "Posted Sales Invoice");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_ranks_word_boundary_hits_above_mid_word_hits()
    {
        // Two rows share a token but differ on where it lands: "Header
        // Field" starts with "head" (boundary hit); "subheaderness widget"
        // buries "head" mid-word. The ranker must surface the boundary hit
        // first.
        var releaseId = await SeedSingleReleaseAsync();
        await SeedObjectsAsync(releaseId,
            ("subheaderness widget", 9000001),
            ("Header Field", 9000002));

        await using var read = _db.NewContext();
        var hits = await NewSearch(read).SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "head"));

        var boundaryIdx = hits.FindIndex(h => h.Name == "Header Field");
        var midWordIdx = hits.FindIndex(h => h.Name == "subheaderness widget");
        boundaryIdx.Should().BeGreaterThanOrEqualTo(0);
        midWordIdx.Should().BeGreaterThanOrEqualTo(0);
        boundaryIdx.Should().BeLessThan(midWordIdx,
            because: "word-boundary token hits rank above mid-word hits");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_pascal_case_boundaries_count_as_word_boundaries()
    {
        // Identifier-style names ("SalesHeader") have no separator, but
        // the lower→upper transition reads like a word boundary to a BC
        // dev. "head" in "SalesHeader" should outrank "head" buried in
        // "unsalesheaderwrap".
        var releaseId = await SeedSingleReleaseAsync();
        await SeedObjectsAsync(releaseId,
            ("SalesHeader", 9000010),
            ("unsalesheaderwrap", 9000011));

        await using var read = _db.NewContext();
        var hits = await NewSearch(read).SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "head"));

        var pascalIdx = hits.FindIndex(h => h.Name == "SalesHeader");
        var nonBoundaryIdx = hits.FindIndex(h => h.Name == "unsalesheaderwrap");
        pascalIdx.Should().BeGreaterThanOrEqualTo(0);
        nonBoundaryIdx.Should().BeGreaterThanOrEqualTo(0);
        pascalIdx.Should().BeLessThan(nonBoundaryIdx,
            because: "lower→upper PascalCase transitions count as word boundaries");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_double_quoted_phrase_matches_exactly()
    {
        // A quoted run is an exact-match token: "sales header" matches only the
        // object whose whole name equals it — not one that merely contains the
        // phrase ("Posted Sales Header") nor one with the words scattered
        // ("Sales Invoice Header"). The unquoted search keeps all three (substring).
        var releaseId = await SeedSingleReleaseAsync();
        await SeedObjectsAsync(releaseId,
            ("Sales Header", 9000070),
            ("Sales Invoice Header", 9000071),
            ("Posted Sales Header", 9000072));
        await using var read = _db.NewContext();
        var query = NewSearch(read);

        var quoted = await query.SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "\"sales header\""));
        quoted.Should().Contain(h => h.Name == "Sales Header");
        quoted.Should().NotContain(h => h.Name == "Sales Invoice Header");
        quoted.Should().NotContain(h => h.Name == "Posted Sales Header");

        var unquoted = await query.SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "sales header"));
        unquoted.Should().Contain(h => h.Name == "Sales Header");
        unquoted.Should().Contain(h => h.Name == "Sales Invoice Header");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_id_range_filters_by_object_id()
    {
        // "50000..50001" returns only objects whose ObjectId falls in the
        // inclusive range, regardless of name.
        var releaseId = await SeedSingleReleaseAsync();
        await SeedObjectsAsync(releaseId,
            ("Alpha", 50000),
            ("Bravo", 50001),
            ("Charlie", 50002));
        await using var read = _db.NewContext();

        var hits = await NewSearch(read).SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "50000..50001"));
        hits.Should().Contain(h => h.ObjectId == 50000);
        hits.Should().Contain(h => h.ObjectId == 50001);
        hits.Should().NotContain(h => h.ObjectId == 50002);
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_matches_the_cal_version_list()
    {
        // The C/AL Version List is searchable on a contains match, so `*CON*`
        // finds an object tagged with it even though its name has no "CON" (#271).
        var releaseId = await SeedSingleReleaseAsync();
        await using (var write = _db.NewContext())
        {
            var moduleId = await write.OeModules
                .Where(m => m.ReleaseId == releaseId).Select(m => m.Id).FirstAsync();
            write.OeModuleObjects.Add(new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = moduleId,
                Kind = "table",
                ObjectId = 50000,
                Name = "Acme Setup",
                VersionList = "NAVW111.00,CON001",
                LineNumber = 1,
            });
            await write.SaveChangesAsync();
        }
        await using var read = _db.NewContext();

        var hits = await NewSearch(read).SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "*CON*"));
        hits.Should().Contain(h => h.Name == "Acme Setup",
            because: "the version list contains CON even though the name does not");
        hits.Should().Contain(h => h.VersionList != null && h.VersionList.Contains("CON001"),
            because: "the matched row carries its version list for display");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_bare_numeric_matches_the_cal_version_list()
    {
        // A bare number takes the id fast-path, which must still match the
        // version list: "111" finds an object tagged NAVW111.00 whose id and
        // name share nothing with the term (#271, numeric branch).
        var releaseId = await SeedSingleReleaseAsync();
        await using (var write = _db.NewContext())
        {
            var moduleId = await write.OeModules
                .Where(m => m.ReleaseId == releaseId).Select(m => m.Id).FirstAsync();
            write.OeModuleObjects.Add(new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = moduleId,
                Kind = "table",
                ObjectId = 50000,
                Name = "Acme Setup",
                VersionList = "NAVW111.00",
                LineNumber = 1,
            });
            await write.SaveChangesAsync();
        }
        await using var read = _db.NewContext();

        var hits = await NewSearch(read).SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "111"));
        hits.Should().Contain(h => h.Name == "Acme Setup",
            because: "the version list contains 111 even though the id and name do not");
    }

    [Fact]
    public async Task ListObjectsAsync_search_matches_the_cal_version_list()
    {
        // The module drill-down's simpler list query must honour the version
        // list too — a C/AL import is a single module, so this is where users
        // often search it (#271, module path).
        var releaseId = await SeedSingleReleaseAsync();
        long moduleId;
        await using (var write = _db.NewContext())
        {
            moduleId = await write.OeModules
                .Where(m => m.ReleaseId == releaseId).Select(m => m.Id).FirstAsync();
            write.OeModuleObjects.Add(new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = moduleId,
                Kind = "table",
                ObjectId = 50000,
                Name = "Acme Setup",
                VersionList = "NAVDK14.49,CON001",
                LineNumber = 1,
            });
            await write.SaveChangesAsync();
        }
        await using var read = _db.NewContext();

        var page = await NewQuery(read).ListObjectsAsync(moduleId,
            new ObjectListFilter(Search: "CON001"), skip: 0, take: 50);
        page.Rows.Should().Contain(o => o.Name == "Acme Setup",
            because: "the version list matches even though the name does not");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_trailing_wildcard_matches_prefix()
    {
        // "sales*" matches names starting with "sales" (case-insensitive), and
        // excludes a name that merely contains "sales" mid-string.
        var releaseId = await SeedSingleReleaseAsync();
        await SeedObjectsAsync(releaseId,
            ("Sales Header", 9000090),
            ("Posted Sales Header", 9000091));
        await using var read = _db.NewContext();

        var hits = await NewSearch(read).SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "sales*"));
        hits.Should().Contain(h => h.Name == "Sales Header");
        hits.Should().NotContain(h => h.Name == "Posted Sales Header");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_negated_token_excludes_matches()
    {
        // "sales -temp" keeps the rows whose name contains "sales" but drops
        // any that also contain "temp", so a negated token filters without
        // contributing to ranking.
        var releaseId = await SeedSingleReleaseAsync();
        await SeedObjectsAsync(releaseId,
            ("Sales Setup", 9000080),
            ("Sales Setup Temp", 9000081));
        await using var read = _db.NewContext();

        var hits = await NewSearch(read).SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "sales -temp"));
        hits.Should().Contain(h => h.Name == "Sales Setup");
        hits.Should().NotContain(h => h.Name == "Sales Setup Temp");
    }

    [Fact]
    public async Task GetFileHeaderAsync_returns_release_and_module_context_without_loading_content()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var ctx = read;

        var fileId = ctx.OeModuleFiles.AsQueryable()
            .First(f => f.Path.Contains("DKCoreEventSubscribers")).Id;

        var header = await NewSourceViewer(ctx).GetFileHeaderAsync(fileId);
        header.Should().NotBeNull();
        header!.ModuleName.Should().Be("DK Core");
        header.ReleaseLabel.Should().Be("BC 25.18 DK");
        header.LineCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SearchProceduresInReleaseAsync_finds_procedures_across_modules()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var hits = await NewSearch(read).SearchProceduresInReleaseAsync(
            releaseId, search: null, moduleId: null, take: 500);

        hits.Should().NotBeEmpty();
        // Every hit's procedure kind is one of the procedure-shaped symbols.
        hits.Should().OnlyContain(h =>
            h.ProcedureKind == "procedure"
            || h.ProcedureKind == "internal_procedure"
            || h.ProcedureKind == "trigger");
    }

    [Fact]
    public async Task SearchContentInReleaseAsync_finds_lines_matching_a_substring()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var hits = await NewSearch(read).SearchContentInReleaseAsync(
            releaseId, search: "codeunit", moduleId: null);
        hits.Should().NotBeEmpty(
            because: "every codeunit's source line starts with the 'codeunit' keyword");
        hits.Should().OnlyContain(h =>
            h.Snippet.Contains("codeunit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchContentInReleaseAsync_returns_empty_for_terms_below_the_trigram_minimum()
    {
        // A one- or two-character term can't form a trigram, so it can't use
        // the content GIN index — the service rejects it up front (empty)
        // rather than sequential-scanning the whole content store. The same
        // source the substring test matches on ("codeunit") is present, so
        // this proves the short term is filtered, not merely absent.
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var search = NewSearch(read);

        var tooShort = await search.SearchContentInReleaseAsync(releaseId, "co", moduleId: null);
        tooShort.Should().BeEmpty();

        var atMinimum = await search.SearchContentInReleaseAsync(releaseId, "cod", moduleId: null);
        atMinimum.Should().NotBeEmpty(
            because: "three characters meets the trigram minimum and 'cod' occurs in 'codeunit'");
    }

    [Fact]
    public async Task ListDeclarationsInFileAsync_returns_object_headers_with_column_positions()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var fileId = read.OeModuleFiles.AsQueryable()
            .First(f => f.Path.Contains("DKCoreEventSubscribers")).Id;

        var decls = await NewSourceViewer(read).ListDeclarationsInFileAsync(fileId);
        decls.Should().NotBeEmpty();
        // The codeunit name lives on its declaration line; the helper must
        // surface columns matching the quoted-or-bare token.
        decls.Should().Contain(d =>
            d.Name == "DK Core Event Subscribers"
            && d.ColumnStart >= 1
            && d.ColumnEnd > d.ColumnStart
            && !d.IsMemberSymbol,
            because: "object headers carry IsMemberSymbol = false so the host routes to /from-symbol/");
    }

    [Fact]
    public async Task ListDeclarationsInFileAsync_includes_procedure_declarations_as_member_symbols()
    {
        // Procedure / field / trigger declarations need to be in the
        // declaration list too so the source viewer underlines them and
        // the right-click "Find references" routes through
        // /from-member-symbol/. They carry IsMemberSymbol = true so the
        // host can pick the right endpoint without reasoning about the
        // kind string.
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var fileId = read.OeModuleFiles.AsQueryable()
            .First(f => f.Path.Contains("DKCoreEventSubscribers")).Id;

        var decls = await NewSourceViewer(read).ListDeclarationsInFileAsync(fileId);

        decls.Where(d => d.IsMemberSymbol).Should().NotBeEmpty(
            because: "the event subscribers codeunit declares procedures / event subscribers");
        decls.Where(d => d.IsMemberSymbol).Should().OnlyContain(d =>
            d.Line >= 1 && d.ColumnStart >= 1 && d.ColumnEnd > d.ColumnStart);
        // The symbol id must come from oe_module_symbols (not the object
        // id) — round-trip through the DbSet to verify.
        var symbolIds = decls.Where(d => d.IsMemberSymbol).Select(d => d.SymbolId).Distinct().ToList();
        var existing = await read.OeModuleSymbols.AsNoTracking()
            .Where(s => symbolIds.Contains(s.Id)).CountAsync();
        existing.Should().Be(symbolIds.Count,
            because: "every IsMemberSymbol row's id maps to a real oe_module_symbols row");
    }

    [Fact]
    public async Task ListDeclarationsInFileAsync_returns_declarations_sorted_by_position()
    {
        // Regression for the source-viewer crash ("Ranges must be added sorted
        // by `from` position"): the helper appends object headers before member
        // symbols, so a file shipping more than one object yields an unsorted
        // list — object B's header (line 50) lands ahead of object A's member
        // (line 10). CodeMirror's RangeSetBuilder rejects descending `from`, so
        // the service must return declarations ordered by (line, column).
        long fileId;
        await using (var write = _db.NewContext())
        {
            var release = new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId,
                Label = "Multi-object fixture",
                Kind = "first_party",
                Status = "ready",
                ImportedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            write.OeReleases.Add(release);
            await write.SaveChangesAsync();

            var module = new OeModule
            {
                OrganizationId = TestDb.DefaultOrgId,
                ReleaseId = release.Id,
                AppId = Guid.NewGuid(),
                Name = "Bundle",
                Publisher = "Acme",
                Version = "1.0.0.0",
                CreatedAt = DateTime.UtcNow,
                DependencyCount = 0,
            };
            write.OeModules.Add(module);
            await write.SaveChangesAsync();

            // 55-line source: object A's header on line 1, object B's header on
            // line 50. The member symbol sits at line 10 — between the two.
            var contentLines = Enumerable.Repeat("    // filler", 55).ToArray();
            contentLines[0] = "codeunit 50000 \"Obj A\"";
            contentLines[49] = "codeunit 50001 \"Obj B\"";
            var content = string.Join("\n", contentLines);
            const string hash = "ABCABCABCABCABCABCABCABCABCABCABCABCABCABCABCABCABCABCABCABCABCABC";
            write.OeFileContents.Add(new OeFileContent
            {
                ContentHash = hash,
                Content = content,
                ContentLength = content.Length,
                LineCount = contentLines.Length,
            });
            var file = new OeModuleFile
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                Path = "src/Bundle.al",
                ContentHash = hash,
                LineCount = contentLines.Length,
            };
            write.OeModuleFiles.Add(file);
            await write.SaveChangesAsync();
            fileId = file.Id;

            var objA = new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                Kind = "codeunit",
                ObjectId = 50000,
                Name = "Obj A",
                LineNumber = 1,
                SourceFileId = file.Id,
            };
            var objB = new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                Kind = "codeunit",
                ObjectId = 50001,
                Name = "Obj B",
                LineNumber = 50,
                SourceFileId = file.Id,
            };
            write.OeModuleObjects.AddRange(objA, objB);
            await write.SaveChangesAsync();

            write.OeModuleSymbols.Add(new OeModuleSymbol
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                ObjectId = objA.Id,
                Kind = "procedure",
                Name = "DoWork",
                LineNumber = 10,
                ColumnStart = 5,
                ColumnEnd = 15,
            });
            await write.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var decls = await NewSourceViewer(read).ListDeclarationsInFileAsync(fileId);

        decls.Should().HaveCount(3, because: "two object headers plus one member symbol");
        decls.Select(d => d.Line).Should().Equal(new[] { 1, 10, 50 },
            "RangeSetBuilder needs declarations ascending by position regardless of objects-then-symbols append order");
    }

    [Fact]
    public async Task GoToDefinitionAsync_resolves_a_click_on_an_object_name_to_its_file()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewSourceViewer(read);

        // Find the declarations on DK Core's event-subscribers file, then
        // simulate a click on the codeunit's name token there. The resolver
        // should land us back on the same file + the declaration line.
        var fileId = read.OeModuleFiles.AsQueryable()
            .First(f => f.Path.Contains("DKCoreEventSubscribers")).Id;
        var decl = (await query.ListDeclarationsInFileAsync(fileId)).First();

        var target = await query.GoToDefinitionAsync(fileId, decl.Line, decl.ColumnStart);
        target.Should().NotBeNull();
        target!.FileId.Should().Be(fileId);
        target.LineNumber.Should().Be(decl.Line);
    }

    /// <summary>
    /// Seeds one Release whose module ships three objects sharing the name
    /// <c>User</c> — a table, a tableextension of it, and a codeunit — plus a
    /// caller file that references the name twice: once with a
    /// <c>Database::</c> prefix and once bare. Returns the caller file's id.
    /// Used by the kind-hint go-to-definition tests (issue #712).
    /// </summary>
    private async Task<(long FileId, int TableLine, int CodeunitLine, int ExtensionLine)> SeedSameNameKindsAsync()
    {
        await using var write = _db.NewContext();
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "Same-name kinds",
            Kind = "first_party",
            Status = "ready",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        write.OeReleases.Add(release);
        await write.SaveChangesAsync();

        var module = new OeModule
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = release.Id,
            AppId = Guid.NewGuid(),
            Name = "Bundle",
            Publisher = "CRONUS",
            Version = "1.0.0.0",
            CreatedAt = DateTime.UtcNow,
            DependencyCount = 0,
        };
        write.OeModules.Add(module);
        await write.SaveChangesAsync();

        async Task<OeModuleFile> AddFileAsync(string path, string[] lines, string hash)
        {
            var text = string.Join("\n", lines);
            write.OeFileContents.Add(new OeFileContent
            {
                ContentHash = hash,
                Content = text,
                ContentLength = text.Length,
                LineCount = lines.Length,
            });
            var f = new OeModuleFile
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                Path = path,
                ContentHash = hash,
                LineCount = lines.Length,
            };
            write.OeModuleFiles.Add(f);
            await write.SaveChangesAsync();
            return f;
        }

        var targetsFile = await AddFileAsync("src/Users.al", new[]
        {
            "table 2000000120 User",           // line 1
            "{",
            "}",
            "tableextension 50010 User extends User", // line 4
            "{",
            "}",
            "codeunit 418 User",               // line 7
            "{",
            "}",
        }, new string('A', 64));

        var callerFile = await AddFileAsync("src/Caller.al", new[]
        {
            "codeunit 50000 Caller",
            "{",
            "    procedure Foo()",
            "    begin",
            "        RecRef.Open(Database::User);", // line 5
            "        Bar(User);",                   // line 6
            "    end;",
            "}",
        }, new string('B', 64));

        write.OeModuleObjects.AddRange(
            new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                Kind = "table", ObjectId = 2000000120, Name = "User",
                LineNumber = 1, SourceFileId = targetsFile.Id,
            },
            new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                Kind = "tableextension", ObjectId = 50010, Name = "User",
                LineNumber = 4, SourceFileId = targetsFile.Id,
            },
            new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                Kind = "codeunit", ObjectId = 418, Name = "User",
                LineNumber = 7, SourceFileId = targetsFile.Id,
            },
            new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                Kind = "codeunit", ObjectId = 50000, Name = "Caller",
                LineNumber = 1, SourceFileId = callerFile.Id,
            });
        await write.SaveChangesAsync();

        return (callerFile.Id, 1, 7, 4);
    }

    [Fact]
    public async Task GoToDefinitionAsync_prefers_the_kind_the_click_names()
    {
        // #712: `Database::User` names the TABLE. Ranking used to be
        // alphabetical by kind, so the click landed on codeunit `User`.
        var seed = await SeedSameNameKindsAsync();
        await using var read = _db.NewContext();

        var target = await NewSourceViewer(read)
            .GoToDefinitionAsync(seed.FileId, line: 5, column: "        RecRef.Open(Database::".Length + 2);

        target.Should().NotBeNull();
        target!.LineNumber.Should().Be(seed.TableLine,
            because: "the Database:: prefix names the table, not the same-named codeunit");
    }

    [Fact]
    public async Task GoToDefinitionAsync_prefers_the_base_object_over_its_extension()
    {
        // #712: with no kind hint the candidates fall back to a deliberate
        // order — a base object ahead of the extension that extends it.
        var seed = await SeedSameNameKindsAsync();
        await using var read = _db.NewContext();

        var target = await NewSourceViewer(read)
            .GoToDefinitionAsync(seed.FileId, line: 6, column: "        Bar(".Length + 2);

        target.Should().NotBeNull();
        target!.LineNumber.Should().Be(seed.TableLine,
            because: "an unhinted click on a name shared by a table and its tableextension means the table");
    }

    [Fact]
    public async Task GoToDefinitionAsync_resolves_a_method_call_to_its_procedure_declaration()
    {
        // Phase-2 reference extraction emits method_call rows with
        // TargetSymbolId pointing at the procedure declaration. The resolver
        // must consult those rows so a Cmd/Ctrl-click on a call site (not on
        // a declaration token) lands on the procedure's source line.
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewSourceViewer(read);

        // Pick any resolved method_call row from the fixture so the test is
        // robust against the fixture's churn — we assert behaviour, not a
        // specific (file, member) pair.
        var pick = await (
            from r in read.OeModuleReferences.AsNoTracking()
            where r.ReferenceKind == "method_call"
                && r.SourceObject!.SourceFileId != null
                && r.LineNumber != null
                && r.TargetMemberName != null
                && r.TargetSymbolId != null
            select new
            {
                FileId = r.SourceObject!.SourceFileId!.Value,
                r.LineNumber,
                r.TargetMemberName,
                ExpectedSymbolLine = r.TargetSymbol!.LineNumber,
                ExpectedSymbolFileId = r.TargetSymbol!.Object!.SourceFileId,
            })
            .Where(x => x.ExpectedSymbolFileId != null)
            .FirstOrDefaultAsync();
        pick.Should().NotBeNull(because: "DK Core's .al source contains resolved method calls");

        // Reconstruct the column of the call-site identifier from the line
        // text. The extractor stored the line number; the column has to come
        // from a re-scan, same as production resolvables.
        var content = await read.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == pick!.FileId).Select(f => f.FileContent!.Content).SingleAsync();
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var lineText = lines[pick!.LineNumber!.Value - 1];
        var idx = lineText.IndexOf(pick.TargetMemberName!, StringComparison.Ordinal);
        idx.Should().BeGreaterThanOrEqualTo(0,
            because: "the recorded member name should still be present on the recorded line");

        var target = await query.GoToDefinitionAsync(pick.FileId, pick.LineNumber!.Value, idx + 1);

        target.Should().NotBeNull();
        target!.FileId.Should().Be(pick.ExpectedSymbolFileId!.Value);
        target.LineNumber.Should().Be(pick.ExpectedSymbolLine);
    }

    [Fact]
    public async Task ListResolvablesInFileAsync_returns_column_spans_for_member_access_tokens()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewSourceViewer(read);

        // Find a file that has at least one resolved member-access row so
        // we know the resolvables list won't be trivially empty.
        var fileWithRefs = await read.OeModuleReferences.AsNoTracking()
            .Where(r => (r.ReferenceKind == "method_call" || r.ReferenceKind == "field_access")
                && r.SourceObject!.SourceFileId != null
                && r.LineNumber != null
                && r.TargetSymbolId != null)
            .Select(r => r.SourceObject!.SourceFileId!.Value)
            .FirstOrDefaultAsync();
        fileWithRefs.Should().BeGreaterThan(0);

        var resolvables = await query.ListResolvablesInFileAsync(fileWithRefs);

        resolvables.Should().NotBeEmpty(
            because: "the file has resolved method_call / field_access rows");
        resolvables.Should().OnlyContain(r =>
            r.Line >= 1 && r.ColumnStart >= 1 && r.ColumnEnd > r.ColumnStart);

        // Verify the spans line up with actual source content — the column
        // range should slice an identifier-shaped substring out of its line.
        var content = await read.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileWithRefs).Select(f => f.FileContent!.Content).SingleAsync();
        var lines = content.Replace("\r\n", "\n").Split('\n');
        foreach (var r in resolvables.Take(20))
        {
            var lineText = lines[r.Line - 1];
            (r.ColumnEnd - 1).Should().BeLessThanOrEqualTo(lineText.Length);
            var slice = lineText.Substring(r.ColumnStart - 1, r.ColumnEnd - r.ColumnStart);
            slice.Should().NotBeNullOrWhiteSpace(
                because: "every resolvable should pin a real identifier (possibly quoted)");
        }
    }

    [Fact]
    public async Task ListResolvablesInFileAsync_underlines_the_extends_target_on_an_extension_header()
    {
        // The token a user ctrl-clicks to jump from a customisation
        // (tableextension / pageextension) to the base object it extends. The
        // SSR viewer only fires go-to-definition on a resolvable span, so if
        // this span is missing the click silently does nothing.
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewSourceViewer(read);

        var ext = await read.OeModuleReferences.AsNoTracking()
            .Where(r => r.ReferenceKind == "extends_target"
                && r.SourceObject!.SourceFileId != null
                && r.SourceObject!.LineNumber > 0
                && r.TargetObjectName != null)
            .Select(r => new
            {
                FileId = r.SourceObject!.SourceFileId!.Value,
                HeaderLine = r.SourceObject!.LineNumber,
                Target = r.TargetObjectName!,
            })
            .FirstOrDefaultAsync();
        ext.Should().NotBeNull(because: "the fixtures ship extension objects with an extends clause");

        var resolvables = await query.ListResolvablesInFileAsync(ext!.FileId);

        resolvables.Should().Contain(r => r.Line == ext.HeaderLine,
            because: "the extends target on an extension header must be clickable for go-to-definition, "
                + "even when the object body has no other resolvable references");
    }

    /// <summary>
    /// The resolvable spans carry the symbol they resolved to so the source
    /// viewer's hover card can fetch a description without asking the server
    /// to re-resolve the position. A span whose id is null is fine (the
    /// importer resolved no target); a span whose id points at nothing is not.
    /// </summary>
    [Fact]
    public async Task ListResolvablesInFileAsync_carries_the_resolved_symbol_id()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewSourceViewer(read);

        var fileWithRefs = await read.OeModuleReferences.AsNoTracking()
            .Where(r => (r.ReferenceKind == "method_call" || r.ReferenceKind == "field_access")
                && r.SourceObject!.SourceFileId != null
                && r.LineNumber != null
                && r.TargetSymbolId != null)
            .Select(r => r.SourceObject!.SourceFileId!.Value)
            .FirstOrDefaultAsync();
        fileWithRefs.Should().BeGreaterThan(0);

        var resolvables = await query.ListResolvablesInFileAsync(fileWithRefs);
        var ids = resolvables.Where(r => r.SymbolId is not null)
            .Select(r => r.SymbolId!.Value).Distinct().ToList();

        ids.Should().NotBeEmpty(
            because: "the file was picked precisely because it has resolved member-access rows");
        var real = await read.OeModuleSymbols.AsNoTracking()
            .Where(sym => ids.Contains(sym.Id)).CountAsync();
        real.Should().Be(ids.Count,
            because: "every stamped id must address a real oe_module_symbols row");
    }

    [Fact]
    public async Task DescribeSymbolAsync_describes_a_member_symbol_and_where_it_lives()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewSourceViewer(read);

        var symbol = await read.OeModuleSymbols.AsNoTracking()
            .Where(sym => sym.Kind == "procedure" && sym.Object!.SourceFileId != null)
            .OrderBy(sym => sym.Id)
            .FirstAsync();

        var card = await query.DescribeSymbolAsync(symbol.Id);

        card.Should().NotBeNull();
        card!.SymbolId.Should().Be(symbol.Id);
        card.Name.Should().Be(symbol.Name);
        card.Kind.Should().Be("procedure");
        card.LineNumber.Should().Be(symbol.LineNumber);
        card.OwnerName.Should().NotBeNullOrWhiteSpace(
            because: "the card names the object the member hangs off");
        card.ModuleName.Should().NotBeNullOrWhiteSpace();
        card.FileId.Should().NotBeNull(
            because: "the owning object was picked with imported source, so the card can offer a jump");
        card.FilePath.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DescribeSymbolAsync_returns_null_for_an_unknown_symbol()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var card = await NewSourceViewer(read).DescribeSymbolAsync(long.MaxValue);

        card.Should().BeNull(because: "the endpoint answers 404 rather than describing something else");
    }

    /// <summary>
    /// TargetLabel is a sentence fragment ("references to table 36 Customer");
    /// the panel heading needs the bare name, so the session carries both.
    /// </summary>
    [Fact]
    public async Task CreateFromSymbolAsync_carries_the_bare_target_name()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var obj = await read.OeModuleObjects.AsNoTracking().OrderBy(o => o.Id).FirstAsync();

        var session = await NewSessions(read).CreateFromSymbolAsync(obj.Id, "owner");

        session.Should().NotBeNull();
        session!.TargetName.Should().Be(obj.Name);
        session.TargetLabel.Should().Contain(obj.Name,
            because: "the long form is still what the close button's tooltip says");
        session.TargetLabel.Should().NotBe(session.TargetName,
            because: "the heading would read 'References to references to ...' if they were the same");
    }

    [Fact]
    public async Task FindInFileAsync_returns_every_line_containing_the_clicked_word()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewSourceViewer(read);
        var fileId = read.OeModuleFiles.AsQueryable()
            .First(f => f.Path.Contains("DKCoreEventSubscribers")).Id;
        var decl = (await query.ListDeclarationsInFileAsync(fileId)).First();

        var hit = await query.FindInFileAsync(fileId, decl.Line, decl.ColumnStart);
        hit.Should().NotBeNull();
        hit!.Occurrences.Should().NotBeEmpty();
        hit.Occurrences.Should().OnlyContain(o => o.LineText.Contains(hit.Word, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFileOutlineAsync_returns_objects_and_symbols_ordered_by_line()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var fileId = read.OeModuleFiles.AsQueryable()
            .First(f => f.Path.Contains("DKCoreEventSubscribers")).Id;

        var outline = await NewSourceViewer(read).GetFileOutlineAsync(fileId);
        outline.Should().NotBeEmpty();
        outline.Select(o => o.LineNumber).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task GetFileOutlineAsync_includes_procedures_with_real_line_numbers()
    {
        // Regression for the post-PR-130 outline gap: ReleaseImportService
        // used to write every sub-symbol with LineNumber=0, and the query
        // filtered those out, so the outline showed only the object header.
        // The import now runs AlSymbolExtractor over each .al file, so every
        // procedure (and trigger / event subscriber) lands with a 1-based
        // line and a non-zero column range the source viewer can scroll to.
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var fileId = read.OeModuleFiles.AsQueryable()
            .First(f => f.Path.Contains("DKCoreEventSubscribers")).Id;

        var outline = await NewSourceViewer(read).GetFileOutlineAsync(fileId);

        outline.Should().Contain(i =>
                i.Kind == "procedure"
                || i.Kind == "internal_procedure"
                || i.Kind == "local_procedure"
                || i.Kind == "event_subscriber",
            because: "DKCoreEventSubscribers ships procedures the symbol extractor must surface");
        outline.Where(i => i.ObjectId is null).Should()
            .OnlyContain(s => s.LineNumber > 0,
                because: "sub-symbol rows must carry the line they were declared on");

        // EndLine surfaces oe_module_symbols.end_line so the source viewer's
        // status bar can scope the cursor to a containing procedure. The
        // post-#181 walker populates it for every procedure-like symbol; if
        // a fixture procedure shows up without one the status-bar lookup
        // silently falls back to "next procedure − 1", which is a useful
        // fallback but not the canonical signal.
        var procedureLike = outline
            .Where(i => i.ObjectId is null
                && (i.Kind == "procedure"
                    || i.Kind == "internal_procedure"
                    || i.Kind == "protected_procedure"
                    || i.Kind == "local_procedure"
                    || i.Kind == "trigger"
                    || i.Kind == "event_publisher"
                    || i.Kind == "event_subscriber"))
            .ToList();
        procedureLike.Should().NotBeEmpty();
        procedureLike.Should().OnlyContain(p => p.EndLine == null || p.EndLine.Value >= p.LineNumber,
            because: "EndLine, when set, must bracket the start line — never trail before it");
    }

    [Fact]
    public async Task ListObjectsAsync_paginates_and_filters_by_kind()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewQuery(read);

        var modules = await query.ListModulesAsync(releaseId, new ModuleListFilter());
        var dkCore = modules.Single(m => m.Name == "DK Core");

        // Kind="codeunit" narrows DK Core to its 4 codeunits.
        var page = await query.ListObjectsAsync(dkCore.Id, new ObjectListFilter(Kinds: new[] { "codeunit" }), skip: 0, take: 50);
        page.TotalCount.Should().Be(4);
        page.Rows.Should().OnlyContain(o => o.Kind == "codeunit");

        // Pagination round-trip.
        var page1 = await query.ListObjectsAsync(dkCore.Id, new ObjectListFilter(), skip: 0, take: 5);
        var page2 = await query.ListObjectsAsync(dkCore.Id, new ObjectListFilter(), skip: 5, take: 5);
        page1.TotalCount.Should().Be(14);
        page2.TotalCount.Should().Be(14);
        page1.Rows.Should().HaveCount(5);
        // No overlap between pages.
        page1.Rows.Select(o => o.Id).Should().NotIntersectWith(page2.Rows.Select(o => o.Id));
    }

    [Fact]
    public async Task GetObjectAsync_returns_variables_and_symbols_inline()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var ctx = read;

        var obj = ctx.OeModuleObjects.AsQueryable()
            .Single(o => o.Kind == "table" && o.Name == "OIOUBL-Profile");

        var detail = await NewQuery(ctx).GetObjectAsync(obj.Id);
        detail.Should().NotBeNull();
        detail!.ModuleName.Should().Be("OIOUBL");
        detail.SourceFilePath.Should().NotBeNullOrEmpty();

        // Two fields surface as table-field symbols (the OIOUBL-Profile
        // table has two fields). Symbol kind is "table_field" after the
        // refactor in .design/al-reference-extractor-refactor.md step 1.
        detail.Symbols.Where(s => s.Kind == "table_field").Should().HaveCount(2);
    }

    // ── Find references — same release ─────────────────────────────────

    [Fact]
    public async Task FindReferencesAsync_returns_dk_core_variables_pointing_at_cancel_fa_ledger_entries()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var matches = await NewReferences(read).FindReferencesAsync(releaseId, new FindReferencesQuery(
            TargetAppId: BaseAppId,
            TargetObjectKind: "codeunit",
            TargetObjectId: 5624,
            TargetObjectName: "Cancel FA Ledger Entries"));

        matches.Should().NotBeEmpty();
        matches.Should().OnlyContain(m => m.SourceModuleName == "DK Core",
            because: "the only module in this release that references that codeunit is DK Core");
        matches.Should().Contain(m =>
            m.ReferenceKind == "variable_type"
            && m.SourceObjectName == "CopyDepreciationBookExt");
        // SourceFileId is needed for the file-viewer links in
        // OeObjectDetail.razor's references table; every row pointing at an
        // object whose source we have should expose it.
        matches.Should().Contain(m => m.SourceFileId.HasValue,
            because: "DK Core's referencing objects ship with source");
    }

    [Fact]
    public async Task FindReferencesAsync_falls_back_to_name_when_id_is_null()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        // extends_target rows have TargetObjectId=null (the symbol package
        // doesn't carry the base object's id on the Target string — only the
        // AppId + name). Querying by name only must still find them.
        var matches = await NewReferences(read).FindReferencesAsync(releaseId, new FindReferencesQuery(
            TargetAppId: BaseAppId,
            TargetObjectKind: "table",
            TargetObjectId: null,
            TargetObjectName: "Finance Charge Memo Line"));

        matches.Should().NotBeEmpty();
        matches.Should().Contain(m => m.ReferenceKind == "extends_target"
            && m.SourceModuleName == "OIOUBL");
    }

    [Fact]
    public async Task FindReferencesAsync_returns_empty_when_target_not_referenced()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var matches = await NewReferences(read).FindReferencesAsync(releaseId, new FindReferencesQuery(
            TargetAppId: BaseAppId,
            TargetObjectKind: "codeunit",
            TargetObjectId: 99999999,
            TargetObjectName: "Nonexistent Codeunit"));
        matches.Should().BeEmpty();
    }

    /// <summary>
    /// Snippet enrichment cuts the line out in SQL (<c>split_part</c> on
    /// <c>'\n'</c>) rather than loading whole file bodies — see issue #687. Two
    /// edges that behaviour has to keep: CRLF content must not leak a trailing
    /// carriage return into the snippet, and a line number past the end of the
    /// file must yield an empty snippet instead of throwing.
    /// </summary>
    [Theory]
    [InlineData(2, "    Message('hi');")]  // real line, CRLF file
    [InlineData(99, "")]                   // past the end of the file
    public async Task FindReferencesAsync_snippet_handles_crlf_and_out_of_range_lines(
        int referenceLine, string expectedSnippet)
    {
        var targetAppId = Guid.NewGuid();
        int releaseId;
        await using (var write = _db.NewContext())
        {
            var release = new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId,
                Label = "CRLF snippet fixture",
                Kind = "first_party",
                Status = "ready",
                ImportedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            write.OeReleases.Add(release);
            await write.SaveChangesAsync();
            releaseId = release.Id;

            var module = new OeModule
            {
                OrganizationId = TestDb.DefaultOrgId,
                ReleaseId = release.Id,
                AppId = Guid.NewGuid(),
                Name = "Caller",
                Publisher = "CRONUS",
                Version = "1.0.0.0",
                CreatedAt = DateTime.UtcNow,
                DependencyCount = 0,
            };
            write.OeModules.Add(module);
            await write.SaveChangesAsync();

            // Windows line endings on purpose.
            var content = "codeunit 50000 \"Caller\"\r\n    Message('hi');\r\n}\r\n";
            const string hash = "CRLFCRLFCRLFCRLFCRLFCRLFCRLFCRLFCRLFCRLFCRLFCRLFCRLFCRLFCRLFCRLF00";
            write.OeFileContents.Add(new OeFileContent
            {
                ContentHash = hash,
                Content = content,
                ContentLength = content.Length,
                LineCount = 3,
            });
            var file = new OeModuleFile
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                Path = "src/Caller.al",
                ContentHash = hash,
                LineCount = 3,
            };
            write.OeModuleFiles.Add(file);
            await write.SaveChangesAsync();

            var caller = new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                Kind = "codeunit",
                ObjectId = 50000,
                Name = "Caller",
                LineNumber = 1,
                SourceFileId = file.Id,
            };
            write.OeModuleObjects.Add(caller);
            await write.SaveChangesAsync();

            write.OeModuleReferences.Add(new OeModuleReference
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                SourceObjectId = caller.Id,
                TargetAppId = targetAppId,
                TargetObjectKind = "table",
                TargetObjectId = 18,
                TargetObjectName = "Customer",
                ReferenceKind = "variable_type",
                LineNumber = referenceLine,
            });
            await write.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var matches = await NewReferences(read).FindReferencesAsync(releaseId, new FindReferencesQuery(
            TargetAppId: targetAppId,
            TargetObjectKind: "table",
            TargetObjectId: 18,
            TargetObjectName: "Customer"));

        matches.Should().ContainSingle();
        matches[0].SourceFilePath.Should().Be("src/Caller.al");
        matches[0].Snippet.Should().Be(expectedSnippet);
    }

    [Fact]
    public async Task FindSystemReferencesAsync_returns_system_calls_for_an_object()
    {
        // Seed a system-reference row directly (no extraction) to lock the
        // query: a codeunit "Caller" that Inserts table 99000 "Acme". #279.
        var releaseId = await SeedSingleReleaseAsync();
        Guid appId;
        await using (var write = _db.NewContext())
        {
            var module = await write.OeModules
                .Where(m => m.ReleaseId == releaseId)
                .Select(m => new { m.Id, m.AppId })
                .FirstAsync();
            appId = module.AppId;
            var caller = new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                Kind = "codeunit",
                ObjectId = 50000,
                Name = "Caller",
                LineNumber = 1,
            };
            write.OeModuleObjects.Add(caller);
            await write.SaveChangesAsync();

            write.OeModuleSystemReferences.Add(new OeModuleSystemReference
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                SourceObjectId = caller.Id,
                TargetAppId = appId,
                TargetObjectKind = "table",
                TargetObjectId = 99000,
                TargetObjectName = "Acme",
                SystemMethodName = "Insert",
                ReferenceKind = "method_call",
                LineNumber = 10,
            });
            await write.SaveChangesAsync();
        }
        await using var read = _db.NewContext();
        var refs = NewReferences(read);

        var hits = await refs.FindSystemReferencesAsync(releaseId, new FindSystemReferencesQuery(
            TargetAppId: appId,
            TargetObjectKind: "table",
            TargetObjectId: 99000,
            TargetObjectName: "Acme"));
        hits.Should().Contain(m => m.MemberName == "Insert"
            && m.Category == "system_call"
            && m.SourceObjectName == "Caller");

        // Narrowing to a method that wasn't called returns nothing.
        var modifyHits = await refs.FindSystemReferencesAsync(releaseId, new FindSystemReferencesQuery(
            TargetAppId: appId, TargetObjectKind: "table", TargetObjectId: 99000,
            TargetObjectName: "Acme", SystemMethodName: "Modify"));
        modifyHits.Should().BeEmpty();
    }

    [Fact]
    public async Task FindReferencesForSymbolAsync_call_rows_carry_the_enclosing_member()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        // Self-discovering: pick a real call-site row the import stamped with a
        // source symbol (the enclosing procedure / trigger) so the test doesn't
        // hard-code fixture-specific names. This is exactly the shape the
        // References panel groups under its object header.
        var seed = await read.OeModuleReferences.AsNoTracking()
            .Where(r => r.Module!.ReleaseId == releaseId)
            .Where(r => r.ReferenceKind == "method_call" || r.ReferenceKind == "field_access")
            .Where(r => r.SourceSymbolId != null && r.TargetMemberName != null && r.TargetObjectName != null)
            .Select(r => new
            {
                r.TargetAppId,
                r.TargetObjectKind,
                r.TargetObjectId,
                r.TargetObjectName,
                r.TargetMemberName,
                r.TargetMemberKind,
                EnclosingName = r.SourceSymbol!.Name,
            })
            .FirstOrDefaultAsync();
        seed.Should().NotBeNull(
            because: "the fixture import emits method_call/field_access rows stamped with a source symbol");

        var matches = await NewReferences(read).FindReferencesForSymbolAsync(releaseId, new FindReferencesQuery(
            TargetAppId: seed!.TargetAppId,
            TargetObjectKind: seed.TargetObjectKind,
            TargetObjectId: seed.TargetObjectId,
            TargetObjectName: seed.TargetObjectName!,
            TargetMemberName: seed.TargetMemberName,
            TargetMemberKind: seed.TargetMemberKind));

        // The enclosing procedure/trigger, resolved from source_symbol_id, must
        // reach the DTO so the panel can show "where in the object" each call sits.
        matches.Should().Contain(m => m.Category == "call" && m.SourceMemberName == seed.EnclosingName,
            because: "call-site references should surface their enclosing procedure/trigger");
    }

    [Fact]
    public async Task FindReferencesForSymbolAsync_field_query_matches_table_field_kind()
    {
        // Regression for the member-kind mismatch: BC table fields are stamped
        // `table_field`, but the MCP find_references tool asks for `field`. An
        // exact kind comparison returned empty for every field-scoped query
        // (the object had matching member rows, the kind filter excluded them);
        // MemberKindMatch collapses the field-like kinds so both spellings hit.
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        // A real field-access call row the AL import stamped as `table_field`.
        var seed = await read.OeModuleReferences.AsNoTracking()
            .Where(r => r.Module!.ReleaseId == releaseId)
            .Where(r => r.ReferenceKind == "field_access")
            .Where(r => r.TargetMemberName != null && r.TargetObjectName != null
                && r.TargetMemberKind == "table_field")
            .Select(r => new
            {
                r.TargetAppId,
                r.TargetObjectKind,
                r.TargetObjectId,
                r.TargetObjectName,
                r.TargetMemberName,
            })
            .FirstOrDefaultAsync();
        seed.Should().NotBeNull(
            because: "the AL fixture import stamps field references with kind 'table_field'");

        FindReferencesQuery QueryWithKind(string? kind) => new(
            TargetAppId: seed!.TargetAppId,
            TargetObjectKind: seed.TargetObjectKind,
            TargetObjectId: seed.TargetObjectId,
            TargetObjectName: seed.TargetObjectName!,
            TargetMemberName: seed.TargetMemberName,
            TargetMemberKind: kind);

        var references = NewReferences(read);
        // The MCP-style "field" kind must reach the table_field rows…
        var asField = await references.FindReferencesForSymbolAsync(releaseId, QueryWithKind("field"));
        // …and so must the web-UI-style real "table_field" kind.
        var asTableField = await references.FindReferencesForSymbolAsync(releaseId, QueryWithKind("table_field"));

        asField.Should().Contain(m => m.Category == "call" && m.MemberName == seed!.TargetMemberName,
            because: "a 'field' kind query must match references stamped 'table_field'");
        asField.Count(m => m.Category == "call")
            .Should().Be(asTableField.Count(m => m.Category == "call"),
                because: "'field' and 'table_field' are interchangeable field-like kinds");
    }

    // ── Find references — parent-chain walk + shadowing ────────────────

    [Fact]
    public async Task FindReferencesAsync_walks_parent_chain_to_find_references_in_ancestor_releases()
    {
        // Parent release has DK Core; child release adds OIOUBL on top.
        // Querying the CHILD must surface DK Core's references too because
        // the chain walk pulls DK Core's module up into the child's view.
        int parentId, childId;
        await using (var ctx = _db.NewContext())
        {
            var imp = NewImporter(ctx);
            await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
            var parent = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "BC 25.18 Parent", "first_party", null, null,
                new[] { new AppFileUpload("dk.app", s1, null) }));
            parentId = parent.ReleaseId;

            await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
            var child = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "Customer X", "project", parent.ReleaseId, null,
                new[] { new AppFileUpload("oioubl.app", s2, null) }));
            childId = child.ReleaseId;
        }

        await using var read = _db.NewContext();
        var query = NewReferences(read);

        var fromChild = await query.FindReferencesAsync(childId, new FindReferencesQuery(
            BaseAppId, "codeunit", 5624, "Cancel FA Ledger Entries"));
        fromChild.Should().NotBeEmpty(because: "DK Core lives in the parent and the chain walk reaches it from the child");
        fromChild.Should().OnlyContain(m => m.SourceModuleName == "DK Core");
    }

    [Fact]
    public async Task FindReferencesAsync_root_release_does_not_see_descendant_references()
    {
        // Parent release: DK Core. Child release: OIOUBL.
        // Querying the PARENT must NOT see OIOUBL's references — chain walk
        // is upward only (toward older Releases), never downward into
        // descendant Releases.
        int parentId, childId;
        await using (var ctx = _db.NewContext())
        {
            var imp = NewImporter(ctx);
            await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
            var parent = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "BC 25.18 Parent", "first_party", null, null,
                new[] { new AppFileUpload("dk.app", s1, null) }));
            parentId = parent.ReleaseId;

            await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
            var child = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "Customer X", "project", parent.ReleaseId, null,
                new[] { new AppFileUpload("oioubl.app", s2, null) }));
            childId = child.ReleaseId;
        }

        await using var read = _db.NewContext();
        var query = NewReferences(read);

        // OIOUBL's extends_target → Base App's "Finance Charge Memo Line".
        // From the PARENT release (DK Core only) we must NOT see this row.
        var fromParent = await query.FindReferencesAsync(parentId, new FindReferencesQuery(
            BaseAppId, "table", null, "Finance Charge Memo Line"));
        fromParent.Should().BeEmpty(
            because: "OIOUBL is in the child Release; chain walk is parent-ward only");
    }

    [Fact]
    public async Task CreateFromSymbolAsync_seeds_at_the_view_release_not_the_objects_home()
    {
        // The whole point of Part 2: find-references on a base object opened
        // from a project Release must be seeded at the project Release (so the
        // upward chain walk includes the customer's own code), not at the
        // object's home Release. CreateFromSymbolAsync forwards viewReleaseId to
        // FindReferencesAsync and records it as the session's seed.
        int parentId, childId;
        await using (var ctx = _db.NewContext())
        {
            var imp = NewImporter(ctx);
            await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
            var parent = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "BC 25.18 Parent", "first_party", null, null,
                new[] { new AppFileUpload("dk.app", s1, null) }));
            parentId = parent.ReleaseId;

            await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
            var child = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "Customer X", "project", parent.ReleaseId, null,
                new[] { new AppFileUpload("oioubl.app", s2, null) }));
            childId = child.ReleaseId;
        }

        await using var read = _db.NewContext();

        // Any object whose home is the parent (DK Core). Its identity is what
        // the session keys the find-references query on.
        var obj = await read.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == parentId)
            .Select(o => new
            {
                o.Id,
                AppId = o.Module!.AppId,
                o.Kind,
                o.ObjectId,
                o.Name,
            })
            .FirstOrDefaultAsync();
        obj.Should().NotBeNull(because: "DK Core ships objects");

        var sessions = NewSessions(read);
        var identity = new FindReferencesQuery(obj!.AppId, obj.Kind, obj.ObjectId, obj.Name);
        var references = NewReferences(read);

        // Seeded explicitly at the child Release.
        var fromChild = await sessions.CreateFromSymbolAsync(obj.Id, "owner", viewReleaseId: childId);
        fromChild!.ReleaseId.Should().Be(childId,
            because: "the session is seeded at the Release the user is viewing from");
        fromChild.Results.Count.Should().Be(
            (await references.FindReferencesAsync(childId, identity)).Count,
            because: "the session forwards the child seed to find-references");

        // No view Release → fall back to the object's home (the parent).
        var fromHome = await sessions.CreateFromSymbolAsync(obj.Id, "owner", viewReleaseId: null);
        fromHome!.ReleaseId.Should().Be(parentId,
            because: "without a view Release we keep the original home-Release behaviour");
        fromHome.Results.Count.Should().Be(
            (await references.FindReferencesAsync(parentId, identity)).Count,
            because: "the home seed matches a direct find-references at the parent");
    }

    [Fact]
    public async Task FindReferencesAsync_shadows_same_appid_with_closest_release_in_chain()
    {
        // This test pins the shadowing rule: same AppId in both parent and child,
        // the child's version wins. We import DK Core into the parent, then
        // import a DIFFERENT-bytes copy of DK Core into the child (we can't
        // easily get one without rebuilding, so we cheat by directly inserting
        // a second module row at the SQL layer to simulate a re-issued .app).
        await using var ctx = _db.NewContext();
        var imp = NewImporter(ctx);

        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var parent = await imp.ImportReleaseAsync(new ReleaseImportRequest(
            "Parent", "first_party", null, null,
            new[] { new AppFileUpload("dk.app", s1, null) }));

        await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var child = await imp.ImportReleaseAsync(new ReleaseImportRequest(
            "Child", "project", parent.ReleaseId, null,
            new[] { new AppFileUpload("dk.app", s2, null) }));

        // Each Release now has a Module with AppId=DK Core. Both also have an
        // OeModuleReference pointing at "Cancel FA Ledger Entries". Querying
        // the CHILD must return references from the CHILD's DK Core module
        // only (shadowing), so we should get exactly the child's count, not
        // double-counted across parent + child.
        await using var read = _db.NewContext();
        var query = NewReferences(read);

        var matches = await query.FindReferencesAsync(child.ReleaseId, new FindReferencesQuery(
            BaseAppId, "codeunit", 5624, "Cancel FA Ledger Entries"));

        var childModuleIds = read.OeModules
            .Where(m => m.ReleaseId == child.ReleaseId && m.Name == "DK Core")
            .Select(m => m.Id)
            .ToHashSet();

        matches.Should().NotBeEmpty();
        matches.Should().OnlyContain(m => childModuleIds.Contains(m.SourceModuleId),
            because: "the child's same-AppId module shadows the parent's");
    }

    // ── Cross-release object resolution (ChainObjectResolution) ────────

    [Fact]
    public async Task ResolveObjectAsync_resolves_an_object_living_in_the_parent_release_from_the_child()
    {
        // Parent: DK Core. Child: OIOUBL on top. An object that physically
        // lives in the parent must resolve when looked up from the child —
        // the gap that left base tables "unresolved" from a project Release.
        int parentId, childId;
        await using (var ctx = _db.NewContext())
        {
            var imp = NewImporter(ctx);
            await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
            var parent = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "BC Parent", "first_party", null, null,
                new[] { new AppFileUpload("dk.app", s1, null) }));
            parentId = parent.ReleaseId;

            await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
            var child = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "Customer X", "project", parent.ReleaseId, null,
                new[] { new AppFileUpload("oioubl.app", s2, null) }));
            childId = child.ReleaseId;
        }

        await using var read = _db.NewContext();

        // A codeunit that exists only in the parent (DK Core), picked from the
        // data so the test doesn't hard-code a symbol-package-specific name.
        var parentObject = read.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == parentId && o.Kind == "codeunit")
            .OrderBy(o => o.Name)
            .First();

        var hit = await ChainObjectResolution.ResolveObjectAsync(
            read, childId, parentObject.Name, "codeunit", null, CancellationToken.None);

        hit.Should().NotBeNull(because: "the object lives in the parent and the chain walk reaches it from the child");
        hit!.Id.Should().Be(parentObject.Id);
        hit.ModuleName.Should().Be("DK Core");
    }

    [Fact]
    public async Task ResolveObjectAsync_does_not_resolve_a_child_only_object_from_the_parent()
    {
        // Chain walk is parent-ward only: an object that lives in the child
        // (OIOUBL's "OIOUBL-Profile" table) must NOT resolve from the parent.
        int parentId;
        await using (var ctx = _db.NewContext())
        {
            var imp = NewImporter(ctx);
            await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
            var parent = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "BC Parent", "first_party", null, null,
                new[] { new AppFileUpload("dk.app", s1, null) }));
            parentId = parent.ReleaseId;

            await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
            await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "Customer X", "project", parent.ReleaseId, null,
                new[] { new AppFileUpload("oioubl.app", s2, null) }));
        }

        await using var read = _db.NewContext();

        var hit = await ChainObjectResolution.ResolveObjectAsync(
            read, parentId, "OIOUBL-Profile", "table", null, CancellationToken.None);

        hit.Should().BeNull(because: "the chain walk only reaches ancestors, never descendant Releases");
    }

    // ── Find references — member-scoped ────────────────────────────────

    [Fact]
    public async Task FindReferencesForSymbolAsync_returns_sibling_declarations_in_the_chain()
    {
        // A member-scoped find against a real (object, member) pair in
        // the seeded data should return at minimum the member's own
        // declaration row tagged Category=declaration. We pick the pair
        // from the actual oe_module_symbols table rather than hard-coding
        // a name — symbol-package shapes vary across modules, so any
        // assertion on a specific procedure name is brittle. The query
        // grabs the first symbol that belongs to an object identifiable
        // by its (kind, object id, name) triplet and uses that.
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var pair = await read.OeModuleSymbols
            .OrderBy(s => s.Id)
            .Select(s => new
            {
                MemberName = s.Name,
                MemberKind = s.Kind,
                OwnerId = s.Object!.Id,
                OwnerKind = s.Object!.Kind,
                OwnerObjectId = s.Object!.ObjectId,
                OwnerName = s.Object!.Name,
                AppId = s.Object!.Module!.AppId,
            })
            .FirstAsync();

        var matches = await NewReferences(read).FindReferencesForSymbolAsync(releaseId, new FindReferencesQuery(
            TargetAppId: pair.AppId,
            TargetObjectKind: pair.OwnerKind,
            TargetObjectId: pair.OwnerObjectId,
            TargetObjectName: pair.OwnerName,
            TargetMemberName: pair.MemberName,
            TargetMemberKind: pair.MemberKind));

        matches.Should().Contain(m => m.Category == "declaration"
            && m.SourceObjectName == pair.OwnerName
            && m.MemberName == pair.MemberName
            && m.MemberKind == pair.MemberKind);
    }

    [Fact]
    public async Task FindReferencesForSymbolAsync_omits_owner_type_indirect_refs()
    {
        // Owner-type rows (variable_type, parameter_type, …) dominated the
        // result set on large releases and most users found them noise
        // rather than signal, so the bucket is currently disabled — see
        // ReferenceQueryService.FindReferencesForSymbolAsync. If the
        // bucket comes back, flip this test back to assert containment.
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var matches = await NewReferences(read).FindReferencesForSymbolAsync(releaseId, new FindReferencesQuery(
            TargetAppId: BaseAppId,
            TargetObjectKind: "codeunit",
            TargetObjectId: 5624,
            TargetObjectName: "Cancel FA Ledger Entries",
            TargetMemberName: "AnyProcedureName",
            TargetMemberKind: "procedure"));

        matches.Should().NotContain(m => m.Category == "owner_type");
    }

    [Fact]
    public async Task FindReferencesForSymbolAsync_call_bucket_is_empty_in_phase_one()
    {
        // Phase 1 leaves target_member_name NULL on every imported row;
        // the call bucket is wired up but should return zero results
        // until the importer starts populating member-scoped references.
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var matches = await NewReferences(read).FindReferencesForSymbolAsync(releaseId, new FindReferencesQuery(
            TargetAppId: BaseAppId,
            TargetObjectKind: "codeunit",
            TargetObjectId: 5624,
            TargetObjectName: "Cancel FA Ledger Entries",
            TargetMemberName: "AnyProcedureName",
            TargetMemberKind: "procedure"));

        matches.Should().NotContain(m => m.Category == "call",
            because: "the importer doesn't populate target_member_name yet");
    }

    // ── Release comparison (CompareReleasesAsync / CompareModuleFilesAsync) ─

    /// <summary>
    /// Imports the same DK Core .app into two separate Releases. The
    /// (AppId, ContentHash) pairs are byte-identical, so the compare should
    /// classify every module as unchanged (Changed bucket empty) and the
    /// Added / Removed buckets are also empty.
    /// </summary>
    private async Task<(int leftId, int rightId)> SeedTwoIdenticalReleasesAsync()
    {
        await using var ctx = _db.NewContext();
        var imp = NewImporter(ctx);

        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var left = await imp.ImportReleaseAsync(new ReleaseImportRequest(
            "BC 25.18 A", "first_party", null, null,
            new[] { new AppFileUpload("dk1.app", s1, null) }));
        await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var right = await imp.ImportReleaseAsync(new ReleaseImportRequest(
            "BC 25.18 B", "first_party", null, null,
            new[] { new AppFileUpload("dk2.app", s2, null) }));
        return (left.ReleaseId, right.ReleaseId);
    }

    [Fact]
    public async Task CompareReleases_returns_added_removed_changed_modules()
    {
        // Left has DK Core only; right has DK Core + OIOUBL. So:
        //  - Removed: empty (DK Core is on both sides)
        //  - Added: OIOUBL
        //  - Changed: empty (DK Core bytes match)
        int leftId, rightId;
        await using (var ctx = _db.NewContext())
        {
            var imp = NewImporter(ctx);
            await using var sLeft = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
            var left = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "Left", "first_party", null, null,
                new[] { new AppFileUpload("dk.app", sLeft, null) }));

            await using var sRight1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
            await using var sRight2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
            var right = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "Right", "first_party", null, null,
                new[]
                {
                    new AppFileUpload("dk.app", sRight1, null),
                    new AppFileUpload("oioubl.app", sRight2, null),
                }));
            leftId = left.ReleaseId;
            rightId = right.ReleaseId;
        }

        await using var read = _db.NewContext();
        var summary = await NewComparison(read).CompareReleasesAsync(leftId, rightId);

        summary.Should().NotBeNull();
        summary!.Added.Should().ContainSingle(m => m.Name == "OIOUBL");
        summary.Removed.Should().BeEmpty();
        summary.Changed.Should().BeEmpty(
            "DK Core's bytes are identical on both sides — ContentHashes match");
    }

    [Fact]
    public async Task CompareReleases_ignores_unchanged_modules()
    {
        var (leftId, rightId) = await SeedTwoIdenticalReleasesAsync();
        await using var read = _db.NewContext();
        var summary = await NewComparison(read).CompareReleasesAsync(leftId, rightId);

        summary.Should().NotBeNull();
        summary!.Added.Should().BeEmpty();
        summary.Removed.Should().BeEmpty();
        summary.Changed.Should().BeEmpty();
    }

    [Fact]
    public async Task CompareReleases_returns_null_when_either_release_missing()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var bad = await NewComparison(read).CompareReleasesAsync(releaseId, releaseId + 999);
        bad.Should().BeNull();
    }

    [Fact]
    public async Task CompareModuleFiles_classifies_added_removed_changed_correctly()
    {
        // Re-import the same module twice → same ContentHashes; every file
        // pair is unchanged so all three buckets are empty.
        var (leftId, rightId) = await SeedTwoIdenticalReleasesAsync();

        await using var read = _db.NewContext();
        var leftMod = await read.OeModules.AsNoTracking().Where(m => m.ReleaseId == leftId).SingleAsync();
        var rightMod = await read.OeModules.AsNoTracking().Where(m => m.ReleaseId == rightId).SingleAsync();

        var result = await NewComparison(read).CompareModuleFilesAsync(leftMod.Id, rightMod.Id);
        result.Should().NotBeNull();
        result!.Added.Should().BeEmpty();
        result.Removed.Should().BeEmpty();
        result.Changed.Should().BeEmpty();
    }

    [Fact]
    public async Task CompareReleases_marks_module_changed_when_file_content_hash_differs()
    {
        // Same .app on both sides → identical (AppId, Version, ContentHash) →
        // compare reports zero diffs. Mutating one file's Content+ContentHash
        // simulates a vendor patch on the right release without re-importing
        // a different fixture, so we can exercise the Changed bucket
        // deterministically.
        var (leftId, rightId) = await SeedTwoIdenticalReleasesAsync();

        await using (var write = _db.NewContext())
        {
            var anyRightFile = await write.OeModuleFiles
                .Where(f => f.Module!.ReleaseId == rightId)
                .OrderBy(f => f.Path)
                .FirstAsync();
            // Point the file at a fresh content blob (distinct hash) to simulate
            // a vendor patch. Content lives in the shared store now, so insert
            // the blob first (the content_hash FK requires it to exist).
            const string patchedHash = "DEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEF";
            write.OeFileContents.Add(new OeFileContent
            {
                ContentHash = patchedHash,
                Content = "// patched by test\n",
                ContentLength = "// patched by test\n".Length,
                LineCount = 2,
            });
            anyRightFile.ContentHash = patchedHash;
            await write.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var summary = await NewComparison(read).CompareReleasesAsync(leftId, rightId);

        summary.Should().NotBeNull();
        summary!.Added.Should().BeEmpty();
        summary.Removed.Should().BeEmpty();
        summary.Changed.Should().ContainSingle()
            .Which.ChangedFileCount.Should().Be(1,
                "one ContentHash was mutated; everything else still matches");

        var leftMod = await read.OeModules.AsNoTracking().Where(m => m.ReleaseId == leftId).SingleAsync();
        var rightMod = await read.OeModules.AsNoTracking().Where(m => m.ReleaseId == rightId).SingleAsync();
        var pairs = await NewComparison(read).CompareModuleFilesAsync(leftMod.Id, rightMod.Id);
        pairs.Should().NotBeNull();
        pairs!.Changed.Should().ContainSingle();
        pairs.Added.Should().BeEmpty();
        pairs.Removed.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCompareTargets_returns_only_releases_with_matching_app_id_and_path()
    {
        // Two Releases of DK Core: the same file path exists on both sides.
        // GetCompareTargetsAsync(leftFileId) should surface the right release.
        var (leftId, rightId) = await SeedTwoIdenticalReleasesAsync();

        await using var read = _db.NewContext();
        var leftFile = await read.OeModuleFiles.AsNoTracking()
            .Where(f => f.Module!.ReleaseId == leftId)
            .OrderBy(f => f.Path)
            .FirstAsync();

        var targets = await NewComparison(read).GetCompareTargetsAsync(leftFile.Id);
        targets.Should().ContainSingle(t => t.ReleaseId == rightId,
            "the right release holds DK Core at the same canonical path");
    }

    [Fact]
    public async Task GetCompareTargets_excludes_the_current_file_release()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var anyFile = await read.OeModuleFiles.AsNoTracking()
            .Where(f => f.Module!.ReleaseId == releaseId)
            .OrderBy(f => f.Id)
            .FirstAsync();

        var targets = await NewComparison(read).GetCompareTargetsAsync(anyFile.Id);
        targets.Should().NotContain(t => t.ReleaseId == releaseId);
    }

    // ── File dependencies (#148) ────────────────────────────────────────

    [Fact]
    public async Task GetFileDependencies_returns_null_for_unknown_file_id()
    {
        await using var read = _db.NewContext();
        var deps = await NewReferences(read).GetFileDependenciesAsync(fileId: 999_999);
        deps.Should().BeNull();
    }

    [Fact]
    public async Task GetFileDependencies_returns_empty_lists_when_file_has_no_objects()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        // Pick any file that has at least one object so we actually exercise
        // the query path; the fixtures all attach objects so this is the
        // happy-path sanity check.
        var file = await read.OeModuleFiles.AsNoTracking()
            .Where(f => read.OeModuleObjects.Any(o => o.SourceFileId == f.Id))
            .OrderBy(f => f.Id)
            .FirstAsync();

        var deps = await NewReferences(read).GetFileDependenciesAsync(file.Id);
        deps.Should().NotBeNull();
        // No guarantee either list is non-empty for an arbitrary fixture
        // file — but the call must succeed without throwing, and both
        // collections must be present (not null).
        deps!.Using.Should().NotBeNull();
        deps.UsedBy.Should().NotBeNull();
    }

    [Fact]
    public async Task GetFileDependencies_excludes_self_references_in_using()
    {
        // The Using list should not surface the file's own object back at
        // itself — covers Rec/xRec self-typing on tables and self-call
        // patterns on codeunits.
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var fileAndObjects = await read.OeModuleFiles.AsNoTracking()
            .Where(f => read.OeModuleObjects.Any(o => o.SourceFileId == f.Id))
            .Select(f => new
            {
                f.Id,
                Objects = read.OeModuleObjects.AsNoTracking()
                    .Where(o => o.SourceFileId == f.Id)
                    .Select(o => new { o.Kind, o.ObjectId, o.Name })
                    .ToList(),
            })
            .FirstAsync();

        var deps = await NewReferences(read).GetFileDependenciesAsync(fileAndObjects.Id);
        deps.Should().NotBeNull();
        foreach (var own in fileAndObjects.Objects)
        {
            deps!.Using.Should().NotContain(d =>
                d.TargetObjectKind == own.Kind
                && d.TargetObjectName == own.Name);
        }
    }

    [Fact]
    public async Task Reextract_captures_a_child_releases_reference_to_a_base_table_field()
    {
        // A project release sits on a parent BC base. Its tableextension code
        // reads a BASE field (Rec.Priority on Prod. Order Line). The base table
        // and its Priority field live in the PARENT Release, so the import-time
        // resolver must see the parent chain to resolve the member — otherwise
        // the field_access reference is silently dropped (the bug this fixes).
        var baseAppId = Guid.NewGuid();
        int childReleaseId;
        await using (var write = _db.NewContext())
        {
            // Parent: a "Base Application" module with table 5406 "Prod. Order
            // Line" and its Priority field. Microsoft + a foundational name so
            // it's implicitly visible to the child (AL never declares a
            // dependency on the base apps — they're implicit).
            var parent = new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId,
                Label = "BC base", Kind = "first_party", Status = "ready",
                ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            write.OeReleases.Add(parent);
            await write.SaveChangesAsync();

            var baseApp = new OeModule
            {
                OrganizationId = TestDb.DefaultOrgId, ReleaseId = parent.Id,
                AppId = baseAppId, Name = "Base Application", Publisher = "Microsoft",
                Version = "1.0.0.0", CreatedAt = DateTime.UtcNow, DependencyCount = 0,
            };
            write.OeModules.Add(baseApp);
            await write.SaveChangesAsync();

            var baseTable = new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId, ModuleId = baseApp.Id,
                Kind = "table", ObjectId = 5406, Name = "Prod. Order Line", LineNumber = 1,
            };
            write.OeModuleObjects.Add(baseTable);
            await write.SaveChangesAsync();

            write.OeModuleSymbols.Add(new OeModuleSymbol
            {
                OrganizationId = TestDb.DefaultOrgId, ModuleId = baseApp.Id,
                ObjectId = baseTable.Id, Kind = "table_field", Name = "Priority",
                FieldId = 53, LineNumber = 5,
            });
            await write.SaveChangesAsync();

            // Child: a project extension anchored to the parent, with a
            // tableextension on Prod. Order Line whose code reads Rec.Priority.
            var child = new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId,
                Label = "Acme on BC base", Kind = "project", Status = "ready",
                ParentReleaseId = parent.Id,
                ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            write.OeReleases.Add(child);
            await write.SaveChangesAsync();
            childReleaseId = child.Id;

            var ext = new OeModule
            {
                OrganizationId = TestDb.DefaultOrgId, ReleaseId = child.Id,
                AppId = Guid.NewGuid(), Name = "Acme Ext", Publisher = "Acme",
                Version = "1.0.0.0", CreatedAt = DateTime.UtcNow, DependencyCount = 0,
            };
            write.OeModules.Add(ext);
            await write.SaveChangesAsync();

            var lines = new[]
            {
                "tableextension 50000 \"ProdOrderLineExt\" extends \"Prod. Order Line\"",
                "{",
                "    procedure CheckPriority()",
                "    begin",
                "        if Rec.Priority <> 0 then",
                "            Rec.Priority := 1;",
                "    end;",
                "}",
            };
            var content = string.Join("\n", lines);
            const string hash = "BEEFBEEFBEEFBEEFBEEFBEEFBEEFBEEFBEEFBEEFBEEFBEEFBEEFBEEFBEEFBEEF1";
            write.OeFileContents.Add(new OeFileContent
            {
                ContentHash = hash, Content = content, ContentLength = content.Length, LineCount = lines.Length,
            });
            var file = new OeModuleFile
            {
                OrganizationId = TestDb.DefaultOrgId, ModuleId = ext.Id,
                Path = "src/ProdOrderLineExt.TableExt.al", ContentHash = hash, LineCount = lines.Length,
            };
            write.OeModuleFiles.Add(file);
            await write.SaveChangesAsync();

            var extObject = new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId, ModuleId = ext.Id,
                Kind = "tableextension", ObjectId = 50000, Name = "ProdOrderLineExt",
                ExtendsObjectName = "Prod. Order Line", ExtendsAppId = baseAppId,
                LineNumber = 1, SourceFileId = file.Id,
            };
            write.OeModuleObjects.Add(extObject);
            await write.SaveChangesAsync();

            write.OeModuleSymbols.Add(new OeModuleSymbol
            {
                OrganizationId = TestDb.DefaultOrgId, ModuleId = ext.Id,
                ObjectId = extObject.Id, Kind = "procedure", Name = "CheckPriority",
                LineNumber = 3, ColumnStart = 5, ColumnEnd = 18,
            });
            await write.SaveChangesAsync();

            // Re-run Phase-2 extraction over the seeded rows.
            await NewImporter(write).ReextractReferencesAsync(child.Id);
        }

        await using var read = _db.NewContext();

        // The field_access reference to the BASE Priority field must now exist,
        // sourced from the child's tableextension and targeting the base app.
        var fieldRefs = await read.OeModuleReferences.AsNoTracking()
            .Where(r => r.ReferenceKind == "field_access"
                && r.TargetMemberName == "Priority"
                && r.TargetObjectName == "Prod. Order Line")
            .Select(r => new { r.TargetAppId, SourceObjectName = r.SourceObject!.Name })
            .ToListAsync();

        fieldRefs.Should().NotBeEmpty(
            because: "the import-time resolver now sees the parent chain, so Rec.Priority resolves to the base field");
        fieldRefs.Should().OnlyContain(r => r.TargetAppId == baseAppId);
        fieldRefs.Should().Contain(r => r.SourceObjectName == "ProdOrderLineExt");

        // End-to-end: find-references seeded at the child surfaces it.
        var owner = await ChainObjectResolution.ResolveObjectAsync(
            read, childReleaseId, "Prod. Order Line", "table", objectId: null, CancellationToken.None);
        owner.Should().NotBeNull();
        var matches = await NewReferences(read).FindReferencesForSymbolAsync(
            childReleaseId,
            new FindReferencesQuery(owner!.AppId, owner.Kind, owner.ObjectId, owner.Name, "Priority", "table_field"));
        matches.Should().Contain(m => m.SourceObjectName == "ProdOrderLineExt" && m.MemberName == "Priority");
    }

    [Fact]
    public async Task Reextract_twice_leaves_the_reference_count_unchanged()
    {
        // Re-extraction used to clear only method_call and field_access
        // while the extractor also re-emits property_object,
        // implements_interface, label_use and variable_use — so every
        // re-run multiplied those rows (issue #712). The source below
        // emits an implements_interface and a label_use, neither of which
        // the old sweep touched.
        int releaseId;
        await using (var write = _db.NewContext())
        {
            var release = new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId,
                Label = "CRONUS release", Kind = "project", Status = "ready",
                ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            write.OeReleases.Add(release);
            await write.SaveChangesAsync();
            releaseId = release.Id;

            var module = new OeModule
            {
                OrganizationId = TestDb.DefaultOrgId, ReleaseId = release.Id,
                AppId = Guid.NewGuid(), Name = "CRONUS Ext", Publisher = "CRONUS",
                Version = "1.0.0.0", CreatedAt = DateTime.UtcNow, DependencyCount = 0,
            };
            write.OeModules.Add(module);
            await write.SaveChangesAsync();

            var lines = new[]
            {
                "codeunit 50000 \"CRONUS Greeter\" implements \"Sample Iface\"",
                "{",
                "    var",
                "        HelloMsg: Label 'Hello';",
                "",
                "    procedure Greet()",
                "    begin",
                "        Message(HelloMsg);",
                "    end;",
                "}",
            };
            var content = string.Join("\n", lines);
            const string hash = "CAFECAFECAFECAFECAFECAFECAFECAFECAFECAFECAFECAFECAFECAFECAFECAFE1";
            write.OeFileContents.Add(new OeFileContent
            {
                ContentHash = hash, Content = content, ContentLength = content.Length, LineCount = lines.Length,
            });
            var file = new OeModuleFile
            {
                OrganizationId = TestDb.DefaultOrgId, ModuleId = module.Id,
                Path = "src/Greeter.Codeunit.al", ContentHash = hash, LineCount = lines.Length,
            };
            write.OeModuleFiles.Add(file);
            await write.SaveChangesAsync();

            var obj = new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId, ModuleId = module.Id,
                Kind = "codeunit", ObjectId = 50000, Name = "CRONUS Greeter",
                LineNumber = 1, SourceFileId = file.Id,
            };
            write.OeModuleObjects.Add(obj);
            await write.SaveChangesAsync();

            write.OeModuleSymbols.Add(new OeModuleSymbol
            {
                OrganizationId = TestDb.DefaultOrgId, ModuleId = module.Id,
                ObjectId = obj.Id, Kind = "procedure", Name = "Greet", LineNumber = 6,
            });
            await write.SaveChangesAsync();

            await NewImporter(write).ReextractReferencesAsync(release.Id);
        }

        int afterFirst;
        await using (var read = _db.NewContext())
        {
            afterFirst = await read.OeModuleReferences.AsNoTracking()
                .CountAsync(r => r.Module!.ReleaseId == releaseId);
        }
        afterFirst.Should().BeGreaterThan(0,
            because: "the fixture emits at least an implements_interface and a label_use");

        await using (var write = _db.NewContext())
        {
            await NewImporter(write).ReextractReferencesAsync(releaseId);
        }

        await using var verify = _db.NewContext();
        var afterSecond = await verify.OeModuleReferences.AsNoTracking()
            .CountAsync(r => r.Module!.ReleaseId == releaseId);
        afterSecond.Should().Be(afterFirst);
    }
}
