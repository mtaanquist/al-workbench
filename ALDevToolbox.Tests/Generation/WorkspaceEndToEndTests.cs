using System.IO.Compression;
using System.Text.Json;
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
/// End-to-end acceptance test for Issue #54: a realistic customer-style TOML
/// is parsed by <see cref="TemplateTomlMapper.FromToml"/>, persisted through
/// <see cref="TemplateService.CreateAsync(TemplateAuthoring, System.Threading.CancellationToken)"/>,
/// and emitted as a workspace ZIP by
/// <see cref="GenerationService.GenerateWorkspaceAsync"/>. Pins the full
/// authoring-to-output pipeline together: per-extension <c>app.json</c>,
/// idRange allocation, dependency resolution across all three reference
/// shapes, recursive folder layout, and module-clone implicit Core/Hotfix
/// deps.
/// </summary>
/// <remarks>
/// The lower-level slices (mapper round-trip, write-side reconciliation,
/// per-extension generation) have their own dedicated test files. This one
/// is deliberately fat — it asserts the pieces work together when an admin
/// pastes TOML and clicks Save.
/// </remarks>
public sealed class WorkspaceEndToEndTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// The literal TOML an admin pastes into the editor: three required
    /// extensions (Core, Hotfix with an explicit <c>extension = "Core"</c>
    /// dep, DocumentCapture with a <c>module = "continia-doc-capture"</c>
    /// dep), each with a recursive folder tree and one AL file embedded so
    /// the ZIP has content to assert against. Mirrors the shape of
    /// <see cref="ALDevToolbox.Tests.Toml.TemplateTomlMapperToleranceTests"/>
    /// but extended with files + cross-extension dependencies.
    /// </summary>
    private const string CustomerTemplateToml = """
[template]
key = "runtime-new"
runtime = "15"
name = "Customer Runtime"
core_id_range_from = 90000
core_id_range_to = 90999
module_id_range_start = 91000
module_id_range_size = 200

[defaults]
publisher = "Test Publisher"
target = "Cloud"
features = ["TranslationFile","NoImplicitWith"]
supportedLocales = ["en-US","da-DK"]
affix = "TEST"
affixType = "Prefix"
extension_prefix = "TEST"
application = "27.5.0.0"
platform = "1.0.0.0"

[[extensions]]
name = "{{extension_prefix}} Core"
path = "Core"

[[extensions.folders]]
path = "src"

[[extensions.folders.folders]]
path = "codeunits"

[[extensions.folders.folders.files]]
path = "AppInstall.Codeunit.al"
content = "codeunit 90000 \"{{affix}} App Install\"\n{\n    Subtype = Install;\n}\n"

[[extensions]]
name = "{{extension_prefix}} Hotfix"
path = "Hotfix"

[[extensions.dependencies]]
extension = "Core"

[[extensions.folders]]
path = "src"

[[extensions.folders.folders]]
path = "codeunits"

[[extensions]]
name = "{{extension_prefix}} Document Capture"
path = "DocumentCapture"

[[extensions.dependencies]]
module = "continia-doc-capture"

[[extensions.folders]]
path = "src"

[[extensions.folders.folders]]
path = "codeunits"
""";

    [Fact]
    public async Task Customer_toml_parses_persists_and_generates_a_workspace_zip_with_expected_extensions_and_deps()
    {
        await SeedContiniaModuleAsync();

        // 1) Parse the TOML the admin would paste into the editor.
        var authoring = TemplateTomlMapper.FromToml(CustomerTemplateToml, deprecated: false);

        // 2) Persist through the unified write path.
        await using (var ctx = _db.NewContext())
        {
            var service = new TemplateService(ctx, NullLogger<TemplateService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx), _db.NewQuotaGuard(ctx));
            await service.CreateAsync(authoring);
        }

        // 3) Generate against the persisted template, selecting the catalogue
        //    module so the module-key dep on DocumentCapture has a workspace
        //    clone to resolve against.
        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan(
            templateKey: "runtime-new",
            workspaceName: "Acme Customer",
            extensionPrefix: "ACME",
            selectedModules: new[] { "continia-doc-capture" }));

        // 4) Three declared extensions emit at the customer-declared paths;
        //    the selected module clones in as a fourth under its PascalCase
        //    ExtensionName (the admin slug `continia-doc-capture` is the
        //    URL/dep-ref target, not the folder name).
        foreach (var path in new[] { "Core", "Hotfix", "DocumentCapture", "ContiniaDocumentCapture" })
        {
            zip.GetEntry($"AcmeCustomer/{path}/app.json").Should().NotBeNull(
                $"the {path} extension folder should ship an app.json");
        }

        var coreJson = ParseAppJson(zip, "AcmeCustomer/Core/app.json");
        var hotfixJson = ParseAppJson(zip, "AcmeCustomer/Hotfix/app.json");
        var docCaptureJson = ParseAppJson(zip, "AcmeCustomer/DocumentCapture/app.json");
        var moduleJson = ParseAppJson(zip, "AcmeCustomer/ContiniaDocumentCapture/app.json");

        // 5) Names render through {{extension_prefix}} from the plan, not the
        //    template default — proves the plan override threads into the
        //    mustache table.
        coreJson.RootElement.GetProperty("name").GetString().Should().Be("ACME Core");
        hotfixJson.RootElement.GetProperty("name").GetString().Should().Be("ACME Hotfix");
        docCaptureJson.RootElement.GetProperty("name").GetString().Should().Be("ACME Document Capture");

        // 6) Each extension gets a distinct GUID. Core/Hotfix/DocCapture all
        //    auto-allocate (no explicit per-extension ranges in the TOML);
        //    the first one takes the plan's Core range, the rest fall onto
        //    module_id_range_start with module_id_range_size each.
        var coreId = coreJson.RootElement.GetProperty("id").GetString()!;
        var hotfixId = hotfixJson.RootElement.GetProperty("id").GetString()!;
        var docCaptureId = docCaptureJson.RootElement.GetProperty("id").GetString()!;
        var moduleId = moduleJson.RootElement.GetProperty("id").GetString()!;
        new[] { coreId, hotfixId, docCaptureId, moduleId }
            .Should().OnlyHaveUniqueItems("each emitted extension should carry its own GUID");

        ReadIdRange(coreJson).Should().Be((90000, 90999), "first extension takes the plan's Core range");
        ReadIdRange(hotfixJson).Should().Be((91000, 91199), "second extension falls onto module_id_range_start + size");
        ReadIdRange(docCaptureJson).Should().Be((91200, 91399));
        // The continia module clone advances the cursor again with the default
        // module size (200).
        ReadIdRange(moduleJson).Should().Be((91400, 91599));

        // 7) Dependency wiring — three reference shapes, three different
        //    resolution paths.

        // 7a) Hotfix → Core via [[extensions.dependencies]] extension = "Core".
        var hotfixDeps = hotfixJson.RootElement.GetProperty("dependencies").EnumerateArray().ToList();
        hotfixDeps.Should().ContainSingle()
            .Which.GetProperty("id").GetString().Should().Be(coreId,
                "the extension-ref dep should resolve to Core's freshly-allocated GUID");

        // 7b) DocumentCapture → continia-doc-capture module clone (since the
        //     module was selected, the module-key dep resolves to the clone's
        //     GUID rather than falling back to a literal).
        var docCaptureDeps = docCaptureJson.RootElement.GetProperty("dependencies").EnumerateArray().ToList();
        docCaptureDeps.Should().ContainSingle()
            .Which.GetProperty("id").GetString().Should().Be(moduleId,
                "the module-key dep should resolve to the in-workspace module clone");

        // 7c) Module clones implicitly depend on every required template
        //     extension — Core, Hotfix, DocumentCapture — so the AL compiler
        //     can see them without the template author having to spell it out.
        var moduleDepIds = moduleJson.RootElement.GetProperty("dependencies").EnumerateArray()
            .Select(d => d.GetProperty("id").GetString()).ToList();
        moduleDepIds.Should().BeEquivalentTo(new[] { coreId, hotfixId, docCaptureId });

        // 8) Folder layout + mustache substitution in file content. The Core
        //    template carries one AL file under src/codeunits; {{affix}}
        //    substitutes from defaults.affix ("TEST" here), distinct from the
        //    plan's extension_prefix ("ACME" — that drives extension names).
        var alEntry = zip.GetEntry("AcmeCustomer/Core/src/codeunits/AppInstall.Codeunit.al");
        alEntry.Should().NotBeNull();
        ReadEntry(alEntry!).Should().Contain("codeunit 90000 \"TEST App Install\"");
    }

    // ===== helpers =====

    private async Task SeedContiniaModuleAsync()
    {
        await using var ctx = _db.NewContext();
        var module = ModuleBuilder.Default("continia-doc-capture", "Continia Document Capture")
            .WithExtensionFolder("src",
                ("Adapter.Codeunit.al", "codeunit 91400 \"{{affix}} Doc Capture Adapter\" { }"));
        ctx.Modules.Add(module);
        await ctx.SaveChangesAsync();
    }

    private async Task<ZipArchive> GenerateAsync(ProjectPlan plan)
    {
        await using var ctx = _db.NewContext();
        var mustache = new ALDevToolbox.Services.Generation.MustacheRenderer(
            NullLogger<ALDevToolbox.Services.Generation.MustacheRenderer>.Instance);
        var service = new GenerationService(
            ctx,
            _db.NewOrganizationConfigService(ctx),
            new FolderTreeHydrator(ctx),
            _db.OrgContext,
            mustache,
            new ALDevToolbox.Services.Generation.WorkspaceZipBuilder(mustache, new WorkspaceConfigService(ctx)),
            NullLogger<GenerationService>.Instance);
        var archive = await service.GenerateWorkspaceAsync(plan);
        return new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: false);
    }

    private static JsonDocument ParseAppJson(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path)
            ?? throw new InvalidOperationException($"ZIP entry '{path}' not found.");
        return JsonDocument.Parse(ReadEntry(entry));
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static (int From, int To) ReadIdRange(JsonDocument appJson)
    {
        var range = appJson.RootElement.GetProperty("idRanges")[0];
        return (range.GetProperty("from").GetInt32(), range.GetProperty("to").GetInt32());
    }
}
