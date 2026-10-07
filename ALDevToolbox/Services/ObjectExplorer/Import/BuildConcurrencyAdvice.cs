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
    /// container's CPU limit, and the garbage collector's available memory follows its
    /// memory limit, so both describe the container rather than the machine under it.
    /// </summary>
    public static Capacity ForThisServer() =>
        For(Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
}
