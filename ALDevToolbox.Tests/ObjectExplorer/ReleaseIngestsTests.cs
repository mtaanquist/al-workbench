using ALDevToolbox.Services.ObjectExplorer.Import;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// What this process is importing, and the gate that keeps whole-release imports from
/// running side by side (#1180). Both are process-wide, so these tests use release ids
/// no database test creates and only ever wait on the gate with a timeout.
/// </summary>
public sealed class ReleaseIngestsTests
{
    [Fact]
    public void A_release_reads_as_importing_until_the_last_of_its_imports_ends()
    {
        const int releaseId = int.MaxValue - 1180;
        var outer = ReleaseIngests.Track(releaseId);
        var inner = ReleaseIngests.Track(releaseId);
        ReleaseIngests.IsRunning(releaseId).Should().BeTrue();

        inner.Dispose();
        inner.Dispose();
        ReleaseIngests.IsRunning(releaseId).Should().BeTrue("disposing one twice must not end the other");

        outer.Dispose();
        ReleaseIngests.IsRunning(releaseId).Should().BeFalse();
    }

    [Fact]
    public async Task Only_one_heavy_import_runs_at_a_time()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var first = await ReleaseIngests.EnterHeavyAsync(timeout.Token);

        ReleaseIngests.TryEnterHeavy().Should().BeNull("another import holds the gate");
        var second = ReleaseIngests.EnterHeavyAsync(timeout.Token);
        await Task.Delay(50);
        second.IsCompleted.Should().BeFalse();

        first.Dispose();
        first.Dispose();
        using (await second.WaitAsync(timeout.Token))
        {
            ReleaseIngests.TryEnterHeavy().Should().BeNull("letting go twice frees the gate once");
        }
    }
}
