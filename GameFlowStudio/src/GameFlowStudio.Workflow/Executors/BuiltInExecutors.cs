using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.States;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Workflow.Coordinates;
using GameFlowStudio.Workflow.Synchronization;

namespace GameFlowStudio.Workflow.Executors;

public abstract class NodeExecutorBase(string nodeType) : INodeExecutor
{
    public string NodeType { get; } = nodeType;
    public abstract Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken);
}

public sealed class StartNodeExecutor() : NodeExecutorBase(NodeTypes.Start)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(NodeExecutionResult.Ok());
}
public sealed class EndNodeExecutor() : NodeExecutorBase(NodeTypes.End)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(NodeExecutionResult.Ok());
}

public sealed class WindowScopeNodeExecutor(IWindowScopeService scopes) : NodeExecutorBase(NodeTypes.WindowScope)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handles = scopes.ResolveWindowHandles(NodeParameterReader.String(node, "titlePattern"));
        var outputVariable = NodeParameterReader.String(node, "outputVariable", "windowHandles");
        context.WindowHandles = handles;
        context.Variables[outputVariable] = handles.Select(handle => handle.ToInt64()).ToArray();
        ActionExecutionLogging.Write(context, node, $"窗口作用域匹配到 {handles.Count} 个窗口，结果句柄写入变量「{outputVariable}」。");
        return Task.FromResult(NodeExecutionResult.Ok());
    }
}

public sealed class WaitNodeExecutor() : NodeExecutorBase(NodeTypes.Wait)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var duration = NodeParameterReader.Int(node, "durationMs", 0);
        if (NodeParameterReader.Has(node, "randomMinMs") && NodeParameterReader.Has(node, "randomMaxMs"))
            duration = Random.Shared.Next(NodeParameterReader.Int(node, "randomMinMs"), NodeParameterReader.Int(node, "randomMaxMs") + 1);
        await PausableDelay.WaitAsync(duration, context.WaitIfPausedAsync, cancellationToken).ConfigureAwait(false);
        ActionExecutionLogging.Write(context, node, $"等待 {duration} ms。");
        return NodeExecutionResult.Ok();
    }
}

public sealed class MouseMoveNodeExecutor(IInputAutomationService input, IMouseCoordinateResolver coordinates) : NodeExecutorBase(NodeTypes.MouseMove)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var point = coordinates.Resolve(NodeParameterReader.Int(node, "x"), NodeParameterReader.Int(node, "y"),
            CoordinateModeReader.Read(NodeParameterReader.String(node, "coordinateMode", "followScript")), context);
        var duration = NodeParameterReader.Int(node, "durationMs", 0);
        await input.MoveMouseAsync(point.X, point.Y, duration, cancellationToken);
        ActionExecutionLogging.Write(context, node, $"鼠标移动到屏幕坐标 ({point.X},{point.Y})，持续 {duration} ms。");
        return NodeExecutionResult.Ok();
    }
}
public sealed class MouseDragNodeExecutor(IInputAutomationService input, IMouseCoordinateResolver coordinates) : NodeExecutorBase(NodeTypes.MouseDrag)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var mode = CoordinateModeReader.Read(NodeParameterReader.String(node, "coordinateMode", "followScript"));
        var start = coordinates.Resolve(NodeParameterReader.Int(node, "startX"), NodeParameterReader.Int(node, "startY"), mode, context);
        var end = coordinates.Resolve(NodeParameterReader.Int(node, "endX"), NodeParameterReader.Int(node, "endY"), mode, context);
        var button = MouseActionParameterReader.Button(node);
        var duration = NodeParameterReader.Int(node, "durationMs", 500);
        await input.DragMouseAsync(
            start.X, start.Y, end.X, end.Y,
            button, duration, cancellationToken);
        ActionExecutionLogging.Write(context, node, $"鼠标从屏幕坐标 ({start.X},{start.Y}) 拖动到 ({end.X},{end.Y})，按键 {button}，持续 {duration} ms。");
        return NodeExecutionResult.Ok();
    }
}

public sealed class MouseScrollNodeExecutor(IInputAutomationService input, IMouseCoordinateResolver coordinates) : NodeExecutorBase(NodeTypes.MouseScroll)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var point = coordinates.Resolve(NodeParameterReader.Int(node, "x"), NodeParameterReader.Int(node, "y"),
            CoordinateModeReader.Read(NodeParameterReader.String(node, "coordinateMode", "followScript")), context);
        var delta = NodeParameterReader.Int(node, "delta", 120);
        await input.ScrollMouseAsync(point.X, point.Y, delta, cancellationToken);
        ActionExecutionLogging.Write(context, node, $"在屏幕坐标 ({point.X},{point.Y}) 滚动 {delta}。");
        return NodeExecutionResult.Ok();
    }
}

internal static class MouseActionParameterReader
{
    public static MouseButtonType Button(WorkflowNode node)
    {
        var name = NodeParameterReader.String(node, "button", "left");
        if (Enum.TryParse<MouseButtonType>(name, true, out var button)) return button;
        throw new InvalidOperationException($"不支持的鼠标按键：{name}。");
    }
}

public sealed class MouseClickNodeExecutor(IInputAutomationService input, IMouseCoordinateResolver coordinates) : NodeExecutorBase(NodeTypes.MouseClick)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var parameters = MouseClickParameterReader.Read(node);
        var absoluteCoordinates = coordinates.Resolve(parameters.X, parameters.Y, parameters.CoordinateMode, context);
        await input.ClickAsync(absoluteCoordinates.X, absoluteCoordinates.Y, parameters.Button, parameters.ClickCount, parameters.IntervalMs, cancellationToken);
        ActionExecutionLogging.Write(context, node, $"点击屏幕坐标 ({absoluteCoordinates.X},{absoluteCoordinates.Y})，按键 {parameters.Button}，次数 {parameters.ClickCount}。");
        return NodeExecutionResult.Ok();
    }
}

public sealed class KeyPressNodeExecutor(IInputAutomationService input) : NodeExecutorBase(NodeTypes.KeyPress)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var key = NodeParameterReader.String(node, "key");
        var duration = NodeParameterReader.Int(node, "durationMs", 50);
        var repeatCount = NodeParameterReader.Int(node, "repeatCount", 1);
        var interval = NodeParameterReader.Int(node, "intervalMs", 0);
        await input.PressKeyAsync(key, duration, repeatCount, interval, cancellationToken);
        ActionExecutionLogging.Write(context, node, $"按键 {key}，重复 {repeatCount} 次，按下 {duration} ms，间隔 {interval} ms。");
        return NodeExecutionResult.Ok();
    }
}

public sealed class TextInputNodeExecutor(IInputAutomationService input) : NodeExecutorBase(NodeTypes.TextInput)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var text = NodeParameterReader.String(node, "text");
        var interval = NodeParameterReader.Int(node, "intervalMs", 0);
        await input.TypeTextAsync(text, interval, cancellationToken);
        ActionExecutionLogging.Write(context, node, $"输入文本长度 {text.Length}，字符间隔 {interval} ms。");
        return NodeExecutionResult.Ok();
    }
}

/// <summary>跳转到项目中指定脚本的指定动作。</summary>
public sealed class JumpNodeExecutor() : NodeExecutorBase(NodeTypes.Jump)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var scriptId = NodeParameterReader.String(node, "scriptId");
        var actionId = NodeParameterReader.String(node, "actionId");
        return Task.FromResult(NodeExecutionResult.Jump(scriptId, actionId));
    }
}

/// <summary>单一底层检测判断节点，检测内容直接来自节点参数。</summary>
public sealed class SingleConditionNodeExecutor(string nodeType, DetectorType expectedType, IDetectorEvaluator evaluator) : NodeExecutorBase(nodeType)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var detector = new DetectorDefinition
        {
            Id = node.Id,
            Name = node.Data.DisplayName,
            Type = expectedType,
            Parameters = node.Data.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
        };
        var evaluation = await evaluator.EvaluateAsync(detector, context.WindowHandles, cancellationToken).ConfigureAwait(false);
        var detail = evaluation.Message ?? "无详细结果";
        ActionExecutionLogging.Write(context, node, $"{detail}；条件结果：{ActionExecutionLogging.Outcome(evaluation.Supported && evaluation.IsOn)}。");
        return NodeExecutionResult.Ok(evaluation.Supported && evaluation.IsOn ? "true" : "false");
    }
}

