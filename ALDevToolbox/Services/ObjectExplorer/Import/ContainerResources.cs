namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// What the kernel says about the app container's processor and memory right now, read
/// from its cgroup files (#1169). Each figure is null where the file is missing or
/// unreadable: outside a container, on cgroup v1, or on a kernel without pressure
/// accounting. Where the container's own pressure files are missing, the host-wide ones
/// under <c>/proc/pressure</c> stand in.
/// </summary>
public sealed class ContainerResources
{
    private readonly string _cgroupDirectory;
    private readonly string _procPressureDirectory;

    public ContainerResources() : this("/sys/fs/cgroup", "/proc/pressure") { }

    /// <summary>Reads from other directories; for tests.</summary>
    internal ContainerResources(string cgroupDirectory, string procPressureDirectory)
    {
        _cgroupDirectory = cgroupDirectory;
        _procPressureDirectory = procPressureDirectory;
    }

    /// <summary>One reading, stamped <paramref name="at"/>.</summary>
    public ResourceSample Read(DateTime at)
    {
        var cpuStat = Read(_cgroupDirectory, "cpu.stat");
        return new ResourceSample(
            at,
            CpuWaitMicroseconds: PressureTotal(Read(_cgroupDirectory, "cpu.pressure") ?? Read(_procPressureDirectory, "cpu")),
            MemoryWaitMicroseconds: PressureTotal(Read(_cgroupDirectory, "memory.pressure") ?? Read(_procPressureDirectory, "memory")),
            Periods: Field(cpuStat, "nr_periods"),
            ThrottledPeriods: Field(cpuStat, "nr_throttled"),
            MemoryUsedBytes: Number(Read(_cgroupDirectory, "memory.current")),
            MemoryLimitBytes: BuildConcurrencyAdvice.ParseLimit(Read(_cgroupDirectory, "memory.max")),
            OomKills: Field(Read(_cgroupDirectory, "memory.events"), "oom_kill"));
    }

    /// <summary>
    /// The running total from a pressure file's <c>some</c> line: microseconds in which
    /// at least one task was waiting for the resource.
    /// </summary>
    internal static long? PressureTotal(string? text)
    {
        var line = Lines(text).FirstOrDefault(l => l.StartsWith("some ", StringComparison.Ordinal));
        var total = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(t => t.StartsWith("total=", StringComparison.Ordinal));
        return total is not null && long.TryParse(total["total=".Length..], out var us) ? us : null;
    }

    /// <summary>The number after <paramref name="name"/> in a <c>name value</c> per line file.</summary>
    internal static long? Field(string? text, string name)
    {
        foreach (var line in Lines(text))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == name && long.TryParse(parts[1], out var value)) return value;
        }
        return null;
    }

    private static long? Number(string? text) => long.TryParse(text?.Trim(), out var n) ? n : null;

    private static IEnumerable<string> Lines(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private static string? Read(string directory, string file)
    {
        var path = Path.Combine(directory, file);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>One reading of <see cref="ContainerResources"/>. The totals only mean something as the difference between two readings.</summary>
public sealed record ResourceSample(
    DateTime At,
    long? CpuWaitMicroseconds,
    long? MemoryWaitMicroseconds,
    long? Periods,
    long? ThrottledPeriods,
    long? MemoryUsedBytes,
    long? MemoryLimitBytes,
    long? OomKills);
