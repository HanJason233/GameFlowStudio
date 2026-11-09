using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Desktop.Interaction;

namespace GameFlowStudio.Desktop.Services;

public interface IRelativeCoordinatePickerService
{
    Task<CoordinatePickResult?> PickAsync(WindowInfo window);
}

public sealed class RelativeCoordinatePickerService(
    IWindowPickerService windows,
    ICoordinatePickerService picker) : IRelativeCoordinatePickerService
{
    public async Task<CoordinatePickResult?> PickAsync(WindowInfo window)
    {
        var bounds = windows.GetWindowBounds(window.Handle);
        var screenPosition = await picker.PickAsync(bounds).ConfigureAwait(true);
        if (screenPosition is null) return null;
        var relative = WindowCoordinateCalculator.ToRelative(bounds, screenPosition.X, screenPosition.Y);
        return new CoordinatePickResult(relative.X, relative.Y, screenPosition.Color);
    }
}
