using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Validation;

internal sealed class WorkflowValidationContext
{
    private static readonly IReadOnlyList<WorkflowEdge> NoEdges = [];
    private readonly Dictionary<string, List<WorkflowEdge>> _incomingEdges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<WorkflowEdge>> _outgoingEdges = new(StringComparer.Ordinal);

    public WorkflowValidationContext(AutomationProject workflow)
    {
        Workflow = workflow;
        Nodes = workflow.Nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.Id))
            .GroupBy(node => node.Id)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Scripts = workflow.Scripts
            .Where(script => !string.IsNullOrWhiteSpace(script.Id))
            .GroupBy(script => script.Id)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        StartNodes = workflow.Nodes
            .Where(node => node.Type == Contracts.Nodes.NodeTypes.Start)
            .ToList();

        foreach (var edge in workflow.Edges)
        {
            AddEdge(_outgoingEdges, edge.Source, edge);
            AddEdge(_incomingEdges, edge.Target, edge);
        }
    }

    public AutomationProject Workflow { get; }
    public IReadOnlyDictionary<string, WorkflowNode> Nodes { get; }
    public IReadOnlyDictionary<string, AutomationScript> Scripts { get; }
    public IReadOnlyList<WorkflowNode> StartNodes { get; }

    public bool ContainsNode(string? nodeId) =>
        !string.IsNullOrWhiteSpace(nodeId) && Nodes.ContainsKey(nodeId);

    public bool ContainsScript(string? scriptId) =>
        !string.IsNullOrWhiteSpace(scriptId) && Scripts.ContainsKey(scriptId);

    public IReadOnlyList<WorkflowEdge> IncomingEdges(string? nodeId) =>
        !string.IsNullOrWhiteSpace(nodeId) && _incomingEdges.TryGetValue(nodeId, out var edges)
            ? edges
            : NoEdges;

    public IReadOnlyList<WorkflowEdge> OutgoingEdges(string? nodeId) =>
        !string.IsNullOrWhiteSpace(nodeId) && _outgoingEdges.TryGetValue(nodeId, out var edges)
            ? edges
            : NoEdges;

    private static void AddEdge(
        Dictionary<string, List<WorkflowEdge>> index,
        string? nodeId,
        WorkflowEdge edge)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return;
        if (!index.TryGetValue(nodeId, out var edges))
        {
            edges = [];
            index[nodeId] = edges;
        }

        edges.Add(edge);
    }
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
