using LogiBuddy.Core.Tracking;
using Xunit;

namespace LogiBuddy.Core.Tests.Tracking;

public class SuspendableKeyStateSourceTests
{
    private const int Vk = 0x52; // 'R'

    [Fact]
    public void IsKeyDown_DelegatesToInner_WhenNotSuspended()
    {
        var inner = new FakeKeyStateSource();
        var gate = new SuspendableKeyStateSource(inner);

        inner.SetDown(Vk, true);

        Assert.True(gate.IsKeyDown(Vk));
    }

    [Fact]
    public void IsKeyDown_ReturnsFalse_WhenSuspended_EvenIfInnerIsDown()
    {
        var inner = new FakeKeyStateSource();
        var gate = new SuspendableKeyStateSource(inner);

        inner.SetDown(Vk, true);
        gate.Suspended = true;

        Assert.False(gate.IsKeyDown(Vk));
    }

    [Fact]
    public void IsKeyDown_ResumesDelegating_AfterSuspendedClears()
    {
        var inner = new FakeKeyStateSource();
        var gate = new SuspendableKeyStateSource(inner);

        inner.SetDown(Vk, true);
        gate.Suspended = true;
        gate.Suspended = false;

        Assert.True(gate.IsKeyDown(Vk));
    }
}
