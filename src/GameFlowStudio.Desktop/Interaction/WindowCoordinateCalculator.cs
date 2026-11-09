using GameFlowStudio.Contracts.Results;

namespace GameFlowStudio.Desktop.Interaction;

public static class WindowCoordinateCalculator
{
    public static (int X, int Y) AddOffset(WindowBounds bounds, int relativeX, int relativeY) =>
        (checked(bounds.Left + relativeX), checked(bounds.Top + relativeY));

    public static (int X, int Y) ToRelative(WindowBounds bounds, int screenX, int screenY) =>
        (checked(screenX - bounds.Left), checked(screenY - bounds.Top));
}
