using System.Text;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The pure, IO-free decision logic of <see cref="ProjectBuildService"/> — the
/// parts that decide <em>what</em> to build and <em>in what order</em> before any
/// process is spawned: app.json parsing, project discovery (with test/.alpackages
/// pruning), the dependencies-first compile order, target-version selection, the
/// country fallback chain, and the git auth header. The clone/compile/ingest IO is
/// exercised by the worker integration test (Stage D), not here.
/// </summary>
public sealed class ProjectBuildServiceTests
{
    // ── ParseManifest ───────────────────────────────────────────────────

    [Fact]
    public void ParseManifest_reads_identity_versions_and_dependencies()
    {
        const string json = """
        {
          "id": "11111111-1111-1111-1111-111111111111",
          "name": "Core",
          "publisher": "Contoso",
          "version": "1.2.3.4",
          "application": "26.0.0.0",
          "platform": "26.0.0.0",
          "dependencies": [
            { "id": "22222222-2222-2222-2222-222222222222", "name": "Base", "publisher": "X", "version": "1.0.0.0" }
          ]
        }
        """;

        var manifest = ProjectBuildService.ParseManifest(json);

        manifest.Should().NotBeNull();
        manifest!.Id.Should().Be("11111111-1111-1111-1111-111111111111");
        manifest.Name.Should().Be("Core");
        manifest.Application.Should().Be("26.0.0.0");
        manifest.Dependencies.Should().ContainSingle()
            .Which.Id.Should().Be("22222222-2222-2222-2222-222222222222");
    }

    [Fact]
    public void ParseManifest_accepts_legacy_appId_dependency_key()
    {
        // Older app.json spelled the dependency id "appId".
        const string json = """
        {
          "id": "aaa", "name": "Ext", "publisher": "X", "version": "1.0.0.0",
          "dependencies": [ { "appId": "bbb", "name": "Dep", "publisher": "Y", "version": "1.0.0.0" } ]
        }
        """;

        var manifest = ProjectBuildService.ParseManifest(json);

        manifest!.Dependencies.Should().ContainSingle().Which.Id.Should().Be("bbb");
    }

    [Fact]
    public void ParseManifest_returns_null_for_invalid_json()
    {
        ProjectBuildService.ParseManifest("not json at all {").Should().BeNull();
    }

    // ── DiscoverAppProjectDirs ──────────────────────────────────────────

    [Fact]
    public void DiscoverAppProjectDirs_finds_apps_and_prunes_excluded_and_test_folders()
    {
        using var temp = new TempDir();
        // A real app, a nested app, plus folders that must NOT be discovered.
        WriteAppJson(temp.Path, "Core");
        WriteAppJson(Path.Combine(temp.Path, "SubApp"), "Sub");
        WriteAppJson(Path.Combine(temp.Path, ".alpackages"), "ShouldSkip");
        WriteAppJson(Path.Combine(temp.Path, "Acme Tests"), "ShouldSkip");
        WriteAppJson(Path.Combine(temp.Path, ".git", "hooks"), "ShouldSkip");

        var dirs = ProjectBuildService.DiscoverAppProjectDirs(temp.Path);

        dirs.Select(Path.GetFileName).Should().BeEquivalentTo(new[]
        {
            Path.GetFileName(temp.Path), "SubApp",
        });
    }

    [Fact]
    public void DiscoverAppProjectDirs_does_not_follow_symbolic_links_out_of_the_clone()
    {
        // A repository can commit links; a link to a folder outside the clone must
        // not lead discovery onto the server's disk (#1109).
        using var clone = new TempDir();
        using var outside = new TempDir();
        WriteAppJson(clone.Path, "Core");
        WriteAppJson(Path.Combine(outside.Path, "Elsewhere"), "Elsewhere");
        Directory.CreateSymbolicLink(Path.Combine(clone.Path, "escape"), outside.Path);
        Directory.CreateDirectory(Path.Combine(clone.Path, "LinkedManifest"));
        File.CreateSymbolicLink(
            Path.Combine(clone.Path, "LinkedManifest", "app.json"),
            Path.Combine(outside.Path, "Elsewhere", "app.json"));

        var dirs = ProjectBuildService.DiscoverAppProjectDirs(clone.Path);

        dirs.Should().ContainSingle().Which.Should().Be(clone.Path);
    }

    [Fact]
    public void CopyCommittedSymbols_does_not_follow_symbolic_links()
    {
        using var clone = new TempDir();
        using var outside = new TempDir();
        using var symbols = new TempDir();
        var packages = Path.Combine(clone.Path, "App", ".alpackages");
        Directory.CreateDirectory(packages);
        File.WriteAllText(Path.Combine(packages, "Vendor_Real_1.0.0.0.app"), "real");
        var outsidePackages = Path.Combine(outside.Path, ".alpackages");
        Directory.CreateDirectory(outsidePackages);
        File.WriteAllText(Path.Combine(outsidePackages, "Vendor_Outside_1.0.0.0.app"), "outside");
        File.CreateSymbolicLink(Path.Combine(packages, "Vendor_Linked_1.0.0.0.app"),
            Path.Combine(outsidePackages, "Vendor_Outside_1.0.0.0.app"));
        Directory.CreateSymbolicLink(Path.Combine(clone.Path, "escape"), outside.Path);

        ProjectBuildService.CopyCommittedSymbols([clone.Path], symbols.Path);

        Directory.GetFiles(symbols.Path).Select(Path.GetFileName)
            .Should().BeEquivalentTo(["Vendor_Real_1.0.0.0.app"]);
    }

    [Fact]
    public void Git_checks_out_a_committed_symbolic_link_as_a_plain_file()
    {
        // The environment every build git call runs with turns core.symlinks off, so
        // a committed link never lands in the clone as a link at all (#1109).
        if (!GitAvailable()) return;
        using var temp = new TempDir();
        var origin = Path.Combine(temp.Path, "origin");
        Directory.CreateDirectory(origin);
        Git(origin, null, "init", "--quiet");
        Directory.CreateSymbolicLink(Path.Combine(origin, "escape"), "/");
        Git(origin, null, "add", "escape");
        Git(origin, null, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "--quiet", "-m", "link");

        var dest = Path.Combine(temp.Path, "clone");
        var env = ProjectBuildService.GitAuthEnv(RepositoryProvider.GitHub, "tok");
        Git(temp.Path, env, "clone", "--quiet", origin, dest);

        var entry = new FileInfo(Path.Combine(dest, "escape"));
        entry.Exists.Should().BeTrue();
        entry.LinkTarget.Should().BeNull();
        File.ReadAllText(entry.FullName).Should().Be("/");
    }

    private static bool GitAvailable()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void Git(string workDir, IReadOnlyDictionary<string, string>? env, params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) info.Environment[k] = v;
        using var p = System.Diagnostics.Process.Start(info)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, stderr);
    }

    [Theory]
    [InlineData("Test", true)]
    [InlineData("Tests", true)]
    [InlineData("My App Test Library", true)]
    [InlineData("Acme Tests", true)]
    [InlineData("Core", false)]
    [InlineData("MyTestApp", false)]
    public void IsTestSegment_matches_folderzipwalker_rules(string segment, bool expected)
    {
        ProjectBuildService.IsTestSegment(segment).Should().Be(expected);
    }

    [Theory]
    [InlineData("dd0be2ea-f733-4d65-bb34-a28f4624fb14", "Library Assert", true)]
    [InlineData("{23DE40A6-DFE8-4F80-80DB-D70F83CE8CAF}", "", true)]
    [InlineData("", "Any", true)]
    [InlineData("11111111-0000-0000-0000-000000000001", "Tests-TestLibraries", true)]
    [InlineData("63ca2fa4-4f03-4f2b-a480-172fef340d3f", "System Application", false)]
    [InlineData("11111111-0000-0000-0000-000000000001", "CRONUS Test Helpers", false)]
    public void An_app_that_depends_on_Microsofts_test_framework_is_a_test_app(string id, string name, bool expected)
    {
        var manifest = new AppJsonManifest("app", "CRONUS App", "CRONUS", "1.0.0.0", "29.0.0.0", null, null,
            [new AppJsonDependency(id, name)]);

        AppJsonManifestParser.IsTestApp(manifest).Should().Be(expected);
    }

    [Fact]
    public void An_app_with_no_dependencies_is_not_a_test_app() =>
        AppJsonManifestParser.IsTestApp(new AppJsonManifest("app", "CRONUS App", "CRONUS", "1.0.0.0", null, null, null, []))
            .Should().BeFalse();

    [Fact]
    public void The_note_for_skipped_test_apps_names_each_once()
    {
        ProjectBuildService.DescribeSkippedTestApps(["A Tests"]).Should()
            .Be("Not built: A Tests. It depends on Microsoft's test framework, so it is a test app rather than an extension to ship.");
        ProjectBuildService.DescribeSkippedTestApps(["A Tests", "B Tests", "A Tests"]).Should()
            .Be("Not built: A Tests, B Tests. They depend on Microsoft's test framework, so they are test apps rather than extensions to ship.");
    }

    // ── SelectTargetMajorMinor ──────────────────────────────────────────

    [Fact]
    public void SelectTargetMajorMinor_picks_the_highest_application_version()
    {
        var manifests = new[]
        {
            Manifest(application: "24.0.0.0"),
            Manifest(application: "26.1.0.0"),
            Manifest(application: "25.0.0.0"),
        };

        ProjectBuildService.SelectTargetMajorMinor(manifests).Should().Be("26.1");
    }

    [Fact]
    public void SelectTargetMajorMinor_falls_back_to_platform_then_null()
    {
        ProjectBuildService.SelectTargetMajorMinor(new[] { Manifest(application: null, platform: "23.0.0.0") })
            .Should().Be("23.0");
        ProjectBuildService.SelectTargetMajorMinor(new[] { Manifest(application: null, platform: null) })
            .Should().BeNull();
    }

    // ── TopologicalOrder ────────────────────────────────────────────────

    [Fact]
    public void TopologicalOrder_places_dependencies_before_dependents()
    {
        // App "B" depends on "A"; "A" must come first regardless of input order.
        var a = App("A", "id-a");
        var b = App("B", "id-b", dependsOn: "id-a");
        var c = App("C", "id-c", dependsOn: "id-b");

        var ordered = ProjectBuildService.TopologicalOrder(new[] { c, b, a });

        ordered.Select(d => d.Manifest.Name).Should().Equal("A", "B", "C");
    }

    [Fact]
    public void TopologicalOrder_is_cycle_safe_and_keeps_all_apps()
    {
        // A ↔ B mutual dependency must not loop forever or drop an app.
        var a = App("A", "id-a", dependsOn: "id-b");
        var b = App("B", "id-b", dependsOn: "id-a");

        var ordered = ProjectBuildService.TopologicalOrder(new[] { a, b });

        ordered.Select(d => d.Manifest.Name).Should().BeEquivalentTo(new[] { "A", "B" });
    }

    [Fact]
    public void TopologicalOrder_ignores_external_dependencies()
    {
        // A dependency id that isn't one of the built apps (a Microsoft/third-party
        // symbol) is simply not an ordering constraint.
        var a = App("A", "id-a", dependsOn: "microsoft-base-app");

        var ordered = ProjectBuildService.TopologicalOrder(new[] { a });

        ordered.Should().ContainSingle().Which.Manifest.Name.Should().Be("A");
    }

    // ── ResolveCountry ──────────────────────────────────────────────────

    [Theory]
    [InlineData("dk", "dk")]
    [InlineData(" DK ", "dk")]  // trimmed + lower-cased
    [InlineData("w1", "w1")]
    public void ResolveCountry_normalises_the_project_country(string project, string expected)
    {
        ProjectBuildService.ResolveCountry(project).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void ResolveCountry_refuses_a_project_without_a_country(string? project)
    {
        // No org-wide fallback anymore: the base localisation is a per-project
        // decision (the org auto-import setting is a multi-country list now).
        var act = () => ProjectBuildService.ResolveCountry(project);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*country code*");
    }

    // ── BasicAuthHeaderValue ────────────────────────────────────────────

    [Fact]
    public void BasicAuthHeaderValue_uses_empty_username_for_azure_devops()
    {
        var header = ProjectBuildService.BasicAuthHeaderValue(RepositoryProvider.AzureDevOps, "tok");

        header.Should().StartWith("Authorization: Basic ");
        Decode(header).Should().Be(":tok");
    }

    [Fact]
    public void BasicAuthHeaderValue_uses_x_access_token_username_for_github()
    {
        var header = ProjectBuildService.BasicAuthHeaderValue(RepositoryProvider.GitHub, "tok");

        Decode(header).Should().Be("x-access-token:tok");
    }

    // ── DescribeCloneFailures ───────────────────────────────────────────

    private static readonly CloneCredential Linked = new("ghu_linked", CloneCredentialResolver.ConnectedAccountSource);
    private static readonly CloneCredential Pat = new("ghp_pasted", CloneCredentialResolver.BuildTokenSource);

    [Fact]
    public void DescribeNothingToBuild_keeps_the_no_extensions_message_when_every_repository_was_checked_out()
    {
        ProjectBuildService.DescribeNothingToBuild([])
            .Should().StartWith("No buildable extensions were found.");
    }

    [Fact]
    public void DescribeNothingToBuild_names_the_reason_once_when_every_repository_failed_the_same_way()
    {
        var reason = CloneCredentialResolver.NothingToCloneWith(RepositoryProvider.GitHub) + " Then rebuild.";
        var failures = new List<BuildAppResult>
        {
            new("cronus/core", string.Empty, ProjectBuildResultStatus.Failed, reason),
            new("cronus/reports", string.Empty, ProjectBuildResultStatus.Failed, reason),
        };

        var message = ProjectBuildService.DescribeNothingToBuild(failures);

        message.Should().Be("Nothing was built. " + reason)
            .And.NotContain("No buildable extensions", "the repositories were never checked out, so whether they hold extensions is unknown");
    }

    [Fact]
    public void DescribeNothingToBuild_names_the_folder_or_repository_of_a_single_failure_on_one_line()
    {
        var failures = new List<BuildAppResult>
        {
            new("Core App", string.Empty, ProjectBuildResultStatus.Failed, "Could not read app.json in this folder."),
        };

        ProjectBuildService.DescribeNothingToBuild(failures)
            .Should().Be("Nothing was built. Core App: Could not read app.json in this folder.");
        ProjectBuildService.DescribeNothingToBuild(
            [new("cronus/core", string.Empty, ProjectBuildResultStatus.Failed, "git clone failed: fatal: one\nfatal: two")])
            .Should().Be("Nothing was built. cronus/core: git clone failed: fatal: one fatal: two");
    }

    [Fact]
    public void DescribeNothingToBuild_names_each_repository_when_the_reasons_differ()
    {
        var failures = new List<BuildAppResult>
        {
            new("cronus/core", string.Empty, ProjectBuildResultStatus.Failed, "git clone failed: Repository not found."),
            new("cronus/devops", string.Empty, ProjectBuildResultStatus.Failed, "You don't have a build token for Azure DevOps."),
        };

        ProjectBuildService.DescribeNothingToBuild(failures).Should().Be(
            "Nothing was built. cronus/core: git clone failed: Repository not found. "
            + "cronus/devops: You don't have a build token for Azure DevOps.");
    }

    [Fact]
    public void DescribeCloneFailures_reads_as_git_error_for_a_single_attempt()
    {
        var text = ProjectBuildService.DescribeCloneFailures(
            [(Pat, "fatal: returned error: 403\n")], [Pat]);

        text.Should().Be("fatal: returned error: 403");
    }

    [Fact]
    public void DescribeCloneFailures_names_each_credential_when_several_were_tried()
    {
        var text = ProjectBuildService.DescribeCloneFailures(
            [
                (Linked, "remote: Repository not found.\nfatal: repository not found"),
                (Pat, "remote: Write access to repository not granted.\nfatal: 403"),
            ],
            [Linked, Pat]);

        text.Should().Be(
            "With the connected GitHub account: remote: Repository not found. fatal: repository not found. " +
            "With the stored build token: remote: Write access to repository not granted. fatal: 403.");
    }

    [Fact]
    public void DescribeCloneFailures_scrubs_every_credential_from_every_attempt()
    {
        var text = ProjectBuildService.DescribeCloneFailures(
            [(Linked, "bad ghp_pasted"), (Pat, "bad ghu_linked")], [Linked, Pat]);

        text.Should().NotContain("ghp_pasted").And.NotContain("ghu_linked");
    }

    [Fact]
    public void DescribeCloneFailures_says_so_when_an_attempt_left_no_message()
    {
        var text = ProjectBuildService.DescribeCloneFailures([(Linked, ""), (Pat, "fatal: 403")], [Linked, Pat]);

        text.Should().StartWith("With the connected GitHub account: failed without a message.");
    }

    // ── ParseChangelog ──────────────────────────────────────────────────

    private const char Us = '\u001f'; // the unit-separator git --pretty emits between fields

    [Fact]
    public void ParseChangelog_parses_hash_author_date_and_subject()
    {
        var stdout = string.Join("\n", new[]
        {
            $"a1b2c3d{Us}Ada Lovelace{Us}2026-06-20T09:30:00+00:00{Us}Fix the posting routine",
            $"e4f5061{Us}Alan Turing{Us}2026-06-19T17:05:00+02:00{Us}Add a setup field",
        });

        var (entries, truncated) = ProjectBuildService.ParseChangelog(stdout, cap: 100);

        truncated.Should().BeFalse();
        entries.Should().HaveCount(2);
        entries[0].ShortHash.Should().Be("a1b2c3d");
        entries[0].Author.Should().Be("Ada Lovelace");
        entries[0].Subject.Should().Be("Fix the posting routine");
        entries[0].CommittedAt.Should().Be(new DateTime(2026, 6, 20, 9, 30, 0, DateTimeKind.Utc));
        // The +02:00 commit is normalised to UTC.
        entries[1].CommittedAt.Should().Be(new DateTime(2026, 6, 19, 15, 5, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseChangelog_flags_truncation_and_caps_the_list()
    {
        // The caller fetches cap + 1 lines so an over-cap range is detectable.
        var lines = Enumerable.Range(0, 4)
            .Select(i => $"hash{i}{Us}Dev{Us}2026-06-20T09:30:00+00:00{Us}Commit {i}");
        var stdout = string.Join("\n", lines);

        var (entries, truncated) = ProjectBuildService.ParseChangelog(stdout, cap: 3);

        truncated.Should().BeTrue();
        entries.Should().HaveCount(3, "the list is capped to exactly cap entries");
    }

    [Fact]
    public void ParseChangelog_is_empty_for_no_commits()
    {
        ProjectBuildService.ParseChangelog("", cap: 100).Entries.Should().BeEmpty();
        ProjectBuildService.ParseChangelog("   \n  ", cap: 100).Truncated.Should().BeFalse();
    }

    [Fact]
    public void ParseChangelog_skips_malformed_lines()
    {
        // A line missing the separator fields is ignored rather than crashing.
        var stdout = $"good{Us}Dev{Us}2026-06-20T09:30:00+00:00{Us}Subject\nmalformed-line-without-separators";

        var (entries, _) = ProjectBuildService.ParseChangelog(stdout, cap: 100);

        entries.Should().ContainSingle().Which.ShortHash.Should().Be("good");
    }

    // ── Extension selection (FilterBySelection / ParseSelectedAppIds / NormalizeAppId) ──

    [Fact]
    public void FilterBySelection_keeps_only_apps_whose_id_is_selected()
    {
        var a = App("A", "11111111-1111-1111-1111-111111111111");
        var b = App("B", "22222222-2222-2222-2222-222222222222");
        var c = App("C", "33333333-3333-3333-3333-333333333333");

        var selected = new HashSet<string>(StringComparer.Ordinal)
        {
            ProjectBuildService.NormalizeAppId("11111111-1111-1111-1111-111111111111"),
            ProjectBuildService.NormalizeAppId("33333333-3333-3333-3333-333333333333"),
        };

        var kept = ProjectBuildService.FilterBySelection(new[] { a, b, c }, selected);

        kept.Select(d => d.Manifest.Name).Should().Equal("A", "C");
    }

    [Fact]
    public void FilterBySelection_matches_ids_case_and_brace_insensitively()
    {
        // The id was captured at discovery brace-wrapped and upper-cased; the
        // manifest read at build time spells it bare and lower-cased. They must match.
        var a = App("A", "{ABCDEF12-0000-0000-0000-000000000000}");
        var selected = new HashSet<string>(StringComparer.Ordinal)
        {
            ProjectBuildService.NormalizeAppId("abcdef12-0000-0000-0000-000000000000"),
        };

        ProjectBuildService.FilterBySelection(new[] { a }, selected)
            .Should().ContainSingle().Which.Manifest.Name.Should().Be("A");
    }

    [Fact]
    public void FilterBySelection_drops_everything_when_selection_matches_nothing()
    {
        var a = App("A", "id-a");

        ProjectBuildService.FilterBySelection(new[] { a }, new HashSet<string>(StringComparer.Ordinal) { "other" })
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all {")]
    public void ParseSelectedAppIds_returns_null_for_blank_or_invalid(string? json)
    {
        // null means "build everything" — the default and back-compat behaviour.
        ProjectBuildService.ParseSelectedAppIds(json).Should().BeNull();
    }

    [Fact]
    public void ParseSelectedAppIds_normalises_and_dedupes_ids()
    {
        var set = ProjectBuildService.ParseSelectedAppIds("""["{ABC}", "abc", "DEF"]""");

        set.Should().NotBeNull();
        set!.Should().BeEquivalentTo(new[] { "abc", "def" });
    }

    [Theory]
    [InlineData("{ABCDEF}", "abcdef")]
    [InlineData("  AbC  ", "abc")]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    public void NormalizeAppId_strips_braces_trims_and_lowercases(string? input, string expected)
    {
        ProjectBuildService.NormalizeAppId(input).Should().Be(expected);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static string Decode(string header)
    {
        var b64 = header["Authorization: Basic ".Length..];
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    private static AppJsonManifest Manifest(string? application, string? platform = null) =>
        new("id", "Name", "Pub", "1.0.0.0", application, platform, null, Array.Empty<AppJsonDependency>());

    private static DiscoveredApp App(string name, string id, string? dependsOn = null)
    {
        var deps = dependsOn is null
            ? Array.Empty<AppJsonDependency>()
            : new[] { new AppJsonDependency(dependsOn, "Dep") };
        return new DiscoveredApp(
            $"/tmp/{name}",
            new AppJsonManifest(id, name, "Pub", "1.0.0.0", "26.0.0.0", null, null, deps),
            new ClonedRepo($"/tmp/{name}", "https://example.test/repo", null, null));
    }

    private static void WriteAppJson(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"),
            $$"""{ "id": "{{name}}", "name": "{{name}}", "publisher": "X", "version": "1.0.0.0" }""");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oe-build-test-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
