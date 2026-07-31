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
        var handles = WindowActionParameters.SelectAll(node, scopes);
        var background = InteractionModeParameterReader.IsBackground(node, "windowActionMode");
        var result = WindowActionParameters.ExecuteAll(windows, handles, windows.CloseWindow, "关闭目标窗口失败。", !background);
        if (result.Success) ActionExecutionLogging.Write(context, node, $"{(background ? "后台" : "前台")}关闭 {handles.Count} 个匹配窗口。");
        return Task.FromResult(result);
    }
}

public sealed class MinimizeWindowNodeExecutor(IWindowAutomationService windows, IWindowScopeService scopes) : NodeExecutorBase(Contracts.Nodes.NodeTypes.MinimizeWindow)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handles = WindowActionParameters.SelectAll(node, scopes);
        var background = InteractionModeParameterReader.IsBackground(node, "windowActionMode");
        var result = WindowActionParameters.ExecuteAll(windows, handles, windows.MinimizeWindow, "最小化目标窗口失败。", !background);
        if (result.Success) ActionExecutionLogging.Write(context, node, $"{(background ? "后台" : "前台")}最小化 {handles.Count} 个匹配窗口。");
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
            NodeParameterReader.Int(node, "offsetY", 20));
        if (arranged) ActionExecutionLogging.Write(context, node, $"整理 {handles.Count} 个窗口，首个窗口屏幕坐标 ({NodeParameterReader.Int(node, "x", 0)},{NodeParameterReader.Int(node, "y", 0)})，依次向下偏移 {NodeParameterReader.Int(node, "offsetY", 20)} 像素。");
        return Task.FromResult(arranged ? NodeExecutionResult.Ok() : NodeExecutionResult.Fail("窗口整理失败，或没有可整理的目标窗口。"));
    }
}

public sealed class SetWindowResolutionNodeExecutor(IWindowAutomationService windows, IWindowScopeService scopes) : NodeExecutorBase(Contracts.Nodes.NodeTypes.SetWindowResolution)
{
    public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handles = WindowActionParameters.SelectAll(node, scopes);
        var width = NodeParameterReader.Int(node, "width");
        var height = NodeParameterReader.Int(node, "height");
        var background = InteractionModeParameterReader.IsBackground(node, "windowActionMode");
        var result = WindowActionParameters.ExecuteAll(
            windows,
            handles,
            target => windows.SetWindowResolution(target, width, height),
            "设置窗口分辨率失败。",
            !background);
        if (result.Success) ActionExecutionLogging.Write(context, node, $"{(background ? "后台" : "前台")}为 {handles.Count} 个匹配窗口设置分辨率为 {width}×{height}。");
        return Task.FromResult(result);
    }
}

internal static class WindowActionParameters
{
    public static IReadOnlyList<nint> SelectAll(WorkflowNode node, IWindowScopeService scopes) =>
        WindowHandleSelector.SelectAll(scopes, NodeParameterReader.String(node, "titlePattern", ".*"));

    public static NodeExecutionResult ExecuteAll(
        IWindowAutomationService windows,
        IReadOnlyList<nint> handles,
        Func<nint, bool> action,
        string failure,
        bool activate)
    {
        foreach (var handle in handles)
        {
            if (activate && !windows.ActivateWindow(handle)) return NodeExecutionResult.Fail($"无法激活目标窗口 {handle}。");
            if (!action(handle)) return NodeExecutionResult.Fail($"{failure}（窗口句柄 {handle}）。");
        }

        return NodeExecutionResult.Ok();
    }
}
