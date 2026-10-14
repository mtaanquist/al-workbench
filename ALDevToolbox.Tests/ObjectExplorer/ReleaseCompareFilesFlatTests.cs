using System.Data.Common;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The flat release-vs-release file diff — the rows the Release page's Compare
/// scope and the <c>compare_release_files</c> MCP tool render.
///
/// Two things are pinned here. The first is the row set itself (contents and
/// order), because the flat walk was moved off the per-module compare and onto
/// the file rows the summary already loads; the output must not have shifted.
/// The second is the cost: the number of queries must not grow with the number
/// of changed modules, which is what made a real DVD-sized compare issue
/// thousands of sequential round-trips (#683).
/// </summary>
public sealed class ReleaseCompareFilesFlatTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private const string HashA = "hash-a";
    private const string HashB = "hash-b";

    [Fact]
    public async Task Flat_compare_reports_every_added_removed_and_modified_file()
    {
        var (leftId, rightId) = await SeedFixtureAsync(changedModuleCount: 1);

        await using var read = _db.NewContext();
        var rows = await NewComparison(read).CompareReleaseFilesFlatAsync(leftId, rightId);

        rows.Select(r => (r.ModuleName, r.Path, r.Status)).Should().Equal(
            ("Changed App 0", "src/Added.al", "added"),
            ("Changed App 0", "src/Modified.al", "modified"),
            ("Changed App 0", "src/Removed.al", "removed"),
            ("Left Only App", "src/Gone.al", "removed"),
            ("Right Only App", "src/Brand New.al", "added"),
            ("Right Only App", "src/Second.al", "added"));

        rows.Should().OnlyContain(r =>
            (r.Status == "added" && r.LeftFileId == null && r.RightFileId != null)
            || (r.Status == "removed" && r.LeftFileId != null && r.RightFileId == null)
            || (r.Status == "modified" && r.LeftFileId != null && r.RightFileId != null));

        rows.Should().NotContain(r => r.ModuleName == "Unchanged App",
            because: "a module whose files all hash the same contributes no rows");
    }

    /// <summary>
    /// The cost guard. One changed module and six must cost the same number of
    /// round-trips: the summary's batched reads already carry every file row the
    /// flat view needs, and both modules of a pair belong to the two releases
    /// whose visibility was checked once on the way in.
    /// </summary>
    [Fact]
    public async Task Flat_compare_cost_does_not_grow_with_the_number_of_changed_modules()
    {
        var one = await SeedFixtureAsync(changedModuleCount: 1);
        var many = await SeedFixtureAsync(changedModuleCount: 6);

        var (rowsOne, queriesOne) = await MeasureAsync(one.LeftId, one.RightId);
        var (rowsMany, queriesMany) = await MeasureAsync(many.LeftId, many.RightId);

        rowsMany.Should().BeGreaterThan(rowsOne, because: "the bigger fixture really does diff more files");
        queriesMany.Should().Be(queriesOne,
            because: "the per-module file compare was replaced by an in-memory walk over "
                   + "rows the summary had already loaded (#683)");
    }

    /// <summary>
    /// The cap follows the find-references convention: at most take + 1 rows come
    /// back, and the extra row is what tells the caller the diff was cut, so the
    /// page can say "showing the first N" instead of quietly listing a subset.
    /// The rows returned are still the first N of the full sort order (#685).
    /// </summary>
    [Fact]
    public async Task Flat_compare_returns_one_row_past_the_cap_when_truncated()
    {
        var (leftId, rightId) = await SeedFixtureAsync(changedModuleCount: 1);

        await using var read = _db.NewContext();
        var comparison = NewComparison(read);

        var all = await comparison.CompareReleaseFilesFlatAsync(leftId, rightId, take: null);
        all.Should().HaveCount(6, "the fixture diffs six files in total");

        var capped = await comparison.CompareReleaseFilesFlatAsync(leftId, rightId, take: 2);

        capped.Should().HaveCount(3, "two rows plus the one that flags the truncation");
        capped.Take(2).Should().Equal(all.Take(2),
            because: "the cap keeps the first rows of the full sort order");

        var uncut = await comparison.CompareReleaseFilesFlatAsync(leftId, rightId, take: 6);
        uncut.Should().HaveCount(6, "an exactly-fitting result has no extra row to flag");
    }

    /// <summary>
    /// An amend can put a newer version of an app beside the old one in the same
    /// release. Compare used to throw on the duplicate app id; it now compares the
    /// higher version, and the chain queries pick the same module (#1224). The
    /// newer version is inserted first so a pick by row id alone would get it wrong.
    /// </summary>
    [Fact]
    public async Task Compare_picks_the_higher_version_when_a_release_holds_two_of_one_app()
    {
        await using var ctx = _db.NewContext();
        var left = NewRelease("CRONUS 2.0.0.0");
        var right = NewRelease("CRONUS amended");
        ctx.OeReleases.AddRange(left, right);
        await ctx.SaveChangesAsync();
        if (!await ctx.OeFileContents.AnyAsync(c => c.ContentHash == HashA))
        {
            ctx.OeFileContents.Add(new OeFileContent { ContentHash = HashA, Content = "// a", ContentLength = 4, LineCount = 1 });
        }
        if (!await ctx.OeFileContents.AnyAsync(c => c.ContentHash == HashB))
        {
            ctx.OeFileContents.Add(new OeFileContent { ContentHash = HashB, Content = "// b", ContentLength = 4, LineCount = 1 });
        }
        var appId = Guid.NewGuid();
        var oldLeft = NewModule(left.Id, appId, "Contoso", "2.0.0.0");
        var newer = NewModule(right.Id, appId, "Contoso", "2.0.10.0");
        ctx.OeModules.AddRange(oldLeft, newer);
        await ctx.SaveChangesAsync();
        var older = NewModule(right.Id, appId, "Contoso", "2.0.9.0");
        ctx.OeModules.Add(older);
        await ctx.SaveChangesAsync();
        ctx.OeModuleFiles.AddRange(
            NewFile(oldLeft.Id, "src/Contoso.al", HashA),
            NewFile(older.Id, "src/Contoso.al", HashA),
            NewFile(newer.Id, "src/Contoso.al", HashB));
        await ctx.SaveChangesAsync();

        await using var read = _db.NewContext();
        var summary = await NewComparison(read).CompareReleasesAsync(left.Id, right.Id);

        summary.Should().NotBeNull();
        summary!.Changed.Should().ContainSingle()
            .Which.RightModuleId.Should().Be(newer.Id);

        var winning = await read.Database
            .SqlQueryRaw<long>(ReleaseAncestrySql.WinningModules + "\nSELECT id AS \"Value\" FROM winning", right.Id)
            .ToListAsync();
        winning.Should().Equal(newer.Id);
    }

    /// <summary>
    /// The object compare and "Compare with..." make the same pick as the module
    /// compare above (#1332). They used to read every object of the release and
    /// keep whichever copy came back first, which is the older version when it was
    /// written first, as it is after an amend: the changed object read
    /// "unchanged", an object the newer version dropped still showed, and the diff
    /// link opened the old file.
    /// </summary>
    [Fact]
    public async Task Object_compare_picks_the_higher_version_when_a_release_holds_two_of_one_app()
    {
        await using var ctx = _db.NewContext();
        var left = NewRelease("CRONUS objects 2.0.0.0");
        var right = NewRelease("CRONUS objects amended");
        ctx.OeReleases.AddRange(left, right);
        await ctx.SaveChangesAsync();
        await EnsureContentAsync(ctx, HashA);
        await EnsureContentAsync(ctx, HashB);
        var appId = Guid.NewGuid();
        var oldLeft = NewModule(left.Id, appId, "Contoso", "2.0.0.0");
        var older = NewModule(right.Id, appId, "Contoso", "2.0.9.0");
        ctx.OeModules.AddRange(oldLeft, older);
        await ctx.SaveChangesAsync();
        var newer = NewModule(right.Id, appId, "Contoso", "2.0.10.0");
        ctx.OeModules.Add(newer);
        await ctx.SaveChangesAsync();
        var leftFile = NewFile(oldLeft.Id, "src/Contoso.al", HashA);
        var olderFile = NewFile(older.Id, "src/Contoso.al", HashA);
        var droppedFile = NewFile(older.Id, "src/Dropped.al", HashA);
        var newerFile = NewFile(newer.Id, "src/Contoso.al", HashB);
        ctx.OeModuleFiles.AddRange(leftFile, olderFile, droppedFile, newerFile);
        await ctx.SaveChangesAsync();
        // The older copy's objects go in first, so a first-wins pick over an
        // unordered read lands on them.
        ctx.OeModuleObjects.AddRange(
            NewObject(oldLeft.Id, "table", 50100, "Contoso Setup", leftFile.Id),
            NewObject(older.Id, "table", 50100, "Contoso Setup", olderFile.Id),
            NewObject(older.Id, "codeunit", 50101, "Contoso Dropped", droppedFile.Id));
        await ctx.SaveChangesAsync();
        ctx.OeModuleObjects.Add(NewObject(newer.Id, "table", 50100, "Contoso Setup", newerFile.Id));
        await ctx.SaveChangesAsync();

        await using var read = _db.NewContext();
        var comparison = NewComparison(read);

        var set = await comparison.CompareReleaseObjectsAsync(left.Id, right.Id, includeUnchanged: true);
        var row = set.Rows.Should().ContainSingle(
            "the codeunit only the older version carried is not part of what the release resolves to").Which;
        row.Status.Should().Be("modified");
        row.RightFileId.Should().Be(newerFile.Id);

        var counterpart = await comparison.FindObjectFileInReleaseAsync(right.Id, "table", 50100, "Contoso Setup");
        counterpart.Should().Be(newerFile.Id);
        (await comparison.FindObjectFileInReleaseAsync(right.Id, "codeunit", 50101, "Contoso Dropped"))
            .Should().BeNull();
    }

    private static OeModuleObject NewObject(long moduleId, string kind, int objectId, string name, long fileId) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        ModuleId = moduleId,
        Kind = kind,
        ObjectId = objectId,
        Name = name,
        SourceFileId = fileId,
    };

    /// <summary>
    /// The database cut and the in-memory sort used different orders (the
    /// column's en_US collation against OrdinalIgnoreCase), so past the cap the
    /// "first N" could skip rows in the middle. en_US ranks "A1" before "a b";
    /// a case-insensitive ordinal sort puts it after "a-b" (#1262).
    /// </summary>
    [Fact]
    public async Task Flat_compare_cut_is_a_clean_prefix_with_mixed_case_and_punctuation()
    {
        int leftId, rightId;
        await using (var ctx = _db.NewContext())
        {
            var left = NewRelease("Sort left");
            var right = NewRelease("Sort right");
            ctx.OeReleases.AddRange(left, right);
            await ctx.SaveChangesAsync();
            await EnsureContentAsync(ctx, HashA);
            var fresh = NewModule(right.Id, Guid.NewGuid(), "Right Only App", "1.0.0.0");
            ctx.OeModules.Add(fresh);
            await ctx.SaveChangesAsync();
            foreach (var path in new[] { "src/A1.al", "src/a b.al", "src/a-b.al", "src/a.b.al", "src/Ab.al" })
            {
                ctx.OeModuleFiles.Add(NewFile(fresh.Id, path, HashA));
            }
            await ctx.SaveChangesAsync();
            (leftId, rightId) = (left.Id, right.Id);
        }

        await using var read = _db.NewContext();
        var comparison = NewComparison(read);
        var all = await comparison.CompareReleaseFilesFlatAsync(leftId, rightId, take: null);

        for (var take = 1; take < all.Count; take++)
        {
            var capped = await comparison.CompareReleaseFilesFlatAsync(leftId, rightId, take: take);
            capped.Select(r => r.Path).Should().Equal(all.Take(take + 1).Select(r => r.Path),
                because: $"a cut at {take} must keep the first rows of the full order, with no gaps");
        }
    }

    /// <summary>
    /// Objects from a symbols-only app have no source file, so there is no hash
    /// to compare, and every one of them used to read "modified" (#1262). Two
    /// releases holding the same app package compare as unchanged; a newer
    /// version of the app still reads as modified.
    /// </summary>
    [Fact]
    public async Task Object_compare_of_identical_symbols_only_apps_reports_unchanged()
    {
        await using var ctx = _db.NewContext();
        var left = NewRelease("Symbols left");
        var same = NewRelease("Symbols same");
        var newer = NewRelease("Symbols newer");
        ctx.OeReleases.AddRange(left, same, newer);
        await ctx.SaveChangesAsync();
        var appId = Guid.NewGuid();
        var modules = new[]
        {
            NewModule(left.Id, appId, "Symbols App", "1.0.0.0"),
            NewModule(same.Id, appId, "Symbols App", "1.0.0.0"),
            NewModule(newer.Id, appId, "Symbols App", "2.0.0.0"),
        };
        modules[0].AppFileHash = modules[1].AppFileHash = "app-v1";
        modules[2].AppFileHash = "app-v2";
        ctx.OeModules.AddRange(modules);
        await ctx.SaveChangesAsync();
        foreach (var m in modules)
        {
            ctx.OeModuleObjects.Add(new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = m.Id,
                Kind = "table",
                ObjectId = 50100,
                Name = "Symbols Table",
            });
        }
        await ctx.SaveChangesAsync();

        await using var read = _db.NewContext();
        var comparison = NewComparison(read);

        var unchanged = await comparison.CompareReleaseObjectsAsync(left.Id, same.Id, includeUnchanged: true);
        unchanged.Rows.Should().ContainSingle().Which.Status.Should().Be("unchanged");

        var changed = await comparison.CompareReleaseObjectsAsync(left.Id, newer.Id, includeUnchanged: true);
        changed.Rows.Should().ContainSingle().Which.Status.Should().Be("modified");
    }

    private static async Task EnsureContentAsync(AppDbContext ctx, string hash)
    {
        if (!await ctx.OeFileContents.AnyAsync(c => c.ContentHash == hash))
        {
            ctx.OeFileContents.Add(new OeFileContent { ContentHash = hash, Content = "// " + hash, ContentLength = 10, LineCount = 1 });
        }
    }

    private ReleaseComparisonService NewComparison(AppDbContext ctx) =>
        new(ctx, new ProjectAccess(ctx, _db.OrgContext), NullLogger<ReleaseComparisonService>.Instance);

    private async Task<(int Rows, int Queries)> MeasureAsync(int leftId, int rightId)
    {
        var recorder = new CommandCounter();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_db.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(recorder)
            .Options;

        await using var ctx = new AppDbContext(options, _db.OrgContext);
        var rows = await NewComparison(ctx).CompareReleaseFilesFlatAsync(leftId, rightId);
        return (rows.Count, recorder.Count);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    // ── Fixture ────────────────────────────────────────────────────────

    /// <summary>
    /// Two releases carrying one module of each shape the flat view has to
    /// handle: added, removed, unchanged, and <paramref name="changedModuleCount"/>
    /// changed ones (each with one added, one removed and one modified file).
    /// </summary>
    private async Task<(int LeftId, int RightId)> SeedFixtureAsync(int changedModuleCount)
    {
        await using var ctx = _db.NewContext();

        var left = NewRelease("BC 26.0 CRONUS");
        var right = NewRelease("BC 26.1 CRONUS");
        ctx.OeReleases.AddRange(left, right);
        await ctx.SaveChangesAsync();

        foreach (var hash in new[] { HashA, HashB })
        {
            if (!await ctx.OeFileContents.AnyAsync(c => c.ContentHash == hash))
            {
                ctx.OeFileContents.Add(new OeFileContent
                {
                    ContentHash = hash,
                    Content = "// " + hash,
                    ContentLength = 10,
                    LineCount = 1,
                });
            }
        }
        await ctx.SaveChangesAsync();

        var files = new List<OeModuleFile>();

        for (var i = 0; i < changedModuleCount; i++)
        {
            var appId = Guid.NewGuid();
            var l = NewModule(left.Id, appId, $"Changed App {i}", "26.0.0.0");
            var r = NewModule(right.Id, appId, $"Changed App {i}", "26.1.0.0");
            ctx.OeModules.AddRange(l, r);
            await ctx.SaveChangesAsync();

            files.Add(NewFile(l.Id, "src/Modified.al", HashA));
            files.Add(NewFile(l.Id, "src/Removed.al", HashA));
            files.Add(NewFile(l.Id, "src/Same.al", HashA));
            files.Add(NewFile(r.Id, "src/Modified.al", HashB));
            files.Add(NewFile(r.Id, "src/Added.al", HashA));
            files.Add(NewFile(r.Id, "src/Same.al", HashA));
        }

        var unchangedAppId = Guid.NewGuid();
        var ul = NewModule(left.Id, unchangedAppId, "Unchanged App", "26.0.0.0");
        var ur = NewModule(right.Id, unchangedAppId, "Unchanged App", "26.0.0.0");
        var gone = NewModule(left.Id, Guid.NewGuid(), "Left Only App", "26.0.0.0");
        var fresh = NewModule(right.Id, Guid.NewGuid(), "Right Only App", "26.1.0.0");
        ctx.OeModules.AddRange(ul, ur, gone, fresh);
        await ctx.SaveChangesAsync();

        files.Add(NewFile(ul.Id, "src/Steady.al", HashA));
        files.Add(NewFile(ur.Id, "src/Steady.al", HashA));
        files.Add(NewFile(gone.Id, "src/Gone.al", HashA));
        files.Add(NewFile(fresh.Id, "src/Brand New.al", HashA));
        files.Add(NewFile(fresh.Id, "src/Second.al", HashA));

        ctx.OeModuleFiles.AddRange(files);
        await ctx.SaveChangesAsync();

        return (left.Id, right.Id);
    }

    private static OeRelease NewRelease(string label) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        Label = label,
        Kind = "first_party",
        Status = "ready",
        ImportedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static OeModule NewModule(int releaseId, Guid appId, string name, string version) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        ReleaseId = releaseId,
        AppId = appId,
        Name = name,
        Publisher = "CRONUS",
        Version = version,
        CreatedAt = DateTime.UtcNow,
    };

    private static OeModuleFile NewFile(long moduleId, string path, string hash) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        ModuleId = moduleId,
        Path = path,
        ContentHash = hash,
        LineCount = 1,
    };
}
