using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Templates;
using ALDevToolbox.Services.Organizations;
using System.IO.Compression;

namespace ALDevToolbox.Tests.Templates;

/// <summary>
/// Behavioural tests for <see cref="TemplateImportService"/> under the
/// unified-extensions model: cross-org clone of a template plus its
/// <see cref="WorkspaceExtension"/> tree (recursive folders, files,
/// dependencies) and its referenced modules with their own extension trees.
/// The Default org stands in for the singleton system org;
/// <see cref="TestDb.OtherOrgId"/> is the importing tenant.
/// </summary>
public sealed class TemplateImportServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Import_clones_template_with_extension_tree_modules_and_application_version()
    {
        await SeedSystemCatalogueAsync();
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;

        int systemTemplateId = await ResolveSystemTemplateIdAsync("runtime-system");
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ImportTemplateAsync(systemTemplateId);
        }

        await using var verify = _db.NewContext();
        var imported = await verify.RuntimeTemplates
            .IgnoreQueryFilters()
            .Where(t => t.OrganizationId == TestDb.OtherOrgId && t.Key == "runtime-system")
            .Include(t => t.WorkspaceExtensions.OrderBy(e => e.Ordering))
                .ThenInclude(e => e.Dependencies)
            .Include(t => t.DefaultModules).ThenInclude(d => d.Module!).ThenInclude(m => m.Dependencies)
            .Include(t => t.DefaultApplicationVersion)
            .SingleAsync();

        // Extension tree carried over: one Core extension with a Source folder
        // containing a single AL file. The Hotfix extension's intra-template
        // dep on Core survives too.
        imported.WorkspaceExtensions.Should().HaveCount(2);
        imported.WorkspaceExtensions.Select(e => e.Path).Should().Equal("Core", "Hotfix");
        var hotfix = imported.WorkspaceExtensions.Single(e => e.Path == "Hotfix");
        hotfix.Dependencies.Should().ContainSingle()
            .Which.RefExtensionPath.Should().Be("Core");

        var coreId = imported.WorkspaceExtensions.Single(e => e.Path == "Core").Id;
        var folders = await verify.WorkspaceExtensionFolders
            .IgnoreQueryFilters()
            .Where(f => f.WorkspaceExtensionId == coreId)
            .OrderBy(f => f.Ordering)
            .ToListAsync();
        folders.Should().ContainSingle().Which.Path.Should().Be("Source");
        var files = await verify.WorkspaceExtensionFiles
            .IgnoreQueryFilters()
            .Where(f => f.WorkspaceExtensionFolderId == folders[0].Id)
            .ToListAsync();
        files.Should().ContainSingle().Which.Content.Should().Contain("// hello");

        // Module folder/file tree clones alongside the module.
        imported.DefaultModules.Should().ContainSingle();
        var importedModule = imported.DefaultModules.Single().Module!;
        importedModule.Key.Should().Be("payment");
        importedModule.OrganizationId.Should().Be(TestDb.OtherOrgId);
        var moduleFolders = await verify.ModuleExtensionFolders
            .IgnoreQueryFilters()
            .Where(f => f.ModuleId == importedModule.Id)
            .ToListAsync();
        moduleFolders.Should().ContainSingle().Which.Path.Should().Be("Setup");

        imported.DefaultApplicationVersion.Should().NotBeNull();
        imported.DefaultApplicationVersion!.Key.Should().Be("bc24");
        imported.DefaultApplicationVersion.OrganizationId.Should().Be(TestDb.OtherOrgId);
    }

    [Fact]
    public async Task Import_refuses_when_local_template_with_same_key_exists()
    {
        await SeedSystemCatalogueAsync();
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-system", organizationId: TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
        }
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;

        int systemTemplateId = await ResolveSystemTemplateIdAsync("runtime-system");

        await using var ctx = _db.NewContext();
        var act = () => NewService(ctx).ImportTemplateAsync(systemTemplateId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Key");
    }

    /// <summary>
    /// #1277 (T23): the source read escapes the tenant filter, so the predicate
    /// pinning it to the system org is the only thing stopping one tenant from
    /// copying another tenant's template by id. A third org's template is
    /// refused as if it did not exist, and nothing is written.
    /// </summary>
    [Fact]
    public async Task Import_refuses_a_template_that_belongs_to_another_tenant_and_writes_nothing()
    {
        const int ThirdOrgId = 9301;
        await SeedSystemCatalogueAsync();
        int foreignTemplateId;
        await using (var seed = _db.NewContext())
        {
            seed.Organizations.Add(new Organization
            {
                Id = ThirdOrgId, Name = "CRONUS Third", Slug = "cronus-third", CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
            var foreign = TemplateBuilder.Default("third-orgs-template", organizationId: ThirdOrgId);
            seed.RuntimeTemplates.Add(foreign);
            await seed.SaveChangesAsync();
            foreignTemplateId = foreign.Id;
        }
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;

        await using (var ctx = _db.NewContext())
        {
            var act = () => NewService(ctx).ImportTemplateAsync(foreignTemplateId);
            var ex = await act.Should().ThrowAsync<PlanValidationException>();
            ex.Which.Errors.Should().ContainKey("Import");
            ex.Which.Errors["Import"].Should().Contain("no longer exists in the system catalogue");
        }

        await using var verify = _db.NewContext();
        (await verify.RuntimeTemplates.IgnoreQueryFilters().CountAsync(t => t.OrganizationId == TestDb.OtherOrgId))
            .Should().Be(0);
        (await verify.Modules.IgnoreQueryFilters().CountAsync(m => m.OrganizationId == TestDb.OtherOrgId))
            .Should().Be(0);
    }

    [Fact]
    public async Task Import_refuses_when_acting_org_is_the_system_org()
    {
        await SeedSystemCatalogueAsync();
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        _db.OrgContext.IsSystemOrganization = true;

        int systemTemplateId = await ResolveSystemTemplateIdAsync("runtime-system");

        await using var ctx = _db.NewContext();
        var act = () => NewService(ctx).ImportTemplateAsync(systemTemplateId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Import");
    }

    [Fact]
    public async Task Import_leaves_source_template_folder_rows_untouched()
    {
        // Regression for #75: the source template was loaded tracked, then
        // its Folders nav was rewritten with untracked clones during tree
        // hydration. EF could try to persist those edits against the source
        // org's rows on SaveChanges, duplicating folders or leaking the
        // importing org's data back into the system org.
        await SeedSystemCatalogueAsync();
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;

        int systemTemplateId = await ResolveSystemTemplateIdAsync("runtime-system");

        List<int> beforeFolderIds;
        await using (var snap = _db.NewContext())
        {
            beforeFolderIds = await snap.WorkspaceExtensionFolders
                .IgnoreQueryFilters()
                .Where(f => f.OrganizationId == TestDb.DefaultOrgId)
                .Select(f => f.Id)
                .OrderBy(id => id)
                .ToListAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ImportTemplateAsync(systemTemplateId);
        }

        await using var verify = _db.NewContext();
        var afterFolderIds = await verify.WorkspaceExtensionFolders
            .IgnoreQueryFilters()
            .Where(f => f.OrganizationId == TestDb.DefaultOrgId)
            .Select(f => f.Id)
            .OrderBy(id => id)
            .ToListAsync();
        afterFolderIds.Should().Equal(beforeFolderIds,
            "the source org's folder rows must be untouched by an import into another org");
    }

    [Fact]
    public async Task Import_reuses_existing_local_module_when_key_already_present()
    {
        await SeedSystemCatalogueAsync();
        await using (var seed = _db.NewContext())
        {
            seed.Modules.Add(ModuleBuilder.Default("payment", "Local Payment", organizationId: TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
        }
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;

        int systemTemplateId = await ResolveSystemTemplateIdAsync("runtime-system");
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ImportTemplateAsync(systemTemplateId);
        }

        await using var verify = _db.NewContext();
        var localModules = await verify.Modules.IgnoreQueryFilters()
            .Where(m => m.OrganizationId == TestDb.OtherOrgId && m.Key == "payment")
            .ToListAsync();
        localModules.Should().HaveCount(1, "the existing local module should be reused, not duplicated");
        localModules.Single().Name.Should().Be("Local Payment", "the importer must not overwrite the local module name");
    }

    [Fact]
    public async Task List_system_templates_marks_already_imported_rows()
    {
        await SeedSystemCatalogueAsync();
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;

        int systemTemplateId = await ResolveSystemTemplateIdAsync("runtime-system");
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ImportTemplateAsync(systemTemplateId);
        }

        await using var ctx2 = _db.NewContext();
        var listed = await NewService(ctx2).ListSystemTemplatesAsync();
        listed.Should().ContainSingle()
            .Which.AlreadyImported.Should().BeTrue();
    }

    [Fact]
    public async Task Import_then_generate_ships_the_included_files_workspace_overlay_and_latest_default()
    {
        // Regression for #1210: the clone dropped the included-file join, the
        // .code-workspace overlay and the "Latest" flag, so a workspace
        // generated from an imported template had no app.json at all.
        await SeedSystemCatalogueAsync();
        await SeedPlatformFilesAsync();
        await using (var seed = _db.NewContext())
        {
            var systemFiles = await seed.OrganizationFiles.IgnoreQueryFilters()
                .Where(f => f.OrganizationId == TestDb.DefaultOrgId)
                .ToDictionaryAsync(f => f.Path, f => f.Id);
            var template = await seed.RuntimeTemplates.IgnoreQueryFilters()
                .SingleAsync(t => t.OrganizationId == TestDb.DefaultOrgId && t.Key == "runtime-system");
            template.CodeWorkspaceJson = """{ "settings": { "imported.overlay": true } }""";
            template.DefaultApplicationVersionLatest = true;
            string[] paths =
            [
                PlatformOrganizationFiles.AppJsonPath,
                PlatformOrganizationFiles.GitignorePath,
                PlatformOrganizationFiles.ReadmePath,
                PlatformOrganizationFiles.RulesetPath,
            ];
            for (var i = 0; i < paths.Length; i++)
            {
                seed.Add(new RuntimeTemplateIncludedFile
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    RuntimeTemplateId = template.Id,
                    OrganizationFileId = systemFiles[paths[i]],
                    Ordering = i,
                });
            }

            // The importing org has edited its own .gitignore; the clone must
            // point at that row, not at the system org's.
            var otherGitignore = await seed.OrganizationFiles.IgnoreQueryFilters()
                .SingleAsync(f => f.OrganizationId == TestDb.OtherOrgId && f.Path == PlatformOrganizationFiles.GitignorePath);
            otherGitignore.Content = "# CRONUS gitignore";
            await seed.SaveChangesAsync();
        }
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;

        int systemTemplateId = await ResolveSystemTemplateIdAsync("runtime-system");
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ImportTemplateAsync(systemTemplateId);
        }

        await using (var verify = _db.NewContext())
        {
            var imported = await verify.RuntimeTemplates
                .Where(t => t.Key == "runtime-system")
                .Include(t => t.IncludedFiles.OrderBy(j => j.Ordering)).ThenInclude(j => j.OrganizationFile)
                .SingleAsync();
            imported.OrganizationId.Should().Be(TestDb.OtherOrgId);
            imported.DefaultApplicationVersionLatest.Should().BeTrue();
            imported.CodeWorkspaceJson.Should().Contain("imported.overlay");
            imported.IncludedFiles.Select(j => j.OrganizationFile!.Path).Should().Equal(
                PlatformOrganizationFiles.AppJsonPath,
                PlatformOrganizationFiles.GitignorePath,
                PlatformOrganizationFiles.ReadmePath,
                PlatformOrganizationFiles.RulesetPath);
            imported.IncludedFiles.Should().OnlyContain(j =>
                j.OrganizationId == TestDb.OtherOrgId && j.OrganizationFile!.OrganizationId == TestDb.OtherOrgId);
        }

        await using var genCtx = _db.NewContext();
        var archive = await _db.NewGenerationService(genCtx).GenerateWorkspaceAsync(
            PlanBuilder.WorkspacePlan(templateKey: "runtime-system", workspaceName: "Cronus Customer", extensionPrefix: "CRO"));
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);

        zip.GetEntry("CronusCustomer/Core/app.json").Should().NotBeNull("each extension needs an app.json for AL to see a project");
        zip.GetEntry("CronusCustomer/README.md").Should().NotBeNull();
        zip.GetEntry($"CronusCustomer/{PlatformOrganizationFiles.RulesetPath}").Should().NotBeNull();
        var gitignore = zip.GetEntry("CronusCustomer/.gitignore");
        gitignore.Should().NotBeNull();
        using (var reader = new StreamReader(gitignore!.Open()))
        {
            (await reader.ReadToEndAsync()).Should().Contain("# CRONUS gitignore");
        }
        var codeWorkspace = zip.GetEntry("CronusCustomer/CronusCustomer.code-workspace");
        codeWorkspace.Should().NotBeNull();
        using (var reader = new StreamReader(codeWorkspace!.Open()))
        {
            (await reader.ReadToEndAsync()).Should().Contain("imported.overlay");
        }
    }

    [Fact]
    public async Task Import_puts_the_orgs_app_json_first_and_skips_files_the_org_lacks()
    {
        await SeedSystemCatalogueAsync();
        await SeedPlatformFilesAsync();
        await using (var seed = _db.NewContext())
        {
            var template = await seed.RuntimeTemplates.IgnoreQueryFilters()
                .SingleAsync(t => t.OrganizationId == TestDb.DefaultOrgId && t.Key == "runtime-system");
            var gitignoreId = await seed.OrganizationFiles.IgnoreQueryFilters()
                .Where(f => f.OrganizationId == TestDb.DefaultOrgId && f.Path == PlatformOrganizationFiles.GitignorePath)
                .Select(f => f.Id).SingleAsync();
            var systemOnly = new OrganizationFile
            {
                OrganizationId = TestDb.DefaultOrgId,
                Path = "docs/system-only.md",
                Content = "system only",
                UpdatedAt = DateTime.UtcNow,
            };
            seed.OrganizationFiles.Add(systemOnly);
            await seed.SaveChangesAsync();
            seed.Add(new RuntimeTemplateIncludedFile
            {
                OrganizationId = TestDb.DefaultOrgId, RuntimeTemplateId = template.Id,
                OrganizationFileId = gitignoreId, Ordering = 0,
            });
            seed.Add(new RuntimeTemplateIncludedFile
            {
                OrganizationId = TestDb.DefaultOrgId, RuntimeTemplateId = template.Id,
                OrganizationFileId = systemOnly.Id, Ordering = 1,
            });
            await seed.SaveChangesAsync();
        }
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;

        int systemTemplateId = await ResolveSystemTemplateIdAsync("runtime-system");
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ImportTemplateAsync(systemTemplateId);
        }

        await using var verify = _db.NewContext();
        var imported = await verify.RuntimeTemplates
            .Where(t => t.Key == "runtime-system")
            .Include(t => t.IncludedFiles.OrderBy(j => j.Ordering)).ThenInclude(j => j.OrganizationFile)
            .SingleAsync();
        imported.IncludedFiles.Select(j => j.OrganizationFile!.Path).Should().Equal(
            PlatformOrganizationFiles.AppJsonPath,
            PlatformOrganizationFiles.GitignorePath);
    }

    // ===== Fixture =====

    /// <summary>
    /// Both orgs carry the platform files, as a real org does from signup
    /// (the importing org) and from startup (the system org).
    /// </summary>
    private async Task SeedPlatformFilesAsync()
    {
        await using var ctx = _db.NewContext();
        await PlatformOrganizationFileSeeder.EnsureForOrganizationAsync(ctx, TestDb.DefaultOrgId, DateTime.UtcNow);
        await PlatformOrganizationFileSeeder.EnsureForOrganizationAsync(ctx, TestDb.OtherOrgId, DateTime.UtcNow);
        await ctx.SaveChangesAsync();
    }

    private async Task SeedSystemCatalogueAsync()
    {
        // Mark the Default org as the singleton system org; configured by the
        // MoveSeedToSystemOrg migration in real deployments. Re-applying here
        // so the cross-org IsSystem lookup succeeds.
        await using (var ctx = _db.NewContext())
        {
            var defaultOrg = await ctx.Organizations.IgnoreQueryFilters()
                .SingleAsync(o => o.Id == TestDb.DefaultOrgId);
            defaultOrg.IsSystem = true;
            await ctx.SaveChangesAsync();
        }

        await using var seed = _db.NewContext();
        var template = TemplateBuilder.Default("runtime-system", organizationId: TestDb.DefaultOrgId);
        template.WorkspaceExtensions.Single().NameTemplate = "{{extension_prefix}} Core";
        template.WithCoreFolder("Source", ("Hello.al", "// hello"));
        // Hotfix extension with an intra-template dep on Core.
        var hotfix = new WorkspaceExtension
        {
            OrganizationId = TestDb.DefaultOrgId,
            Path = "Hotfix",
            NameTemplate = "{{extension_prefix}} Hotfix",
            Required = true,
            Ordering = 1,
        };
        hotfix.Dependencies.Add(new WorkspaceExtensionDependency
        {
            OrganizationId = TestDb.DefaultOrgId,
            RefExtensionPath = "Core",
            Ordering = 0,
        });
        template.WorkspaceExtensions.Add(hotfix);

        var module = ModuleBuilder.Default("payment", "Payment", organizationId: TestDb.DefaultOrgId)
            .WithDependency("11111111-1111-1111-1111-111111111111", "Base App", "Microsoft", "27.0.0.0")
            .WithExtensionFolder("Setup", ("Payment.Setup.al", "// payment setup"));

        var appVersion = new ApplicationVersion
        {
            OrganizationId = TestDb.DefaultOrgId,
            Key = "bc24",
            Name = "BC 24",
            Application = "24.0.0.0",
            Runtime = "15",
            Ordering = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        seed.Modules.Add(module);
        seed.ApplicationVersions.Add(appVersion);
        await seed.SaveChangesAsync();

        // Attach the module + app version to the template after both exist.
        template.DefaultApplicationVersionId = appVersion.Id;
        template.DefaultModules.Add(new RuntimeTemplateDefaultModule
        {
            OrganizationId = TestDb.DefaultOrgId,
            ModuleId = module.Id,
            Ordering = 0,
        });
        seed.RuntimeTemplates.Add(template);
        await seed.SaveChangesAsync();
    }

    private async Task<int> ResolveSystemTemplateIdAsync(string key)
    {
        await using var read = _db.NewContext();
        return await read.RuntimeTemplates.IgnoreQueryFilters()
            .Where(t => t.OrganizationId == TestDb.DefaultOrgId && t.Key == key)
            .Select(t => t.Id)
            .SingleAsync();
    }

    private TemplateImportService NewService(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext,
            new FolderTreeHydrator(ctx),
            _db.NewQuotaGuard(ctx),
            NullLogger<TemplateImportService>.Instance);
}
