using System.IO.Compression;
using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// <see cref="AlSymbolFeedResolver"/> against <see cref="FakeSymbolFeeds"/>, a
/// stand-in for Microsoft's feeds with their recorded quirks (issue #901).
/// </summary>
public sealed class AlSymbolFeedResolverTests : IDisposable
{
    private const string CoreId = "4b915d7e-c02a-435f-85ab-649086c1e002";
    private const string SystemAppId = "e4b442d0-e8e3-4210-bfca-f1e66686caa0";
    private const string CoreSymbols = "ContiniaSoftware.ContiniaCore.symbols." + CoreId;
    private const string SystemAppSymbols = "ContiniaSoftware.ContiniaSystemApplication.symbols." + SystemAppId;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "feed-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _cache;
    private readonly string _target;
    private readonly FakeSymbolFeeds _feeds = new();

    public AlSymbolFeedResolverTests()
    {
        _cache = Path.Combine(_root, "cache");
        _target = Path.Combine(_root, "symbols");
        Directory.CreateDirectory(_target);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private AlSymbolFeedResolver Resolver() => new(_feeds, NullLogger<AlSymbolFeedResolver>.Instance, new AlSymbolFeedOptions
    {
        AppSourceFeedUrl = FakeSymbolFeeds.AppSourceIndex,
        MicrosoftFeedUrl = FakeSymbolFeeds.MicrosoftIndex,
        CacheDirectory = _cache,
    });

    private SymbolFeedRequest Request(string? application = "29.0", params SymbolDependency[] deps) =>
        new(deps, _target, new HashSet<string>(), application, "dk");

    private void SeedCoreAndSystemApp()
    {
        _feeds.Add("appsource", SystemAppSymbols, SystemAppId, "Continia System Application", "29.0.0.5", application: "29.0.0");
        _feeds.Add("appsource", CoreSymbols, CoreId, "Continia Core", "28.5.0.1", application: "28.0.0",
            dependsOn: [(SystemAppSymbols, "28.0.0")]);
        _feeds.Add("appsource", CoreSymbols, CoreId, "Continia Core", "29.0.0.199323", application: "29.0.0",
            dependsOn: [(SystemAppSymbols, "29.0.0")]);
        _feeds.Add("appsource", CoreSymbols, CoreId, "Continia Core", "25.0.0.196196", application: "25.0.0");
    }

    private IReadOnlyList<string> AppIdsInTarget() =>
        Directory.EnumerateFiles(_target, "*.app")
            .Select(p => AppPackageReader.TryReadManifest(p)!.AppId.ToString())
            .ToList();

    [Fact]
    public async Task Resolves_by_app_id_and_follows_the_nuspec_to_a_transitive_dependency()
    {
        SeedCoreAndSystemApp();

        var outcome = await Resolver().ResolveAsync(Request("29.0", new SymbolDependency(CoreId, "Continia Core", "25.0.0.0")));

        outcome.Unresolved.Should().BeEmpty();
        outcome.Resolved.Select(r => (r.Name, r.Version, r.Feed)).Should().Equal(
            ("Continia Core", "29.0.0.199323", AlSymbolFeedResolver.AppSourceFeedName),
            ("Continia System Application", "29.0.0.5", AlSymbolFeedResolver.AppSourceFeedName));
        AppIdsInTarget().Should().BeEquivalentTo([CoreId, SystemAppId]);
        _feeds.Requests.Should().Contain(r => r.StartsWith("https://blob.test/"), "the 303 on the .nupkg is followed");
    }

    [Fact]
    public async Task Picks_the_newest_version_that_fits_the_target_Business_Central_whatever_order_the_index_lists()
    {
        SeedCoreAndSystemApp();

        // BC 28.0: 29.x needs application 29 and is passed over.
        var outcome = await Resolver().ResolveAsync(Request("28.0", new SymbolDependency(CoreId, "Continia Core", "25.0.0.0")));

        outcome.Resolved.Should().Contain(r => r.AppId == CoreId && r.Version == "28.5.0.1");
    }

    [Fact]
    public async Task Respects_the_version_floor()
    {
        SeedCoreAndSystemApp();

        var outcome = await Resolver().ResolveAsync(Request("28.0", new SymbolDependency(CoreId, "Continia Core", "29.0.0.0")));

        outcome.Resolved.Should().BeEmpty();
        var missing = outcome.Unresolved.Should().ContainSingle().Subject;
        missing.AppId.Should().Be(CoreId);
        missing.Reason.Should().Contain("fits Business Central 28.0");
    }

    [Fact]
    public async Task A_dependency_cycle_terminates()
    {
        const string aId = "aaaaaaaa-0000-0000-0000-000000000001";
        const string bId = "bbbbbbbb-0000-0000-0000-000000000002";
        _feeds.Add("appsource", "Test.A.symbols." + aId, aId, "App A", "1.0.0.0", dependsOn: [("Test.B.symbols." + bId, "1.0.0")]);
        _feeds.Add("appsource", "Test.B.symbols." + bId, bId, "App B", "1.0.0.0", dependsOn: [("Test.A.symbols." + aId, "1.0.0")]);

        var outcome = await Resolver().ResolveAsync(Request(null, new SymbolDependency(aId, "App A", "1.0.0.0")));

        outcome.Resolved.Select(r => r.AppId).Should().Equal(aId, bId);
        outcome.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_an_app_already_in_the_directory_or_provided_elsewhere()
    {
        SeedCoreAndSystemApp();
        await File.WriteAllBytesAsync(Path.Combine(_target, "already.app"),
            SyntheticApp.Build(SystemAppId, "Continia System Application", "Continia", "29.0.0.9"));

        var provided = new SymbolFeedRequest(
            [new SymbolDependency(CoreId, "Continia Core", "25.0.0.0")], _target, new HashSet<string>(), "29.0", null);
        var outcome = await Resolver().ResolveAsync(provided);
        outcome.Resolved.Should().ContainSingle(r => r.AppId == CoreId, "the system app was already there at a fitting version");

        _feeds.Requests.Clear();
        var sibling = new SymbolFeedRequest(
            [new SymbolDependency("cccccccc-0000-0000-0000-000000000003", "Our own app", "1.0.0.0")], _target,
            new HashSet<string> { "cccccccc-0000-0000-0000-000000000003" }, "29.0", null);
        (await Resolver().ResolveAsync(sibling)).Resolved.Should().BeEmpty();
        _feeds.Requests.Should().BeEmpty("a sibling the build compiles is never looked up");
    }

    [Fact]
    public async Task An_unknown_id_is_a_per_app_failure_naming_both_feeds()
    {
        SeedCoreAndSystemApp();
        const string unknown = "dddddddd-0000-0000-0000-000000000004";

        var outcome = await Resolver().ResolveAsync(Request("29.0",
            new SymbolDependency(unknown, "Someone's PTE", "1.0.0.0"),
            new SymbolDependency(CoreId, "Continia Core", "25.0.0.0")));

        outcome.Resolved.Should().Contain(r => r.AppId == CoreId, "one missing app doesn't stop the others");
        var missing = outcome.Unresolved.Should().ContainSingle().Subject;
        missing.AppId.Should().Be(unknown);
        missing.Name.Should().Be("Someone's PTE");
        missing.Reason.Should().Contain("AppSource symbol feed").And.Contain("Microsoft symbol feed");
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("continia-core")]
    [InlineData("1234/../../x")]
    public async Task A_dependency_id_that_is_not_a_guid_is_refused_without_touching_the_feeds_or_the_cache(string id)
    {
        var outcome = await Resolver().ResolveAsync(Request("29.0", new SymbolDependency(id, "Odd Extension", "1.0.0.0")));

        outcome.Resolved.Should().BeEmpty();
        outcome.Unresolved.Should().ContainSingle().Which.Reason.Should().Contain("not a valid app id");
        _feeds.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_feed_answering_5xx_is_a_per_app_failure_not_an_exception()
    {
        SeedCoreAndSystemApp();
        _feeds.Down.Add("appsource");
        _feeds.Down.Add("mssymbols");

        var outcome = await Resolver().ResolveAsync(Request("29.0", new SymbolDependency(CoreId, "Continia Core", "25.0.0.0")));

        outcome.Resolved.Should().BeEmpty();
        var missing = outcome.Unresolved.Should().ContainSingle().Subject;
        missing.Reason.Should().Contain("could not be reached").And.Contain("HTTP 503");
    }

    [Fact]
    public async Task Falls_back_to_the_Microsoft_feed_and_prefers_the_build_country()
    {
        const string cloudId = "58623bfa-0559-4bc2-ae1c-0979c29fd9e0";
        _feeds.Add("mssymbols", "Microsoft.IntelligentCloudBase.symbols." + cloudId, cloudId, "Intelligent Cloud Base", "27.0.1.1", application: "[27.0.0.0,27.1.0.0)");
        _feeds.Add("mssymbols", "Microsoft.IntelligentCloudBase.DK.symbols." + cloudId, cloudId, "Intelligent Cloud Base", "27.0.1.1", application: "[27.0.0.0,27.1.0.0)");
        _feeds.Add("mssymbols", "Microsoft.IntelligentCloudBase.DK.symbols." + cloudId, cloudId, "Intelligent Cloud Base", "28.5.1.1", application: "[28.5.0.0,28.6.0.0)");

        var outcome = await Resolver().ResolveAsync(Request("27.0", new SymbolDependency(cloudId, "Intelligent Cloud Base", "27.0.0.0")));

        var package = outcome.Resolved.Should().ContainSingle().Subject;
        package.Feed.Should().Be(AlSymbolFeedResolver.MicrosoftFeedName);
        package.Version.Should().Be("27.0.1.1");
        _feeds.Requests.Should().Contain(r => r.Contains("microsoft.intelligentcloudbase.dk.symbols.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_resolution_comes_from_the_cache_without_any_HTTP()
    {
        SeedCoreAndSystemApp();
        await Resolver().ResolveAsync(Request("29.0", new SymbolDependency(CoreId, "Continia Core", "25.0.0.0")));

        _feeds.Requests.Clear();
        foreach (var file in Directory.EnumerateFiles(_target)) File.Delete(file);
        var again = await Resolver().ResolveAsync(Request("29.0", new SymbolDependency(CoreId, "Continia Core", "25.0.0.0")));

        _feeds.Requests.Should().BeEmpty();
        again.Resolved.Should().HaveCount(2).And.OnlyContain(r => r.FromCache);
        again.Resolved.Select(r => r.Feed).Should().OnlyContain(f => f == AlSymbolFeedResolver.AppSourceFeedName);
        AppIdsInTarget().Should().BeEquivalentTo([CoreId, SystemAppId]);
    }

    [Fact]
    public async Task A_package_whose_app_is_a_different_app_is_refused()
    {
        _feeds.Add("appsource", CoreSymbols, "eeeeeeee-0000-0000-0000-000000000005", "Impostor", "29.0.0.1");

        var outcome = await Resolver().ResolveAsync(Request(null, new SymbolDependency(CoreId, "Continia Core", "1.0.0.0")));

        outcome.Resolved.Should().BeEmpty();
        outcome.Unresolved.Should().ContainSingle().Which.Reason.Should().Contain("not " + CoreId);
        Directory.EnumerateFiles(_target).Should().BeEmpty();
    }

    [Theory]
    [InlineData("../evil.app")]
    [InlineData("sub/dir.app")]
    [InlineData("..")]
    [InlineData("")]
    public void The_zip_slip_guard_refuses_anything_but_a_plain_file_name(string entry)
    {
        var act = () => AlSymbolFeedResolver.GuardedPath(_root, entry);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Nuspec_parsing_reads_app_id_dependencies_and_base_floors()
    {
        var spec = AlSymbolFeedResolver.ParseNuspec("""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>
              <id>ContiniaSoftware.ContiniaCore.symbols.4b915d7e-c02a-435f-85ab-649086c1e002</id>
              <title>Continia Core</title>
              <dependencies>
                <dependency id="Microsoft.Application.symbols" version="[27.0.0.0,27.1.0.0)" />
                <dependency id="Microsoft.Platform.symbols" version="27.0.0" />
                <dependency id="ContiniaSoftware.ContiniaSystemApplication.symbols.E4B442D0-E8E3-4210-BFCA-F1E66686CAA0" version="29.0.0" />
              </dependencies>
            </metadata></package>
            """);

        spec.Name.Should().Be("Continia Core");
        spec.BaseFloors.Should().Equal(new Version(27, 0, 0, 0), new Version(27, 0, 0));
        var dep = spec.Dependencies.Should().ContainSingle().Subject;
        dep.AppId.Should().Be(SystemAppId);
        dep.MinVersion.Should().Be(new Version(29, 0, 0));
    }

    [Fact]
    public void Search_hit_choice_is_the_single_hit_else_the_country_else_worldwide()
    {
        var id = "58623bfa-0559-4bc2-ae1c-0979c29fd9e0";
        string[] hits = [$"Microsoft.IntelligentCloudBase.AT.symbols.{id}", $"Microsoft.IntelligentCloudBase.symbols.{id}", $"Microsoft.IntelligentCloudBase.DK.symbols.{id}", "Unrelated.symbols.x"];

        AlSymbolFeedResolver.PickSearchHit(hits, id, null, "dk").Should().Be($"Microsoft.IntelligentCloudBase.DK.symbols.{id}");
        AlSymbolFeedResolver.PickSearchHit(hits, id, null, "w1").Should().Be($"Microsoft.IntelligentCloudBase.symbols.{id}");
        AlSymbolFeedResolver.PickSearchHit(hits, id, null, "xx").Should().Be($"Microsoft.IntelligentCloudBase.symbols.{id}");
        AlSymbolFeedResolver.PickSearchHit([$"ARQUICONSULTSA.ARQEBIIntegrationwithContinia.symbols.{id}"], id, null, "dk")
            .Should().Be($"ARQUICONSULTSA.ARQEBIIntegrationwithContinia.symbols.{id}");
        AlSymbolFeedResolver.PickSearchHit(["Something.else"], id, null, null).Should().BeNull();
    }

    [Fact]
    public void Manifest_only_read_returns_identity_and_null_for_non_apps()
    {
        var bytes = SyntheticApp.Build(CoreId, "Continia Core", "Continia", "29.0.0.1");
        using var app = new MemoryStream(bytes);
        var manifest = AppPackageReader.TryReadManifest(app);
        manifest!.AppId.Should().Be(Guid.Parse(CoreId));
        manifest.Version.Should().Be("29.0.0.1");

        using var notAnApp = new MemoryStream(new byte[100]);
        AppPackageReader.TryReadManifest(notAnApp).Should().BeNull();
        using var plainZip = new MemoryStream();
        using (var zip = new ZipArchive(plainZip, ZipArchiveMode.Create, leaveOpen: true)) zip.CreateEntry("x.txt");
        plainZip.Position = 0;
        AppPackageReader.TryReadManifest(plainZip).Should().BeNull();
    }
}
