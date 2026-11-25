using ALDevToolbox.Services.Migration;
using AwesomeAssertions;
using static ALDevToolbox.Tests.Migration.MigrationTestFiles;

namespace ALDevToolbox.Tests.Migration;

/// <summary>
/// What a Migrate repositories preview reads out of cloned repositories (#1699):
/// every folder with an app.json is an extension (tests included), each keeps
/// its folder name and one at a repository's root takes the repository's name,
/// and what would stop the migration is reported as a problem.
/// </summary>
public sealed class MigrationSourceAnalyzerTests : IDisposable
{
    private readonly MigrationTempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private const string BaseId = "11111111-1111-1111-1111-111111111111";
    private const string PayrollId = "22222222-2222-2222-2222-222222222222";
    private const string TestId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public void A_single_extension_repository_takes_the_repository_name_and_a_workspace_keeps_its_folders()
    {
        var single = _temp.Sub("single");
        WriteApp(single, BaseId, "CRONUS Base");
        var workspace = _temp.Sub("workspace");
        WriteApp(Path.Combine(workspace, "Payroll"), PayrollId, "CRONUS Payroll", dependencies: (BaseId, "CRONUS Base", "CRONUS"));
        WriteApp(Path.Combine(workspace, "Payroll Tests"), TestId, "CRONUS Payroll Tests");

        var result = MigrationSourceAnalyzer.Analyze([new("cronus-base", single), new("cronus-payroll", workspace)]);

        result.Extensions.Select(e => (e.Folder, e.RepositoryName, e.SourcePath)).Should().Equal(
            ("cronus-base", "cronus-base", ""),
            ("Payroll", "cronus-payroll", "Payroll"),
            ("Payroll Tests", "cronus-payroll", "Payroll Tests"));
        result.Extensions[0].Should().BeEquivalentTo(new { Id = BaseId, Name = "CRONUS Base", Publisher = "CRONUS", Version = "1.0.0.0" });
        result.Findings.Should().BeEmpty("the dependency on CRONUS Base is part of the run");
        result.CanMigrate.Should().BeTrue();
    }

    [Fact]
    public void Tooling_folders_are_not_searched_for_extensions()
    {
        var repo = _temp.Sub("repo");
        WriteApp(Path.Combine(repo, "App"), BaseId, "CRONUS Base");
        WriteApp(Path.Combine(repo, "App", ".alpackages", "ms"), PayrollId, "Not an extension");
        WriteApp(Path.Combine(repo, ".vscode"), TestId, "Not one either");

        var result = MigrationSourceAnalyzer.Analyze([new("repo", repo)]);

        result.Extensions.Select(e => e.Folder).Should().Equal("App");
    }

    [Fact]
    public void Two_extensions_that_would_share_a_folder_block_the_migration()
    {
        var one = _temp.Sub("one");
        WriteApp(Path.Combine(one, "App"), BaseId, "CRONUS Base");
        var two = _temp.Sub("two");
        WriteApp(Path.Combine(two, "src", "app"), PayrollId, "CRONUS Payroll");

        var result = MigrationSourceAnalyzer.Analyze([new("one", one), new("two", two)]);

        result.CanMigrate.Should().BeFalse();
        result.Findings.Should().ContainSingle(f => f.Severity == MigrationFindingSeverity.Blocking)
            .Which.Message.Should().Contain("CRONUS Base (one/App)").And.Contain("CRONUS Payroll (two/src/app)")
            .And.Contain("into the folder \"App\"");
    }

    [Fact]
    public void The_same_app_id_twice_blocks_the_migration_whatever_its_braces_and_case()
    {
        var one = _temp.Sub("one");
        WriteApp(one, BaseId, "CRONUS Base");
        var two = _temp.Sub("two");
        WriteApp(two, "{" + BaseId.ToUpperInvariant() + "}", "CRONUS Base copy");

        var result = MigrationSourceAnalyzer.Analyze([new("one", one), new("two", two)]);

        result.Findings.Should().ContainSingle()
            .Which.Message.Should().Contain("have the same app id");
        result.CanMigrate.Should().BeFalse();
    }

    [Fact]
    public void An_unreadable_app_json_or_one_without_an_id_blocks_the_migration()
    {
        var broken = _temp.Sub("broken");
        WriteFile(Path.Combine(broken, "app.json"), "{ not json");
        var noId = _temp.Sub("noid");
        WriteFile(Path.Combine(noId, "App", "app.json"), """{ "name": "Nameless" }""");

        var result = MigrationSourceAnalyzer.Analyze([new("broken", broken), new("noid", noId)]);

        result.Extensions.Should().BeEmpty();
        result.Findings.Select(f => (f.Severity, f.RepositoryName)).Should().BeEquivalentTo(new[]
        {
            (MigrationFindingSeverity.Blocking, "broken"),
            (MigrationFindingSeverity.Blocking, "noid"),
        });
        result.Findings.Single(f => f.RepositoryName == "noid").Message.Should().Contain("in App has no id");
    }

    [Fact]
    public void A_repository_without_an_extension_is_a_warning()
    {
        var empty = _temp.Sub("docs");
        WriteFile(Path.Combine(empty, "README.md"));
        var app = _temp.Sub("app");
        WriteApp(app, BaseId, "CRONUS Base");

        var result = MigrationSourceAnalyzer.Analyze([new("docs", empty), new("app", app)]);

        result.Findings.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Severity = MigrationFindingSeverity.Warning, RepositoryName = "docs" });
        result.CanMigrate.Should().BeTrue();
    }

    [Fact]
    public void Nothing_to_move_cannot_be_migrated()
    {
        var empty = _temp.Sub("docs");
        WriteFile(Path.Combine(empty, "README.md"));

        MigrationSourceAnalyzer.Analyze([new("docs", empty)]).CanMigrate.Should().BeFalse();
    }

    [Fact]
    public void A_dependency_on_an_own_app_left_out_of_the_run_is_a_warning_but_other_publishers_are_not()
    {
        var repo = _temp.Sub("payroll");
        WriteApp(repo, PayrollId, "CRONUS Payroll", dependencies:
        [
            (BaseId, "CRONUS Base", "CRONUS"),
            ("44444444-4444-4444-4444-444444444444", "Document Capture", "Continia Software"),
            ("437dbf0e-84ff-417a-965d-ed2bb9650972", "Base Application", "Microsoft"),
        ]);

        var result = MigrationSourceAnalyzer.Analyze([new("payroll", repo)]);

        result.Findings.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Severity = MigrationFindingSeverity.Warning, RepositoryName = "payroll" });
        result.Findings[0].Message.Should().Contain("CRONUS Payroll depends on CRONUS Base by CRONUS");
    }

    [Fact]
    public void A_file_over_100_MB_blocks_the_migration_unless_it_is_in_alpackages()
    {
        var repo = _temp.Sub("repo");
        WriteApp(repo, BaseId, "CRONUS Base");
        SparseFile(Path.Combine(repo, "assets", "huge.bin"), MigrationSourceAnalyzer.MaxFileBytes + 1);
        SparseFile(Path.Combine(repo, ".alpackages", "Microsoft_Base.app"), MigrationSourceAnalyzer.MaxFileBytes + 1);
        SparseFile(Path.Combine(repo, "exactly-limit.bin"), MigrationSourceAnalyzer.MaxFileBytes);

        var result = MigrationSourceAnalyzer.Analyze([new("repo", repo)]);

        result.Findings.Should().ContainSingle()
            .Which.Message.Should().StartWith("assets/huge.bin is larger than 100 MB");
        result.CanMigrate.Should().BeFalse();
    }

    private static void SparseFile(string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.SetLength(length);
    }
}
