using System.IO.Compression;
using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Templates;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Generation;

/// <summary>
/// Standalone New Extension flow through
/// <see cref="GenerationService.GenerateExtensionAsync"/>. Pins the split that
/// issue #520 asked for: a spaced extension name is kept verbatim in
/// <c>app.json</c>'s <c>name</c>, while the folder name strips the spaces
/// (via <see cref="ALDevToolbox.Services.Generation.GenerationNaming.StripWhitespace"/>).
/// </summary>
public sealed class StandaloneExtensionGenerationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Spaced_extension_name_keeps_spaces_in_app_json_but_strips_them_from_the_folder()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());

        using var zip = await GenerateExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"));

        // Folder name strips spaces: the ZIP roots the extension at
        // "MyCustomFeature/…", not "My Custom Feature/…".
        var appJsonEntry = zip.GetEntry("MyCustomFeature/app.json");
        appJsonEntry.Should().NotBeNull("the folder name should strip spaces from the extension name");
        zip.GetEntry("My Custom Feature/app.json").Should().BeNull(
            "the spaced name must not leak into the on-disk folder path");

        // Display name keeps the spaces: app.json "name" is the raw input.
        var appJson = JsonDocument.Parse(ReadEntry(appJsonEntry!));
        appJson.RootElement.GetProperty("name").GetString().Should().Be("My Custom Feature",
            "app.json is where spaces are wanted (issue #520)");
    }

    [Fact]
    public async Task Latest_application_version_resolves_to_the_newest_catalogue_entry()
    {
        // The web forms resolve "Latest" before building a plan; an MCP caller
        // can pass it straight through, so the generator resolves it too.
        await SeedTemplateAsync(TemplateBuilder.Default());
        await SeedApplicationVersionAsync("28.2.0.0", "16.0");

        using var zip = await GenerateExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature") with
            {
                ApplicationVersion = ApplicationVersionService.LatestSentinel,
                RuntimeVersion = ApplicationVersionService.LatestSentinel,
            });

        using var appJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("MyCustomFeature/app.json")!));
        appJson.RootElement.GetProperty("application").GetString().Should().Be("28.2.0.0");
        appJson.RootElement.GetProperty("runtime").GetString().Should().Be("16.0");
    }

    [Fact]
    public async Task Latest_application_version_is_refused_when_the_catalogue_is_empty()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());
        await using (var ctx = _db.NewContext())
        {
            (await ctx.ApplicationVersions.AnyAsync()).Should().BeFalse("the fixture starts with an empty catalogue");
        }

        var plan = PlanBuilder.ExtensionPlan() with { ApplicationVersion = ApplicationVersionService.LatestSentinel };
        var act = () => GenerateExtensionAsync(plan);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("ApplicationVersion");
    }

    [Fact]
    public async Task Workspace_root_org_files_land_at_the_standalone_extension_folder_root()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());

        using var zip = await GenerateExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature", publisher: "CRONUS"));

        // Issue #522: a standalone extension folder *is* the root of what the
        // user unzips, so the workspace-root-scoped organisation files the
        // template opts into ship there — not just the per-extension ones.
        zip.GetEntry("MyCustomFeature/.gitignore").Should().NotBeNull(
            "the template opts into the .gitignore organisation file");
        zip.GetEntry("MyCustomFeature/README.md").Should().NotBeNull();
        zip.GetEntry("MyCustomFeature/.assets/rulesets/Company.ruleset.json").Should().NotBeNull(
            "nested workspace-root paths keep their folder structure");
        // ...alongside the per-extension-scoped ones, which already worked.
        zip.GetEntry("MyCustomFeature/app.json").Should().NotBeNull();

        // The mustache context for those root files is the standalone plan:
        // {{workspace_name}} resolves to the extension's display name.
        ReadEntry(zip.GetEntry("MyCustomFeature/README.md")!)
            .Should().Contain("My Custom Feature");
    }

    [Fact]
    public async Task Sibling_workspace_file_keeps_the_root_entry_last_and_excludes_every_extension()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());

        var archive = await NewService().GenerateExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"),
            new SiblingWorkspaceContext("CRONUS Customer", Array.Empty<string>(), new[] { "Core" }));
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: false);

        using var doc = JsonDocument.Parse(ReadEntry(zip.GetEntry("CRONUSCustomer.code-workspace")!));
        doc.RootElement.GetProperty("folders").EnumerateArray()
            .Select(f => f.GetProperty("path").GetString())
            .Should().Equal("Core", "MyCustomFeature", ".");
        doc.RootElement.GetProperty("settings").GetProperty("files.exclude").EnumerateObject()
            .Select(p => p.Name)
            .Should().Equal("Core", "MyCustomFeature");
    }

    [Fact]
    public async Task Sibling_folder_with_glob_characters_is_listed_but_not_excluded()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());

        // Folder names come back from the workspace's saved settings, which a
        // person can edit; as an exclude pattern "*" would hide everything.
        var archive = await NewService().GenerateExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"),
            new SiblingWorkspaceContext("CRONUS Customer", Array.Empty<string>(), new[] { "Core", "*" }));
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: false);

        using var doc = JsonDocument.Parse(ReadEntry(zip.GetEntry("CRONUSCustomer.code-workspace")!));
        doc.RootElement.GetProperty("folders").EnumerateArray()
            .Select(f => f.GetProperty("path").GetString())
            .Should().Equal("Core", "*", "MyCustomFeature", ".");
        doc.RootElement.GetProperty("settings").GetProperty("files.exclude").EnumerateObject()
            .Select(p => p.Name)
            .Should().Equal("Core", "MyCustomFeature");
    }

    [Fact]
    public async Task Sibling_extension_leaves_the_workspace_root_org_files_to_the_existing_workspace()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());

        var archive = await NewService().GenerateExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"),
            new SiblingWorkspaceContext("CRONUS Customer", Array.Empty<string>(), new[] { "Core" }));
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: false);

        // The workspace this folder is being dropped into already carries
        // these at its own root; a nested second copy would be noise.
        zip.GetEntry("MyCustomFeature/.gitignore").Should().BeNull();
        zip.GetEntry("MyCustomFeature/README.md").Should().BeNull();
        // The rewritten workspace file is still the point of sibling mode.
        zip.GetEntry("CRONUSCustomer.code-workspace").Should().NotBeNull();
    }

    [Fact]
    public async Task Root_folders_land_at_the_standalone_extension_folder_root()
    {
        await SeedTemplateAsync(TemplateWithRootFolder());

        using var zip = await GenerateExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"));

        // Same rule as the workspace-root org files above: the extension
        // folder is the root of what the user unzips, so the folders the
        // template declares empty belong here too.
        zip.GetEntry("MyCustomFeature/.alpackages/.gitkeep").Should().NotBeNull();
    }

    [Fact]
    public async Task Sibling_extension_leaves_the_root_folders_to_the_existing_workspace()
    {
        await SeedTemplateAsync(TemplateWithRootFolder());

        var archive = await NewService().GenerateExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"),
            new SiblingWorkspaceContext("CRONUS Customer", Array.Empty<string>(), new[] { "Core" }));
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: false);

        // The workspace already has .alpackages at its own root; one nested a
        // level down would not be the folder the compiler looks in.
        zip.GetEntry("MyCustomFeature/.alpackages/.gitkeep").Should().BeNull();
    }

    [Fact]
    public async Task Downloaded_extension_carries_the_logo_and_points_app_json_at_it()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());
        await SeedLogoAsync("../.assets/logo.png");

        using var zip = await GenerateExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"));

        // The extension folder is the root of the download, so the logo the
        // workspace build puts at the workspace root lands here, and app.json
        // points at it without the '../' that would leave the folder.
        zip.GetEntry("MyCustomFeature/.assets/logo.png").Should().NotBeNull();
        AppJsonLogo(zip).Should().Be(".assets/logo.png");
    }

    [Fact]
    public async Task Sibling_extension_points_at_the_existing_workspace_logo()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());
        await SeedLogoAsync("../.assets/logo.png");

        var archive = await NewService().GenerateExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"),
            new SiblingWorkspaceContext("CRONUS Customer", Array.Empty<string>(), new[] { "Core" }));
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: false);

        // The workspace it is added to already has the logo at its root.
        zip.Entries.Should().NotContain(e => e.FullName.EndsWith("logo.png"));
        AppJsonLogo(zip).Should().Be("../.assets/logo.png");
    }

    [Fact]
    public async Task Extension_added_to_a_repository_points_at_the_existing_workspace_logo()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());
        await SeedLogoAsync("../.assets/logo.png");

        var archive = await NewService().GenerateExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"),
            includeWorkspaceRootFiles: false);
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: false);

        zip.Entries.Should().NotContain(e => e.FullName.EndsWith("logo.png"));
        AppJsonLogo(zip).Should().Be("../.assets/logo.png");
    }

    // ===== Joining an existing workspace =====

    [Fact]
    public async Task Sibling_extension_is_named_with_the_workspace_prefix_in_a_folder_without_it()
    {
        await SeedTemplateAsync(TemplateWithExample());

        using var zip = await GenerateSiblingAsync(
            PlanBuilder.ExtensionPlan(extensionName: "Banking"), CronusSibling());

        // "Banking" joins "CRO Core" as "CRO Banking", in a folder named the
        // way the workspace's own are: "Banking", not "CROBanking".
        using var appJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("Banking/app.json")!));
        appJson.RootElement.GetProperty("name").GetString().Should().Be("CRO Banking");
        using var workspace = JsonDocument.Parse(ReadEntry(zip.GetEntry("CRONUSCustomer.code-workspace")!));
        workspace.RootElement.GetProperty("folders").EnumerateArray()
            .Select(f => f.GetProperty("path").GetString())
            .Should().Equal("Core", "Banking", ".");
    }

    [Fact]
    public async Task Sibling_extension_renders_the_workspace_short_name_and_prefix_in_its_files()
    {
        await SeedTemplateAsync(TemplateWithExample());

        using var zip = await GenerateSiblingAsync(
            PlanBuilder.ExtensionPlan(extensionName: "Banking"), CronusSibling());

        // What the workspace's own extensions render, not the new extension's
        // name standing in for a customer it does not know about.
        ReadEntry(zip.GetEntry("Banking/src/Naming.Codeunit.al")!)
            .Should().Be("prefix=CRO short=CRO workspace=CRONUS Customer");
    }

    [Fact]
    public async Task Sibling_extension_typed_with_the_prefix_is_not_prefixed_twice()
    {
        await SeedTemplateAsync(TemplateWithExample());

        using var zip = await GenerateSiblingAsync(
            PlanBuilder.ExtensionPlan(extensionName: "CRO Banking"), CronusSibling());

        using var appJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("Banking/app.json")!));
        appJson.RootElement.GetProperty("name").GetString().Should().Be("CRO Banking");
    }

    [Fact]
    public async Task Sibling_extension_from_a_workspace_saved_without_a_prefix_takes_the_organisations()
    {
        await SeedTemplateAsync(TemplateWithExample());

        // Saved before the prefix was recorded: the organisation's default
        // policy falls back to the short name, which is what the workspace's
        // own extensions were named with.
        using var zip = await GenerateSiblingAsync(
            PlanBuilder.ExtensionPlan(extensionName: "Banking"),
            CronusSibling() with { ExtensionPrefix = null });

        using var appJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("Banking/app.json")!));
        appJson.RootElement.GetProperty("name").GetString().Should().Be("CRO Banking");
    }

    [Fact]
    public async Task Sibling_extension_leaves_the_example_files_out_even_when_asked_for_them()
    {
        await SeedTemplateAsync(TemplateWithExample());

        using var zip = await GenerateSiblingAsync(
            PlanBuilder.ExtensionPlan(extensionName: "Banking") with { IncludeExamples = true },
            CronusSibling());

        // The workspace already has the template's examples; a second copy
        // repeats their object names under the same prefix.
        zip.GetEntry("Banking/src/Example.Table.al").Should().BeNull();
        zip.GetEntry("Banking/src/Naming.Codeunit.al").Should().NotBeNull("only the example files are left out");
    }

    [Fact]
    public async Task Standalone_extension_keeps_the_example_files_when_asked_for_them()
    {
        await SeedTemplateAsync(TemplateWithExample());

        using var zip = await GenerateExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "Banking") with { IncludeExamples = true });

        zip.GetEntry("Banking/src/Example.Table.al").Should().NotBeNull();
    }

    [Fact]
    public async Task Sibling_extension_in_a_folder_the_workspace_already_has_is_refused()
    {
        await SeedTemplateAsync(TemplateWithExample());

        var act = () => GenerateSiblingAsync(
            PlanBuilder.ExtensionPlan(extensionName: "Core"), CronusSibling());

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["ExtensionName"].Should().Contain("folder called Core");
    }

    [Fact]
    public async Task Sibling_extension_overlapping_an_existing_id_range_is_refused()
    {
        await SeedTemplateAsync(TemplateWithExample());

        var act = () => GenerateSiblingAsync(
            PlanBuilder.ExtensionPlan(extensionName: "Banking", idFrom: 50500, idTo: 51499),
            CronusSibling() with { SavedExtensions = [CoreIdentity()] });

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["IdRangeFrom"].Should().Contain("CRO Core").And.Contain("51000");
    }

    [Fact]
    public async Task Sibling_extension_lists_itself_in_the_workspaces_saved_settings()
    {
        await SeedTemplateAsync(TemplateWithExample());
        var saved = PlanBuilder.WorkspacePlan(workspaceName: "CRONUS Customer", shortName: "CRO", extensionPrefix: "CRO");

        using var zip = await GenerateSiblingAsync(
            PlanBuilder.ExtensionPlan(extensionName: "Banking", idFrom: 51000, idTo: 51999),
            CronusSibling() with { SavedPlan = saved, SavedExtensions = [CoreIdentity()] });

        // Read back the way the next "add an extension" will read it, so that
        // one starts after Banking's IDs and keeps its folder in the workspace.
        var import = await new WorkspaceConfigService(_db.NewContext())
            .ParseAsync(ReadEntry(zip.GetEntry(WorkspaceConfigService.FileName)!));
        import.Workspace!.WorkspaceName.Should().Be("CRONUS Customer");
        import.Extensions.Select(e => (e.Name, e.Folder, e.IdRangeFrom, e.IdRangeTo)).Should().Equal(
            ("CRO Core", "Core", 50000, 50999),
            ("CRO Banking", "Banking", 51000, 51999));
        import.Extensions[1].Kind.Should().Be(WorkspaceExtensionIdentity.AddedKind);

        using var appJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("Banking/app.json")!));
        import.Extensions[1].Id.ToString().Should().Be(appJson.RootElement.GetProperty("id").GetString(),
            "the saved identity is what the next extension declares its dependency on");
    }

    [Fact]
    public async Task Sibling_extension_without_saved_settings_leaves_the_workspaces_file_alone()
    {
        await SeedTemplateAsync(TemplateWithExample());

        using var zip = await GenerateSiblingAsync(
            PlanBuilder.ExtensionPlan(extensionName: "Banking"), CronusSibling());

        // The download path posts only the fields it needs; it cannot rewrite
        // a file it does not have.
        zip.GetEntry(WorkspaceConfigService.FileName).Should().BeNull();
    }

    [Fact]
    public async Task Sibling_extension_of_a_workspace_saved_without_its_extension_list_leaves_its_files_alone()
    {
        await SeedTemplateAsync(TemplateWithExample());
        var saved = PlanBuilder.WorkspacePlan(workspaceName: "CRONUS Customer", shortName: "CRO", extensionPrefix: "CRO");

        // No folders to rebuild the workspace file from: one listing only the
        // new folder would drop Core and every module from it.
        using var zip = await GenerateSiblingAsync(
            PlanBuilder.ExtensionPlan(extensionName: "Banking"),
            new SiblingWorkspaceContext("CRONUS Customer", [], [], "CRO", "CRO", SavedPlan: saved, SavedExtensions: []));

        zip.GetEntry("CRONUSCustomer.code-workspace").Should().BeNull();
        zip.GetEntry(WorkspaceConfigService.FileName).Should().BeNull();
        using var appJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("Banking/app.json")!));
        appJson.RootElement.GetProperty("name").GetString().Should().Be("CRO Banking");
    }

    [Fact]
    public async Task Sibling_extension_saves_the_prefix_it_was_given_when_the_workspace_had_none()
    {
        await SeedTemplateAsync(TemplateWithExample());
        var saved = PlanBuilder.WorkspacePlan(workspaceName: "CRONUS Customer", shortName: "CRO", extensionPrefix: "");

        using var zip = await GenerateSiblingAsync(
            PlanBuilder.ExtensionPlan(extensionName: "Banking", idFrom: 51000, idTo: 51999),
            CronusSibling() with { ExtensionPrefix = null, SavedPlan = saved, SavedExtensions = [CoreIdentity()] });

        // Recorded, so a later change to the organisation's policy cannot give
        // the next extension in this solution a different prefix.
        var import = await new WorkspaceConfigService(_db.NewContext())
            .ParseAsync(ReadEntry(zip.GetEntry(WorkspaceConfigService.FileName)!));
        import.Workspace!.ExtensionPrefix.Should().Be("CRO");
    }

    [Fact]
    public async Task Sibling_extension_whose_prefixed_name_is_too_long_is_refused()
    {
        await SeedTemplateAsync(TemplateWithExample());

        var act = () => GenerateSiblingAsync(
            PlanBuilder.ExtensionPlan(extensionName: "B" + new string('a', 197)), CronusSibling());

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["ExtensionName"].Should().Contain("202 characters");
    }

    private static SiblingWorkspaceContext CronusSibling() => new(
        "CRONUS Customer", Array.Empty<string>(), new[] { "Core" }, ShortName: "CRO", ExtensionPrefix: "CRO");

    private static WorkspaceExtensionIdentity CoreIdentity() => new(
        WorkspaceExtensionIdentity.CoreKind, null, Guid.NewGuid(), "CRO Core", "Core", "CRONUS", 50000, 50999);

    /// <summary>
    /// The default template with one example file and one ordinary file that
    /// renders the workspace-level naming variables.
    /// </summary>
    private static RuntimeTemplate TemplateWithExample()
    {
        var template = TemplateBuilder.Default().WithCoreFolder("src",
            ("Example.Table.al", "table 50000 Example { }"),
            ("Naming.Codeunit.al", "prefix={{extension_prefix}} short={{short_name}} workspace={{workspace_name}}"));
        var src = template.WorkspaceExtensions.Single(e => e.Path == TemplateBuilder.CoreExtensionPath)
            .Folders.Single(f => f.Path == "src");
        src.Files.Single(f => f.Path == "Example.Table.al").IsExample = true;
        return template;
    }

    private async Task<ZipArchive> GenerateSiblingAsync(StandaloneExtensionPlan plan, SiblingWorkspaceContext sibling)
    {
        var archive = await NewService().GenerateExtensionAsync(plan, sibling);
        return new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: false);
    }

    private static RuntimeTemplate TemplateWithRootFolder()
    {
        var template = TemplateBuilder.Default();
        template.RootFolders.Add(new RuntimeTemplateRootFolder
        {
            OrganizationId = TemplateBuilder.DefaultOrganizationId,
            Path = ".alpackages",
            Ordering = 0,
        });
        return template;
    }

    // ===== helpers =====

    private GenerationService NewService()
    {
        var ctx = _db.NewContext();
        var mustache = new ALDevToolbox.Services.Generation.MustacheRenderer(
            NullLogger<ALDevToolbox.Services.Generation.MustacheRenderer>.Instance);
        return new GenerationService(
            ctx,
            _db.NewOrganizationConfigService(ctx),
            new FolderTreeHydrator(ctx),
            _db.OrgContext,
            mustache,
            new ALDevToolbox.Services.Generation.WorkspaceZipBuilder(mustache, new WorkspaceConfigService(ctx)),
            NullLogger<GenerationService>.Instance);
    }

    private async Task<ZipArchive> GenerateExtensionAsync(ALDevToolbox.Domain.ValueObjects.StandaloneExtensionPlan plan)
    {
        var archive = await NewService().GenerateExtensionAsync(plan);
        return new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: false);
    }

    /// <summary>
    /// Seeds the template and joins it to the Default org's files so the
    /// per-extension <c>app.json</c> is emitted — same shape as
    /// <c>WorkspaceGenerationTests.SeedTemplateAsync</c>.
    /// </summary>
    private async Task SeedApplicationVersionAsync(string application, string runtime)
    {
        await using var ctx = _db.NewContext();
        ctx.ApplicationVersions.Add(new ApplicationVersion
        {
            OrganizationId = TemplateBuilder.DefaultOrganizationId,
            Key = "bc-latest",
            Name = "Latest test version",
            Application = application,
            Runtime = runtime,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedTemplateAsync(RuntimeTemplate template)
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(template);
        await ctx.SaveChangesAsync();

        var orgFileIds = await ctx.OrganizationFiles
            .Where(f => f.OrganizationId == template.OrganizationId)
            .OrderBy(f => f.Ordering)
            .Select(f => f.Id)
            .ToListAsync();
        for (var i = 0; i < orgFileIds.Count; i++)
        {
            ctx.Set<RuntimeTemplateIncludedFile>().Add(new RuntimeTemplateIncludedFile
            {
                OrganizationId = template.OrganizationId,
                RuntimeTemplateId = template.Id,
                OrganizationFileId = orgFileIds[i],
                Ordering = i,
            });
        }
        if (orgFileIds.Count > 0)
        {
            await ctx.SaveChangesAsync();
        }
    }

    private async Task SeedLogoAsync(string defaultLogoPath)
    {
        await using var ctx = _db.NewContext();
        await _db.NewOrganizationConfigService(ctx).SaveSettingsAsync(new OrganizationSettingsInput(
            "CRONUS", 50000, 50999, string.Empty, string.Empty, DefaultLogo: defaultLogoPath));
        await _db.NewOrganizationBrandingService(ctx).UploadLogoAsync(
            "image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    }

    private static string? AppJsonLogo(ZipArchive zip)
    {
        var entry = zip.GetEntry("MyCustomFeature/app.json");
        entry.Should().NotBeNull();
        return JsonDocument.Parse(ReadEntry(entry!)).RootElement.GetProperty("logo").GetString();
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
