using GameFlowStudio.Contracts.Enums;

namespace GameFlowStudio.Contracts.Recording;

public static class InputRecordingLimits
{
    public const int MaxActions = 4_000;
}

/// <summary>操作录制使用的坐标和输入来源范围。</summary>
public enum InputRecordingScope
{
    Screen,
    Window
}

/// <summary>启动操作录制时使用的范围选项。</summary>
public sealed record InputRecordingOptions
{
    public InputRecordingScope Scope { get; init; } = InputRecordingScope.Screen;
    public nint TargetWindowHandle { get; init; }
}

/// <summary>可转换为现有工作流节点的输入操作类型。</summary>
public enum RecordedInputActionKind
{
    MouseClick,
    MouseDrag,
    MouseScroll,
    KeyPress,
    TextInput
}

/// <summary>
/// 一次输入操作。时间来自录制开始后的单调时钟；
/// 屏幕模式坐标使用虚拟桌面物理像素，窗口模式坐标使用所选窗口外框的相对物理像素。
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
    public string Text { get; init; } = string.Empty;
}

public sealed class RecordedInputActionEventArgs(RecordedInputAction action) : EventArgs
{
    public RecordedInputAction Action { get; } = action;
}
