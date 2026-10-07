using ALDevToolbox.Services.Workers;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The in-use count the compiler and the artifact cache share (#1137): a tidy-up skips
/// whatever a job holds, holds are counted, and a lease lets go exactly once.
/// </summary>
public sealed class InUseLeasesTests
{
    [Fact]
    public void A_key_stays_held_until_every_lease_on_it_is_disposed()
    {
        var leases = new InUseLeases<string>(StringComparer.OrdinalIgnoreCase);
        var first = leases.Hold("30.0", "compiler");
        var second = leases.Hold("30.0", "compiler");

        Held(leases, "30.0").Should().BeTrue();
        Held(leases, "30.0-BETA").Should().BeFalse();
        Held(leases, "30.0".ToUpperInvariant()).Should().BeTrue("the comparer is the caller's");

        first.Dispose();
        first.Dispose(); // Twice is harmless: it lets go of one hold, not two.
        Held(leases, "30.0").Should().BeTrue("the second lease still holds it");

        second.Dispose();
        Held(leases, "30.0").Should().BeFalse();
    }

    [Fact]
    public void TryHold_takes_nothing_once_the_thing_is_gone()
    {
        var leases = new InUseLeases<string>();

        leases.TryHold("set", () => false, "paths").Should().BeNull();
        Held(leases, "set").Should().BeFalse();

        using var lease = leases.TryHold("set", () => true, "paths");
        lease!.Value.Should().Be("paths");
        Held(leases, "set").Should().BeTrue();
    }

    [Fact]
    public void Discard_runs_the_discard_instead_of_the_release_and_only_once()
    {
        var released = 0;
        var discarded = 0;
        var lease = new InUseLease<string>("paths", () => released++, () => discarded++);

        lease.Discard();
        lease.Dispose();
        lease.Discard();

        discarded.Should().Be(1);
        released.Should().Be(0);
    }

    private static bool Held(InUseLeases<string> leases, string key)
    {
        var held = false;
        leases.Locked(isHeld => held = isHeld(key));
        return held;
    }
}
