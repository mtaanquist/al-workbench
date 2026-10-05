using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Build numbers in app versions: the version a pipeline build gives an app, the
/// app.json rewrite in the build's own clone, and which builds do it at all. See
/// <c>.design/object-explorer-project-builds.md</c>, "Build numbers in app versions".
/// </summary>
public sealed class BuildVersionStampTests
{
    [Theory]
    [InlineData("28.2.0.0", 4812, "28.2.4812.0")]
    // Somebody's own bump is kept: the build number goes on top of it.
    [InlineData("28.2.5.0", 4812, "28.2.4817.0")]
    [InlineData("28.2.5.3", 4812, "28.2.4817.3")]
    // Missing parts read as 0, and the result always has four.
    [InlineData("1.0", 7, "1.0.7.0")]
    [InlineData(" 1.0.0.0 ", 7, "1.0.7.0")]
    public void The_build_number_is_added_to_the_third_part(string version, int buildNumber, string expected) =>
        BuildVersionStamp.Compute(version, buildNumber).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.0.0.0.0")]
    [InlineData("1.0.x.0")]
    [InlineData("1.-1.0.0")]
    [InlineData("1..0.0")]
    public void A_version_that_is_not_whole_numbers_is_not_numbered(string? version) =>
        BuildVersionStamp.Compute(version, 12).Should().BeNull();

    [Fact]
    public void A_sum_that_does_not_fit_in_a_version_part_is_not_numbered() =>
        BuildVersionStamp.Compute($"1.0.{int.MaxValue}.0", 1).Should().BeNull();

    [Fact]
    public void Only_the_version_changes_in_the_app_json()
    {
        const string json = """
        {
          "id": "11111111-1111-1111-1111-111111111111",
          // kept as written
          "name": "Core",
          "version": "28.2.0.0",
          "dependencies": [ { "id": "22222222-2222-2222-2222-222222222222", "name": "Base", "version": "28.2.0.0" } ],
        }
        """;

        var rewritten = BuildVersionStamp.WriteVersion(json, "28.2.4812.0");

        rewritten.Should().Be(json.Replace("\"version\": \"28.2.0.0\",", "\"version\": \"28.2.4812.0\","));
    }

    [Theory]
    [InlineData(5, ProjectBuildTrigger.Manual, BcBuildTarget.Current, true)]
    // A pull-request check and the nightly preview check are never deployed or published.
    [InlineData(null, ProjectBuildTrigger.PullRequest, BcBuildTarget.Current, false)]
    [InlineData(5, ProjectBuildTrigger.PullRequest, BcBuildTarget.Current, false)]
    [InlineData(5, ProjectBuildTrigger.PreviewCheck, BcBuildTarget.NextMinor, false)]
    [InlineData(5, ProjectBuildTrigger.Manual, BcBuildTarget.NextMajor, false)]
    // A build with no pipeline (a staged GitHub release) was never compiled here.
    [InlineData(null, ProjectBuildTrigger.Manual, BcBuildTarget.Current, false)]
    public void Only_a_pipelines_own_build_against_the_current_version_numbers_its_apps(
        int? pipelineId, string trigger, BcBuildTarget target, bool expected) =>
        ProjectBuildService.MayNumberApps(pipelineId, trigger, target).Should().Be(expected);

    [Fact]
    public void Stamping_rewrites_each_clone_and_the_manifests_the_compile_reads()
    {
        using var temp = new TempDir();
        var core = WriteApp(temp.Path, "Core", "28.2.0.0");
        var sales = WriteApp(temp.Path, "Sales", "1.3.2.0");
        var logs = new List<ProjectBuildService.PendingLog>();

        var stamped = ProjectBuildService.StampBuildNumber([core, sales], 4812, logs);

        stamped.Select(a => a.Manifest.Version).Should().Equal("28.2.4812.0", "1.3.4814.0");
        ProjectBuildService.ParseManifest(File.ReadAllText(Path.Combine(core.ProjectDir, "app.json")))!
            .Version.Should().Be("28.2.4812.0");
        ProjectBuildService.ParseManifest(File.ReadAllText(Path.Combine(sales.ProjectDir, "app.json")))!
            .Version.Should().Be("1.3.4814.0");
        logs.Should().ContainSingle().Which.Content.Should()
            .Contain("Build #4812").And.Contain("Core: 28.2.0.0 in app.json is built as 28.2.4812.0.");
    }

    [Fact]
    public void An_app_whose_version_cannot_be_read_keeps_it_and_the_log_says_so()
    {
        using var temp = new TempDir();
        var odd = WriteApp(temp.Path, "Odd", "1.0.0.beta");
        var logs = new List<ProjectBuildService.PendingLog>();

        var stamped = ProjectBuildService.StampBuildNumber([odd], 12, logs);

        stamped.Should().ContainSingle().Which.Manifest.Version.Should().Be("1.0.0.beta");
        File.ReadAllText(Path.Combine(odd.ProjectDir, "app.json")).Should().Contain("\"1.0.0.beta\"");
        logs.Single().Content.Should().Contain("Odd: kept 1.0.0.beta");
    }

    private static DiscoveredApp WriteApp(string root, string name, string version)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        var json = $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "{{name}}",
          "publisher": "CRONUS",
          "version": "{{version}}"
        }
        """;
        File.WriteAllText(Path.Combine(dir, "app.json"), json);
        return new DiscoveredApp(dir, ProjectBuildService.ParseManifest(json)!,
            new ClonedRepo(dir, "https://example.test/repo", null, null));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oe-version-test-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
