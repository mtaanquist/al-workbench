using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The pure selection logic of <see cref="AlCompilerProvisioner"/>: which package
/// version to install (newest, or a pin) and which target-framework folder to
/// extract (prefer net10.0 so it runs natively on the runtime image, else the
/// highest netN) - and, against a fake NuGet feed and a temp install root, the
/// side-by-side installs a next-major build needs (#993).
/// </summary>
public sealed class AlCompilerProvisionerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alc-provisioner-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private AlCompilerProvisioner NewProvisioner(FakeNuGet feed, string? pin = null) =>
        new(feed, NullLogger<AlCompilerProvisioner>.Instance,
            new AlCompilerOptions { InstallDirectory = _root, VersionPin = pin });

    [Fact]
    public async Task A_beta_installs_beside_the_stable_compiler_and_each_line_keeps_its_own()
    {
        var feed = new FakeNuGet("17.0.34.45391", "18.0.41.62505", "30.0.42.32495-beta");
        var provisioner = NewProvisioner(feed);

        var stable = await provisioner.ResolveAsync();
        var beta = await provisioner.ResolveAsync(prerelease: true);

        stable!.Version.Should().Be("18.0.41.62505");
        stable.AlcPath.Should().Be(Path.Combine(_root, "18.0.41.62505", "bin", "alc.dll"));
        beta!.Version.Should().Be("30.0.42.32495-beta");
        beta.AlcPath.Should().Be(Path.Combine(_root, "30.0.42.32495-beta", "bin", "alc.dll"));
        File.Exists(stable.AlcPath).Should().BeTrue("installing the beta must not replace the stable compiler");

        // Installed now: neither line downloads again.
        (await provisioner.ResolveAsync())!.Version.Should().Be("18.0.41.62505");
        (await provisioner.ResolveAsync(prerelease: true))!.Version.Should().Be("30.0.42.32495-beta");
        feed.Downloads.Should().Equal("18.0.41.62505", "30.0.42.32495-beta");
    }

    [Fact]
    public async Task An_install_at_the_root_moves_under_its_version_without_downloading()
    {
        // The layout every deployment has before #993: one compiler, at the root.
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "alc.dll"), "compiler");
        File.WriteAllText(Path.Combine(_root, "installed.json"),
            JsonSerializer.Serialize(new { Version = "18.0.41.62505", Tfm = "net10.0", Entry = "alc.dll" }));
        var feed = new FakeNuGet("18.0.41.62505", "18.1.0.1");

        var resolved = await NewProvisioner(feed).ResolveAsync();

        resolved!.Version.Should().Be("18.0.41.62505", "a newer stable on the feed never replaces the installed one by itself");
        resolved.AlcPath.Should().Be(Path.Combine(_root, "18.0.41.62505", "bin", "alc.dll"));
        File.ReadAllText(resolved.AlcPath).Should().Be("compiler");
        File.Exists(Path.Combine(_root, "installed.json")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "bin")).Should().BeFalse();
        feed.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_newer_beta_replaces_the_older_one_and_leaves_stable_alone()
    {
        var feed = new FakeNuGet("18.0.41.62505", "30.0.42.11883-beta");
        var provisioner = NewProvisioner(feed);
        await provisioner.ResolveAsync();
        await provisioner.ResolveAsync(prerelease: true);

        feed.Versions.Add("30.0.42.32495-beta");
        var beta = await provisioner.ResolveAsync(prerelease: true);

        beta!.Version.Should().Be("30.0.42.32495-beta");
        Directory.Exists(Path.Combine(_root, "30.0.42.11883-beta")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "18.0.41.62505")).Should().BeTrue();
    }

    [Fact]
    public async Task A_next_major_build_takes_the_stable_compiler_when_no_beta_is_ahead_of_it()
    {
        // The beta line shipped: its leftover prerelease sits below the stable release.
        var feed = new FakeNuGet("18.0.37.11445-beta", "18.0.41.62505");

        var resolved = await NewProvisioner(feed).ResolveAsync(prerelease: true);

        resolved!.Version.Should().Be("18.0.41.62505");
    }

    [Fact]
    public async Task The_pin_selects_the_stable_line_only()
    {
        var feed = new FakeNuGet("17.0.34.45391", "18.0.41.62505", "30.0.42.32495-beta");
        var provisioner = NewProvisioner(feed, pin: "17.0.34.45391");

        (await provisioner.ResolveAsync())!.Version.Should().Be("17.0.34.45391");
        (await provisioner.ResolveAsync(prerelease: true))!.Version.Should().Be("30.0.42.32495-beta");
    }

    [Fact]
    public async Task With_nuget_unreachable_the_installed_compiler_still_builds_whatever_the_pin_says()
    {
        var feed = new FakeNuGet("18.0.41.62505");
        await NewProvisioner(feed).ResolveAsync();
        feed.Down = true;

        (await NewProvisioner(feed, pin: "18.1.0.1").ResolveAsync())!.Version.Should().Be("18.0.41.62505");
        (await NewProvisioner(feed).ResolveAsync(prerelease: true))!.Version.Should().Be("18.0.41.62505");
    }

    [Fact]
    public async Task A_package_without_a_compiler_leaves_no_folder_behind()
    {
        var feed = new FakeNuGet("18.0.41.62505", "30.0.1.1-beta") { Empty = { "30.0.1.1-beta" } };

        var resolved = await NewProvisioner(feed).ResolveAsync(prerelease: true);

        resolved!.Version.Should().Be("18.0.41.62505", "no beta installs, so the stable compiler answers");
        Directory.Exists(Path.Combine(_root, "30.0.1.1-beta")).Should().BeFalse();
    }

    [Fact]
    public void PickPrereleaseCandidates_takes_the_newest_betas_ahead_of_stable()
    {
        var versions = new[] { "17.0.27.27275-beta", "17.0.34.45391", "18.0.41.62505", "30.0.42.11883-beta", "30.0.42.32495-beta" };
        AlCompilerProvisioner.PickPrereleaseCandidates(versions)
            .Should().Equal(["30.0.42.32495-beta", "30.0.42.11883-beta"], "the 17.0 beta is a leftover of a shipped line");
        AlCompilerProvisioner.PickPrereleaseCandidates(["18.0.41.62505"]).Should().BeEmpty();
        AlCompilerProvisioner.PickPrereleaseCandidates(["30.0.1.1-beta/../../etc"]).Should().BeEmpty("a version that is not a plain folder name is never installed");
    }

    [Fact]
    public void PickNewest_returns_the_newest_stable_when_no_pin()
    {
        // The NuGet flat-container index is SemVer-ascending, so newest is last;
        // a prerelease is never the default (#921), only a pin selects one.
        var versions = new[] { "16.2.28.57946", "17.0.27.27275-beta", "17.0.34.45391", "18.0.37.11445-beta" };
        AlCompilerProvisioner.PickNewest(versions, pin: null).Should().Be("17.0.34.45391");
        AlCompilerProvisioner.PickNewest(new[] { "18.0.37.11445-beta" }, pin: null).Should().Be("18.0.37.11445-beta",
            "a feed with nothing but prereleases still yields something");
    }

    [Fact]
    public void PickNewest_honours_a_matching_pin()
    {
        var versions = new[] { "16.2.28.57946", "17.0.27.27275-beta", "18.0.37.11445-beta" };
        AlCompilerProvisioner.PickNewest(versions, pin: "16.2.28.57946").Should().Be("16.2.28.57946");
    }

    [Fact]
    public void PickNewest_returns_null_when_pin_absent_or_no_versions()
    {
        AlCompilerProvisioner.PickNewest(new[] { "16.2.28.57946" }, pin: "99.0.0.0").Should().BeNull();
        AlCompilerProvisioner.PickNewest(Array.Empty<string>(), pin: null).Should().BeNull();
    }

    [Fact]
    public void PickTfm_prefers_net10_for_native_runtime()
    {
        var entries = new[]
        {
            "lib/net8.0/alc", "lib/net8.0/altool.dll",
            "lib/net10.0/alc", "lib/net10.0/altool.dll",
            "package/services/metadata/core-properties/x.psmdcp",
        };
        AlCompilerProvisioner.PickTfm(entries).Should().Be("net10.0");
    }

    [Fact]
    public void PickTfm_falls_back_to_highest_net_when_no_net10()
    {
        // Numeric ordering — net8.0 beats net6.0, and beats a lexical trap.
        var entries = new[] { "lib/net6.0/alc", "lib/net8.0/alc" };
        AlCompilerProvisioner.PickTfm(entries).Should().Be("net8.0");
    }

    [Fact]
    public void PickTfm_returns_null_when_no_compiler_in_either_layout()
    {
        // tools/<tfm>/alc is neither layout: the main package nests one more folder ("any").
        AlCompilerProvisioner.PickTfm(new[] { "tools/net8.0/alc", "README.md" }).Should().BeNull();
    }

    // ── The main package's layout (#921) ────────────────────────────────

    [Fact]
    public void PickLayout_takes_the_framework_dependent_compiler_from_the_main_package()
    {
        // Microsoft.Dynamics.BusinessCentral.Development.Tools 18.0.41.62505, as published.
        var entries = new[]
        {
            "tools/net8.0/any/alc.dll", "tools/net8.0/any/alc.runtimeconfig.json",
            "tools/net10.0/any/alc.dll", "tools/net10.0/any/alc.exe", "tools/net10.0/any/altool.dll",
            "templates/README.md",
        };
        AlCompilerProvisioner.PickLayout(entries).Should().Be(new CompilerLayout("tools/net10.0/any/", "net10.0", "alc.dll"));
    }

    [Fact]
    public void PickLayout_prefers_the_framework_dependent_compiler_over_an_apphost_in_the_same_package()
    {
        var entries = new[] { "lib/net10.0/alc", "tools/net10.0/any/alc.dll" };
        AlCompilerProvisioner.PickLayout(entries)!.Entry.Should().Be("alc.dll");
    }

    [Fact]
    public void PickLayout_returns_null_for_an_analyzers_only_package()
    {
        // The .Linux package from 18.x: cops and their deps.json, no compiler.
        var entries = new[]
        {
            "lib/net10.0/Microsoft.Dynamics.Nav.CodeCop.dll", "lib/net10.0/Microsoft.Dynamics.Nav.AppSourceCop.dll",
            "lib/net8.0/Microsoft.Dynamics.Nav.UICop.dll",
        };
        AlCompilerProvisioner.PickLayout(entries).Should().BeNull(
            "an install must not succeed on a package that holds no alc, which is how #921 broke fresh volumes");
    }

    [Fact]
    public void PickCandidates_walks_the_newest_stable_few_so_a_compilerless_package_can_be_skipped()
    {
        var versions = new[] { "16.2.28.57946", "17.0.34.45391", "18.0.41.39415", "18.0.41.62505", "30.0.42.11883-beta" };
        AlCompilerProvisioner.PickCandidates(versions, pin: null)
            .Should().Equal(["18.0.41.62505", "18.0.41.39415", "17.0.34.45391"], "the beta is skipped and the stable ones walk newest first");
        AlCompilerProvisioner.PickCandidates(versions, pin: "30.0.42.11883-beta").Should().Equal("30.0.42.11883-beta");
        AlCompilerProvisioner.PickCandidates(versions, pin: "99.0.0.0").Should().BeEmpty();
    }

    [Fact]
    public void A_framework_dependent_compiler_runs_through_dotnet_and_an_apphost_runs_itself()
    {
        var dll = new AlCompilerInfo("/var/lib/aldevtoolbox/altool/bin/alc.dll", NeedsRollForward: false, "18.0.41.62505");
        dll.FileName.Should().Be("dotnet");
        dll.LeadingArguments.Should().Equal("/var/lib/aldevtoolbox/altool/bin/alc.dll");

        var apphost = new AlCompilerInfo("/var/lib/aldevtoolbox/altool/bin/alc", NeedsRollForward: false, "17.0.34.45391");
        apphost.FileName.Should().Be(apphost.AlcPath);
        apphost.LeadingArguments.Should().BeEmpty();
    }
}

/// <summary>
/// nuget.org as far as the provisioner reads it: the flat-container index, a
/// package per version holding <c>tools/net10.0/any/alc.dll</c>, and the
/// registration leaf and catalog entry that publish its SHA-512.
/// </summary>
internal sealed class FakeNuGet : HttpMessageHandler, IHttpClientFactory
{
    public FakeNuGet(params string[] versions) => Versions = versions.ToList();

    /// <summary>What the index lists, SemVer-ascending like nuget.org's.</summary>
    public List<string> Versions { get; }

    /// <summary>Versions whose package carries no compiler.</summary>
    public HashSet<string> Empty { get; } = new();

    /// <summary>Every package version downloaded, in order.</summary>
    public List<string> Downloads { get; } = new();

    /// <summary>When set, every request answers 503.</summary>
    public bool Down { get; set; }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var url = request.RequestUri!.ToString();
        const string Package = AlCompilerProvisioner.PackageId;
        if (url.EndsWith($"/{Package}/index.json", StringComparison.Ordinal))
            return Json(new { versions = Versions });
        if (url.EndsWith(".nupkg", StringComparison.Ordinal))
        {
            var version = Versions.Single(v => url.EndsWith($"/{v.ToLowerInvariant()}/{Package}.{v.ToLowerInvariant()}.nupkg", StringComparison.Ordinal));
            Downloads.Add(version);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PackageBytes(version)) });
        }
        if (url.Contains("/registration5-gz-semver2/", StringComparison.Ordinal))
        {
            var version = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
            return Json(new { catalogEntry = $"https://catalog.test/{version}.json" });
        }
        if (url.StartsWith("https://catalog.test/", StringComparison.Ordinal))
        {
            var version = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
            var original = Versions.Single(v => v.Equals(version, StringComparison.OrdinalIgnoreCase));
            return Json(new { packageHashAlgorithm = "SHA512", packageHash = Convert.ToBase64String(SHA512.HashData(PackageBytes(original))) });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static Task<HttpResponseMessage> Json(object body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) });

    /// <summary>Deterministic per version, so the published hash matches the download.</summary>
    private byte[] PackageBytes(string version)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(Empty.Contains(version) ? "tools/net10.0/any/analyzers.dll" : "tools/net10.0/any/alc.dll");
            entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("alc " + version);
        }
        return ms.ToArray();
    }
}
