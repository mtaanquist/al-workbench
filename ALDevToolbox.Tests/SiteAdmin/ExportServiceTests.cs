using System.IO.Compression;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Smoke coverage for <see cref="ExportService"/>: ZIP layout (templates
/// under their key, modules under <c>modules/</c>, application versions
/// under <c>application-versions/</c>, catalogue at
/// <c>catalog/well-known-deps.toml</c>) and soft-deleted rows are excluded
/// from the archive. The org-config block is covered separately in
/// <see cref="ALDevToolbox.Tests.Configuration.ConfigExportImportRoundTripTests"/>.
/// </summary>
public sealed class ExportServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ExportAllAsync_writes_active_rows_under_expected_paths()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-active"));
            seed.Modules.Add(ModuleBuilder.Default("mod-active"));
            seed.WellKnownDependencies.Add(WellKnownDependencyBuilder.ForNav(
                "00000000-0000-0000-0000-000000000099", "Base"));
            seed.ApplicationVersions.Add(new ApplicationVersion
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "bc24", Name = "BC 24",
                Application = "24.0.0.0", Runtime = "15.0",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var entries = await ExportAndListEntriesAsync();

        entries.Should().Contain("runtime-active/template.toml");
        entries.Should().Contain("modules/mod-active.toml");
        entries.Should().Contain("application-versions/bc24.toml");
        entries.Should().Contain("catalog/well-known-deps.toml");
    }

    [Fact]
    public async Task ExportAllAsync_excludes_soft_deleted_rows()
    {
        await using (var seed = _db.NewContext())
        {
            var deletedTemplate = TemplateBuilder.Default("runtime-deleted");
            deletedTemplate.DeletedAt = DateTime.UtcNow;
            seed.RuntimeTemplates.Add(deletedTemplate);

            var deletedModule = ModuleBuilder.Default("mod-deleted");
            deletedModule.DeletedAt = DateTime.UtcNow;
            seed.Modules.Add(deletedModule);

            seed.ApplicationVersions.Add(new ApplicationVersion
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "bc-deleted", Name = "BC Deleted",
                Application = "20.0.0.0", Runtime = "11.0",
                DeletedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var entries = await ExportAndListEntriesAsync();

        entries.Should().NotContain("runtime-deleted/template.toml");
        entries.Should().NotContain("modules/mod-deleted.toml");
        entries.Should().NotContain("application-versions/bc-deleted.toml");
    }

    [Fact]
    public async Task ExportAllAsync_excludes_rows_from_other_orgs()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-mine"));
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-theirs",
                organizationId: TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        var entries = await ExportAndListEntriesAsync();

        entries.Should().Contain("runtime-mine/template.toml");
        entries.Should().NotContain("runtime-theirs/template.toml");
    }

    [Fact]
    public async Task ExportAllAsync_template_toml_round_trips_through_the_mapper()
    {
        // Use the same TemplateTomlMapper the admin TOML editor uses, so a
        // freshly-exported template parses back into an equivalent model.
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-rt"));
            await seed.SaveChangesAsync();
        }

        string templateToml;
        await using (var ctx = _db.NewContext())
        {
            var svc = new ExportService(ctx, _db.OrgContext, new FolderTreeHydrator(ctx), NullLogger<ExportService>.Instance);
            var archive = await svc.ExportAllAsync();
            using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry("runtime-rt/template.toml");
            entry.Should().NotBeNull();
            using var reader = new StreamReader(entry!.Open());
            templateToml = await reader.ReadToEndAsync();
        }

        var parsed = TemplateTomlMapper.FromToml(templateToml, deprecated: false);
        parsed.Key.Should().Be("runtime-rt");
    }

    /// <summary>
    /// #1326 review: a template keeps its link to a deleted default module so a
    /// restore brings it back, but the exported template.toml must leave it
    /// out - the TOML editor refuses a key that names no live module.
    /// </summary>
    [Fact]
    public async Task ExportAllAsync_template_toml_leaves_out_deleted_default_modules()
    {
        await using (var seed = _db.NewContext())
        {
            var live = ModuleBuilder.Default("mod-live");
            var retired = ModuleBuilder.Default("mod-retired");
            retired.DeletedAt = DateTime.UtcNow;
            seed.Modules.AddRange(live, retired);
            await seed.SaveChangesAsync();

            var template = TemplateBuilder.Default("runtime-defaults");
            template.DefaultModules.Add(new RuntimeTemplateDefaultModule { OrganizationId = template.OrganizationId, ModuleId = retired.Id, Ordering = 0 });
            template.DefaultModules.Add(new RuntimeTemplateDefaultModule { OrganizationId = template.OrganizationId, ModuleId = live.Id, Ordering = 1 });
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
        }

        string templateToml;
        await using (var ctx = _db.NewContext())
        {
            var svc = new ExportService(ctx, _db.OrgContext, new FolderTreeHydrator(ctx), NullLogger<ExportService>.Instance);
            var archive = await svc.ExportAllAsync();
            using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);
            using var reader = new StreamReader(zip.GetEntry("runtime-defaults/template.toml")!.Open());
            templateToml = await reader.ReadToEndAsync();
        }

        templateToml.Should().NotContain("mod-retired");
        TemplateTomlMapper.FromToml(templateToml, deprecated: false)
            .DefaultModuleKeys.Should().Equal("mod-live");
    }

    [Fact]
    public async Task ExportAllAsync_template_toml_carries_dependencies_folders_included_files_and_root_folders()
    {
        // #1220: the export loaded only the extensions themselves, so the
        // template.toml it wrote had none of the layout below.
        await using (var seed = _db.NewContext())
        {
            var file = new OrganizationFile
            {
                OrganizationId = TestDb.DefaultOrgId,
                Path = "export-included.txt",
                Content = "x",
                UpdatedAt = DateTime.UtcNow,
            };
            seed.OrganizationFiles.Add(file);
            await seed.SaveChangesAsync();

            var template = TemplateBuilder.Default("runtime-layout")
                .WithCoreFolder("src/Codeunits", ("Hello.Codeunit.al", "codeunit 50100 Hello {}"));
            var core = template.WorkspaceExtensions.Single();
            core.Dependencies.Add(new WorkspaceExtensionDependency
            {
                OrganizationId = TestDb.DefaultOrgId,
                Ordering = 0,
                LitId = "11111111-2222-3333-4444-555555555555",
                LitName = "Base",
                LitPublisher = "CRONUS",
                LitVersion = "1.0.0.0",
            });
            template.IncludedFiles.Add(new RuntimeTemplateIncludedFile
            {
                OrganizationId = TestDb.DefaultOrgId,
                OrganizationFileId = file.Id,
                Ordering = 0,
            });
            template.RootFolders.Add(new RuntimeTemplateRootFolder
            {
                OrganizationId = TestDb.DefaultOrgId,
                Path = ".alpackages",
                Ordering = 0,
            });
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
        }

        string templateToml;
        await using (var ctx = _db.NewContext())
        {
            var svc = new ExportService(ctx, _db.OrgContext, new FolderTreeHydrator(ctx), NullLogger<ExportService>.Instance);
            var archive = await svc.ExportAllAsync();
            using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);
            using var reader = new StreamReader(zip.GetEntry("runtime-layout/template.toml")!.Open());
            templateToml = await reader.ReadToEndAsync();
        }

        var parsed = TemplateTomlMapper.FromToml(templateToml, deprecated: false);
        var ext = parsed.Extensions.Should().ContainSingle().Subject;
        ext.Dependencies.Should().ContainSingle(d => d.LitName == "Base" && d.LitPublisher == "CRONUS");
        var src = ext.Folders.Should().ContainSingle(f => f.Path == "src").Subject;
        var codeunits = src.Folders.Should().ContainSingle(f => f.Path == "Codeunits").Subject;
        codeunits.Files.Should().ContainSingle(f => f.Path == "Hello.Codeunit.al" && f.Content == "codeunit 50100 Hello {}");
        parsed.IncludedFilePaths.Should().Equal("export-included.txt");
        parsed.RootFolderPaths.Should().Equal(".alpackages");
    }

    [Fact]
    public async Task ExportAllAsync_writes_the_size_a_module_takes_from_its_repository()
    {
        await using (var seed = _db.NewContext())
        {
            var following = ModuleBuilder.Default("mod-following");
            following.SourceIdRangeSize = 120;
            seed.Modules.Add(following);
            await seed.SaveChangesAsync();
        }

        string toml;
        await using (var ctx = _db.NewContext())
        {
            var svc = new ExportService(ctx, _db.OrgContext, new FolderTreeHydrator(ctx), NullLogger<ExportService>.Instance);
            var archive = await svc.ExportAllAsync();
            using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);
            using var reader = new StreamReader(zip.GetEntry("modules/mod-following.toml")!.Open());
            toml = await reader.ReadToEndAsync();
        }

        toml.Should().Contain("id_range_size = 120", "a blank size that follows the repository is exported as it stands (#1631)");
    }

    private async Task<List<string>> ExportAndListEntriesAsync()
    {
        await using var ctx = _db.NewContext();
        var svc = new ExportService(ctx, _db.OrgContext, new FolderTreeHydrator(ctx), NullLogger<ExportService>.Instance);
        var archive = await svc.ExportAllAsync();
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).ToList();
    }
}
