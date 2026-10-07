using ALDevToolbox.Services.ObjectExplorer.Import;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The recommendation beside "Builds that run at once" (#1164): one build per
/// gigabyte, one fewer than the cores, between 1 and the most the queue allows.
/// </summary>
public sealed class BuildConcurrencyAdviceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(4, 4 * Gb, 3)]      // the shipped compose limits
    [InlineData(8, 4 * Gb, 4)]      // memory is the tighter limit
    [InlineData(4, 16 * Gb, 3)]     // cores are the tighter limit
    [InlineData(1, 8 * Gb, 1)]      // never below one
    [InlineData(2, Gb / 2, 1)]      // under a gigabyte still gets one
    [InlineData(64, 256 * Gb, ProjectBuildQueue.MaxConcurrency)]
    [InlineData(6, 5 * Gb + Gb / 2, 5)] // memory rounds down
    public void Recommends_from_cores_and_memory(int cores, long memoryBytes, int expected) =>
        BuildConcurrencyAdvice.Recommend(cores, memoryBytes).Should().Be(expected);

    [Fact]
    public void Reports_the_figures_it_used()
    {
        var capacity = BuildConcurrencyAdvice.For(cores: 4, memoryBytes: 4 * Gb);

        capacity.Should().Be(new BuildConcurrencyAdvice.Capacity(Cores: 4, MemoryGb: 4, Recommended: 3));
    }

    [Fact]
    public void This_server_has_at_least_one_core_and_a_recommendation_in_range()
    {
        var capacity = BuildConcurrencyAdvice.ForThisServer();

        capacity.Cores.Should().BeGreaterThan(0);
        capacity.Recommended.Should().BeInRange(1, ProjectBuildQueue.MaxConcurrency);
    }

    [Theory]
    [InlineData("4294967296\n", 4294967296L)]
    [InlineData("max\n", null)]
    [InlineData("9223372036854771712", null)] // cgroup v1 with no limit
    [InlineData("", null)]
    [InlineData("lots", null)]
    public void A_container_memory_limit_is_read_from_the_cgroup_file(string text, long? expected) =>
        BuildConcurrencyAdvice.ParseLimit(text).Should().Be(expected);
}
