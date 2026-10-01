using System.IO.Compression;
using System.Text.Json;
using ALDevToolbox.Domain.Entities;
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
