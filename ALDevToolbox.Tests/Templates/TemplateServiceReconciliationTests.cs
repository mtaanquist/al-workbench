using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Templates;

/// <summary>
/// Behavioural tests for the unified-extensions
/// <see cref="TemplateService.CreateAsync(TemplateAuthoring, CancellationToken)"/>
/// /
/// <see cref="TemplateService.UpdateAsync(int, TemplateAuthoring, CancellationToken)"/>
/// write path. Covers the validator (field-keyed errors), the create-and-persist
/// happy path, the update-then-rebuild path, and the default-modules
/// reconciler (PK stability).
/// </summary>
public sealed class TemplateServiceWriteSideTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Create_persists_template_with_extension_tree_and_default_modules()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Modules.Add(ModuleBuilder.Default("alpha"));
            await seed.SaveChangesAsync();
        }

        var input = NewAuthoring("runtime-create", extensions: new[]
        {
            new ExtensionAuthoring(
                Path: "Core",
                NameTemplate: "{{extension_prefix}} Core",
                Required: true,
                Application: null, Runtime: null,
                IdRangeFrom: null, IdRangeTo: null,
                Folders: new[]
                {
                    new FolderAuthoring(
                        Path: "src",
                        Folders: new[]
                        {
                            new FolderAuthoring(
                                Path: "codeunits",
                                Folders: Array.Empty<FolderAuthoring>(),
                                Files: new[]
                                {
                                    new FileAuthoring("AppInstall.al", "codeunit Install {}", IsExample: false),
                                }),
                        },
                        Files: Array.Empty<FileAuthoring>()),
                },
                Dependencies: Array.Empty<DependencyAuthoring>()),
        }, defaultModuleKeys: new[] { "alpha" });

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).CreateAsync(input);
        }

        await using var verify = _db.NewContext();
        var saved = await verify.RuntimeTemplates
            .Where(t => t.Key == "runtime-create")
            .Include(t => t.WorkspaceExtensions)
            .Include(t => t.DefaultModules)
            .SingleAsync();
        saved.WorkspaceExtensions.Should().ContainSingle();
        var coreId = saved.WorkspaceExtensions.Single().Id;
        var folders = await verify.WorkspaceExtensionFolders
            .Where(f => f.WorkspaceExtensionId == coreId)
            .OrderBy(f => f.ParentFolderId == null ? 0 : 1)
            .ToListAsync();
        folders.Should().HaveCount(2, "the nested codeunits folder is its own row");
        folders.Select(f => f.Path).Should().Contain(new[] { "src", "codeunits" });
        saved.DefaultModules.Should().ContainSingle();
    }

    [Fact]
    public async Task Update_replaces_extension_tree_wholesale_via_cascade_delete()
    {
        int templateId;
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-update")
                .WithCoreFolder("OldFolder", ("Sample.al", "// old"));
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
            templateId = template.Id;
        }

        // New input declares a different folder + file. The reconciler should
        // cascade-drop the old folder/file rows and add fresh ones.
        var updated = NewAuthoring("runtime-update", extensions: new[]
        {
            new ExtensionAuthoring(
                Path: "Core",
                NameTemplate: "{{extension_prefix}} Core",
                Required: true,
                Application: null, Runtime: null,
                IdRangeFrom: null, IdRangeTo: null,
                Folders: new[]
                {
                    new FolderAuthoring(
                        Path: "NewFolder",
                        Folders: Array.Empty<FolderAuthoring>(),
                        Files: new[] { new FileAuthoring("Fresh.al", "// fresh", IsExample: false) }),
                },
                Dependencies: Array.Empty<DependencyAuthoring>()),
        });

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(templateId, updated);
        }

        await using var verify = _db.NewContext();
        var folders = await verify.WorkspaceExtensionFolders
            .Where(f => f.Extension!.TemplateId == templateId)
            .ToListAsync();
        folders.Select(f => f.Path).Should().ContainSingle().Which.Should().Be("NewFolder");
        var files = await verify.WorkspaceExtensionFiles
            .Where(f => f.Folder!.Extension!.TemplateId == templateId)
            .ToListAsync();
        files.Should().ContainSingle().Which.Content.Should().Be("// fresh");
    }

    [Fact]
    public async Task Update_preserves_default_module_join_row_primary_keys_on_reorder()
    {
        int templateId, alphaJoinId, betaJoinId;
        await using (var seed = _db.NewContext())
        {
            seed.Modules.AddRange(ModuleBuilder.Default("alpha"), ModuleBuilder.Default("beta"));
            await seed.SaveChangesAsync();

            var template = TemplateBuilder.Default("runtime-reconcile");
            var alpha = seed.Modules.Single(m => m.Key == "alpha");
            var beta = seed.Modules.Single(m => m.Key == "beta");
            template.DefaultModules.Add(new RuntimeTemplateDefaultModule { OrganizationId = template.OrganizationId, ModuleId = alpha.Id, Ordering = 0 });
            template.DefaultModules.Add(new RuntimeTemplateDefaultModule { OrganizationId = template.OrganizationId, ModuleId = beta.Id, Ordering = 1 });
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();

            templateId = template.Id;
            alphaJoinId = template.DefaultModules.Single(d => d.Ordering == 0).Id;
            betaJoinId = template.DefaultModules.Single(d => d.Ordering == 1).Id;
        }

        // Swap the order. The reconciler matches rows by ModuleId (their
        // natural identity), so each existing join row keeps its PK; only the
        // ordering column is rewritten. Mutating ModuleId in place would
        // collide with the (runtime_template_id, module_id) unique index
        // mid-batch, which EF can't topologically sort around.
        var updated = NewAuthoring("runtime-reconcile",
            extensions: SingleCoreExtension(),
            defaultModuleKeys: new[] { "beta", "alpha" });

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(templateId, updated);
        }

        await using var verify = _db.NewContext();
        var rows = await verify.RuntimeTemplateDefaultModules
            .Where(d => d.RuntimeTemplateId == templateId)
            .OrderBy(d => d.Ordering)
            .Include(d => d.Module!)
            .ToListAsync();
        rows.Should().HaveCount(2);
        // beta is now at Ordering=0; its row is the one that was previously
        // ordered second, so its PK matches betaJoinId.
        rows[0].Module!.Key.Should().Be("beta");
        rows[0].Id.Should().Be(betaJoinId);
        rows[1].Module!.Key.Should().Be("alpha");
        rows[1].Id.Should().Be(alphaJoinId);
    }

    /// <summary>
    /// #1326: a template whose default module was deleted loads without it,
    /// saves unchanged, and keeps the link so restoring the module makes it a
    /// default again.
    /// </summary>
    [Fact]
    public async Task Template_with_a_deleted_default_module_saves_unchanged_and_keeps_the_link()
    {
        int templateId, retiredJoinId;
        await using (var seed = _db.NewContext())
        {
            seed.Modules.AddRange(ModuleBuilder.Default("alpha"), ModuleBuilder.Default("retired"), ModuleBuilder.Default("beta"));
            await seed.SaveChangesAsync();

            var template = TemplateBuilder.Default("runtime-retired");
            var ids = seed.Modules.ToDictionary(m => m.Key, m => m.Id);
            template.DefaultModules.Add(new RuntimeTemplateDefaultModule { OrganizationId = template.OrganizationId, ModuleId = ids["alpha"], Ordering = 0 });
            template.DefaultModules.Add(new RuntimeTemplateDefaultModule { OrganizationId = template.OrganizationId, ModuleId = ids["retired"], Ordering = 1 });
            template.DefaultModules.Add(new RuntimeTemplateDefaultModule { OrganizationId = template.OrganizationId, ModuleId = ids["beta"], Ordering = 2 });
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
            templateId = template.Id;
            retiredJoinId = template.DefaultModules.Single(d => d.ModuleId == ids["retired"]).Id;

            seed.Modules.Single(m => m.Key == "retired").DeletedAt = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }

        TemplateAuthoring authoring;
        await using (var ctx = _db.NewContext())
        {
            authoring = (await NewService(ctx).GetAuthoringByKeyAsync("runtime-retired"))!;
        }
        authoring.DefaultModuleKeys.Should().Equal("alpha", "beta");

        // A rename only: before #1326 this was refused with "Unknown default module(s): retired".
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(templateId, authoring with { Name = "Renamed" });
        }

        await using (var verify = _db.NewContext())
        {
            var rows = await verify.RuntimeTemplateDefaultModules
                .Where(d => d.RuntimeTemplateId == templateId)
                .OrderBy(d => d.Ordering)
                .Include(d => d.Module!)
                .ToListAsync();
            rows.Select(r => r.Module!.Key).Should().Equal("alpha", "beta", "retired");
            rows.Single(r => r.Module!.Key == "retired").Id.Should().Be(retiredJoinId);

            verify.Modules.Single(m => m.Key == "retired").DeletedAt = null;
            await verify.SaveChangesAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            var restored = await NewService(ctx).GetAuthoringByKeyAsync("runtime-retired");
            restored!.Name.Should().Be("Renamed");
            restored.DefaultModuleKeys.Should().Equal("alpha", "beta", "retired");
        }
    }

    [Fact]
    public async Task Create_rejects_invalid_folder_path_segments()
    {
        var input = NewAuthoring("runtime-bad-path", extensions: new[]
        {
            new ExtensionAuthoring(
                Path: "Core",
                NameTemplate: "Core",
                Required: true,
                Application: null, Runtime: null,
                IdRangeFrom: null, IdRangeTo: null,
                Folders: new[]
                {
                    new FolderAuthoring(
                        Path: "with/slash",
                        Folders: Array.Empty<FolderAuthoring>(),
                        Files: Array.Empty<FileAuthoring>()),
                },
                Dependencies: Array.Empty<DependencyAuthoring>()),
        });

        await using var ctx = _db.NewContext();
        var act = () => NewService(ctx).CreateAsync(input);

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Keys.Should().Contain(k => k.EndsWith("Path"));
    }

    [Fact]
    public async Task Create_rejects_intra_template_dep_to_unknown_extension()
    {
        var input = NewAuthoring("runtime-bad-dep", extensions: new[]
        {
            new ExtensionAuthoring(
                Path: "Core",
                NameTemplate: "Core",
                Required: true,
                Application: null, Runtime: null,
                IdRangeFrom: null, IdRangeTo: null,
                Folders: Array.Empty<FolderAuthoring>(),
                Dependencies: new[]
                {
                    new DependencyAuthoring(
                        RefExtensionPath: "Missing",
                        RefModuleKey: null,
                        LitId: null, LitName: null, LitPublisher: null, LitVersion: null),
                }),
        });

        await using var ctx = _db.NewContext();
        var act = () => NewService(ctx).CreateAsync(input);

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Keys.Should().Contain(k => k.Contains("Extension"));
    }

    [Fact]
    public async Task Create_persists_per_template_code_workspace_json_overlay()
    {
        const string overlay = """
            { "settings": { "al.newSetting": "${X}" } }
            """;
        var input = NewAuthoring("runtime-overlay") with { CodeWorkspaceJson = overlay };

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).CreateAsync(input);
        }

        await using var verify = _db.NewContext();
        var saved = await verify.RuntimeTemplates.SingleAsync(t => t.Key == "runtime-overlay");
        saved.CodeWorkspaceJson.Should().NotBeNullOrWhiteSpace();
        saved.CodeWorkspaceJson!.Should().Contain("al.newSetting");
    }

    [Fact]
    public async Task Create_treats_blank_code_workspace_json_as_null()
    {
        // Blank / whitespace input rounds-trips to NULL in the column so the
        // generator's "no override" branch fires and the audit log stays
        // clean — empty-string vs null shouldn't be a meaningful distinction
        // for admins.
        var input = NewAuthoring("runtime-blank-overlay") with { CodeWorkspaceJson = "   " };

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).CreateAsync(input);
        }

        await using var verify = _db.NewContext();
        var saved = await verify.RuntimeTemplates.SingleAsync(t => t.Key == "runtime-blank-overlay");
        saved.CodeWorkspaceJson.Should().BeNull();
    }

    [Theory]
    [InlineData("not even close to json")]
    [InlineData("[\"array root not allowed\"]")]
    [InlineData("\"plain string\"")]
    public async Task Create_rejects_invalid_code_workspace_json(string bad)
    {
        var input = NewAuthoring("runtime-bad-overlay") with { CodeWorkspaceJson = bad };

        await using var ctx = _db.NewContext();
        var act = () => NewService(ctx).CreateAsync(input);

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey(nameof(TemplateAuthoring.CodeWorkspaceJson));
    }

    [Fact]
    public async Task Create_rejects_dependency_with_multiple_reference_shapes_set()
    {
        var input = NewAuthoring("runtime-multi-ref", extensions: new[]
        {
            new ExtensionAuthoring(
                Path: "Core",
                NameTemplate: "Core",
                Required: true,
                Application: null, Runtime: null,
                IdRangeFrom: null, IdRangeTo: null,
                Folders: Array.Empty<FolderAuthoring>(),
                Dependencies: new[]
                {
                    // Both extension-ref and module-ref set — only one allowed.
                    new DependencyAuthoring(
                        RefExtensionPath: "Core",
                        RefModuleKey: "system-application",
                        LitId: null, LitName: null, LitPublisher: null, LitVersion: null),
                }),
        });

        await using var ctx = _db.NewContext();
        var act = () => NewService(ctx).CreateAsync(input);

        await act.Should().ThrowAsync<PlanValidationException>();
    }

    // ===== Helpers =====

    private TemplateService NewService(Data.AppDbContext ctx) =>
        new(ctx, NullLogger<TemplateService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx));

    private static IReadOnlyList<ExtensionAuthoring> SingleCoreExtension() => new[]
    {
        new ExtensionAuthoring(
            Path: "Core",
            NameTemplate: "Core",
            Required: true,
            Application: null, Runtime: null,
            IdRangeFrom: null, IdRangeTo: null,
            Folders: Array.Empty<FolderAuthoring>(),
            Dependencies: Array.Empty<DependencyAuthoring>()),
    };

    /// <summary>Builds a minimally-valid TemplateAuthoring with sensible defaults so each test only spells out the field it cares about.</summary>
    private static TemplateAuthoring NewAuthoring(
        string key,
        IReadOnlyList<ExtensionAuthoring>? extensions = null,
        IReadOnlyList<string>? defaultModuleKeys = null) => new(
            Key: key,
            Runtime: "15",
            Name: "Test " + key,
            Description: null,
            DefaultsJson: JsonSerializer.Serialize(new TemplateDefaults
            {
                Publisher = "Acme",
                Application = "24.0.0.0",
                Platform = "1.0.0.0",
                AffixType = AffixType.None,
            }),
            AppSourceCopJson: JsonSerializer.Serialize(new AppSourceCopSettings()),
            CoreIdRangeFrom: 90000,
            CoreIdRangeTo: 90999,
            ModuleIdRangeStart: 91000,
            ModuleIdRangeSize: 200,
            Deprecated: false,
            IsDefault: false,
            DefaultApplicationVersionKey: null,
            DefaultModuleKeys: defaultModuleKeys ?? Array.Empty<string>(),
            Extensions: extensions ?? SingleCoreExtension());
}
