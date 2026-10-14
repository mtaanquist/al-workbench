using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The Translator's repository round trip (issue #625): finding the XLIFF files
/// in a repository, opening one, and saving it back as a pull request.
///
/// <para>The rule these tests exist for is the conflict one. A translator who
/// silently commits over a colleague's afternoon is the failure that matters
/// here, so every path into a write - the check before it and GitHub's own 409
/// after it - is pinned, along with the sha the write quotes and the fact that
/// the default branch is never touched.</para>
///
/// <para>GitHub is stood in for by <see cref="FakeGitHubApi"/>; see its note.
/// The request shapes are GitHub's documented ones - they have not been
/// exercised against api.github.com from this environment.</para>
/// </summary>
public sealed class GitHubTranslationServiceTests : IDisposable
{
    private const int UserId = 811;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string Repo = "cronus-dk/customer-app";
    private const string BaseSha = "base-commit-sha";
    private const string Branch = "aldt/translate-da-DK";
    private const string FilePath = "PaymentImport/Translations/PaymentImport.da-DK.xlf";
    private const string SourcePath = "PaymentImport/Translations/PaymentImport.g.xlf";
    private const string LoadedSha = "loaded-blob-sha";

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
              </group>
            </body>
          </file>
        </xliff>
        """;

    private readonly TestDb _db = new();

    public GitHubTranslationServiceTests()
    {
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

    public void Dispose() => _db.Dispose();

    // ── Finding the files ────────────────────────────────────────────────

    [Fact]
    public async Task Only_the_xlf_files_directly_inside_a_translations_folder_are_offered()
    {
        await ReadyAsync();
        var api = ReadableApi(TreeJson(
            ("PaymentImport/Translations/PaymentImport.da-DK.xlf", "blob"),
            ("PaymentImport/Translations/PaymentImport.de-DE.xlf", "blob"),
            // Not offered: not an XLIFF, a folder deeper down, and a file that
            // only happens to be named like one somewhere else.
            ("PaymentImport/Translations/README.md", "blob"),
            ("PaymentImport/Translations/old/PaymentImport.sv-SE.xlf", "blob"),
            ("PaymentImport/src/Table.al", "blob"),
            ("PaymentImport/Translations", "tree")));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var files = await service.ListFilesAsync(Repo);

        files.Select(f => f.Path).Should().Equal(
            "PaymentImport/Translations/PaymentImport.da-DK.xlf",
            "PaymentImport/Translations/PaymentImport.de-DE.xlf");
        files[0].Language.Should().Be("da-DK");
        files[0].Folder.Should().Be("PaymentImport");
    }

    [Fact]
    public async Task A_translations_folder_at_the_repository_root_is_found_as_well()
    {
        await ReadyAsync();
        var api = ReadableApi(TreeJson(("Translations/App.nb-NO.xlf", "blob")));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var files = await service.ListFilesAsync(Repo);

        files.Should().ContainSingle();
        files[0].Folder.Should().BeEmpty("a single-app repository has no extension folder to name");
    }

    [Fact]
    public async Task The_generated_source_file_is_recognised_and_offered_first()
    {
        await ReadyAsync();
        var api = ReadableApi(TreeJson(
            (FilePath, "blob"),
            (SourcePath, "blob")));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var files = await service.ListFilesAsync(Repo);

        files[0].IsSource.Should().BeTrue("it is the file you start a new language from");
        files[0].Language.Should().BeNull("the generated file is not in any one language");
        files[1].IsSource.Should().BeFalse();
    }

    [Fact]
    public async Task A_repository_outside_the_connected_organisation_is_refused()
    {
        await ReadyAsync();
        var (service, ctx) = NewService(ReadableApi(TreeJson()));
        await using var _ = ctx;

        var act = () => service.ListFilesAsync("someone-else/private-app");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["GitHubRepository"].Should().Contain("not one the workbench can offer you");
    }

    [Fact]
    public async Task A_user_who_has_not_connected_their_github_account_is_told_so_rather_than_shown_a_failure()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var api = ReadableApi(TreeJson());
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.ListFilesAsync(Repo);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["GitHubRepository"].Should().Contain("Connect your own GitHub account");
        api.Calls.Should().BeEmpty("nothing can be asked of GitHub without a credential to ask with");
    }

    // ── Opening one ──────────────────────────────────────────────────────

    [Fact]
    public async Task Opening_a_file_hands_back_its_text_and_the_sha_a_save_will_quote()
    {
        await ReadyAsync();
        var api = ReadableApi(TreeJson((FilePath, "blob")))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/{FilePath}", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, Xliff, LoadedSha));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var file = await service.OpenAsync(Repo, FilePath);

        file.Should().NotBeNull();
        file!.Text.Should().Contain("<source>Amount</source>");
        file.Sha.Should().Be(LoadedSha);
    }

    [Fact]
    public async Task A_file_that_is_no_longer_there_comes_back_as_nothing_rather_than_as_an_error()
    {
        await ReadyAsync();
        var api = ReadableApi(TreeJson())
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.NotFound);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.OpenAsync(Repo, FilePath)).Should().BeNull();
    }

    // ── Saving it back ───────────────────────────────────────────────────

    [Fact]
    public async Task A_first_save_commits_to_its_own_branch_and_opens_a_pull_request()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var save = await service.SaveAsync(Source(), "da-DK", Xliff, "1 of 1 strings are translated.");

        save.IsNewPullRequest.Should().BeTrue();
        save.PullRequest.Number.Should().Be(12);
        save.PullRequest.HeadBranch.Should().Be(Branch);
        save.Source.BaseSha.Should().Be("written-blob-sha", "the next save builds on what this one wrote");

        BodyOf(api, "POST", "/git/refs").Should().Contain($"refs/heads/{Branch}");
        var pull = BodyOf(api, "POST", "/pulls");
        pull.Should().Contain($"\"head\":\"{Branch}\"");
        pull.Should().Contain("\"base\":\"main\"");
    }

    [Fact]
    public async Task Nothing_is_ever_written_to_the_default_branch()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.SaveAsync(Source(), "da-DK", Xliff, "done");

        // main is read to find the commit to branch from, and never written to
        // - even though this repository has no branch protection to stop us.
        BodyOf(api, "PUT", "/contents/").Should().Contain($"\"branch\":\"{Branch}\"");
        api.Bodies.Should().NotContain(b => b.Body.Contains("\"branch\":\"main\""));
        api.Bodies.Should().NotContain(b => b.Body.Contains("refs/heads/main"));
    }

    [Fact]
    public async Task The_write_quotes_the_sha_of_the_version_that_was_read()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.SaveAsync(Source(), "da-DK", Xliff, "done");

        BodyOf(api, "PUT", "/contents/").Should().Contain($"\"sha\":\"{LoadedSha}\"",
            "quoting the sha is what makes GitHub refuse a write that would undo somebody else's");
    }

    [Fact]
    public async Task A_second_save_adds_a_commit_to_the_pull_request_that_is_already_open()
    {
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/{Branch}", HttpStatusCode.OK,
                $"{{\"object\":{{\"sha\":\"{BaseSha}\"}}}}")
            .On(HttpMethod.Get, $"/repos/{Repo}/pulls", HttpStatusCode.OK,
                $"[{{\"number\":12,\"html_url\":\"https://github.com/{Repo}/pull/12\"}}]");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var save = await service.SaveAsync(Source(), "da-DK", Xliff, "done");

        save.IsNewPullRequest.Should().BeFalse();
        save.PullRequest.Number.Should().Be(12);
        api.Calls.Should().NotContain(c => c.StartsWith("POST", StringComparison.Ordinal) && c.Contains("/pulls"),
            "a second pull request beside the first would split one review in two");
        api.Calls.Should().NotContain(c => c.Contains("/git/refs"),
            "the branch is already there and its pull request is still open");
    }

    // ── The conflict, which is the point of the feature ──────────────────

    [Fact]
    public async Task A_file_that_changed_since_it_was_opened_is_refused_rather_than_written_over()
    {
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, Xliff, "somebody-elses-sha"));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.SaveAsync(Source(), "da-DK", Xliff, "done");

        await act.Should().ThrowAsync<GitHubContentConflictException>();
        api.Calls.Should().NotContain(c => c.StartsWith("PUT", StringComparison.Ordinal),
            "the write is not attempted at all once the file is known to have moved on");
    }

    [Fact]
    public async Task Githubs_own_409_is_read_as_the_same_conflict()
    {
        // The check above loses a race when someone commits between the read
        // and the write. GitHub answers 409, and that has to mean exactly what
        // the check means, or the race would end in an overwrite.
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Put, $"/repos/{Repo}/contents/", HttpStatusCode.Conflict,
                "{\"message\":\"is at 9d0a... but expected 8c1b...\"}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.SaveAsync(Source(), "da-DK", Xliff, "done");

        (await act.Should().ThrowAsync<GitHubContentConflictException>())
            .Which.Path.Should().Be(FilePath);
    }

    [Fact]
    public async Task A_422_saying_the_sha_does_not_match_is_the_same_answer_in_a_different_status()
    {
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Put, $"/repos/{Repo}/contents/", HttpStatusCode.UnprocessableEntity,
                "{\"message\":\"sha does not match\"}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.SaveAsync(Source(), "da-DK", Xliff, "done");

        await act.Should().ThrowAsync<GitHubContentConflictException>();
    }

    [Fact]
    public async Task A_translation_that_already_exists_is_not_replaced_by_one_started_from_the_source_file()
    {
        // Opening the generated source file and translating it saves to a new
        // language file. Finding one already there is not a race - it is
        // somebody's work - so it gets its own answer, not the conflict one.
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(FilePath, Xliff, "an-existing-translation"));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.SaveAsync(Source(baseSha: null), "da-DK", Xliff, "done");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["GitHubRepository"].Should().Contain("already has a da-DK translation");
    }

    [Fact]
    public async Task A_file_that_is_not_in_the_repository_yet_is_created_without_a_sha()
    {
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.NotFound);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.SaveAsync(Source(baseSha: null), "da-DK", Xliff, "done");

        BodyOf(api, "PUT", "/contents/").Should().NotContain("\"sha\"",
            "there is no earlier version to quote");
    }

    [Fact]
    public async Task A_repository_with_no_commits_yet_is_refused_with_a_reason_the_user_can_act_on()
    {
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/main", HttpStatusCode.NotFound);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.SaveAsync(Source(), "da-DK", Xliff, "done");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["GitHubRepository"].Should().Contain("no commits");
    }

    [Fact]
    public async Task The_branch_is_named_for_the_language_being_translated()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var save = await service.SaveAsync(Source(), "nb-NO", Xliff, "done");

        save.PullRequest.HeadBranch.Should().Be("aldt/translate-nb-NO");
    }

    // ── The translation branch after its pull request (#1329) ─────────────

    [Fact]
    public async Task While_the_pull_request_is_open_the_file_is_read_from_its_branch()
    {
        await ReadyAsync();
        var api = BranchApi(openPullRequest: true)
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/{FilePath}", BranchOrMain("branch-blob-sha"));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var file = await service.OpenOnTranslationBranchAsync(Repo, FilePath, "da-DK");

        file.Should().NotBeNull();
        file!.Sha.Should().Be("branch-blob-sha");
    }

    [Fact]
    public async Task A_branch_kept_after_its_pull_request_was_merged_is_not_read()
    {
        // GitHub keeps a merged branch unless the repository deletes it, so the
        // branch existing says nothing about whether it is current. Reading it
        // would hide every string added to main since the merge.
        await ReadyAsync();
        var api = BranchApi(openPullRequest: false)
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/{FilePath}", BranchOrMain("branch-blob-sha"));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.OpenOnTranslationBranchAsync(Repo, FilePath, "da-DK")).Should().BeNull();
        api.Calls.Should().NotContain(c => c.Contains("/contents/", StringComparison.Ordinal),
            "with no pull request open the caller reads the default branch instead");
    }

    [Fact]
    public async Task A_branch_that_does_not_carry_the_file_comes_back_as_nothing()
    {
        await ReadyAsync();
        var api = BranchApi(openPullRequest: true)
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/{FilePath}", HttpStatusCode.NotFound);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.OpenOnTranslationBranchAsync(Repo, FilePath, "da-DK")).Should().BeNull();
    }

    [Fact]
    public async Task Reopening_while_the_pull_request_is_open_and_saving_again_quotes_the_branchs_sha()
    {
        // The Translator's second afternoon: the file is opened again, from the
        // branch, and the save has to quote the sha it was read at - or it is
        // refused as a conflict with the translator's own first commit.
        await ReadyAsync();
        var api = BranchApi(openPullRequest: true)
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/{FilePath}", BranchOrMain("branch-blob-sha"));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var file = await service.OpenOnTranslationBranchAsync(Repo, FilePath, "da-DK");
        var save = await service.SaveAsync(Source(baseSha: file!.Sha), "da-DK", Xliff, "done");

        BodyOf(api, "PUT", "/contents/").Should().Contain("\"sha\":\"branch-blob-sha\"");
        save.IsNewPullRequest.Should().BeFalse();
        save.PullRequest.Number.Should().Be(12);
        api.Calls.Should().NotContain(c => c.StartsWith("PATCH", StringComparison.Ordinal),
            "a branch under review is never moved");
    }

    [Fact]
    public async Task Saving_after_the_pull_request_was_merged_moves_the_branch_to_main_and_opens_a_new_one()
    {
        // Opened from main (no pull request open), so the edit is based on
        // main's copy. The kept branch is moved onto main first, so the write
        // lands on top of everything main has and the new pull request does not
        // conflict with it.
        await ReadyAsync();
        var api = StaleBranchApi()
            .On(HttpMethod.Patch, $"/repos/{Repo}/git/refs/heads/", HttpStatusCode.OK, FakeGitHubApi.ShaJson(BaseSha));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var save = await service.SaveAsync(Source(), "da-DK", Xliff, "done");

        var move = BodyOf(api, "PATCH", $"/git/refs/heads/");
        move.Should().Contain($"\"sha\":\"{BaseSha}\"");
        move.Should().Contain("\"force\":false", "a fast-forward is tried first");
        BodyOf(api, "PUT", "/contents/").Should().Contain($"\"sha\":\"{LoadedSha}\"");
        save.IsNewPullRequest.Should().BeTrue();
        api.Calls.Should().NotContain(c => c.StartsWith("POST", StringComparison.Ordinal) && c.Contains("/git/refs"),
            "the branch already exists, so it is moved rather than created");
    }

    [Fact]
    public async Task A_squash_merged_branch_that_cannot_fast_forward_is_reset_to_main()
    {
        await ReadyAsync();
        var api = StaleBranchApi()
            .On(HttpMethod.Patch, $"/repos/{Repo}/git/refs/heads/", request =>
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("\"force\":true")
                    ? (HttpStatusCode.OK, FakeGitHubApi.ShaJson(BaseSha))
                    : (HttpStatusCode.UnprocessableEntity, "{\"message\":\"Update is not a fast forward\"}"));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var save = await service.SaveAsync(Source(), "da-DK", Xliff, "done");

        api.Bodies.Where(b => b.Call.StartsWith("PATCH", StringComparison.Ordinal))
            .Select(b => b.Body.Contains("\"force\":true"))
            .Should().Equal(false, true);
        save.IsNewPullRequest.Should().BeTrue();
    }

    // --- helpers ------------------------------------------------------------

    /// <summary>
    /// A repository whose da-DK branch exists and sits apart from main, with
    /// its pull request still open or already merged (GitHub lists only open
    /// ones for <c>state=open</c>).
    /// </summary>
    private static FakeGitHubApi BranchApi(bool openPullRequest) =>
        WritableApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/{Branch}", HttpStatusCode.OK,
                $"{{\"ref\":\"refs/heads/{Branch}\",\"object\":{{\"sha\":\"branch-head\"}}}}")
            .On(HttpMethod.Get, $"/repos/{Repo}/pulls", HttpStatusCode.OK, openPullRequest
                ? $"[{{\"number\":12,\"html_url\":\"https://github.com/{Repo}/pull/12\"}}]"
                : "[]");

    /// <summary>
    /// A merged round's kept branch: its copy of the file is stale until the
    /// branch is moved, after which it is main's. The save reads the file only
    /// once every move has been made, so "any PATCH of the ref" is enough.
    /// </summary>
    private static FakeGitHubApi StaleBranchApi()
    {
        FakeGitHubApi api = null!;
        api = BranchApi(openPullRequest: false)
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/{FilePath}", request =>
                request.RequestUri!.Query.Contains("ref=aldt", StringComparison.Ordinal)
                    && !api.Calls.Any(c => c.StartsWith("PATCH", StringComparison.Ordinal))
                    ?(HttpStatusCode.OK, FakeGitHubApi.FileContentsJson(FilePath, Xliff, "stale-branch-sha"))
                    : (HttpStatusCode.OK, FakeGitHubApi.FileContentsJson(FilePath, Xliff, LoadedSha)));
        return api;
    }

    /// <summary>The file as the translation branch has it, or as main does.</summary>
    private static Func<HttpRequestMessage, (HttpStatusCode, string?)> BranchOrMain(string branchSha) =>
        request => request.RequestUri!.Query.Contains("ref=aldt", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, FakeGitHubApi.FileContentsJson(FilePath, Xliff, branchSha))
            : (HttpStatusCode.OK, FakeGitHubApi.FileContentsJson(FilePath, Xliff, LoadedSha));

    private static RepositoryTranslationSource Source(string? baseSha = LoadedSha) =>
        new(
            new GitHubRepositorySummary(
                Repo, OrgLogin, "customer-app", "main", IsPrivate: true, Description: null,
                HtmlUrl: $"https://github.com/{Repo}", CloneUrl: $"https://github.com/{Repo}.git"),
            FilePath,
            "PaymentImport.da-DK.xlf",
            baseSha);

    private (GitHubTranslationService Service, AppDbContext Context) NewService(FakeGitHubApi api)
    {
        var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var access = _db.NewGitHubAccessService(ctx, client);
        return (_db.NewGitHubTranslationService(ctx, client, access), ctx);
    }

    private static string BodyOf(FakeGitHubApi api, string method, string pathSuffix) =>
        api.Bodies
            .Where(b => b.Call.StartsWith(method, StringComparison.Ordinal) && b.Call.Contains(pathSuffix))
            .Select(b => b.Body)
            .LastOrDefault()
            ?? throw new InvalidOperationException($"No {method} request to a path containing '{pathSuffix}' was made.");

    /// <summary>A Git Data tree listing, in the shape the recursive route returns.</summary>
    private static string TreeJson(params (string Path, string Type)[] entries) =>
        "{\"sha\":\"tree-sha\",\"truncated\":false,\"tree\":["
        + string.Join(',', entries.Select(e =>
            $"{{\"path\":\"{e.Path}\",\"type\":\"{e.Type}\",\"sha\":\"blob-{e.Path.GetHashCode():x}\"}}"))
        + "]}";

    /// <summary>Enough of GitHub to resolve the repository and read from it.</summary>
    private static FakeGitHubApi ReadableApi(string treeJson) =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, $"/repos/{Repo}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(Repo))
            .On(HttpMethod.Get, $"/repos/{Repo}/git/trees/main", HttpStatusCode.OK, treeJson);

    /// <summary>
    /// A GitHub that answers every call one save makes: the repository, the
    /// branch that is not there yet, the branch it is cut from, the file as the
    /// branch has it, the write, and the pull request.
    /// </summary>
    private static FakeGitHubApi WritableApi() =>
        ReadableApi(TreeJson((FilePath, "blob")))
            // No translation branch yet, so it is cut from the default branch.
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
        var access = _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, api));
        await access.LinkAsync("the-code");
    }

    /// <summary>Deployment configured, organisation connected, user linked.</summary>
    private async Task ReadyAsync()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        await LinkAsync();
    }
}
