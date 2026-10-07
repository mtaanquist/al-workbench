namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// The recommendation shown beside the "Builds that run at once" setting (#1164):
/// what the app container can carry, worked out from the cores and memory the
/// runtime says it has. Advice only; nothing sets the limit from it.
///
/// <para>
/// Each running build keeps about one core busy compiling and wants about a gigabyte
/// of memory, and the rest of the app needs a core of its own. So the rule is one
/// build per gigabyte, one fewer than the cores, never less than one and never more
/// than <see cref="ProjectBuildQueue.MaxConcurrency"/>. See
/// <c>.design/deployment.md</c>, "Resource sizing".
/// </para>
/// </summary>
public static class BuildConcurrencyAdvice
{
    private const long BytesPerGb = 1024L * 1024 * 1024;

    /// <summary>What the container has, and what that suggests.</summary>
    public sealed record Capacity(int Cores, int MemoryGb, int Recommended);

    /// <summary>
    /// max(1, min(cores - 1, floor(memory in GB))), held at
    /// <see cref="ProjectBuildQueue.MaxConcurrency"/>.
    /// </summary>
    public static int Recommend(int cores, long memoryBytes)
    {
        var byMemory = memoryBytes <= 0 ? 0 : memoryBytes / BytesPerGb;
        var fits = Math.Min(cores - 1L, byMemory);
        return (int)Math.Clamp(fits, ProjectBuildQueue.MinConcurrency, ProjectBuildQueue.MaxConcurrency);
    }

    /// <summary>The figures for <paramref name="cores"/> and <paramref name="memoryBytes"/>.</summary>
    public static Capacity For(int cores, long memoryBytes) =>
        new(cores, (int)Math.Max(0, memoryBytes / BytesPerGb), Recommend(cores, memoryBytes));

    /// <summary>
    /// The figures for this process. <see cref="Environment.ProcessorCount"/> follows the
    /// container's CPU limit. Memory is the container's own limit, read from the cgroup
    /// files, because the garbage collector's figure is its heap limit, which .NET sets
    /// to 75% of the container's by default and would make a 4 GB container read as 3.
    /// Outside a container (or with no limit) it is what the runtime says it can use.
    /// </summary>
    public static Capacity ForThisServer() =>
        For(Environment.ProcessorCount, ContainerMemoryBytes() ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    private static long? ContainerMemoryBytes() =>
        ReadLimit("/sys/fs/cgroup/memory.max") ?? ReadLimit("/sys/fs/cgroup/memory/memory.limit_in_bytes");

    /// <summary>
    /// A cgroup memory limit from <paramref name="text"/>: null for <c>max</c> (cgroup v2's
    /// "no limit"), for cgroup v1's near-<see cref="long.MaxValue"/> stand-in, or for
    /// anything unreadable.
    /// </summary>
    internal static long? ParseLimit(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value == "max") return null;
        if (!long.TryParse(value, out var bytes) || bytes <= 0) return null;
        // cgroup v1 writes a page-rounded long.MaxValue when there is no limit.
        return bytes >= long.MaxValue / 2 ? null : bytes;
    }

    private static long? ReadLimit(string path)
    {
        try
        {
            return File.Exists(path) ? ParseLimit(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
