namespace GameFlowStudio.Automation.Input;

/// <summary>不依赖系统钩子的窗口录制筛选与坐标换算规则。</summary>
internal static class WindowInputRecordingRules
{
    public static bool IsTargetWindow(
        nint targetWindow,
        nint candidateRoot,
        nint candidateRootOwner) =>
        targetWindow != 0
        && (candidateRoot == targetWindow || candidateRootOwner == targetWindow);

    public static bool ShouldCaptureMouse(
        uint message,
        bool hasActiveMouseButton,
        nint targetWindow,
        nint eventRoot,
        nint eventRootOwner)
    {
        // 已从目标窗口开始的拖动在移出窗口后仍属于同一次操作。
        if (hasActiveMouseButton
            && message is InputRecordingMessages.MouseMove
                or InputRecordingMessages.LeftButtonUp
                or InputRecordingMessages.RightButtonUp
                or InputRecordingMessages.MiddleButtonUp)
        {
            return true;
        }

        return IsTargetWindow(targetWindow, eventRoot, eventRootOwner);
    }

    public static (int X, int Y) ToRelativePoint(
        int screenX,
        int screenY,
        int windowLeft,
        int windowTop) =>
        (screenX - windowLeft, screenY - windowTop);
}
