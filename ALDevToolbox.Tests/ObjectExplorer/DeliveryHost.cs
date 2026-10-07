using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The delivery background work as the app wires it, for tests that drive
/// <see cref="DeliveryScheduler"/> or <see cref="DeliveryWorker"/> rather than the service:
/// the organisation in scope comes from <see cref="HttpOrganizationContext"/>, which with no
/// request falls back to the ambient scope each of them enters, and the deployment notifier
/// is the real one. Business Central is unreachable; the token source is the test's.
/// </summary>
internal static class DeliveryHost
{
    public static ServiceProvider Build(TestDb db, TimeProvider clock, Func<IDeliveryTokenSource> tokens, DeliveryQueue? queue = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddScoped<IOrganizationContext, HttpOrganizationContext>();
        services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(db.ConnectionString).AddInterceptors(db.CommandTracker));
        services.AddSingleton(clock);
        TestDb.AddToolServices(services);
        services.AddScoped<ProjectAccess>();
        services.AddScoped(_ => tokens());
        services.AddSingleton<IBcAppManagementClient>(new UnreachableAppManagementClient());
        services.AddSingleton<IBcAdminClient>(new UnreachableAdminClient());
        services.AddSingleton(queue ?? new DeliveryQueue());
        services.AddSingleton(new BcPanelCache(TimeProvider.System));
        services.AddScoped<DeliveryService>();
        services.AddSingleton(db.NewContextFactory());
        services.AddSingleton<IEmailService>(new CapturingEmailService());
        services.AddSingleton<EmailRenderer>();
        services.AddSingleton(new PublicOrigin(null));
        services.AddScoped<NotificationPreferenceService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<DeploymentNotifier>();
        return services.BuildServiceProvider();
    }

    public sealed record Seed(int ProjectId, int ReleasePipelineId, int BuildId, int PipelineCreatorId);

    /// <summary>A solution with one Production environment, a deployment pipeline to it and a ready build.</summary>
    public static async Task<Seed> SeedAsync(TestDb db, DateTime now)
    {
        await using var ctx = db.NewContext();
        var creator = new User
        {
            OrganizationId = TestDb.DefaultOrgId, Email = $"u{Guid.NewGuid():N}@cronus.example", DisplayName = "Alex Hansen",
            PasswordHash = "x", Role = UserRole.User, Status = UserStatus.Active, CreatedAt = now,
        };
        var project = new OeProject { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Coffee", CreatedAt = now, UpdatedAt = now };
        ctx.Users.Add(creator);
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
            BuildPipelineId = pipeline.Id, ProjectEnvironmentId = env.Id, CreatedByUserId = creator.Id,
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
        return new Seed(project.Id, releasePipeline.Id, build.Id, creator.Id);
    }

    /// <summary>A deployment of <paramref name="seed"/>'s build in <paramref name="status"/>, with one app still to send.</summary>
    public static async Task<int> AddDeliveryAsync(TestDb db, Seed seed, string status, DateTime at, DateTime? claimedAt = null)
    {
        await using var ctx = db.NewContext();
        var delivery = new OeProjectDelivery
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId, ReleasePipelineId = seed.ReleasePipelineId,
            ProjectBuildId = seed.BuildId, EnvironmentName = "Production",
            DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
            ScheduledFor = at, ClaimedAt = claimedAt, Status = status, CreatedAt = at, UpdatedAt = at,
        };
        delivery.Results.Add(new OeProjectDeliveryResult
        {
            OrganizationId = TestDb.DefaultOrgId, Ordering = 0, AppName = "CRONUS Coffee", AppVersion = "1.4.0.0",
            Status = ProjectDeliveryResultStatus.Pending, CreatedAt = at, UpdatedAt = at,
        });
        ctx.OeProjectDeliveries.Add(delivery);
        await ctx.SaveChangesAsync();
        return delivery.Id;
    }

    public static async Task<string> StatusAsync(TestDb db, int deliveryId)
    {
        await using var ctx = db.NewContext();
        return await ctx.OeProjectDeliveries.Where(d => d.Id == deliveryId).Select(d => d.Status).SingleAsync();
    }
}
