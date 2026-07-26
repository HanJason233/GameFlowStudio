using System.Text.Json;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Executors;

public sealed record MouseClickParameters(
    int X,
    int Y,
    MouseButtonType Button,
    int ClickCount,
    int IntervalMs,
    bool IsWindowRelative,
    string WindowHandleVariable,
    int WindowHandleIndex);

public interface IMouseClickCoordinateResolver
{
    (int X, int Y) Resolve(MouseClickParameters parameters, WorkflowExecutionContext context);
}

public sealed class MouseClickCoordinateResolver(IWindowAutomationService windows) : IMouseClickCoordinateResolver
{
    public (int X, int Y) Resolve(MouseClickParameters parameters, WorkflowExecutionContext context)
    {
        if (!parameters.IsWindowRelative) return (parameters.X, parameters.Y);

        var handle = WindowHandleSelector.Select(context, parameters.WindowHandleVariable, parameters.WindowHandleIndex);
        var bounds = windows.GetWindowBounds(handle);
        return WindowCoordinateCalculator.AddOffset(bounds, parameters.X, parameters.Y);
    }
}

internal static class MouseClickParameterReader
{
    public static MouseClickParameters Read(WorkflowNode node)
    {
        var mode = NodeParameterReader.String(node, "coordinateMode", "screen");
        var isWindowRelative = mode.Equals("windowRelative", StringComparison.OrdinalIgnoreCase) ||
                               mode.Equals("relative", StringComparison.OrdinalIgnoreCase);
        if (!isWindowRelative && !mode.Equals("screen", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"不支持的坐标模式：{mode}。");

        var buttonName = NodeParameterReader.String(node, "button", "left");
        if (!Enum.TryParse<MouseButtonType>(buttonName, true, out var button))
            throw new InvalidOperationException($"不支持的鼠标按键：{buttonName}。");

        return new(
            NodeParameterReader.Int(node, "x"),
            NodeParameterReader.Int(node, "y"),
            button,
            NodeParameterReader.Int(node, "clickCount", 1),
            NodeParameterReader.Int(node, "intervalMs", 0),
            isWindowRelative,
            NodeParameterReader.String(node, "windowHandleVariable", "windowHandles"),
            NodeParameterReader.Int(node, "windowHandleIndex", 0));
    }
}

internal static class WindowHandleSelector
{
    public static nint Select(WorkflowExecutionContext context, string variableName, int index)
    {
        if (index < 0) throw new InvalidOperationException("窗口句柄索引不能为负数。");
        var handles = ReadHandles(context, variableName);
        if (index >= handles.Count) throw new InvalidOperationException($"窗口句柄变量 {variableName} 中没有索引为 {index} 的句柄。");
        return handles[index];
    }

    internal static IReadOnlyList<nint> SelectAll(WorkflowExecutionContext context, string variableName) => ReadHandles(context, variableName);

    private static IReadOnlyList<nint> ReadHandles(WorkflowExecutionContext context, string variableName)
    {
        if (!context.Variables.TryGetValue(variableName, out var value) || value is null)
        {
            if (variableName.Equals("windowHandles", StringComparison.Ordinal) && context.WindowHandles.Count > 0)
                return context.WindowHandles;
            throw new InvalidOperationException($"找不到窗口句柄变量：{variableName}。");
        }

        return value switch
        {
            long[] handles => handles.Select(handle => (nint)handle).ToArray(),
            int[] handles => handles.Select(handle => (nint)handle).ToArray(),
            nint[] handles => handles,
            JsonElement element when element.ValueKind == JsonValueKind.Array =>
                element.EnumerateArray().Select(element => (nint)element.GetInt64()).ToArray(),
            long handle => [(nint)handle],
            int handle => [(nint)handle],
            _ => throw new InvalidOperationException($"窗口句柄变量 {variableName} 的值不是有效句柄集合。")
        };
    }
}

public static class WindowCoordinateCalculator
{
    public static (int X, int Y) AddOffset(WindowBounds bounds, int relativeX, int relativeY) =>
        (checked(bounds.Left + relativeX), checked(bounds.Top + relativeY));

    public static (int X, int Y) ToRelative(WindowBounds bounds, int screenX, int screenY) =>
        (checked(screenX - bounds.Left), checked(screenY - bounds.Top));
}
