using CameoIFV.Core.Interaction;
using Xunit;

namespace CameoIFV.Core.Tests;

public class ConfirmationGateTests
{
    [Fact]
    public void Confirm_WithinWindow_SucceedsAndClears()
    {
        var gate = new ConfirmationGate(TimeSpan.FromSeconds(5));
        var now = DateTimeOffset.Parse("2026-06-05T12:00:00Z");

        gate.Arm("playtest", now);

        Assert.True(gate.Confirm("playtest", now.AddSeconds(4)));
        Assert.False(gate.Confirm("playtest", now.AddSeconds(4)));
    }

    [Fact]
    public void Confirm_AfterExpiry_Fails()
    {
        var gate = new ConfirmationGate(TimeSpan.FromSeconds(5));
        var now = DateTimeOffset.Parse("2026-06-05T12:00:00Z");

        gate.Arm("playtest", now);

        Assert.False(gate.Confirm("playtest", now.AddSeconds(6)));
        Assert.True(gate.IsExpired("playtest", now.AddSeconds(6)));
    }

    [Fact]
    public void NewArm_InvalidatesPriorVersion()
    {
        var gate = new ConfirmationGate(TimeSpan.FromSeconds(5));
        var now = DateTimeOffset.Parse("2026-06-05T12:00:00Z");

        var first = gate.Arm("one", now);
        var second = gate.Arm("two", now.AddSeconds(1));

        Assert.False(gate.IsArmed("one", now.AddSeconds(1), first));
        Assert.True(gate.IsArmed("two", now.AddSeconds(1), second));
    }
}
