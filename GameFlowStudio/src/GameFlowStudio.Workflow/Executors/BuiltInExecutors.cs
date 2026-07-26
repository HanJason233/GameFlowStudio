using System.Globalization;
using System.Text.RegularExpressions;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.States;
using GameFlowStudio.Contracts.Workflows;
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
        return NodeExecutionResult.Ok();
    }
}

public sealed class MouseMoveNodeExecutor(IInputAutomationService input) : NodeExecutorBase(NodeTypes.MouseMove)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    { await input.MoveMouseAsync(NodeParameterReader.Int(node, "x"), NodeParameterReader.Int(node, "y"), NodeParameterReader.Int(node, "durationMs", 0), cancellationToken); return NodeExecutionResult.Ok(); }
}

public sealed class MouseDragNodeExecutor(IInputAutomationService input) : NodeExecutorBase(NodeTypes.MouseDrag)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        await input.DragMouseAsync(
            NodeParameterReader.Int(node, "startX"), NodeParameterReader.Int(node, "startY"),
            NodeParameterReader.Int(node, "endX"), NodeParameterReader.Int(node, "endY"),
            MouseActionParameterReader.Button(node), NodeParameterReader.Int(node, "durationMs", 500), cancellationToken);
        return NodeExecutionResult.Ok();
    }
}

public sealed class MouseScrollNodeExecutor(IInputAutomationService input) : NodeExecutorBase(NodeTypes.MouseScroll)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        await input.ScrollMouseAsync(NodeParameterReader.Int(node, "x"), NodeParameterReader.Int(node, "y"), NodeParameterReader.Int(node, "delta", 120), cancellationToken);
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

public sealed class MouseClickNodeExecutor(IInputAutomationService input, IMouseClickCoordinateResolver coordinates, IWindowAutomationService? windows = null) : NodeExecutorBase(NodeTypes.MouseClick)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var parameters = MouseClickParameterReader.Read(node);
        if (parameters.IsWindowRelative && windows is not null)
        {
            var handle = WindowHandleSelector.Select(context, parameters.WindowHandleVariable, parameters.WindowHandleIndex);
            if (!windows.ActivateWindow(handle)) return NodeExecutionResult.Fail("无法激活目标窗口。");
        }
        var absoluteCoordinates = coordinates.Resolve(parameters, context);
        await input.ClickAsync(absoluteCoordinates.X, absoluteCoordinates.Y, parameters.Button, parameters.ClickCount, parameters.IntervalMs, cancellationToken);
        return NodeExecutionResult.Ok();
    }
}

public sealed class KeyPressNodeExecutor(IInputAutomationService input) : NodeExecutorBase(NodeTypes.KeyPress)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    { await input.PressKeyAsync(NodeParameterReader.String(node, "key"), NodeParameterReader.Int(node, "durationMs", 50), NodeParameterReader.Int(node, "repeatCount", 1), NodeParameterReader.Int(node, "intervalMs", 0), cancellationToken); return NodeExecutionResult.Ok(); }
}

public sealed class TextInputNodeExecutor(IInputAutomationService input) : NodeExecutorBase(NodeTypes.TextInput)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    { await input.TypeTextAsync(NodeParameterReader.String(node, "text"), NodeParameterReader.Int(node, "intervalMs", 0), cancellationToken); return NodeExecutionResult.Ok(); }
}

/// <summary>脚本逻辑判断：流程走到节点时只检测一次，并分出 true/false。</summary>
public sealed class StateConditionNodeExecutor(IStateConditionEvaluator evaluator) : NodeExecutorBase(NodeTypes.StateCondition)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var rules = StateRuleReader.Read(node);
        var evaluation = await evaluator.EvaluateAsync(context.Workflow.Detectors, rules, cancellationToken).ConfigureAwait(false);
        return NodeExecutionResult.Ok(evaluation.Matched ? "true" : "false");
    }
}

/// <summary>单一底层检测判断节点，检测内容直接来自节点参数。</summary>
public sealed class SingleStateConditionNodeExecutor(string nodeType, DetectorType expectedType, IStateConditionEvaluator evaluator) : NodeExecutorBase(nodeType)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var state = NodeParameterReader.Int(node, "state", 1);
        if (state is not (0 or 1)) throw new InvalidOperationException("检测器期望状态只能是 0 或 1。");

        var detector = new DetectorDefinition
        {
            Id = node.Id,
            Name = node.Data.DisplayName,
            Type = expectedType,
            Parameters = node.Data.Parameters
                .Where(pair => pair.Key != "state")
                .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
        };
        var evaluation = await evaluator.EvaluateAsync([detector], [new StateDetectorRule { DetectorId = detector.Id, State = state }], cancellationToken).ConfigureAwait(false);
        return NodeExecutionResult.Ok(evaluation.Matched ? "true" : "false");
    }
}

public sealed partial class LogNodeExecutor() : NodeExecutorBase(NodeTypes.Log)
{
    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex VariablePattern();
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var message = VariablePattern().Replace(NodeParameterReader.String(node, "message"), match => context.Variables.TryGetValue(match.Groups["name"].Value, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty : match.Value);
        var level = Enum.Parse<WorkflowLogLevel>(NodeParameterReader.String(node, "level", "information"), true);
        context.Log(new(DateTimeOffset.Now, level, context.Workflow.Project.Id, node.Id, node.Data.DisplayName, message));
        return Task.FromResult(NodeExecutionResult.Ok());
    }
}
