using System.Net;
using ALDevToolbox.Components.Pages.Admin;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Translation;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Translation memory page as an editor meets it (issue #631): its loading,
/// empty and populated states, the Repository column that links a learned pair back
/// to the file it came from, and the rule that "Read from repositories" is
/// offered only once the organisation has connected a GitHub organisation.
///
/// <para>A screenshot is not possible in this environment, so these renders are
/// the evidence that the page shows what the copy promises.</para>
/// </summary>
public sealed class AdminTranslationMemoryTests : IDisposable
{
    private const long InstallationId = 42;

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    /// <summary>
    /// Keeps what the page logged. The page catches its own load failures, so
    /// without this a search that threw is indistinguishable from a slow one -
    /// which is what left #764 undiagnosable for two CI runs (same reasoning
    /// as #739).
    /// </summary>
    private readonly CapturingLoggerProvider _logs = new();

    public AdminTranslationMemoryTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("editor@cronus.example");
        auth.SetRoles("Editor");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<TranslationMemoryService>();
        _ctx.Services.AddScoped<TranslationMemoryIngestService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(
            Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(_logs)));
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Logger<>));
        _db.AddStorageServices(_ctx.Services);
        _db.AddGitHubServices(_ctx.Services, ConnectedApi());
        _ctx.Services.AddScoped<ALDevToolbox.Services.Organizations.OrganizationConfigService>();
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void An_empty_memory_says_how_it_fills_up_and_offers_a_way_to_start()
    {
        var cut = _ctx.Render<AdminTranslationMemory>();

        cut.WaitForAssertion(() =>
        {
            // Nothing has been learned yet, which is a different situation from a
            // search that matched nothing - and it does not mention filters.
            cut.Markup.Should().Contain("No translations learned yet");
            cut.Markup.Should().NotContain("Nothing matches");
            cut.FindAll("a").Any(a => a.TextContent.Contains("Import XLIFF")).Should().BeTrue();
        });
    }

    [Fact]
    public async Task A_search_that_matches_nothing_offers_a_way_back_to_everything()
    {
        await SeedEntryAsync();

        var cut = _ctx.Render<AdminTranslationMemory>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());

        // The first suspect for #764 was #739's shape - a search starting while
        // the page's own hydration was still on the wire, on the one scoped
        // DbContext. Measurement says no: the page probes GitHub *after* the
        // list loads, but ComponentBase renders nothing between the two, so by
        // the time a row exists the context is idle (checked with every read
        // artificially slowed, and it was idle every time). This makes that a
        // stated precondition rather than an assumption, and would catch a
        // future load that renders before it has finished.
        _db.CommandTracker.WaitUntilIdle(TimeSpan.FromSeconds(10)).Should().BeTrue(
            "the page has to be idle before the search is submitted");

        cut.Find(".filter-bar__search").Input("nothing-like-this-exists");
        cut.Find(".filter-bar button[type=submit]").Click();

        // Wait for the search to reach *either* outcome, then assert it was the
        // one we want. Waiting only for "Nothing matches" meant a failed search
        // burned the full 30-second bUnit timeout and reported nothing but "the
        // assertion did not pass".
        cut.WaitForAssertion(() => cut.Markup.Should().Match(
            m => m.Contains("Nothing matches") || m.Contains("could not be loaded"),
            "the search renders the empty state or the failure banner when it finishes"));

        cut.Markup.Should().Contain("Nothing matches",
            "the search must succeed; the page reported a failure instead. What it logged:\n"
            + _logs.ErrorsForFailureMessage());
        cut.Find(".empty-state__action button").TextContent.Should().Contain("Clear filters");

        cut.Find(".empty-state__action button").Click();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
    }

    [Fact]
    public async Task A_pair_learned_from_a_repository_links_to_the_file_it_came_from()
    {
        await SeedEntryAsync(
            sourceRepository: "cronus-dk/customer-app",
            sourcePath: "PaymentImport/Translations/PaymentImport.da-DK.xlf");

        var cut = _ctx.Render<AdminTranslationMemory>();

        cut.WaitForAssertion(() =>
        {
            var link = cut.FindAll("td.tr-mem-from a").Should().ContainSingle().Subject;
            link.GetAttribute("href").Should().Be(
                "https://github.com/cronus-dk/customer-app/blob/HEAD/PaymentImport/Translations/PaymentImport.da-DK.xlf");
            link.GetAttribute("target").Should().Be("_blank");
            link.GetAttribute("rel").Should().Contain("noopener");
        });
    }

    [Fact]
    public async Task A_pair_that_came_from_an_upload_shows_no_link()
    {
        await SeedEntryAsync();

        var cut = _ctx.Render<AdminTranslationMemory>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody tr").Should().ContainSingle();
            cut.FindAll("td.tr-mem-from a").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task Refresh_from_repositories_is_offered_once_a_github_organisation_is_connected()
    {
        await ConnectAsync();

        var cut = _ctx.Render<AdminTranslationMemory>();

        cut.WaitForAssertion(() =>
            cut.FindAll("button").Any(b => b.TextContent.Contains("Read from repositories"))
                .Should().BeTrue());
    }

    [Fact]
    public void Refresh_from_repositories_is_not_offered_when_nothing_is_connected()
    {
        var cut = _ctx.Render<AdminTranslationMemory>();
        // The empty memory first: the page draws it only once its whole load, the
        // GitHub read included, has come back. Before that the button is absent anyway.
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No translations learned yet"));

        cut.FindAll("button").Any(b => b.TextContent.Contains("Read from repositories"))
            .Should().BeFalse("a button whose only answer would be 'nothing is connected' is worse than none");
    }

    // --- helpers ------------------------------------------------------------

    /// <summary>A GitHub that can mint an installation token and reports no files.</summary>
    private static FakeGitHubApi ConnectedApi() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson());

    private async Task ConnectAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = TestDb.DefaultOrgId,
            GitHubInstallationId = InstallationId,
            GitHubOrgLogin = "cronus-dk",
            GitHubConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
        await _db.NewSystemSettingsService(ctx).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: System.Security.Cryptography.RSA.Create(2048).ExportRSAPrivateKeyPem(),
            ClearPrivateKey: false));
    }

    private async Task SeedEntryAsync(string? sourceRepository = null, string? sourcePath = null)
    {
        await using var ctx = _db.NewContext();
        ctx.TranslationMemory.Add(new TranslationMemoryEntry
        {
            OrganizationId = TestDb.DefaultOrgId,
            SourceLanguage = "en-US",
            TargetLanguage = "da-DK",
            SourceText = "Posting Date",
            TargetText = "Bogføringsdato",
            SourceHash = Guid.NewGuid().ToString("N"),
            TargetHash = Guid.NewGuid().ToString("N"),
            Kind = "caption",
            Origin = "customer-app / PaymentImport",
            SourceRepository = sourceRepository,
            SourcePath = sourcePath,
            HitCount = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }
}
