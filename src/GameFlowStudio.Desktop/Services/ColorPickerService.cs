using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Desktop.Interaction;
using GameFlowStudio.Desktop.Views;

namespace GameFlowStudio.Desktop.Services;

public interface IColorPickerService
{
    Task<CoordinatePickResult?> PickAsync(WindowBounds? relativeTo = null);
}

public sealed class ColorPickerService(
    CursorPositionService cursor,
    IDesktopCaptureService capture) : IColorPickerService
{
    public async Task<CoordinatePickResult?> PickAsync(WindowBounds? relativeTo = null)
    {
        using var session = DesktopPickerSession.Start();
        await session.WaitForDesktopUpdateAsync();
        var picker = new ColorPickerWindow(cursor, capture, relativeTo);
        picker.ShowDialog();
        var result = picker.SelectedResult;
        if (result is { } selected)
        {
            await session.WaitForDesktopUpdateAsync();
            try
            {
                var frame = await capture.CaptureAsync(
                    new CaptureRegion(selected.X, selected.Y, 1, 1),
                    CancellationToken.None);
                if (ScreenPixel.TryRead(frame, 0, 0, out var pixel))
                    result = selected with { Color = pixel.Hex };
            }
            catch
            {
                // 精确重采样失败时保留预览帧颜色，坐标拾取仍然有效。
            }
        }

        return result;
    }
}
