using System.Net;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Translation;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Tests.Auth;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The nightly preview check (#994): which pipelines' checks are due
/// (<see cref="PreviewCheckService.ListDueAsync"/>), and the sweep that starts them
/// as the person who turned the check on (<see cref="PreviewCheckScheduler"/>).
/// Microsoft's artifact indexes are faked; nothing is cloned or compiled here.
/// See <c>.design/object-explorer-project-builds.md</c>, "The nightly preview check".
/// </summary>
public sealed class PreviewCheckTests : IDisposable
{
    private const string Released = "29.0.1.2";
    private const string NextMinor = "29.1.3.4";
    private const string NextMajor = "30.0.7.8";

    private static readonly DateTime Tonight = new(2026, 10, 2, 1, 15, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();
    private readonly ArtifactCdn _cdn = new();

    public void Dispose() => _db.Dispose();

    // ── Which checks are due ────────────────────────────────────────────

    [Fact]
    public async Task A_pipeline_without_the_check_is_never_due()
    {
        var owner = await SeedUserAsync();
        var (_, pipelineId) = await SeedPipelineAsync(owner, previewCheck: false);

        var due = await ListDueAsync();

        due.Should().NotContain(d => d.PipelineId == pipelineId);
    }

    [Fact]
    public async Task A_first_check_is_due_against_both_upcoming_versions_as_its_owner()
    {
        var owner = await SeedUserAsync();
        var (_, pipelineId) = await SeedPipelineAsync(owner);

        var due = await ListDueAsync();

        due.Should().BeEquivalentTo(new[]
        {
            new PreviewCheckDue(pipelineId, owner, ProjectBuildTarget.NextMinor, null),
            new PreviewCheckDue(pipelineId, owner, ProjectBuildTarget.NextMajor, null),
        });
    }

    [Fact]
    public async Task A_check_that_already_ran_tonight_or_is_still_running_is_not_due_again()
    {
        var owner = await SeedUserAsync();
        var (projectId, pipelineId) = await SeedPipelineAsync(owner);
        await SeedCheckAsync(projectId, pipelineId, ProjectBuildTarget.NextMinor, Tonight.AddMinutes(-10), NextMinor);
        await SeedCheckAsync(projectId, pipelineId, ProjectBuildTarget.NextMajor, Tonight.AddDays(-1), NextMajor,
            status: ProjectBuildStatus.Building);

        var due = await ListDueAsync();

        due.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_changed_since_the_last_check_means_it_is_not_due()
    {
        var owner = await SeedUserAsync();
        var (projectId, pipelineId) = await SeedPipelineAsync(owner);
        await SeedBuildAsync(projectId, pipelineId, Tonight.AddDays(-3));
        await SeedCheckAsync(projectId, pipelineId, ProjectBuildTarget.NextMinor, Tonight.AddDays(-1), NextMinor);
        await SeedCheckAsync(projectId, pipelineId, ProjectBuildTarget.NextMajor, Tonight.AddDays(-1), NextMajor);

        var due = await ListDueAsync();

        due.Should().BeEmpty("Microsoft has published nothing new and the pipeline has not built since");
    }

    [Fact]
    public async Task A_new_preview_from_Microsoft_makes_that_check_due()
    {
        var owner = await SeedUserAsync();
        var (projectId, pipelineId) = await SeedPipelineAsync(owner);
        await SeedCheckAsync(projectId, pipelineId, ProjectBuildTarget.NextMinor, Tonight.AddDays(-1), NextMinor);
        await SeedCheckAsync(projectId, pipelineId, ProjectBuildTarget.NextMajor, Tonight.AddDays(-1), "30.0.5.5");

        var due = await ListDueAsync();

        due.Select(d => d.BcTarget).Should().Equal(ProjectBuildTarget.NextMajor);
    }

    [Fact]
    public async Task A_build_of_the_pipeline_since_the_last_check_makes_both_due()
    {
        var owner = await SeedUserAsync();
        var (projectId, pipelineId) = await SeedPipelineAsync(owner);
        await SeedCheckAsync(projectId, pipelineId, ProjectBuildTarget.NextMinor, Tonight.AddDays(-1), NextMinor);
        await SeedCheckAsync(projectId, pipelineId, ProjectBuildTarget.NextMajor, Tonight.AddDays(-1), NextMajor);
        await SeedBuildAsync(projectId, pipelineId, Tonight.AddHours(-8));

        var due = await ListDueAsync();

        due.Select(d => d.BcTarget).Should().Equal(ProjectBuildTarget.NextMinor, ProjectBuildTarget.NextMajor);
    }

    [Fact]
    public async Task A_check_runs_at_least_weekly_even_when_nothing_it_can_see_changed()
    {
        var owner = await SeedUserAsync();
        var (projectId, pipelineId) = await SeedPipelineAsync(owner);
        await SeedCheckAsync(projectId, pipelineId, ProjectBuildTarget.NextMinor, Tonight.AddDays(-8), NextMinor);
        await SeedCheckAsync(projectId, pipelineId, ProjectBuildTarget.NextMajor, Tonight.AddDays(-2), NextMajor);

        var due = await ListDueAsync();

        due.Select(d => d.BcTarget).Should().Equal(ProjectBuildTarget.NextMinor);
    }

    [Fact]
    public async Task No_preview_from_Microsoft_yet_means_nothing_to_check()
    {
        var owner = await SeedUserAsync();
        await SeedPipelineAsync(owner);
        _cdn.Insider = [];

        var due = await ListDueAsync();

        due.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_check_whose_owner_is_gone_or_disabled_is_paused(bool deleted)
    {
        var owner = await SeedUserAsync();
        var (_, pipelineId) = await SeedPipelineAsync(owner);
        await using (var ctx = _db.NewContext())
        {
            if (deleted)
            {
                await ctx.Users.Where(u => u.Id == owner).ExecuteDeleteAsync();
            }
            else
            {
                await ctx.Users.Where(u => u.Id == owner).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, UserStatus.Disabled));
            }
        }

        var due = await ListDueAsync();

        due.Should().ContainSingle().Which.Should().Be(
            new PreviewCheckDue(pipelineId, null, null, AutomatedBuilds.NoOwnerMessage));
    }

    [Fact]
    public async Task A_solution_without_a_country_is_paused_with_the_reason()
    {
        var owner = await SeedUserAsync();
        var (_, pipelineId) = await SeedPipelineAsync(owner, country: null);

        var due = await ListDueAsync();

        var paused = due.Should().ContainSingle().Subject;
        paused.PipelineId.Should().Be(pipelineId);
        paused.BcTarget.Should().BeNull();
        paused.Blocked.Should().Contain("country");
    }

    [Fact]
    public async Task SetBlockedAsync_records_and_clears_the_reason()
    {
        var owner = await SeedUserAsync();
        var (_, pipelineId) = await SeedPipelineAsync(owner);
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).SetBlockedAsync(pipelineId, AutomatedBuilds.NoAccessMessage);
        }
        await using (var read = _db.NewContext())
        {
            (await read.OePipelines.SingleAsync(p => p.Id == pipelineId)).PreviewCheckBlocked.Should().Be(AutomatedBuilds.NoAccessMessage);
        }

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).SetBlockedAsync(pipelineId, null);
        }
        await using (var read = _db.NewContext())
        {
            (await read.OePipelines.SingleAsync(p => p.Id == pipelineId)).PreviewCheckBlocked.Should().BeNull();
        }
    }

    // ── The nightly sweep ───────────────────────────────────────────────

    [Fact]
    public void The_sweep_runs_once_in_its_hour()
    {
        var today = DateOnly.FromDateTime(Tonight);
        PreviewCheckScheduler.IsDue(Tonight, null).Should().BeTrue();
        PreviewCheckScheduler.IsDue(Tonight, today).Should().BeFalse("it already ran tonight");
        PreviewCheckScheduler.IsDue(Tonight.AddHours(1), null).Should().BeFalse("it is past the hour");
        PreviewCheckScheduler.IsDue(Tonight.AddDays(1), today).Should().BeTrue();
    }

    [Fact]
    public async Task The_sweep_starts_both_builds_as_the_person_who_turned_the_check_on()
    {
        var owner = await SeedUserAsync();
        var (_, pipelineId) = await SeedPipelineAsync(owner, projectOwner: owner);

        var queued = await NewScheduler().SweepAsync(CancellationToken.None);

        queued.Should().Be(2);
        await using var read = _db.NewContext();
        var builds = await read.OeProjectBuilds
            .Where(b => b.PipelineId == pipelineId)
            .OrderBy(b => b.BcTarget)
            .ToListAsync();
        builds.Select(b => b.BcTarget).Should().Equal(ProjectBuildTarget.NextMajor, ProjectBuildTarget.NextMinor);
        builds.Should().OnlyContain(b => b.Trigger == ProjectBuildTrigger.PreviewCheck
                                         && b.StartedByUserId == owner
                                         && b.Status == ProjectBuildStatus.Queued);
        (await read.OePipelines.SingleAsync(p => p.Id == pipelineId))
            .PreviewCheckBlocked.Should().BeNull();

        // A second sweep the same night finds both already started.
        (await NewScheduler().SweepAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task The_sweep_pauses_a_check_whose_owner_can_no_longer_manage_the_solution()
    {
        var solutionOwner = await SeedUserAsync("owner@cronus.test");
        var former = await SeedUserAsync("former@cronus.test", UserRole.User);
        var (_, pipelineId) = await SeedPipelineAsync(former, projectOwner: solutionOwner, visibility: ProjectVisibility.ReadOnly);

        var queued = await NewScheduler().SweepAsync(CancellationToken.None);

        queued.Should().Be(0);
        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.AnyAsync(b => b.PipelineId == pipelineId))
            .Should().BeFalse();
        (await read.OePipelines.SingleAsync(p => p.Id == pipelineId))
            .PreviewCheckBlocked.Should().Be(AutomatedBuilds.NoAccessMessage);
    }

    [Fact]
    public async Task The_sweep_lifts_a_pause_whose_cause_has_gone_even_with_nothing_to_build()
    {
        var owner = await SeedUserAsync();
        var (projectId, pipelineId) = await SeedPipelineAsync(owner, projectOwner: owner);
        await SeedCheckAsync(projectId, pipelineId, ProjectBuildTarget.NextMinor, Tonight.AddDays(-1), NextMinor);
        await SeedCheckAsync(projectId, pipelineId, ProjectBuildTarget.NextMajor, Tonight.AddDays(-1), NextMajor);
        await using (var ctx = _db.NewContext())
        {
            await ctx.OePipelines.Where(p => p.Id == pipelineId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.PreviewCheckBlocked, AutomatedBuilds.NoOwnerMessage));
        }

        var queued = await NewScheduler().SweepAsync(CancellationToken.None);

        queued.Should().Be(0, "nothing changed since last night's check");
        await using var read = _db.NewContext();
        (await read.OePipelines.SingleAsync(p => p.Id == pipelineId)).PreviewCheckBlocked.Should().BeNull();
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private async Task<List<PreviewCheckDue>> ListDueAsync()
    {
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using var ctx = _db.NewContext();
        // Leave out the "can run" marker each healthy pipeline also returns; the sweep
        // test below covers what it is for.
        return (await NewService(ctx).ListDueAsync(Tonight))
            .Where(d => d.BcTarget is not null || d.Blocked is not null)
            .ToList();
    }

    private PreviewCheckService NewService(AppDbContext ctx) =>
        new(ctx, new BcArtifactService(_cdn, ctx, _db.OrgContext, NullLogger<BcArtifactService>.Instance),
            NullLogger<PreviewCheckService>.Instance);

    /// <summary>The scheduler over a service provider shaped like the app's, with no request behind it.</summary>
    private PreviewCheckScheduler NewScheduler()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOrganizationContext>(new AmbientOnlyOrganizationContext());
        services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _db.AddStorageServices(services);
        services.AddSingleton<IHttpClientFactory>(_cdn);
        services.AddSingleton(new ReleaseImportQueue());
        services.AddScoped<ProjectAccess>();
        services.AddScoped<TranslationMemoryService>();
        services.AddScoped<TranslationImportService>();
        services.AddScoped<CallSiteReferenceEmitter>();
        services.AddScoped<ReleaseImportService>();
        services.AddScoped<PersistedImportJobs>();
        services.AddScoped<BcArtifactService>();
        services.AddScoped<PreviewCheckService>();
        services.AddScoped<ProjectBuildImporter>();
        // The importer's credential check, which a preview check never consults.
        services.AddScoped<CloneCredentialResolver>(_ => null!);

        return new PreviewCheckScheduler(
            services.BuildServiceProvider(),
            new FakeTimeProvider(new DateTimeOffset(Tonight)),
            NullLogger<PreviewCheckScheduler>.Instance,
            new WorkerHeartbeatRegistry());
    }

    private sealed class AmbientOnlyOrganizationContext : IOrganizationContext
    {
        public int? CurrentOrganizationId => AmbientOrganizationScope.Current?.OrganizationId;
        public int OrganizationIdForFilter => CurrentOrganizationId ?? 0;
        public int? CurrentUserId => AmbientOrganizationScope.Current?.UserId;
        public bool IsSiteAdmin => AmbientOrganizationScope.Current?.IsSiteAdmin ?? false;
        public bool IsSystemOrganization => AmbientOrganizationScope.Current?.IsSystemOrganization ?? false;
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

    private async Task<(int ProjectId, int PipelineId)> SeedPipelineAsync(
        int checkOwner, bool previewCheck = true, string? country = "dk", int? projectOwner = null,
        ProjectVisibility visibility = ProjectVisibility.Public)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS " + Guid.NewGuid().ToString("N"),
            DefaultArtifactCountry = country,
            CreatedByUserId = projectOwner,
            Visibility = visibility,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        ctx.OeProjectRepositories.Add(new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Provider = RepositoryProvider.GitHub,
            Url = "https://github.com/cronus/core", DisplayName = "core",
        });
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Name = "Production",
            PreviewCheck = previewCheck,
            PreviewCheckByUserId = previewCheck ? checkOwner : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return (project.Id, pipeline.Id);
    }

    private async Task SeedBuildAsync(int projectId, int pipelineId, DateTime startedAt)
    {
        await using var ctx = _db.NewContext();
        ctx.OeProjectBuilds.Add(new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipelineId,
            Status = ProjectBuildStatus.Ready, StartedAt = startedAt, FinishedAt = startedAt.AddMinutes(3),
        });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedCheckAsync(int projectId, int pipelineId, string target, DateTime startedAt, string version,
        string status = ProjectBuildStatus.Ready)
    {
        await using var ctx = _db.NewContext();
        ctx.OeProjectBuilds.Add(new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipelineId,
            Status = status, BcTarget = target, Trigger = ProjectBuildTrigger.PreviewCheck, BcArtifactVersion = version,
            StartedAt = startedAt, FinishedAt = status == ProjectBuildStatus.Ready ? startedAt.AddMinutes(3) : null,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>Microsoft's two artifact indexes for <c>dk</c>: one shipped version, and the insider previews.</summary>
    private sealed class ArtifactCdn : HttpMessageHandler, IHttpClientFactory
    {
        public string[] Insider { get; set; } = [NextMinor, NextMajor];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            string[]? versions = uri.Host == BcArtifactIndex.CdnHost ? [Released]
                : uri.Host == BcArtifactIndex.InsiderCdnHost ? Insider
                : null;
            if (versions is null || !uri.AbsolutePath.EndsWith("/indexes/dk.json", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(versions.Select(v => new { Version = v }))),
            });
        }
    }
}
