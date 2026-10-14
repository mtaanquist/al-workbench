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
        var world = new BranchWorld();
        world.Heads[Branch] = "branch-head";
        world.OpenPullRequests[Branch] = 12;
        world.FileShas[Branch] = "branch-blob-sha";
        var (service, ctx) = NewService(world.Api);
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
        var world = new BranchWorld();
        world.Heads[Branch] = "branch-head";
        world.ClosedPullRequestHeads[Branch] = "branch-head";
        world.FileShas[Branch] = "branch-blob-sha";
        var (service, ctx) = NewService(world.Api);
        await using var _ = ctx;

        (await service.OpenOnTranslationBranchAsync(Repo, FilePath, "da-DK")).Should().BeNull();
        world.Api.Calls.Should().NotContain(c => c.Contains("/contents/", StringComparison.Ordinal),
            "with no pull request open the caller reads the default branch instead");
    }

    [Fact]
    public async Task A_branch_that_does_not_carry_the_file_comes_back_as_nothing()
    {
        await ReadyAsync();
        var world = new BranchWorld();
        world.Heads[Branch] = "branch-head";
        world.OpenPullRequests[Branch] = 12;
        var (service, ctx) = NewService(world.Api);
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
        var world = new BranchWorld();
        world.Heads[Branch] = "branch-head";
        world.OpenPullRequests[Branch] = 12;
        world.FileShas[Branch] = "branch-blob-sha";
        var (service, ctx) = NewService(world.Api);
        await using var _ = ctx;

        var file = await service.OpenOnTranslationBranchAsync(Repo, FilePath, "da-DK");
        var save = await service.SaveAsync(Source(baseSha: file!.Sha), "da-DK", Xliff, "done");

        BodyOf(world.Api, "PUT", "/contents/").Should().Contain("\"sha\":\"branch-blob-sha\"");
        save.IsNewPullRequest.Should().BeFalse();
        save.PullRequest.Number.Should().Be(12);
        world.Api.Calls.Should().NotContain(c => c.StartsWith("PATCH", StringComparison.Ordinal),
            "a branch under review is never moved");
    }

    [Fact]
    public async Task A_branch_whose_commits_are_all_on_main_is_fast_forwarded_and_gets_a_new_pull_request()
    {
        // Merged with a merge commit: main contains the branch, so moving the
        // branch onto main loses nothing and is a plain fast-forward.
        await ReadyAsync();
        var world = new BranchWorld();
        world.Heads[Branch] = "branch-head";
        world.FileShas[Branch] = "stale-branch-sha";
        world.FastForwardable.Add(Branch);
        var (service, ctx) = NewService(world.Api);
        await using var _ = ctx;

        var save = await service.SaveAsync(Source(), "da-DK", Xliff, "done");

        var move = BodyOf(world.Api, "PATCH", "/git/refs/heads/");
        move.Should().Contain($"\"sha\":\"{BaseSha}\"");
        move.Should().Contain("\"force\":false");
        BodyOf(world.Api, "PUT", "/contents/").Should().Contain($"\"sha\":\"{LoadedSha}\"");
        save.IsNewPullRequest.Should().BeTrue();
        save.PullRequest.HeadBranch.Should().Be(Branch);
    }

    [Fact]
    public async Task A_squash_merged_branch_still_at_its_pull_requests_head_is_reset_to_main()
    {
        // The squash commit on main is not the branch's commit, so a
        // fast-forward is refused; but the branch head is exactly what the
        // merged pull request carried, and GitHub keeps that for good.
        await ReadyAsync();
        var world = new BranchWorld();
        world.Heads[Branch] = "branch-head";
        world.ClosedPullRequestHeads[Branch] = "branch-head";
        world.FileShas[Branch] = "stale-branch-sha";
        var (service, ctx) = NewService(world.Api);
        await using var _ = ctx;

        var save = await service.SaveAsync(Source(), "da-DK", Xliff, "done");

        world.Api.Bodies.Where(b => b.Call.StartsWith("PATCH", StringComparison.Ordinal))
            .Select(b => b.Body.Contains("\"force\":true"))
            .Should().Equal(false, true);
        world.Heads[Branch].Should().Be(BaseSha);
        save.IsNewPullRequest.Should().BeTrue();
        save.PullRequest.HeadBranch.Should().Be(Branch);
    }

    [Fact]
    public async Task Commits_no_pull_request_ever_carried_are_left_alone_and_the_next_name_is_used()
    {
        // A save whose pull request was never opened, or commits pushed by hand
        // after the last one closed: nothing on GitHub keeps those, so the
        // branch is not moved and the round goes on a -2 branch instead.
        await ReadyAsync();
        var world = new BranchWorld();
        world.Heads[Branch] = "unreviewed-head";
        world.ClosedPullRequestHeads[Branch] = "older-head";
        world.FileShas[Branch] = "unreviewed-blob-sha";
        var (service, ctx) = NewService(world.Api);
        await using var _ = ctx;

        var save = await service.SaveAsync(Source(), "da-DK", Xliff, "done");

        world.Api.Bodies.Should().NotContain(b => b.Body.Contains("\"force\":true"),
            "nothing would keep the commits the reset moved away from");
        world.Heads[Branch].Should().Be("unreviewed-head");
        world.Heads.Should().ContainKey(Branch + "-2");
        BodyOf(world.Api, "PUT", "/contents/").Should().Contain($"\"branch\":\"{Branch}-2\"");
        save.PullRequest.HeadBranch.Should().Be(Branch + "-2");
        save.IsNewPullRequest.Should().BeTrue();
    }

    [Fact]
    public async Task An_open_pull_request_on_a_numbered_branch_is_read_and_reused()
    {
        await ReadyAsync();
        var world = new BranchWorld();
        world.Heads[Branch] = "unreviewed-head";
        world.Heads[Branch + "-2"] = "second-head";
        world.OpenPullRequests[Branch + "-2"] = 14;
        world.FileShas[Branch + "-2"] = "second-blob-sha";
        var (service, ctx) = NewService(world.Api);
        await using var _ = ctx;

        var file = await service.OpenOnTranslationBranchAsync(Repo, FilePath, "da-DK");
        file!.Sha.Should().Be("second-blob-sha");

        var save = await service.SaveAsync(Source(baseSha: file.Sha), "da-DK", Xliff, "done");

        save.IsNewPullRequest.Should().BeFalse();
        save.PullRequest.Number.Should().Be(14);
        BodyOf(world.Api, "PUT", "/contents/").Should().Contain($"\"branch\":\"{Branch}-2\"");
        world.Api.Calls.Should().NotContain(c => c.StartsWith("PATCH", StringComparison.Ordinal));
        world.Api.Calls.Should().NotContain(c => c.StartsWith("POST", StringComparison.Ordinal) && c.Contains("/git/refs"));
    }

    // --- helpers ------------------------------------------------------------

    /// <summary>
    /// A small model of one repository's branches and pull requests, answering
    /// the calls a translation save makes from that state - so a test sets up
    /// "this branch exists, at this head, with this pull request" and the
    /// routes follow whatever the service then does to it.
    /// </summary>
    private sealed class BranchWorld
    {
        public Dictionary<string, string> Heads { get; } = new(StringComparer.Ordinal) { ["main"] = BaseSha };
        public Dictionary<string, int> OpenPullRequests { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ClosedPullRequestHeads { get; } = new(StringComparer.Ordinal);

        /// <summary>The file's blob sha on a branch; a branch at main's head has main's.</summary>
        public Dictionary<string, string> FileShas { get; } = new(StringComparer.Ordinal);

        /// <summary>Branches whose commits are all on main, so moving them onto it is a fast-forward.</summary>
        public HashSet<string> FastForwardable { get; } = new(StringComparer.Ordinal);

        public FakeGitHubApi Api { get; }

        public BranchWorld()
        {
            Api = WritableApi()
                .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/", request =>
                {
                    var name = After(request, "/git/ref/heads/");
                    return Heads.TryGetValue(name, out var sha)
                        ? (HttpStatusCode.OK, $"{{\"ref\":\"refs/heads/{name}\",\"object\":{{\"sha\":\"{sha}\"}}}}")
                        : (HttpStatusCode.NotFound, null);
                })
                .On(HttpMethod.Post, $"/repos/{Repo}/git/refs", request =>
                {
                    using var body = System.Text.Json.JsonDocument.Parse(BodyText(request));
                    var name = body.RootElement.GetProperty("ref").GetString()!["refs/heads/".Length..];
                    if (Heads.ContainsKey(name))
                    {
                        return (HttpStatusCode.UnprocessableEntity, "{\"message\":\"Reference already exists\"}");
                    }
                    Heads[name] = body.RootElement.GetProperty("sha").GetString()!;
                    return (HttpStatusCode.Created, FakeGitHubApi.ShaJson(Heads[name]));
                })
                .On(HttpMethod.Patch, $"/repos/{Repo}/git/refs/heads/", request =>
                {
                    var name = After(request, "/git/refs/heads/");
                    using var body = System.Text.Json.JsonDocument.Parse(BodyText(request));
                    var force = body.RootElement.GetProperty("force").GetBoolean();
                    if (!force && !FastForwardable.Contains(name))
                    {
                        return (HttpStatusCode.UnprocessableEntity, "{\"message\":\"Update is not a fast forward\"}");
                    }
                    Heads[name] = body.RootElement.GetProperty("sha").GetString()!;
                    return (HttpStatusCode.OK, FakeGitHubApi.ShaJson(Heads[name]));
                })
                .On(HttpMethod.Get, $"/repos/{Repo}/pulls", request =>
                {
                    var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
                    var head = query["head"] ?? string.Empty;
                    var name = head[(head.IndexOf(':') + 1)..];
                    if (query["state"] == "open" && OpenPullRequests.TryGetValue(name, out var number))
                    {
                        return (HttpStatusCode.OK, $"[{{\"number\":{number},\"html_url\":\"https://github.com/{Repo}/pull/{number}\"}}]");
                    }
                    if (query["state"] == "closed" && ClosedPullRequestHeads.TryGetValue(name, out var closedHead))
                    {
                        return (HttpStatusCode.OK,
                            $"[{{\"number\":9,\"html_url\":\"https://github.com/{Repo}/pull/9\",\"head\":{{\"sha\":\"{closedHead}\"}}}}]");
                    }
                    return (HttpStatusCode.OK, "[]");
                })
                .On(HttpMethod.Get, $"/repos/{Repo}/contents/", request =>
                {
                    var gitRef = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["ref"];
                    if (gitRef is null || Heads.GetValueOrDefault(gitRef) == BaseSha)
                    {
                        return (HttpStatusCode.OK, FakeGitHubApi.FileContentsJson(FilePath, Xliff, LoadedSha));
                    }
                    return FileShas.TryGetValue(gitRef, out var sha)
                        ? (HttpStatusCode.OK, FakeGitHubApi.FileContentsJson(FilePath, Xliff, sha))
                        : (HttpStatusCode.NotFound, null);
                });
        }

        private static string After(HttpRequestMessage request, string marker)
        {
            var path = request.RequestUri!.AbsolutePath;
            return Uri.UnescapeDataString(path[(path.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]);
        }

        private static string BodyText(HttpRequestMessage request) =>
            request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
    }

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
