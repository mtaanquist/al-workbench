using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Building on push (#1079): which pipelines a GitHub push builds, and the whole
/// path from a replayed delivery through <see cref="GitHubPullRequestBuildWorker"/>
/// to a queued build that runs as the person who turned it on.
/// </summary>
public sealed class PushBuildTests : IDisposable
{
    private const long ConnectedInstallation = 42;

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    // --- Which pipelines a push builds ---------------------------------------

    [Fact]
    public async Task A_push_to_the_watched_branch_is_due_with_the_pushed_repository_and_the_person()
    {
        var owner = await SeedUserAsync();
        var (repositoryId, pipelineId) = await SeedSolutionAsync(owner, branch: "main");

        var due = await ListDueAsync(Push());

        due.Should().ContainSingle().Which.Should().Be(new PushBuildDue(pipelineId, repositoryId, owner, null));
    }

    [Fact]
    public async Task A_pipeline_without_a_branch_builds_pushes_to_the_default_branch_only()
    {
        var owner = await SeedUserAsync();
        var (_, pipelineId) = await SeedSolutionAsync(owner, branch: null);

        (await ListDueAsync(Push())).Should().ContainSingle().Which.PipelineId.Should().Be(pipelineId);
        (await ListDueAsync(Push(branch: "feature/vat"))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_push_to_another_branch_or_a_pipeline_that_does_not_build_on_push_is_not_due()
    {
        var owner = await SeedUserAsync();
        await SeedSolutionAsync(owner, branch: "release/25.0");
        await SeedSolutionAsync(owner, branch: "main", buildOnPush: false);

        (await ListDueAsync(Push())).Should().BeEmpty();
    }

    [Fact]
    public async Task A_push_to_a_disabled_pipeline_is_not_due()
    {
        var owner = await SeedUserAsync();
        var (_, pipelineId) = await SeedSolutionAsync(owner, branch: "main");
        await using (var ctx = _db.NewContext())
        {
            await ctx.OePipelines.Where(p => p.Id == pipelineId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.DisabledAt, DateTime.UtcNow));
        }

        (await ListDueAsync(Push())).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_forced_push_or_a_deleted_branch_builds_nothing(bool forced, bool deleted)
    {
        var owner = await SeedUserAsync();
        await SeedSolutionAsync(owner, branch: "main");
        var push = Push(forced: forced, deleted: deleted);

        PushBuildService.IsBuildable(push).Should().BeFalse();
        (await ListDueAsync(push)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_pipeline_whose_person_is_gone_is_due_with_the_reason()
    {
        var owner = await SeedUserAsync();
        var (_, pipelineId) = await SeedSolutionAsync(owner, branch: "main");
        await using (var ctx = _db.NewContext())
        {
            await ctx.Users.Where(u => u.Id == owner).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, UserStatus.Disabled));
        }

        var due = (await ListDueAsync(Push())).Should().ContainSingle().Subject;

        due.PipelineId.Should().Be(pipelineId);
        due.UserId.Should().BeNull();
        due.Blocked.Should().Be(AutomatedBuilds.NoOwnerMessage);
    }

    [Fact]
    public async Task A_commit_already_built_on_push_is_not_due_again()
    {
        var owner = await SeedUserAsync();
        var (_, pipelineId) = await SeedSolutionAsync(owner, branch: "main");
        await using (var ctx = _db.NewContext())
        {
            var projectId = await ctx.OePipelines.Where(p => p.Id == pipelineId).Select(p => p.ProjectId).SingleAsync();
            ctx.OeProjectBuilds.Add(new OeProjectBuild
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipelineId,
                Trigger = ProjectBuildTrigger.Push, HeadSha = GitHubWebhookPayloads.After,
                Status = ProjectBuildStatus.Ready, StartedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        (await ListDueAsync(Push())).Should().BeEmpty("GitHub redelivering a push does not build it twice");
    }

    [Fact]
    public async Task A_push_older_than_the_branch_head_already_recorded_is_not_due()
    {
        var owner = await SeedUserAsync();
        var (repositoryId, _) = await SeedSolutionAsync(owner, branch: "main");
        var push = Push();
        await using (var ctx = _db.NewContext())
        {
            ctx.OeRepositoryBranchHeads.Add(new OeRepositoryBranchHead
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectRepositoryId = repositoryId, Branch = "main",
                HeadSha = "4444444444444444444444444444444444444444", PushedAt = push.PushedAt.AddMinutes(1),
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        (await ListDueAsync(push)).Should().BeEmpty("a late or redelivered push must not build older code after newer");
    }

    // --- From the delivery to a queued build ---------------------------------

    [Fact]
    public async Task A_push_queues_a_build_of_the_pushed_commit_as_the_person_who_turned_it_on()
    {
        await ConnectAsync();
        var owner = await SeedUserAsync();
        await GiveTokenAsync(owner);
        var (repositoryId, pipelineId) = await SeedSolutionAsync(owner, branch: "main", blocked: "an old reason.");
        var builds = new ProjectBuildQueue();

        await NewWorker(builds).RunOneAsync(Push(), CancellationToken.None);

        await using var read = _db.NewContext();
        var build = await read.OeProjectBuilds.SingleAsync(b => b.PipelineId == pipelineId);
        build.Trigger.Should().Be(ProjectBuildTrigger.Push);
        build.StartedByUserId.Should().Be(owner);
        build.HeadSha.Should().Be(GitHubWebhookPayloads.After);
        build.HeadRepositoryId.Should().Be(repositoryId);
        builds.Reader.TryRead(out _).Should().BeTrue();
        (await read.OePipelines.SingleAsync(p => p.Id == pipelineId)).BuildOnPushBlocked
            .Should().BeNull("a build that started lifts the pause");
        (await read.OeRepositoryBranchHeads.CountAsync()).Should().Be(1, "the push is still recorded for freshness");
    }

    [Fact]
    public async Task Pushes_in_quick_succession_each_get_their_own_build_in_order()
    {
        await ConnectAsync();
        var owner = await SeedUserAsync();
        await GiveTokenAsync(owner);
        var (_, pipelineId) = await SeedSolutionAsync(owner, branch: "main");
        var worker = NewWorker(new ProjectBuildQueue());
        var second = GitHubWebhookPayloads.Sha(77);

        await worker.RunOneAsync(Push(), CancellationToken.None);
        await worker.RunOneAsync(Push(before: GitHubWebhookPayloads.After, after: second), CancellationToken.None);

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.Where(b => b.PipelineId == pipelineId).OrderBy(b => b.Id).Select(b => b.HeadSha).ToListAsync())
            .Should().Equal(GitHubWebhookPayloads.After, second);
    }

    [Fact]
    public async Task A_push_pauses_building_when_the_person_has_nothing_to_clone_with()
    {
        await ConnectAsync();
        var owner = await SeedUserAsync();
        var (_, pipelineId) = await SeedSolutionAsync(owner, branch: "main");

        await NewWorker(new ProjectBuildQueue()).RunOneAsync(Push(), CancellationToken.None);

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.AnyAsync()).Should().BeFalse();
        (await read.OePipelines.SingleAsync(p => p.Id == pipelineId)).BuildOnPushBlocked
            .Should().Be(ProjectBuildImporter.NothingToCloneWithOnPush(RepositoryProvider.GitHub));
    }

    [Fact]
    public async Task A_push_pauses_building_when_the_person_can_no_longer_manage_the_solution()
    {
        await ConnectAsync();
        var solutionOwner = await SeedUserAsync("owner@cronus.test");
        var former = await SeedUserAsync("former@cronus.test", UserRole.User);
        await GiveTokenAsync(former);
        var (_, pipelineId) = await SeedSolutionAsync(former, branch: "main", projectOwner: solutionOwner);

        await NewWorker(new ProjectBuildQueue()).RunOneAsync(Push(), CancellationToken.None);

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.AnyAsync()).Should().BeFalse();
        (await read.OePipelines.SingleAsync(p => p.Id == pipelineId)).BuildOnPushBlocked
            .Should().Be(AutomatedBuilds.NoAccessMessage);
    }

    [Fact]
    public async Task One_pipelines_refusal_does_not_stop_the_others()
    {
        await ConnectAsync();
        var withToken = await SeedUserAsync("alice@cronus.test");
        var withoutToken = await SeedUserAsync("bob@cronus.test");
        await GiveTokenAsync(withToken);
        var (_, refused) = await SeedSolutionAsync(withoutToken, branch: "main");
        var (_, built) = await SeedSolutionAsync(withToken, branch: "main");

        await NewWorker(new ProjectBuildQueue()).RunOneAsync(Push(), CancellationToken.None);

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.CountAsync(b => b.PipelineId == built)).Should().Be(1);
        (await read.OeProjectBuilds.CountAsync(b => b.PipelineId == refused)).Should().Be(0);
        (await read.OePipelines.SingleAsync(p => p.Id == refused)).BuildOnPushBlocked.Should().NotBeNull();
    }

    // --- Documentation-only pushes ------------------------------------------

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("docs/Setup.MD", true)]
    [InlineData("app/CHANGELOG.markdown", true)]
    [InlineData(".gitignore", true)]
    [InlineData("app/.gitignore", true)]
    [InlineData(".gitattributes", false)]
    [InlineData(".editorconfig", true)]
    [InlineData("LICENSE", true)]
    [InlineData(".github/CODEOWNERS", true)]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData("app/src/Vat.Codeunit.al", false)]
    [InlineData("app/app.json", false)]
    [InlineData("app/Translations/Customer App.da-DK.xlf", false)]
    [InlineData("app/src/Customer.PermissionSet.al", false)]
    [InlineData("app/logo.png", false)]
    [InlineData(".vscode/settings.json", false)]
    [InlineData("app/.github/notes.txt", false)]
    [InlineData("README.md.al", false)]
    public void Documentation_is_told_from_what_the_build_compiles(string path, bool documentation) =>
        DocumentationPaths.IsDocumentation(path).Should().Be(documentation);

    [Fact]
    public void A_push_reads_as_documentation_only_when_every_file_it_touched_is()
    {
        Push(paths: ["README.md", ".gitignore"]).DocumentationOnly.Should().BeTrue();
        Push(paths: ["README.md", "app/src/Vat.Codeunit.al"]).DocumentationOnly.Should().BeFalse();
        Push().DocumentationOnly.Should().BeFalse();
    }

    [Fact]
    public void A_push_whose_payload_cannot_vouch_for_every_file_is_not_documentation_only()
    {
        Push(paths: ["README.md"], commitCount: GitHubWebhookEndpoints.ListedPushCommitLimit).DocumentationOnly
            .Should().BeFalse("a payload listing that many commits may have been cut short");
        Push(paths: ["README.md"], commitCount: GitHubWebhookEndpoints.ListedPushCommitLimit - 1).DocumentationOnly
            .Should().BeTrue();
        Push(paths: [], commitCount: 0).DocumentationOnly
            .Should().BeFalse("a new branch at an existing commit lists no commits at all");
        Push(paths: []).DocumentationOnly.Should().BeFalse("a commit that touched nothing is not documentation");

        var withoutFileLists = System.Text.Json.Nodes.JsonNode.Parse(GitHubWebhookPayloads.Push(paths: ["README.md"]))!;
        withoutFileLists["commits"]![0]!.AsObject().Remove("removed");
        GitHubWebhookEndpoints.TryReadPush(
                System.Text.Encoding.UTF8.GetBytes(withoutFileLists.ToJsonString()), "delivery", NullLogger.Instance)!
            .DocumentationOnly.Should().BeFalse();
    }

    [Fact]
    public async Task A_documentation_only_push_after_a_built_push_is_not_built()
    {
        await ConnectAsync();
        var owner = await SeedUserAsync();
        await GiveTokenAsync(owner);
        var (_, pipelineId) = await SeedSolutionAsync(owner, branch: "main");
        var worker = NewWorker(new ProjectBuildQueue());
        var docs = GitHubWebhookPayloads.Sha(77);

        await worker.RunOneAsync(Push(), CancellationToken.None);
        await worker.RunOneAsync(
            Push(before: GitHubWebhookPayloads.After, after: docs, paths: ["README.md", ".gitignore"], pushedAt: 1_790_000_060),
            CancellationToken.None);

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.Where(b => b.PipelineId == pipelineId).Select(b => b.HeadSha).ToListAsync())
            .Should().Equal([GitHubWebhookPayloads.After], "the documentation change compiles the same apps");
        (await read.OeRepositoryBranchHeads.SingleAsync()).HeadSha
            .Should().Be(docs, "the push is still recorded, so the branch head stays true");
    }

    [Fact]
    public async Task A_documentation_only_push_from_a_commit_no_build_covers_is_built()
    {
        await ConnectAsync();
        var owner = await SeedUserAsync();
        await GiveTokenAsync(owner);
        var (_, pipelineId) = await SeedSolutionAsync(owner, branch: "main");
        var worker = NewWorker(new ProjectBuildQueue());

        // The push that changed code may still be on its way, and would be dropped as
        // older when it arrives; this push's build is the only one that carries it.
        await worker.RunOneAsync(Push(paths: ["README.md"]), CancellationToken.None);

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.Where(b => b.PipelineId == pipelineId).Select(b => b.HeadSha).ToListAsync())
            .Should().Equal(GitHubWebhookPayloads.After);
    }

    [Fact]
    public async Task A_documentation_only_push_after_a_forced_push_is_built()
    {
        await ConnectAsync();
        var owner = await SeedUserAsync();
        await GiveTokenAsync(owner);
        var (_, pipelineId) = await SeedSolutionAsync(owner, branch: "main");
        var worker = NewWorker(new ProjectBuildQueue());
        var docs = GitHubWebhookPayloads.Sha(77);

        await worker.RunOneAsync(Push(forced: true), CancellationToken.None);
        await worker.RunOneAsync(
            Push(before: GitHubWebhookPayloads.After, after: docs, paths: ["README.md"], pushedAt: 1_790_000_060),
            CancellationToken.None);

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.Where(b => b.PipelineId == pipelineId).Select(b => b.HeadSha).ToListAsync())
            .Should().Equal([docs], "the forced push was left to a person, so its code is still unbuilt");
    }

    [Fact]
    public async Task A_documentation_only_push_after_a_failed_build_is_built()
    {
        await ConnectAsync();
        var owner = await SeedUserAsync();
        await GiveTokenAsync(owner);
        var (_, pipelineId) = await SeedSolutionAsync(owner, branch: "main");
        var worker = NewWorker(new ProjectBuildQueue());
        var docs = GitHubWebhookPayloads.Sha(77);

        await worker.RunOneAsync(Push(), CancellationToken.None);
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjectBuilds.Where(b => b.PipelineId == pipelineId)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, ProjectBuildStatus.Failed));
        }
        await worker.RunOneAsync(
            Push(before: GitHubWebhookPayloads.After, after: docs, paths: ["README.md"], pushedAt: 1_790_000_060),
            CancellationToken.None);

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.Where(b => b.PipelineId == pipelineId).OrderBy(b => b.Id).Select(b => b.HeadSha).ToListAsync())
            .Should().Equal(GitHubWebhookPayloads.After, docs);
    }

    [Fact]
    public async Task A_documentation_only_push_from_a_commit_a_manual_build_pinned_is_not_built()
    {
        var owner = await SeedUserAsync();
        var (repositoryId, pipelineId) = await SeedSolutionAsync(owner, branch: "main");
        await using (var ctx = _db.NewContext())
        {
            var build = new OeProjectBuild
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = (await ctx.OePipelines.SingleAsync(p => p.Id == pipelineId)).ProjectId,
                PipelineId = pipelineId, Status = ProjectBuildStatus.Ready, StartedAt = DateTime.UtcNow,
            };
            build.RepoCommits.Add(new OeProjectBuildRepoCommit
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectRepositoryId = repositoryId,
                RepoUrl = "https://github.com/cronus-dk/customer-app.git", RepoDisplayName = "customer-app",
                CommitHash = GitHubWebhookPayloads.Before,
            });
            ctx.OeProjectBuilds.Add(build);
            await ctx.SaveChangesAsync();
        }

        (await ListDueAsync(Push(paths: ["README.md"]))).Should().BeEmpty();
        (await ListDueAsync(Push())).Should().ContainSingle("a push that changed code still builds");
    }

    [Fact]
    public async Task A_redelivered_documentation_only_push_is_still_not_built()
    {
        await ConnectAsync();
        var owner = await SeedUserAsync();
        await GiveTokenAsync(owner);
        var (_, pipelineId) = await SeedSolutionAsync(owner, branch: "main");
        var worker = NewWorker(new ProjectBuildQueue());
        var docs = Push(before: GitHubWebhookPayloads.After, after: GitHubWebhookPayloads.Sha(77), paths: ["README.md"], pushedAt: 1_790_000_060);

        await worker.RunOneAsync(Push(), CancellationToken.None);
        await worker.RunOneAsync(docs, CancellationToken.None);
        await worker.RunOneAsync(docs, CancellationToken.None);

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.CountAsync(b => b.PipelineId == pipelineId)).Should().Be(1);
    }

    // --- Fixture -------------------------------------------------------------

    private static GitHubPushJob Push(
        string branch = "main", bool forced = false, bool deleted = false,
        string before = GitHubWebhookPayloads.Before, string after = GitHubWebhookPayloads.After,
        string[]? paths = null, int commitCount = 1, long pushedAt = 1_790_000_000)
    {
        var json = GitHubWebhookPayloads.Push(branch: branch, forced: forced, deleted: deleted, before: before,
            after: deleted ? GitHubWebhookPayloads.Zero : after, paths: paths, commitCount: commitCount, pushedAt: pushedAt);
        var job = GitHubWebhookEndpoints.TryReadPush(System.Text.Encoding.UTF8.GetBytes(json), "delivery", NullLogger.Instance);
        job.Should().NotBeNull();
        return job!;
    }

    private async Task<List<PushBuildDue>> ListDueAsync(GitHubPushJob push)
    {
        await using var ctx = _db.NewContext();
        return await new PushBuildService(ctx).ListDueAsync(push);
    }

    private async Task<int> SeedUserAsync(string email = "alice@cronus.test", UserRole role = UserRole.Admin)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId, Email = email, DisplayName = email, PasswordHash = "x",
            Role = role, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>A GitHub build token for <paramref name="userId"/>, saved as that person.</summary>
    private async Task GiveTokenAsync(int userId)
    {
        var previous = _db.OrgContext.CurrentUserId;
        _db.OrgContext.CurrentUserId = userId;
        try
        {
            await using var ctx = _db.NewContext();
            await new UserRepositoryTokenService(ctx, _db.OrgContext, NullLogger<UserRepositoryTokenService>.Instance, _db.DataProtectionProvider)
                .SaveTokenAsync(RepositoryProvider.GitHub, "ghp_pasted", clear: false);
        }
        finally
        {
            _db.OrgContext.CurrentUserId = previous;
        }
    }

    /// <summary>A solution tracking the repository the deliveries name, with one pipeline.</summary>
    private async Task<(int RepositoryId, int PipelineId)> SeedSolutionAsync(
        int buildOnPushBy, string? branch, bool buildOnPush = true, int? projectOwner = null, string? blocked = null)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS " + Guid.NewGuid().ToString("N")[..8],
            CreatedByUserId = projectOwner ?? buildOnPushBy,
            Visibility = projectOwner is null ? ProjectVisibility.Public : ProjectVisibility.ReadOnly,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        var repository = new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Url = "https://github.com/cronus-dk/customer-app.git",
            Provider = RepositoryProvider.GitHub,
            DisplayName = "customer-app",
        };
        ctx.OeProjectRepositories.Add(repository);
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Name = "main " + Guid.NewGuid().ToString("N")[..8],
            Branch = branch,
            BuildOnPush = buildOnPush,
            BuildOnPushByUserId = buildOnPush ? buildOnPushBy : null,
            BuildOnPushBlocked = blocked,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return (repository.Id, pipeline.Id);
    }

    private async Task ConnectAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = TestDb.DefaultOrgId,
            GitHubInstallationId = ConnectedInstallation,
            GitHubOrgLogin = "cronus-dk",
            GitHubConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// The worker over a provider shaped like the app's, with no request behind it:
    /// the organisation and the acting person come only from the ambient scope the
    /// worker enters, which is what makes "runs as the person who turned it on" real.
    /// </summary>
    private GitHubPullRequestBuildWorker NewWorker(ProjectBuildQueue builds)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOrganizationContext>(new AmbientOnlyOrganizationContext());
        services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddDataProtection();
        _db.AddStorageServices(services);
        services.AddScoped<OrganizationConfigService>();
        services.AddScoped<SystemSettingsService>();
        _db.AddGitHubServices(services, new FakeGitHubApi());
        services.AddSingleton(builds);
        services.AddScoped<ALDevToolbox.Services.Translation.TranslationMemoryService>();
        services.AddScoped<TranslationImportService>();
        services.AddScoped<CallSiteReferenceEmitter>();
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
            NullLogger<ReleaseManagementService>.Instance));
        services.AddScoped<ProjectBuildImporter>();
        services.AddScoped(sp => new UserRepositoryTokenService(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IOrganizationContext>(),
            NullLogger<UserRepositoryTokenService>.Instance, _db.DataProtectionProvider));
        services.AddScoped<CloneCredentialResolver>();
        services.AddScoped<GitHubBranchActivityService>();
        services.AddScoped<PushBuildService>();
        services.AddScoped<GitHubCheckRunService>();

        return new GitHubPullRequestBuildWorker(
            new GitHubWebhookQueue(), services.BuildServiceProvider(), new MaintenanceModeState(),
            NullLogger<GitHubPullRequestBuildWorker>.Instance,
            new ALDevToolbox.Services.Workers.WorkerHeartbeatRegistry(TimeProvider.System));
    }

    private sealed class AmbientOnlyOrganizationContext : IOrganizationContext
    {
        public int? CurrentOrganizationId => AmbientOrganizationScope.Current?.OrganizationId;
        public int OrganizationIdForFilter => CurrentOrganizationId ?? 0;
        public int? CurrentUserId => AmbientOrganizationScope.Current?.UserId;
        public bool IsSiteAdmin => AmbientOrganizationScope.Current?.IsSiteAdmin ?? false;
        public bool IsSystemOrganization => AmbientOrganizationScope.Current?.IsSystemOrganization ?? false;
    }
}
