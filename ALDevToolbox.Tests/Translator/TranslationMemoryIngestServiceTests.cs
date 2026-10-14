using System.Net;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Translation;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Translator;

/// <summary>
/// The translation-memory ingest (issue #631): reading every <c>.xlf</c> in the
/// repositories the organisation already tracks, and remembering which version
/// of each file it has already learned from.
///
/// <para>Two rules these tests exist for. The read acts as the
/// <em>organisation</em> - the installation token - so what bounds it has to be
/// the tracked list and the connected organisation's login, and both are pinned
/// here. And the sweep is nightly over every repository a customer has, so
/// re-reading a file nothing changed in would be the difference between a cheap
/// job and an expensive one; the blob sha is what makes that skip real.</para>
///
/// <para>GitHub is stood in for by <see cref="FakeGitHubApi"/>; see its note.</para>
/// </summary>
public sealed class TranslationMemoryIngestServiceTests : IDisposable
{
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string Repo = "cronus-dk/customer-app";
    private const string FilePath = "PaymentImport/Translations/PaymentImport.da-DK.xlf";
    private const string SourcePath = "PaymentImport/Translations/PaymentImport.g.xlf";
    private const string InstallationToken = "ghs_installation";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    // ── What it learns, and what it credits it to ────────────────────────

    [Fact]
    public async Task It_learns_the_translated_pairs_in_a_tracked_repository_and_says_where_they_came_from()
    {
        await ConnectAsync();
        await TrackAsync($"https://github.com/{Repo}.git");
        var api = ApiWith(TreeJson((FilePath, "blob", "sha-1")))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, Xliff("Amount", "Beløb")));

        var summary = await IngestAsync(api);

        summary.RepositoriesScanned.Should().Be(1);
        summary.FilesRead.Should().Be(1);
        summary.PairsLearned.Should().Be(1);

        await using var ctx = _db.NewContext();
        var entry = await ctx.TranslationMemory.SingleAsync();
        entry.TargetText.Should().Be("Beløb");
        entry.Origin.Should().Be("customer-app / PaymentImport",
            "the caption names the repository and the extension the file belongs to");
        entry.SourceRepository.Should().Be(Repo);
        entry.SourcePath.Should().Be(FilePath);
    }

    [Fact]
    public async Task The_read_goes_out_as_the_organisation_not_as_any_one_person()
    {
        await ConnectAsync();
        await TrackAsync($"https://github.com/{Repo}.git");
        var api = ApiWith(TreeJson((FilePath, "blob", "sha-1")))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, Xliff("Amount", "Beløb")));

        await IngestAsync(api);

        api.Credentials
            .Where(c => c.Call.Contains($"/repos/{Repo}/"))
            .Should().OnlyContain(c => c.Token == InstallationToken,
                "nobody has to be signed in for the nightly sweep to run");
    }

    [Fact]
    public async Task A_translations_folder_at_the_repository_root_is_credited_to_the_repository_alone()
    {
        await ConnectAsync();
        await TrackAsync($"https://github.com/{Repo}.git");
        var api = ApiWith(TreeJson(("Translations/App.da-DK.xlf", "blob", "sha-1")))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("Translations/App.da-DK.xlf", Xliff("Amount", "Beløb")));

        await IngestAsync(api);

        await using var ctx = _db.NewContext();
        (await ctx.TranslationMemory.SingleAsync()).Origin.Should().Be("customer-app");
    }

    [Fact]
    public async Task The_generated_source_file_is_not_read_at_all()
    {
        // A .g.xlf holds every string and no translations, so reading one costs
        // a call and teaches nothing.
        await ConnectAsync();
        await TrackAsync($"https://github.com/{Repo}.git");
        var api = ApiWith(TreeJson((SourcePath, "blob", "sha-1")));

        var summary = await IngestAsync(api);

        summary.FilesRead.Should().Be(0);
        api.Calls.Should().NotContain(c => c.Contains("/contents/"));
    }

    [Fact]
    public async Task Untranslated_units_are_not_remembered_as_translations()
    {
        await ConnectAsync();
        await TrackAsync($"https://github.com/{Repo}.git");
        var api = ApiWith(TreeJson((FilePath, "blob", "sha-1")))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, Xliff("Amount", "")));

        (await IngestAsync(api)).PairsLearned.Should().Be(0);
    }

    // ── Reading only what moved ──────────────────────────────────────────

    [Fact]
    public async Task A_file_whose_version_is_already_known_is_not_read_again()
    {
        await ConnectAsync();
        await TrackAsync($"https://github.com/{Repo}.git");
        var api = ApiWith(TreeJson((FilePath, "blob", "sha-1")))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, Xliff("Amount", "Beløb")));
        await IngestAsync(api);

        var second = ApiWith(TreeJson((FilePath, "blob", "sha-1")));
        var summary = await IngestAsync(second);

        summary.FilesRead.Should().Be(0);
        summary.FilesUnchanged.Should().Be(1);
        second.Calls.Should().NotContain(c => c.Contains("/contents/"),
            "a nightly sweep over a customer's repositories has to cost one call each when nothing moved");
    }

    [Fact]
    public async Task The_same_file_at_a_new_version_is_read_again_and_keeps_one_row()
    {
        await ConnectAsync();
        await TrackAsync($"https://github.com/{Repo}.git");
        await IngestAsync(ApiWith(TreeJson((FilePath, "blob", "sha-1")))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, Xliff("Amount", "Beløb"))));

        var summary = await IngestAsync(ApiWith(TreeJson((FilePath, "blob", "sha-2")))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, Xliff("Amount", "Beløb"))));

        summary.FilesRead.Should().Be(1);
        summary.FilesUnchanged.Should().Be(0);

        await using var ctx = _db.NewContext();
        var source = await ctx.TranslationMemorySources.SingleAsync();
        source.BlobSha.Should().Be("sha-2", "the version just learned from is the one to compare against next time");
        source.UnitCount.Should().Be(1);
    }

    [Fact]
    public async Task A_file_that_changed_is_read_again_and_takes_over_the_attribution()
    {
        await ConnectAsync();
        await TrackAsync($"https://github.com/{Repo}.git");
        await IngestAsync(ApiWith(TreeJson((FilePath, "blob", "sha-1")))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, Xliff("Amount", "Beløb"))));

        const string movedPath = "Sales/Translations/Sales.da-DK.xlf";
        var summary = await IngestAsync(ApiWith(TreeJson((movedPath, "blob", "sha-2")))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(movedPath, Xliff("Amount", "Beløb"))));

        summary.FilesRead.Should().Be(1);
        await using var ctx = _db.NewContext();
        var entry = await ctx.TranslationMemory.SingleAsync();
        entry.HitCount.Should().Be(2, "the same pair was seen twice rather than duplicated");
        entry.SourcePath.Should().Be(movedPath, "the most recent file to say it is the one to point at");
        entry.Origin.Should().Be("customer-app / Sales");
    }

    [Fact]
    public async Task A_file_that_is_no_longer_in_the_repository_loses_its_remembered_version()
    {
        await ConnectAsync();
        await TrackAsync($"https://github.com/{Repo}.git");
        await IngestAsync(ApiWith(TreeJson((FilePath, "blob", "sha-1")))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, Xliff("Amount", "Beløb"))));

        await IngestAsync(ApiWith(TreeJson()));

        await using var ctx = _db.NewContext();
        (await ctx.TranslationMemorySources.CountAsync()).Should().Be(0);
        (await ctx.TranslationMemory.CountAsync()).Should().Be(1,
            "a translation is not wrong because the file it came from was deleted");
    }

    [Fact]
    public async Task A_file_too_large_for_the_contents_api_is_read_through_the_blob_endpoint()
    {
        // GitHub declines to inline anything over a megabyte, answering without a
        // content field - which reaches us as "there is nothing to read here".
        await ConnectAsync();
        await TrackAsync($"https://github.com/{Repo}.git");
        var api = ApiWith(TreeJson((FilePath, "blob", "big-blob-sha")))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                $"{{\"path\":\"{FilePath}\",\"sha\":\"big-blob-sha\",\"size\":2000000,\"encoding\":\"none\",\"content\":\"\"}}")
            .On(HttpMethod.Get, $"/repos/{Repo}/git/blobs/big-blob-sha", HttpStatusCode.OK,
                Xliff("Amount", "Beløb"));

        var summary = await IngestAsync(api);

        summary.PairsLearned.Should().Be(1);
        api.Calls.Should().Contain(c => c.Contains("/git/blobs/big-blob-sha"));
    }

    // ── What it will not read ────────────────────────────────────────────

    [Fact]
    public async Task A_repository_outside_the_connected_organisation_is_left_alone()
    {
        // The installation token can reach every repository the App was installed
        // on. What bounds this read is the tracked list *and* the connected
        // organisation - a solution pointing somewhere else is not ours to read.
        await ConnectAsync();
        await TrackAsync("https://github.com/someone-else/private-app.git");
        var api = ApiWith(TreeJson());

        var summary = await IngestAsync(api);

        summary.RepositoriesScanned.Should().Be(0);
        api.Calls.Should().BeEmpty("not even a token is minted when there is nothing to read");
    }

    [Fact]
    public async Task An_azure_devops_repository_is_left_alone()
    {
        await ConnectAsync();
        await TrackAsync("https://dev.azure.com/cronus/_git/customer-app", RepositoryProvider.AzureDevOps);

        (await IngestAsync(ApiWith(TreeJson()))).RepositoriesScanned.Should().Be(0);
    }

    [Fact]
    public async Task A_repository_belonging_to_a_deleted_solution_is_left_alone()
    {
        await ConnectAsync();
        await TrackAsync($"https://github.com/{Repo}.git", deleted: true);

        (await IngestAsync(ApiWith(TreeJson()))).RepositoriesScanned.Should().Be(0);
    }

    [Fact]
    public async Task A_repository_nobody_confirmed_they_can_read_is_left_alone()
    {
        await ConnectAsync();
        await TrackAsync($"https://github.com/{Repo}.git", confirmed: false);
        var api = ApiWith(TreeJson());

        (await IngestAsync(api)).RepositoriesScanned.Should().Be(0);
        api.Calls.Should().BeEmpty("the installation token never reaches a repository nobody vouched for");
    }

    [Fact]
    public async Task An_organisation_that_has_connected_nothing_reads_nothing_rather_than_failing()
    {
        await TrackAsync($"https://github.com/{Repo}.git");

        var summary = await IngestAsync(ApiWith(TreeJson()));

        summary.FoundNothingToScan.Should().BeTrue();
    }

    // ── Nothing fails the caller ─────────────────────────────────────────

    [Fact]
    public async Task A_repository_that_cannot_be_read_is_counted_and_the_next_one_is_still_read()
    {
        await ConnectAsync();
        await TrackAsync("https://github.com/cronus-dk/broken-app.git");
        await TrackAsync($"https://github.com/{Repo}.git");
        var api = ApiWith(TreeJson((FilePath, "blob", "sha-1")))
            .On(HttpMethod.Get, "/repos/cronus-dk/broken-app/git/trees/", HttpStatusCode.InternalServerError,
                "{\"message\":\"Server Error\"}")
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, Xliff("Amount", "Beløb")));

        var summary = await IngestAsync(api);

        summary.RepositoriesFailed.Should().Be(1);
        summary.RepositoriesScanned.Should().Be(1);
        summary.PairsLearned.Should().Be(1, "one broken repository does not cost the others their translations");
    }

    [Fact]
    public async Task A_file_that_does_not_parse_teaches_nothing_and_the_sweep_carries_on()
    {
        await ConnectAsync();
        await TrackAsync($"https://github.com/{Repo}.git");
        var api = ApiWith(TreeJson((FilePath, "blob", "sha-1"), ("Sales/Translations/Sales.da-DK.xlf", "blob", "sha-2")))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/{FilePath}", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, "not xliff at all"))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/Sales", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("Sales/Translations/Sales.da-DK.xlf", Xliff("Amount", "Beløb")));

        var summary = await IngestAsync(api);

        summary.RepositoriesFailed.Should().Be(0);
        summary.PairsLearned.Should().Be(1);
    }

    // ── Tenant isolation ─────────────────────────────────────────────────

    [Fact]
    public async Task One_organisations_ingest_state_is_invisible_to_another()
    {
        await ConnectAsync();
        await TrackAsync($"https://github.com/{Repo}.git");
        await IngestAsync(ApiWith(TreeJson((FilePath, "blob", "sha-1")))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, Xliff("Amount", "Beløb"))));

        var otherOrg = new AmbientOrganizationContext { CurrentOrganizationId = TestDb.OtherOrgId };
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_db.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var otherCtx = new AppDbContext(options, otherOrg);
        (await otherCtx.TranslationMemorySources.CountAsync()).Should().Be(0,
            "the tenant query filter scopes the ingest state to the acting org");
    }

    // --- helpers ------------------------------------------------------------

    private async Task<TranslationMemoryIngestSummary> IngestAsync(FakeGitHubApi api)
    {
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var access = _db.NewGitHubAccessService(ctx, client);
        return await _db.NewTranslationMemoryIngestService(ctx, client, access)
            .IngestCurrentOrganisationAsync();
    }

    /// <summary>Enough of GitHub to mint a token and list a repository's files.</summary>
    private static FakeGitHubApi ApiWith(string treeJson) =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson(InstallationToken))
            .On(HttpMethod.Get, $"/repos/{Repo}/git/trees/", HttpStatusCode.OK, treeJson);

    /// <summary>A Git Data tree listing, in the shape the recursive route returns.</summary>
    private static string TreeJson(params (string Path, string Type, string Sha)[] entries) =>
        "{\"sha\":\"tree-sha\",\"truncated\":false,\"tree\":["
        + string.Join(',', entries.Select(e =>
            $"{{\"path\":\"{e.Path}\",\"type\":\"{e.Type}\",\"sha\":\"{e.Sha}\"}}"))
        + "]}";

    private static string Xliff(string source, string target) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
          <file datatype="xml" source-language="en-US" target-language="da-DK" original="PaymentImport">
            <body>
              <group id="body">
                <trans-unit id="Table 1 - Field 2 - Property Caption" size-unit="char">
                  <source>{source}</source>
                  <target>{target}</target>
                </trans-unit>
              </group>
            </body>
          </file>
        </xliff>
        """;

    private async Task ConnectAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = TestDb.DefaultOrgId,
            GitHubInstallationId = InstallationId,
            GitHubOrgLogin = OrgLogin,
            GitHubConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        // The deployment's App registration: without a private key there is no
        // JWT to sign, and so no installation token to read with.
        await _db.NewSystemSettingsService(ctx).SaveGitHubAppAsync(new ALDevToolbox.Services.Operations.GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: System.Security.Cryptography.RSA.Create(2048).ExportRSAPrivateKeyPem(),
            ClearPrivateKey: false));
    }

    /// <summary>One solution tracking one repository, which is what puts it in scope.</summary>
    private async Task TrackAsync(
        string url, RepositoryProvider provider = RepositoryProvider.GitHub, bool deleted = false, bool confirmed = true)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = $"CRONUS {Guid.NewGuid():N}",
            DefaultArtifactCountry = "dk",
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = deleted ? now : null,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        ctx.OeProjectRepositories.Add(new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Provider = provider,
            Url = url,
            DisplayName = "customer-app",
            AccessConfirmedAt = confirmed ? DateTime.UtcNow : null,
        });
        await ctx.SaveChangesAsync();
    }
}
