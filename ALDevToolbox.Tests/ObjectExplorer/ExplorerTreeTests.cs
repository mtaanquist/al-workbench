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
/// The Object Explorer's left-hand tree (PR 14b). Seeded from the real
/// <c>.app</c> fixtures rather than hand-written rows, because the shape the
/// tree walks — module-relative paths with a <c>src/</c> prefix, one object per
/// file, some files with no object at all — is exactly the shape a hand-rolled
/// fixture gets to choose, and a fixture that agrees with the assumption cannot
/// falsify it.
///
/// The property worth guarding hardest is the <em>bounded</em> one: expanding a
/// folder must return that folder's own children and nothing deeper. A Base
/// Application module runs to thousands of files, and the difference between
/// "the immediate children" and "every descendant" is invisible on a fixture
/// with twenty.
/// </summary>
public sealed class ExplorerTreeTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    private ReleaseImportService NewImporter(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext,
                new ALDevToolbox.Services.Translation.TranslationMemoryService(
                    ctx, _db.OrgContext,
                    NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
                _db.NewQuotaGuard(ctx), NullLogger<TranslationImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext)),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext));

    private ExplorerTreeService NewTree(Data.AppDbContext ctx) =>
        new(ctx, new SourceVisibility(ctx, new ProjectAccess(ctx, _db.OrgContext)));

    /// <summary>Two modules in one release, so the tree has siblings to close.</summary>
    private async Task<int> SeedAsync()
    {
        await using var ctx = _db.NewContext();
        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var summary = await NewImporter(ctx).ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[]
            {
                new AppFileUpload("Microsoft_DK_Core.app", s1, null),
                new AppFileUpload("Microsoft_OIOUBL.app", s2, null),
            }));
        return summary.ReleaseId;
    }

    private static async Task<(long FileId, string Path, long ModuleId)> AFileAsync(Data.AppDbContext ctx) =>
        await ctx.OeModuleFiles.AsNoTracking()
            .Where(f => f.Path.Contains("DKCoreEventSubscribers"))
            .Select(f => new ValueTuple<long, string, long>(f.Id, f.Path, f.ModuleId))
            .FirstAsync();

    // ── The opened branch ──────────────────────────────────────────────

    [Fact]
    public async Task The_tree_lists_every_module_in_the_release_and_opens_only_the_files_own()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (fileId, _, moduleId) = await AFileAsync(ctx);

        var tree = await NewTree(ctx).GetExplorerTreeAsync(fileId);

        var modules = tree.Where(n => n.Kind == "module").ToList();
        modules.Should().HaveCount(2, because: "the release holds DK Core and OIOUBL");
        modules.Should().OnlyContain(m => m.HasChildren);
        modules.Where(m => m.IsOpen).Should().ContainSingle()
            .Which.ModuleId.Should().Be(moduleId);
        modules.Select(m => m.Name).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Every_folder_on_the_way_to_the_open_file_is_open_and_the_file_is_active()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (fileId, path, _) = await AFileAsync(ctx);

        var tree = await NewTree(ctx).GetExplorerTreeAsync(fileId);

        // "src/Codeunits/DKCoreEventSubscribers.Codeunit.al" → src, Codeunits
        var folders = path.Split('/')[..^1];
        foreach (var (name, i) in folders.Select((n, i) => (n, i)))
        {
            tree.Should().ContainSingle(
                    n => n.Kind == "folder" && n.Name == name && n.IsOpen && n.Depth == i + 1,
                    because: $"'{name}' is on the path to the open file")
                .Which.Path.Should().EndWith("/", because: "a folder path is usable as a prefix");
        }

        var active = tree.Where(n => n.IsActive).ToList();
        active.Should().ContainSingle().Which.FileId.Should().Be(fileId);
        active[0].Depth.Should().Be(folders.Length + 1);
    }

    /// <summary>
    /// The list is flat but reads as a tree, so a folder's children have to sit
    /// immediately beneath it — not after its siblings. Verified by walking the
    /// rows and checking depth never jumps by more than one.
    /// </summary>
    [Fact]
    public async Task The_flat_list_reads_as_a_pre_order_walk()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (fileId, path, _) = await AFileAsync(ctx);

        var tree = await NewTree(ctx).GetExplorerTreeAsync(fileId);

        for (var i = 1; i < tree.Count; i++)
        {
            (tree[i].Depth - tree[i - 1].Depth).Should().BeLessThanOrEqualTo(1,
                because: $"row {i} ({tree[i].Name}) cannot be deeper than one below its predecessor");
        }

        // And the deepest row, the open file, follows its own folder directly.
        var fileIndex = tree.FindIndex(n => n.FileId == fileId);
        var parentName = path.Split('/')[^2];
        tree.Take(fileIndex).Last(n => n.Depth == tree[fileIndex].Depth - 1)
            .Name.Should().Be(parentName);
    }

    [Fact]
    public async Task A_file_that_does_not_exist_produces_no_tree()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();

        (await NewTree(ctx).GetExplorerTreeAsync(-1)).Should().BeEmpty();
    }

    // ── Lazy children ──────────────────────────────────────────────────

    /// <summary>
    /// The reason the tree is lazy at all. Expanding the module root must
    /// return <c>src</c> once, not every path that starts with it.
    /// </summary>
    [Fact]
    public async Task Expanding_a_folder_returns_its_own_children_and_nothing_deeper()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (_, _, moduleId) = await AFileAsync(ctx);
        var tree = NewTree(ctx);

        var root = await tree.GetTreeChildrenAsync(moduleId, "");
        root.Should().NotBeEmpty();
        root.Should().OnlyContain(n => n.Depth == 0, because: "the caller assigns depth");
        root.Where(n => n.Kind == "folder").Select(n => n.Name)
            .Should().OnlyHaveUniqueItems().And.Contain("src");
        root.Should().NotContain(n => n.Name.Contains('/'),
            because: "a child is one segment, never a path");

        var src = await tree.GetTreeChildrenAsync(moduleId, "src");
        src.Should().NotBeEmpty();
        src.Where(n => n.Kind == "folder").Should().OnlyContain(n => n.Path.StartsWith("src/"));
        src.Should().NotContain(n => n.Name == "src", because: "src is the parent, not its own child");

        var everyFile = await ctx.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == moduleId).CountAsync();
        src.Count.Should().BeLessThan(everyFile,
            because: "expanding one folder must not return the whole module");
    }

    [Fact]
    public async Task A_trailing_slash_on_the_prefix_makes_no_difference()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (_, _, moduleId) = await AFileAsync(ctx);
        var tree = NewTree(ctx);

        var withSlash = await tree.GetTreeChildrenAsync(moduleId, "src/");
        var without = await tree.GetTreeChildrenAsync(moduleId, "src");

        withSlash.Should().BeEquivalentTo(without);
    }

    [Fact]
    public async Task Folders_come_before_files_and_both_are_alphabetical()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (_, path, moduleId) = await AFileAsync(ctx);
        var parent = path[..(path.LastIndexOf('/') + 1)];

        var children = await NewTree(ctx).GetTreeChildrenAsync(moduleId, parent);

        var kinds = children.Select(c => c.Kind).ToList();
        kinds.LastIndexOf("folder").Should().BeLessThan(
            kinds.IndexOf("file") < 0 ? int.MaxValue : kinds.IndexOf("file") + 1);
        children.Where(c => c.Kind == "file").Select(c => c.Name)
            .Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_file_reads_as_its_object_and_keeps_its_file_name()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (fileId, path, moduleId) = await AFileAsync(ctx);
        var parent = path[..(path.LastIndexOf('/') + 1)];
        var expected = await ctx.OeModuleObjects.AsNoTracking()
            .Where(o => o.SourceFileId == fileId)
            .Select(o => new { o.Name, o.Kind, o.ObjectId })
            .FirstAsync();

        var row = (await NewTree(ctx).GetTreeChildrenAsync(moduleId, parent))
            .Single(c => c.FileId == fileId);

        row.Name.Should().Be(expected.Name);
        row.FileName.Should().Be(path[(path.LastIndexOf('/') + 1)..]);
        row.ObjectKind.Should().Be(expected.Kind);
        row.Badge.Should().Be(expected.ObjectId?.ToString());
        row.HasChildren.Should().BeFalse();
        row.Path.Should().Be(path);
    }

    [Fact]
    public async Task Every_file_in_the_module_is_reachable_exactly_once()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (_, _, moduleId) = await AFileAsync(ctx);
        var tree = NewTree(ctx);

        var seen = new List<long>();
        var queue = new Queue<string>([""]);
        while (queue.Count > 0)
        {
            foreach (var child in await tree.GetTreeChildrenAsync(moduleId, queue.Dequeue()))
            {
                if (child.Kind == "folder") queue.Enqueue(child.Path);
                else seen.Add(child.FileId!.Value);
            }
        }

        var all = await ctx.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == moduleId).Select(f => f.Id).ToListAsync();
        seen.Should().OnlyHaveUniqueItems();
        seen.Should().BeEquivalentTo(all,
            because: "walking the tree must reach every file the module holds, and no file twice");
    }

    /// <summary>
    /// A folder path is data, not a LIKE pattern. These two paths are written
    /// by hand rather than taken from a fixture on purpose: the bug needs a
    /// module holding two folders whose names differ only where a LIKE
    /// metacharacter sits, and no real <c>.app</c> we have does. The rows are
    /// added to a module the importer built, so everything around them is
    /// still production-shaped.
    /// </summary>
    [Theory]
    [InlineData("Mobile_WMS", "MobileXWMS")]
    [InlineData("VAT%Setup", "VATzSetup")]
    public async Task A_folder_name_holding_a_like_metacharacter_does_not_match_its_neighbour(
        string withMeta, string decoy)
    {
        await SeedAsync();
        long moduleId;
        await using (var ctx = _db.NewContext())
        {
            (_, _, moduleId) = await AFileAsync(ctx);
            await AddFileAsync(ctx, moduleId, $"src/{withMeta}/Own.Codeunit.al");
            await AddFileAsync(ctx, moduleId, $"src/{decoy}/Foreign.Codeunit.al");
        }

        await using var read = _db.NewContext();
        var children = await NewTree(read).GetTreeChildrenAsync(moduleId, $"src/{withMeta}/");

        children.Should().ContainSingle(because: "only this folder's own file belongs to it")
            .Which.FileName.Should().Be("Own.Codeunit.al");
    }

    /// <summary>
    /// Modules whose .app shipped without embedded source have no files. A
    /// caret on one opens, shows nothing, and latches itself as loaded, so it
    /// can never be tried again — an open, empty, unexplained node.
    /// </summary>
    [Fact]
    public async Task A_module_with_no_source_draws_no_caret()
    {
        var releaseId = await SeedAsync();
        long fileId;
        await using (var ctx = _db.NewContext())
        {
            (fileId, _, _) = await AFileAsync(ctx);
            ctx.OeModules.Add(new OeModule
            {
                OrganizationId = TestDb.DefaultOrgId,
                ReleaseId = releaseId,
                AppId = Guid.NewGuid(),
                Name = "Symbols Only",
                Publisher = "Microsoft",
                Version = "25.0.0.0",
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var tree = await NewTree(read).GetExplorerTreeAsync(fileId);

        tree.Should().ContainSingle(n => n.Name == "Symbols Only")
            .Which.HasChildren.Should().BeFalse();
        tree.Where(n => n.Kind == "module" && n.Name != "Symbols Only")
            .Should().OnlyContain(n => n.HasChildren);
    }

    /// <summary>
    /// The release page hides test, internal and language-pack apps by default
    /// (<see cref="ObjectExplorerService.ListModulesAsync"/>). A tree that
    /// shows them puts a different count four pixels from that page's, for the
    /// same release, in the same session.
    /// </summary>
    [Fact]
    public async Task The_tree_hides_the_same_apps_the_release_page_hides()
    {
        var releaseId = await SeedAsync();
        long fileId;
        await using (var ctx = _db.NewContext())
        {
            (fileId, _, _) = await AFileAsync(ctx);
            foreach (var (name, test, internalOnly, langPack) in new[]
                     {
                         ("DK Core Tests", true, false, false),
                         ("DK Core Internal", false, true, false),
                         ("DK Core da-DK", false, false, true),
                     })
            {
                ctx.OeModules.Add(new OeModule
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    ReleaseId = releaseId,
                    AppId = Guid.NewGuid(),
                    Name = name,
                    Publisher = "Microsoft",
                    Version = "25.0.0.0",
                    IsTest = test,
                    IsInternal = internalOnly,
                    IsLanguagePack = langPack,
                });
            }
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var tree = await NewTree(read).GetExplorerTreeAsync(fileId);

        tree.Should().NotContain(n => n.Name.EndsWith(" Tests"));
        tree.Should().NotContain(n => n.Name.EndsWith(" Internal"));
        tree.Should().NotContain(n => n.Name.EndsWith(" da-DK"));
        tree.Where(n => n.Kind == "module").Should().HaveCount(2);
    }

    /// <summary>
    /// ...but never the branch it exists to show. Opening a file inside a test
    /// app has to render that app, filter or no filter.
    /// </summary>
    [Fact]
    public async Task The_open_files_own_app_survives_the_filter()
    {
        await SeedAsync();
        long fileId;
        long moduleId;
        await using (var ctx = _db.NewContext())
        {
            (fileId, _, moduleId) = await AFileAsync(ctx);
            var module = await ctx.OeModules.SingleAsync(m => m.Id == moduleId);
            module.IsTest = true;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var tree = await NewTree(read).GetExplorerTreeAsync(fileId);

        tree.Should().ContainSingle(n => n.Kind == "module" && n.ModuleId == moduleId && n.IsOpen);
        tree.Should().ContainSingle(n => n.FileId == fileId && n.IsActive);
    }

    /// <summary>
    /// The row displays its object's name when it has one, so that is the name
    /// it has to sort under. Keying on the object name directly sorted a blank
    /// one under the empty string, so the row jumped to the top of the folder
    /// showing a file name that belonged further down.
    /// </summary>
    [Fact]
    public async Task A_file_whose_object_has_no_name_sorts_where_it_is_drawn()
    {
        await SeedAsync();
        long moduleId;
        await using (var ctx = _db.NewContext())
        {
            (_, _, moduleId) = await AFileAsync(ctx);
            var fileId = await AddFileAsync(ctx, moduleId, "src/Sorting/ZZZ.Codeunit.al");
            ctx.OeModuleObjects.Add(new OeModuleObject
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = moduleId,
                Kind = "codeunit",
                Name = "   ",
                SourceFileId = fileId,
                LineNumber = 1,
            });
            await AddFileAsync(ctx, moduleId, "src/Sorting/AAA.Codeunit.al");
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var children = await NewTree(read).GetTreeChildrenAsync(moduleId, "src/Sorting/");

        children.Select(c => c.Name).Should()
            .ContainInOrder("AAA.Codeunit.al", "ZZZ.Codeunit.al");
    }

    private static async Task<long> AddFileAsync(Data.AppDbContext ctx, long moduleId, string path)
    {
        var hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        ctx.OeFileContents.Add(new OeFileContent
        {
            ContentHash = hash,
            Content = "// " + path,
            ContentLength = path.Length + 3,
            LineCount = 1,
        });
        var file = new OeModuleFile
        {
            OrganizationId = TestDb.DefaultOrgId,
            ModuleId = moduleId,
            Path = path,
            ContentHash = hash,
            LineCount = 1,
        };
        ctx.OeModuleFiles.Add(file);
        await ctx.SaveChangesAsync();
        return file.Id;
    }

    /// <summary>
    /// The legacy C/AL ingest writes every object of a kind into one folder,
    /// so a real module can hold thousands of files in a single node. The tree
    /// caps the list and says what it left out rather than server-rendering
    /// thousands of rows into the page response in silence.
    /// </summary>
    [Fact]
    public async Task A_folder_larger_than_the_cap_says_what_it_left_out()
    {
        await SeedAsync();
        long moduleId;
        const int total = 405;
        await using (var ctx = _db.NewContext())
        {
            (_, _, moduleId) = await AFileAsync(ctx);
            for (var i = 0; i < total; i++)
            {
                await AddFileAsync(ctx, moduleId, $"CAL/Table/{i:D4} - Table.txt");
            }
        }

        await using var read = _db.NewContext();
        var children = await NewTree(read).GetTreeChildrenAsync(moduleId, "CAL/Table/");

        children.Count(c => c.Kind == "file").Should().Be(400);
        var overflow = children.Should().ContainSingle(c => c.Kind == "overflow").Which;
        overflow.Name.Should().Contain("5 more");
        overflow.FileId.Should().BeNull(because: "it is a label, not a destination");
        children[^1].Should().Be(overflow, because: "it belongs at the end of the list");
    }

    // ── Flat mode and search ───────────────────────────────────────────

    /// <summary>
    /// The ungrouped view answers a different question from the tree: not
    /// "where does this live" but "what is in here". One app's files by name,
    /// no folders and no other apps, with the open file still active.
    /// </summary>
    [Fact]
    public async Task Ungrouped_lists_one_apps_files_and_nothing_else()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (fileId, _, moduleId) = await AFileAsync(ctx);

        var tree = await NewTree(ctx).GetExplorerTreeAsync(
            fileId, ExplorerTreeService.TreeGrouping.None);

        tree.Should().NotContain(n => n.Kind == "folder", because: "ungrouped means no folders");
        tree.Should().NotContain(n => n.Kind == "module", because: "and no other apps");
        tree.Should().ContainSingle(n => n.IsActive).Which.FileId.Should().Be(fileId);

        var everyFile = await ctx.OeModuleFiles.AsNoTracking()
            .CountAsync(f => f.ModuleId == moduleId);
        tree.Count(n => n.Kind == "file").Should().Be(everyFile,
            because: "the whole app is the point of the ungrouped view");
        tree.Where(n => n.Kind == "file").Select(n => n.Name)
            .Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Grouping by object kind puts one section per AL kind above its files.
    /// The sections arrive with their children, so folding one never asks the
    /// server again; only the open file's section starts open.
    /// </summary>
    [Fact]
    public async Task Grouping_by_kind_sections_the_apps_files()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (fileId, _, moduleId) = await AFileAsync(ctx);

        var tree = await NewTree(ctx).GetExplorerTreeAsync(
            fileId, ExplorerTreeService.TreeGrouping.Kind);

        var sections = tree.Where(n => n.Kind == "section").ToList();
        sections.Should().NotBeEmpty();
        sections.Should().OnlyContain(sn => sn.HasChildren && sn.Depth == 0);
        // Only the section holding the open file starts open; the others are
        // folded, with their rows present but hidden.
        var openSection = sections.Should().ContainSingle(sn => sn.IsOpen).Which;
        foreach (var section in sections)
        {
            tree.Skip(tree.IndexOf(section) + 1).TakeWhile(n => n.Depth == 1)
                .Should().OnlyContain(n => n.IsHidden == !section.IsOpen,
                    because: $"rows under '{section.Name}' follow its fold");
        }
        tree.Skip(tree.IndexOf(openSection) + 1).TakeWhile(n => n.Depth == 1)
            .Should().Contain(n => n.IsActive, because: "the open section is the one holding the open file");
        sections.Select(sn => sn.Name).Should().Contain("Codeunits");
        tree.Where(n => n.Kind == "file").Should().OnlyContain(n => n.Depth == 1);
        tree.Should().ContainSingle(n => n.IsActive).Which.FileId.Should().Be(fileId);

        // Every file sits under a section, and each section's badge is its own count.
        foreach (var section in sections)
        {
            var index = tree.IndexOf(section);
            var under = tree.Skip(index + 1).TakeWhile(n => n.Depth == 1).Count();
            section.Badge.Should().Be(under.ToString("N0"),
                because: $"the '{section.Name}' badge counts the files drawn beneath it");
        }
        tree.Count(n => n.Kind == "file").Should()
            .Be(await ctx.OeModuleFiles.AsNoTracking().CountAsync(f => f.ModuleId == moduleId));
    }

    /// <summary>
    /// #1339: the kind grouping used to be built from the flat list, which is
    /// already cut to the alphabetically first few hundred files, so on a
    /// large app the badges counted a subset and late kinds vanished. Now each
    /// section counts every file of its kind and is capped on its own.
    /// </summary>
    [Fact]
    public async Task Grouping_by_kind_counts_every_file_and_caps_each_section_on_its_own()
    {
        await SeedAsync();
        long fileId, moduleId;
        const int extra = 405;
        await using (var ctx = _db.NewContext())
        {
            (fileId, _, moduleId) = await AFileAsync(ctx);
            // No object behind these, so they land in "Other files"; their
            // names sort ahead of every object name.
            for (var i = 0; i < extra; i++)
            {
                await AddFileAsync(ctx, moduleId, $"Extra/{i:D4}.txt");
            }
        }

        await using var read = _db.NewContext();
        var tree = await NewTree(read).ListModuleTreeAsync(
            moduleId, ExplorerTreeService.TreeGrouping.Kind, fileId);

        var sections = tree.Where(n => n.Kind == "section").ToList();
        sections.Select(n => n.Name).Should().Contain("Codeunits",
            because: "a kind that sorts after the cap is still drawn");

        var other = sections.Should().ContainSingle(n => n.Name == "Other files").Which;
        var objectFileIds = await read.OeModuleObjects.AsNoTracking()
            .Where(o => o.ModuleId == moduleId && o.SourceFileId != null)
            .Select(o => o.SourceFileId!.Value)
            .Distinct()
            .ToListAsync();
        var otherTotal = await read.OeModuleFiles.AsNoTracking()
            .CountAsync(f => f.ModuleId == moduleId && !objectFileIds.Contains(f.Id));
        other.Badge.Should().Be(otherTotal.ToString("N0"), because: "the badge counts every file of the kind");

        var under = tree.Skip(tree.IndexOf(other) + 1).TakeWhile(n => n.Depth == 1).ToList();
        under.Count(n => n.Kind == "file").Should().Be(100);
        under[^1].Kind.Should().Be("overflow", because: "the cut section says what it left out, at its own end");
        under[^1].Name.Should().Be($"{otherTotal - 100:N0} more files - type a name in the search box above",
            because: "the row names what it left out and where to find it");
        other.IsOpen.Should().BeFalse(because: "the open file is not in it");
        under.Should().OnlyContain(n => n.IsHidden, because: "a folded section's rows, overflow row included, start hidden");

        tree.Should().ContainSingle(n => n.IsActive).Which.FileId.Should().Be(fileId);
    }

    [Theory]
    [InlineData(null, ExplorerTreeService.TreeGrouping.Folder)]
    [InlineData("", ExplorerTreeService.TreeGrouping.Folder)]
    [InlineData("nonsense", ExplorerTreeService.TreeGrouping.Folder)]
    [InlineData("KIND", ExplorerTreeService.TreeGrouping.Kind)]
    [InlineData("none", ExplorerTreeService.TreeGrouping.None)]
    public void An_unknown_grouping_falls_back_to_folders(string? raw, string expected)
    {
        // The value arrives from a cookie and a query string, so it is not ours.
        ExplorerTreeService.TreeGrouping.Parse(raw).Should().Be(expected);
    }

    /// <summary>
    /// The search crosses apps, which is the question it exists to answer, so
    /// a hit is badged with the app it came from rather than an object id.
    /// </summary>
    [Fact]
    public async Task Search_crosses_every_app_in_the_release_and_names_the_app()
    {
        var releaseId = await SeedAsync();
        await using var ctx = _db.NewContext();

        var hits = await NewTree(ctx).SearchTreeAsync(releaseId, "subscriber");

        hits.Should().NotBeEmpty();
        hits.Should().OnlyContain(h => h.Kind == "file" || h.Kind == "overflow");
        hits.Where(h => h.Kind == "file").Should().OnlyContain(h => !string.IsNullOrEmpty(h.Badge),
            because: "which app a hit came from is what a name alone cannot tell you");
        hits.Select(h => h.Name).Should().Contain(n => n.Contains("Subscriber", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_matches_the_path_for_a_file_with_no_object()
    {
        var releaseId = await SeedAsync();
        long moduleId;
        await using (var ctx = _db.NewContext())
        {
            (_, _, moduleId) = await AFileAsync(ctx);
            await AddFileAsync(ctx, moduleId, "Permissions/DkCoreAdmin.PermissionSet.xml");
        }

        await using var read = _db.NewContext();
        var hits = await NewTree(read).SearchTreeAsync(releaseId, "DkCoreAdmin");

        hits.Should().ContainSingle(h => h.Name.Contains("DkCoreAdmin"));
    }

    /// <summary>
    /// The object-name half and the path half of the search are two separate
    /// queries now (the single filtered pass had to compute every file's object
    /// name before it could discard anything, which is what made the box slow
    /// on a large catalogue). A file whose object name *and* path both match
    /// lands in both halves, so the merge has to dedupe — otherwise the reader
    /// sees the same file twice and the result count is a lie.
    /// </summary>
    [Fact]
    public async Task Search_lists_a_file_once_when_both_its_name_and_its_path_match()
    {
        var releaseId = await SeedAsync();
        await using var ctx = _db.NewContext();

        // "Subscriber" appears in the object name *and* in the file path of
        // the DK Core event-subscriber codeunit.
        var hits = await NewTree(ctx).SearchTreeAsync(releaseId, "Subscriber");

        var files = hits.Where(h => h.Kind == "file").ToList();
        files.Should().NotBeEmpty();
        files.Select(h => h.FileId).Should().OnlyHaveUniqueItems(
            because: "a file matching on both name and path is still one file");
    }

    /// <summary>
    /// A file bundling several objects reads as the first one declared in it.
    /// That pick used to be a database-side ORDER BY inside a correlated
    /// subquery; it is now a first-wins walk over a keyed lookup, so the
    /// ordering contract is worth pinning where it can actually break.
    /// </summary>
    [Fact]
    public async Task A_file_holding_several_objects_reads_as_the_first_one_declared()
    {
        await SeedAsync();
        long moduleId, fileId;
        await using (var ctx = _db.NewContext())
        {
            (_, _, moduleId) = await AFileAsync(ctx);
            fileId = await AddFileAsync(ctx, moduleId, "src/Bundled/Several.al");

            // Deliberately added out of line order, so a lookup that keeps
            // whichever row the database happened to hand back first fails.
            ctx.OeModuleObjects.AddRange(
                NewObject(moduleId, fileId, "Second Object", lineNumber: 40),
                NewObject(moduleId, fileId, "First Object", lineNumber: 10),
                NewObject(moduleId, fileId, "Third Object", lineNumber: 90));
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var children = await NewTree(read).GetTreeChildrenAsync(moduleId, "src/Bundled/");

        children.Should().ContainSingle(n => n.FileId == fileId)
            .Which.Name.Should().Be("First Object",
                because: "the lowest line number is the object the file leads with");
    }

    private static OeModuleObject NewObject(long moduleId, long fileId, string name, int lineNumber) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        ModuleId = moduleId,
        Kind = "codeunit",
        Name = name,
        SourceFileId = fileId,
        LineNumber = lineNumber,
    };

    /// <summary>
    /// The search caps each half before merging them, so the object-name half
    /// has to reduce to distinct <em>files</em> before it takes the cap. Taking
    /// the cap over object rows first spends the budget on duplicates: a
    /// release whose files each declare two matching objects filled only half a
    /// page, and — because the overflow row keys off the merged count — the
    /// listing then presented that half page as the complete answer, which is
    /// the worse half of the bug.
    /// </summary>
    [Fact]
    public async Task Search_fills_a_full_page_when_files_hold_several_matching_objects()
    {
        const int MaxSearchResults = 200;   // ExplorerTreeService's cap.
        const int Files = MaxSearchResults + 100;

        var releaseId = await SeedAsync();
        long moduleId;
        await using (var ctx = _db.NewContext())
        {
            (_, _, moduleId) = await AFileAsync(ctx);

            var hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            ctx.OeFileContents.Add(new OeFileContent
            {
                ContentHash = hash,
                Content = "// bundled",
                ContentLength = 11,
                LineCount = 1,
            });

            var files = new List<OeModuleFile>(Files);
            for (var i = 0; i < Files; i++)
            {
                files.Add(new OeModuleFile
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    ModuleId = moduleId,
                    // The needle must NOT appear in the path: the search's path
                    // half would then match these files on its own and mask
                    // whatever the object-name half got wrong.
                    Path = $"src/Bundled/Bundled{i}.al",
                    ContentHash = hash,
                    LineCount = 1,
                });
            }
            ctx.OeModuleFiles.AddRange(files);
            await ctx.SaveChangesAsync();

            // Two matching objects per file - the case that made the cap
            // return half as many files as it was asked for.
            foreach (var file in files)
            {
                ctx.OeModuleObjects.AddRange(
                    // Padded id first so a file's two objects sort next to each
                    // other. Names that group all the Alphas before all the
                    // Betas would let a cap taken over object rows still land
                    // on a full page of distinct files and hide the bug.
                    NewObject(moduleId, file.Id, $"Needle {file.Id:D6} Alpha", lineNumber: 10),
                    NewObject(moduleId, file.Id, $"Needle {file.Id:D6} Beta", lineNumber: 20));
            }
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var hits = await NewTree(read).SearchTreeAsync(releaseId, "Needle");

        hits.Count(h => h.Kind == "file").Should().Be(MaxSearchResults,
            because: "the cap counts files the reader can open, not object rows behind them");
        hits.Select(h => h.FileId).Where(id => id != null).Should().OnlyHaveUniqueItems();
        hits.Should().ContainSingle(h => h.Kind == "overflow",
            because: $"{Files} files matched and only {MaxSearchResults} are listed - saying so "
                   + "is what stops a truncated page reading as the whole answer");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    public async Task Search_says_nothing_until_it_has_something_to_go_on(string query)
    {
        var releaseId = await SeedAsync();
        await using var ctx = _db.NewContext();

        (await NewTree(ctx).SearchTreeAsync(releaseId, query)).Should().BeEmpty(
            because: "one character across a whole release is every file, which is not an answer");
    }

    [Fact]
    public async Task Search_is_case_insensitive()
    {
        var releaseId = await SeedAsync();
        await using var ctx = _db.NewContext();
        var tree = NewTree(ctx);

        var lower = await tree.SearchTreeAsync(releaseId, "subscriber");
        var upper = await tree.SearchTreeAsync(releaseId, "SUBSCRIBER");

        upper.Select(h => h.FileId).Should().BeEquivalentTo(lower.Select(h => h.FileId));
    }

    // ── ListModuleTreeAsync (#1273) ────────────────────────────────────

    [Fact]
    public async Task Module_tree_by_folder_lists_the_apps_files_and_marks_the_active_one()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (fileId, _, moduleId) = await AFileAsync(ctx);
        var fileCount = await ctx.OeModuleFiles.CountAsync(f => f.ModuleId == moduleId);

        var nodes = await NewTree(ctx).ListModuleTreeAsync(moduleId, ExplorerTreeService.TreeGrouping.Folder, fileId);

        nodes.Where(n => n.Kind == "file").Should().HaveCount(fileCount);
        nodes.Should().ContainSingle(n => n.IsActive).Which.FileId.Should().Be(fileId);
    }

    [Fact]
    public async Task Module_tree_by_kind_puts_each_file_under_its_kinds_section_in_reading_order()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (fileId, _, moduleId) = await AFileAsync(ctx);
        var fileCount = await ctx.OeModuleFiles.CountAsync(f => f.ModuleId == moduleId);

        var nodes = await NewTree(ctx).ListModuleTreeAsync(moduleId, ExplorerTreeService.TreeGrouping.Kind, fileId);

        var sections = nodes.Where(n => n.Kind == "section").ToList();
        sections.Should().NotBeEmpty();
        sections.Should().OnlyContain(s => s.Depth == 0);
        nodes.Where(n => n.Kind == "file").Should().HaveCount(fileCount)
            .And.OnlyContain(n => n.Depth == 1);
        nodes.First().Kind.Should().Be("section", because: "every file sits under a heading");

        // Each section's badge counts the files listed under it.
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Kind != "section") continue;
            var under = nodes.Skip(i + 1).TakeWhile(n => n.Kind == "file").Count();
            nodes[i].Badge.Should().Be(under.ToString("N0"));
        }

        // Codeunits come before "Other files", whatever the alphabet says.
        var titles = sections.Select(s => s.Name).ToList();
        if (titles.Contains("Codeunits") && titles.Contains("Other files"))
        {
            titles.IndexOf("Codeunits").Should().BeLessThan(titles.IndexOf("Other files"));
        }
        nodes.Should().ContainSingle(n => n.IsActive).Which.FileId.Should().Be(fileId);
    }

    [Fact]
    public async Task Module_tree_with_an_unknown_grouping_falls_back_to_folders()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (_, _, moduleId) = await AFileAsync(ctx);
        var tree = NewTree(ctx);

        var unknown = await tree.ListModuleTreeAsync(moduleId, "made-up");
        var folder = await tree.ListModuleTreeAsync(moduleId, ExplorerTreeService.TreeGrouping.Folder);

        unknown.Select(n => n.FileId).Should().Equal(folder.Select(n => n.FileId));
        unknown.Should().NotContain(n => n.Kind == "section");
    }

    [Fact]
    public async Task Module_tree_of_an_unknown_module_is_empty()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();

        (await NewTree(ctx).ListModuleTreeAsync(long.MaxValue, ExplorerTreeService.TreeGrouping.Kind))
            .Should().BeEmpty();
    }

    /// <summary>
    /// "_" and "%" are LIKE wildcards; unescaped, a search for "__" or "%%"
    /// matched every file in the release (#1262).
    /// </summary>
    [Theory]
    [InlineData("__", "src/Odd/My__Helper.Codeunit.al")]
    [InlineData("%%", "src/Odd/Rate%%Calc.Codeunit.al")]
    public async Task Search_treats_wildcard_characters_literally(string query, string expectedPath)
    {
        var releaseId = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            var (_, _, moduleId) = await AFileAsync(ctx);
            await AddFileAsync(ctx, moduleId, "src/Odd/My__Helper.Codeunit.al");
            await AddFileAsync(ctx, moduleId, "src/Odd/Rate%%Calc.Codeunit.al");
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var hits = await NewTree(read).SearchTreeAsync(releaseId, query);

        hits.Should().ContainSingle(h => h.Kind == "file")
            .Which.Path.Should().Be(expectedPath);
    }

    /// <summary>
    /// A search box is reachable by anyone signed in, and the release id comes
    /// off the URL. The query filter is the only thing scoping it.
    /// </summary>
    [Fact]
    public async Task Search_cannot_reach_another_orgs_release()
    {
        var releaseId = await SeedAsync();

        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        try
        {
            await using var other = _db.NewContext();
            (await NewTree(other).SearchTreeAsync(releaseId, "subscriber")).Should().BeEmpty();
        }
        finally
        {
            _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        }
    }

    [Fact]
    public async Task An_unknown_module_yields_nothing_rather_than_throwing()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();

        (await NewTree(ctx).GetTreeChildrenAsync(-1, "")).Should().BeEmpty();
    }

    /// <summary>
    /// The tree endpoint takes a module id straight off the URL, so the query
    /// filter is the only thing standing between one org and another's source
    /// layout. Same guard the rest of the viewer's reads get.
    /// </summary>
    [Fact]
    public async Task Another_orgs_module_is_invisible_even_by_id()
    {
        await SeedAsync();
        long moduleId;
        long fileId;
        await using (var ctx = _db.NewContext())
        {
            (fileId, _, moduleId) = await AFileAsync(ctx);
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        try
        {
            await using var other = _db.NewContext();
            (await NewTree(other).GetTreeChildrenAsync(moduleId, "")).Should().BeEmpty();
            (await NewTree(other).GetExplorerTreeAsync(fileId)).Should().BeEmpty();
        }
        finally
        {
            _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        }
    }

    [Fact]
    public async Task The_object_count_is_the_modules_own()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (_, _, moduleId) = await AFileAsync(ctx);

        var expected = await ctx.OeModuleObjects.AsNoTracking()
            .CountAsync(o => o.ModuleId == moduleId);

        (await NewTree(ctx).CountModuleObjectsAsync(moduleId)).Should().Be(expected).And.BePositive();
    }
}
