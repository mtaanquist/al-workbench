using System.IO.Compression;
using ALDevToolbox.Services.ObjectExplorer.Import;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Unit tests for <see cref="FolderZipWalker"/>. Builds tiny synthetic ZIPs
/// in memory so the assertions stay focused on the folder/filename
/// conventions the DVD uses — no need for the real Microsoft fixtures here.
/// </summary>
public sealed class FolderZipWalkerTests
{
    [Fact]
    public void Pairs_app_with_sibling_source_zip_in_same_folder()
    {
        using var archive = BuildArchive(
            "applications/DKCore/Source/Microsoft_DK Core.app",
            "applications/DKCore/Source/DK Core.Source.zip");

        var entries = FolderZipWalker.Walk(archive);
        entries.Should().ContainSingle()
            .Which.SourceZipEntry.Should().NotBeNull();
    }

    [Fact]
    public void Strips_publisher_prefix_when_pairing()
    {
        using var archive = BuildArchive(
            "applications/BaseApp/Source/Microsoft_Base Application.app",
            "applications/BaseApp/Source/Base Application.Source.zip");

        var entries = FolderZipWalker.Walk(archive);
        entries.Should().ContainSingle()
            .Which.SourceZipEntry!.Name.Should().Be("Base Application.Source.zip");
    }

    [Fact]
    public void Pairs_by_bare_stem_when_publisher_prefix_present_on_both_sides()
    {
        // _Exclude_ apps keep their prefix on both .app and .Source.zip; the
        // bare-stem candidate (1st try) should match without falling back to
        // the prefix-stripped form.
        using var archive = BuildArchive(
            "applications/APIV2/Source/Microsoft__Exclude_APIV2_.app",
            "applications/APIV2/Source/_Exclude_APIV2_.Source.zip");

        var entries = FolderZipWalker.Walk(archive);
        entries.Should().ContainSingle()
            .Which.SourceZipEntry.Should().NotBeNull();
    }

    [Fact]
    public void Leaves_source_zip_null_when_no_sibling_present()
    {
        using var archive = BuildArchive(
            "applications/MyApp/Source/Partner_MyApp.app");

        var entries = FolderZipWalker.Walk(archive);
        entries.Should().ContainSingle()
            .Which.SourceZipEntry.Should().BeNull();
    }

    [Theory]
    [InlineData("applications/BaseApp/Test/Microsoft_Tests-Bank.app", true)]
    [InlineData("applications/BaseApp/Source/Microsoft_Base Application.app", false)]
    [InlineData("applications/TestFramework/AITestToolkit/Microsoft_AI Test Toolkit.app", true)]
    [InlineData("applications/TestFramework/TestLibraries/Any/Microsoft_Any.app", true)]
    [InlineData("applications/Quality Management/Test Library/Microsoft_Quality Management Test Library.app", true)]
    [InlineData("applications/Quality Management/Source/Microsoft_Quality Management.app", false)]
    [InlineData("applications/Shopify/test/Microsoft_Shopify Connector Test.app", true)]
    // BC 28.1+ ships some test toolkits as their own top-level extension folders
    // rather than as a Test/ subfolder under the product app. The folder name itself
    // ends with " Test Library" (or " Test Toolkit") — exact-match against
    // TestFolderNames alone misses these, so the walker also checks suffix patterns.
    [InlineData("applications/Application Test Library/Source/Microsoft_Application Test Library.app", true)]
    [InlineData("applications/AI Test Toolkit/Source/Microsoft_AI Test Toolkit.app", true)]
    [InlineData("applications/Quality Management Test Libraries/Source/Microsoft_QM Test Libs.app", true)]
    public void Sets_is_test_when_ancestor_folder_matches_test_convention(string path, bool expected)
    {
        using var archive = BuildArchive(path);
        var entry = FolderZipWalker.Walk(archive).Single();
        entry.IsTest.Should().Be(expected);
    }

    [Theory]
    [InlineData("applications/APIV2/Source/Microsoft__Exclude_APIV2_.app", true)]
    [InlineData("applications/APIV2/Source/_Exclude_APIV2_.app", true)]
    [InlineData("applications/BankDeposits/Source/Microsoft__Exclude_Bank Deposits.app", true)]
    [InlineData("applications/BaseApp/Source/Microsoft_Base Application.app", false)]
    public void Sets_is_internal_when_filename_contains_exclude_marker(string path, bool expected)
    {
        using var archive = BuildArchive(path);
        var entry = FolderZipWalker.Walk(archive).Single();
        entry.IsInternal.Should().Be(expected);
    }

    [Theory]
    [InlineData("applications/BaseApp/Source/Microsoft_Danish language (Denmark).app", true)]
    [InlineData("applications/BaseApp/Source/Microsoft_French language (Switzerland).app", true)]
    [InlineData("applications/BaseApp/Source/Microsoft_Base Application.app", false)]
    [InlineData("applications/BaseApp/Source/Microsoft_DK Core.app", false)]
    public void Sets_is_language_pack_when_name_matches_language_pattern(string path, bool expected)
    {
        using var archive = BuildArchive(path);
        var entry = FolderZipWalker.Walk(archive).Single();
        entry.IsLanguagePack.Should().Be(expected);
    }

    [Fact]
    public void Does_not_pair_a_source_zip_from_a_different_folder()
    {
        // Two different apps with similarly-named source zips that live in
        // different subfolders — pairing must respect folder boundaries.
        using var archive = BuildArchive(
            "applications/DKCore/Source/Microsoft_DK Core.app",
            "applications/Other/Source/DK Core.Source.zip");

        var entry = FolderZipWalker.Walk(archive).Single();
        entry.SourceZipEntry.Should().BeNull(
            because: "the source zip sits under a different folder; pairing is per-directory");
    }

    [Fact]
    public void Returns_one_entry_per_app_in_a_multi_app_archive()
    {
        using var archive = BuildArchive(
            "applications/DKCore/Source/Microsoft_DK Core.app",
            "applications/DKCore/Source/DK Core.Source.zip",
            "applications/OIOUBL/Source/Microsoft_OIOUBL.app",
            "applications/OIOUBL/Source/OIOUBL.Source.zip");

        var entries = FolderZipWalker.Walk(archive);
        entries.Should().HaveCount(2);
        entries.Select(e => e.FileName).Should().BeEquivalentTo(
            new[] { "Microsoft_DK Core.app", "Microsoft_OIOUBL.app" });
    }

    // ── WalkDvd: full-DVD subset selection ─────────────────────────────

    [Fact]
    public void WalkDvd_keeps_applications_apps_and_system_app_only()
    {
        using var archive = BuildArchive(
            "BC/Applications/DKCore/Source/Microsoft_DK Core.app",
            "BC/Applications/DKCore/Source/DK Core.Source.zip",
            "BC/ModernDev/PFiles/Microsoft Dynamics NAV/280/AL Development Environment/System.app",
            // Noise outside Applications that must be ignored:
            "BC/ServiceTier/Some.app",
            "BC/Prerequisite Components/setup.exe",
            "BC/ModernDev/PFiles/Microsoft Dynamics NAV/280/AL Development Environment/ReadMe.txt");

        var entries = FolderZipWalker.WalkDvd(archive);

        entries.Select(e => e.FileName).Should().BeEquivalentTo(
            new[] { "Microsoft_DK Core.app", "System.app" });
        entries.Single(e => e.FileName == "Microsoft_DK Core.app")
            .SourceZipEntry.Should().NotBeNull();
        entries.Single(e => e.FileName == "System.app")
            .SourceZipEntry.Should().BeNull();
    }

    [Fact]
    public void WalkDvd_excludes_test_apps_and_their_source()
    {
        using var archive = BuildArchive(
            "BC/Applications/BaseApp/Source/Microsoft_Base Application.app",
            "BC/Applications/BaseApp/Source/Base Application.Source.zip",
            "BC/Applications/BaseApp/Test/Microsoft_Base Application Test.app",
            "BC/Applications/BaseApp/Test/Base Application Test.Source.zip",
            "BC/Applications/TestFramework/TestLibraries/Microsoft_Any.app");

        var entries = FolderZipWalker.WalkDvd(archive);

        entries.Select(e => e.FileName).Should().BeEquivalentTo(
            new[] { "Microsoft_Base Application.app" },
            because: "test extensions and their source are dropped entirely on the DVD path");
    }

    [Fact]
    public void WalkDvd_excludes_top_level_application_test_library_extension()
    {
        // BC 28.1 ships "Application Test Library" as its own top-level
        // Applications/<name>/ folder (not as a Test/ subfolder of another app).
        // The folder name itself ends with "Test Library", so the walker's
        // suffix rule should drop it.
        using var archive = BuildArchive(
            "BC/Applications/BaseApp/Source/Microsoft_Base Application.app",
            "BC/Applications/Application Test Library/Source/Microsoft_Application Test Library.app",
            "BC/Applications/Application Test Library/Source/Application Test Library.Source.zip");

        var entries = FolderZipWalker.WalkDvd(archive);

        entries.Select(e => e.FileName).Should().BeEquivalentTo(
            new[] { "Microsoft_Base Application.app" });
    }

    [Theory]
    [InlineData("BC/Applications/DKCore/Source/Microsoft_DK Core.app")]   // modern, PascalCase
    [InlineData("BC/applications/DKCore/Source/Microsoft_DK Core.app")]   // lower-case
    [InlineData("BC/Extensions/DKCore/Source/Microsoft_DK Core.app")]     // older "Extensions" name
    [InlineData("BC/extensions/DKCore/Source/Microsoft_DK Core.app")]
    public void WalkDvd_matches_known_app_folders_case_insensitively(string appPath)
    {
        using var archive = BuildArchive(appPath);
        FolderZipWalker.WalkDvd(archive).Select(e => e.FileName)
            .Should().BeEquivalentTo(new[] { "Microsoft_DK Core.app" });
    }

    [Fact]
    public void WalkDvd_handles_backslash_separated_paths()
    {
        // Some Microsoft DVD ZIPs (e.g. BC 26.x, build 38819) store entries with
        // Windows backslash separators. Splitting on '/' alone would collapse the
        // whole path to one segment and match nothing.
        using var archive = BuildArchive(
            @"applications\BaseApp\Source\Microsoft_Base Application.app",
            @"applications\BaseApp\Source\Base Application.Source.zip",
            @"applications\BaseApp\Test\Microsoft_Base Application Test.app",
            @"ModernDev\PFiles\Microsoft Dynamics NAV\260\AL Development Environment\System.app");

        var entries = FolderZipWalker.WalkDvd(archive);

        entries.Select(e => e.FileName).Should().BeEquivalentTo(
            new[] { "Microsoft_Base Application.app", "System.app" });
        entries.Single(e => e.FileName == "Microsoft_Base Application.app")
            .SourceZipEntry.Should().NotBeNull("the sibling .Source.zip pairs across backslash paths too");
    }

    [Fact]
    public void WalkDvd_returns_empty_when_no_applications_or_system_app_present()
    {
        // Mimics a future DVD layout where nothing matches our anchors — the
        // endpoint turns an empty result into a clear "not a DVD" error.
        using var archive = BuildArchive(
            "BC/ServiceTier/Some.app",
            "BC/Prerequisite Components/setup.exe");

        FolderZipWalker.WalkDvd(archive).Should().BeEmpty();
    }

    // ── WalkWorkspace: zipped VS Code AL workspace ─────────────────────

    [Fact]
    public void LooksLikeWorkspace_true_when_app_json_present_false_for_dvd()
    {
        using var workspace = BuildArchive(
            "Core/app.json",
            "Core/Consortio_Dansani Core_1.0.0.0.app");
        using var dvd = BuildArchive(
            "applications/DKCore/Source/Microsoft_DK Core.app");

        FolderZipWalker.LooksLikeWorkspace(workspace).Should().BeTrue();
        FolderZipWalker.LooksLikeWorkspace(dvd).Should().BeFalse();
    }

    [Fact]
    public void WalkWorkspace_one_module_per_app_folder_ignoring_alpackages()
    {
        // A multi-root workspace: each folder is an app (app.json + .app), and
        // each folder's .alpackages/ holds dependency copies that must NOT be
        // imported — otherwise Dansani Core would come in twice (once as its
        // own app, once as EDI's cached dependency).
        using var archive = BuildArchive(
            "Core/app.json",
            "Core/Consortio_Dansani Core_1.1.29.336.app",
            "Core/.alpackages/Microsoft_Application_14.24.46857.0.app",
            "Core/src/codeunits/Foo.Codeunit.al",
            "EDI/app.json",
            "EDI/Consortio_Dansani EDI_1.0.8.77.app",
            "EDI/.alpackages/Consortio_Dansani Core_1.1.28.250.app",
            "EDI/.alpackages/Microsoft_Application_14.24.46857.0.app");

        var entries = FolderZipWalker.WalkWorkspace(archive);

        entries.Select(e => e.FileName).Should().BeEquivalentTo(new[]
        {
            "Consortio_Dansani Core_1.1.29.336.app",
            "Consortio_Dansani EDI_1.0.8.77.app",
        });
    }

    [Fact]
    public void WalkWorkspace_keeps_only_the_newest_version_and_drops_dep_app()
    {
        // One folder, many historical builds plus the .dep.app sidecars. Only
        // the highest version's plain .app should survive.
        using var archive = BuildArchive(
            "Core/app.json",
            "Core/Consortio_Dansani Core_1.1.28.300.app",
            "Core/Consortio_Dansani Core_1.1.29.310.app",
            "Core/Consortio_Dansani Core_1.1.29.310.dep.app",
            "Core/Consortio_Dansani Core_1.1.29.336.app",
            "Core/Consortio_Dansani Core_1.1.29.336.dep.app");

        var entries = FolderZipWalker.WalkWorkspace(archive);

        entries.Select(e => e.FileName).Should().BeEquivalentTo(
            new[] { "Consortio_Dansani Core_1.1.29.336.app" });
    }

    [Fact]
    public void WalkWorkspace_pairs_sibling_source_zip_when_present()
    {
        using var archive = BuildArchive(
            "Core/app.json",
            "Core/Consortio_Dansani Core_1.0.0.0.app",
            "Core/Dansani Core.Source.zip");

        var entry = FolderZipWalker.WalkWorkspace(archive).Should().ContainSingle().Subject;
        entry.SourceZipEntry.Should().NotBeNull();
    }

    [Fact]
    public void DescribeUncompiledAppRoots_names_folders_with_no_built_app()
    {
        // FMA declares an app.json but was never compiled — no .app anywhere in
        // its folder. Core is built. Only FMA should be reported.
        using var archive = BuildArchive(
            "Core/app.json",
            "Core/Consortio_Dansani Core_1.0.0.0.app",
            "FMA/app.json",
            "FMA/src/codeunits/Foo.Codeunit.al");

        FolderZipWalker.WalkWorkspace(archive).Select(e => e.FileName)
            .Should().BeEquivalentTo(new[] { "Consortio_Dansani Core_1.0.0.0.app" });
        FolderZipWalker.DescribeUncompiledAppRoots(archive)
            .Should().BeEquivalentTo(new[] { "FMA" });
    }

    [Fact]
    public void WalkWorkspace_ignores_stray_sibling_app_in_a_folder_root()
    {
        // MultiCompany's app.json is for "Dansani Multi-Company" but a stray
        // "Dansani Monitoring" .app sits in its root (a dependency dropped next
        // to app.json rather than in .alpackages). Without the app.json identity
        // check, Monitoring 1.0.6.8 gets claimed by BOTH folders and the import
        // aborts on the duplicate (AppId, Version). Only Monitoring's own folder
        // should contribute it; MultiCompany contributes nothing and reads as
        // uncompiled. (Regression for the failure reported on v6.4.0.)
        using var archive = BuildArchiveWithContent(
            ("Monitoring/app.json", Manifest("Consortio IT ApS", "Dansani Monitoring")),
            ("Monitoring/Consortio IT ApS_Dansani Monitoring_1.0.6.8.app", "monitoring-bytes"),
            ("MultiCompany/app.json", Manifest("Consortio IT ApS", "Dansani Multi-Company")),
            ("MultiCompany/Consortio IT ApS_Dansani Monitoring_1.0.6.8.app", "different-bytes"));

        FolderZipWalker.WalkWorkspace(archive).Select(e => e.FileName)
            .Should().BeEquivalentTo(
                new[] { "Consortio IT ApS_Dansani Monitoring_1.0.6.8.app" },
                because: "only Monitoring's own folder owns that app; the stray copy in MultiCompany is skipped");
        FolderZipWalker.DescribeUncompiledAppRoots(archive)
            .Should().BeEquivalentTo(new[] { "MultiCompany" });
    }

    [Fact]
    public void WalkWorkspace_dedupes_when_two_folders_share_an_app_identity()
    {
        // Pathological: two folders with the same app.json identity, each
        // holding the app with different bytes. The dedupe safety net keeps one
        // so the import can't hard-fail on a duplicate (AppId, Version).
        using var archive = BuildArchiveWithContent(
            ("A/app.json", Manifest("Pub", "Shared App")),
            ("A/Pub_Shared App_1.0.0.0.app", "a"),
            ("B/app.json", Manifest("Pub", "Shared App")),
            ("B/Pub_Shared App_1.0.0.0.app", "b"));

        FolderZipWalker.WalkWorkspace(archive).Should().ContainSingle()
            .Which.FileName.Should().Be("Pub_Shared App_1.0.0.0.app");
    }

    [Fact]
    public void WalkWorkspace_matches_own_app_across_punctuation_differences()
    {
        // app.json name/publisher and the .app filename can differ in spaces,
        // pluses and the like; the normalised identity match must still bind
        // them so a real build isn't mistaken for a stray.
        using var archive = BuildArchiveWithContent(
            ("App/app.json", Manifest("Consortio IT ApS", "Dansani OSS + WEB")),
            ("App/Consortio IT ApS_Dansani OSS + WEB_1.0.5.2.app", "bytes"));

        FolderZipWalker.WalkWorkspace(archive).Select(e => e.FileName)
            .Should().BeEquivalentTo(new[] { "Consortio IT ApS_Dansani OSS + WEB_1.0.5.2.app" });
    }

    private static string Manifest(string publisher, string name) =>
        $$"""{"id":"00000000-0000-0000-0000-000000000000","name":"{{name}}","publisher":"{{publisher}}","version":"1.0.0.0"}""";

    /// <summary>
    /// Builds a throwaway <see cref="ZipArchive"/> in memory with the supplied
    /// entry paths. File bodies are empty — none of the walker's behaviour
    /// depends on payload content, only on names and folder layout.
    /// </summary>
    // ── Microsoft artifact layout (verified against a real BC 25.5 DK artifact) ──

    [Fact]
    public void Artifact_application_walk_picks_localized_apps_under_country_suffixed_folder()
    {
        using var archive = BuildArchive(
            "dk/Applications.DK/Microsoft_Base Application_25.5.30849.48785.app",
            "dk/Applications.DK/Base Application.Source.zip",
            "dk/Applications.DK/Microsoft_System Application_25.5.30849.48785.app",
            "dk/Extensions/Microsoft_Sustainability_25.5.30849.48785.app",
            "dk/BusinessCentral-DK.bak",
            "dk/manifest.json");

        var entries = FolderZipWalker.WalkBcArtifactApplication(archive);

        entries.Select(e => e.FileName).Should().BeEquivalentTo(
            "Microsoft_Base Application_25.5.30849.48785.app",
            "Microsoft_System Application_25.5.30849.48785.app",
            "Microsoft_Sustainability_25.5.30849.48785.app");
    }

    [Fact]
    public void Artifact_application_walk_keeps_the_country_folder_copy_when_extensions_repeats_an_app()
    {
        // The insider Sandbox artifact (30.0.55227.0 dk, read live) carries Base
        // Application, System Application, Application, Business Foundation and
        // PEPPOL in BOTH Applications.DK/ and Extensions/ under the same filename
        // with different bytes. Importing both would trip the (AppId, Version)
        // collision guard and fail the release; the copy beside its .Source.zip
        // is the one we want. Apps that appear once in Extensions/ still come
        // through.
        using var archive = BuildArchive(
            "Applications.DK/Microsoft_Base Application_30.0.55227.0.app",
            "Applications.DK/Base Application.Source.zip",
            "Applications.DK/Microsoft_PEPPOL_30.0.55227.0.app",
            "Extensions/Microsoft_Base Application_30.0.55227.0.app",
            "Extensions/Microsoft_PEPPOL_30.0.55227.0.app",
            "Extensions/Microsoft_Sustainability_30.0.55227.0.app",
            "manifest.json");

        var entries = FolderZipWalker.WalkBcArtifactApplication(archive);

        entries.Select(e => e.FileName).Should().BeEquivalentTo(
            "Microsoft_Base Application_30.0.55227.0.app",
            "Microsoft_PEPPOL_30.0.55227.0.app",
            "Microsoft_Sustainability_30.0.55227.0.app");
        var baseApp = entries.Single(e => e.FileName.StartsWith("Microsoft_Base Application"));
        baseApp.AppEntry.FullName.Should().StartWith("Applications.DK/");
        baseApp.SourceZipEntry.Should().NotBeNull("the country-folder copy is the one paired with its source");
        entries.Single(e => e.FileName.StartsWith("Microsoft_PEPPOL")).AppEntry.FullName.Should().StartWith("Applications.DK/");
    }

    [Fact]
    public void Artifact_application_walk_pairs_versioned_app_with_unversioned_source_zip()
    {
        using var archive = BuildArchive(
            "dk/Applications.DK/Microsoft_Base Application_25.5.30849.48785.app",
            "dk/Applications.DK/Base Application.Source.zip");

        var entry = FolderZipWalker.WalkBcArtifactApplication(archive).Should().ContainSingle().Subject;
        entry.SourceZipEntry.Should().NotBeNull();
        entry.SourceZipEntry!.Name.Should().Be("Base Application.Source.zip");
    }

    [Theory]
    [InlineData("dk/Applications.DK/Microsoft_Tests-Bank_25.5.30849.48785.app")]
    [InlineData("dk/Applications.DK/Microsoft_System Application Test Library_25.5.30849.48785.app")]
    [InlineData("dk/Applications.DK/Microsoft_Business Foundation Test Libraries_25.5.30849.48785.app")]
    [InlineData("dk/Applications.DK/Microsoft_Test Runner_25.5.30849.48785.app")]
    [InlineData("dk/Applications.DK/Microsoft_TestRunner-Internal_25.5.30849.48785.app")]
    [InlineData("dk/Applications.DK/Microsoft_AI Test Toolkit_25.5.30849.48785.app")]
    [InlineData("dk/Applications.DK/Microsoft_Permissions Mock_25.5.30849.48785.app")]
    [InlineData("dk/Applications.DK/Microsoft_Performance Toolkit Tests_25.5.30849.48785.app")]
    // Test-framework / dev-tooling libraries the artifact ships flat (caught by
    // the exact-name list rather than a "test" pattern):
    [InlineData("dk/Applications.DK/Microsoft_Any_25.5.30849.48785.app")]
    [InlineData("dk/Applications.DK/Microsoft_Library Assert_25.5.30849.48785.app")]
    [InlineData("dk/Applications.DK/Microsoft_Library Variable Storage_25.5.30849.48785.app")]
    [InlineData("dk/Applications.DK/Microsoft_Performance Toolkit_25.5.30849.48785.app")]
    [InlineData("dk/Applications.DK/Microsoft_Performance Toolkit Samples_25.5.30849.48785.app")]
    [InlineData("dk/Applications.DK/Microsoft_BuildTools_25.5.30849.48785.app")]
    [InlineData("dk/Applications.DK/Microsoft_DemoTool_25.5.30849.48785.app")]
    public void Artifact_application_walk_drops_test_and_tooling_apps(string nonProductAppPath)
    {
        using var archive = BuildArchive(
            "dk/Applications.DK/Microsoft_Base Application_25.5.30849.48785.app",
            nonProductAppPath);

        FolderZipWalker.WalkBcArtifactApplication(archive)
            .Select(e => e.FileName)
            .Should().ContainSingle().Which.Should().StartWith("Microsoft_Base Application");
    }

    [Fact]
    public void Artifact_application_walk_keeps_the_product_apps()
    {
        // The core localized product apps must survive the test/tooling filter.
        using var archive = BuildArchive(
            "dk/Applications.DK/Microsoft_Base Application_25.5.30849.48785.app",
            "dk/Applications.DK/Microsoft_System Application_25.5.30849.48785.app",
            "dk/Applications.DK/Microsoft_Application_25.5.30849.48785.app",
            "dk/Applications.DK/Microsoft_Business Foundation_25.5.30849.48785.app",
            "dk/Extensions/Microsoft_Sustainability_25.5.30849.48785.app");

        FolderZipWalker.WalkBcArtifactApplication(archive).Should().HaveCount(5);
    }

    [Fact]
    public void Artifact_platform_walk_returns_only_system_app_not_the_w1_apps()
    {
        // The platform artifact is the classic W1 DVD plus the unique System.app;
        // its W1 apps would collide with the localized ones, so only System.app
        // comes through.
        using var archive = BuildArchive(
            "platform/Applications/BaseApp/Source/Microsoft_Base Application.app",
            "platform/Applications/Application/Source/Microsoft_Application.app",
            "platform/ModernDev/program files/Microsoft Dynamics NAV/252/AL Development Environment/System.app");

        var entries = FolderZipWalker.WalkBcArtifactPlatform(archive);

        entries.Should().ContainSingle().Which.FileName.Should().Be("System.app");
    }

    private static ZipArchive BuildArchive(params string[] paths)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var p in paths)
            {
                z.CreateEntry(p);
            }
        }
        ms.Position = 0;
        // Caller is responsible for disposing the returned archive (which
        // also disposes the backing MemoryStream).
        return new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
    }

    /// <summary>
    /// Like <see cref="BuildArchive"/> but writes a body for each entry — needed
    /// for the workspace tests, which parse <c>app.json</c> content to bind a
    /// folder to its own app.
    /// </summary>
    private static ZipArchive BuildArchiveWithContent(params (string Path, string Content)[] files)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var entry = z.CreateEntry(path);
                using var w = new StreamWriter(entry.Open());
                w.Write(content);
            }
        }
        ms.Position = 0;
        return new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
    }
}
