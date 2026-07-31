using GameFlowStudio.Automation.Detectors;
using GameFlowStudio.Contracts.Results;

namespace GameFlowStudio.Workflow.Tests;

public sealed class WindowCaptureServiceTests
{
    [Fact]
    public void CropPreservesRequestedPhysicalOriginAndBgraRows()
    {
        var source = new CapturedFrame(
            -2,
            -1,
            4,
            3,
            Pixels(
                1, 2, 3, 4,
                5, 6, 7, 8,
                9, 10, 11, 12));

        var result = WindowCaptureService.Crop(
            source,
            new CaptureRegion(-1, 0, 2, 2));

        Assert.Equal(-1, result.X);
        Assert.Equal(0, result.Y);
        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Equal(Pixels(6, 7, 10, 11), result.Bgra32);
    }

    [Fact]
    public void CropReturnsWholeFrameWithoutCopying()
    {
        var source = new CapturedFrame(100, 200, 2, 1, Pixels(1, 2));

        var result = WindowCaptureService.Crop(
            source,
            new CaptureRegion(100, 200, 2, 1));

        Assert.Same(source, result);
    }

    [Theory]
    [InlineData(99, 200, 1, 1)]
    [InlineData(100, 199, 1, 1)]
    [InlineData(101, 200, 2, 1)]
    [InlineData(100, 200, 1, 2)]
    public void CropRejectsRegionsThatExtendOutsideTheWindow(
        int x,
        int y,
        int width,
        int height)
    {
        var source = new CapturedFrame(100, 200, 2, 1, Pixels(1, 2));

        var exception = Assert.Throws<InvalidOperationException>(
            () => WindowCaptureService.Crop(
                source,
                new CaptureRegion(x, y, width, height)));

        Assert.Contains("必须完整位于目标窗口内", exception.Message);
    }

    [Fact]
    public void CropRejectsIncompleteSourcePixels()
    {
        var source = new CapturedFrame(0, 0, 2, 1, [1, 2, 3, 4]);

        Assert.Throws<ArgumentException>(
            () => WindowCaptureService.Crop(
                source,
                new CaptureRegion(0, 0, 1, 1)));
    }

    [Fact]
    public void MakeOpaqueNormalizesUndefinedGdiAlpha()
    {
        byte[] pixels = [1, 2, 3, 0, 4, 5, 6, 128];

        WindowCaptureService.MakeOpaque(pixels);

        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, pixels);
    }

    [Fact]
    public void MakeOpaqueRejectsNonBgraBuffer()
    {
        Assert.Throws<ArgumentException>(
            () => WindowCaptureService.MakeOpaque([1, 2, 3]));
    }

    private static byte[] Pixels(params byte[] values)
    {
        var pixels = new byte[values.Length * 4];
        for (var index = 0; index < values.Length; index++)
        {
            var offset = index * 4;
            pixels[offset] = values[index];
            pixels[offset + 1] = (byte)(values[index] + 20);
            pixels[offset + 2] = (byte)(values[index] + 40);
            pixels[offset + 3] = byte.MaxValue;
        }
        return pixels;
    }
}
