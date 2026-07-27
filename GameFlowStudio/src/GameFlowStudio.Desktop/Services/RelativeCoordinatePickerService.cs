using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Workflow.Executors;

namespace GameFlowStudio.Desktop.Services;

public interface IRelativeCoordinatePickerService
{
    Task<(int X, int Y)?> PickAsync(WindowInfo window);
}

public sealed class RelativeCoordinatePickerService(
    IWindowAutomationService windows,
    ICoordinatePickerService picker) : IRelativeCoordinatePickerService
{
    public async Task<(int X, int Y)?> PickAsync(WindowInfo window)
    {
        var bounds = windows.GetWindowBounds(window.Handle);
        var screenPosition = await picker.PickAsync(bounds).ConfigureAwait(true);
        if (screenPosition is null) return null;
        return WindowCoordinateCalculator.ToRelative(bounds, screenPosition.Value.X, screenPosition.Value.Y);
    }
}
