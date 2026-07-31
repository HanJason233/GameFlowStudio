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

public sealed class WindowScopeNodeExecutor(
    IWindowScopeService scopes,
    IWindowAutomationService? windows = null) : NodeExecutorBase(NodeTypes.WindowScope)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<nint> handles = scopes.ResolveWindowHandles(NodeParameterReader.String(node, "titlePattern"));
        // 已绑定到单个窗口的执行轨道只能继续缩小到该窗口，不能重新引入脚本匹配模式已排除的目标。
        if (context.WindowHandles.Count == 1)
        {
            var currentWindow = context.WindowHandles[0];
            if (!handles.Contains(currentWindow))
                throw new InvalidOperationException("窗口作用域不包含当前操作窗口。");
            handles = [currentWindow];
        }
        if (NodeParameterReader.Bool(node, "singleWindow", false) && handles.Count > 1)
            handles = [handles[0]];
        if (context.ScriptInteractionMode == ScriptInteractionMode.Foreground
            && NodeParameterReader.Bool(node, "activateWindow", false))
        {
            if (handles.Count == 0)
                throw new InvalidOperationException("窗口作用域没有找到匹配的窗口。");
            if (windows is null || !windows.ActivateWindow(handles[0]))
                throw new InvalidOperationException("无法激活窗口作用域中的目标窗口。");
        }
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

public sealed class MouseMoveNodeExecutor(
    IInputAutomationService input,
    IMouseCoordinateResolver coordinates,
    IBackgroundInputAutomationService? backgroundInput = null) : NodeExecutorBase(NodeTypes.MouseMove)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var point = coordinates.Resolve(NodeParameterReader.Int(node, "x"), NodeParameterReader.Int(node, "y"),
            CoordinateModeReader.Read(NodeParameterReader.String(node, "coordinateMode", "followScript")), context);
        var duration = NodeParameterReader.Int(node, "durationMs", 0);
        if (context.ScriptInteractionMode == ScriptInteractionMode.Background)
        {
            var handle = BackgroundInputExecution.RequireTarget(context, backgroundInput, "鼠标移动");
            await backgroundInput!.MoveMouseAsync(handle, point.X, point.Y, duration, cancellationToken).ConfigureAwait(false);
            ActionExecutionLogging.Write(context, node, $"后台移动窗口 0x{handle.ToInt64():X} 内的鼠标消息到屏幕坐标 ({point.X},{point.Y})，持续 {duration} ms。");
        }
        else
        {
            await input.MoveMouseAsync(point.X, point.Y, duration, cancellationToken).ConfigureAwait(false);
            ActionExecutionLogging.Write(context, node, $"前台鼠标移动到屏幕坐标 ({point.X},{point.Y})，持续 {duration} ms。");
        }
        return NodeExecutionResult.Ok();
    }
}
public sealed class MouseDragNodeExecutor(
    IInputAutomationService input,
    IMouseCoordinateResolver coordinates,
    IBackgroundInputAutomationService? backgroundInput = null) : NodeExecutorBase(NodeTypes.MouseDrag)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var mode = CoordinateModeReader.Read(NodeParameterReader.String(node, "coordinateMode", "followScript"));
        var start = coordinates.Resolve(NodeParameterReader.Int(node, "startX"), NodeParameterReader.Int(node, "startY"), mode, context);
        var end = coordinates.Resolve(NodeParameterReader.Int(node, "endX"), NodeParameterReader.Int(node, "endY"), mode, context);
        var button = MouseActionParameterReader.Button(node);
        var duration = NodeParameterReader.Int(node, "durationMs", 500);
        if (context.ScriptInteractionMode == ScriptInteractionMode.Background)
        {
            var handle = BackgroundInputExecution.RequireTarget(context, backgroundInput, "鼠标拖动");
            await backgroundInput!.DragMouseAsync(
                handle, start.X, start.Y, end.X, end.Y,
                button, duration, cancellationToken).ConfigureAwait(false);
            ActionExecutionLogging.Write(context, node, $"后台向窗口 0x{handle.ToInt64():X} 投递鼠标拖动消息：({start.X},{start.Y}) → ({end.X},{end.Y})，按键 {button}，持续 {duration} ms。");
        }
        else
        {
            await input.DragMouseAsync(
                start.X, start.Y, end.X, end.Y,
                button, duration, cancellationToken).ConfigureAwait(false);
            ActionExecutionLogging.Write(context, node, $"前台鼠标从屏幕坐标 ({start.X},{start.Y}) 拖动到 ({end.X},{end.Y})，按键 {button}，持续 {duration} ms。");
        }
        return NodeExecutionResult.Ok();
    }
}

public sealed class MouseScrollNodeExecutor(
    IInputAutomationService input,
    IMouseCoordinateResolver coordinates,
    IBackgroundInputAutomationService? backgroundInput = null) : NodeExecutorBase(NodeTypes.MouseScroll)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var point = coordinates.Resolve(NodeParameterReader.Int(node, "x"), NodeParameterReader.Int(node, "y"),
            CoordinateModeReader.Read(NodeParameterReader.String(node, "coordinateMode", "followScript")), context);
        var delta = NodeParameterReader.Int(node, "delta", 120);
        if (context.ScriptInteractionMode == ScriptInteractionMode.Background)
        {
            var handle = BackgroundInputExecution.RequireTarget(context, backgroundInput, "鼠标滚动");
            await backgroundInput!.ScrollMouseAsync(handle, point.X, point.Y, delta, cancellationToken).ConfigureAwait(false);
            ActionExecutionLogging.Write(context, node, $"后台向窗口 0x{handle.ToInt64():X} 的屏幕坐标 ({point.X},{point.Y}) 投递滚动消息 {delta}。");
        }
        else
        {
            await input.ScrollMouseAsync(point.X, point.Y, delta, cancellationToken).ConfigureAwait(false);
            ActionExecutionLogging.Write(context, node, $"前台在屏幕坐标 ({point.X},{point.Y}) 滚动 {delta}。");
        }
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

internal static class BackgroundInputExecution
{
    public static nint RequireTarget(
        WorkflowExecutionContext context,
        IBackgroundInputAutomationService? backgroundInput,
        string operation)
    {
        if (context.WindowHandles.Count != 1)
            throw new InvalidOperationException($"后台{operation}需要唯一的目标窗口。请在脚本属性中启用“正则匹配窗口”，或先使用只返回一个窗口的窗口作用域节点。");
        if (backgroundInput is null)
            throw new InvalidOperationException($"后台{operation}服务未注册，当前运行环境不支持该操作。");
        return context.WindowHandles[0];
    }
}

public sealed class MouseClickNodeExecutor(
    IInputAutomationService input,
    IMouseCoordinateResolver coordinates,
    IBackgroundMouseClickService? backgroundClicks = null) : NodeExecutorBase(NodeTypes.MouseClick)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var parameters = MouseClickParameterReader.Read(node);
        if (context.ScriptInteractionMode == ScriptInteractionMode.Background)
        {
            if (context.WindowHandles.Count != 1)
                throw new InvalidOperationException("后台点击需要唯一的目标窗口。请在脚本属性中启用“正则匹配窗口”，或先使用只返回一个窗口的窗口作用域节点。");
            if (backgroundClicks is null)
                throw new InvalidOperationException("后台点击服务未注册，当前运行环境不支持后台点击。");

            var handle = context.WindowHandles[0];
            var absoluteCoordinates = coordinates.Resolve(parameters.X, parameters.Y, parameters.CoordinateMode, context);
            await backgroundClicks.ClickAsync(
                handle,
                absoluteCoordinates.X,
                absoluteCoordinates.Y,
                parameters.Button,
                parameters.ClickCount,
                parameters.IntervalMs,
                cancellationToken);
            ActionExecutionLogging.Write(
                context,
                node,
                $"后台点击窗口 0x{handle.ToInt64():X} 的屏幕坐标 ({absoluteCoordinates.X},{absoluteCoordinates.Y})，按键 {parameters.Button}，次数 {parameters.ClickCount}。");
        }
        else
        {
            var absoluteCoordinates = coordinates.Resolve(parameters.X, parameters.Y, parameters.CoordinateMode, context);
            await input.ClickAsync(absoluteCoordinates.X, absoluteCoordinates.Y, parameters.Button, parameters.ClickCount, parameters.IntervalMs, cancellationToken);
            ActionExecutionLogging.Write(context, node, $"前台点击屏幕坐标 ({absoluteCoordinates.X},{absoluteCoordinates.Y})，按键 {parameters.Button}，次数 {parameters.ClickCount}。");
        }
        return NodeExecutionResult.Ok();
    }
}

public sealed class KeyPressNodeExecutor(
    IInputAutomationService input,
    IBackgroundInputAutomationService? backgroundInput = null) : NodeExecutorBase(NodeTypes.KeyPress)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var key = NodeParameterReader.String(node, "key");
        var duration = NodeParameterReader.Int(node, "durationMs", 50);
        var repeatCount = NodeParameterReader.Int(node, "repeatCount", 1);
        var interval = NodeParameterReader.Int(node, "intervalMs", 0);
        if (context.ScriptInteractionMode == ScriptInteractionMode.Background)
        {
            var handle = BackgroundInputExecution.RequireTarget(context, backgroundInput, "键盘按键");
            await backgroundInput!.PressKeyAsync(handle, key, duration, repeatCount, interval, cancellationToken).ConfigureAwait(false);
            ActionExecutionLogging.Write(context, node, $"后台向窗口 0x{handle.ToInt64():X} 投递按键 {key}，重复 {repeatCount} 次，按下 {duration} ms，间隔 {interval} ms。");
        }
        else
        {
            await input.PressKeyAsync(key, duration, repeatCount, interval, cancellationToken).ConfigureAwait(false);
            ActionExecutionLogging.Write(context, node, $"前台按键 {key}，重复 {repeatCount} 次，按下 {duration} ms，间隔 {interval} ms。");
        }
        return NodeExecutionResult.Ok();
    }
}

public sealed class TextInputNodeExecutor(
    IInputAutomationService input,
    IBackgroundInputAutomationService? backgroundInput = null) : NodeExecutorBase(NodeTypes.TextInput)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var text = NodeParameterReader.String(node, "text");
        var interval = NodeParameterReader.Int(node, "intervalMs", 0);
        if (context.ScriptInteractionMode == ScriptInteractionMode.Background)
        {
            var handle = BackgroundInputExecution.RequireTarget(context, backgroundInput, "文本输入");
            await backgroundInput!.TypeTextAsync(handle, text, interval, cancellationToken).ConfigureAwait(false);
            ActionExecutionLogging.Write(context, node, $"后台向窗口 0x{handle.ToInt64():X} 投递文本，长度 {text.Length}，字符间隔 {interval} ms。");
        }
        else
        {
            await input.TypeTextAsync(text, interval, cancellationToken).ConfigureAwait(false);
            ActionExecutionLogging.Write(context, node, $"前台输入文本长度 {text.Length}，字符间隔 {interval} ms。");
        }
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
        var evaluation = await evaluator.EvaluateAsync(
            detector,
            context.WindowHandles,
            context.ScriptInteractionMode,
            cancellationToken).ConfigureAwait(false);
        var detail = evaluation.Message ?? "无详细结果";
        if (!evaluation.Supported)
        {
            ActionExecutionLogging.Write(context, node, $"{detail}；检测未能完成，节点执行失败。");
            return NodeExecutionResult.Fail(detail);
        }

        ActionExecutionLogging.Write(context, node, $"{detail}；条件结果：{ActionExecutionLogging.Outcome(evaluation.IsOn)}。");
        return NodeExecutionResult.Ok(evaluation.IsOn ? "true" : "false");
    }
}

