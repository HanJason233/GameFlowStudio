using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Contracts.Results;

namespace GameFlowStudio.Contracts.Events;

public sealed class NodeExecutionEventArgs(
    WorkflowNode node,
    long? elapsedMilliseconds = null,
    string? error = null,
    nint? windowHandle = null,
    string? windowTitle = null) : EventArgs
{
    public WorkflowNode Node { get; } = node;
    public long? ElapsedMilliseconds { get; } = elapsedMilliseconds;
    public string? Error { get; } = error;
    public nint? WindowHandle { get; } = windowHandle;
    public string? WindowTitle { get; } = windowTitle;
}

public sealed class WorkflowStateChangedEventArgs(WorkflowExecutionState previous, WorkflowExecutionState current) : EventArgs
{
    public WorkflowExecutionState Previous { get; } = previous;
    public WorkflowExecutionState Current { get; } = current;
}
