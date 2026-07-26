using System.Text.Json;
using GameFlowStudio.Contracts.States;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Executors;

internal static class StateDetectorReader
{
    public static IReadOnlyList<DetectorDefinition> Read(WorkflowNode node)
    {
        var element = NodeParameterReader.Element(node, "detectors");
        if (element.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("联合判断的 detectors 必须是数组。");
        return element.Deserialize<List<DetectorDefinition>>(ProjectJson.Options)
            ?? throw new InvalidOperationException("联合判断的 detectors 无法读取。");
    }
}
