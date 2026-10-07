using System.Globalization;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Watches the app container while pipeline builds run and warns when they are short of
/// processor or memory (#1169): the sign that "Pipeline builds that run at once" is set
/// higher than the server can carry. Every <see cref="PollInterval"/> it compares two
/// <see cref="ContainerResources"/> readings taken while builds ran, logs a warning
/// naming the running builds and the limit, and keeps the latest for the Builds settings
/// tab. Warnings repeat at most every <see cref="WarningInterval"/>.
///
/// <para>
/// Advice only, like <see cref="BuildConcurrencyAdvice"/>: nothing lowers the limit from
/// it. Processor shortage only counts with two or more builds running, because one build
/// that fills the processors on its own is not helped by running fewer. Where the kernel
/// exposes none of the figures, nothing is ever reported. Opt out with
/// <c>DISABLE_BUILD_RESOURCE_MONITOR=1</c>. See <c>.design/deployment.md</c>,
/// "Resource sizing".
/// </para>
/// </summary>
public sealed class BuildResourceMonitor : PolledScheduler
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(15);

    /// <summary>Waiting for a processor this share of the time, with two or more builds running.</summary>
    internal const double CpuWaitThreshold = 0.8;

    /// <summary>Out of processor time the container is allowed, in this share of the scheduler's periods.</summary>
    internal const double ThrottledThreshold = 0.8;

    /// <summary>Waiting for memory this share of the time.</summary>
    internal const double MemoryWaitThreshold = 0.1;

    /// <summary>Using this share of the container's memory limit.</summary>
    internal const double MemoryFullThreshold = 0.9;

    private readonly ProjectBuildQueue _builds;
    private readonly ContainerResources _resources;
    private readonly BuildResourceState _state;
    private readonly TimeProvider _clock;
    private readonly ILogger<BuildResourceMonitor> _logger;

    private ResourceSample? _previous;

    public BuildResourceMonitor(
        ProjectBuildQueue builds,
        ContainerResources resources,
        BuildResourceState state,
        TimeProvider clock,
        ILogger<BuildResourceMonitor> logger,
        WorkerHeartbeatRegistry heartbeats)
        : base(logger, heartbeats, nameof(BuildResourceMonitor),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromMinutes(1),
            maxIdleSilence: TimeSpan.FromMinutes(3),
            disableEnvVar: "DISABLE_BUILD_RESOURCE_MONITOR")
    {
        _builds = builds;
        _resources = resources;
        _state = state;
        _clock = clock;
        _logger = logger;
    }

    protected override Task TickAsync(CancellationToken ct)
    {
        Check();
        return Task.CompletedTask;
    }

    /// <summary>One poll: a reading, and a warning when it and the last one say builds are starved.</summary>
    internal void Check()
    {
        var running = _builds.RunningCount;
        if (running == 0)
        {
            // Only time spent building counts, so the next build starts a fresh comparison.
            _previous = null;
            return;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var current = _resources.Read(now);
        var previous = _previous;
        _previous = current;
        if (previous is null) return;

        var shortage = Assess(previous, current, running);
        if (shortage is null) return;
        if (_state.Last is { } last && now - last.At < WarningInterval) return;

        var warning = new BuildResourceWarning(now, running, _builds.Limit, shortage);
        _state.Last = warning;
        _logger.LogWarning(
            "Pipeline builds are short of server resources: {Running} running with the limit at {Limit}; {Shortage}. Lower \"Pipeline builds that run at once\" or give the server more.",
            running, _builds.Limit, shortage.Describe());
    }

    /// <summary>
    /// What the change from <paramref name="previous"/> to <paramref name="current"/>
    /// says the builds were short of, or null when they were not.
    /// </summary>
    internal static BuildResourceShortage? Assess(ResourceSample previous, ResourceSample current, int running)
    {
        var elapsedUs = (current.At - previous.At).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        if (elapsedUs <= 0) return null;

        var cpuWait = Share(previous.CpuWaitMicroseconds, current.CpuWaitMicroseconds, elapsedUs);
        var memoryWait = Share(previous.MemoryWaitMicroseconds, current.MemoryWaitMicroseconds, elapsedUs);
        var periods = current.Periods - previous.Periods;
        var throttledPeriods = current.ThrottledPeriods - previous.ThrottledPeriods;
        double? throttled = periods is > 0 && throttledPeriods is >= 0
            ? Math.Min(1, (double)throttledPeriods.Value / periods.Value)
            : null;
        double? memoryUsed = current.MemoryLimitBytes is > 0 && current.MemoryUsedBytes is { } used
            ? (double)used / current.MemoryLimitBytes.Value
            : null;
        var oomKills = Math.Max(0, (current.OomKills - previous.OomKills) ?? 0);

        var shortOfProcessor = running >= 2 && (cpuWait >= CpuWaitThreshold || throttled >= ThrottledThreshold);
        var shortOfMemory = memoryWait >= MemoryWaitThreshold || memoryUsed >= MemoryFullThreshold || oomKills > 0;
        if (!shortOfProcessor && !shortOfMemory) return null;

        return new BuildResourceShortage(
            CpuWait: shortOfProcessor && cpuWait >= CpuWaitThreshold ? cpuWait : null,
            Throttled: shortOfProcessor && throttled >= ThrottledThreshold ? throttled : null,
            MemoryWait: memoryWait >= MemoryWaitThreshold ? memoryWait : null,
            MemoryUsed: memoryUsed >= MemoryFullThreshold ? memoryUsed : null,
            OomKills: oomKills);
    }

    private static double? Share(long? before, long? after, long elapsedUs) =>
        before is { } b && after is { } a && a >= b ? Math.Min(1, (double)(a - b) / elapsedUs) : null;
}

/// <summary>What builds were short of between two readings. Each share is from 0 to 1, null when it was fine.</summary>
public sealed record BuildResourceShortage(
    double? CpuWait, double? Throttled, double? MemoryWait, double? MemoryUsed, long OomKills)
{
    /// <summary>The shortage in words a site admin reads, joined with "and".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (CpuWait is { } cpu) parts.Add($"builds waited for a processor {Percent(cpu)} of the time");
        if (Throttled is { } throttled) parts.Add($"they used up the processor time the server allows {Percent(throttled)} of the time");
        if (MemoryWait is { } memory) parts.Add($"builds waited for memory {Percent(memory)} of the time");
        if (MemoryUsed is { } used) parts.Add($"{Percent(used)} of the server's memory was in use");
        if (OomKills == 1) parts.Add("the server stopped a process that ran out of memory");
        else if (OomKills > 1) parts.Add($"the server stopped {OomKills} processes that ran out of memory");
        return string.Join(" and ", parts);
    }

    private static string Percent(double share) =>
        Math.Round(share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}

/// <summary>One warning: when, how many builds ran, the limit then, and what they were short of.</summary>
public sealed record BuildResourceWarning(DateTime At, int Running, int Limit, BuildResourceShortage Shortage);

/// <summary>
/// The latest <see cref="BuildResourceWarning"/>, for the Builds settings tab. Kept in
/// memory: it says what happened since the app last started, which is what a site admin
/// changing the limit needs.
/// </summary>
public sealed class BuildResourceState
{
    private BuildResourceWarning? _last;

    public BuildResourceWarning? Last
    {
        get => Volatile.Read(ref _last);
        set => Volatile.Write(ref _last, value);
    }
}
