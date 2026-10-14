using System.Net;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Translation;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Translator;

/// <summary>
/// The nightly sweep behind the translation-memory ingest (issue #631).
///
/// <para>The rule worth pinning is the fence: the sweep enumerates
/// organisations - a table with no tenant filter - and then enters each one's
/// <see cref="AmbientOrganizationScope"/> before reading anything of theirs, so
/// no <c>IgnoreQueryFilters()</c> is needed anywhere in the feature. These
/// tests prove that an organisation is actually entered (its own rows are
/// written) and that an organisation with no GitHub connection costs nothing.</para>
/// </summary>
public sealed class TranslationMemoryIngestSchedulerTests : IDisposable
{
    private const long InstallationId = 42;
    private const string Repo = "cronus-dk/customer-app";
    private const string FilePath = "PaymentImport/Translations/PaymentImport.da-DK.xlf";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_sweep_ingests_for_a_connected_organisation()
    {
        await ConnectAsync(TestDb.DefaultOrgId);
        await TrackAsync(TestDb.DefaultOrgId, $"https://github.com/{Repo}.git");

        var api = Api()
            .On(HttpMethod.Get, $"/repos/{Repo}/git/trees/", HttpStatusCode.OK,
                $"{{\"truncated\":false,\"tree\":[{{\"path\":\"{FilePath}\",\"type\":\"blob\",\"sha\":\"sha-1\"}}]}}")
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, Xliff));

        var swept = await NewScheduler(api).SweepAsync(CancellationToken.None);

        swept.Should().Be(1);
        await using var ctx = _db.NewContext();
        var entry = await ctx.TranslationMemory.SingleAsync();
        entry.OrganizationId.Should().Be(TestDb.DefaultOrgId,
            "the sweep wrote inside the organisation's own ambient scope");
        entry.SourceRepository.Should().Be(Repo);
    }

    [Fact]
    public async Task An_organisation_with_no_github_connection_is_skipped_without_calling_github()
    {
        await TrackAsync(TestDb.DefaultOrgId, $"https://github.com/{Repo}.git");
        var api = Api();

        (await NewScheduler(api).SweepAsync(CancellationToken.None)).Should().Be(0);
        api.Calls.Should().BeEmpty();
    }

    // --- helpers ------------------------------------------------------------

    private const string Xliff = """
        <?xml version="1.0" encoding="utf-8"?>
        <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
          <file datatype="xml" source-language="en-US" target-language="da-DK" original="PaymentImport">
            <body>
              <group id="body">
                <trans-unit id="Table 1 - Field 2 - Property Caption" size-unit="char">
                  <source>Amount</source>
                  <target>Beløb</target>
                </trans-unit>
              </group>
            </body>
          </file>
        </xliff>
        """;

    private static FakeGitHubApi Api() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson());

    /// <summary>
    /// The scheduler over a service provider shaped like the app's: a scoped
    /// context, the GitHub services, and the ingest. The organisation identity
    /// comes from the ambient scope the sweep enters, exactly as in production -
    /// nothing here pins an org up front.
    /// </summary>
    private TranslationMemoryIngestScheduler NewScheduler(FakeGitHubApi api)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOrganizationContext>(new AmbientOnlyOrganizationContext());
        services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        _db.AddStorageServices(services);
        _db.AddGitHubServices(services, api);
        services.AddScoped<OrganizationConfigService>();
        services.AddScoped<TranslationMemoryService>();
        services.AddScoped<TranslationMemoryIngestService>();

        return new TranslationMemoryIngestScheduler(
            services.BuildServiceProvider(),
            TimeProvider.System,
            NullLogger<TranslationMemoryIngestScheduler>.Instance,
            new WorkerHeartbeatRegistry());
    }

    /// <summary>
    /// An organisation context with no request behind it, so the only thing that
    /// can answer "which organisation" is the ambient scope the sweep enters.
    /// </summary>
    private sealed class AmbientOnlyOrganizationContext : IOrganizationContext
    {
        public int? CurrentOrganizationId => AmbientOrganizationScope.Current?.OrganizationId;
        public int OrganizationIdForFilter => CurrentOrganizationId ?? 0;
        public int? CurrentUserId => AmbientOrganizationScope.Current?.UserId;
        public bool IsSiteAdmin => AmbientOrganizationScope.Current?.IsSiteAdmin ?? false;
        public bool IsSystemOrganization => AmbientOrganizationScope.Current?.IsSystemOrganization ?? false;
    }

    private async Task ConnectAsync(int organizationId)
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = organizationId,
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

    private async Task TrackAsync(int organizationId, string url)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new OeProject
        {
            OrganizationId = organizationId,
            Name = $"CRONUS {Guid.NewGuid():N}",
            DefaultArtifactCountry = "dk",
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        ctx.OeProjectRepositories.Add(new OeProjectRepository
        {
            OrganizationId = organizationId,
            ProjectId = project.Id,
            Provider = RepositoryProvider.GitHub,
            Url = url,
            DisplayName = "customer-app",
            AccessConfirmedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }
}
