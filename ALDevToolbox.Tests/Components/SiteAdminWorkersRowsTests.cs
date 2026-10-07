using ALDevToolbox.Components.Pages.SiteAdmin;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.Workers;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// All 16 build workers are always running so a new build limit applies without a
/// restart (#1164). The workers page folds the idle ones into one row so they do not
/// bury the rest, and keeps a row of its own for any build worker that is busy or stuck.
/// </summary>
public sealed class SiteAdminWorkersRowsTests
{
    private static readonly TimeSpan BuildCeiling = TimeSpan.FromMinutes(90);

    private static WorkerHeartbeatRegistry WithBuildWorkers()
    {
        var registry = new WorkerHeartbeatRegistry();
        registry.Register("BackupScheduler", TimeSpan.FromHours(2), TimeSpan.FromMinutes(5));
        for (var slot = 1; slot <= ProjectBuildQueue.MaxConcurrency; slot++)
            registry.Register($"ProjectBuildWorker {slot}", BuildCeiling, maxIdleSilence: null);
        return registry;
    }

    [Fact]
    public void Idle_build_workers_share_one_healthy_row()
    {
        var registry = WithBuildWorkers();
        // Long after startup: an idle queue worker never beats again, and must not read as stalled.
        var now = DateTime.UtcNow.AddDays(3);

        var rows = SiteAdminWorkers.Rows(registry.All(), now, buildLimit: 2);

        rows.Select(r => r.Name).Should().BeEquivalentTo("BackupScheduler", "ProjectBuildWorker x16");
        var builders = rows.Single(r => r.Name.StartsWith("ProjectBuildWorker"));
        builders.Healthy.Should().BeTrue();
        builders.ActiveSinceUtc.Should().BeNull();
        builders.Plain.Should().Be("Waiting for builds (16 ready, up to 2 run at once)");
    }

    [Fact]
    public void A_busy_build_worker_keeps_its_own_row()
    {
        var registry = WithBuildWorkers();
        registry.Register("ProjectBuildWorker 3", BuildCeiling, null).BeginActive();

        var rows = SiteAdminWorkers.Rows(registry.All(), DateTime.UtcNow, buildLimit: 2);

        rows.Select(r => r.Name).Should().Contain(["ProjectBuildWorker 3", "ProjectBuildWorker x15"]);
        rows.Single(r => r.Name == "ProjectBuildWorker 3").Plain.Should().Be("Running a build");
    }

    [Fact]
    public void A_stuck_build_worker_keeps_its_own_unhealthy_row()
    {
        var registry = WithBuildWorkers();
        registry.Register("ProjectBuildWorker 7", BuildCeiling, null).BeginActive();

        var rows = SiteAdminWorkers.Rows(registry.All(), DateTime.UtcNow.AddHours(3), buildLimit: 2);

        rows.Single(r => r.Name == "ProjectBuildWorker 7").Healthy.Should().BeFalse();
        rows.Single(r => r.Name == "ProjectBuildWorker x15").Healthy.Should().BeTrue();
    }

    [Fact]
    public void Idle_deployment_workers_share_one_row_and_a_busy_one_keeps_its_own()
    {
        var registry = new WorkerHeartbeatRegistry();
        for (var slot = 1; slot <= DeliveryWorker.Lanes; slot++)
            registry.Register($"DeliveryWorker {slot}", TimeSpan.FromMinutes(30), maxIdleSilence: null);
        registry.Register("DeliveryWorker 2", TimeSpan.FromMinutes(30), null).BeginActive();

        var rows = SiteAdminWorkers.Rows(registry.All(), DateTime.UtcNow, buildLimit: 2);

        rows.Select(r => r.Name).Should().BeEquivalentTo("DeliveryWorker 2", $"DeliveryWorker x{DeliveryWorker.Lanes - 1}");
        rows.Single(r => r.Name == "DeliveryWorker 2").Plain.Should().Be("Deploying to Business Central");
        rows.Single(r => r.Name.EndsWith($"x{DeliveryWorker.Lanes - 1}")).Plain
            .Should().Be($"Waiting for deployments ({DeliveryWorker.Lanes - 1} ready)");
    }
}
