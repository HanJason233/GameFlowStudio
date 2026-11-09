using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Desktop.Interaction;
using GameFlowStudio.Desktop.Views;

namespace GameFlowStudio.Desktop.Services;

public sealed record CoordinatePickResult(int X, int Y, string? Color = null);

public interface ICoordinatePickerService
{
    Task<CoordinatePickResult?> PickAsync(WindowBounds? relativeTo = null);
}

public sealed class CoordinatePickerService(
    CursorPositionService cursor,
    IDesktopCaptureService capture) : ICoordinatePickerService
{
    public async Task<CoordinatePickResult?> PickAsync(WindowBounds? relativeTo = null)
    {
        using var session = DesktopPickerSession.Start();
        await session.WaitForDesktopUpdateAsync();
        var picker = new CoordinatePickerWindow(cursor, capture, relativeTo);
        picker.ShowDialog();
        if (picker.SelectedPosition is not { } position) return null;

        await session.WaitForDesktopUpdateAsync();
        string? color = null;
        try
        {
            var frame = await capture.CaptureAsync(
                new CaptureRegion(position.X, position.Y, 1, 1),
                CancellationToken.None);
            if (ScreenPixel.TryRead(frame, 0, 0, out var pixel)) color = pixel.Hex;
        }
        catch
        {
            // 坐标拾取仍然有效，颜色读取失败时保留空值。
        }

        return new CoordinatePickResult(position.X, position.Y, color);
    }
}
