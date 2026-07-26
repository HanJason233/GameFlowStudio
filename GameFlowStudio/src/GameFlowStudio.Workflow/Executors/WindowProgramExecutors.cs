using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Executors;

public sealed class RunProgramNodeExecutor(IProgramAutomationService programs) : NodeExecutorBase(Contracts.Nodes.NodeTypes.RunProgram)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken) =>
        RunAsync(node, cancellationToken);

    private async Task<NodeExecutionResult> RunAsync(WorkflowNode node, CancellationToken cancellationToken)
    {
        await programs.StartAsync(
            NodeParameterReader.String(node, "path"),
            NodeParameterReader.String(node, "arguments", string.Empty),
            NodeParameterReader.String(node, "workingDirectory", string.Empty),
            NodeParameterReader.Bool(node, "waitForExit", false),
            cancellationToken).ConfigureAwait(false);
        return NodeExecutionResult.Ok();
    }
}

public sealed class CloseProgramNodeExecutor(IWindowAutomationService windows) : NodeExecutorBase(Contracts.Nodes.NodeTypes.CloseProgram)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = WindowActionParameters.SelectTarget(node, context);
        return Task.FromResult(WindowActionParameters.ActivateAndExecute(windows, handle, windows.CloseWindow, "关闭目标窗口失败。"));
    }
}

public sealed class ResizeWindowNodeExecutor(IWindowAutomationService windows) : NodeExecutorBase(Contracts.Nodes.NodeTypes.ResizeWindow)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = WindowActionParameters.SelectTarget(node, context);
        var width = NodeParameterReader.Int(node, "width");
        var height = NodeParameterReader.Int(node, "height");
        return Task.FromResult(WindowActionParameters.ActivateAndExecute(windows, handle, target => windows.ResizeWindow(target, width, height), "设置窗口大小失败。"));
    }
}

public sealed class MinimizeWindowNodeExecutor(IWindowAutomationService windows) : NodeExecutorBase(Contracts.Nodes.NodeTypes.MinimizeWindow)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = WindowActionParameters.SelectTarget(node, context);
        return Task.FromResult(WindowActionParameters.ActivateAndExecute(windows, handle, windows.MinimizeWindow, "最小化目标窗口失败。"));
    }
}

public sealed class ArrangeWindowsNodeExecutor(IWindowAutomationService windows) : NodeExecutorBase(Contracts.Nodes.NodeTypes.ArrangeWindows)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handles = WindowHandleSelector.SelectAll(context, NodeParameterReader.String(node, "windowHandleVariable", "windowHandles"));
        var arranged = windows.ArrangeWindows(handles,
            NodeParameterReader.Int(node, "x", 0), NodeParameterReader.Int(node, "y", 0),
            NodeParameterReader.Int(node, "width", 800), NodeParameterReader.Int(node, "height", 600),
            NodeParameterReader.Int(node, "columns", 2), NodeParameterReader.Int(node, "gapX", 20), NodeParameterReader.Int(node, "gapY", 20));
        return Task.FromResult(arranged ? NodeExecutionResult.Ok() : NodeExecutionResult.Fail("窗口整理失败，或没有可整理的目标窗口。"));
    }
}

public sealed class SetWindowResolutionNodeExecutor(IWindowAutomationService windows) : NodeExecutorBase(Contracts.Nodes.NodeTypes.SetWindowResolution)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = WindowActionParameters.SelectTarget(node, context);
        var width = NodeParameterReader.Int(node, "width");
        var height = NodeParameterReader.Int(node, "height");
        return Task.FromResult(WindowActionParameters.ActivateAndExecute(windows, handle, target => windows.SetWindowResolution(target, width, height), "设置窗口分辨率失败。"));
    }
}

internal static class WindowActionParameters
{
    public static nint SelectTarget(WorkflowNode node, WorkflowExecutionContext context) =>
        WindowHandleSelector.Select(context,
            NodeParameterReader.String(node, "windowHandleVariable", "windowHandles"),
            NodeParameterReader.Int(node, "windowHandleIndex", 0));

    public static NodeExecutionResult ActivateAndExecute(IWindowAutomationService windows, nint handle, Func<nint, bool> action, string failure)
    {
        if (!windows.ActivateWindow(handle)) return NodeExecutionResult.Fail("无法激活目标窗口。");
        return action(handle) ? NodeExecutionResult.Ok() : NodeExecutionResult.Fail(failure);
    }
}
