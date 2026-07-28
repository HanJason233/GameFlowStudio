using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Workflow.Coordinates;

namespace GameFlowStudio.Workflow.Executors;

public sealed record MouseClickParameters(
    int X,
    int Y,
    MouseButtonType Button,
    int ClickCount,
    int IntervalMs,
    CoordinateMode CoordinateMode);

public interface IMouseCoordinateResolver
{
    (int X, int Y) Resolve(int x, int y, CoordinateMode mode, WorkflowExecutionContext context);
}

public sealed class MouseCoordinateResolver(IWindowAutomationService windows) : IMouseCoordinateResolver
{
    public (int X, int Y) Resolve(int x, int y, CoordinateMode mode, WorkflowExecutionContext context)
    {
        if (!CoordinateModeReader.UsesWindow(mode, context.WindowHandles)) return (x, y);

        var bounds = CoordinateModeReader.GetWindowBounds(context.WindowHandles, windows);
        return WindowCoordinateCalculator.AddOffset(bounds, x, y);
    }
}

internal static class MouseClickParameterReader
{
    public static MouseClickParameters Read(WorkflowNode node)
    {
        var buttonName = NodeParameterReader.String(node, "button", "left");
        if (!Enum.TryParse<MouseButtonType>(buttonName, true, out var button))
            throw new InvalidOperationException($"不支持的鼠标按键：{buttonName}。");

        return new(
            NodeParameterReader.Int(node, "x"),
            NodeParameterReader.Int(node, "y"),
            button,
            NodeParameterReader.Int(node, "clickCount", 1),
            NodeParameterReader.Int(node, "intervalMs", 0),
            CoordinateModeReader.Read(NodeParameterReader.String(node, "coordinateMode", "followScript")));
    }
}

internal static class WindowHandleSelector
{
    public static nint Select(WorkflowExecutionContext context, int index)
    {
        if (index < 0) throw new InvalidOperationException("窗口句柄索引不能为负数。");
        var handles = context.WindowHandles;
        if (index >= handles.Count) throw new InvalidOperationException($"当前窗口作用域中没有索引为 {index} 的窗口句柄。");
        return handles[index];
    }

    internal static IReadOnlyList<nint> SelectAll(WorkflowExecutionContext context) => context.WindowHandles;
}

public static class WindowCoordinateCalculator
{
    public static (int X, int Y) AddOffset(WindowBounds bounds, int relativeX, int relativeY) =>
        (checked(bounds.Left + relativeX), checked(bounds.Top + relativeY));

    public static (int X, int Y) ToRelative(WindowBounds bounds, int screenX, int screenY) =>
        (checked(screenX - bounds.Left), checked(screenY - bounds.Top));
}
