using HelloOtp.Core;
using Xunit;

namespace HelloOtp.Tests;

public class PlacementTests
{
    private static readonly PixelRect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void OpensToTheRightWhenThereIsRoom() =>
        Assert.Equal((158, 100), Placement.BesideIcon(new PixelRect(100, 100, 150, 150), 340, 400, Work));

    [Fact]
    public void FlipsLeftAtTheRightEdge() =>
        Assert.Equal((1512, 100), Placement.BesideIcon(new PixelRect(1860, 100, 1910, 150), 340, 400, Work));

    [Fact]
    public void StaysAboveTheBottomEdge() =>
        Assert.Equal((158, 640), Placement.BesideIcon(new PixelRect(100, 1000, 150, 1040), 340, 400, Work));

    [Fact]
    public void RestoresOnSavedMonitorIncludingNegativeCoordinates()
    {
        var monitors = new[]
        {
            new MonitorArea(@"\\.\DISPLAY1", Work, true),
            new MonitorArea(@"\\.\DISPLAY2", new PixelRect(-1920, 0, 0, 1040), false),
        };
        Assert.Equal((-1820, 50), Placement.RestoreIcon(@"\\.\DISPLAY2", 100, 50, 48, monitors));
    }

    [Fact]
    public void FallsBackToPrimaryWhenMonitorIsGone()
    {
        var monitors = new[] { new MonitorArea(@"\\.\DISPLAY1", Work, true) };
        Assert.Equal((1848, 24), Placement.RestoreIcon(@"\\.\DISPLAY2", 100, 50, 48, monitors));
    }

    [Fact]
    public void ClampsWhenMonitorResolutionShrank()
    {
        var monitors = new[] { new MonitorArea(@"\\.\DISPLAY1", new PixelRect(0, 0, 1280, 720), true) };
        Assert.Equal((1232, 672), Placement.RestoreIcon(@"\\.\DISPLAY1", 1800, 1000, 48, monitors));
    }
}
