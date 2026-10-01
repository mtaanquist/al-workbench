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
/// Issue #61: the <c>.code-workspace</c> file's <c>settings</c> block is
/// admin-editable per organisation, with mustache substitution. The generator
/// overlays a computed <c>folders</c> array onto the admin's JSON template
/// regardless of what they pasted. These tests drive
/// <see cref="GenerationService.GenerateWorkspaceAsync"/> directly so the
/// overlay and substitution contract stays honoured under refactors.
/// </summary>
public sealed class CodeWorkspaceJsonTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Default_admin_json_produces_today_settings_block()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());
        // No SaveSettings call — the organisation_settings row stays
        // un-persisted and the service's transient fallback supplies the
        // OrganizationDefaults seed.

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var entry = zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace");
        entry.Should().NotBeNull();

        using var doc = JsonDocument.Parse(ReadEntry(entry!));
        var settings = doc.RootElement.GetProperty("settings");
        settings.GetProperty("editor.formatOnSave").GetBoolean().Should().BeTrue();
        settings.GetProperty("al.enableCodeAnalysis").GetBoolean().Should().BeTrue();
        settings.GetProperty("al.ruleSetPath").GetString()
            .Should().Be("../.assets/rulesets/Company.ruleset.json");
    }

    [Fact]
    public async Task Admin_json_substitutes_mustache_variables()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());
        await SetCodeWorkspaceJsonAsync("""
            {
              "settings": {
                "al.ruleSetPath": "../.assets/rulesets/{{publisher}}.ruleset.json",
                "al.workspace.short_name": "{{shortName}}"
              }
            }
            """);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var entry = zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace");
        using var doc = JsonDocument.Parse(ReadEntry(entry!));
        var settings = doc.RootElement.GetProperty("settings");

        // {{publisher}} resolves to the TemplateBuilder.Default() publisher
        // (the org's default publisher is empty in these tests; generation
        // pulls the publisher from the template defaults).
        settings.GetProperty("al.ruleSetPath").GetString()
            .Should().Contain("Acme");
        // {{short_name}} is the customer's abbreviated name - with no
        // abbreviation on the plan it falls back to the name as typed, spaces
        // and all. The folder name is {{workspace_folder}}'s job now.
        settings.GetProperty("al.workspace.short_name").GetString()
            .Should().Be("Acme Customer");
    }

    [Fact]
    public async Task Short_name_renders_the_abbreviation_and_workspace_folder_stays_the_folder()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());
        await SetCodeWorkspaceJsonAsync("""
            {
              "settings": {
                "al.workspace.short_name": "{{short_name}}",
                "al.workspace.folder": "{{workspace_folder}}"
              }
            }
            """);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan(shortName: "AC"));

        var entry = zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace");
        using var doc = JsonDocument.Parse(ReadEntry(entry!));
        var settings = doc.RootElement.GetProperty("settings");

        settings.GetProperty("al.workspace.short_name").GetString().Should().Be("AC");
        settings.GetProperty("al.workspace.folder").GetString().Should().Be("AcmeCustomer");
    }

    [Fact]
    public async Task Generator_overwrites_admin_supplied_folders_array()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());
        // Admin put a folders key in — the generator must replace it with the
        // computed list. Without this guarantee the workspace would refer to
        // folders that aren't in the ZIP.
        await SetCodeWorkspaceJsonAsync("""
            {
              "folders": [
                { "path": "BogusUserChoice" }
              ],
              "settings": {}
            }
            """);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var entry = zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace");
        using var doc = JsonDocument.Parse(ReadEntry(entry!));
        var paths = doc.RootElement.GetProperty("folders")
            .EnumerateArray()
            .Select(f => f.GetProperty("path").GetString())
            .ToList();
        paths.Should().Equal("Core", ".");
        paths.Should().NotContain("BogusUserChoice");
    }

    [Fact]
    public async Task Folders_end_with_the_workspace_root_and_extensions_are_excluded_from_it()
    {
        var template = TemplateBuilder.Default();
        template.WorkspaceExtensions.Add(new WorkspaceExtension
        {
            OrganizationId = template.OrganizationId,
            Path = "Hotfix",
            NameTemplate = "{{extension_prefix}} Hotfix",
            Required = false,
            Ordering = 1,
        });
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(
            PlanBuilder.WorkspacePlan(selectedExtensions: new[] { "Hotfix" }));

        using var doc = ReadCodeWorkspace(zip);
        var folders = doc.RootElement.GetProperty("folders").EnumerateArray().ToList();
        folders.Select(f => f.GetProperty("path").GetString())
            .Should().Equal("Core", "Hotfix", ".");
        // Only the root entry carries a display name.
        folders[^1].GetProperty("name").GetString().Should().Be("Workspace");
        folders[0].TryGetProperty("name", out _).Should().BeFalse();

        // Every extension folder is hidden under the root entry so it is not
        // listed twice in the explorer, and nothing else is.
        var exclude = doc.RootElement.GetProperty("settings").GetProperty("files.exclude");
        exclude.EnumerateObject().Select(p => (p.Name, p.Value.GetBoolean()))
            .Should().Equal(("Core", true), ("Hotfix", true));
    }

    [Fact]
    public async Task Files_exclude_merges_into_what_the_admin_already_excludes()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());
        await SetCodeWorkspaceJsonAsync("""
            {
              "settings": {
                "files.exclude": { "**/.git": true, "Core": false }
              }
            }
            """);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        using var doc = ReadCodeWorkspace(zip);
        var exclude = doc.RootElement.GetProperty("settings").GetProperty("files.exclude");
        exclude.GetProperty("**/.git").GetBoolean().Should().BeTrue();
        // The generator owns its extension folders' keys.
        exclude.GetProperty("Core").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Files_exclude_is_written_when_the_admin_json_has_no_settings()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());
        await SetCodeWorkspaceJsonAsync("{}");

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        using var doc = ReadCodeWorkspace(zip);
        doc.RootElement.GetProperty("settings").GetProperty("files.exclude")
            .GetProperty("Core").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Generation_throws_when_admin_json_is_not_a_json_object()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());
        // Save bypasses the form validator by writing the column directly —
        // proves the generator's own guard catches a malformed row that
        // somehow slipped past validation.
        await using (var ctx = _db.NewContext())
        {
            ctx.OrganizationSettings.Add(new OrganizationSettings
            {
                OrganizationId = TestDb.DefaultOrgId,
                DefaultPublisher = "Acme",
                DefaultIdRangeFrom = 50000,
                DefaultIdRangeTo = 50999,
                CodeWorkspaceJson = "this is not json",
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var service = NewService();
        var act = () => service.GenerateWorkspaceAsync(PlanBuilder.WorkspacePlan());

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("codeWorkspaceJson");
    }

    [Fact]
    public async Task Template_overlay_deep_merges_settings_onto_org_base()
    {
        // Org base: a baseline al.ruleSetPath. Template overlay adds a fresh
        // setting and rewrites an existing one. The merged result should keep
        // org-only keys, accept template-only keys, and prefer the template
        // on conflict — all inside the `settings` object.
        await using (var ctx = _db.NewContext())
        {
            var template = TemplateBuilder.Default();
            template.CodeWorkspaceJson = """
                {
                  "settings": {
                    "al.ruleSetPath": "../from-template.ruleset.json",
                    "al.newAnalyzer": "${X}"
                  }
                }
                """;
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
        }
        await SetCodeWorkspaceJsonAsync("""
            {
              "settings": {
                "editor.formatOnSave": true,
                "al.ruleSetPath": "../from-org.ruleset.json"
              }
            }
            """);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var entry = zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace");
        using var doc = JsonDocument.Parse(ReadEntry(entry!));
        var settings = doc.RootElement.GetProperty("settings");
        settings.GetProperty("editor.formatOnSave").GetBoolean().Should().BeTrue();
        settings.GetProperty("al.ruleSetPath").GetString()
            .Should().Be("../from-template.ruleset.json");
        settings.GetProperty("al.newAnalyzer").GetString().Should().Be("${X}");
    }

    [Fact]
    public async Task Template_overlay_replaces_non_settings_keys_wholesale()
    {
        // tasks/launch-style top-level keys aren't merged element-by-element —
        // the template wholesale replaces whatever the org had. Keeps merge
        // semantics predictable for arbitrarily-shaped values.
        await using (var ctx = _db.NewContext())
        {
            var template = TemplateBuilder.Default();
            template.CodeWorkspaceJson = """
                {
                  "tasks": { "version": "2.0.0", "tasks": [{ "label": "from-template" }] }
                }
                """;
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
        }
        await SetCodeWorkspaceJsonAsync("""
            {
              "settings": { "editor.formatOnSave": true },
              "tasks": { "version": "1.0.0", "tasks": [{ "label": "from-org" }] }
            }
            """);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var entry = zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace");
        using var doc = JsonDocument.Parse(ReadEntry(entry!));
        // settings survived (org-only).
        doc.RootElement.GetProperty("settings")
            .GetProperty("editor.formatOnSave").GetBoolean().Should().BeTrue();
        // tasks block was replaced wholesale by the template's.
        doc.RootElement.GetProperty("tasks")
            .GetProperty("version").GetString().Should().Be("2.0.0");
        doc.RootElement.GetProperty("tasks")
            .GetProperty("tasks")[0]
            .GetProperty("label").GetString().Should().Be("from-template");
    }

    [Fact]
    public async Task Template_overlay_ignores_a_folders_key_so_generator_stays_authoritative()
    {
        // Even if the template's overlay carries a folders array, the
        // generator overwrites it with the computed list. The workspace
        // must point at the extensions actually emitted to the ZIP.
        await using (var ctx = _db.NewContext())
        {
            var template = TemplateBuilder.Default();
            template.CodeWorkspaceJson = """
                {
                  "folders": [{ "path": "BogusFromTemplate" }],
                  "settings": { "al.newSetting": "${X}" }
                }
                """;
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
        }

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var entry = zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace");
        using var doc = JsonDocument.Parse(ReadEntry(entry!));
        var paths = doc.RootElement.GetProperty("folders")
            .EnumerateArray()
            .Select(f => f.GetProperty("path").GetString())
            .ToList();
        paths.Should().Equal("Core", ".");
        paths.Should().NotContain("BogusFromTemplate");
    }

    [Fact]
    public async Task Template_overlay_supports_mustache_substitution()
    {
        // Both layers run through the substitution table so a template can
        // reference the workspace context just like the org base does.
        await using (var ctx = _db.NewContext())
        {
            var template = TemplateBuilder.Default();
            template.CodeWorkspaceJson = """
                {
                  "settings": {
                    "al.workspaceShortName": "{{shortName}}"
                  }
                }
                """;
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
        }

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var entry = zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace");
        using var doc = JsonDocument.Parse(ReadEntry(entry!));
        doc.RootElement.GetProperty("settings")
            .GetProperty("al.workspaceShortName").GetString()
            .Should().Be("Acme Customer");
    }

    [Fact]
    public async Task Template_overlay_with_invalid_json_throws_with_template_keyed_error()
    {
        // Bypassing the form validator — same defensive guard the org-base
        // path has, but the error key carries `template.` so the UI can route
        // the message to the per-template field rather than the org form.
        await using (var ctx = _db.NewContext())
        {
            var template = TemplateBuilder.Default();
            template.CodeWorkspaceJson = "this is not json";
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
        }

        var service = NewService();
        var act = () => service.GenerateWorkspaceAsync(PlanBuilder.WorkspacePlan());

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("template.codeWorkspaceJson");
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

    private async Task SeedTemplateAsync(RuntimeTemplate template, params Module[] modules)
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(template);
        if (modules.Length > 0) ctx.Modules.AddRange(modules);
        await ctx.SaveChangesAsync();
    }

    private async Task SetCodeWorkspaceJsonAsync(string json)
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        await svc.SaveCodeWorkspaceJsonAsync(json);
    }

    private async Task<ZipArchive> GenerateAsync(ProjectPlan plan)
    {
        var archive = await NewService().GenerateWorkspaceAsync(plan);
        return new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: false);
    }

    private static JsonDocument ReadCodeWorkspace(ZipArchive zip) =>
        JsonDocument.Parse(ReadEntry(zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace")!));

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
