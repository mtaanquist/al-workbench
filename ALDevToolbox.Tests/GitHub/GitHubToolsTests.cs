using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Mcp.Dtos;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// MCP parity for the GitHub workflows (issue #633): the standalone tools an
/// agent finds by name - listing repositories, creating one, adding an
/// extension to one, and translating a file in one.
///
/// <para>What these pin is that each tool goes through the same service the
/// matching page uses, so an agent gets the answer a person would: the
/// narrowed repository list, the same refusals, the same sha quoted on a
/// write. GitHub is stood in for by <see cref="FakeGitHubApi"/>; see its
/// note.</para>
/// </summary>
public sealed class GitHubToolsTests : IDisposable
{
    private const int UserId = 933;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string RepoName = "customer-app";
    private const string Repo = $"{OrgLogin}/{RepoName}";
    private const string NewRepoName = "CRONUS-Customer";
    private const string NewRepo = $"{OrgLogin}/{NewRepoName}";
    private const string FilePath = "PaymentImport/Translations/PaymentImport.da-DK.xlf";
    private const string SourcePath = "PaymentImport/Translations/PaymentImport.g.xlf";
    private const string LoadedSha = "loaded-blob-sha";
    private const string BaseSha = "base-commit-sha";

    private const string AmountId = "Table 1 - Field 2 - Property Caption";
    private const string PostedId = "Table 1 - Field 3 - Property Caption";

    /// <summary>Two strings, so a test can prove only the named one moved.</summary>
    private const string Xliff = """
        <?xml version="1.0" encoding="utf-8"?>
        <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
          <file datatype="xml" source-language="en-US" target-language="da-DK" original="PaymentImport">
            <body>
              <group id="body">
                <trans-unit id="Table 1 - Field 2 - Property Caption" size-unit="char">
                  <source>Amount</source>
                  <target state="needs-translation"></target>
                </trans-unit>
                <trans-unit id="Table 1 - Field 3 - Property Caption" size-unit="char">
                  <source>Posted</source>
                  <target state="translated">Bogfoert</target>
                </trans-unit>
              </group>
            </body>
          </file>
        </xliff>
        """;

    /// <summary>The compiler's generated file: nothing translated, and its target language is its source one.</summary>
    private const string GeneratedXliff = """
        <?xml version="1.0" encoding="utf-8"?>
        <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
          <file datatype="xml" source-language="en-US" target-language="en-US" original="PaymentImport">
            <body>
              <group id="body">
                <trans-unit id="Table 1 - Field 2 - Property Caption" size-unit="char">
                  <source>Amount</source>
                  <target state="needs-translation"></target>
                </trans-unit>
              </group>
            </body>
          </file>
        </xliff>
        """;

    private readonly TestDb _db = new();

    public GitHubToolsTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "dev@cronus.example",
            DisplayName = "dev@cronus.example",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public void Dispose() => _db.Dispose();

    // ── list_repositories ────────────────────────────────────────────────

    [Fact]
    public async Task The_list_is_the_installations_repositories_narrowed_to_the_ones_the_caller_can_open()
    {
        await ReadyAsync();
        var api = ListableApi(
            granted: [$"{OrgLogin}/solution-b", $"{OrgLogin}/solution-a", $"{OrgLogin}/secret"],
            visible: [$"{OrgLogin}/solution-b", $"{OrgLogin}/solution-a"])
            .On(HttpMethod.Get, $"/repos/{OrgLogin}/secret", HttpStatusCode.NotFound);
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var result = await tools.ListRepositoriesAsync();

        result.Readiness.Should().Be("Ready");
        result.Guidance.Should().BeNull("there is nothing for the caller to fix");
        result.Repositories.Select(r => r.FullName).Should().Equal(
            $"{OrgLogin}/solution-a", $"{OrgLogin}/solution-b");
        result.Repositories[0].DefaultBranch.Should().Be("main");
        result.Repositories[0].CloneUrl.Should().Be($"https://github.com/{OrgLogin}/solution-a.git");
    }

    [Fact]
    public async Task An_unlinked_caller_is_offered_nothing_and_told_what_to_connect()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var api = ListableApi($"{OrgLogin}/solution-a");
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var result = await tools.ListRepositoriesAsync();

        result.Repositories.Should().BeEmpty();
        result.Readiness.Should().Be("NotLinked");
        result.Guidance.Should().Contain("Connect your own GitHub account");
        api.Calls.Should().BeEmpty("nothing can be asked of GitHub without a credential to ask with");
    }

    [Fact]
    public async Task A_deployment_with_no_github_app_says_so_rather_than_failing()
    {
        var (tools, ctx) = NewTools(new FakeGitHubApi());
        await using var _ = ctx;

        var result = await tools.ListRepositoriesAsync();

        result.Readiness.Should().Be("NotConfigured");
        result.Guidance.Should().Contain("not set up on this server");
        result.Repositories.Should().BeEmpty();
    }

    // ── create_repository ────────────────────────────────────────────────

    [Fact]
    public async Task Creating_a_repository_reports_the_one_it_created()
    {
        await ReadyAsync();
        await SeedTemplateAsync();
        var (tools, ctx) = NewTools(CreatableApi());
        await using var _ = ctx;

        var created = await tools.CreateRepositoryAsync(PlanInput(), NewRepoName);

        created.RepositoryFullName.Should().Be(NewRepo);
        created.HtmlUrl.Should().Be($"https://github.com/{NewRepo}");
        created.DefaultBranch.Should().Be("main");
        created.IsPrivate.Should().BeTrue();
        created.FileCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Creating_a_repository_inherits_the_outside_the_organisation_refusal()
    {
        await ReadyAsync();
        await SeedTemplateAsync();
        var api = CreatableApi()
            .On(HttpMethod.Get, $"/orgs/{OrgLogin}/members/", HttpStatusCode.NotFound);
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var act = () => tools.CreateRepositoryAsync(PlanInput(), NewRepoName);

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain(OrgLogin);
        api.Calls.Should().NotContain(c => c.Contains($"/orgs/{OrgLogin}/repos"),
            "nothing is created for somebody the GitHub organisation does not have");
    }

    [Fact]
    public async Task Creating_a_repository_takes_a_name_and_never_an_owner()
    {
        await ReadyAsync();
        await SeedTemplateAsync();
        var api = CreatableApi();
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var act = () => tools.CreateRepositoryAsync(PlanInput(), "someone-else/theirs");

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("Validation failed");
        api.Calls.Should().NotContain(c => c.Contains("someone-else"));
    }

    // ── add_extension_to_repository ──────────────────────────────────────

    [Fact]
    public async Task Adding_an_extension_opens_a_pull_request_and_reports_it()
    {
        await ReadyAsync();
        await SeedTemplateAsync();
        var (tools, ctx) = NewTools(DeliverableApi());
        await using var _ = ctx;

        var delivered = await tools.AddExtensionToRepositoryAsync(ExtensionPlanInput(), Repo);

        delivered.RepositoryFullName.Should().Be(Repo);
        delivered.BaseBranch.Should().Be("main");
        delivered.PullRequestNumber.Should().Be(12);
        delivered.PullRequestUrl.Should().Be($"https://github.com/{Repo}/pull/12");
        delivered.Branch.Should().NotBe("main", "the default branch is never committed to directly");
    }

    [Fact]
    public async Task Adding_an_extension_inherits_the_outside_the_organisation_refusal()
    {
        await ReadyAsync();
        await SeedTemplateAsync();
        var api = DeliverableApi();
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var act = () => tools.AddExtensionToRepositoryAsync(ExtensionPlanInput(), "someone-else/private-app");

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("not one the workbench can offer you");
        api.Calls.Should().NotContain(c => c.Contains("someone-else"));
    }

    // ── list_translation_files ───────────────────────────────────────────

    [Fact]
    public async Task The_translation_files_of_a_repository_are_listed_wherever_the_translations_folder_sits()
    {
        await ReadyAsync();
        var api = ReadableApi(TreeJson(
            (FilePath, "blob"),
            (SourcePath, "blob"),
            ("Translations/App.nb-NO.xlf", "blob"),
            ("PaymentImport/src/Table.al", "blob")));
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var files = await tools.ListTranslationFilesAsync(Repo);

        files.Select(f => f.Path).Should().Equal(
            "Translations/App.nb-NO.xlf", SourcePath, FilePath);
        files[0].Folder.Should().BeEmpty("a single-app repository has no extension folder to name");
        files[1].IsSource.Should().BeTrue();
        files[2].Language.Should().Be("da-DK");
    }

    [Fact]
    public async Task Listing_translation_files_outside_the_connected_organisation_is_refused()
    {
        await ReadyAsync();
        var (tools, ctx) = NewTools(ReadableApi(TreeJson()));
        await using var _ = ctx;

        var act = () => tools.ListTranslationFilesAsync("someone-else/private-app");

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("not one the workbench can offer you");
    }

    // ── open_translation_pr ──────────────────────────────────────────────

    [Fact]
    public async Task Only_the_strings_that_were_named_are_changed()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var result = await tools.OpenTranslationPullRequestAsync(
            Repo, FilePath, "da-DK",
            [new TranslationUnitEditInput(AmountId, "Beloeb", "translated")]);

        result.UnitsEdited.Should().Be(1);
        result.SavedPath.Should().Be(FilePath);
        result.IsNewPullRequest.Should().BeTrue();
        result.PullRequest.PullRequestNumber.Should().Be(12);

        var written = WrittenXml(api);
        written.Should().Contain("<target state=\"translated\">Beloeb</target>");
        // Everything else is byte for byte what was read - the second string
        // included, which is what makes the diff the translations themselves.
        written.Should().Be(Xliff.Replace(
            "<target state=\"needs-translation\"></target>",
            "<target state=\"translated\">Beloeb</target>",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_write_quotes_the_sha_of_the_version_that_was_read()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        await tools.OpenTranslationPullRequestAsync(
            Repo, FilePath, "da-DK", [new TranslationUnitEditInput(AmountId, "Beloeb")]);

        BodyOf(api, "PUT", "/contents/").Should().Contain($"\"sha\":\"{LoadedSha}\"",
            "quoting the sha is what makes GitHub refuse a write that would undo somebody else's");
        BodyOf(api, "PUT", "/contents/").Should().Contain("\"branch\":\"aldt/translate-da-DK\"");
    }

    [Fact]
    public async Task A_string_the_file_does_not_carry_is_refused_and_nothing_is_written()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var act = () => tools.OpenTranslationPullRequestAsync(
            Repo, FilePath, "da-DK",
            [
                new TranslationUnitEditInput(AmountId, "Beloeb"),
                new TranslationUnitEditInput("Table 9 - Field 9 - Property Caption", "Noget"),
            ]);

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("Table 9 - Field 9 - Property Caption");
        api.Calls.Should().NotContain(c => c.StartsWith("PUT", StringComparison.Ordinal),
            "an edit that could not land is refused before anything is committed");
    }

    [Fact]
    public async Task The_same_string_edited_twice_is_refused_by_name()
    {
        // Two edits for one id would silently drop one of them and the agent
        // would be told the write succeeded.
        await ReadyAsync();
        var api = WritableApi();
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var act = () => tools.OpenTranslationPullRequestAsync(
            Repo, FilePath, "da-DK",
            [
                new TranslationUnitEditInput(AmountId, "Beloeb"),
                new TranslationUnitEditInput(AmountId, "Belob"),
            ]);

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain(AmountId).And.Contain("more than once");
        api.Calls.Should().NotContain(c => c.StartsWith("PUT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_edits_at_all_is_refused_before_github_is_asked_anything()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var act = () => tools.OpenTranslationPullRequestAsync(Repo, FilePath, "da-DK", []);

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("No translations were given");
        api.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_that_changed_since_it_was_read_is_reported_as_that_rather_than_written_over()
    {
        await ReadyAsync();
        // The tool reads the file, then the save reads the same path on the
        // branch - so the second answer is the one that has moved on, which is
        // exactly the race the feature exists to refuse.
        var api = WritableApi()
            .OnSequence(HttpMethod.Get, $"/repos/{Repo}/contents/",
                (HttpStatusCode.OK, FakeGitHubApi.FileContentsJson(FilePath, Xliff, LoadedSha)),
                (HttpStatusCode.OK, FakeGitHubApi.FileContentsJson(FilePath, Xliff, "somebody-elses-sha")));
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var act = () => tools.OpenTranslationPullRequestAsync(
            Repo, FilePath, "da-DK", [new TranslationUnitEditInput(AmountId, "Beloeb")]);

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("changed in the repository since it was read");
        api.Calls.Should().NotContain(c => c.StartsWith("PUT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_translation_started_from_the_generated_file_is_written_beside_it()
    {
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/{SourcePath}", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(SourcePath, GeneratedXliff, "generated-sha"))
            // The language file it would land in is not there yet.
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/{FilePath}", HttpStatusCode.NotFound);
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var result = await tools.OpenTranslationPullRequestAsync(
            Repo, SourcePath, "da-DK", [new TranslationUnitEditInput(AmountId, "Beloeb", "translated")]);

        result.SavedPath.Should().Be(FilePath, "the generated file belongs to the compiler");
        var body = BodyOf(api, "PUT", "/contents/");
        body.Should().NotContain("\"sha\"", "the language file is being created, not replaced");
        // A da-DK file that says target-language="en-US" is not a Danish
        // translation as far as AL is concerned.
        WrittenXml(api).Should().Contain("target-language=\"da-DK\"");
    }

    // --- helpers ------------------------------------------------------------

    private (GitHubTools Tools, ALDevToolbox.Data.AppDbContext Context) NewTools(FakeGitHubApi api)
    {
        var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var access = _db.NewGitHubAccessService(ctx, client);
        var tools = new GitHubTools(
            _db.NewGitHubRepositoryService(ctx, client, access),
            _db.NewGitHubWorkspaceRepositoryService(ctx, client, access),
            _db.NewGitHubExtensionDeliveryService(ctx, client, access),
            _db.NewGitHubTranslationService(ctx, client, access),
            new ALDevToolbox.Services.Templates.TemplateService(
                ctx, NullLogger<ALDevToolbox.Services.Templates.TemplateService>.Instance, _db.OrgContext,
                new ALDevToolbox.Services.Templates.FolderTreeHydrator(ctx)),
            NullLogger<GitHubTools>.Instance);
        return (tools, ctx);
    }

    /// <summary>The XML a write actually put in the repository, out of its base64 body.</summary>
    private static string WrittenXml(FakeGitHubApi api)
    {
        using var document = JsonDocument.Parse(BodyOf(api, "PUT", "/contents/"));
        return Encoding.UTF8.GetString(
            Convert.FromBase64String(document.RootElement.GetProperty("content").GetString()!));
    }

    private static string BodyOf(FakeGitHubApi api, string method, string pathSuffix) =>
        api.Bodies
            .Where(b => b.Call.StartsWith(method, StringComparison.Ordinal) && b.Call.Contains(pathSuffix))
            .Select(b => b.Body)
            .LastOrDefault()
            ?? throw new InvalidOperationException($"No {method} request to a path containing '{pathSuffix}' was made.");

    private static ProjectPlanInput PlanInput() => new(
        TemplateKey: "runtime-test",
        WorkspaceName: "CRONUS Customer",
        ExtensionPrefix: "CRONUS",
        Brief: "Test brief.",
        Description: "Test description.",
        ApplicationVersion: "24.0.0.0",
        RuntimeVersion: "15",
        CoreIdRangeFrom: 90000,
        CoreIdRangeTo: 90999,
        IncludeExamples: true,
        SelectedExtensionPaths: null,
        SelectedModuleKeys: null);

    private static StandaloneExtensionPlanInput ExtensionPlanInput() => new(
        TemplateKey: "runtime-test",
        ExtensionName: "Payment Import",
        Brief: "Test brief.",
        Description: "Test description.",
        ApplicationVersion: "24.0.0.0",
        RuntimeVersion: "15",
        IdRangeFrom: 90000,
        IdRangeTo: 90999,
        IncludeExamples: true,
        Publisher: "CRONUS A/S",
        Dependencies: null);

    private static string TreeJson(params (string Path, string Type)[] entries) =>
        "{\"sha\":\"tree-sha\",\"truncated\":false,\"tree\":["
        + string.Join(',', entries.Select(e =>
            $"{{\"path\":\"{e.Path}\",\"type\":\"{e.Type}\",\"sha\":\"blob-{e.Path.GetHashCode():x}\"}}"))
        + "]}";

    /// <summary>A GitHub that can mint an installation token and list the repositories it was granted.</summary>
    private static FakeGitHubApi ListableApi(params string[] fullNames) => ListableApi(fullNames, fullNames);

    private static FakeGitHubApi ListableApi(string[] granted, string[] visible)
    {
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson(granted));
        foreach (var name in visible)
        {
            api.On(HttpMethod.Get, $"/repos/{name}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(name));
        }
        return api;
    }

    /// <summary>Enough of GitHub to resolve the repository and read its tree.</summary>
    private static FakeGitHubApi ReadableApi(string treeJson) =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, $"/repos/{Repo}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(Repo))
            .On(HttpMethod.Get, $"/repos/{Repo}/git/trees/main", HttpStatusCode.OK, treeJson);

    /// <summary>A GitHub that answers everything one translation save asks.</summary>
    private static FakeGitHubApi WritableApi() =>
        ReadableApi(TreeJson((FilePath, "blob"), (SourcePath, "blob")))
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/", HttpStatusCode.NotFound)
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/main", HttpStatusCode.OK,
                $"{{\"ref\":\"refs/heads/main\",\"object\":{{\"sha\":\"{BaseSha}\"}}}}")
            .On(HttpMethod.Post, $"/repos/{Repo}/git/refs", HttpStatusCode.Created, FakeGitHubApi.ShaJson(BaseSha))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, Xliff, LoadedSha))
            .On(HttpMethod.Put, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                "{\"content\":{\"sha\":\"written-blob-sha\"},\"commit\":{\"sha\":\"new-commit-sha\"}}")
            .On(HttpMethod.Get, $"/repos/{Repo}/pulls", HttpStatusCode.OK, "[]")
            .On(HttpMethod.Post, $"/repos/{Repo}/pulls", HttpStatusCode.Created,
                $"{{\"number\":12,\"html_url\":\"https://github.com/{Repo}/pull/12\"}}");

    /// <summary>A GitHub that accepts a new repository and the first commit into it.</summary>
    private static FakeGitHubApi CreatableApi() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, $"/orgs/{OrgLogin}/members/", HttpStatusCode.NoContent)
            .On(HttpMethod.Post, $"/orgs/{OrgLogin}/repos", HttpStatusCode.Created,
                FakeGitHubApi.RepositoryJson(NewRepo))
            .On(HttpMethod.Put, $"/repos/{NewRepo}/contents/", HttpStatusCode.Created, FakeGitHubApi.FileWriteJson())
            .On(HttpMethod.Post, $"/repos/{NewRepo}/git/blobs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("blob-sha"))
            .On(HttpMethod.Post, $"/repos/{NewRepo}/git/trees", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-tree-sha"))
            .On(HttpMethod.Post, $"/repos/{NewRepo}/git/commits", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit-sha"))
            .On(HttpMethod.Patch, $"/repos/{NewRepo}/git/refs/heads/", HttpStatusCode.OK, FakeGitHubApi.ShaJson("new-commit-sha"))
            .EmptyRepository(NewRepo);

    /// <summary>A GitHub that accepts an extension being added to an existing repository.</summary>
    private static FakeGitHubApi DeliverableApi() =>
        ReadableApi(TreeJson())
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/main", HttpStatusCode.OK,
                $"{{\"ref\":\"refs/heads/main\",\"object\":{{\"sha\":\"{BaseSha}\"}}}}")
            .On(HttpMethod.Get, $"/repos/{Repo}/git/commits/{BaseSha}", HttpStatusCode.OK,
                $"{{\"sha\":\"{BaseSha}\",\"tree\":{{\"sha\":\"base-tree-sha\"}}}}")
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.NotFound)
            .On(HttpMethod.Post, $"/repos/{Repo}/git/refs", HttpStatusCode.Created, FakeGitHubApi.ShaJson(BaseSha))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/blobs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("blob-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/trees", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-tree-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/commits", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/pulls", HttpStatusCode.Created,
                $"{{\"number\":12,\"html_url\":\"https://github.com/{Repo}/pull/12\"}}");

    private async Task ConfigureDeploymentAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
    }

    private async Task ConnectOrganisationAsync()
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
    }

    private async Task LinkAsync()
    {
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson());
        await using var ctx = _db.NewContext();
        await _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, api)).LinkAsync("the-code");
    }

    /// <summary>Deployment configured, organisation connected, user linked.</summary>
    private async Task ReadyAsync()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        await LinkAsync();
    }

    /// <summary>A template complete enough to generate from, with the org's always-included files.</summary>
    private async Task SeedTemplateAsync()
    {
        var template = TemplateBuilder.Default();
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(template);
        await ctx.SaveChangesAsync();

        var orgFileIds = await ctx.OrganizationFiles
            .Where(f => f.OrganizationId == template.OrganizationId)
            .OrderBy(f => f.Ordering)
            .Select(f => f.Id)
            .ToListAsync();
        for (var i = 0; i < orgFileIds.Count; i++)
        {
            ctx.Set<RuntimeTemplateIncludedFile>().Add(new RuntimeTemplateIncludedFile
            {
                OrganizationId = template.OrganizationId,
                RuntimeTemplateId = template.Id,
                OrganizationFileId = orgFileIds[i],
                Ordering = i,
            });
        }
        if (orgFileIds.Count > 0)
        {
            await ctx.SaveChangesAsync();
        }
    }
}
