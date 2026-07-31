using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Recording;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Recording;

public sealed record RecordedScriptAppendOptions
{
    public required string TargetEndNodeId { get; init; }
    public InputRecordingScope Scope { get; init; } = InputRecordingScope.Screen;
    public string? WindowTitle { get; init; }
    public string? SessionId { get; init; }
}

/// <summary>将输入录制快照转换为现有工作流节点，或追加到当前脚本。</summary>
public static class RecordedScriptFactory
{
    public const int MinimumWaitMilliseconds = 20;
    public const string RecordingSessionParameter = "recordingSessionId";
    public const string RecordingBoundaryParameter = "recordingBoundary";
    private const int NodesPerRow = 6;
    private const double HorizontalStep = 190;
    private const double VerticalStep = 120;

    public static AutomationScript Create(
        string name,
        string id,
        IReadOnlyList<RecordedInputAction> actions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count > InputRecordingLimits.MaxActions)
            throw new ArgumentOutOfRangeException(
                nameof(actions),
                $"单次录制最多支持 {InputRecordingLimits.MaxActions} 个操作。");

        var script = new AutomationScript
        {
            Id = id,
            Name = name.Trim(),
            DefaultNodeDelayMs = 0
        };
        AddNode(script, NodeTypes.Start, "开始", []);

        var ordered = actions
            .OrderBy(action => action.OffsetMilliseconds)
            .ToArray();
        RecordedInputAction? previous = null;
        foreach (var action in ordered)
        {
            if (previous is not null)
            {
                var gap = action.OffsetMilliseconds
                    - previous.OffsetMilliseconds
                    - PlaybackDuration(previous);
                if (gap >= MinimumWaitMilliseconds)
                {
                    var duration = (int)Math.Min(gap, int.MaxValue);
                    AddNode(
                        script,
                        NodeTypes.Wait,
                        $"等待 {duration} ms",
                        Parameters(("durationMs", duration)));
                }
            }

            AddActionNode(script, action);
            previous = action;
        }

        AddNode(script, NodeTypes.End, "结束", []);
        for (var index = 1; index < script.Nodes.Count; index++)
        {
            var source = script.Nodes[index - 1];
            var target = script.Nodes[index];
            script.Edges.Add(new WorkflowEdge
            {
                Id = $"{id}-edge-{source.Id}-{target.Id}",
                Source = source.Id,
                Target = target.Id
            });
        }
        return script;
    }

    /// <summary>
    /// 在指定结束节点前插入录制片段。返回脚本副本，不修改传入脚本。
    /// </summary>
    public static AutomationScript Append(
        AutomationScript source,
        IReadOnlyList<RecordedInputAction> actions,
        RecordedScriptAppendOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TargetEndNodeId);
        ValidateActionCount(actions);

        if (options.Scope == InputRecordingScope.Window
            && string.IsNullOrWhiteSpace(options.WindowTitle))
            throw new ArgumentException("窗口录制必须提供目标窗口标题。", nameof(options));

        var script = CloneScript(source, options);
        var targetEnd = script.Nodes.FirstOrDefault(node =>
            node.Id == options.TargetEndNodeId
            && node.Type == NodeTypes.End)
            ?? throw new InvalidOperationException("找不到用于追加录制步骤的结束节点。");
        var incoming = script.Edges
            .Where(edge => edge.Target == targetEnd.Id)
            .ToArray();
        if (incoming.Length == 0)
            throw new InvalidOperationException("录制步骤只能追加到已有输入连接的结束节点前。");

        var sessionId = string.IsNullOrWhiteSpace(options.SessionId)
            ? Guid.NewGuid().ToString("N")
            : options.SessionId.Trim();
        var fragment = CreateFragment(actions, options, sessionId);
        var nodeIds = script.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var edgeIds = script.Edges.Select(edge => edge.Id).ToHashSet(StringComparer.Ordinal);
        AssignFragmentIdsAndPositions(script, fragment, targetEnd, sessionId, nodeIds);

        foreach (var edge in incoming)
            edge.Target = fragment[0].Id;

        for (var index = 1; index < fragment.Count; index++)
        {
            var sourceNode = fragment[index - 1];
            var targetNode = fragment[index];
            script.Edges.Add(new WorkflowEdge
            {
                Id = NextUniqueId(edgeIds, $"recording-{sessionId}-edge-{index}"),
                Source = sourceNode.Id,
                Target = targetNode.Id
            });
        }
        script.Edges.Add(new WorkflowEdge
        {
            Id = NextUniqueId(edgeIds, $"recording-{sessionId}-edge-end"),
            Source = fragment[^1].Id,
            Target = targetEnd.Id
        });

        script.Nodes.Remove(targetEnd);
        script.Nodes.AddRange(fragment);
        script.Nodes.Add(targetEnd);
        return script;
    }

    private static List<WorkflowNode> CreateFragment(
        IReadOnlyList<RecordedInputAction> actions,
        RecordedScriptAppendOptions options,
        string sessionId)
    {
        var fragmentScript = new AutomationScript();
        var scopeLabel = options.Scope == InputRecordingScope.Window
            ? $"窗口：{options.WindowTitle!.Trim()}"
            : "整个屏幕";
        AddNode(
            fragmentScript,
            NodeTypes.Wait,
            $"录制开始 · {scopeLabel}",
            Parameters(
                ("durationMs", 0),
                (RecordingSessionParameter, sessionId),
                (RecordingBoundaryParameter, "start")));

        var ordered = actions
            .OrderBy(action => action.OffsetMilliseconds)
            .ToArray();
        RecordedInputAction? previous = null;
        foreach (var action in ordered)
        {
            var gap = previous is null
                ? action.OffsetMilliseconds
                : action.OffsetMilliseconds
                    - previous.OffsetMilliseconds
                    - PlaybackDuration(previous);
            if (gap >= MinimumWaitMilliseconds)
            {
                var duration = (int)Math.Min(gap, int.MaxValue);
                AddNode(
                    fragmentScript,
                    NodeTypes.Wait,
                    $"等待 {duration} ms",
                    Parameters(
                        ("durationMs", duration),
                        (RecordingSessionParameter, sessionId)));
            }

            AddActionNode(
                fragmentScript,
                action,
                options.Scope == InputRecordingScope.Window ? "relative" : "absolute",
                sessionId);
            previous = action;
        }

        AddNode(
            fragmentScript,
            NodeTypes.Wait,
            $"录制结束 · {scopeLabel}",
            Parameters(
                ("durationMs", 0),
                (RecordingSessionParameter, sessionId),
                (RecordingBoundaryParameter, "end")));
        return fragmentScript.Nodes;
    }

    private static void AddActionNode(
        AutomationScript script,
        RecordedInputAction action,
        string coordinateMode = "absolute",
        string? recordingSessionId = null)
    {
        switch (action.Kind)
        {
            case RecordedInputActionKind.MouseClick:
                AddNode(
                    script,
                    NodeTypes.MouseClick,
                    $"{ButtonLabel(action)}点击 ({action.X}, {action.Y})",
                    RecordedParameters(
                        recordingSessionId,
                        ("coordinateMode", coordinateMode),
                        ("x", action.X),
                        ("y", action.Y),
                        ("button", action.Button.ToString().ToLowerInvariant()),
                        ("clickCount", Math.Max(1, action.ClickCount)),
                        ("intervalMs", Math.Max(0, action.IntervalMilliseconds))));
                break;
            case RecordedInputActionKind.MouseDrag:
                AddNode(
                    script,
                    NodeTypes.MouseDrag,
                    $"拖动 ({action.X}, {action.Y}) → ({action.EndX}, {action.EndY})",
                    RecordedParameters(
                        recordingSessionId,
                        ("coordinateMode", coordinateMode),
                        ("startX", action.X),
                        ("startY", action.Y),
                        ("endX", action.EndX),
                        ("endY", action.EndY),
                        ("button", action.Button.ToString().ToLowerInvariant()),
                        ("durationMs", Math.Max(0, action.DurationMilliseconds))));
                break;
            case RecordedInputActionKind.MouseScroll:
                if (action.Delta == 0) return;
                AddNode(
                    script,
                    NodeTypes.MouseScroll,
                    $"滚轮 {(action.Delta > 0 ? "+" : string.Empty)}{action.Delta} ({action.X}, {action.Y})",
                    RecordedParameters(
                        recordingSessionId,
                        ("coordinateMode", coordinateMode),
                        ("x", action.X),
                        ("y", action.Y),
                        ("delta", action.Delta)));
                break;
            case RecordedInputActionKind.KeyPress:
                if (string.IsNullOrWhiteSpace(action.Key)) return;
                AddNode(
                    script,
                    NodeTypes.KeyPress,
                    $"按键 {action.Key}",
                    RecordedParameters(
                        recordingSessionId,
                        ("key", action.Key),
                        ("durationMs", Math.Max(0, action.DurationMilliseconds)),
                        ("repeatCount", 1),
                        ("intervalMs", 0)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "不支持的录制操作类型。");
        }
    }

    private static void AddNode(
        AutomationScript script,
        string type,
        string displayName,
        Dictionary<string, JsonElement> parameters)
    {
        var index = script.Nodes.Count;
        var row = index / NodesPerRow;
        var column = index % NodesPerRow;
        script.Nodes.Add(new WorkflowNode
        {
            Id = (index + 1).ToString(CultureInfo.InvariantCulture),
            Type = type,
            Position = new NodePosition
            {
                X = 48 + column * HorizontalStep,
                Y = 48 + row * VerticalStep
            },
            Data = new WorkflowNodeData
            {
                DisplayName = displayName,
                Parameters = parameters
            }
        });
    }

    private static Dictionary<string, JsonElement> RecordedParameters(
        string? recordingSessionId,
        params (string Name, object Value)[] values)
    {
        if (string.IsNullOrWhiteSpace(recordingSessionId))
            return Parameters(values);
        return Parameters(
            [.. values, (RecordingSessionParameter, recordingSessionId)]);
    }

    private static Dictionary<string, JsonElement> Parameters(
        params (string Name, object Value)[] values) =>
        values.ToDictionary(
            value => value.Name,
            value => JsonSerializer.SerializeToElement(value.Value, value.Value.GetType(), ProjectJson.Options),
            StringComparer.Ordinal);

    private static void ValidateActionCount(IReadOnlyList<RecordedInputAction> actions)
    {
        if (actions.Count > InputRecordingLimits.MaxActions)
            throw new ArgumentOutOfRangeException(
                nameof(actions),
                $"单次录制最多支持 {InputRecordingLimits.MaxActions} 个操作。");
    }

    private static AutomationScript CloneScript(AutomationScript source, RecordedScriptAppendOptions options) => new()
    {
        Id = source.Id,
        Name = source.Name,
        InteractionMode = source.InteractionMode,
        WindowScope = CreateScriptWindowScope(source, options),
        DefaultNodeDelayMs = source.DefaultNodeDelayMs,
        Variables = source.Variables.ToDictionary(
            pair => pair.Key,
            pair => new ProjectVariable
            {
                Type = pair.Value.Type,
                Value = pair.Value.Value.Clone()
            },
            StringComparer.Ordinal),
        Nodes = source.Nodes.Select(node => new WorkflowNode
        {
            Id = node.Id,
            Type = node.Type,
            Position = new NodePosition
            {
                X = node.Position.X,
                Y = node.Position.Y
            },
            Data = new WorkflowNodeData
            {
                DisplayName = node.Data.DisplayName,
                Parameters = node.Data.Parameters.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Clone(),
                    StringComparer.Ordinal)
            }
        }).ToList(),
        Edges = source.Edges.Select(edge => new WorkflowEdge
        {
            Id = edge.Id,
            Source = edge.Source,
            SourceHandle = edge.SourceHandle,
            Target = edge.Target,
            TargetHandle = edge.TargetHandle
        }).ToList()
    };

    private static ScriptWindowScope CreateScriptWindowScope(
        AutomationScript source,
        RecordedScriptAppendOptions options)
    {
        var scope = new ScriptWindowScope
        {
            Enabled = source.WindowScope.Enabled,
            MatchPattern = source.WindowScope.MatchPattern,
            ExcludePattern = source.WindowScope.ExcludePattern,
            ExecutionMode = source.WindowScope.ExecutionMode
        };

        if (options.Scope == InputRecordingScope.Window)
        {
            scope.Enabled = true;
            scope.MatchPattern = $"^{Regex.Escape(options.WindowTitle!.Trim())}$";
            scope.ExcludePattern = string.Empty;
        }

        return scope;
    }

    private static void AssignFragmentIdsAndPositions(
        AutomationScript script,
        IReadOnlyList<WorkflowNode> fragment,
        WorkflowNode targetEnd,
        string sessionId,
        HashSet<string> nodeIds)
    {
        var top = script.Nodes
            .Where(node => node.Id != targetEnd.Id)
            .Select(node => node.Position.Y)
            .DefaultIfEmpty(targetEnd.Position.Y - VerticalStep)
            .Max() + VerticalStep;

        for (var index = 0; index < fragment.Count; index++)
        {
            var node = fragment[index];
            node.Id = NextUniqueId(nodeIds, $"recording-{sessionId}-{index + 1}");
            node.Position = Position(index, top);
        }
        targetEnd.Position = Position(fragment.Count, top);
    }

    private static NodePosition Position(int index, double top) => new()
    {
        X = 48 + index % NodesPerRow * HorizontalStep,
        Y = top + index / NodesPerRow * VerticalStep
    };

    private static string NextUniqueId(HashSet<string> used, string preferred)
    {
        var candidate = preferred;
        var suffix = 2;
        while (!used.Add(candidate))
            candidate = $"{preferred}-{suffix++}";
        return candidate;
    }

    private static long PlaybackDuration(RecordedInputAction action) =>
        action.Kind switch
        {
            RecordedInputActionKind.MouseDrag or RecordedInputActionKind.KeyPress =>
                Math.Max(0, action.DurationMilliseconds),
            RecordedInputActionKind.MouseClick when action.ClickCount > 1 =>
                (long)(action.ClickCount - 1) * Math.Max(0, action.IntervalMilliseconds),
            _ => 0
        };

    private static string ButtonLabel(RecordedInputAction action) =>
        action.Button switch
        {
            Contracts.Enums.MouseButtonType.Right => "右键",
            Contracts.Enums.MouseButtonType.Middle => "中键",
            _ => "左键"
        };
}
