using System.Windows;
using GameFlowStudio.Automation.Coordinates;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
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
    public Task<CoordinatePickResult?> PickAsync(WindowBounds? relativeTo = null)
    {
        var main = Application.Current.MainWindow;
        var previous = main.WindowState;
        main.WindowState = WindowState.Minimized;
        CoordinatePickResult? result = null;
        try
        {
            var picker = new ColorPickerWindow(cursor, capture, relativeTo);
            picker.ShowDialog();
            result = picker.SelectedResult;
            if (result is { Color: null } selected)
            {
                try
                {
                    var frame = capture.CaptureAsync(new CaptureRegion(selected.X, selected.Y, 1, 1), CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var pixel = frame.Pixel(0, 0);
                    if (pixel.Length >= 3)
                        result = selected with { Color = $"#{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}" };
                }
                catch
                {
                    // 坐标拾取仍然有效，像素读取失败时保留空颜色。
                }
            }
        }
        finally
        {
            main.WindowState = previous;
            main.Activate();
        }
        return Task.FromResult(result);
    }
}
