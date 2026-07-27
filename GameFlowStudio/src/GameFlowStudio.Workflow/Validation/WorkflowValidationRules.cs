using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.States;
using GameFlowStudio.Workflow.Coordinates;
using System.Globalization;
using System.Text.Json;
using GameFlowStudio.Workflow.Executors;
using GameFlowStudio.Workflow.Detectors;

namespace GameFlowStudio.Workflow.Validation;

internal sealed class ProjectStructureRule(NodeParameterValidator parameterValidator) : IWorkflowValidationRule
{
    public void Validate(WorkflowValidationContext context, WorkflowValidationResult result)
    {
        var workflow = context.Workflow;
        if (workflow.SchemaVersion is not ("1.1" or "1.2" or "1.3")) result.Add("schema", $"不支持的架构版本：{workflow.SchemaVersion}。");
        foreach (var group in workflow.Nodes.GroupBy(node => node.Id).Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1))
            result.Add("node_id", "节点 ID 不能为空且必须唯一。", group.Key);
        if (context.StartNodes.Count == 0) result.Add("start_missing", "工作流必须包含一个开始节点。");
        if (context.StartNodes.Count > 1) result.Add("start_multiple", "工作流只能包含一个开始节点。");
        if (!workflow.Nodes.Any(node => node.Type == NodeTypes.End)) result.Add("end_missing", "工作流必须至少包含一个结束节点。");

        foreach (var node in workflow.Nodes)
        {
            if (!NodeTypes.All.Contains(node.Type)) result.Add("node_type", $"未知节点类型：{node.Type}。", node.Id);
            if (!double.IsFinite(node.Position.X) || !double.IsFinite(node.Position.Y)) result.Add("position", "节点坐标必须是有限数值。", node.Id);
            if (string.IsNullOrWhiteSpace(node.Data.DisplayName)) result.Add("display_name", "节点名称不能为空。", node.Id);
            parameterValidator.Validate(node, result);
        }
    }
}

internal sealed class EdgeReferenceRule : IWorkflowValidationRule
{
    public void Validate(WorkflowValidationContext context, WorkflowValidationResult result)
    {
        foreach (var group in context.Workflow.Edges.GroupBy(edge => edge.Id).Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1))
            result.Add("edge_id", "连接 ID 不能为空且必须唯一。", edgeId: group.Key);

        foreach (var edge in context.Workflow.Edges)
        {
            if (!context.Nodes.ContainsKey(edge.Source)) result.Add("edge_source", "连接引用了不存在的源节点。", edgeId: edge.Id);
            if (!context.Nodes.ContainsKey(edge.Target)) result.Add("edge_target", "连接引用了不存在的目标节点。", edgeId: edge.Id);
            if (edge.Source == edge.Target) result.Add("self_edge", "节点不能直接连接到自身。", edge.Source, edge.Id);
            if (edge.TargetHandle != "input") result.Add("target_handle", "目标句柄必须为 input。", edge.Target, edge.Id);
        }
    }
}

internal sealed class NodeConnectionRule : IWorkflowValidationRule
{
    public void Validate(WorkflowValidationContext context, WorkflowValidationResult result)
    {
        foreach (var node in context.Workflow.Nodes.Where(node => context.Nodes.ContainsKey(node.Id)))
        {
            var incoming = context.Workflow.Edges.Where(edge => edge.Target == node.Id).ToList();
            var outgoing = context.Workflow.Edges.Where(edge => edge.Source == node.Id).ToList();
            ValidateInputs(node, incoming, result);
            ValidateOutputs(node, outgoing, result);
        }
    }

    private static void ValidateInputs(WorkflowNode node, IReadOnlyCollection<WorkflowEdge> incoming, WorkflowValidationResult result)
    {
        if (node.Type == NodeTypes.Start && incoming.Count != 0) result.Add("start_input", "开始节点不能有输入连接。", node.Id);
        if (node.Type != NodeTypes.Start && incoming.Count == 0) result.Add("orphan", "节点没有输入连接。", node.Id);
    }

    private static void ValidateOutputs(WorkflowNode node, List<WorkflowEdge> outgoing, WorkflowValidationResult result)
    {
        switch (node.Type)
        {
            case NodeTypes.End:
                if (outgoing.Count != 0) result.Add("end_output", "结束节点不能有输出连接。", node.Id);
                break;
            case NodeTypes.ColorCondition:
            case NodeTypes.ImageCondition:
            case NodeTypes.OcrCondition:
            case NodeTypes.StateCondition:
                RequireHandles(result, node, outgoing, "true", "false");
                break;
            default:
                if (outgoing.Count(edge => edge.SourceHandle == "output") != 1 || outgoing.Any(edge => edge.SourceHandle != "output"))
                    result.Add("normal_output", "普通节点必须且只能有一个 output 连接。", node.Id);
                break;
        }
    }

    private static void RequireHandles(WorkflowValidationResult result, WorkflowNode node, IReadOnlyCollection<WorkflowEdge> outgoing, params string[] handles)
    {
        foreach (var handle in handles)
            if (outgoing.Count(edge => edge.SourceHandle == handle) != 1) result.Add("branch_output", $"节点必须且只能有一个 {handle} 连接。", node.Id);
        if (outgoing.Any(edge => !handles.Contains(edge.SourceHandle, StringComparer.Ordinal))) result.Add("branch_handle", "节点包含无效输出句柄。", node.Id);
    }

}

internal sealed class GraphTopologyRule : IWorkflowValidationRule
{
    public void Validate(WorkflowValidationContext context, WorkflowValidationResult result)
    {
        if (context.StartNodes.Count == 1)
        {
            var reachable = ReachableFrom(context.StartNodes[0].Id, context.Workflow.Edges);
            foreach (var node in context.Workflow.Nodes.Where(node => !reachable.Contains(node.Id))) result.Add("unreachable", "节点无法从开始节点到达。", node.Id);
        }
        if (HasCycle(context.Nodes, context.Workflow.Edges)) result.Add("cycle", "检测到工作流环路。");
    }

    private static HashSet<string> ReachableFrom(string start, IReadOnlyList<WorkflowEdge> edges)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!reachable.Add(node)) continue;
            foreach (var edge in edges.Where(edge => edge.Source == node)) pending.Push(edge.Target);
        }
        return reachable;
    }

    private static bool HasCycle(IReadOnlyDictionary<string, WorkflowNode> nodes, IReadOnlyList<WorkflowEdge> edges)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return nodes.Keys.Any(Visit);

        bool Visit(string id)
        {
            if (visiting.Contains(id)) return true;
            if (!visited.Add(id)) return false;
            visiting.Add(id);
            if (edges.Where(edge => edge.Source == id).Any(edge => Visit(edge.Target))) return true;
            visiting.Remove(id);
            return false;
        }
    }
}

internal sealed class JumpTargetRule : IWorkflowValidationRule
{
    public void Validate(WorkflowValidationContext context, WorkflowValidationResult result)
    {
        foreach (var node in context.Workflow.Nodes.Where(node => node.Type == NodeTypes.Jump))
        {
            if (!NodeParameterReader.Has(node, "scriptId") || !NodeParameterReader.Has(node, "actionId")) continue;

            var scriptId = NodeParameterReader.String(node, "scriptId");
            var actionId = NodeParameterReader.String(node, "actionId");
            var script = context.Workflow.Scripts.FirstOrDefault(candidate => candidate.Id == scriptId);
            if (script is null)
            {
                result.Add("jump_script", $"跳转目标脚本不存在：{scriptId}。", node.Id);
                continue;
            }

            if (!script.Nodes.Any(target => target.Id == actionId))
                result.Add("jump_action", $"跳转目标动作不存在：{actionId}。", node.Id);
        }
    }
}

internal sealed class StateConfigurationRule : IWorkflowValidationRule
{
    public void Validate(WorkflowValidationContext context, WorkflowValidationResult result)
    {
        foreach (var node in context.Workflow.Nodes.Where(node => node.Type == NodeTypes.StateCondition))
        {
            try
            {
                var detectors = StateDetectorReader.Read(node);
                var rules = StateRuleReader.Read(node);
                ValidateDetectors(detectors, result, node.Id);
                if (rules.Count == 0) result.Add("state_rules", "联合判断至少需要一个检测器规则。", node.Id);
                if (rules.Count > 0) ValidateRules(rules, detectors.Select(detector => detector.Id).ToHashSet(StringComparer.Ordinal), result, node.Id);
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException)
            { result.Add("state_rules", exception.Message, node.Id); }
        }

        foreach (var node in context.Workflow.Nodes.Where(node => node.Type is NodeTypes.ColorCondition or NodeTypes.ImageCondition or NodeTypes.OcrCondition))
        {
            try
            {
                var detector = new DetectorDefinition
                {
                    Id = node.Id,
                    Name = node.Data.DisplayName,
                    Type = node.Type switch
                    {
                        NodeTypes.ColorCondition => DetectorType.Color,
                        NodeTypes.ImageCondition => DetectorType.Image,
                        _ => DetectorType.Ocr
                    },
                    Parameters = node.Data.Parameters
                        .Where(pair => pair.Key != "state")
                        .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
                };
                ValidateDetectorParameters(detector, result, node.Id);
                var state = NodeParameterReader.Int(node, "state", 1);
                if (state is not (0 or 1)) result.Add("detector_state", "检测器期望状态只能是 0 或 1。", node.Id);
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException)
            { result.Add("state_parameters", exception.Message, node.Id); }
        }
    }

    private static void ValidateDetectors(IReadOnlyList<DetectorDefinition> detectors, WorkflowValidationResult result, string nodeId)
    {
        foreach (var group in detectors.GroupBy(detector => detector.Id).Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1))
            result.Add("detector_id", "检测器 ID 不能为空且必须唯一。", nodeId);

        foreach (var detector in detectors)
        {
            if (string.IsNullOrWhiteSpace(detector.Name)) result.Add("detector_name", "检测器名称不能为空。", nodeId);
            if (!Enum.IsDefined(detector.Type)) result.Add("detector_type", "检测器类型无效。", nodeId);
            ValidateDetectorParameters(detector, result, nodeId);
        }
    }

    private static void ValidateRules(IReadOnlyList<StateDetectorRule> rules, HashSet<string> detectorIds, WorkflowValidationResult result, string ownerId)
    {
        foreach (var rule in rules)
        {
            if (!detectorIds.Contains(rule.DetectorId)) result.Add("detector_reference", $"找不到检测器 {rule.DetectorId}。", nodeId: ownerId);
            if (rule.State is not (0 or 1)) result.Add("detector_state", "检测器期望状态只能是 0 或 1。", nodeId: ownerId);
        }
    }

    private static void ValidateDetectorParameters(DetectorDefinition detector, WorkflowValidationResult result, string? nodeId = null)
    {
        try
        {
            _ = CoordinateModeReader.Read(DetectorParameterReader.String(detector, "coordinateMode", "followScript"));
            if (detector.Type == DetectorType.Color)
            {
                ValidateCoordinate(detector, "x", "y");
                ValidateColor(DetectorParameterReader.String(detector, "targetColor", "#FFFFFF"));
                if (DetectorParameterReader.Int(detector, "width", 1) <= 0 || DetectorParameterReader.Int(detector, "height", 1) <= 0)
                    throw new InvalidOperationException("颜色检测区域的宽度和高度必须大于 0。");
                ValidateRange(DetectorParameterReader.Int(detector, "tolerance", 10), 0, 255, "颜色容差");
                ValidateRange(DetectorParameterReader.Double(detector, "minMatchRatio", 1), 0, 1, "最低匹配比例");
            }
            if (detector.Type == DetectorType.Image)
            {
                if (string.IsNullOrWhiteSpace(DetectorParameterReader.String(detector, "templatePath")))
                    throw new InvalidOperationException("图片模板路径不能为空。");
                ValidateCoordinate(detector, "x", "y");
                var hasWidth = detector.Parameters.ContainsKey("width");
                var hasHeight = detector.Parameters.ContainsKey("height");
                if (hasWidth != hasHeight) throw new InvalidOperationException("图片搜索区域的宽度和高度必须同时设置。");
                if (hasWidth && (DetectorParameterReader.Int(detector, "width") <= 0 || DetectorParameterReader.Int(detector, "height") <= 0))
                    throw new InvalidOperationException("图片搜索区域的宽度和高度必须大于 0。");
                ValidateRange(DetectorParameterReader.Int(detector, "tolerance", 10), 0, 255, "图片容差");
                ValidateRange(DetectorParameterReader.Double(detector, "matchThreshold", 0.95), 0, 1, "图片匹配阈值");
            }
            if (detector.Type == DetectorType.Ocr)
            {
                ValidateCoordinate(detector, "x", "y");
                var targetText = OcrTargetText(detector).Trim();
                if (targetText.Length == 0) throw new InvalidOperationException("OCR 目标文本不能为空。");
                if (DetectorParameterReader.Int(detector, "width", 1) <= 0 || DetectorParameterReader.Int(detector, "height", 1) <= 0)
                    throw new InvalidOperationException("OCR 检测区域的宽度和高度必须大于 0。");
                ValidateRange(OcrThreshold(detector), 0, 1, "OCR 匹配阈值");
            }
        }
        catch (InvalidOperationException exception)
        {
            result.Add("detector_parameters", exception.Message, nodeId, nodeId is null ? detector.Id : null);
        }
    }

    private static void ValidateCoordinate(DetectorDefinition detector, string xName, string yName)
    {
        var x = DetectorParameterReader.Int(detector, xName, 0);
        var y = DetectorParameterReader.Int(detector, yName, 0);
        if (x is < -100000 or > 100000 || y is < -100000 or > 100000) throw new InvalidOperationException("检测区域坐标超出允许范围。");
    }

    private static string OcrTargetText(DetectorDefinition detector) =>
        detector.Parameters.ContainsKey("targetText")
            ? DetectorParameterReader.String(detector, "targetText", string.Empty)
            : DetectorParameterReader.String(detector, "expectedText", string.Empty);

    private static double OcrThreshold(DetectorDefinition detector) =>
        detector.Parameters.ContainsKey("matchThreshold")
            ? DetectorParameterReader.Double(detector, "matchThreshold", 0.8)
            : DetectorParameterReader.Double(detector, "confidenceThreshold", 0.8);

    private static void ValidateColor(string value)
    {
        var normalized = value.Trim().TrimStart('#');
        if (normalized.Length == 3) normalized = string.Concat(normalized.Select(character => $"{character}{character}"));
        if (normalized.Length != 6 || !int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException("目标颜色无效，应使用 #RRGGBB 格式。");
    }

    private static void ValidateRange(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum) throw new InvalidOperationException($"{name}必须在 {minimum} 到 {maximum} 之间。");
    }
}
