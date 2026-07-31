using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Workflow.Coordinates;

public static class CoordinateModeReader
{
    public static CoordinateMode Read(string value) => value.Trim().ToLowerInvariant() switch
    {
        "followscript" => CoordinateMode.FollowScript,
        "absolute" => CoordinateMode.Absolute,
        "relative" => CoordinateMode.Relative,
        _ => throw new InvalidOperationException($"不支持的坐标模式：{value}。")
    };

    public static bool UsesWindow(CoordinateMode mode, IReadOnlyList<nint> handles) =>
        mode == CoordinateMode.Relative || mode == CoordinateMode.FollowScript && handles.Count > 0;

    public static WindowBounds GetWindowBounds(IReadOnlyList<nint> handles, IWindowAutomationService windows)
    {
        if (handles.Count != 1) throw new InvalidOperationException("相对坐标需要当前脚本只有一个目标窗口。");
        return windows.GetWindowBounds(handles[0]);
    }
}
