using System.Data.Common;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Guards the explorer's listings against the regression that made the source
/// viewer take five seconds on a 230-release install: a per-file lookup whose
/// cost tracked the size of the whole catalogue instead of the size of the page
/// being drawn.
///
/// These are deliberately <em>not</em> wall-clock assertions. A stopwatch test
/// cannot work here, for two reasons that pull in opposite directions: on a
/// fixture small enough to seed in a test it would pass even against the broken
/// code (the defect only shows once the object table is large), and on a fixture
/// large enough to expose it the threshold would have to be so loose to survive
/// a shared CI runner that it would miss anything short of a total collapse.
///
/// So they measure <em>work</em> rather than <em>time</em>: the queries are
/// re-run under EXPLAIN ANALYZE and the rows the database actually touched are
/// counted. That number is deterministic — it does not move with machine load,
/// core count, or a cold cache — and it expresses the real invariant directly:
/// listing a folder of ten files must examine about ten objects' worth of rows,
/// whatever else the catalogue happens to hold. The broken shape examined every
/// object row in the organisation, so it fails this by two orders of magnitude
/// on a fixture that seeds in about a second.
///
/// See <c>ExplorerTreeService.LoadPrimaryObjectsAsync</c> for the shape being
/// guarded and why EF produces it.
/// </summary>
public sealed class ExplorerQueryScalingTests : IClassFixture<ExplorerQueryScalingTests.SkewedModule>
{
    private readonly TestDb _db;
    private readonly long _moduleId;

    public ExplorerQueryScalingTests(SkewedModule fixture)
    {
        _db = fixture.Db;
        _moduleId = fixture.ModuleId;
    }

    /// <summary>
    /// The seeded catalogue, shared by every test in the class. Seeding it
    /// writes about 8,000 rows and costs several seconds, and every test here
    /// only reads (#1584), so a test must not write to it.
    /// </summary>
    public sealed class SkewedModule : IAsyncLifetime
    {
        public TestDb Db { get; } = new();

        public long ModuleId { get; private set; }

        public async ValueTask InitializeAsync() => ModuleId = await SeedSkewedModuleAsync(Db);

        public ValueTask DisposeAsync()
        {
            Db.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Files in the folder under test. The listing has to look at these.
    /// </summary>
    private const int FilesInFolder = 10;

    /// <summary>
    /// Files (and so objects) elsewhere in the same module. These stand in for
    /// the rest of the catalogue: the listing must <em>not</em> look at them.
    /// Large enough that Postgres prefers the index on <c>source_file_id</c>
    /// over a sequential scan, so the measurement reflects the query shape
    /// rather than a planner coin-flip on a tiny table.
    /// </summary>
    private const int FilesElsewhere = 4_000;

    /// <summary>
    /// How many object rows one folder listing may touch. The fixed cost is
    /// <see cref="FilesInFolder"/>; the budget leaves generous headroom for
    /// index-page reads and planner variation while staying far below
    /// <see cref="FilesElsewhere"/> — the number the broken shape touched.
    /// </summary>
    private const int ObjectRowBudget = 500;

    /// <summary>
    /// How many object rows one search may touch. Looser than
    /// <see cref="ObjectRowBudget"/>, and deliberately so: on a fixture this
    /// small Postgres prefers a sequential scan over the trigram index, so the
    /// search reads the object table about once, and that choice is the
    /// planner's to make rather than something a test should legislate. What
    /// the search must not do is read it <em>many times over</em>, which is
    /// what deriving every file's primary object per row amounted to — the
    /// broken shape examined roughly 100,000 rows against these 4,010, some
    /// twenty-five passes. One pass is the invariant; the budget allows two.
    /// </summary>
    private const int SearchRowBudget = 2 * (FilesInFolder + FilesElsewhere);

    [Fact]
    public async Task Listing_one_folder_does_not_examine_the_whole_object_table()
    {
        var moduleId = _moduleId;

        var (_, examined) = await MeasureAsync(
            (_, tree) => tree.GetTreeChildrenAsync(moduleId, "src/Target/"));

        examined.Should().BeLessThan(ObjectRowBudget,
            because: "a ten-file folder listing must cost ten files' worth of object rows, "
                   + $"not the {FilesElsewhere:N0} sitting elsewhere in the catalogue");
    }

    [Fact]
    public async Task Searching_the_explorer_does_not_examine_the_whole_object_table()
    {
        var moduleId = _moduleId;
        int releaseId;
        await using (var ctx = _db.NewContext())
        {
            releaseId = await ctx.OeModules.AsNoTracking()
                .Where(m => m.Id == moduleId).Select(m => m.ReleaseId).SingleAsync();
        }

        // "Needle" is on the ten target objects only, so the answer is small
        // however big the module is around it.
        var (_, examined) = await MeasureAsync(
            (_, tree) => tree.SearchTreeAsync(releaseId, "Needle"));

        examined.Should().BeLessThan(SearchRowBudget,
            because: "the search may read the object table once, but not once per file - "
                   + "deriving every file's object name and then discarding almost all of "
                   + "them turned one pass into twenty-five");
    }

    /// <summary>
    /// The defect this file exists for is structural, not statistical: EF cannot
    /// emit a correlated LIMIT 1 that projects several columns, so it silently
    /// rewrites one into a <c>ROW_NUMBER() OVER (PARTITION BY …)</c> across the
    /// whole table. The window is in the SQL whatever the planner then does with
    /// it, so asserting on its absence catches a reintroduction immediately and
    /// without depending on fixture size or planner mood — the row-count tests
    /// above say the cost is bounded, this one says why it stays bounded.
    /// </summary>
    [Theory]
    [InlineData(Listing.Folder)]
    [InlineData(Listing.WholeModule)]
    [InlineData(Listing.Search)]
    public async Task The_object_lookup_is_keyed_not_windowed_over_the_table(Listing listing)
    {
        var moduleId = _moduleId;

        var (sql, _) = await MeasureAsync((_, tree) => Invoke(tree, listing, moduleId));

        var objectQueries = sql.Where(s => s.Contains("oe_module_objects")).ToList();
        objectQueries.Should().NotBeEmpty(because: "the listing does read the object table");
        objectQueries.Should().OnlyContain(s => !s.Contains("ROW_NUMBER"),
            because: "a ROW_NUMBER window over oe_module_objects is EF's rewrite of an "
                   + "inlined multi-column FirstOrDefault(), and it partitions the entire "
                   + "table to answer one folder - see LoadPrimaryObjectsAsync");
    }

    /// <summary>
    /// A self-check on the measurement, because a guard is only as good as its
    /// counter. "Actual Rows" on its own reports rows <em>emitted</em>, so a
    /// full scan that filters in SQL looks free — it returns nothing and would
    /// sail through the budgets above while reading the whole table. The
    /// counter has to notice the rows that were read and thrown away.
    /// </summary>
    [Fact]
    public async Task The_row_counter_counts_rows_read_and_discarded_not_just_returned()
    {
        // line_number carries no index and no row matches, so the database has
        // to read every object row to answer this and emits none of them.
        var examined = await ExplainRowsAsync(new RecordedCommand(
            "SELECT id FROM oe_module_objects WHERE line_number = -1", []));

        examined.Should().BeGreaterThan(FilesInFolder + FilesElsewhere - 1,
            because: "every object row was read to answer this, and a counter that "
                   + "only saw the (zero) rows returned would call a full scan free");
    }

    /// <summary>
    /// Drawing one source page calls the viewer eight times, and each call used
    /// to re-ask the same two questions: may this file be seen, and what is its
    /// text. Both answers are fixed for the DI scope (in a Blazor circuit, the
    /// page), so both are resolved once and reused — this pins that, because
    /// losing it again is invisible to any functional test.
    /// </summary>
    [Fact]
    public async Task One_page_load_resolves_visibility_and_reads_the_blob_once()
    {
        var moduleId = _moduleId;
        long fileId;
        await using (var ctx = _db.NewContext())
        {
            fileId = await ctx.OeModuleFiles.AsNoTracking()
                .Where(f => f.ModuleId == moduleId && f.Path == "src/Target/Target0.al")
                .Select(f => f.Id)
                .SingleAsync();
        }

        var (sql, _) = await MeasureAsync(async (viewer, _) =>
        {
            await viewer.GetFileHeaderAsync(fileId);
            await viewer.GetFileAsync(fileId);
            await viewer.GetFileOutlineAsync(fileId);
            await viewer.ListDeclarationsInFileAsync(fileId);
            await viewer.ListResolvablesInFileAsync(fileId);
        });

        sql.Count(IsVisibilityProbe).Should().Be(1,
            because: "the file's owning release is resolved once per scope and memoised, "
                   + "not re-resolved by each of the five calls the page makes");
        sql.Count(s => s.Contains("oe_file_contents")).Should().Be(1,
            because: "one page shows one file's text, so the blob crosses the wire once");
    }

    /// <summary>
    /// The visibility probe is the only query that reaches oe_module_files for
    /// the owning release alone — every other read the page makes either joins
    /// the content store, the release row, or the object table.
    /// </summary>
    private static bool IsVisibilityProbe(string sql) =>
        sql.Contains("oe_module_files")
        && sql.Contains("release_id")
        && !sql.Contains("oe_file_contents")
        && !sql.Contains("oe_releases")
        && !sql.Contains("oe_module_objects");

    /// <summary>Which listing a case exercises. All three share the lookup.</summary>
    public enum Listing
    {
        /// <summary>One folder's children.</summary>
        Folder,

        /// <summary>Every file in one app, as the flat and by-kind groupings ask for.</summary>
        WholeModule,

        /// <summary>The explorer's search box.</summary>
        Search,
    }

    private async Task Invoke(ExplorerTreeService tree, Listing listing, long moduleId)
    {
        switch (listing)
        {
            case Listing.Folder:
                await tree.GetTreeChildrenAsync(moduleId, "src/Target/");
                break;
            case Listing.WholeModule:
                await tree.ListModuleFilesAsync(moduleId);
                break;
            case Listing.Search:
                int releaseId;
                await using (var ctx = _db.NewContext())
                {
                    releaseId = await ctx.OeModules.AsNoTracking()
                        .Where(m => m.Id == moduleId).Select(m => m.ReleaseId).SingleAsync();
                }
                await tree.SearchTreeAsync(releaseId, "Needle");
                break;
        }
    }

    // ── Harness ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="work"/> against a context that records every command
    /// it issues, then replays each command that touched <c>oe_module_objects</c>
    /// under EXPLAIN ANALYZE and totals the rows the database actually visited.
    /// </summary>
    private async Task<(List<string> Sql, long ObjectRowsExamined)> MeasureAsync(
        Func<SourceViewerService, ExplorerTreeService, Task> work)
    {
        var recorder = new CommandRecorder();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_db.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(recorder)
            .Options;

        await using (var ctx = new AppDbContext(options, _db.OrgContext))
        {
            // One SourceVisibility for both, as the DI scope hands them.
            var visibility = new SourceVisibility(ctx, new ProjectAccess(ctx, _db.OrgContext));
            await work(
                new SourceViewerService(
                    ctx,
                    new ReferenceQueryService(ctx, new ProjectAccess(ctx, _db.OrgContext), _db.OrgContext, NullLogger<ReferenceQueryService>.Instance),
                    visibility),
                new ExplorerTreeService(ctx, visibility));
        }

        long examined = 0;
        foreach (var cmd in recorder.Commands.Where(c => c.Sql.Contains("oe_module_objects")))
        {
            examined += await ExplainRowsAsync(cmd);
        }
        return (recorder.Commands.Select(c => c.Sql).ToList(), examined);
    }

    private async Task<long> ExplainRowsAsync(RecordedCommand cmd)
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var explain = conn.CreateCommand();
        explain.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) " + cmd.Sql;
        foreach (var (name, value) in cmd.Parameters)
        {
            explain.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));
        }

        var json = (string?)await explain.ExecuteScalarAsync();
        return json is null ? 0 : SumActualRows(JsonDocument.Parse(json).RootElement);
    }

    /// <summary>
    /// Rows visited by every node of an EXPLAIN ANALYZE plan.
    ///
    /// "Actual Rows" alone is rows <em>emitted</em>, which is not the same
    /// thing: a node that reads a million rows and filters them down to ten
    /// reports ten. The rows it discarded show up separately, as the several
    /// "Rows Removed by …" counters, and those are exactly the work this guard
    /// exists to notice — a future rewrite that scans the whole object table
    /// but filters in SQL would otherwise report a handful of rows and sail
    /// through the budget. Both halves are counted.
    ///
    /// Every counter is per-loop, so each is multiplied by "Actual Loops": a
    /// nested loop probing an index ten times reports ten loops of one row, and
    /// the ten is the part that matters.
    /// </summary>
    private static long SumActualRows(JsonElement el)
    {
        long total = 0;
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) total += SumActualRows(item);
                break;

            case JsonValueKind.Object:
                var loops = el.TryGetProperty("Actual Loops", out var l) ? l.GetDouble() : 1d;
                foreach (var counter in RowCounters)
                {
                    if (el.TryGetProperty(counter, out var value))
                    {
                        total += (long)(value.GetDouble() * loops);
                    }
                }
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Name is "Actual Loops" || RowCounters.Contains(prop.Name)) continue;
                    total += SumActualRows(prop.Value);
                }
                break;
        }
        return total;
    }

    /// <summary>
    /// The EXPLAIN ANALYZE counters that together describe how many rows a node
    /// actually touched: the ones it passed on, plus the ones it read and threw
    /// away at each of the places Postgres can discard them.
    /// </summary>
    private static readonly string[] RowCounters =
    [
        "Actual Rows",
        "Rows Removed by Filter",
        "Rows Removed by Index Recheck",
        "Rows Removed by Join Filter",
    ];

    private sealed record RecordedCommand(string Sql, List<(string Name, object? Value)> Parameters);

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<RecordedCommand> Commands { get; } = [];

        private void Record(DbCommand command)
        {
            var parameters = command.Parameters
                .Cast<DbParameter>()
                .Select(p => (p.ParameterName, p.Value))
                .ToList();
            Commands.Add(new RecordedCommand(command.CommandText, parameters));
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }
    }

    // ── Fixture ────────────────────────────────────────────────────────

    /// <summary>
    /// One module holding a small target folder and a large remainder, which is
    /// the shape that separates "cost tracks the page" from "cost tracks the
    /// catalogue". Every file carries one object, as AL files do.
    /// </summary>
    private static async Task<long> SeedSkewedModuleAsync(TestDb db)
    {
        await using var ctx = db.NewContext();

        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "BC 26 CRONUS",
            Kind = "first_party",
            Status = "ready",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();

        var module = new OeModule
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = release.Id,
            AppId = Guid.NewGuid(),
            Name = "Base Application",
            Publisher = "CRONUS",
            Version = "26.0.0.0",
            CreatedAt = DateTime.UtcNow,
        };
        ctx.OeModules.Add(module);
        await ctx.SaveChangesAsync();

        // One shared content row: the store is deduplicated by hash, and the
        // text is irrelevant to what these tests measure.
        const string hash = "scaling-fixture-content-hash";
        ctx.OeFileContents.Add(new OeFileContent
        {
            ContentHash = hash,
            Content = "// fixture",
            ContentLength = 10,
            LineCount = 1,
        });

        var files = new List<OeModuleFile>(FilesInFolder + FilesElsewhere);
        for (var i = 0; i < FilesInFolder; i++)
        {
            files.Add(NewFile(module.Id, $"src/Target/Target{i}.al", hash));
        }
        for (var i = 0; i < FilesElsewhere; i++)
        {
            // Spread across folders so no single other folder is itself huge.
            files.Add(NewFile(module.Id, $"src/Bulk{i % 40}/Bulk{i}.al", hash));
        }
        ctx.OeModuleFiles.AddRange(files);
        await ctx.SaveChangesAsync();

        var objects = new List<OeModuleObject>(files.Count);
        foreach (var file in files)
        {
            var isTarget = file.Path.StartsWith("src/Target/", StringComparison.Ordinal);
            objects.Add(new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                Kind = "codeunit",
                Name = isTarget
                    ? $"Needle {file.Path[^6..^3]}"
                    : $"Haystack {file.Id}",
                SourceFileId = file.Id,
                LineNumber = 1,
            });
        }
        ctx.OeModuleObjects.AddRange(objects);
        await ctx.SaveChangesAsync();

        // The planner needs statistics to prefer the index over a seq scan;
        // without this the tables look empty and the measurement is meaningless.
        await ctx.Database.ExecuteSqlRawAsync(
            "ANALYZE oe_module_files, oe_module_objects, oe_modules, oe_releases");

        return module.Id;
    }

    private static OeModuleFile NewFile(long moduleId, string path, string hash) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        ModuleId = moduleId,
        Path = path,
        ContentHash = hash,
        LineCount = 1,
    };
}
