using ALDevToolbox.Services.ObjectExplorer.Import;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// What this process is importing, and the gate that keeps whole-release imports from
/// running side by side (#1180).
/// </summary>
public sealed class ReleaseIngestsTests
{
    [Fact]
    public void A_release_reads_as_importing_until_the_last_of_its_imports_ends()
    {
        var ingests = new ReleaseIngests();
        var outer = ingests.Track(7);
        var inner = ingests.Track(7);
        ingests.IsRunning(7).Should().BeTrue();

        inner.Dispose();
        inner.Dispose();
        ingests.IsRunning(7).Should().BeTrue("disposing one twice must not end the other");

        outer.Dispose();
        ingests.IsRunning(7).Should().BeFalse();
    }

    [Fact]
    public async Task Only_one_heavy_import_runs_at_a_time()
    {
        var ingests = new ReleaseIngests();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var first = await ingests.EnterHeavyAsync(timeout.Token);

        ingests.TryEnterHeavy().Should().BeNull("another import holds the gate");
        var second = ingests.EnterHeavyAsync(timeout.Token);
        await Task.Delay(50);
        second.IsCompleted.Should().BeFalse();

        first.Dispose();
        first.Dispose();
        using (await second.WaitAsync(timeout.Token))
        {
            ingests.TryEnterHeavy().Should().BeNull("letting go twice frees the gate once");
        }
        ingests.TryEnterHeavy().Should().NotBeNull();
    }
}
