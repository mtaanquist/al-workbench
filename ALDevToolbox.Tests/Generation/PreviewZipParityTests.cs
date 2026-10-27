using System.IO.Compression;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Templates;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Generation;

/// <summary>
/// The New Workspace preview is a second implementation of the ZIP layout
/// (<see cref="WorkspacePreviewBuilder"/>, which <c>NewWorkspace.razor</c>
/// calls), and <c>generation-engine.md</c> promises the user sees what the
/// ZIP holds. This builds both from one template and plan and compares the
/// paths, so a rule changed on one side only fails here.
/// </summary>
/// <remarks>
/// The org logo is left out: the fixture has none.
/// </remarks>
public sealed class PreviewZipParityTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_preview_lists_exactly_the_files_the_zip_holds(bool includeExamples)
    {
        var template = TemplateBuilder.Default()
            .WithCoreFolder("src/codeunits", ("Install.Codeunit.al", "codeunit 90000 Install { }"))
            .WithCoreFolder("src/samples", ("Sample.Codeunit.al", "codeunit 90001 Sample { }"))
            .WithCoreFolder("src/permissionsets");
        template.WorkspaceExtensions.Single().Folders.Single(f => f.Path == "src")
            .Folders.Single(f => f.Path == "samples").Files.Single().IsExample = true;
        template.WorkspaceExtensions.Add(new WorkspaceExtension
        {
            OrganizationId = template.OrganizationId,
            Path = "Test",
            NameTemplate = "{{extension_prefix}} Test",
            Required = false,
            Ordering = 1,
        });
        // One empty root folder, and one that an opted-in file (the ruleset
        // under .assets/rulesets/) already fills.
        template.RootFolders.Add(new RuntimeTemplateRootFolder
        {
            OrganizationId = template.OrganizationId, Path = ".alpackages", Ordering = 0,
        });
        template.RootFolders.Add(new RuntimeTemplateRootFolder
        {
            OrganizationId = template.OrganizationId, Path = ".assets", Ordering = 1,
        });
        var module = ModuleBuilder.Default("document-capture", "Document Capture")
            .WithExtensionFolder("src", ("Doc.Codeunit.al", "codeunit 91000 Doc { }"));
        // A second module the plan leaves unticked, so the selection filter counts.
        var unticked = ModuleBuilder.Default("payroll", "Payroll")
            .WithExtensionFolder("src", ("Pay.Codeunit.al", "codeunit 92000 Pay { }"));
        await SeedAsync(template, module, unticked);

        var plan = PlanBuilder.WorkspacePlan(
            includeExamples: includeExamples,
            selectedExtensions: new[] { "Test" },
            selectedModules: new[] { "document-capture" });

        var zipPaths = await GenerateZipPathsAsync(plan);
        var previewPaths = await BuildPreviewPathsAsync(plan);

        previewPaths.Should().BeEquivalentTo(zipPaths);
        zipPaths.Should().Contain("AcmeCustomer/Core/src/permissionsets/.gitkeep")
            .And.Contain("AcmeCustomer/.alpackages/.gitkeep")
            .And.NotContain("AcmeCustomer/.assets/.gitkeep");
        zipPaths.Contains("AcmeCustomer/Core/src/samples/.gitkeep").Should().Be(!includeExamples);
    }

    private async Task<List<string>> GenerateZipPathsAsync(ProjectPlan plan)
    {
        var ctx = _db.NewContext();
        var mustache = new MustacheRenderer(NullLogger<MustacheRenderer>.Instance);
        var service = new GenerationService(
            ctx,
            _db.NewOrganizationConfigService(ctx),
            new FolderTreeHydrator(ctx),
            _db.OrgContext,
            mustache,
            new WorkspaceZipBuilder(mustache, new WorkspaceConfigService(ctx)),
            NullLogger<GenerationService>.Instance);
        var archive = await service.GenerateWorkspaceAsync(plan);
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).Where(n => !n.EndsWith('/')).ToList();
    }

    private async Task<List<string>> BuildPreviewPathsAsync(ProjectPlan plan)
    {
        // Read back the way the page reads it: rows without their folder
        // trees, then the trees hydrated on top, paths only.
        await using var ctx = _db.NewContext();
        var template = await ctx.RuntimeTemplates.AsNoTracking()
            .Include(t => t.WorkspaceExtensions)
            .Include(t => t.IncludedFiles)
            .Include(t => t.RootFolders)
            .SingleAsync(t => t.Key == plan.TemplateKey);
        var modules = await ctx.Modules.AsNoTracking().ToListAsync();
        var hydrator = new FolderTreeHydrator(ctx);
        await hydrator.HydrateExtensionFolderTreeAsync(new[] { template }, pathsOnly: true);
        await hydrator.HydrateModuleExtensionFolderTreeAsync(modules, pathsOnly: true);
        var orgConfig = await _db.NewOrganizationConfigService(ctx).GetCurrentAsync();

        var root = WorkspacePreviewBuilder.Build(
            CustomerNaming.Apply(plan.WorkspaceName, NamingStyle.PascalCase),
            template,
            plan.SelectedExtensionPaths.ToList(),
            modules,
            plan.SelectedModuleKeys.ToList(),
            orgConfig,
            plan.IncludeExamples);

        var paths = new List<string>();
        Flatten(root, string.Empty, paths);
        return paths;
    }

    /// <summary>
    /// File paths the preview shows as shipping. A struck-through example is
    /// in the tree but not in the ZIP, so it is skipped.
    /// </summary>
    private static void Flatten(PreviewNode node, string parent, List<string> paths)
    {
        var path = parent.Length == 0 ? node.Name : $"{parent}/{node.Name}";
        if (node.Kind == PreviewNodeKind.File)
        {
            if (!node.IsExcluded) paths.Add(path);
            return;
        }
        foreach (var child in node.Children) Flatten(child, path, paths);
    }

    /// <summary>
    /// Seeds the template opted into every org file, as
    /// <c>WorkspaceGenerationTests.SeedTemplateAsync</c> does.
    /// </summary>
    private async Task SeedAsync(RuntimeTemplate template, params Module[] modules)
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(template);
        ctx.Modules.AddRange(modules);
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
        await ctx.SaveChangesAsync();
    }
}
