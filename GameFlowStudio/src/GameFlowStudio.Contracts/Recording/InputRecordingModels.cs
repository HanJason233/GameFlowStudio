using GameFlowStudio.Contracts.Enums;

namespace GameFlowStudio.Contracts.Recording;

public static class InputRecordingLimits
{
    public const int MaxActions = 4_000;
}

/// <summary>可转换为现有工作流节点的输入操作类型。</summary>
public enum RecordedInputActionKind
{
    MouseClick,
    MouseDrag,
    MouseScroll,
    KeyPress
}

/// <summary>
/// 一次全局输入操作。时间和坐标均来自录制开始后的单调时钟与虚拟桌面物理像素。
/// </summary>
public sealed record RecordedInputAction
{
    public required RecordedInputActionKind Kind { get; init; }
    public long OffsetMilliseconds { get; init; }
    public int DurationMilliseconds { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int EndX { get; init; }
    public int EndY { get; init; }
    public MouseButtonType Button { get; init; } = MouseButtonType.Left;
    public int ClickCount { get; init; } = 1;
    public int IntervalMilliseconds { get; init; }
    public int Delta { get; init; }
    public string Key { get; init; } = string.Empty;
}

public sealed class RecordedInputActionEventArgs(RecordedInputAction action) : EventArgs
{
    public RecordedInputAction Action { get; } = action;
}
