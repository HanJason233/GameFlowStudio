using System.Text.Json;
using System.Text.RegularExpressions;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Workflow.Coordinates;
using GameFlowStudio.Workflow.Executors;

namespace GameFlowStudio.Workflow.Validation;

internal sealed class NodeParameterValidator
{
    public void Validate(WorkflowNode node, WorkflowValidationResult result)
    {
        try { ValidateKnownNode(node); }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or JsonException)
        {
            result.Add("parameters", exception.Message, node.Id);
        }
    }

    private static void ValidateKnownNode(WorkflowNode node)
    {
        switch (node.Type)
        {
            case NodeTypes.WindowScope:
                ValidateWindowScope(node); break;
            case NodeTypes.MouseClick:
                ValidateMouseClick(node); break;
            case NodeTypes.MouseMove:
                CoordinateMode(node); Coordinate(node); NonNegative(node, "durationMs", 0); break;
            case NodeTypes.MouseDrag:
                CoordinateMode(node); Coordinate(node, "startX", "startY"); Coordinate(node, "endX", "endY");
                MouseButton(node); NonNegative(node, "durationMs", 0); break;
            case NodeTypes.MouseScroll:
                CoordinateMode(node); Coordinate(node); if (NodeParameterReader.Int(node, "delta", 120) == 0) throw new InvalidOperationException("滚动量不能为零。"); break;
            case NodeTypes.KeyPress:
                RequiredText(node, "key"); NonNegative(node, "durationMs", 0); Positive(node, "repeatCount", 1); NonNegative(node, "intervalMs", 0); break;
            case NodeTypes.TextInput:
                NodeParameterReader.String(node, "text"); NonNegative(node, "intervalMs", 0); break;
            case NodeTypes.Wait:
                ValidateWait(node); break;
            case NodeTypes.ColorCondition:
            case NodeTypes.ImageCondition:
                CoordinateMode(node); ValidateSingleStateCondition(node); break;
            case NodeTypes.StateCondition:
                _ = StateRuleReader.Read(node); break;
            case NodeTypes.Jump:
                RequiredText(node, "scriptId"); RequiredText(node, "actionId"); break;
            case NodeTypes.Log:
                NodeParameterReader.String(node, "message"); ValidateLogLevel(node); break;
            case NodeTypes.RunProgram:
                RequiredText(node, "path"); NodeParameterReader.Bool(node, "waitForExit", false); break;
            case NodeTypes.CloseProgram:
            case NodeTypes.MinimizeWindow:
                WindowTarget(node); break;
            case NodeTypes.ResizeWindow:
            case NodeTypes.SetWindowResolution:
                WindowTarget(node); Positive(node, "width", 0); Positive(node, "height", 0); break;
            case NodeTypes.ArrangeWindows:
                RequiredText(node, "windowHandleVariable"); Positive(node, "columns", 2); Positive(node, "width", 800); Positive(node, "height", 600);
                NonNegative(node, "gapX", 0); NonNegative(node, "gapY", 0); break;
        }
    }

    private static void ValidateWindowScope(WorkflowNode node)
    {
        var pattern = NodeParameterReader.String(node, "titlePattern");
        RequiredText(node, "titlePattern");
        RequiredText(node, "outputVariable");
        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("窗口标题正则表达式无效。", exception);
        }
    }

    private static void ValidateMouseClick(WorkflowNode node)
    {
        var parameters = MouseClickParameterReader.Read(node);
        Coordinate(node);
        Positive(node, "clickCount", parameters.ClickCount);
        NonNegative(node, "intervalMs", parameters.IntervalMs);
    }

    private static void ValidateWait(WorkflowNode node)
    {
        NonNegative(node, "durationMs", 0);
        if (!NodeParameterReader.Has(node, "randomMinMs") && !NodeParameterReader.Has(node, "randomMaxMs")) return;
        var minimum = NonNegative(node, "randomMinMs", 0);
        var maximum = NonNegative(node, "randomMaxMs", 0);
        if (minimum > maximum) throw new InvalidOperationException("随机等待最小值不能大于最大值。");
    }

    private static void ValidateSingleStateCondition(WorkflowNode node)
    {
        var state = NodeParameterReader.Int(node, "state", 1);
        if (state is not (0 or 1)) throw new InvalidOperationException("检测器期望状态只能是 0 或 1。");
    }

    private static void ValidateLogLevel(WorkflowNode node)
    {
        var level = NodeParameterReader.String(node, "level", "information");
        if (!Enum.TryParse<WorkflowLogLevel>(level, true, out _)) throw new InvalidOperationException($"不支持的日志级别：{level}。");
    }

    private static void WindowTarget(WorkflowNode node)
    {
        RequiredText(node, "windowHandleVariable");
        if (NodeParameterReader.Int(node, "windowHandleIndex", 0) < 0) throw new InvalidOperationException("窗口句柄索引不能为负数。");
    }

    private static void Coordinate(WorkflowNode node) => Coordinate(node, "x", "y");

    private static void Coordinate(WorkflowNode node, string xName, string yName)
    {
        var x = NodeParameterReader.Int(node, xName);
        var y = NodeParameterReader.Int(node, yName);
        if (x is < -100000 or > 100000 || y is < -100000 or > 100000) throw new InvalidOperationException("屏幕坐标超出允许范围。");
    }

    private static void CoordinateMode(WorkflowNode node) =>
        _ = CoordinateModeReader.Read(NodeParameterReader.String(node, "coordinateMode", "followScript"));

    private static void MouseButton(WorkflowNode node)
    {
        var name = NodeParameterReader.String(node, "button", "left");
        if (!Enum.TryParse<Contracts.Enums.MouseButtonType>(name, true, out _)) throw new InvalidOperationException($"不支持的鼠标按键：{name}。");
    }

    private static int NonNegative(WorkflowNode node, string name, int fallback)
    {
        var value = NodeParameterReader.Int(node, name, fallback);
        if (value < 0) throw new InvalidOperationException($"参数 {name} 不能为负数。");
        return value;
    }

    private static void Positive(WorkflowNode node, string name, int fallback)
    {
        if (NodeParameterReader.Int(node, name, fallback) <= 0) throw new InvalidOperationException($"参数 {name} 必须大于零。");
    }

    private static void RequiredText(WorkflowNode node, string name)
    {
        if (string.IsNullOrWhiteSpace(NodeParameterReader.String(node, name))) throw new InvalidOperationException($"参数 {name} 不能为空。");
    }
}
