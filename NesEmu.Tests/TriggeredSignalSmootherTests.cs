using NesEmu.Core;

namespace NesEmu.Tests;

public sealed class TriggeredSignalSmootherTests
{
    [Fact]
    public void TriggerChange_BlendsInsteadOfJumpingImmediately()
    {
        var smoother = new TriggeredSignalSmoother(1_000, TimeSpan.FromMilliseconds(4));
        smoother.Prime(0.0f);

        var first = smoother.Process(1.0f, triggerVersion: 1);
        var second = smoother.Process(1.0f, triggerVersion: 1);
        var third = smoother.Process(1.0f, triggerVersion: 1);
        var fourth = smoother.Process(1.0f, triggerVersion: 1);

        Assert.Equal(0.25f, first, 3);
        Assert.Equal(0.50f, second, 3);
        Assert.Equal(0.75f, third, 3);
        Assert.Equal(1.00f, fourth, 3);
    }

    [Fact]
    public void WithoutTriggerChange_ValuePassesThrough()
    {
        var smoother = new TriggeredSignalSmoother(1_000, TimeSpan.FromMilliseconds(4));
        smoother.Prime(0.0f);

        Assert.Equal(0.5f, smoother.Process(0.5f, triggerVersion: 0), 3);
        Assert.Equal(-0.25f, smoother.Process(-0.25f, triggerVersion: 0), 3);
    }
}
