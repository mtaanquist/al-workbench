using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Templates;

/// <summary>
/// Covers module folder/file authoring: the create/update write path persists
/// the recursive <c>module_extension_folders</c> tree (the feature that was
/// designed-in but had no authoring surface until now), <see cref="ModuleService.GetByKeyAsync"/>
/// hydrates it back, and the shared folder-tree validator rejects bad paths.
/// </summary>
public sealed class ModuleServiceFolderTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Create_persists_nested_folder_tree_and_GetByKey_round_trips()
    {
        var folders = new List<FolderAuthoring>
        {
            new(
                Path: "src",
                Folders: new List<FolderAuthoring>
                {
                    new(
                        Path: "codeunits",
                        Folders: new List<FolderAuthoring>(),
                        Files: new List<FileAuthoring>
                        {
                            new("Hello.Codeunit.al", "codeunit 90000 \"{{extension_prefix}} Hello\" { }", false),
                            new("Sample.Codeunit.al", "// sample", true),
                        }),
                },
                Files: new List<FileAuthoring>
                {
                    new("Readme.md", "# {{extension_prefix}}", false),
                }),
        };

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).CreateAsync(ModuleInputFor("mod-folders", folders));
        }

        await using (var verify = _db.NewContext())
        {
            var module = await NewService(verify).GetByKeyAsync("mod-folders");
            module.Should().NotBeNull();

            module!.ExtensionFolders.Should().HaveCount(1);
            var src = module.ExtensionFolders.Single();
            src.Path.Should().Be("src");
            src.Files.Select(f => f.Path).Should().Equal("Readme.md");

            src.Folders.Should().HaveCount(1);
            var codeunits = src.Folders.Single();
            codeunits.Path.Should().Be("codeunits");
            codeunits.Files.OrderBy(f => f.Ordering).Select(f => f.Path)
                .Should().Equal("Hello.Codeunit.al", "Sample.Codeunit.al");
            codeunits.Files.Single(f => f.Path == "Hello.Codeunit.al").Content
                .Should().Contain("{{extension_prefix}}");
            codeunits.Files.Single(f => f.Path == "Sample.Codeunit.al").IsExample.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Update_replaces_the_folder_tree()
    {
        int moduleId;
        await using (var ctx = _db.NewContext())
        {
            var module = await NewService(ctx).CreateAsync(ModuleInputFor("mod-replace", new List<FolderAuthoring>
            {
                new("old", new List<FolderAuthoring>(), new List<FileAuthoring> { new("Old.al", "x", false) }),
            }));
            moduleId = module.Id;
        }

        var replacement = new List<FolderAuthoring>
        {
            new("src", new List<FolderAuthoring>(), new List<FileAuthoring> { new("New.al", "y", false) }),
        };

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(moduleId, ModuleInputFor("mod-replace", replacement));
        }

        await using (var verify = _db.NewContext())
        {
            // The old tree is gone, not just hidden — no orphaned rows linger.
            var allFolders = verify.ModuleExtensionFolders.Where(f => f.ModuleId == moduleId).Select(f => f.Path).ToList();
            allFolders.Should().Equal("src");
            verify.ModuleExtensionFiles
                .Count(f => f.Folder!.ModuleId == moduleId).Should().Be(1);

            var module = await NewService(verify).GetByKeyAsync("mod-replace");
            module!.ExtensionFolders.Single().Files.Single().Path.Should().Be("New.al");
        }
    }

    [Fact]
    public async Task Create_rejects_invalid_folder_and_file_paths()
    {
        var folders = new List<FolderAuthoring>
        {
            new(
                Path: "src/nested",                       // slash — not a single segment
                Folders: new List<FolderAuthoring>(),
                Files: new List<FileAuthoring>
                {
                    new("Dup.al", "a", false),
                    new("Dup.al", "b", false),            // duplicate filename in folder
                }),
            new("dupe", new List<FolderAuthoring>(), new List<FileAuthoring>()),
            new("DUPE", new List<FolderAuthoring>(), new List<FileAuthoring>()), // case-insensitive sibling clash
        };

        await using var ctx = _db.NewContext();
        var act = async () => await NewService(ctx).CreateAsync(ModuleInputFor("mod-bad", folders));

        var ex = (await act.Should().ThrowAsync<PlanValidationException>()).Which;
        ex.Errors.Should().ContainKey("Folders[0].Path");
        ex.Errors.Should().ContainKey("Folders[0].Files[1].Path");
        ex.Errors.Should().ContainKey("Folders[2].Path");
    }

    private ModuleService NewService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<ModuleService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx), _db.NewQuotaGuard(ctx));

    private static ModuleInput ModuleInputFor(string key, IReadOnlyList<FolderAuthoring> folders) =>
        new(
            Key: key,
            Name: "Test Module",
            ExtensionName: "TestModule",
            IdRangeSize: null,
            Deprecated: false,
            Dependencies: new List<ModuleDependencyInput>(),
            Folders: folders);
}
