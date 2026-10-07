using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The restart half of <see cref="DeliveryScheduler"/> (#1114, #1179): a deployment left
/// in progress by the previous process is failed once per organisation, judged against
/// the time this process started, and an organisation whose check threw is tried again
/// on the next sweep rather than left with deployments stuck in progress.
/// </summary>
public sealed class DeliverySchedulerTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(Started);
    private int _failNextResolves;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_first_sweep_fails_a_deployment_claimed_before_the_process_started_and_leaves_one_claimed_since()
    {
        var seed = await SeedAsync();
        var orphan = await AddInProgressAsync(seed, claimedAt: Started.UtcDateTime.AddMinutes(-3));
        var running = await AddInProgressAsync(seed, claimedAt: Started.UtcDateTime.AddSeconds(10));
        await using var provider = BuildProvider();
        var scheduler = NewScheduler(provider);
        // The worker is already draining when the first sweep comes round (#1114).
        _clock.Advance(TimeSpan.FromSeconds(30));

        await scheduler.SweepAsync(CancellationToken.None);

        (await StatusAsync(orphan)).Should().Be(ProjectDeliveryStatus.Failed);
        (await StatusAsync(running)).Should().Be(ProjectDeliveryStatus.Installing);
    }

    [Fact]
    public async Task An_organisation_whose_check_threw_is_checked_again_on_the_next_sweep_and_only_until_it_succeeds()
    {
        var seed = await SeedAsync();
        var orphan = await AddInProgressAsync(seed, claimedAt: Started.UtcDateTime.AddMinutes(-3));
        await using var provider = BuildProvider();
        var scheduler = NewScheduler(provider);

        _failNextResolves = 1;
        await scheduler.SweepAsync(CancellationToken.None);
        (await StatusAsync(orphan)).Should().Be(ProjectDeliveryStatus.Installing, "the check for this organisation threw");

        await scheduler.SweepAsync(CancellationToken.None);
        (await StatusAsync(orphan)).Should().Be(ProjectDeliveryStatus.Failed, "the next sweep tries again");

        // Done once it got through: the check is a restart's, not every sweep's.
        var later = await AddInProgressAsync(seed, claimedAt: Started.UtcDateTime.AddMinutes(-2));
        await scheduler.SweepAsync(CancellationToken.None);
        (await StatusAsync(later)).Should().Be(ProjectDeliveryStatus.Installing);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private DeliveryScheduler NewScheduler(IServiceProvider provider) =>
        new(provider, _clock, NullLogger<DeliveryScheduler>.Instance, new WorkerHeartbeatRegistry());

    /// <summary>
    /// A container shaped like the app's own: the organisation in scope comes from
    /// <see cref="HttpOrganizationContext"/>, which with no request falls back to the
    /// ambient scope the scheduler enters for each organisation.
    /// </summary>
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddScoped<IOrganizationContext, HttpOrganizationContext>();
        services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        services.AddSingleton<TimeProvider>(_clock);
        TestDb.AddToolServices(services);
        services.AddScoped<ProjectAccess>();
        // Resolving the delivery service is how a test makes one organisation's check throw.
        services.AddScoped<IDeliveryTokenSource>(_ => _failNextResolves-- > 0
            ? throw new InvalidOperationException("The database went away for a moment.")
            : new NoTokens());
        services.AddSingleton<IBcAppManagementClient>(new UnreachableAppManagementClient());
        services.AddSingleton<IBcAdminClient>(new UnreachableAdminClient());
        services.AddSingleton(new DeliveryQueue());
        services.AddSingleton(new BcPanelCache(TimeProvider.System));
        services.AddScoped<DeliveryService>();
        services.AddSingleton(_db.NewContextFactory());
        services.AddSingleton<IEmailService>(new CapturingEmailService());
        services.AddSingleton<EmailRenderer>();
        services.AddSingleton(new PublicOrigin(null));
        services.AddScoped<NotificationPreferenceService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<DeploymentNotifier>();
        return services.BuildServiceProvider();
    }

    private sealed record Seed(int ProjectId, int ReleasePipelineId, int BuildId);

    private async Task<Seed> SeedAsync()
    {
        var now = Started.UtcDateTime;
        await using var ctx = _db.NewContext();
        var project = new OeProject { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Coffee", CreatedAt = now, UpdatedAt = now };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        var pipeline = new OePipeline { OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Build", CreatedAt = now, UpdatedAt = now };
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production", FetchedAt = now,
        };
        ctx.OePipelines.Add(pipeline);
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();

        var releasePipeline = new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "CRONUS Coffee → Production",
            BuildPipelineId = pipeline.Id, ProjectEnvironmentId = env.Id,
            DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
            CreatedAt = now, UpdatedAt = now,
        };
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, PipelineId = pipeline.Id,
            Status = ProjectBuildStatus.Ready, StartedAt = now,
        };
        ctx.OeReleasePipelines.Add(releasePipeline);
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        return new Seed(project.Id, releasePipeline.Id, build.Id);
    }

    private async Task<int> AddInProgressAsync(Seed seed, DateTime claimedAt)
    {
        await using var ctx = _db.NewContext();
        var delivery = new OeProjectDelivery
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId, ReleasePipelineId = seed.ReleasePipelineId,
            ProjectBuildId = seed.BuildId, EnvironmentName = "Production",
            DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
            ScheduledFor = claimedAt, ClaimedAt = claimedAt, Status = ProjectDeliveryStatus.Installing,
            CreatedAt = claimedAt, UpdatedAt = claimedAt,
        };
        ctx.OeProjectDeliveries.Add(delivery);
        await ctx.SaveChangesAsync();
        return delivery.Id;
    }

    private async Task<string> StatusAsync(int deliveryId)
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeProjectDeliveries.Where(d => d.Id == deliveryId).Select(d => d.Status).SingleAsync();
    }

    /// <summary>Nothing here deploys: a token is never asked for.</summary>
    private sealed class NoTokens : IDeliveryTokenSource
    {
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, bool forceRefresh, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
