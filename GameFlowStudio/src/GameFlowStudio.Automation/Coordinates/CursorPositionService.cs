using GameFlowStudio.Automation.Interop;

namespace GameFlowStudio.Automation.Coordinates;

public sealed class CursorPositionService
{
    public (int X, int Y) GetPosition()
    {
        if (!NativeMethods.GetCursorPos(out var point)) throw new InvalidOperationException("无法读取鼠标坐标。");
        return (point.X, point.Y);
    }
}
