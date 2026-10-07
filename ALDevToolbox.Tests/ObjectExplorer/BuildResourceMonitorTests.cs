using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Auth;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Running builds short of processor or memory are reported (#1169): read from the
/// container's cgroup files, judged between two readings taken while builds ran, and
/// warned about at most every fifteen minutes.
/// </summary>
public sealed class BuildResourceMonitorTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AmbientOrganizationScope.OrganizationIdentity Identity =
        new(OrganizationId: 1, UserId: null, IsSiteAdmin: false, IsSystemOrganization: false);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aldt-resources-" + Guid.NewGuid().ToString("N"));
    private readonly string _cgroup;
    private readonly string _proc;

    public BuildResourceMonitorTests()
    {
        _cgroup = Path.Combine(_root, "cgroup");
        _proc = Path.Combine(_root, "proc");
        Directory.CreateDirectory(_cgroup);
        Directory.CreateDirectory(_proc);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static ResourceSample Sample(
        int seconds, long? cpuWait = 0, long? memoryWait = 0, long? periods = 0, long? throttled = 0,
        long? used = 1, long? limit = 10, long? oom = 0) =>
        new(T0.AddSeconds(seconds), cpuWait, memoryWait, periods, throttled, used, limit, oom);

    private const long Second = 1_000_000;

    [Fact]
    public void Builds_with_room_to_spare_are_not_reported() =>
        BuildResourceMonitor.Assess(Sample(0), Sample(30, cpuWait: 10 * Second, memoryWait: 1 * Second, periods: 300, throttled: 30, used: 5), running: 3)
            .Should().BeNull();

    [Fact]
    public void Several_builds_waiting_for_a_processor_most_of_the_time_are_reported()
    {
        var shortage = BuildResourceMonitor.Assess(Sample(0), Sample(30, cpuWait: 27 * Second), running: 3);

        shortage.Should().NotBeNull();
        shortage!.Describe().Should().Be("builds waited for a processor 90% of the time");
    }

    [Fact]
    public void One_build_that_fills_the_processors_on_its_own_is_not_reported() =>
        BuildResourceMonitor.Assess(Sample(0), Sample(30, cpuWait: 30 * Second, periods: 300, throttled: 300), running: 1)
            .Should().BeNull("running fewer builds would not help it");

    [Fact]
    public void Running_out_of_the_processor_time_the_server_allows_is_reported() =>
        BuildResourceMonitor.Assess(Sample(0), Sample(30, periods: 300, throttled: 270), running: 2)!
            .Describe().Should().Be("they used up the processor time the server allows 90% of the time");

    [Fact]
    public void Memory_shortage_is_reported_even_for_one_build()
    {
        var shortage = BuildResourceMonitor.Assess(
            Sample(0, oom: 2), Sample(30, memoryWait: 6 * Second, used: 95, limit: 100, oom: 3), running: 1);

        shortage!.Describe().Should().Be(
            "builds waited for memory 20% of the time and 95% of the server's memory was in use and the server stopped a process that ran out of memory");
    }

    [Fact]
    public void Figures_the_kernel_does_not_report_are_never_a_shortage() =>
        BuildResourceMonitor.Assess(
                Sample(0, cpuWait: null, memoryWait: null, periods: null, throttled: null, used: null, limit: null, oom: null),
                Sample(30, cpuWait: null, memoryWait: null, periods: null, throttled: null, used: null, limit: null, oom: null),
                running: 4)
            .Should().BeNull();

    [Fact]
    public void The_readings_come_from_the_container_files()
    {
        File.WriteAllText(Path.Combine(_cgroup, "cpu.pressure"), "some avg10=1.00 avg60=2.00 avg300=3.00 total=12345\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=99\n");
        File.WriteAllText(Path.Combine(_cgroup, "memory.pressure"), "some avg10=0.00 avg60=0.00 avg300=0.00 total=678\n");
        File.WriteAllText(Path.Combine(_cgroup, "cpu.stat"), "usage_usec 1\nnr_periods 40\nnr_throttled 7\nthrottled_usec 9\n");
        File.WriteAllText(Path.Combine(_cgroup, "memory.current"), "2048\n");
        File.WriteAllText(Path.Combine(_cgroup, "memory.max"), "4096\n");
        File.WriteAllText(Path.Combine(_cgroup, "memory.events"), "low 0\nhigh 0\nmax 2\noom 1\noom_kill 1\n");

        var sample = new ContainerResources(_cgroup, _proc).Read(T0);

        sample.Should().Be(new ResourceSample(T0, 12345, 678, 40, 7, 2048, 4096, 1));
    }

    [Fact]
    public void Without_the_containers_pressure_files_the_host_wide_ones_stand_in()
    {
        File.WriteAllText(Path.Combine(_proc, "cpu"), "some avg10=0.05 avg60=3.11 avg300=10.84 total=1420689821\n");

        var sample = new ContainerResources(_cgroup, _proc).Read(T0);

        sample.CpuWaitMicroseconds.Should().Be(1420689821);
        sample.MemoryWaitMicroseconds.Should().BeNull();
        sample.MemoryLimitBytes.Should().BeNull();
    }

    private (BuildResourceMonitor Monitor, BuildResourceState State, FakeTimeProvider Clock, ListLogger Log) NewMonitor(ProjectBuildQueue queue)
    {
        var state = new BuildResourceState();
        var clock = new FakeTimeProvider(new DateTimeOffset(T0));
        var log = new ListLogger();
        var monitor = new BuildResourceMonitor(
            queue, new ContainerResources(_cgroup, _proc), state, clock, log, new WorkerHeartbeatRegistry());
        return (monitor, state, clock, log);
    }

    private void WriteCpuWait(long totalUs) =>
        File.WriteAllText(Path.Combine(_cgroup, "cpu.pressure"), $"some avg10=0 avg60=0 avg300=0 total={totalUs}\n");

    private static ProjectBuildQueue RunningQueue(int running)
    {
        var queue = new ProjectBuildQueue(defaultLimit: 4);
        for (var i = 1; i <= running; i++)
        {
            queue.Enqueue(new ReleaseImportJob(i, Identity, new ReleaseImportSource.ProjectBuild(i),
                BuildOrder: ProjectBuildOrder.For(i, ProjectBuildTarget.Current, ProjectBuildTrigger.Manual)));
            queue.Reader.TryRead(out _).Should().BeTrue();
        }
        return queue;
    }

    [Fact]
    public void A_starved_poll_warns_once_and_keeps_the_warning_for_the_settings_tab()
    {
        var (monitor, state, clock, log) = NewMonitor(RunningQueue(3));

        WriteCpuWait(0);
        monitor.Check();
        clock.Advance(TimeSpan.FromSeconds(30));
        WriteCpuWait(28 * Second);
        monitor.Check();
        clock.Advance(TimeSpan.FromSeconds(30));
        WriteCpuWait(56 * Second);
        monitor.Check();

        log.Warnings.Should().ContainSingle()
            .Which.Should().Contain("3 running with the limit at 4").And.Contain("waited for a processor 93% of the time");
        state.Last.Should().BeEquivalentTo(new { At = T0.AddSeconds(30), Running = 3, Limit = 4 });
    }

    [Fact]
    public void After_fifteen_minutes_it_warns_again()
    {
        var (monitor, _, clock, log) = NewMonitor(RunningQueue(2));
        long total = 0;
        WriteCpuWait(total);
        monitor.Check();
        for (var poll = 0; poll < 31; poll++)
        {
            clock.Advance(BuildResourceMonitor.PollInterval);
            total += 29 * Second;
            WriteCpuWait(total);
            monitor.Check();
        }

        log.Warnings.Should().HaveCount(2);
    }

    [Fact]
    public void Time_with_no_build_running_is_not_counted()
    {
        var queue = RunningQueue(0);
        var (monitor, state, clock, _) = NewMonitor(queue);

        WriteCpuWait(0);
        monitor.Check();
        clock.Advance(TimeSpan.FromSeconds(30));
        WriteCpuWait(30 * Second);
        monitor.Check();

        state.Last.Should().BeNull();
    }

    [Fact]
    public void A_limit_above_the_recommendation_is_logged()
    {
        var log = new ListLogger();
        var capacity = BuildConcurrencyAdvice.For(cores: 4, memoryBytes: 8L * 1024 * 1024 * 1024);

        BuildConcurrencyAdvice.WarnIfAboveRecommendation(3, capacity, log).Should().BeFalse();
        BuildConcurrencyAdvice.WarnIfAboveRecommendation(5, capacity, log).Should().BeTrue();

        log.Warnings.Should().ContainSingle().Which.Should().Contain("is 5, above the 3 recommended");
    }

    private sealed class ListLogger : ILogger<BuildResourceMonitor>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
