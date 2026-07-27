using System.Windows;
using GameFlowStudio.Automation.Coordinates;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Desktop.Views;

namespace GameFlowStudio.Desktop.Services;

public sealed record RectanglePickResult(int X, int Y, int Width, int Height);

public interface IRectanglePickerService
{
    Task<RectanglePickResult?> PickAsync(WindowBounds? relativeTo = null);
}

public sealed class RectanglePickerService(CursorPositionService cursor) : IRectanglePickerService
{
    public Task<RectanglePickResult?> PickAsync(WindowBounds? relativeTo = null)
    {
        var main = Application.Current.MainWindow;
        var previous = main.WindowState;
        main.WindowState = WindowState.Minimized;
        RectanglePickResult? result = null;
        try
        {
            var picker = new RectanglePickerWindow(cursor, relativeTo);
            picker.ShowDialog();
            if (picker.SelectedRegion is { } region)
                result = new(region.X, region.Y, region.Width, region.Height);
        }
        finally
        {
            main.WindowState = previous;
            main.Activate();
        }
        return Task.FromResult(result);
    }
}
