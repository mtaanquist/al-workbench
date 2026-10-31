using System.Reflection;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ALDevToolbox.Tests.Startup;

/// <summary>
/// Boots the app and resolves the registrations that have no route of their own,
/// so a service dropped while moving registrations between the Startup/ Add*
/// methods fails here rather than at first use in production. Covers every
/// <see cref="BackgroundService"/> in the app assembly and every MCP tool class.
/// </summary>
public class ServiceRegistrationTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;

    public ServiceRegistrationTests(TestDb db) => _db = db;

    [Fact]
    public void Every_background_service_and_mcp_tool_resolves()
    {
        // Keep the schedulers: this test is the one that proves they are registered.
        using var factory = new EndpointFactory(_db, keepSchedulers: true);
        using var _ = factory.CreateClient();

        var appAssembly = typeof(ALDevToolbox.Services.Generation.GenerationService).Assembly;

        // Hosted services are constructed by the host, so resolving them
        // also proves their whole dependency graph is registered.
        var registered = factory.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .ToHashSet();
        var expected = appAssembly.GetTypes()
            .Where(t => typeof(BackgroundService).IsAssignableFrom(t)
                && !t.IsAbstract
                && !t.IsGenericTypeDefinition)
            .ToList();
        expected.Should().NotBeEmpty("the app registers background workers");
        expected.Except(registered).Should().BeEmpty(
            "every BackgroundService in the app assembly must be registered as a hosted service");

        // MCP tool classes are built per request by the MCP server, so a
        // dependency it can no longer resolve surfaces as a failing tool
        // call in production and nowhere else. Construct each one from a
        // request scope — most are registered in DI, the rest are activated
        // by the SDK, and both paths need the same graph to resolve.
        var tools = appAssembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true }
                && t.GetCustomAttributes(inherit: false)
                    .Any(a => a.GetType().Name == "McpServerToolTypeAttribute"))
            .ToList();
        tools.Should().NotBeEmpty("the MCP server exposes tool classes");

        using var scope = factory.Services.CreateScope();
        var notInContainer = new List<string>();
        foreach (var tool in tools)
        {
            var instance = scope.ServiceProvider.GetService(tool);
            if (instance is null)
            {
                notInContainer.Add(tool.Name);
                instance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, tool);
            }
            instance.Should().NotBeNull($"{tool.Name} and its dependencies must resolve");
        }

        // Every tool class but this one is registered in the container. Keep
        // that true: a tool that quietly falls off the registration list
        // still works today (the SDK activates it) but loses its lifetime.
        notInContainer.Should().BeEquivalentTo(new[] { "BcQualityTools" });

        // The build pipeline is resolved per job inside the import worker, so a
        // dependency missing from its graph (the symbol feed resolver, #901)
        // would only show as a failed build.
        scope.ServiceProvider.GetRequiredService<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectBuildService>()
            .Should().NotBeNull();
    }

    /// <summary>
    /// The restore's guarantees live in the worker bases and the queues. A scheduler
    /// or drain worker that does not get the maintenance flag runs straight through a
    /// restore, and a queue without it runs jobs from before one (#1538). This checks
    /// the instances the real host built, so a new worker, or a registration that
    /// loses the flag, fails here.
    /// </summary>
    [Fact]
    public async Task Every_scheduler_drain_worker_and_restore_aware_queue_gets_the_maintenance_flag()
    {
        using var factory = new EndpointFactory(_db, keepSchedulers: true);
        using var client = factory.CreateClient();
        var maintenance = factory.Services.GetRequiredService<MaintenanceModeState>();

        var workers = factory.Services.GetServices<IHostedService>()
            .Where(s => s is PolledScheduler || IsDrainWorker(s.GetType()))
            .ToList();
        workers.Should().Contain(s => s is PolledScheduler);
        workers.Any(s => IsDrainWorker(s.GetType())).Should().BeTrue();
        workers.Where(s => !ReferenceEquals(BaseMaintenance(s), maintenance))
            .Select(s => s.GetType().Name)
            .Should().BeEmpty("the base class holds the maintenance gate, so DI must hand every worker the flag");

        // The queues drop a job from before a restore only if they read its epoch.
        var imports = factory.Services.GetRequiredService<ReleaseImportQueue>();
        var builds = factory.Services.GetRequiredService<ProjectBuildQueue>();
        var discovery = factory.Services.GetRequiredService<ProjectDiscoveryQueue>();
        var identity = AmbientOrganizationScope.OrganizationIdentity.ForOrganization(TestDb.DefaultOrgId, isSystem: false);
        var job = new ReleaseImportJob(-1, identity, new ReleaseImportSource.Backfill());
        maintenance.Enter("Restoring backup test.dump");
        try
        {
            await imports.EnqueueAsync(job, TestContext.Current.CancellationToken);
            builds.Enqueue(job with { BuildOrder = ProjectBuildOrder.Unordered });
            discovery.TryEnqueue(new ProjectDiscoveryJob(-1, identity)).Should().BeTrue();
            maintenance.AdvanceEpoch();
            imports.Reader.TryRead(out _).Should().BeFalse("the import queue reads the restore epoch");
            builds.Reader.TryRead(out _).Should().BeFalse("the build queue reads the restore epoch");
            discovery.Reader.TryRead(out _).Should().BeFalse("the discovery queue reads the restore epoch");
        }
        finally
        {
            maintenance.Exit();
        }

        static bool IsDrainWorker(Type t)
        {
            for (var b = t.BaseType; b is not null; b = b.BaseType)
                if (b.IsGenericType && b.GetGenericTypeDefinition() == typeof(QueueDrainWorker<>)) return true;
            return false;
        }

        static MaintenanceModeState? BaseMaintenance(IHostedService s) =>
            s is PolledScheduler scheduler
                ? scheduler.Maintenance
                : (MaintenanceModeState?)s.GetType()
                    .GetProperty("Maintenance", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(s);
    }
}
