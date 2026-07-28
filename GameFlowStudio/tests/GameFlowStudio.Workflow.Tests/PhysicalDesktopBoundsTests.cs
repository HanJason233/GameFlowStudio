using GameFlowStudio.Automation.Coordinates;
using GameFlowStudio.Automation.Detectors;
using GameFlowStudio.Contracts.Results;

namespace GameFlowStudio.Workflow.Tests;

public sealed class PhysicalDesktopBoundsTests
{
    [Fact]
    public void UnionPreservesNegativeOriginsAndMonitorGaps()
    {
        CaptureRegion[] monitors =
        [
            new(-1920, 0, 1920, 1080),
            new(0, -200, 2560, 1440),
            new(2560, 100, 1600, 900)
        ];

        var bounds = PhysicalDesktopBounds.Union(monitors);

        Assert.Equal(new CaptureRegion(-1920, -200, 6080, 1440), bounds);
    }

    [Fact]
    public void UnionIgnoresInvalidMonitorRectangles()
    {
        CaptureRegion[] monitors =
        [
            new(0, 0, 0, 1080),
            new(100, 200, 800, 600)
        ];

        var bounds = PhysicalDesktopBounds.Union(monitors);

        Assert.Equal(new CaptureRegion(100, 200, 800, 600), bounds);
    }

    [Fact]
    public void UnionRejectsMissingPhysicalDisplays()
    {
        Assert.Throws<InvalidOperationException>(() => PhysicalDesktopBounds.Union([]));
    }

    [Fact]
    public void UnionRejectsVirtualDesktopLargerThanSupportedCoordinates()
    {
        CaptureRegion[] monitors =
        [
            new(int.MinValue, 0, int.MaxValue, 1),
            new(int.MaxValue, 0, 1, 1)
        ];

        Assert.Throws<InvalidOperationException>(() => PhysicalDesktopBounds.Union(monitors));
    }

    [Fact]
    public void CaptureBufferLengthUsesBgra32PhysicalPixels()
    {
        Assert.Equal(13 * 13 * 4, DesktopCaptureService.RequiredBufferLength(new CaptureRegion(-6, -6, 13, 13)));
    }

    [Fact]
    public void CaptureBufferLengthRejectsExcessiveAllocationsBeforeCallingGdi()
    {
        Assert.Throws<InvalidOperationException>(
            () => DesktopCaptureService.RequiredBufferLength(new CaptureRegion(0, 0, 20_000, 20_000)));
    }
}
