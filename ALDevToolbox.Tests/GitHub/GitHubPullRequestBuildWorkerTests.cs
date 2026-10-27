using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The routing half of the compile gate (issue #627): turning an installation id
/// GitHub sent us back into the organisation that connected it, without a single
/// cross-tenant read.
///
/// <para>This is the piece the fence question in <c>CLAUDE.md</c> is about. One
/// <c>IgnoreQueryFilters()</c> would answer it in a query, and it would sit in
/// code an anonymous inbound request reaches. Instead the worker walks
/// <c>organizations</c> - which carries no tenant filter - and asks each
/// organisation, under its own filter and its own ambient scope, what it
/// connected. These tests pin that the walk finds the right organisation, and
/// only that one.</para>
/// </summary>
public sealed class GitHubPullRequestBuildWorkerTests : IDisposable
{
    private const long ConnectedInstallation = 42;

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_organisation_that_connected_the_installation_is_the_one_found()
    {
        await ConfigureDeploymentAsync();
        var otherOrgId = await SeedOrganizationAsync("Other");
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await ConnectAsync(otherOrgId, 99, "someone-else");

        var worker = NewWorker();

        var resolved = await worker.ResolveOrganizationAsync(ConnectedInstallation, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Value.Identity.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        resolved.Value.OrgLogin.Should().Be("cronus-dk");
    }

    [Fact]
    public async Task An_installation_nobody_connected_resolves_to_nothing()
    {
        // Ordinary rather than alarming: an app can be installed on a GitHub
        // organisation that no workbench organisation has connected, and a
        // disconnected one keeps its webhook until the installation is removed.
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");

        var resolved = await NewWorker().ResolveOrganizationAsync(4242, CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task With_no_organisation_connected_at_all_nothing_resolves()
    {
        await ConfigureDeploymentAsync();

        var resolved = await NewWorker().ResolveOrganizationAsync(ConnectedInstallation, CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task The_resolved_identity_carries_the_organisations_own_system_flag()
    {
        // The flag decides how storage-quota and template-import rules treat the
        // organisation, so scheduled work has to see the same value an interactive
        // request would.
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");

        await using var ctx = _db.NewContext();
        var isSystem = await ctx.Organizations.AsNoTracking()
            .Where(o => o.Id == TestDb.DefaultOrgId).Select(o => o.IsSystem).SingleAsync();

        var resolved = await NewWorker().ResolveOrganizationAsync(ConnectedInstallation, CancellationToken.None);

        resolved!.Value.Identity.IsSystemOrganization.Should().Be(isSystem);
        resolved.Value.Identity.IsSiteAdmin.Should().BeFalse("background work acts for an organisation, never as a site admin");
        resolved.Value.Identity.UserId.Should().BeNull("a webhook build has no user behind it");
    }

    [Fact]
    public async Task A_pending_organisation_is_not_asked()
    {
        // Pending organisations are signup rows nobody has approved; the sweeps
        // skip them and so does this.
        await ConfigureDeploymentAsync();
        var pendingId = await SeedOrganizationAsync("Pending", isPending: true);
        await ConnectAsync(pendingId, ConnectedInstallation, "cronus-dk");

        var resolved = await NewWorker().ResolveOrganizationAsync(ConnectedInstallation, CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task A_delivery_that_arrives_during_a_restore_is_put_back_rather_than_built()
    {
        // The webhook route stays open through maintenance on purpose - GitHub
        // disables a hook whose deliveries keep failing - so the worker is what
        // has to keep the build off a database being rewritten under it.
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");

        var queue = new GitHubWebhookQueue();
        var maintenance = new MaintenanceModeState();
        maintenance.Enter("Restoring a backup");
        var worker = NewWorker(queue, maintenance);
        var job = NewJob();

        await worker.RunOneAsync(job, CancellationToken.None);

        queue.Reader.TryRead(out var again).Should().BeTrue("the delivery is offered again, not dropped");
        again.Should().Be(job, "the same head is built once the restore is over");
    }

    // --- Member forks (#627) -----------------------------------------------

    [Fact]
    public async Task A_member_fork_is_built_when_GitHub_confirms_the_membership()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        var api = ApiAnswering(HttpStatusCode.NoContent);
        var builds = new ProjectBuildQueue();

        await NewWorker(api: api, builds: builds).RunOneAsync(NewJob(isMemberFork: true), CancellationToken.None);

        api.Calls.Should().Contain(c => c.Contains("/orgs/cronus-dk/members/erik"),
            "the delivery's author_association is re-checked at build time, not trusted");
        api.Calls.Should().Contain(c => c.Contains("/check-runs"), "the pull request gets an answer");
        builds.Reader.TryRead(out var queued).Should().BeTrue("a confirmed member's fork builds like any branch");
        queued!.Source.Should().BeOfType<ReleaseImportSource.PullRequestBuild>()
            .Which.ForkAuthor.Should().Be("erik", "the check run says where the code came from");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(FakeGitHubApi.Unreachable)]
    public async Task A_fork_whose_author_GitHub_does_not_confirm_is_dropped_without_a_check_run(HttpStatusCode membership)
    {
        // 404 is "not a member", 302 is "you are not in this organisation
        // either", and no answer at all is not a yes. All three refuse, and none
        // of them opens a check run - there is nothing to leave spinning on a
        // pull request the workbench will say nothing about.
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        var api = ApiAnswering(membership);
        var builds = new ProjectBuildQueue();

        await NewWorker(api: api, builds: builds).RunOneAsync(NewJob(isMemberFork: true), CancellationToken.None);

        api.Calls.Should().NotContain(c => c.Contains("/check-runs"));
        builds.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_pull_request_from_the_repository_itself_never_asks_about_membership()
    {
        // The membership call costs a request on the organisation's rate limit
        // and answers a question a branch pull request does not raise.
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        var api = ApiAnswering(HttpStatusCode.NoContent);
        var builds = new ProjectBuildQueue();

        await NewWorker(api: api, builds: builds).RunOneAsync(NewJob(), CancellationToken.None);

        api.Calls.Should().NotContain(c => c.Contains("/members/"));
        builds.Reader.TryRead(out var queued).Should().BeTrue();
        queued!.Source.Should().BeOfType<ReleaseImportSource.PullRequestBuild>()
            .Which.ForkAuthor.Should().BeNull("a branch of the repository is not anybody's fork");
    }

    // --- Branch watching: replayed push and merged-PR deliveries (#963) -----

    [Fact]
    public async Task A_push_to_a_tracked_repository_stores_the_branch_head()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        var builds = new ProjectBuildQueue();

        await NewWorker(builds: builds).RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push(commitCount: 3)), CancellationToken.None);

        var head = (await HeadsAsync()).Should().ContainSingle().Subject;
        head.Branch.Should().Be("main");
        head.HeadSha.Should().Be(GitHubWebhookPayloads.After);
        head.PusherLogin.Should().Be("erik");
        head.CommitCount.Should().Be(3);
        head.Forced.Should().BeFalse();
        head.IsDefaultBranch.Should().BeTrue();
        head.DeletedAt.Should().BeNull();
        head.PushedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000).UtcDateTime);
        head.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        var commits = GitHubBranchActivityService.ReadCommits(head.CommitsJson);
        commits.Select(c => c.Sha).Should().Equal(
            GitHubWebhookPayloads.Sha(1), GitHubWebhookPayloads.Sha(2), GitHubWebhookPayloads.After);
        commits[0].Message.Should().StartWith("Commit 1 on main");
        builds.Reader.TryRead(out _).Should().BeFalse("nothing is built on a push");
    }

    [Fact]
    public async Task A_push_keeps_only_the_newest_ten_commits()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();

        await NewWorker().RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push(commitCount: 20)), CancellationToken.None);

        var head = (await HeadsAsync()).Single();
        head.CommitCount.Should().Be(20, "the count is what the push carried");
        var commits = GitHubBranchActivityService.ReadCommits(head.CommitsJson);
        commits.Should().HaveCount(10);
        commits[^1].Sha.Should().Be(GitHubWebhookPayloads.After, "the newest are the ones kept");
    }

    [Fact]
    public async Task A_fast_forward_push_adds_to_the_stored_commits()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        var worker = NewWorker();
        const string Next = "4444444444444444444444444444444444444444";

        await worker.RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push(commitCount: 1)), CancellationToken.None);
        await worker.RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push(
            before: GitHubWebhookPayloads.After, after: Next, commitCount: 1)), CancellationToken.None);

        var head = (await HeadsAsync()).Single();
        head.HeadSha.Should().Be(Next);
        GitHubBranchActivityService.ReadCommits(head.CommitsJson).Select(c => c.Sha)
            .Should().Equal(GitHubWebhookPayloads.After, Next);
    }

    [Fact]
    public async Task A_push_that_arrives_after_a_newer_one_leaves_the_branch_head_alone()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        var worker = NewWorker();
        const string Next = "4444444444444444444444444444444444444444";

        await worker.RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push(
            before: GitHubWebhookPayloads.After, after: Next, pushedAt: 1_790_000_060)), CancellationToken.None);
        await worker.RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push(pushedAt: 1_790_000_000)), CancellationToken.None);

        var head = (await HeadsAsync()).Single();
        head.HeadSha.Should().Be(Next, "GitHub does not promise delivery order, so the older push is late, not new");
        head.PushedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_790_000_060).UtcDateTime);
    }

    [Fact]
    public async Task A_force_push_is_flagged_and_replaces_the_stored_commits()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        var worker = NewWorker();
        const string Rewritten = "5555555555555555555555555555555555555555";

        await worker.RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push(commitCount: 2)), CancellationToken.None);
        await worker.RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push(
            before: GitHubWebhookPayloads.After, after: Rewritten, forced: true, commitCount: 1)), CancellationToken.None);

        var head = (await HeadsAsync()).Single();
        head.HeadSha.Should().Be(Rewritten);
        head.Forced.Should().BeTrue();
        GitHubBranchActivityService.ReadCommits(head.CommitsJson).Select(c => c.Sha)
            .Should().Equal([Rewritten], "the rewritten-away commits may not be on the branch any more");
    }

    [Fact]
    public async Task A_deleted_branch_is_marked_gone_rather_than_removed_and_comes_back_when_pushed_again()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        var worker = NewWorker();

        await worker.RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push(branch: "release/25.0")), CancellationToken.None);
        await worker.RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push(
            branch: "release/25.0", before: GitHubWebhookPayloads.After, deleted: true)), CancellationToken.None);

        var gone = (await HeadsAsync()).Single();
        gone.DeletedAt.Should().NotBeNull();
        gone.HeadSha.Should().Be(GitHubWebhookPayloads.After, "the commit the branch last pointed at is kept");
        gone.IsDefaultBranch.Should().BeFalse();

        await worker.RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push(
            branch: "release/25.0", before: GitHubWebhookPayloads.Zero, created: true)), CancellationToken.None);

        (await HeadsAsync()).Single().DeletedAt.Should().BeNull();
    }

    [Fact]
    public void A_tag_push_is_not_read_as_a_branch_moving()
    {
        var job = GitHubWebhookEndpoints.TryReadPush(
            System.Text.Encoding.UTF8.GetBytes(GitHubWebhookPayloads.Push(reference: "refs/tags/v25.0.1")),
            "delivery", NullLogger.Instance);

        job.Should().BeNull();
    }

    [Fact]
    public async Task A_push_to_a_repository_no_solution_tracks_writes_nothing()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();

        await NewWorker().RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push(
            cloneUrl: "https://github.com/cronus-dk/somebody-elses-tool.git")), CancellationToken.None);

        (await HeadsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_push_for_an_installation_nobody_connected_writes_nothing()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();

        await NewWorker().RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push(installationId: 4242)), CancellationToken.None);

        (await HeadsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_push_refreshes_a_module_copied_from_the_repository_even_with_no_solution_tracking_it()
    {
        // #1415: modules follow a branch through the same pushes the pipelines
        // listen to, so a module's repository needs no solution of its own.
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        int moduleId;
        await using (var ctx = _db.NewContext())
        {
            var module = new Module
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "customer-app",
                Name = "Customer App",
                ExtensionName = "CustomerApp",
                SourceRepository = "cronus-dk/customer-app",
                SourceCommitSha = Templates.ModuleSourceTestKit.ShaA,
                SourceFetchedAt = DateTime.UtcNow.AddDays(-1),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.Modules.Add(module);
            await ctx.SaveChangesAsync();
            moduleId = module.Id;
        }
        var api = Templates.ModuleSourceTestKit.ServeTarball(
            Templates.ModuleSourceTestKit.ResolveRef(
                Templates.ModuleSourceTestKit.Repository(ApiAnswering(HttpStatusCode.NoContent), "cronus-dk/customer-app", isPrivate: true),
                "cronus-dk/customer-app", "HEAD", GitHubWebhookPayloads.After),
            "cronus-dk/customer-app", GitHubWebhookPayloads.After, Templates.ModuleSourceTestKit.SimpleExtension());

        await NewWorker(api: api).RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push()), CancellationToken.None);

        await using var verify = _db.NewContext();
        (await verify.Modules.AsNoTracking().SingleAsync(m => m.Id == moduleId))
            .SourceCommitSha.Should().Be(GitHubWebhookPayloads.After);
    }

    [Fact]
    public async Task Every_solution_tracking_the_repository_gets_its_own_head()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        await SeedSolutionTrackingTheRepositoryAsync("CRONUS Second Solution", "https://github.com/CRONUS-dk/customer-app");

        await NewWorker().RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push()), CancellationToken.None);

        (await HeadsAsync()).Select(h => h.ProjectRepositoryId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_default_branch_rename_moves_the_default_flag_on_the_next_push()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        var worker = NewWorker();

        await worker.RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push(branch: "master", defaultBranch: "master")), CancellationToken.None);
        await worker.RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push(branch: "main", defaultBranch: "main")), CancellationToken.None);

        var heads = await HeadsAsync();
        heads.Single(h => h.Branch == "main").IsDefaultBranch.Should().BeTrue();
        heads.Single(h => h.Branch == "master").IsDefaultBranch.Should().BeFalse();
    }

    [Fact]
    public async Task A_merged_pull_request_is_recorded_once_even_when_redelivered()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        var worker = NewWorker();
        var builds = new ProjectBuildQueue();
        var job = GitHubWebhookEndpoints.TryReadMergedPullRequest(
            System.Text.Encoding.UTF8.GetBytes(GitHubWebhookPayloads.MergedPullRequest(number: 12)), "delivery", NullLogger.Instance);
        job.Should().NotBeNull();

        await NewWorker(builds: builds).RunOneAsync(job!, CancellationToken.None);
        await worker.RunOneAsync(job!, CancellationToken.None);

        await using var ctx = _db.NewContext();
        var row = (await ctx.OeRepositoryMergedPullRequests.AsNoTracking().ToListAsync()).Should().ContainSingle().Subject;
        row.Number.Should().Be(12);
        row.Title.Should().Be("Post VAT to the right account");
        row.BaseBranch.Should().Be("main");
        row.MergeSha.Should().Be(GitHubWebhookPayloads.MergeSha);
        row.MergedAt.Should().Be(new DateTime(2026, 9, 24, 8, 30, 0, DateTimeKind.Utc));
        row.AuthorLogin.Should().Be("erik");
        builds.Reader.TryRead(out _).Should().BeFalse("a merged pull request is recorded, not built");
    }

    [Fact]
    public async Task Merged_pull_requests_far_older_than_a_new_one_are_dropped()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        var worker = NewWorker();
        var merged = DateTime.UtcNow;
        var old = merged - GitHubBranchActivityService.MergedPullRequestRetention - TimeSpan.FromDays(1);
        var recent = merged - GitHubBranchActivityService.MergedPullRequestRetention + TimeSpan.FromDays(1);

        foreach (var (number, at) in new[] { (10, old), (11, recent), (12, merged) })
        {
            var job = GitHubWebhookEndpoints.TryReadMergedPullRequest(
                System.Text.Encoding.UTF8.GetBytes(GitHubWebhookPayloads.MergedPullRequest(number: number, mergedAt: at.ToString("yyyy-MM-ddTHH:mm:ssZ"))),
                "delivery", NullLogger.Instance);
            await worker.RunOneAsync(job!, CancellationToken.None);
        }

        await using var ctx = _db.NewContext();
        (await ctx.OeRepositoryMergedPullRequests.AsNoTracking().Select(m => m.Number).OrderBy(n => n).ToListAsync())
            .Should().Equal(11, 12);
    }

    [Fact]
    public void A_pull_request_closed_without_merging_is_not_read_as_a_merge()
    {
        var job = GitHubWebhookEndpoints.TryReadMergedPullRequest(
            System.Text.Encoding.UTF8.GetBytes(GitHubWebhookPayloads.MergedPullRequest(merged: false)), "delivery", NullLogger.Instance);

        job.Should().BeNull();
    }

    private static GitHubPushJob ReplayPush(string json)
    {
        var job = GitHubWebhookEndpoints.TryReadPush(System.Text.Encoding.UTF8.GetBytes(json), "delivery", NullLogger.Instance);
        job.Should().NotBeNull("the replayed payload is a branch push the parser accepts");
        return job!;
    }

    private async Task<List<ALDevToolbox.Domain.Entities.ObjectExplorer.OeRepositoryBranchHead>> HeadsAsync()
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeRepositoryBranchHeads.AsNoTracking().OrderBy(h => h.Id).ToListAsync();
    }

    // --- Another organisation tracking the same repository ------------------
    //
    // Any organisation can type any github.com address into a solution, so two
    // tenants tracking one repository is ordinary. A delivery names an
    // installation, and only the organisation that connected it may see anything
    // happen. The reads below cross the tenant filter on purpose, to see every
    // organisation's rows.
    //
    // These workers run on the app's own organisation context rather than the
    // fixture's, which is pinned to the default organisation: off a request it
    // follows the ambient scope the worker enters, so a scope entered for the
    // wrong organisation, or too late, shows up here.

    [Fact]
    public async Task A_pull_request_builds_only_the_solution_of_the_organisation_that_connected_the_installation()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        var otherOrgId = await SeedOrganizationAsync("Other");
        await ConnectAsync(otherOrgId, 99, "someone-else");
        var ownProjectId = await SeedSolutionTrackingTheRepositoryAsync();
        await SeedSolutionTrackingTheRepositoryAsync("Other Customer App", organizationId: otherOrgId);
        var api = ApiAnswering(HttpStatusCode.NoContent);
        var builds = new ProjectBuildQueue();

        await NewWorker(api: api, builds: builds, orgContext: OffRequest()).RunOneAsync(NewJob(), CancellationToken.None);

        builds.Reader.TryRead(out var queued).Should().BeTrue();
        queued!.Identity.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        queued.Source.Should().BeOfType<ReleaseImportSource.PullRequestBuild>().Which.ProjectId.Should().Be(ownProjectId);
        builds.Reader.TryRead(out _).Should().BeFalse("the other organisation's solution is not built");
        api.Calls.Count(c => c.Contains("/check-runs")).Should().Be(1, "one check run, for the one solution");
        (await AllBuildsAsync()).Should().OnlyContain(b => b.OrganizationId == TestDb.DefaultOrgId);
    }

    [Fact]
    public async Task A_pull_request_never_reaches_another_organisations_solution_when_its_own_tracks_nothing()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        var otherOrgId = await SeedOrganizationAsync("Other");
        await ConnectAsync(otherOrgId, 99, "someone-else");
        await SeedSolutionTrackingTheRepositoryAsync("Other Customer App", organizationId: otherOrgId);
        var api = ApiAnswering(HttpStatusCode.NoContent);
        var builds = new ProjectBuildQueue();

        await NewWorker(api: api, builds: builds, orgContext: OffRequest()).RunOneAsync(NewJob(), CancellationToken.None);

        builds.Reader.TryRead(out _).Should().BeFalse();
        api.Calls.Should().NotContain(c => c.Contains("/check-runs"));
        (await AllBuildsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_push_writes_branch_heads_only_for_the_organisation_that_connected_the_installation()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        var otherOrgId = await SeedOrganizationAsync("Other");
        await ConnectAsync(otherOrgId, 99, "someone-else");
        await SeedSolutionTrackingTheRepositoryAsync();
        await SeedSolutionTrackingTheRepositoryAsync("Other Customer App", organizationId: otherOrgId);

        await NewWorker(orgContext: OffRequest()).RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push()), CancellationToken.None);

        (await AllHeadsAsync()).Should().ContainSingle().Which.OrganizationId.Should().Be(TestDb.DefaultOrgId);
    }

    [Fact]
    public async Task A_push_never_reaches_another_organisations_solution_when_its_own_tracks_nothing()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        var otherOrgId = await SeedOrganizationAsync("Other");
        await ConnectAsync(otherOrgId, 99, "someone-else");
        await SeedSolutionTrackingTheRepositoryAsync("Other Customer App", organizationId: otherOrgId);

        await NewWorker(orgContext: OffRequest()).RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push()), CancellationToken.None);

        (await AllHeadsAsync()).Should().BeEmpty();
        (await AllBuildsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_merged_pull_request_is_recorded_only_for_the_organisation_that_connected_the_installation()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        var otherOrgId = await SeedOrganizationAsync("Other");
        await ConnectAsync(otherOrgId, 99, "someone-else");
        await SeedSolutionTrackingTheRepositoryAsync();
        await SeedSolutionTrackingTheRepositoryAsync("Other Customer App", organizationId: otherOrgId);
        var job = GitHubWebhookEndpoints.TryReadMergedPullRequest(
            System.Text.Encoding.UTF8.GetBytes(GitHubWebhookPayloads.MergedPullRequest()), "delivery", NullLogger.Instance);

        await NewWorker(orgContext: OffRequest()).RunOneAsync(job!, CancellationToken.None);

        await using var ctx = _db.NewContext();
        (await ctx.OeRepositoryMergedPullRequests.IgnoreQueryFilters().AsNoTracking().ToListAsync())
            .Should().ContainSingle().Which.OrganizationId.Should().Be(TestDb.DefaultOrgId);
    }

    // The same two, with the roles turned round: the default organisation is the
    // first one asked and also tracks the repository, but connected a different
    // installation. Picking whichever organisation comes first would pass the
    // cases above and fail these.

    [Fact]
    public async Task A_pull_request_builds_in_the_connecting_organisation_even_when_it_is_not_the_first()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, 99, "someone-else");
        var connectingOrgId = await SeedOrganizationAsync("Connecting");
        await ConnectAsync(connectingOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        var connectingProjectId = await SeedSolutionTrackingTheRepositoryAsync("Connecting Customer App", organizationId: connectingOrgId);
        var api = ApiAnswering(HttpStatusCode.NoContent);
        var builds = new ProjectBuildQueue();

        await NewWorker(api: api, builds: builds, orgContext: OffRequest()).RunOneAsync(NewJob(), CancellationToken.None);

        builds.Reader.TryRead(out var queued).Should().BeTrue();
        queued!.Identity.OrganizationId.Should().Be(connectingOrgId);
        queued.Source.Should().BeOfType<ReleaseImportSource.PullRequestBuild>().Which.ProjectId.Should().Be(connectingProjectId);
        builds.Reader.TryRead(out _).Should().BeFalse("the default organisation's solution is not built");
        (await AllBuildsAsync()).Should().OnlyContain(b => b.OrganizationId == connectingOrgId);
    }

    [Fact]
    public async Task A_push_writes_its_head_in_the_connecting_organisation_even_when_it_is_not_the_first()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, 99, "someone-else");
        var connectingOrgId = await SeedOrganizationAsync("Connecting");
        await ConnectAsync(connectingOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        await SeedSolutionTrackingTheRepositoryAsync("Connecting Customer App", organizationId: connectingOrgId);

        await NewWorker(orgContext: OffRequest()).RunOneAsync(ReplayPush(GitHubWebhookPayloads.Push()), CancellationToken.None);

        (await AllHeadsAsync()).Should().ContainSingle().Which.OrganizationId.Should().Be(connectingOrgId);
    }

    /// <summary>The organisation context the hosted worker gets: no request, so only the ambient scope speaks.</summary>
    private static IOrganizationContext OffRequest() =>
        new HttpOrganizationContext(new Microsoft.AspNetCore.Http.HttpContextAccessor());

    /// <summary>Every organisation's branch heads.</summary>
    private async Task<List<ALDevToolbox.Domain.Entities.ObjectExplorer.OeRepositoryBranchHead>> AllHeadsAsync()
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeRepositoryBranchHeads.IgnoreQueryFilters().AsNoTracking().ToListAsync();
    }

    /// <summary>Every organisation's solution builds.</summary>
    private async Task<List<ALDevToolbox.Domain.Entities.ObjectExplorer.OeProjectBuild>> AllBuildsAsync()
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeProjectBuilds.IgnoreQueryFilters().AsNoTracking().ToListAsync();
    }

    // --- Fixture -----------------------------------------------------------

    private static GitHubPullRequestJob NewJob(bool isMemberFork = false, string authorLogin = "erik") => new(
        InstallationId: ConnectedInstallation,
        RepositoryFullName: "cronus-dk/customer-app",
        CloneUrl: "https://github.com/cronus-dk/customer-app.git",
        PullRequestNumber: 7,
        HeadSha: "abc1234",
        HeadRef: "feature/vat",
        BaseRef: "main",
        DeliveryId: "delivery-1",
        AuthorLogin: authorLogin,
        IsMemberFork: isMemberFork);

    /// <summary>
    /// A GitHub that mints an installation token, answers the membership
    /// question with <paramref name="membership"/>, and takes a check run.
    /// <see cref="FakeGitHubApi.Unreachable"/> is GitHub not answering at all.
    /// </summary>
    private static FakeGitHubApi ApiAnswering(HttpStatusCode membership)
    {
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{ConnectedInstallation}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Post, "/repos/cronus-dk/customer-app/check-runs",
                HttpStatusCode.Created, "{\"id\":555}");
        return api.On(HttpMethod.Get, "/orgs/cronus-dk/members/erik", membership);
    }

    /// <summary>A solution tracking the repository the deliveries are about; answers its id.</summary>
    private async Task<int> SeedSolutionTrackingTheRepositoryAsync(
        string name = "CRONUS Customer App", string url = "https://github.com/cronus-dk/customer-app.git",
        int organizationId = TestDb.DefaultOrgId)
    {
        await using var ctx = _db.NewContext();
        var project = new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProject
        {
            OrganizationId = organizationId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        ctx.OeProjectRepositories.Add(new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProjectRepository
        {
            OrganizationId = organizationId,
            ProjectId = project.Id,
            Url = url,
            Provider = ALDevToolbox.Domain.ValueObjects.RepositoryProvider.GitHub,
            DisplayName = "customer-app",
        });
        await ctx.SaveChangesAsync();
        return project.Id;
    }


    /// <summary>
    /// A worker over a service provider that hands out a fresh scope per
    /// organisation, exactly as the hosted worker's own does - the point of the
    /// test is that each read happens under its own tenant filter.
    /// </summary>
    private GitHubPullRequestBuildWorker NewWorker(
        GitHubWebhookQueue? queue = null,
        MaintenanceModeState? maintenance = null,
        FakeGitHubApi? api = null,
        ProjectBuildQueue? builds = null,
        IOrganizationContext? orgContext = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOrganizationContext>(orgContext ?? _db.OrgContext);
        services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));
        services.AddDataProtection();
        _db.AddStorageServices(services);
        services.AddScoped<OrganizationConfigService>();
        services.AddScoped<SystemSettingsService>();
        _db.AddGitHubServices(services, api ?? new FakeGitHubApi());

        // Everything from the check run down: a member fork that passes the gate
        // has to reach a real OpenAsync and a real StartPullRequestBuildAsync, or
        // "it was built" is only the absence of a log line.
        services.AddScoped<GitHubCheckRunService>();
        services.AddSingleton(builds ?? new ProjectBuildQueue());
        services.AddScoped<ALDevToolbox.Services.Translation.TranslationMemoryService>();
        services.AddScoped<TranslationImportService>();
        services.AddScoped<CallSiteReferenceEmitter>();
        // Built by hand rather than by the container: the dependency-drift scan is
        // an optional constructor argument the container would insist on
        // resolving, and it plays no part in a pull-request build.
        services.AddScoped(sp => new ReleaseImportService(
            sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<IOrganizationContext>(),
            sp.GetRequiredService<StorageQuotaGuard>(),
            sp.GetRequiredService<TranslationImportService>(),
            sp.GetRequiredService<CallSiteReferenceEmitter>(),
            NullLogger<ReleaseImportService>.Instance));
        services.AddScoped<PersistedImportJobs>();
        services.AddScoped<ProjectAccess>();
        services.AddScoped(sp => new ReleaseManagementService(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IOrganizationContext>(),
            new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IOrganizationContext>()),
            NullLogger<ReleaseManagementService>.Instance));
        services.AddScoped<ProjectBuildImporter>();
        // The importer's credential check is for manual builds; a pull-request build never consults it.
        services.AddScoped<CloneCredentialResolver>(_ => null!);
        services.AddScoped<GitHubBranchActivityService>();
        services.AddScoped<PushBuildService>();

        var provider = services.BuildServiceProvider();
        return new GitHubPullRequestBuildWorker(
            queue ?? new GitHubWebhookQueue(), provider,
            maintenance ?? new MaintenanceModeState(),
            NullLogger<GitHubPullRequestBuildWorker>.Instance,
            new ALDevToolbox.Services.Workers.WorkerHeartbeatRegistry(TimeProvider.System))
        {
            MaintenanceRetryDelay = TimeSpan.Zero,
        };
    }

    private async Task ConfigureDeploymentAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
    }

    private async Task<int> SeedOrganizationAsync(string name, bool isPending = false)
    {
        await using var ctx = _db.NewContext();
        var org = new Organization
        {
            Name = name + " " + Guid.NewGuid().ToString("N")[..8],
            IsPending = isPending,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Organizations.Add(org);
        await ctx.SaveChangesAsync();
        return org.Id;
    }

    /// <summary>Records a connection directly, without the guarded ConnectAsync handshake.</summary>
    private async Task ConnectAsync(int organizationId, long installationId, string orgLogin)
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = organizationId,
            GitHubInstallationId = installationId,
            GitHubOrgLogin = orgLogin,
            GitHubConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }
}
