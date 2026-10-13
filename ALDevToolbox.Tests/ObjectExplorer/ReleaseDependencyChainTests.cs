using System.Text.Json;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The release chain across a dependency link (#901, Part 4): a pipeline build's
/// project Release links the vendor Releases it resolved symbols from, and the
/// chain walk sees them one step from the seed - after the release's own
/// objects, before its Microsoft parent's. The three Releases are built from
/// synthetic <c>.app</c>s run through the real importer, so the rows are the
/// ones production writes: a Microsoft parent, a symbols-only vendor package
/// parented onto it, and a project with source on top.
/// </summary>
public sealed class ReleaseDependencyChainTests : IDisposable
{
    private const string MicrosoftAppId = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string VendorAppId = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string ProjectAppId = "cccccccc-0000-0000-0000-000000000003";
    private const string ForeignAppId = "dddddddd-0000-0000-0000-000000000004";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    // ── Symbols-only packages ─────────────────────────────────────────

    [Fact]
    public async Task A_symbols_only_package_lands_its_modules_and_objects_with_no_file_rows()
    {
        var vendorId = await ImportAsync("Vendor Core 29.0.0.1 (symbols)", "third_party", parentId: null, VendorApp());

        await using var read = _db.NewContext();
        var modules = await read.OeModules.AsNoTracking().Where(m => m.ReleaseId == vendorId).ToListAsync();
        modules.Should().ContainSingle().Which.Name.Should().Be("Vendor Core");
        var objects = await read.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == vendorId).Select(o => o.Name).ToListAsync();
        objects.Should().BeEquivalentTo(["Vendor Mgt", "Shared Name", "Dup Name"]);
        (await read.OeModuleFiles.AsNoTracking().CountAsync(f => f.Module!.ReleaseId == vendorId)).Should().Be(0);
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == vendorId);
        release.Status.Should().Be("ready");
        release.SourceFileCount.Should().Be(0);
    }

    // ── Resolution across the link ────────────────────────────────────

    [Fact]
    public async Task A_project_release_resolves_an_object_that_lives_in_a_linked_vendor_release()
    {
        var (projectId, _, _) = await SeedChainAsync(link: true);

        await using var read = _db.NewContext();
        var hit = await ChainObjectResolution.ResolveObjectAsync(read, projectId, "Vendor Mgt", "codeunit", null, CancellationToken.None);
        hit.Should().NotBeNull();
        hit!.ModuleName.Should().Be("Vendor Core");
        (await ChainObjectResolution.ResolveMemberSymbolIdAsync(read, projectId, "Vendor Mgt", "DoWork", CancellationToken.None))
            .Should().NotBeNull("the vendor's procedure is a navigable target from our code");
    }

    [Fact]
    public async Task Without_the_link_the_vendor_release_is_a_sibling_and_stays_invisible()
    {
        var (projectId, _, _) = await SeedChainAsync(link: false);

        await using var read = _db.NewContext();
        (await ChainObjectResolution.ResolveObjectAsync(read, projectId, "Vendor Mgt", "codeunit", null, CancellationToken.None))
            .Should().BeNull();
        // And the ingest could not resolve our call into it, so there is no call site to find.
        var calls = await NewReferences(read).FindReferencesAsync(projectId,
            new FindReferencesQuery(Guid.Parse(VendorAppId), "codeunit", 70000, "Vendor Mgt", TargetMemberName: "DoWork"));
        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_projects_own_object_shadows_a_vendor_object_of_the_same_name()
    {
        var (projectId, _, _) = await SeedChainAsync(link: true);

        await using var read = _db.NewContext();
        var hit = await ChainObjectResolution.ResolveObjectAsync(read, projectId, "Dup Name", "codeunit", null, CancellationToken.None);
        hit!.ModuleName.Should().Be("CRONUS Extension", "the release itself is depth 0 and its dependency links depth 1");
    }

    [Fact]
    public async Task A_vendor_object_beats_a_Microsoft_object_of_the_same_name()
    {
        var (projectId, _, _) = await SeedChainAsync(link: true);

        await using var read = _db.NewContext();
        var hit = await ChainObjectResolution.ResolveObjectAsync(read, projectId, "Shared Name", "codeunit", null, CancellationToken.None);
        hit!.ModuleName.Should().Be("Vendor Core", "a dependency link sits between the release and its Microsoft parent");
        // Microsoft's own objects are still there through the ordinary parent.
        (await ChainObjectResolution.ResolveObjectAsync(read, projectId, "Customer", "table", null, CancellationToken.None))!
            .ModuleName.Should().Be("CRONUS Base");
    }

    [Fact]
    public async Task Find_references_on_a_vendor_procedure_finds_the_call_site_in_our_code()
    {
        var (projectId, _, _) = await SeedChainAsync(link: true);

        await using var read = _db.NewContext();
        var matches = await NewReferences(read).FindReferencesAsync(projectId,
            new FindReferencesQuery(Guid.Parse(VendorAppId), "codeunit", 70000, "Vendor Mgt", TargetMemberName: "DoWork"));

        matches.Should().Contain(m => m.SourceObjectName == "CRONUS Caller" && m.Category == "call" && m.MemberName == "DoWork",
            "the call resolves at ingest because the link puts the vendor in the chain");
    }

    [Fact]
    public async Task The_release_page_lists_the_linked_vendor_releases()
    {
        var (projectId, vendorId, _) = await SeedChainAsync(link: true);

        await using var read = _db.NewContext();
        var service = new ObjectExplorerService(read, NewReferences(read), new ProjectAccess(read, _db.OrgContext),
            NullLogger<ObjectExplorerService>.Instance);
        var rows = await service.GetDependencyReleasesAsync(projectId);
        rows.Should().ContainSingle().Which.Should().Be(
            new ReleaseDependencyRow(vendorId, "Vendor Core 29.0.0.1 (symbols)", "Vendor Software", "ready", "Vendor Core", "29.0.0.1"));
    }

    // ── Tenant fence ──────────────────────────────────────────────────

    [Fact]
    public async Task A_link_to_another_organisations_release_is_never_followed()
    {
        var (projectId, _, _) = await SeedChainAsync(link: true);

        // Another tenant's release, imported under its own org.
        int foreignId;
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        try
        {
            foreignId = await ImportAsync("Foreign Core", "third_party", parentId: null,
                SyntheticApp.Build(ForeignAppId, "Foreign Core", "Foreign", "1.0.0.0",
                    symbolReferenceJson: Symbols(("Codeunits", 71000, "Foreign Mgt", null))));
        }
        finally
        {
            _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        }

        // A link row the build would never write, inserted directly.
        await using (var seed = _db.NewContext())
        {
            seed.OeReleaseDependencies.Add(new OeReleaseDependency
            {
                OrganizationId = TestDb.DefaultOrgId,
                ReleaseId = projectId,
                DependencyReleaseId = foreignId,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await ChainObjectResolution.ResolveObjectAsync(read, projectId, "Foreign Mgt", "codeunit", null, CancellationToken.None))
            .Should().BeNull("the chain follows a link only to a release in the seed's own organisation");
        // The same-org link beside it still works.
        (await ChainObjectResolution.ResolveObjectAsync(read, projectId, "Vendor Mgt", "codeunit", null, CancellationToken.None))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task A_parent_pointer_to_another_organisations_release_is_never_followed()
    {
        // Another tenant's release, imported under its own org.
        int foreignId;
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        try
        {
            foreignId = await ImportAsync("Foreign Core", "first_party", parentId: null,
                SyntheticApp.Build(ForeignAppId, "Foreign Core", "Foreign", "1.0.0.0",
                    symbolReferenceJson: Symbols(("Codeunits", 71000, "Foreign Mgt", null))));
        }
        finally
        {
            _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        }

        var ownId = await ImportAsync("Vendor Core 29.0.0.1 (symbols)", "third_party", parentId: null, VendorApp());

        // A parent pointer the import would refuse, written directly.
        await using (var seed = _db.NewContext())
        {
            var own = await seed.OeReleases.SingleAsync(r => r.Id == ownId);
            own.ParentReleaseId = foreignId;
            await seed.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await ChainObjectResolution.ResolveObjectAsync(read, ownId, "Foreign Mgt", "codeunit", null, CancellationToken.None))
            .Should().BeNull("the chain follows a parent only to a release in the seed's own organisation");
        // The release's own objects still resolve.
        (await ChainObjectResolution.ResolveObjectAsync(read, ownId, "Vendor Mgt", "codeunit", null, CancellationToken.None))
            .Should().NotBeNull();
    }

    // ── Harness ───────────────────────────────────────────────────────

    /// <summary>
    /// Microsoft parent, a vendor release on it, and a project release on the same
    /// parent that optionally links the vendor. The link is written before the
    /// project's ingest, as the build does, so the ingest's call-site pass sees it.
    /// </summary>
    private async Task<(int ProjectId, int VendorId, int MicrosoftId)> SeedChainAsync(bool link)
    {
        var microsoftId = await ImportAsync("Business Central 29.0 (W1)", "first_party", parentId: null,
            SyntheticApp.Build(MicrosoftAppId, "CRONUS Base", "Microsoft", "29.0.0.0",
                symbolReferenceJson: Symbols(("Codeunits", 1, "Shared Name", null), ("Tables", 18, "Customer", null))));
        var vendorId = await ImportAsync("Vendor Core 29.0.0.1 (symbols)", "third_party", microsoftId, VendorApp(), "Vendor Software");

        await using var ctx = _db.NewContext();
        var importer = NewImporter(ctx);
        var projectId = await importer.BeginReleaseAsync(new ReleaseImportMetadata(
            "CRONUS on BC 29.0", OeRelease.ProjectBuildKind, microsoftId, null));
        if (link)
        {
            ctx.OeReleaseDependencies.Add(new OeReleaseDependency
            {
                OrganizationId = TestDb.DefaultOrgId,
                ReleaseId = projectId,
                DependencyReleaseId = vendorId,
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        using var app = new MemoryStream(ProjectApp());
        await importer.ProcessReleaseAsync(projectId, [new AppFileUpload("CRONUS_Extension_1.0.0.0.app", app, null)]);
        return (projectId, vendorId, microsoftId);
    }

    private async Task<int> ImportAsync(string label, string kind, int? parentId, byte[] app, string? publisher = null)
    {
        await using var ctx = _db.NewContext();
        using var stream = new MemoryStream(app);
        var summary = await NewImporter(ctx).ImportReleaseAsync(new ReleaseImportRequest(
            label, kind, parentId, null, [new AppFileUpload("app.app", stream, null)], Publisher: publisher));
        return summary.ReleaseId;
    }

    /// <summary>The vendor's symbols package: objects and procedure signatures, no <c>src/</c>.</summary>
    private static byte[] VendorApp() =>
        SyntheticApp.Build(VendorAppId, "Vendor Core", "Vendor Software", "29.0.0.1",
            symbolReferenceJson: Symbols(
                ("Codeunits", 70000, "Vendor Mgt", "DoWork"),
                ("Codeunits", 70001, "Shared Name", null),
                ("Codeunits", 70002, "Dup Name", null)));

    /// <summary>Our extension: a codeunit whose source calls into the vendor, and one that shares a vendor object's name.</summary>
    private static byte[] ProjectApp()
    {
        var symbols = JsonSerializer.Serialize(new
        {
            RuntimeVersion = "14.0",
            Codeunits = new object[]
            {
                new
                {
                    Id = 50200,
                    Name = "CRONUS Caller",
                    ReferenceSourceFileName = "src/Caller.Codeunit.al",
                    Methods = new object[] { new { Name = "Run", Parameters = Array.Empty<object>() } },
                    Variables = new object[]
                    {
                        new
                        {
                            Name = "VendorMgt",
                            TypeDefinition = new { Name = "Codeunit", Subtype = new { Name = "Vendor Mgt", Id = 70000, ModuleId = VendorAppId } },
                        },
                    },
                },
                new { Id = 50201, Name = "Dup Name", ReferenceSourceFileName = "src/DupName.Codeunit.al" },
            },
        });
        var source = new Dictionary<string, string>
        {
            ["src/Caller.Codeunit.al"] = """
                codeunit 50200 "CRONUS Caller"
                {
                    var
                        VendorMgt: Codeunit "Vendor Mgt";

                    procedure Run()
                    begin
                        VendorMgt.DoWork();
                    end;
                }
                """,
            ["src/DupName.Codeunit.al"] = """
                codeunit 50201 "Dup Name"
                {
                }
                """,
        };
        return SyntheticApp.Build(ProjectAppId, "CRONUS Extension", "CRONUS", "1.0.0.0",
            dependencies: [(VendorAppId, "Vendor Core", "29.0.0.1"), (MicrosoftAppId, "CRONUS Base", "29.0.0.0")],
            symbolReferenceJson: symbols, source: source);
    }

    /// <summary>A root-level (pre-namespace) symbol tree: each object in its collection, optionally with one parameterless procedure.</summary>
    internal static string Symbols(params (string Collection, int Id, string Name, string? Procedure)[] objects)
    {
        var root = new Dictionary<string, object> { ["RuntimeVersion"] = "14.0" };
        foreach (var group in objects.GroupBy(o => o.Collection))
        {
            root[group.Key] = group.Select(o => o.Procedure is null
                ? (object)new { o.Id, o.Name }
                : new { o.Id, o.Name, Methods = new[] { new { Name = o.Procedure, Parameters = Array.Empty<object>() } } })
                .ToList();
        }
        return JsonSerializer.Serialize(root);
    }

    private ReleaseImportService NewImporter(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext,
                new ALDevToolbox.Services.Translation.TranslationMemoryService(ctx, _db.OrgContext,
                    NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
                NullLogger<TranslationImportService>.Instance),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance);

    private ReferenceQueryService NewReferences(Data.AppDbContext ctx) =>
        new(ctx, new ProjectAccess(ctx, _db.OrgContext), _db.OrgContext, NullLogger<ReferenceQueryService>.Instance);
}
