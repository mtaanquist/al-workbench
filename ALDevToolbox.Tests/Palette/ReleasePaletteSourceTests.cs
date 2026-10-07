using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Services.Palette.Sources;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// The Releases source (#883): found by label, Business Central version,
/// country, and - for a release a Solution build produced - the Solution's name.
///
/// <para>Runs the shared fence harness
/// (<see cref="PaletteSourceVisibilityTestBase"/>) over a world where every
/// solution has a build that produced a release, which is the case the fence is
/// about: a Private solution's build must not surface its release by the
/// release's own label nor by the solution's name.</para>
/// </summary>
public sealed class ReleasePaletteSourceTests : PaletteSourceVisibilityTestBase
{
    protected override IPaletteSource CreateSource(PaletteSourceUnderTest context) =>
        new ReleasePaletteSource(context.Db, context.Access, Db.NewToolEnablement(context.Db));

    /// <summary>
    /// One ready release per solution, produced by a build of it - the linkage
    /// <c>ProjectAccess.VisibleReleasePredicate</c> walks. The label carries the
    /// solution's name the way <c>ProjectBuildService</c> writes it, so the
    /// harness's search for a Private solution's name exercises both the label
    /// and the joined-solution path at once.
    /// </summary>
    protected override async Task SeedRowAsync(AppDbContext ctx, PaletteSourceSeed seed)
    {
        await SeedBuildReleaseAsync(ctx, seed.OrganizationId, seed.ProjectId, $"{seed.Title} on BC 26.0");
    }

    [Fact]
    public async Task A_release_is_found_by_its_version_and_country()
    {
        await SeedWorldAsync();
        await SeedArtifactReleaseAsync("Business Central 26.0 (DK)", "26.0.12345.67890", "bc-onprem:26.0:dk");
        await SeedArtifactReleaseAsync("Business Central 26.0 (W1)", "26.0.12345.67890", "bc-onprem:26.0:w1");

        var results = await SearchAsync("26.0 dk");

        var row = results.Should().ContainSingle(
            "'26.0 dk' names one release, and the W1 build of the same version is not it").Subject;
        row.Kind.Should().Be("release");
        row.Title.Should().Be("Business Central 26.0 (DK)");
        row.Subtitle.Should().Be("26.0.12345.67890 - DK");
    }

    [Fact]
    public async Task A_release_is_found_by_the_name_of_the_solution_whose_build_produced_it()
    {
        await SeedWorldAsync();
        // A label with nothing of the solution's name in it, so only the join
        // through the build can find this row.
        await SeedBuildReleaseAsync(null, TestDbOrgId, VisibleProjectId, "Nightly 2026-09-20");

        var results = await SearchAsync("cronus nightly");

        var row = results.Should().ContainSingle().Subject;
        row.Title.Should().Be("Nightly 2026-09-20");
        row.Subtitle.Should().Contain(VisibleName, "the row has to say why it is here");
    }

    [Fact]
    public async Task A_private_solutions_release_is_hidden_by_its_own_label_too()
    {
        await SeedWorldAsync();
        // The leak the harness cannot see: a Private solution's build produced a
        // release whose label says nothing about the solution, so searching the
        // solution's name would never reach it - but searching the label would,
        // if the visibility predicate were missing.
        await SeedBuildReleaseAsync(null, TestDbOrgId, PrivateProjectId, "Midnight Special 9.9");

        var results = await SearchAsync("midnight special");

        results.Should().BeEmpty(
            "a release a Private solution's build produced is as private as the solution");
    }

    [Fact]
    public async Task A_deleted_release_is_left_out()
    {
        await SeedWorldAsync();
        await SeedArtifactReleaseAsync(
            "Business Central 24.0 (DK)", "24.0.1.1", "bc-onprem:24.0:dk",
            deletedAt: DateTime.UtcNow);

        var results = await SearchAsync("24.0 dk");

        results.Should().BeEmpty("a deleted release is gone, not merely hidden from the list");
    }

    [Fact]
    public async Task A_failed_release_is_left_out()
    {
        await SeedWorldAsync();
        await SeedArtifactReleaseAsync(
            "Business Central 23.0 (DK)", "23.0.1.1", "bc-onprem:23.0:dk", status: "failed");

        var results = await SearchAsync("23.0 dk");

        results.Should().BeEmpty("a failed import is a tombstone holding no objects to open");
    }

    [Fact]
    public async Task An_importing_release_is_offered_and_says_so()
    {
        await SeedWorldAsync();
        await SeedArtifactReleaseAsync(
            "Business Central 27.0 (DK)", "27.0.1.1", "bc-onprem:27.0:dk", status: "ingesting");

        var results = await SearchAsync("27.0 dk");

        results.Should().ContainSingle().Which.Subtitle.Should().Be("27.0.1.1 - DK - Ingesting...");
    }

    [Fact]
    public async Task An_importing_solution_build_says_it_is_building()
    {
        await SeedWorldAsync();
        await SeedBuildReleaseAsync(
            null, TestDbOrgId, VisibleProjectId, "Nightly 2026-09-21", status: "ingesting");

        var results = await SearchAsync("nightly 2026-09-21");

        results.Should().ContainSingle().Which.Subtitle.Should()
            .EndWith("Building...", "a solution's release that is ingesting is mid-build");
    }

    [Fact]
    public async Task A_ready_release_says_nothing_about_its_status()
    {
        await SeedWorldAsync();
        await SeedArtifactReleaseAsync("Business Central 22.0 (DK)", "22.0.1.1", "bc-onprem:22.0:dk");

        var results = await SearchAsync("22.0 dk");

        results.Should().ContainSingle().Which.Subtitle.Should().Be("22.0.1.1 - DK");
    }

    [Fact]
    public async Task A_wildcard_in_the_query_matches_literally()
    {
        await SeedWorldAsync();
        await SeedArtifactReleaseAsync("Business Central 21.0 (DK)", "21.0.1.1", "bc-onprem:21.0:dk");

        var results = await SearchAsync("%% dk");

        results.Should().BeEmpty("'%' is a character somebody typed, not a wildcard to hand to ILIKE");
    }

    // ── Seeding ─────────────────────────────────────────────────────────

    /// <summary>The organisation the harness's caller belongs to.</summary>
    private const int TestDbOrgId = Infrastructure.TestDb.DefaultOrgId;

    /// <summary>
    /// A symbols package a build pulled from the feeds with no source in it is not on
    /// the releases page, so the palette does not offer it either; one that carried
    /// source is offered (#1092).
    /// </summary>
    [Fact]
    public async Task An_empty_symbol_package_from_the_feeds_is_not_offered()
    {
        await SeedWorldAsync();
        await SeedFeedSymbolsAsync("CRONUS Banking - Import 28.6.0.0 (symbols)", "symbols:aaaa:28.6.0.0", files: 0);
        await SeedFeedSymbolsAsync("CRONUS Banking - PSP 28.6.0.0 (symbols)", "symbols:bbbb:28.6.0.0", files: 3);
        // Still importing: its files are not counted yet, so it is offered like any
        // other release that is on its way.
        await SeedFeedSymbolsAsync("CRONUS Banking - SEPA 28.6.0.0 (symbols)", "symbols:cccc:28.6.0.0", files: 0, status: "ingesting");

        var results = await SearchAsync("cronus banking");

        results.Select(r => r.Title).Should().BeEquivalentTo(
            "CRONUS Banking - PSP 28.6.0.0 (symbols)", "CRONUS Banking - SEPA 28.6.0.0 (symbols)");
    }

    [Fact]
    public async Task A_preview_check_that_indexed_nothing_is_not_offered()
    {
        await SeedWorldAsync();
        await SeedBuildReleaseAsync(null, TestDbOrgId, VisibleProjectId, "Nightly check 2026-10-07", bcTarget: ProjectBuildTarget.NextMajor);

        var results = await SearchAsync("cronus nightly check");

        results.Should().BeEmpty("a preview check records compile results only (#1140)");
    }

    private async Task SeedFeedSymbolsAsync(string label, string dedupKey, int files, string status = "ready")
    {
        await using var ctx = Db.NewContext();
        ctx.OeReleases.Add(new OeRelease
        {
            OrganizationId = TestDbOrgId,
            Label = label,
            BcVersion = "28.6",
            DedupKey = dedupKey,
            Kind = "third_party",
            Status = status,
            SourceFileCount = files,
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// A Microsoft OnPrem artifact import: the one kind of release that records
    /// a country, inside its dedup key. Mirrors
    /// <c>BcArtifactIndex.FormatLabel</c> / <c>FormatDedupKey</c>.
    /// </summary>
    private async Task SeedArtifactReleaseAsync(
        string label,
        string bcVersion,
        string dedupKey,
        string status = "ready",
        DateTime? deletedAt = null)
    {
        await using var ctx = Db.NewContext();
        ctx.OeReleases.Add(new OeRelease
        {
            OrganizationId = TestDbOrgId,
            Label = label,
            BcVersion = bcVersion,
            DedupKey = dedupKey,
            Kind = "first_party",
            Status = status,
            DeletedAt = deletedAt,
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// A release a Solution build produced, with the build row that links the
    /// two. <paramref name="ctx"/> is the harness's context when it is seeding
    /// the world, and null when a case seeds one of its own.
    /// </summary>
    private async Task SeedBuildReleaseAsync(
        AppDbContext? ctx, int organizationId, int projectId, string label, string status = "ready",
        string bcTarget = ProjectBuildTarget.Current)
    {
        var owned = ctx is null ? Db.NewContext() : null;
        var context = ctx ?? owned!;
        try
        {
            var release = new OeRelease
            {
                OrganizationId = organizationId,
                Label = label,
                BcVersion = "26.0.1.1",
                Kind = "project",
                Status = status,
                // Stamped at build time and deliberately not what the source
                // searches: a stale copy of the solution's name must not be a
                // way past the fence.
                ProjectName = label,
                ImportedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            context.OeReleases.Add(release);
            await context.SaveChangesAsync();

            context.OeProjectBuilds.Add(new OeProjectBuild
            {
                OrganizationId = organizationId,
                ProjectId = projectId,
                ReleaseId = release.Id,
                Status = ProjectBuildStatus.Ready,
                BcTarget = bcTarget,
                StartedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }
        finally
        {
            if (owned is not null) await owned.DisposeAsync();
        }
    }
}
