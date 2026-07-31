namespace GameFlowStudio.Contracts.Enums;

/// <summary>工作流运行状态。</summary>
public enum WorkflowExecutionState
{
    Idle,
    Running,
    Paused,
    Stopping,
    Completed,
    Failed
}

/// <summary>鼠标按钮。</summary>
public enum MouseButtonType { Left, Right, Middle }

/// <summary>鼠标点击的输入投递方式。</summary>
public enum MouseClickMode
{
    Foreground,
    Background
}

/// <summary>鼠标点击节点的共享安全限制。</summary>
public static class MouseClickLimits
{
    public const int MaximumBackgroundClickCount = 100;
}

/// <summary>项目变量类型。</summary>
public enum VariableType { String, Number, Boolean }

/// <summary>节点日志级别。</summary>
public enum WorkflowLogLevel { Debug, Information, Warning, Error }

/// <summary>用于检测屏幕状态的检测器类型。</summary>
public enum DetectorType
{
    Color,
    Image,
    Ocr,
    AiRecognition
}

/// <summary>脚本匹配多个窗口时的执行顺序。</summary>
public enum WindowExecutionMode
{
    FirstOnly,
    WindowPriority,
    ActionPriority
}

/// <summary>带坐标动作的坐标基准。</summary>
public enum CoordinateMode
{
    FollowScript,
    Absolute,
    Relative
}
