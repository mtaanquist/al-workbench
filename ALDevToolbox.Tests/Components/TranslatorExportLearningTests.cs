using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Translation;
using ALDevToolbox.Services.Translation.Providers;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using TranslatorPage = ALDevToolbox.Components.Pages.Translator;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// What the Translator asks the translation memory to learn when it exports
/// (#1540). The export endpoint learns with no repository attached, and a pair
/// with none is suggested to the whole organisation - so a file that came from
/// a repository, which may belong to a Private solution, must send an empty
/// learn list, both while it is open and after a reload restores it. A file
/// from anywhere else still teaches the memory its edits.
///
/// <para>The page is rendered for real with its JS module stood in for, so
/// these follow the wiring - the repository origin handed to the browser's
/// session record, the restore reading it back, and the list the export
/// posts - rather than only the helpers that decide.</para>
/// </summary>
public sealed class TranslatorExportLearningTests : IAsyncDisposable
{
    private const int UserId = 1540;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string Repo = "cronus-dk/private-app";
    private const string SourcePath = "App/Translations/App.g.xlf";
    private const string UnitId = "Table 1 - Field 2 - Property Caption";

    private const string GeneratedXliff = """
        <?xml version="1.0" encoding="utf-8"?>
        <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
          <file datatype="xml" source-language="en-US" target-language="en-US" original="App">
            <body>
              <group id="body">
                <trans-unit id="Table 1 - Field 2 - Property Caption" size-unit="char">
                  <source>Amount</source>
                </trans-unit>
              </group>
            </body>
          </file>
        </xliff>
        """;

    private static readonly string DanishXliff = GeneratedXliff
        .Replace("target-language=\"en-US\"", "target-language=\"da-DK\"", StringComparison.Ordinal);

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly BunitJSModuleInterop _module;
    private readonly FakeGitHubApi _api;

    public TranslatorExportLearningTests()
    {
        _ctx.AddAuthorization().SetAuthorized("translator@cronus.example");
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _module = _ctx.JSInterop.SetupModule("./Components/Pages/Translator.razor.js");
        _module.Mode = JSRuntimeMode.Loose;
        _module.Setup<bool>("exportNow", _ => true).SetResult(true);

        _api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, $"/repos/{Repo}/git/trees/main", HttpStatusCode.OK,
                $$"""{"sha":"tree-sha","truncated":false,"tree":[{"path":"{{SourcePath}}","type":"blob","sha":"blob-sha"}]}""")
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(SourcePath, GeneratedXliff, "loaded-sha"))
            .On(HttpMethod.Get, $"/repos/{Repo}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(Repo));

        var services = _ctx.Services;
        services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        services.AddDisplayTimeZone(_db);
        services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _db.AddStorageServices(services);
        services.AddScoped<OrganizationConfigService>();
        services.AddDataProtection();
        services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddScoped<TranslationMemoryService>();
        services.AddScoped<TranslationSuggestionCoordinator>();
        services.AddScoped(sp => _db.NewMachineTranslationSettingsService(sp.GetRequiredService<ALDevToolbox.Data.AppDbContext>()));
        services.AddSingleton<IMachineTranslationProviderFactory, NoMachineTranslation>();
        services.AddScoped<MachineTranslationService>();
        services.AddScoped(sp => _db.NewGitHubAppClient(sp.GetRequiredService<ALDevToolbox.Data.AppDbContext>(), _api));
        services.AddScoped(sp => _db.NewGitHubAccessService(
            sp.GetRequiredService<ALDevToolbox.Data.AppDbContext>(), sp.GetRequiredService<GitHubAppClient>()));
        services.AddScoped(sp => _db.NewGitHubRepositoryService(
            sp.GetRequiredService<ALDevToolbox.Data.AppDbContext>(), sp.GetRequiredService<GitHubAppClient>(),
            sp.GetRequiredService<GitHubAccessService>()));
        services.AddScoped(sp => _db.NewGitHubTranslationService(
            sp.GetRequiredService<ALDevToolbox.Data.AppDbContext>(), sp.GetRequiredService<GitHubAppClient>(),
            sp.GetRequiredService<GitHubAccessService>()));

        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "translator@cronus.example",
            DisplayName = "translator@cronus.example",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.Renderer.DisposeAsync();
        _db.WaitForQueriesToSettle();
        await _ctx.DisposeAsync();
        _db.Dispose();
    }

    [Fact]
    public async Task Export_a_copy_of_a_file_opened_from_a_repository_sends_nothing_to_learn()
    {
        await ReadyAsync();
        var cut = _ctx.Render<TranslatorPage>();

        var picker = cut.FindComponent<RepositoryPicker>();
        await cut.InvokeAsync(() => picker.Instance.SelectedChanged.InvokeAsync(new GitHubRepositorySummary(
            Repo, OrgLogin, "private-app", "main", IsPrivate: true, Description: null,
            HtmlUrl: $"https://github.com/{Repo}", CloneUrl: $"https://github.com/{Repo}.git")));
        cut.WaitForElement(".trepo__file").Click();
        Type(cut, "Beloeb");

        // The browser's record is told where the file came from, so a reload knows.
        var stored = _module.Invocations["putBaseStream"].Single().Arguments[4];
        JsonSerializer.Serialize(stored).Should().Contain(Repo).And.Contain("App/Translations/App.");

        cut.FindAll("button").Single(b => b.TextContent.Contains("Export a copy")).Click();

        var export = ExportCall(cut);
        ((string)export.Arguments[1]!).Should().Contain("Beloeb", "the copy still carries the edit");
        export.Arguments[2].Should().Be("[]", "the endpoint would learn it with no repository attached");
    }

    [Fact]
    public void A_restored_session_of_a_repository_file_still_sends_nothing_to_learn()
    {
        var cut = Restore(new TranslatorPage.RepositoryOriginDto { Repository = Repo, Path = "App/Translations/App.da-DK.xlf" });

        ExportRestored(cut).Arguments[2].Should().Be("[]");
    }

    [Fact]
    public void A_restored_session_of_a_file_from_elsewhere_teaches_its_edits()
    {
        var cut = Restore(repo: null);

        ExportRestored(cut).Arguments[2].Should().Be(JsonSerializer.Serialize(new[] { UnitId }));
    }

    [Fact]
    public void An_uploaded_file_teaches_its_edits_and_records_no_repository()
    {
        var cut = _ctx.Render<TranslatorPage>();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(DanishXliff, "App.da-DK.xlf"));
        cut.WaitForElement("textarea.tr-tgtarea");
        Type(cut, "Beloeb");

        _module.Invocations["putBaseStream"].Single().Arguments.Skip(4).FirstOrDefault()
            .Should().BeNull("a file from disk has no repository to remember");

        cut.FindAll("button").First(b => b.ClassList.Contains("btn--primary") && b.TextContent.Contains("Export")).Click();

        var export = ExportCall(cut);
        export.Arguments[2].Should().Be(JsonSerializer.Serialize(new[] { UnitId }));
    }

    private IRenderedComponent<TranslatorPage> Restore(TranslatorPage.RepositoryOriginDto? repo)
    {
        _module.Setup<TranslatorPage.SessionPeek?>("peek").SetResult(new TranslatorPage.SessionPeek
        {
            FileName = "App.da-DK.xlf",
            SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        _module.Setup<TranslatorPage.SessionMeta?>("loadMeta").SetResult(new TranslatorPage.SessionMeta
        {
            FileName = "App.da-DK.xlf",
            SourceLang = "en-US",
            TargetLang = "da-DK",
            Edits = new() { [UnitId] = new TranslatorPage.EditDto { T = "Beloeb", S = "translated" } },
            Repo = repo,
        });
        _module.Setup<IJSStreamReference>("loadOriginalStream").SetResult(new TextStreamReference(DanishXliff));

        var cut = _ctx.Render<TranslatorPage>();
        cut.WaitForAssertion(() =>
            cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Continue"));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Continue").Click();
        // The restored edit is already in the overlay, so the unsaved count shows on Export.
        cut.WaitForAssertion(() => cut.FindAll("button.btn--primary")
            .Should().Contain(b => b.TextContent.Contains("Export .xlf (1)")));
        return cut;
    }

    private JSRuntimeInvocation ExportCall(IRenderedComponent<TranslatorPage> cut)
    {
        cut.WaitForAssertion(() => _module.Invocations["exportNow"].Should().ContainSingle());
        return _module.Invocations["exportNow"].Single();
    }

    private JSRuntimeInvocation ExportRestored(IRenderedComponent<TranslatorPage> cut)
    {
        cut.FindAll("button").First(b => b.ClassList.Contains("btn--primary") && b.TextContent.Contains("Export")).Click();
        var export = ExportCall(cut);
        ((string)export.Arguments[1]!).Should().Contain("Beloeb", "the restored edit is in the copy");
        return export;
    }

    private static void Type(IRenderedComponent<TranslatorPage> cut, string text) =>
        cut.WaitForElement("textarea.tr-tgtarea").Input(text);

    private async Task ReadyAsync()
    {
        using (var rsa = RSA.Create(2048))
        {
            await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
                AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
                ClientSecret: "s3cr3t", ClearClientSecret: false,
                PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
        }
        await using (var ctx = _db.NewContext())
        {
            ctx.OrganizationSettings.Add(new OrganizationSettings
            {
                OrganizationId = TestDb.DefaultOrgId,
                GitHubInstallationId = InstallationId,
                GitHubOrgLogin = OrgLogin,
                GitHubConnectedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        var oauth = new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson());
        await using (var ctx = _db.NewContext())
        {
            await _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, oauth)).LinkAsync("the-code");
        }
    }

    /// <summary>Machine translation is off for the org, so nothing ever builds a provider.</summary>
    private sealed class NoMachineTranslation : IMachineTranslationProviderFactory
    {
        public IMachineTranslationProvider Create(ResolvedMtSettings settings) =>
            throw new InvalidOperationException("Machine translation is off in these tests.");
    }

    /// <summary>The browser's stored original, as the restore streams it back.</summary>
    private sealed class TextStreamReference : IJSStreamReference
    {
        private readonly byte[] _bytes;
        public TextStreamReference(string text) => _bytes = Encoding.UTF8.GetBytes(text);
        public long Length => _bytes.Length;
        public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(_bytes));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
