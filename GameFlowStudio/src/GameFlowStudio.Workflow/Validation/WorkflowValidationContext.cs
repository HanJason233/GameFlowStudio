using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Validation;

internal sealed class WorkflowValidationContext(AutomationProject workflow)
{
    public AutomationProject Workflow { get; } = workflow;
    public IReadOnlyDictionary<string, WorkflowNode> Nodes { get; } = workflow.Nodes
        .Where(node => !string.IsNullOrWhiteSpace(node.Id))
        .GroupBy(node => node.Id)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    public IReadOnlyList<WorkflowNode> StartNodes { get; } = workflow.Nodes.Where(node => node.Type == Contracts.Nodes.NodeTypes.Start).ToList();
}

internal interface IWorkflowValidationRule
{
    void Validate(WorkflowValidationContext context, WorkflowValidationResult result);
}

internal static class ValidationErrors
{
    public static void Add(this WorkflowValidationResult result, string code, string message, string? nodeId = null, string? edgeId = null) =>
        result.Errors.Add(new(code, message, nodeId, edgeId));
}
