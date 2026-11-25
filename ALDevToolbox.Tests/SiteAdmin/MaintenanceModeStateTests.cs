using ALDevToolbox.Services;
using AwesomeAssertions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// <see cref="MaintenanceModeState"/> is a tiny singleton, but the
/// middleware in Program.cs reads it on every request — pin the toggle
/// contract so a regression there (e.g. forgetting to lift the flag on
/// a restore failure) doesn't ship silently.
/// </summary>
public sealed class MaintenanceModeStateTests
{
    [Fact]
    public void Enter_sets_reason_and_marks_active()
    {
        var state = new MaintenanceModeState();
        state.IsActive.Should().BeFalse();

        state.Enter("Restoring backup x");

        state.IsActive.Should().BeTrue();
        state.Reason.Should().Be("Restoring backup x");
        state.StartedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Exit_clears_reason()
    {
        var state = new MaintenanceModeState();
        state.Enter("Restoring");

        state.Exit();

        state.IsActive.Should().BeFalse();
        state.Reason.Should().BeNull();
    }

    [Fact]
    public void A_captured_hold_after_a_failed_restore_comes_back_unchanged()
    {
        var state = new MaintenanceModeState();
        state.HoldAfterFailedRestore("Restoring backup x failed.");
        var held = state.Capture();

        state.Enter("Restoring backup y");
        state.IsHeldAfterFailedRestore.Should().BeFalse();
        state.Restore(held);

        state.IsActive.Should().BeTrue();
        state.IsHeldAfterFailedRestore.Should().BeTrue();
        state.Reason.Should().Be("Restoring backup x failed.");
        state.StartedAtUtc.Should().Be(held.StartedAtUtc);
    }
}
