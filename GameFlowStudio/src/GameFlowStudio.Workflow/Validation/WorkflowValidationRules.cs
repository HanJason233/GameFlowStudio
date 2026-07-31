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
        var scriptInteractionMode = ResolveScriptInteractionMode(workflow);
        if (workflow.SchemaVersion is not "1.5") result.Add("schema", $"不支持的架构版本：{workflow.SchemaVersion}。");
        foreach (var group in workflow.Scripts.GroupBy(script => script.Id).Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1))
            result.Add("script_id", "脚本 ID 不能为空且必须唯一。");
        foreach (var script in workflow.Scripts.Where(script => string.IsNullOrWhiteSpace(script.Name)))
            result.Add("script_name", $"脚本 {script.Id} 的名称不能为空。");
        foreach (var script in workflow.Scripts.Where(script => !Enum.IsDefined(script.InteractionMode)))
            result.Add("interaction_mode", $"脚本 {script.Id} 的运行方式无效。");
        if (workflow.ActiveScriptId is not null && !context.ContainsScript(workflow.ActiveScriptId))
            result.Add("active_script", "活动脚本不存在。");
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
            parameterValidator.Validate(node, scriptInteractionMode, result);
        }

        foreach (var script in ReachableTargetScripts(workflow))
        {
            if (ReferenceEquals(script.Nodes, workflow.Nodes)
                || string.Equals(script.Id, workflow.ActiveScriptId, StringComparison.Ordinal))
                continue;

            foreach (var node in script.Nodes)
                parameterValidator.Validate(node, script.InteractionMode, result);
        }
    }

    private static ScriptInteractionMode ResolveScriptInteractionMode(AutomationProject workflow)
    {
        if (workflow.ActiveScriptId is { } activeScriptId)
        {
            var active = workflow.Scripts.FirstOrDefault(script => script.Id == activeScriptId);
            if (active is not null) return active.InteractionMode;
        }

        return workflow.Scripts.FirstOrDefault(script => ReferenceEquals(script.Nodes, workflow.Nodes))?.InteractionMode
            ?? workflow.InteractionMode;
    }

    private static IEnumerable<AutomationScript> ReachableTargetScripts(AutomationProject workflow)
    {
        var scripts = workflow.Scripts
            .Where(script => !string.IsNullOrWhiteSpace(script.Id))
            .GroupBy(script => script.Id)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(workflow.ActiveScriptId)) visited.Add(workflow.ActiveScriptId);
        foreach (var active in workflow.Scripts.Where(script => ReferenceEquals(script.Nodes, workflow.Nodes)))
            visited.Add(active.Id);
        var pending = new Queue<IReadOnlyList<WorkflowNode>>();
        pending.Enqueue(workflow.Nodes);

        while (pending.Count > 0)
        {
            foreach (var node in pending.Dequeue().Where(node => node.Type == NodeTypes.Jump))
            {
                if (!node.Data.Parameters.TryGetValue("scriptId", out var scriptIdElement)
                    || scriptIdElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(scriptIdElement.GetString())
                    || !scripts.TryGetValue(scriptIdElement.GetString()!, out var target)
                    || !visited.Add(target.Id))
                    continue;

                yield return target;
                pending.Enqueue(target.Nodes);
            }
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
            if (!context.ContainsNode(edge.Source)) result.Add("edge_source", "连接引用了不存在的源节点。", edgeId: edge.Id);
            if (!context.ContainsNode(edge.Target)) result.Add("edge_target", "连接引用了不存在的目标节点。", edgeId: edge.Id);
            if (edge.Source == edge.Target) result.Add("self_edge", "节点不能直接连接到自身。", edge.Source, edge.Id);
            if (edge.TargetHandle != "input") result.Add("target_handle", "目标句柄必须为 input。", edge.Target, edge.Id);
        }
    }
}

internal sealed class NodeConnectionRule : IWorkflowValidationRule
{
    public void Validate(WorkflowValidationContext context, WorkflowValidationResult result)
    {
        foreach (var node in context.Workflow.Nodes.Where(node => context.ContainsNode(node.Id)))
        {
            ValidateInputs(node, context.IncomingEdges(node.Id), result);
            ValidateOutputs(node, context.OutgoingEdges(node.Id), result);
        }
    }

    private static void ValidateInputs(WorkflowNode node, IReadOnlyList<WorkflowEdge> incoming, WorkflowValidationResult result)
    {
        if (node.Type == NodeTypes.Start && incoming.Count != 0) result.Add("start_input", "开始节点不能有输入连接。", node.Id);
        if (node.Type != NodeTypes.Start && incoming.Count == 0) result.Add("orphan", "节点没有输入连接。", node.Id);
    }

    private static void ValidateOutputs(WorkflowNode node, IReadOnlyList<WorkflowEdge> outgoing, WorkflowValidationResult result)
    {
        switch (node.Type)
        {
            case NodeTypes.End:
                if (outgoing.Count != 0) result.Add("end_output", "结束节点不能有输出连接。", node.Id);
                break;
            case NodeTypes.ColorCondition:
            case NodeTypes.ImageCondition:
            case NodeTypes.OcrCondition:
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
            var reachable = ReachableFrom(context.StartNodes[0].Id, context);
            foreach (var node in context.Workflow.Nodes.Where(node => !reachable.Contains(node.Id))) result.Add("unreachable", "节点无法从开始节点到达。", node.Id);
        }
        if (HasCycle(context)) result.Add("cycle", "检测到工作流环路。");
    }

    private static HashSet<string> ReachableFrom(string start, WorkflowValidationContext context)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!reachable.Add(node)) continue;
            foreach (var edge in context.OutgoingEdges(node))
                if (context.ContainsNode(edge.Target))
                    pending.Push(edge.Target);
        }
        return reachable;
    }

    private static bool HasCycle(WorkflowValidationContext context)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return context.Nodes.Keys.Any(Visit);

        bool Visit(string id)
        {
            if (visiting.Contains(id)) return true;
            if (!visited.Add(id)) return false;
            visiting.Add(id);
            if (context.OutgoingEdges(id).Any(edge => context.ContainsNode(edge.Target) && Visit(edge.Target))) return true;
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
            if (!context.Scripts.TryGetValue(scriptId, out var script))
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
                    Parameters = node.Data.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
                };
                ValidateDetectorParameters(detector, result, node.Id);
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException)
            { result.Add("state_parameters", exception.Message, node.Id); }
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
                var imageSearchMode = DetectorParameterReader.String(detector, "imageSearchMode", "fixed").Trim().ToLowerInvariant();
                if (imageSearchMode is not ("fixed" or "current"))
                    throw new InvalidOperationException("图片匹配范围只支持固定坐标或当前屏幕/窗口。");
                if (imageSearchMode == "fixed")
                {
                    ValidateCoordinate(detector, "x", "y");
                    var hasWidth = detector.Parameters.ContainsKey("width");
                    var hasHeight = detector.Parameters.ContainsKey("height");
                    if (hasWidth != hasHeight) throw new InvalidOperationException("图片搜索区域的宽度和高度必须同时设置。");
                    if (hasWidth && (DetectorParameterReader.Int(detector, "width") <= 0 || DetectorParameterReader.Int(detector, "height") <= 0))
                        throw new InvalidOperationException("图片搜索区域的宽度和高度必须大于 0。");
                }
                ValidateRange(DetectorParameterReader.Int(detector, "tolerance", 10), 0, 255, "图片容差");
                ValidateRange(DetectorParameterReader.Double(detector, "matchThreshold", 0.95), 0, 1, "图片匹配阈值");
                var imageMatchEngine = DetectorParameterReader.String(detector, "imageMatchEngine", "custom").Trim().ToLowerInvariant();
                if (imageMatchEngine is not ("custom" or "opencv"))
                    throw new InvalidOperationException("图像匹配引擎只支持自实现或 OpenCV。");
            }
            if (detector.Type == DetectorType.Ocr)
            {
                var textSearchMode = DetectorParameterReader.String(detector, "textSearchMode", "fixed").Trim().ToLowerInvariant();
                if (textSearchMode is not ("fixed" or "current"))
                    throw new InvalidOperationException("文本匹配范围只支持固定坐标或当前屏幕/窗口。");
                if (textSearchMode == "fixed")
                {
                    ValidateCoordinate(detector, "x", "y");
                    if (DetectorParameterReader.Int(detector, "width", 1) <= 0 || DetectorParameterReader.Int(detector, "height", 1) <= 0)
                        throw new InvalidOperationException("文本识别区域的宽度和高度必须大于 0。");
                }
                var targetText = OcrTargetText(detector).Trim();
                if (targetText.Length == 0) throw new InvalidOperationException("匹配文本不能为空。");
                ValidateRange(OcrThreshold(detector), 0, 1, "文本匹配阈值");
                var ocrEngine = DetectorParameterReader.String(detector, "ocrEngine", "gemini").Trim().ToLowerInvariant();
                if (ocrEngine is not ("local" or "gemini"))
                    throw new InvalidOperationException("文本识别引擎只支持本地 OCR 或 Gemini OCR。");
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
