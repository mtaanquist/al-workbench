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

    [Fact]
    public async Task Create_rejects_a_module_dependency_whose_key_names_no_module()
    {
        // #1342: generation drops a module dependency whose module is not in
        // the workspace, so a typo used to save fine and then vanish.
        await using (var seed = _db.NewContext())
        {
            seed.Modules.Add(ModuleBuilder.Default("document-capture"));
            var deleted = ModuleBuilder.Default("payroll");
            deleted.DeletedAt = DateTime.UtcNow;
            seed.Modules.Add(deleted);
            await seed.SaveChangesAsync();
        }
        var input = NewAuthoring("runtime-module-dep", extensions: new[]
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
                    ModuleDependency("document-capture"),
                    ModuleDependency("documnet-capture"),
                    ModuleDependency("payroll"),
                }),
        });

        await using var ctx = _db.NewContext();
        var act = () => NewService(ctx).CreateAsync(input);

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Keys.Should().BeEquivalentTo(
            "Extensions[0].Dependencies[1].Module", "Extensions[0].Dependencies[2].Module");
        ex.Which.Errors["Extensions[0].Dependencies[1].Module"].Should().Contain("'documnet-capture'");
    }

    [Fact]
    public async Task Create_accepts_a_module_dependency_on_a_live_module()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Modules.Add(ModuleBuilder.Default("document-capture"));
            await seed.SaveChangesAsync();
        }
        var input = NewAuthoring("runtime-module-dep-ok", extensions: new[]
        {
            new ExtensionAuthoring(
                Path: "Core",
                NameTemplate: "Core",
                Required: true,
                Application: null, Runtime: null,
                IdRangeFrom: null, IdRangeTo: null,
                Folders: Array.Empty<FolderAuthoring>(),
                Dependencies: new[] { ModuleDependency("document-capture") }),
        });

        await using var ctx = _db.NewContext();
        var saved = await NewService(ctx).CreateAsync(input);

        saved.WorkspaceExtensions.Single().Dependencies.Single().RefModuleKey.Should().Be("document-capture");
    }

    [Fact]
    public async Task Create_rejects_a_blank_module_dependency_key_with_a_clear_message()
    {
        var input = NewAuthoring("runtime-module-dep-blank", extensions: new[]
        {
            CoreWithDependencies(ModuleDependency("   ")),
        });

        await using var ctx = _db.NewContext();
        var act = () => NewService(ctx).CreateAsync(input);

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Extensions[0].Dependencies[0].Module")
            .WhoseValue.Should().NotContain("''");
    }

    /// <summary>
    /// A module deleted after the template was saved must not lock the template
    /// against further edits - the #1326 trap, for dependencies (#1342).
    /// </summary>
    [Fact]
    public async Task Update_keeps_a_dependency_on_a_since_deleted_module_when_something_else_changes()
    {
        var id = await CreateTemplateDependingOnPayrollThenDeletePayrollAsync("runtime-dep-deleted-rename");

        await using (var ctx = _db.NewContext())
        {
            var current = await NewService(ctx).GetAuthoringByKeyAsync("runtime-dep-deleted-rename");
            await NewService(ctx).UpdateAsync(id, current! with { Name = "Renamed" });
        }

        await using var verify = _db.NewContext();
        var saved = await verify.RuntimeTemplates
            .Include(t => t.WorkspaceExtensions).ThenInclude(e => e.Dependencies)
            .SingleAsync(t => t.Id == id);
        saved.Name.Should().Be("Renamed");
        saved.WorkspaceExtensions.Single().Dependencies.Select(d => d.RefModuleKey).Should().Equal("payroll");
    }

    [Fact]
    public async Task Update_refuses_a_newly_added_module_key_that_names_no_module()
    {
        var id = await CreateTemplateDependingOnPayrollThenDeletePayrollAsync("runtime-dep-new-typo");

        await using var ctx = _db.NewContext();
        var current = await NewService(ctx).GetAuthoringByKeyAsync("runtime-dep-new-typo");
        var core = current!.Extensions.Single();
        var withTypo = current with
        {
            Extensions = new[]
            {
                core with { Dependencies = core.Dependencies.Append(ModuleDependency("documnet-capture")).ToList() },
            },
        };
        var act = () => NewService(ctx).UpdateAsync(id, withTypo);

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Keys.Should().BeEquivalentTo("Extensions[0].Dependencies[1].Module");
        ex.Which.Errors["Extensions[0].Dependencies[1].Module"].Should().Contain("'documnet-capture'");
    }

    [Fact]
    public async Task Toml_export_and_reimport_saves_a_template_depending_on_a_since_deleted_module()
    {
        var id = await CreateTemplateDependingOnPayrollThenDeletePayrollAsync("runtime-dep-deleted-toml");

        await using (var ctx = _db.NewContext())
        {
            var current = await NewService(ctx).GetAuthoringByKeyAsync("runtime-dep-deleted-toml");
            var toml = TemplateTomlMapper.ToToml(current!);
            await NewService(ctx).UpdateAsync(id, TemplateTomlMapper.FromToml(toml, deprecated: false));
        }

        await using var verify = _db.NewContext();
        var deps = await verify.WorkspaceExtensionDependencies
            .Where(d => d.Extension!.TemplateId == id)
            .Select(d => d.RefModuleKey)
            .ToListAsync();
        deps.Should().Equal("payroll");
    }

    private async Task<int> CreateTemplateDependingOnPayrollThenDeletePayrollAsync(string key)
    {
        await using (var seed = _db.NewContext())
        {
            seed.Modules.Add(ModuleBuilder.Default("payroll"));
            await seed.SaveChangesAsync();
        }
        int id;
        await using (var ctx = _db.NewContext())
        {
            id = (await NewService(ctx).CreateAsync(NewAuthoring(key, extensions: new[]
            {
                CoreWithDependencies(ModuleDependency("payroll")),
            }))).Id;
        }
        await using (var del = _db.NewContext())
        {
            var module = await del.Modules.SingleAsync(m => m.Key == "payroll");
            module.DeletedAt = DateTime.UtcNow;
            await del.SaveChangesAsync();
        }
        return id;
    }

    private static ExtensionAuthoring CoreWithDependencies(params DependencyAuthoring[] deps) => new(
        Path: "Core",
        NameTemplate: "Core",
        Required: true,
        Application: null, Runtime: null,
        IdRangeFrom: null, IdRangeTo: null,
        Folders: Array.Empty<FolderAuthoring>(),
        Dependencies: deps);

    private static DependencyAuthoring ModuleDependency(string key) => new(
        RefExtensionPath: null, RefModuleKey: key,
        LitId: null, LitName: null, LitPublisher: null, LitVersion: null);

    // ===== Helpers =====

    private TemplateService NewService(Data.AppDbContext ctx) =>
        new(ctx, NullLogger<TemplateService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx), _db.NewQuotaGuard(ctx));

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

    /// <summary>
    /// The editor page keeps its context between saves. Another tab's save
    /// rebuilds the extensions in between, and the page's next save must not
    /// cascade deletes onto the rows that save already removed (#1600).
    /// </summary>
    [Fact]
    public async Task A_page_that_saved_once_can_save_again_after_another_save_rebuilt_the_extensions()
    {
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).CreateAsync(NewAuthoring("runtime-stale"));
        }
        int id;
        await using (var ctx = _db.NewContext())
        {
            id = await ctx.RuntimeTemplates.Where(t => t.Key == "runtime-stale").Select(t => t.Id).SingleAsync();
        }

        await using var page = _db.NewContext();
        var pageService = NewService(page);
        await pageService.UpdateAsync(id, NewAuthoring("runtime-stale") with { Name = "First" });
        await using (var other = _db.NewContext())
        {
            await NewService(other).UpdateAsync(id, NewAuthoring("runtime-stale") with { Name = "Other tab" });
        }
        await pageService.UpdateAsync(id, NewAuthoring("runtime-stale") with { Name = "Second" });

        await using var read = _db.NewContext();
        (await read.RuntimeTemplates.SingleAsync(t => t.Id == id)).Name.Should().Be("Second");
    }

    [Fact]
    public async Task Creating_a_template_over_the_storage_quota_is_refused()
    {
        await _db.SetZeroStorageQuotaAsync();
        await using var ctx = _db.NewContext();

        var act = () => NewService(ctx).CreateAsync(NewAuthoring("runtime-quota"));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("storage");
    }

    [Fact]
    public async Task Updating_a_template_over_the_storage_quota_is_refused()
    {
        int id;
        await using (var ctx = _db.NewContext())
        {
            id = (await NewService(ctx).CreateAsync(NewAuthoring("runtime-quota-edit"))).Id;
        }
        await _db.SetZeroStorageQuotaAsync();

        await using (var ctx = _db.NewContext())
        {
            var act = () => NewService(ctx).UpdateAsync(id, NewAuthoring("runtime-quota-edit") with { Name = "Renamed" });
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("storage");
        }

        await using var read = _db.NewContext();
        (await read.RuntimeTemplates.SingleAsync(t => t.Id == id)).Name.Should().NotBe("Renamed");
    }

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
