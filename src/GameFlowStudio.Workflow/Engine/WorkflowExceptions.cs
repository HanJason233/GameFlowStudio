namespace GameFlowStudio.Workflow.Engine;

public sealed class WorkflowValidationException(string message) : InvalidOperationException(message);
