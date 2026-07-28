using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Executors;

public sealed class RunProgramNodeExecutor(IProgramAutomationService programs) : NodeExecutorBase(Contracts.Nodes.NodeTypes.RunProgram)
{
    public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var path = NodeParameterReader.String(node, "path");
        var arguments = NodeParameterReader.String(node, "arguments", string.Empty);
        await programs.StartAsync(
            path, arguments,
            cancellationToken).ConfigureAwait(false);
        ActionExecutionLogging.Write(context, node, $"启动程序「{path}」{(string.IsNullOrWhiteSpace(arguments) ? string.Empty : $"，参数：{arguments}")}。");
        return NodeExecutionResult.Ok();
    }
}

public sealed class CloseProgramNodeExecutor(IWindowAutomationService windows, IWindowScopeService scopes) : NodeExecutorBase(Contracts.Nodes.NodeTypes.CloseProgram)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = WindowActionParameters.SelectTarget(node, scopes);
        var result = WindowActionParameters.ActivateAndExecute(windows, handle, windows.CloseWindow, "关闭目标窗口失败。");
        if (result.Success) ActionExecutionLogging.Write(context, node, $"关闭窗口句柄 {handle}。");
        return Task.FromResult(result);
    }
}

public sealed class MinimizeWindowNodeExecutor(IWindowAutomationService windows, IWindowScopeService scopes) : NodeExecutorBase(Contracts.Nodes.NodeTypes.MinimizeWindow)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = WindowActionParameters.SelectTarget(node, scopes);
        var result = WindowActionParameters.ActivateAndExecute(windows, handle, windows.MinimizeWindow, "最小化目标窗口失败。");
        if (result.Success) ActionExecutionLogging.Write(context, node, $"最小化窗口句柄 {handle}。");
        return Task.FromResult(result);
    }
}

public sealed class ArrangeWindowsNodeExecutor(IWindowAutomationService windows, IWindowScopeService scopes) : NodeExecutorBase(Contracts.Nodes.NodeTypes.ArrangeWindows)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handles = WindowActionParameters.SelectAll(node, scopes);
        var arranged = windows.ArrangeWindows(handles,
            NodeParameterReader.Int(node, "x", 0), NodeParameterReader.Int(node, "y", 0),
            NodeParameterReader.Int(node, "width", 800), NodeParameterReader.Int(node, "height", 600),
            NodeParameterReader.Int(node, "columns", 2), NodeParameterReader.Int(node, "gapX", 20), NodeParameterReader.Int(node, "gapY", 20));
        if (arranged) ActionExecutionLogging.Write(context, node, $"整理 {handles.Count} 个窗口，区域起点 ({NodeParameterReader.Int(node, "x", 0)},{NodeParameterReader.Int(node, "y", 0)})，大小 {NodeParameterReader.Int(node, "width", 800)}×{NodeParameterReader.Int(node, "height", 600)}。");
        return Task.FromResult(arranged ? NodeExecutionResult.Ok() : NodeExecutionResult.Fail("窗口整理失败，或没有可整理的目标窗口。"));
    }
}

public sealed class SetWindowResolutionNodeExecutor(IWindowAutomationService windows, IWindowScopeService scopes) : NodeExecutorBase(Contracts.Nodes.NodeTypes.SetWindowResolution)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = WindowActionParameters.SelectTarget(node, scopes);
        var width = NodeParameterReader.Int(node, "width");
        var height = NodeParameterReader.Int(node, "height");
        var result = WindowActionParameters.ActivateAndExecute(windows, handle, target => windows.SetWindowResolution(target, width, height), "设置窗口分辨率失败。");
        if (result.Success) ActionExecutionLogging.Write(context, node, $"设置窗口句柄 {handle} 分辨率为 {width}×{height}。");
        return Task.FromResult(result);
    }
}

internal static class WindowActionParameters
{
    public static nint SelectTarget(WorkflowNode node, IWindowScopeService scopes) =>
        WindowHandleSelector.Select(scopes,
            NodeParameterReader.String(node, "titlePattern", ".*"),
            NodeParameterReader.Int(node, "windowHandleIndex", 0));

    public static IReadOnlyList<nint> SelectAll(WorkflowNode node, IWindowScopeService scopes) =>
        WindowHandleSelector.SelectAll(scopes, NodeParameterReader.String(node, "titlePattern", ".*"));

    public static NodeExecutionResult ActivateAndExecute(IWindowAutomationService windows, nint handle, Func<nint, bool> action, string failure)
    {
        if (!windows.ActivateWindow(handle)) return NodeExecutionResult.Fail("无法激活目标窗口。");
        return action(handle) ? NodeExecutionResult.Ok() : NodeExecutionResult.Fail(failure);
    }
}
