using Bandichan.Core;

namespace Bandichan.Tests;

public class UnitTest1
{
    [Fact]
    public void AppState_ShouldStartWithIdleValue()
    {
        Assert.Equal(0, (int)AppState.Idle);
    }

    [Fact]
    public void DisplayMonitor_ToString_ShouldContainResolution()
    {
        var monitor = new DisplayMonitor("id1", "Display 1", 0, 0, 1920, 1080);
        Assert.Contains("1920x1080", monitor.ToString());
    }
}
