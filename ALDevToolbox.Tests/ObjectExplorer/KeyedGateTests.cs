using ALDevToolbox.Services.Workers;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>One holder per key at a time; other keys never wait on it (#1137).</summary>
public sealed class KeyedGateTests
{
    [Fact]
    public async Task A_second_holder_of_the_same_key_waits_and_another_key_does_not()
    {
        var gate = new KeyedGate<int>();
        var first = await gate.EnterAsync(1);

        var sameKey = gate.EnterAsync(1);
        var otherKey = gate.EnterAsync(2);

        (await otherKey.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        sameKey.IsCompleted.Should().BeFalse();

        first.Dispose();
        first.Dispose(); // Twice is harmless: it lets in one waiter, not two.
        (await sameKey.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }
}
