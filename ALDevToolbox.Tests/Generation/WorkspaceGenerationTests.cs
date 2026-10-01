using System.IO.Compression;
using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Generation;

/// <summary>
/// End-to-end coverage for the unified-extensions <see cref="GenerationService"/>:
/// required vs. optional extension emission, module-clone path, recursive
/// folder layout, mustache substitution (<c>{{affix}}</c> and
/// <c>{{extension_prefix}}</c>), id-range allocation, and dependency
/// resolution by stable identifier. Drives the public
/// <see cref="GenerationService.GenerateWorkspaceAsync"/> API and reads the
/// ZIP back so a private-helper refactor still has to honour the contract.
/// </summary>
public sealed class WorkspaceGenerationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Required_extension_emits_its_folder_with_app_json_and_appsourcecop()
    {
        // AppSourceCop.json now ships only when the org has an
        // OrganizationFile at that path with Scope = EveryExtension and the
        // template opts in. Seed one before generation to keep the per-
        // extension emission covered by this test.
        await using (var ctx = _db.NewContext())
        {
            ctx.OrganizationFiles.Add(new OrganizationFile
            {
                OrganizationId = ALDevToolbox.Tests.Builders.TemplateBuilder.DefaultOrganizationId,
                Path = "AppSourceCop.json",
                Content = "{ \"mandatoryPrefix\": \"ACME\" }",
                MustacheEnabled = false,
                Scope = ALDevToolbox.Domain.ValueObjects.OrganizationFileScope.EveryExtension,
                Ordering = 2000,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        await SeedTemplateAsync(TemplateBuilder.Default());

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        zip.GetEntry("AcmeCustomer/Core/app.json").Should().NotBeNull();
        zip.GetEntry("AcmeCustomer/Core/AppSourceCop.json").Should().NotBeNull();
        // No fallback folder injection — what the template declares is what
        // ships, per issue #60.
        zip.GetEntry("AcmeCustomer/Core/libs/.gitkeep").Should().BeNull();
        zip.GetEntry("AcmeCustomer/Core/permissionsets/.gitkeep").Should().BeNull();
        zip.GetEntry("AcmeCustomer/Core/Translations/.gitkeep").Should().BeNull();
        // Workspace-root metadata files.
        zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace").Should().NotBeNull();
        zip.GetEntry("AcmeCustomer/README.md").Should().NotBeNull();
        zip.GetEntry("AcmeCustomer/.gitignore").Should().NotBeNull();
    }

    [Fact]
    public async Task AppSourceCop_omitted_when_no_per_extension_org_file_is_opted_in()
    {
        // No OrganizationFile at path AppSourceCop.json — the template
        // doesn't opt into anything per-extension, so the file shouldn't
        // land. The template-level AppSourceCop.Include flag is no longer
        // consulted by the generator (it's kept on the entity for the TOML
        // round-trip until the column gets dropped).
        await SeedTemplateAsync(TemplateBuilder.Default());

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        zip.GetEntry("AcmeCustomer/Core/AppSourceCop.json").Should().BeNull();
    }

    [Fact]
    public async Task AppSourceCop_content_is_admin_authored_verbatim_per_extension()
    {
        // Admin-authored content lands as-is into every extension folder
        // (mustache disabled here for the simple shape; templating still
        // works for adminstrators that need {{affix}} or {{name}}).
        const string adminAuthored = """
            {
              "mandatoryPrefix": "ACME",
              "supportedCountries": ["US", "DK"]
            }
            """;
        await using (var ctx = _db.NewContext())
        {
            ctx.OrganizationFiles.Add(new OrganizationFile
            {
                OrganizationId = ALDevToolbox.Tests.Builders.TemplateBuilder.DefaultOrganizationId,
                Path = "AppSourceCop.json",
                Content = adminAuthored,
                MustacheEnabled = false,
                Scope = ALDevToolbox.Domain.ValueObjects.OrganizationFileScope.EveryExtension,
                Ordering = 2000,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        await SeedTemplateAsync(TemplateBuilder.Default());

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var content = ReadEntry(zip.GetEntry("AcmeCustomer/Core/AppSourceCop.json")!);
        content.Should().Be(adminAuthored,
            "with the OrganizationFile concept the generator no longer synthesises "
            + "AppSourceCop content from a structured column — the admin owns the JSON.");
    }

    [Fact]
    public async Task A_customer_name_outside_ascii_still_gets_a_folder_and_a_workspace_file()
    {
        // The name a Danish consultant actually types. It used to be refused;
        // now it is transliterated into the folder and the .code-workspace file
        // name, and kept as typed everywhere a person reads it.
        // See .design/customer-naming.md.
        await SeedTemplateAsync(TemplateBuilder.Default());

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan(workspaceName: "Jørgensen Møbler"));

        zip.GetEntry("JorgensenMobler/JorgensenMobler.code-workspace").Should().NotBeNull();
        zip.GetEntry("JorgensenMobler/Core/app.json").Should().NotBeNull();
        zip.Entries.Should().OnlyContain(e => !e.FullName.Contains('ø'),
            "nothing in the archive is named with a character the name was transliterated out of");
    }

    [Fact]
    public async Task Optional_extension_emits_only_when_user_selected()
    {
        var template = TemplateBuilder.Default();
        // Append an optional Hotfix extension to the template's required Core.
        template.WorkspaceExtensions.Add(new WorkspaceExtension
        {
            OrganizationId = template.OrganizationId,
            Path = "Hotfix",
            NameTemplate = "{{extension_prefix}} Hotfix",
            Required = false,
            Ordering = 1,
        });
        await SeedTemplateAsync(template);

        // Without selection: only Core ships.
        using (var zipDefault = await GenerateAsync(PlanBuilder.WorkspacePlan()))
        {
            zipDefault.GetEntry("AcmeCustomer/Core/app.json").Should().NotBeNull();
            zipDefault.GetEntry("AcmeCustomer/Hotfix/app.json").Should().BeNull();
        }

        // With selection: both ship.
        using var zipSelected = await GenerateAsync(
            PlanBuilder.WorkspacePlan(selectedExtensions: new[] { "Hotfix" }));
        zipSelected.GetEntry("AcmeCustomer/Core/app.json").Should().NotBeNull();
        zipSelected.GetEntry("AcmeCustomer/Hotfix/app.json").Should().NotBeNull();
    }

    [Fact]
    public async Task Module_selection_clones_extension_tree_into_workspace()
    {
        var template = TemplateBuilder.Default();
        var module = ModuleBuilder.Default("document-capture", "Document Capture")
            .WithExtensionFolder("src", ("IDocumentSink.al", "interface \"{{affix}} IDocumentSink\" { }"));
        await SeedTemplateAsync(template, module);

        using var zip = await GenerateAsync(
            PlanBuilder.WorkspacePlan(selectedModules: new[] { "document-capture" }));

        // Module's PascalCase ExtensionName ("DocumentCapture") is the folder
        // name (the admin slug `document-capture` is only used in URLs and as
        // a dep-ref target). Its declared file ships under that folder.
        var entry = zip.GetEntry("AcmeCustomer/DocumentCapture/src/IDocumentSink.al");
        entry.Should().NotBeNull();
        ReadEntry(entry!).Should().Contain("interface \"ACME IDocumentSink\"");

        // Module clone gets an implicit dependency on the required Core extension.
        var moduleAppJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("AcmeCustomer/DocumentCapture/app.json")!));
        var deps = moduleAppJson.RootElement.GetProperty("dependencies").EnumerateArray().ToList();
        deps.Should().Contain(d => d.GetProperty("name").GetString() == "ACME Core");
    }

    [Fact]
    public async Task Recursive_folder_tree_emits_files_at_correct_depth()
    {
        var template = TemplateBuilder.Default()
            .WithCoreFolder("src/codeunits", ("AppInstall.Codeunit.al", "codeunit 90000 \"{{affix}} App Install\" { Subtype = Install; }"));
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var entry = zip.GetEntry("AcmeCustomer/Core/src/codeunits/AppInstall.Codeunit.al");
        entry.Should().NotBeNull();
        ReadEntry(entry!).Should().Contain("ACME App Install");
    }

    [Fact]
    public async Task Example_files_skipped_when_include_examples_is_false()
    {
        var template = TemplateBuilder.Default()
            .WithCoreFolder("src",
                ("Real.al", "codeunit Real {}"),
                ("Example.al", "codeunit Example {}"));
        // Flag the second file as an example. The builder's WithCoreFolder
        // doesn't expose IsExample; reach into the tree directly.
        var core = template.WorkspaceExtensions.Single(e => e.Path == "Core");
        var folder = core.Folders.Single(f => f.Path == "src");
        folder.Files.Single(f => f.Path == "Example.al").IsExample = true;
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan(includeExamples: false));

        zip.GetEntry("AcmeCustomer/Core/src/Real.al").Should().NotBeNull();
        zip.GetEntry("AcmeCustomer/Core/src/Example.al").Should().BeNull();
    }

    [Fact]
    public async Task Affix_type_none_substitutes_to_empty_string()
    {
        var template = TemplateBuilder.Default();
        template.Defaults.AffixType = AffixType.None;
        template.Defaults.Affix = "ignored";
        template.WorkspaceExtensions.Single().NameTemplate = "Core";
        template.WithCoreFolder("src", ("Sample.al", "codeunit \"{{affix}} Sample\" {}"));
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var content = ReadEntry(zip.GetEntry("AcmeCustomer/Core/src/Sample.al")!);
        // AffixType.None ⇒ {{affix}} collapses to the empty string regardless
        // of the configured affix value.
        content.Should().Be("codeunit \" Sample\" {}");
    }

    [Fact]
    public async Task Extension_prefix_substitutes_into_extension_name_and_file_content()
    {
        var template = TemplateBuilder.Default()
            .WithCoreFolder("src", ("Marker.al", "// brand={{extension_prefix}}"));
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(
            PlanBuilder.WorkspacePlan(extensionPrefix: "CRO"));

        // Extension name rendered through {{extension_prefix}}.
        var appJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("AcmeCustomer/Core/app.json")!));
        appJson.RootElement.GetProperty("name").GetString().Should().Be("CRO Core");
        // {{extension_prefix}} substitutes in file content too.
        ReadEntry(zip.GetEntry("AcmeCustomer/Core/src/Marker.al")!)
            .Should().Be("// brand=CRO");
    }

    [Fact]
    public async Task Extension_dependency_by_path_resolves_to_generated_guid()
    {
        var template = TemplateBuilder.Default();
        // Add a second required extension that depends on Core by stable path.
        var hotfix = new WorkspaceExtension
        {
            OrganizationId = template.OrganizationId,
            Path = "Hotfix",
            NameTemplate = "{{extension_prefix}} Hotfix",
            Required = true,
            Ordering = 1,
        };
        hotfix.Dependencies.Add(new WorkspaceExtensionDependency
        {
            OrganizationId = template.OrganizationId,
            RefExtensionPath = "Core",
            Ordering = 0,
        });
        template.WorkspaceExtensions.Add(hotfix);
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var coreId = ReadId(zip.GetEntry("AcmeCustomer/Core/app.json")!);
        var hotfixAppJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("AcmeCustomer/Hotfix/app.json")!));
        var deps = hotfixAppJson.RootElement.GetProperty("dependencies").EnumerateArray().ToList();
        deps.Should().HaveCount(1);
        deps[0].GetProperty("id").GetString().Should().Be(coreId);
        deps[0].GetProperty("name").GetString().Should().Be("ACME Core");
    }

    [Fact]
    public async Task Literal_dependency_emits_verbatim()
    {
        var template = TemplateBuilder.Default();
        var core = template.WorkspaceExtensions.Single();
        core.Dependencies.Add(new WorkspaceExtensionDependency
        {
            OrganizationId = template.OrganizationId,
            LitId = "63ca2fa4-4f03-4f2b-a480-172fef340d3f",
            LitName = "System Application",
            LitPublisher = "Microsoft",
            LitVersion = "27.0.0.0",
            Ordering = 0,
        });
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var appJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("AcmeCustomer/Core/app.json")!));
        var dep = appJson.RootElement.GetProperty("dependencies").EnumerateArray().Single();
        dep.GetProperty("id").GetString().Should().Be("63ca2fa4-4f03-4f2b-a480-172fef340d3f");
        dep.GetProperty("name").GetString().Should().Be("System Application");
        dep.GetProperty("publisher").GetString().Should().Be("Microsoft");
        dep.GetProperty("version").GetString().Should().Be("27.0.0.0");
    }

    [Fact]
    public async Task App_json_is_emitted_in_microsoft_two_space_pretty_printed_style()
    {
        // The canonical template interpolates {{dependencies_array}} and
        // {{id_ranges_array}} as compact single-line JSON; the generator
        // re-serialises the whole app.json so those land as pretty-printed,
        // 2-space-indented arrays matching Microsoft's own app.json style.
        var template = TemplateBuilder.Default();
        template.WorkspaceExtensions.Single().Dependencies.Add(new WorkspaceExtensionDependency
        {
            OrganizationId = template.OrganizationId,
            LitId = "63ca2fa4-4f03-4f2b-a480-172fef340d3f",
            LitName = "System Application",
            LitPublisher = "Microsoft",
            LitVersion = "27.0.0.0",
            Ordering = 0,
        });
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());
        var content = ReadEntry(zip.GetEntry("AcmeCustomer/Core/app.json")!);

        // Top-level keys indent two spaces (not the canonical template's four).
        content.Should().Contain("\n  \"id\":");
        // dependencies + idRanges are pretty-printed arrays, not the compact
        // single-line interpolation.
        content.Should().Contain(
            "\n  \"dependencies\": [\n    {\n      \"id\": \"63ca2fa4-4f03-4f2b-a480-172fef340d3f\",");
        content.Should().Contain("\n  \"idRanges\": [\n    {\n      \"from\": 90000,");
        // …and it's still valid JSON with the expected single dependency.
        JsonDocument.Parse(content).RootElement.GetProperty("dependencies")
            .EnumerateArray().Should().ContainSingle();
    }

    [Fact]
    public async Task App_json_free_text_is_not_over_escaped()
    {
        // The relaxed encoder keeps ampersands/angle brackets literal so a
        // brief like "Acme & Co" doesn't render as "Acme & Co".
        var template = TemplateBuilder.Default();
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan(brief: "Acme & Co <reporting>"));
        var content = ReadEntry(zip.GetEntry("AcmeCustomer/Core/app.json")!);

        content.Should().Contain("\"brief\": \"Acme & Co <reporting>\"");
        content.Should().NotContain("\\u0026");
    }

    [Fact]
    public async Task Per_extension_id_range_override_takes_precedence_over_template_defaults()
    {
        var template = TemplateBuilder.Default();
        var core = template.WorkspaceExtensions.Single();
        core.IdRangeFrom = 70000;
        core.IdRangeTo = 70999;
        await SeedTemplateAsync(template);

        // Plan's CoreIdRange is the usual 90000..90999 but the extension
        // override should win.
        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var appJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("AcmeCustomer/Core/app.json")!));
        var range = appJson.RootElement.GetProperty("idRanges")[0];
        range.GetProperty("from").GetInt32().Should().Be(70000);
        range.GetProperty("to").GetInt32().Should().Be(70999);
    }

    [Fact]
    public async Task Module_id_range_uses_size_override_and_advances_the_cursor()
    {
        var template = TemplateBuilder.Default();
        var wide = ModuleBuilder.Default("wide", "Wide", idRangeSize: 500);
        var follow = ModuleBuilder.Default("follow", "Follow");
        await SeedTemplateAsync(template, wide, follow);

        using var zip = await GenerateAsync(
            PlanBuilder.WorkspacePlan(selectedModules: new[] { "wide", "follow" }));

        // wide gets 91000..91499 (500 wide), follow gets 91500..91699 (default 200).
        // ModuleBuilder backfills ExtensionName from name ("Wide" / "Follow").
        ReadIdRange(zip, "AcmeCustomer/Wide/app.json").Should().Be((91000, 91499));
        ReadIdRange(zip, "AcmeCustomer/Follow/app.json").Should().Be((91500, 91699));
    }

    [Fact]
    public async Task Auto_allocated_extension_takes_a_module_slice()
    {
        var template = TemplateBuilder.Default();
        // First extension keeps Core's range from the plan (auto).
        // Second extension has no explicit range and no module → falls onto the
        // template's module_id_range_start cursor.
        template.WorkspaceExtensions.Add(new WorkspaceExtension
        {
            OrganizationId = template.OrganizationId,
            Path = "Auto",
            NameTemplate = "Auto",
            Required = true,
            Ordering = 1,
        });
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        ReadIdRange(zip, "AcmeCustomer/Core/app.json").Should().Be((90000, 90999));
        ReadIdRange(zip, "AcmeCustomer/Auto/app.json").Should().Be((91000, 91199));
    }

    [Fact]
    public async Task Publisher_resolves_to_org_configuration_default_over_template_default()
    {
        // {{publisher}} is documented as "Organisation publisher from the
        // configuration defaults" — the value an admin sets at
        // /admin/configuration/defaults (OrganizationSettings.DefaultPublisher),
        // not the template's own defaults_json.publisher. A populated org
        // setting wins. Regression guard for the publisher going missing from
        // generated app.json after it moved off the workspace form onto org
        // settings (the standalone flow already sourced it from there).
        var template = TemplateBuilder.Default();
        template.Defaults.Publisher = "Template Default Publisher";
        await SeedTemplateAsync(template);
        await SetOrgPublisherAsync("Acme Software A/S");

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        ReadPublisher(zip, "AcmeCustomer/Core/app.json").Should().Be("Acme Software A/S",
            "the org configuration default publisher should win over the template default");
    }

    [Fact]
    public async Task Publisher_falls_back_to_template_default_when_org_default_is_blank()
    {
        // A fresh org whose settings row carries no publisher yet still renders
        // a sensible {{publisher}} from the template default rather than an
        // empty string — the fallback the original code relied on.
        var template = TemplateBuilder.Default();
        template.Defaults.Publisher = "Template Default Publisher";
        await SeedTemplateAsync(template);
        // No OrganizationSettings row → DefaultPublisher is blank.

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        ReadPublisher(zip, "AcmeCustomer/Core/app.json").Should().Be("Template Default Publisher",
            "a blank org default should fall back to the template default publisher");
    }

    [Fact]
    public async Task Overlapping_id_ranges_surface_a_field_keyed_validation_error()
    {
        // Two extensions with explicit overlapping ranges.
        var template = TemplateBuilder.Default();
        template.WorkspaceExtensions.Single().IdRangeFrom = 70000;
        template.WorkspaceExtensions.Single().IdRangeTo = 70999;
        template.WorkspaceExtensions.Add(new WorkspaceExtension
        {
            OrganizationId = template.OrganizationId,
            Path = "Clash",
            NameTemplate = "Clash",
            Required = true,
            Ordering = 1,
            IdRangeFrom = 70500,
            IdRangeTo = 71499,
        });
        await SeedTemplateAsync(template);

        var service = NewService();
        var act = () => service.GenerateWorkspaceAsync(PlanBuilder.WorkspacePlan());

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Keys.Should().Contain(k => k.Contains("IdRange"));
    }

    [Fact]
    public async Task The_folder_and_workspace_file_follow_the_organisations_folder_style()
    {
        await SetNamingAsync(folderStyle: NamingStyle.Lowercase);
        await SeedTemplateAsync(TemplateBuilder.Default());

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan(workspaceName: "Jørgensen Møbler"));

        // Transliterated, then joined in the organisation's style. The
        // extension folders inside keep the paths the template declares (#757).
        zip.GetEntry("jorgensenmobler/Core/app.json").Should().NotBeNull();
        zip.GetEntry("jorgensenmobler/jorgensenmobler.code-workspace").Should().NotBeNull();
    }

    [Fact]
    public async Task A_fixed_organisation_prefix_beats_the_one_the_caller_passed()
    {
        await SetNamingAsync(prefixMode: ExtensionPrefixMode.Fixed, prefix: "PARTNER");
        await SeedTemplateAsync(TemplateBuilder.Default());

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan(extensionPrefix: "TYPED"));

        var appJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("AcmeCustomer/Core/app.json")!));
        appJson.RootElement.GetProperty("name").GetString().Should().Be("PARTNER Core");
    }

    [Fact]
    public async Task With_no_prefix_convention_the_short_name_names_the_extensions()
    {
        await SetNamingAsync(prefixMode: ExtensionPrefixMode.Hidden);
        await SeedTemplateAsync(TemplateBuilder.Default());

        using var zip = await GenerateAsync(
            PlanBuilder.WorkspacePlan(shortName: "JM", extensionPrefix: "TYPED"));

        var appJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("AcmeCustomer/Core/app.json")!));
        appJson.RootElement.GetProperty("name").GetString().Should().Be("JM Core");
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

    // ===== Empty root folders =====

    [Fact]
    public async Task Declared_root_folders_ship_as_empty_directories()
    {
        var template = TemplateBuilder.Default();
        template.RootFolders.Add(new RuntimeTemplateRootFolder
        {
            OrganizationId = TemplateBuilder.DefaultOrganizationId,
            Path = ".alpackages",
            Ordering = 0,
        });
        template.RootFolders.Add(new RuntimeTemplateRootFolder
        {
            OrganizationId = TemplateBuilder.DefaultOrganizationId,
            Path = "docs/decisions",
            Ordering = 1,
        });
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        // A ZIP carries no empty directory, so the placeholder is what makes
        // the folder exist on disk and survive a git commit.
        zip.GetEntry("AcmeCustomer/.alpackages/.gitkeep").Should().NotBeNull();
        zip.GetEntry("AcmeCustomer/docs/decisions/.gitkeep").Should().NotBeNull();
    }

    [Fact]
    public async Task Root_folder_that_an_included_file_already_fills_gets_no_placeholder()
    {
        // SeedTemplateAsync opts the template into every org file the fixture
        // seeds, which includes the ruleset at .assets/rulesets/... — so
        // .assets is in the ZIP with real content and a placeholder beside it
        // would be litter.
        var template = TemplateBuilder.Default();
        template.RootFolders.Add(new RuntimeTemplateRootFolder
        {
            OrganizationId = TemplateBuilder.DefaultOrganizationId,
            Path = ".assets",
            Ordering = 0,
        });
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        zip.GetEntry("AcmeCustomer/.assets/rulesets/Company.ruleset.json").Should().NotBeNull();
        zip.GetEntry("AcmeCustomer/.assets/.gitkeep").Should().BeNull();
    }

    [Fact]
    public async Task Root_folders_get_no_code_workspace_entry_of_their_own()
    {
        // .alpackages holds no app.json and already shows under the
        // workspace-root entry, so it is not listed separately.
        var template = TemplateBuilder.Default();
        template.RootFolders.Add(new RuntimeTemplateRootFolder
        {
            OrganizationId = TemplateBuilder.DefaultOrganizationId,
            Path = ".alpackages",
            Ordering = 0,
        });
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var entry = zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace");
        entry.Should().NotBeNull();
        using var reader = new StreamReader(entry!.Open());
        using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
        var folders = doc.RootElement.GetProperty("folders")
            .EnumerateArray()
            .Select(f => f.GetProperty("path").GetString())
            .ToList();
        folders.Should().Equal("Core", ".");
    }

    [Fact]
    public async Task Template_with_no_root_folders_generates_exactly_as_before()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        zip.Entries.Should().NotContain(e => e.FullName.EndsWith("/.alpackages/.gitkeep", StringComparison.Ordinal));
    }

    private async Task SeedTemplateAsync(RuntimeTemplate template, params Module[] modules)
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(template);
        if (modules.Length > 0) ctx.Modules.AddRange(modules);
        await ctx.SaveChangesAsync();

        // The MovePlatformFilesToOrgFiles migration backfills the join only
        // for templates that existed at migration time. Tests create their
        // template here, after the migration ran — preserve the legacy
        // expectation that `.gitignore`, the shared ruleset, and README.md
        // land at the workspace root by joining the new template to every
        // org file the fixture's Default org has.
        var orgFileIds = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            ctx.OrganizationFiles
                .Where(f => f.OrganizationId == template.OrganizationId)
                .OrderBy(f => f.Ordering)
                .Select(f => f.Id));
        for (var i = 0; i < orgFileIds.Count; i++)
        {
            ctx.Set<ALDevToolbox.Domain.Entities.RuntimeTemplateIncludedFile>().Add(
                new ALDevToolbox.Domain.Entities.RuntimeTemplateIncludedFile
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

    private async Task<ZipArchive> GenerateAsync(ProjectPlan plan)
    {
        var archive = await NewService().GenerateWorkspaceAsync(plan);
        return new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: false);
    }

    /// <summary>
    /// Writes the organisation's naming block (#757). Separate from
    /// <see cref="SetOrgPublisherAsync"/> because a test needs one or the
    /// other, and the settings row is unique per organisation.
    /// </summary>
    private async Task SetNamingAsync(
        NamingStyle folderStyle = NamingStyle.PascalCase,
        ExtensionPrefixMode prefixMode = ExtensionPrefixMode.PerWorkspace,
        string? prefix = null)
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = TemplateBuilder.DefaultOrganizationId,
            NamingFolderStyle = folderStyle,
            ExtensionPrefixMode = prefixMode,
            ExtensionPrefix = prefix,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task SetOrgPublisherAsync(string publisher)
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = TemplateBuilder.DefaultOrganizationId,
            DefaultPublisher = publisher,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private static string ReadPublisher(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidOperationException($"ZIP entry '{path}' not found.");
        return JsonDocument.Parse(ReadEntry(entry)).RootElement.GetProperty("publisher").GetString()!;
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static string ReadId(ZipArchiveEntry entry) =>
        JsonDocument.Parse(ReadEntry(entry)).RootElement.GetProperty("id").GetString()!;

    private static (int From, int To) ReadIdRange(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidOperationException($"ZIP entry '{path}' not found.");
        var doc = JsonDocument.Parse(ReadEntry(entry));
        var range = doc.RootElement.GetProperty("idRanges")[0];
        return (range.GetProperty("from").GetInt32(), range.GetProperty("to").GetInt32());
    }
}
