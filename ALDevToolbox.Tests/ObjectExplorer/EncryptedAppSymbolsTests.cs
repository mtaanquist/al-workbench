using System.IO.Compression;
using System.Text;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// An encrypted vendor <c>.app</c> that ships its source is read from the AppSource
/// feed's symbols for that exact version, with the source beside it (#1803).
/// </summary>
public sealed class EncryptedAppSymbolsTests : IDisposable
{
    private const string BankingId = "2d3c5bd4-6a5f-4cbe-9d0b-7a8e0c51f1a3";
    private const string BankingPackage = "ContiniaSoftware.ContiniaBanking.symbols." + BankingId;

    private readonly TestDb _db = new();
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "encrypted-feed-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSymbolFeeds _feeds = new();

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_cache, recursive: true); } catch { /* best-effort */ }
    }

    private EncryptedAppSymbols Lookup() => new(
        new AlSymbolFeedResolver(_feeds, NullLogger<AlSymbolFeedResolver>.Instance, new AlSymbolFeedOptions
        {
            AppSourceFeedUrl = FakeSymbolFeeds.AppSourceIndex,
            MicrosoftFeedUrl = FakeSymbolFeeds.MicrosoftIndex,
            CacheDirectory = _cache,
        }),
        NullLogger<EncryptedAppSymbols>.Instance);

    private static MemoryStream SourceZip(string? appJsonVersion, string wrapper = "Continia Banking/")
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (appJsonVersion is not null)
            {
                using var w = new StreamWriter(zip.CreateEntry(wrapper + "app.json").Open());
                w.Write($$"""{ "id": "{{BankingId}}", "name": "Continia Banking", "publisher": "Continia Software", "version": "{{appJsonVersion}}" }""");
            }
            using (var w = new StreamWriter(zip.CreateEntry(wrapper + "Test/app.json").Open()))
            {
                // A nested test app's manifest must not be the one read.
                w.Write("""{ "id": "00000000-0000-0000-0000-000000000001", "name": "Banking Test", "publisher": "Continia Software", "version": "1.0.0.0" }""");
            }
            using (var w = new StreamWriter(zip.CreateEntry(wrapper + "src/BankAccount.Codeunit.al").Open()))
            {
                w.Write("codeunit 6175280 \"CBA Bank Account\"\n{\n}\n");
            }
        }
        ms.Position = 0;
        return ms;
    }

    /// <summary>The NAVX header with the NEA magic after it: what an encrypted marketplace .app starts with.</summary>
    private static MemoryStream EncryptedApp()
    {
        var buf = new byte[256];
        "NAVX"u8.CopyTo(buf);
        "NAVX.NEA"u8.CopyTo(buf.AsSpan(36));
        return new MemoryStream(buf);
    }

    private void PublishBanking(params string[] versions)
    {
        foreach (var v in versions)
        {
            _feeds.Add("appsource", BankingPackage, BankingId, "Continia Banking", v, application: "29.0.0")
                .SymbolReferenceJson = ReleaseDependencyChainTests.Symbols(("Codeunits", 6175280, "CBA Bank Account", null));
        }
    }

    [Fact]
    public async Task Finds_the_exact_version_the_source_zip_names()
    {
        PublishBanking("29.1.1.257932", "29.2.0.260000");

        await using var source = SourceZip("29.1.1.257932");
        var found = await Lookup().FindAsync(source);

        found.Reason.Should().BeNull();
        found.Version.Should().Be("29.1.1.257932");
        found.SymbolsApp.Should().NotBeNull();
        AppPackageReader.TryReadManifest(new MemoryStream(found.SymbolsApp!))!.Version.Should().Be("29.1.1.257932");
        source.Position.Should().Be(0, "the caller reads the source zip again");
    }

    [Fact]
    public async Task A_version_the_feed_does_not_have_is_reported_rather_than_a_newer_one_used()
    {
        PublishBanking("29.2.0.260000");

        await using var source = SourceZip("29.1.1.257932");
        var found = await Lookup().FindAsync(source);

        found.SymbolsApp.Should().BeNull();
        found.Reason.Should().StartWith("encrypted, and").And.Contain("29.1.1.257932");
    }

    [Fact]
    public async Task A_source_zip_without_an_app_json_is_reported()
    {
        PublishBanking("29.1.1.257932");

        await using var source = SourceZip(appJsonVersion: null);
        var found = await Lookup().FindAsync(source);

        found.SymbolsApp.Should().BeNull();
        found.Reason.Should().Contain("app.json");
        _feeds.Requests.Should().BeEmpty("nothing names an app to look up");
    }

    [Fact]
    public async Task A_source_file_that_is_not_a_zip_is_reported()
    {
        await using var notZip = new MemoryStream(Encoding.UTF8.GetBytes("not a zip"));
        var found = await Lookup().FindAsync(notZip);

        found.SymbolsApp.Should().BeNull();
        found.Reason.Should().StartWith("encrypted, and");
    }

    private static MemoryStream ZipOf(params (string Path, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(content);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static string AppJson(string id, string version) =>
        $$"""{ "id": "{{id}}", "name": "Continia Banking", "publisher": "Continia Software", "version": "{{version}}" }""";

    [Fact]
    public async Task A_version_ending_in_zero_matches_the_feed_listing_it_with_three_parts()
    {
        PublishBanking("29.1.0");

        await using var source = SourceZip("29.1.0.0");
        var found = await Lookup().FindAsync(source);

        found.Reason.Should().BeNull();
        found.SymbolsApp.Should().NotBeNull();
    }

    [Fact]
    public async Task An_oversized_app_json_is_reported_rather_than_thrown()
    {
        await using var source = ZipOf(("app.json", AppJson(BankingId, "29.1.1.257932") + new string(' ', 2 * 1024 * 1024)));
        var found = await Lookup().FindAsync(source);

        found.SymbolsApp.Should().BeNull();
        found.Reason.Should().StartWith("encrypted, and");
    }

    [Fact]
    public void The_root_app_json_wins_and_a_test_folder_beside_the_app_is_passed_over()
    {
        using (var rooted = ZipOf(("Test/app.json", AppJson("00000000-0000-0000-0000-000000000001", "1.0.0.0")),
                   ("app.json", AppJson(BankingId, "29.1.1.257932"))))
        {
            EncryptedAppSymbols.ReadAppJson(rooted)!.Id.Should().Be(BankingId);
        }

        using var siblings = ZipOf(("Test/app.json", AppJson("00000000-0000-0000-0000-000000000001", "1.0.0.0")),
            ("Banking/app.json", AppJson(BankingId, "29.1.1.257932")));
        EncryptedAppSymbols.ReadAppJson(siblings)!.Id.Should().Be(BankingId);
    }

    private ReleaseImportService NewService(Data.AppDbContext ctx, EncryptedAppSymbols? lookup) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext, new ALDevToolbox.Services.Translation.TranslationMemoryService(ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance), _db.NewQuotaGuard(ctx), NullLogger<TranslationImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext)),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext),
            encryptedAppSymbols: lookup);

    private async Task<int> BeginAsync(Data.AppDbContext ctx, EncryptedAppSymbols? lookup) =>
        await NewService(ctx, lookup).BeginReleaseAsync(new ReleaseImportMetadata(
            Label: "Continia Banking", Kind: "third_party", ParentReleaseId: null, ApplicationVersionId: null, Publisher: "Continia Software"));

    [Fact]
    public async Task An_encrypted_app_with_its_source_imports_from_the_feed_symbols_and_the_source()
    {
        PublishBanking("29.1.1.257932");
        var lookup = Lookup();
        await using var ctx = _db.NewContext();
        var releaseId = await BeginAsync(ctx, lookup);

        await using var app = EncryptedApp();
        await using var source = SourceZip("29.1.1.257932");
        var summary = await NewService(ctx, lookup).ProcessReleaseAsync(releaseId,
            [new AppFileUpload("Continia Banking 29.1.1.257932 - 29.0.app", app, source)]);

        summary.ModulesImported.Should().Be(1);
        summary.Unreadable.Should().BeEmpty();

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        release.Status.Should().Be("ready");
        release.SkippedAppsJson.Should().BeNull();
        var module = await read.OeModules.AsNoTracking().SingleAsync(m => m.ReleaseId == releaseId);
        module.AppId.Should().Be(Guid.Parse(BankingId));
        module.Version.Should().Be("29.1.1.257932");
        (await read.OeModuleObjects.AsNoTracking().CountAsync(o => o.ModuleId == module.Id)).Should().Be(1);
        (await read.OeModuleFiles.AsNoTracking().CountAsync(f => f.ModuleId == module.Id))
            .Should().BeGreaterThan(0, "the source zip's files are kept beside the feed's symbols");
    }

    [Fact]
    public async Task An_encrypted_app_the_feed_does_not_have_is_left_out_with_the_reason()
    {
        PublishBanking("29.2.0.260000");
        var lookup = Lookup();
        await using var ctx = _db.NewContext();
        var releaseId = await BeginAsync(ctx, lookup);

        await using var encrypted = EncryptedApp();
        await using var source = SourceZip("29.1.1.257932");
        await using var dk = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer", "Microsoft_DK_Core.app"));
        var summary = await NewService(ctx, lookup).ProcessReleaseAsync(releaseId,
        [
            new AppFileUpload("Banking.app", encrypted, source),
            new AppFileUpload("Microsoft_DK_Core.app", dk, null),
        ]);

        summary.ModulesImported.Should().Be(1);
        summary.Unreadable.Should().ContainSingle().Which.Reason.Should().Contain("29.1.1.257932");
    }

    [Fact]
    public async Task An_encrypted_app_without_source_is_left_out_without_asking_the_feed()
    {
        PublishBanking("29.1.1.257932");
        var lookup = Lookup();
        await using var ctx = _db.NewContext();
        var releaseId = await BeginAsync(ctx, lookup);

        await using var encrypted = EncryptedApp();
        await using var dk = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer", "Microsoft_DK_Core.app"));
        var summary = await NewService(ctx, lookup).ProcessReleaseAsync(releaseId,
        [
            new AppFileUpload("Banking.app", encrypted, null),
            new AppFileUpload("Microsoft_DK_Core.app", dk, null),
        ]);

        summary.Unreadable.Should().ContainSingle().Which.Reason.Should().Be(ReleaseSkippedApp.EncryptedReason);
        _feeds.Requests.Should().BeEmpty();
    }
}
