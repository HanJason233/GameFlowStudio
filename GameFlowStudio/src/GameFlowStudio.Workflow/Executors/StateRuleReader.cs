using System.Text.Json;
using GameFlowStudio.Contracts.States;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Executors;

internal static class StateRuleReader
{
    public static IReadOnlyList<StateDetectorRule> Read(WorkflowNode node)
    {
        var element = NodeParameterReader.Element(node, "detectorRules");
        if (element.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("逻辑判断的 detectorRules 必须是数组。");
        var rules = element.Deserialize<List<StateDetectorRule>>(ProjectJson.Options);
        return rules ?? throw new InvalidOperationException("逻辑判断的 detectorRules 无法读取。");
    }
}
