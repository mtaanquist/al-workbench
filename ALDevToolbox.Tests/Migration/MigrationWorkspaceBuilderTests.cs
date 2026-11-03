using System.Diagnostics;
using System.Text.Json.Nodes;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Migration;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using static ALDevToolbox.Tests.Migration.MigrationTestFiles;

namespace ALDevToolbox.Tests.Migration;

/// <summary>
/// The workspace a migration builds (#1700): the template's files at the root,
/// each source extension in its own folder with its app.json rebuilt on the
/// template's, the workspace files pointed at those folders, and everything
/// outside the extensions or ignored by the template's .gitignore left behind
/// and listed.
/// </summary>
public sealed class MigrationWorkspaceBuilderTests : IDisposable
{
    private const string BaseId = "11111111-1111-1111-1111-111111111111";
    private const string SalesId = "22222222-2222-2222-2222-222222222222";
    private const string SalesTestId = "33333333-3333-3333-3333-333333333333";

    private readonly TestDb _db = new();
    private readonly MigrationTempDir _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Builds_the_template_around_the_moved_extensions_and_lists_what_stays_behind()
    {
        if (!GitAvailable()) return;
        await MigrationTestServices.SeedTemplateAsync(_db);

        // A single-extension repository: the extension is the repository.
        var single = _temp.Sub("cronus-base");
        WriteApp(single, BaseId, "CRONUS Base");
        WriteFile(Path.Combine(single, "src", "Base.Codeunit.al"), "codeunit 50100 Base { }");
        WriteFile(Path.Combine(single, "azure-pipelines.yml"), "trigger: none");
        WriteFile(Path.Combine(single, ".vscode", "settings.json"), "{}");
        WriteFile(Path.Combine(single, "build", "CRONUS_CRONUS Base_1.0.0.0.app"), "compiled");
        WriteFile(Path.Combine(single, "Translations", "CRONUS Base.g.xlf"), "<xliff/>");

        // A workspace repository with an app and its test app nested inside it.
        var mono = _temp.Sub("cronus-sales");
        WriteApp(Path.Combine(mono, "Sales"), SalesId, "CRONUS Sales", dependencies: (BaseId, "CRONUS Base", "CRONUS"));
        WriteFile(Path.Combine(mono, "Sales", "src", "Sales.Codeunit.al"), "codeunit 50200 Sales { }");
        WriteApp(Path.Combine(mono, "Sales", "Test"), SalesTestId, "CRONUS Sales Test");
        WriteFile(Path.Combine(mono, "Sales", "Test", "SalesTest.Codeunit.al"), "codeunit 50300 SalesTest { }");
        WriteFile(Path.Combine(mono, "README.md"), "# Old readme");
        WriteFile(Path.Combine(mono, "scripts", "build.ps1"), "Write-Host");
        WriteFile(Path.Combine(mono, "scripts", "deploy.ps1"), "Write-Host");
        WriteFile(Path.Combine(mono, "cronus.code-workspace"), "{}");

        var sources = new[]
        {
            new ClonedSource("cronus-base", single, "https://dev.azure.com/cronus/Sales/_git/cronus-base"),
            new ClonedSource("cronus-sales", mono, "https://dev.azure.com/cronus/Sales/_git/cronus-sales"),
        };
        var analysis = MigrationSourceAnalyzer.Analyze(sources);
        analysis.CanMigrate.Should().BeTrue();
        var destination = Path.Combine(_temp.Path, "out");

        MigrationWorkspace workspace;
        await using (var ctx = _db.NewContext())
        {
            workspace = await MigrationTestServices.NewBuilder(_db, ctx).BuildAsync(
                MigrationTestServices.Target(), sources, analysis,
                new Dictionary<string, string> { ["cronus-base"] = "abc1234", ["cronus-sales"] = "def5678" },
                destination, CancellationToken.None, new AzureDevOpsProject("cronus", "Sales"));
        }

        Files(destination).Should().BeEquivalentTo(
            ".gitignore",
            "ARCHIVE.md",
            "README.md",
            ".assets/rulesets/Company.ruleset.json",
            "CRONUSAS.code-workspace",
            "workspace.aldt.toml",
            "cronus-base/app.json",
            "cronus-base/.vscode/settings.json",
            "cronus-base/src/Base.Codeunit.al",
            "cronus-base/Translations/CRONUS Base.g.xlf",
            "Sales/app.json",
            "Sales/src/Sales.Codeunit.al",
            "Test/app.json",
            "Test/SalesTest.Codeunit.al");
        workspace.FileCount.Should().Be(14);
        workspace.CanMigrate.Should().BeTrue();
        workspace.TemplateFolders.Should().BeEmpty("the template's own Core is only added when asked for");
        workspace.RootFiles.Should().Contain([".gitignore", "README.md", "CRONUSAS.code-workspace", "workspace.aldt.toml"]);
        File.ReadAllText(Path.Combine(destination, "README.md")).Should().NotContain("Old readme");
        workspace.RootFiles.Should().Contain(MigrationWorkspaceBuilder.ArchiveFile);
        File.ReadAllText(Path.Combine(destination, MigrationWorkspaceBuilder.ArchiveFile)).Should()
            .Contain("[cronus/Sales](https://dev.azure.com/cronus/Sales)")
            .And.Contain("| [cronus-base](https://dev.azure.com/cronus/Sales/_git/cronus-base) | [abc1234](https://dev.azure.com/cronus/Sales/_git/cronus-base/commit/abc1234) |")
            .And.Contain("[cronus-sales](https://dev.azure.com/cronus/Sales/_git/cronus-sales)");

        workspace.LeftBehind.Select(l => (l.RepositoryName, l.Path, l.Reason, l.FileCount)).Should().BeEquivalentTo(new[]
        {
            ("cronus-base", "azure-pipelines.yml", MigrationLeftBehindReason.NotInExtension, 1),
            ("cronus-base", "build/", MigrationLeftBehindReason.IgnoredByGitignore, 1),
            ("cronus-sales", "README.md", MigrationLeftBehindReason.ReplacedByTemplate, 1),
            ("cronus-sales", "cronus.code-workspace", MigrationLeftBehindReason.ReplacedByTemplate, 1),
            ("cronus-sales", "scripts/", MigrationLeftBehindReason.NotInExtension, 2),
        });

        var sales = JsonNode.Parse(File.ReadAllText(Path.Combine(destination, "Sales", "app.json")))!;
        sales["id"]!.GetValue<string>().Should().Be(SalesId);
        sales["dependencies"]![0]!["id"]!.GetValue<string>().Should().Be(BaseId);
        sales["application"]!.GetValue<string>().Should().Be("24.0.0.0", "the template's application is filled in");
        workspace.AppJson.Select(a => a.Folder).Should().Equal("cronus-base", "Sales", "Test");
        workspace.AppJson.Single(a => a.Folder == "Sales").Changes.Should().Contain(c => c.Property == "application" && c.Before == null);

        var codeWorkspace = JsonNode.Parse(File.ReadAllText(Path.Combine(destination, "CRONUSAS.code-workspace")))!;
        codeWorkspace["folders"]!.AsArray().Select(f => f!["path"]!.GetValue<string>())
            .Should().Equal("cronus-base", "Sales", "Test", ".");
        codeWorkspace["settings"]!["files.exclude"]!.AsObject().Select(p => p.Key)
            .Should().Contain(["cronus-base", "Sales", "Test"]).And.NotContain("Core");

        await using (var ctx = _db.NewContext())
        {
            var config = await new WorkspaceConfigService(ctx).ParseAsync(File.ReadAllText(Path.Combine(destination, "workspace.aldt.toml")));
            config.Extensions.Select(e => (e.Kind, e.Folder, e.Id.ToString(), e.SourceRepository, e.SourceCommit)).Should().Equal(
                (WorkspaceExtensionIdentity.MigratedKind, "cronus-base", BaseId, "cronus-base", "abc1234"),
                (WorkspaceExtensionIdentity.MigratedKind, "Sales", SalesId, "cronus-sales/Sales", "def5678"),
                (WorkspaceExtensionIdentity.MigratedKind, "Test", SalesTestId, "cronus-sales/Sales/Test", "def5678"));
            config.Workspace!.WorkspaceName.Should().Be("CRONUS A/S");
        }
    }

    [Fact]
    public async Task The_templates_own_extensions_come_along_when_asked_and_a_clash_with_them_blocks()
    {
        await MigrationTestServices.SeedTemplateAsync(_db);
        var repo = _temp.Sub("cronus-core");
        WriteApp(Path.Combine(repo, "Core"), BaseId, "CRONUS Core");
        WriteApp(Path.Combine(repo, "Sales"), SalesId, "CRONUS Sales");
        var sources = new[] { new ClonedSource("cronus-core", repo) };
        var analysis = MigrationSourceAnalyzer.Analyze(sources);
        var destination = Path.Combine(_temp.Path, "out");

        MigrationWorkspace workspace;
        await using (var ctx = _db.NewContext())
        {
            workspace = await MigrationTestServices.NewBuilder(_db, ctx).BuildAsync(
                MigrationTestServices.Target(addTemplateExtensions: true), sources, analysis,
                new Dictionary<string, string>(), destination, CancellationToken.None);
        }

        workspace.TemplateFolders.Should().Equal("Core");
        workspace.CanMigrate.Should().BeFalse();
        workspace.Findings.Should().ContainSingle(f => f.Severity == MigrationFindingSeverity.Blocking && f.Message.Contains("\"Core\""));
    }

    private static List<string> Files(string root) => Directory
        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
        .ToList();

    private static bool GitAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
