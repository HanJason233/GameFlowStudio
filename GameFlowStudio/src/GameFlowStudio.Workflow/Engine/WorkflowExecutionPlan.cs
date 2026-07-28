using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Engine;

/// <summary>
/// Per-run, shallow indexes over the project scripts. Node and edge objects are not
/// cloned; only their collection membership is fixed for the duration of a run.
/// </summary>
internal sealed class WorkflowExecutionPlan
{
    private readonly Dictionary<string, ScriptExecutionGraph> _scripts;

    public WorkflowExecutionPlan(AutomationProject workflow, AutomationScript initialScript)
    {
        var scripts = new Dictionary<string, ScriptExecutionGraph>(StringComparer.Ordinal);
        foreach (var script in workflow.Scripts)
        {
            if (script is null || string.IsNullOrWhiteSpace(script.Id) || scripts.ContainsKey(script.Id)) continue;
            scripts.Add(script.Id, new ScriptExecutionGraph(script));
        }

        _scripts = scripts;
        InitialGraph = scripts.TryGetValue(initialScript.Id, out var indexed)
            && ReferenceEquals(indexed.Script, initialScript)
                ? indexed
                : new ScriptExecutionGraph(initialScript);
    }

    public ScriptExecutionGraph InitialGraph { get; }

    public (ScriptExecutionGraph Graph, WorkflowNode Node) ResolveJump(WorkflowJumpTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.ScriptId) || !_scripts.TryGetValue(target.ScriptId, out var graph))
            throw new InvalidOperationException($"跳转目标脚本不存在：{target.ScriptId}。");

        return (graph, graph.ResolveJumpTarget(target.ActionId));
    }
}

/// <summary>Indexes one script by node ID and source-node/output-handle.</summary>
internal sealed class ScriptExecutionGraph
{
    private readonly Dictionary<string, WorkflowNode[]> _nodes;
    private readonly Dictionary<(string SourceId, string SourceHandle), WorkflowEdge[]> _outgoingEdges;
    private readonly WorkflowNode[] _startNodes;

    public ScriptExecutionGraph(AutomationScript script)
    {
        Script = script;
        var nodes = script.Nodes ?? [];
        var edges = script.Edges ?? [];
        _nodes = nodes
            .Where(node => node is not null && node.Id is not null)
            .GroupBy(node => node.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        _outgoingEdges = edges
            .Where(edge => edge is not null)
            .GroupBy(edge => (edge.Source, edge.SourceHandle))
            .ToDictionary(group => group.Key, group => group.ToArray());
        _startNodes = nodes
            .Where(node => node is not null && node.Type == NodeTypes.Start)
            .ToArray();
    }

    public AutomationScript Script { get; }

    public WorkflowNode StartNode => _startNodes.Length switch
    {
        1 => _startNodes[0],
        0 => throw new InvalidOperationException($"脚本 {Script.Id} 没有开始节点。"),
        _ => throw new InvalidOperationException($"脚本 {Script.Id} 包含多个开始节点。")
    };

    public WorkflowNode ResolveJumpTarget(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || !_nodes.TryGetValue(nodeId, out var matches))
            throw new InvalidOperationException($"跳转目标动作不存在：{nodeId}（脚本 {Script.Id}）。");
        if (matches.Length != 1)
            throw new InvalidOperationException($"跳转目标动作不唯一：{nodeId}（脚本 {Script.Id}）。");
        return matches[0];
    }

    public WorkflowNode ResolveNext(WorkflowNode current, string handle)
    {
        if (!_outgoingEdges.TryGetValue((current.Id, handle), out var edges))
            throw new InvalidOperationException($"节点 {current.Data.DisplayName} 的 {handle} 出口没有连接。");
        if (edges.Length != 1)
            throw new InvalidOperationException($"节点 {current.Data.DisplayName} 的 {handle} 出口包含多个连接。");

        var targetId = edges[0].Target;
        if (!_nodes.TryGetValue(targetId, out var targets))
            throw new InvalidOperationException(
                $"节点 {current.Data.DisplayName} 的 {handle} 出口指向不存在的节点 {targetId}。");
        if (targets.Length != 1)
            throw new InvalidOperationException(
                $"节点 {current.Data.DisplayName} 的 {handle} 出口指向重复节点 ID {targetId}。");
        return targets[0];
    }
}
