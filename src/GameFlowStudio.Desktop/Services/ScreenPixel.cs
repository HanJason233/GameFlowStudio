using GameFlowStudio.Contracts.Results;

namespace GameFlowStudio.Desktop.Services;

internal readonly record struct ScreenPixel(byte Red, byte Green, byte Blue)
{
    public string Hex => $"#{Red:X2}{Green:X2}{Blue:X2}";

    public static bool TryRead(CapturedFrame frame, int x, int y, out ScreenPixel color)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var pixel = frame.Pixel(x, y);
        if (pixel.Length < 3)
        {
            color = default;
            return false;
        }

        color = new ScreenPixel(pixel[2], pixel[1], pixel[0]);
        return true;
    }
}
