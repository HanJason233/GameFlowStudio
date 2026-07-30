using System.Globalization;
using System.Text.Json;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Recording;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Recording;

/// <summary>将输入录制快照转换为只使用内置节点的线性脚本。</summary>
public static class RecordedScriptFactory
{
    public const int MinimumWaitMilliseconds = 20;
    private const int NodesPerRow = 12;
    private const double HorizontalGap = 280;
    private const double VerticalGap = 170;

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

    private static void AddActionNode(
        AutomationScript script,
        RecordedInputAction action)
    {
        switch (action.Kind)
        {
            case RecordedInputActionKind.MouseClick:
                AddNode(
                    script,
                    NodeTypes.MouseClick,
                    $"{ButtonLabel(action)}点击 ({action.X}, {action.Y})",
                    Parameters(
                        ("coordinateMode", "absolute"),
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
                    Parameters(
                        ("coordinateMode", "absolute"),
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
                    Parameters(
                        ("coordinateMode", "absolute"),
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
                    Parameters(
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
        var columnInRow = index % NodesPerRow;
        var column = row % 2 == 0
            ? columnInRow
            : NodesPerRow - 1 - columnInRow;
        script.Nodes.Add(new WorkflowNode
        {
            Id = (index + 1).ToString(CultureInfo.InvariantCulture),
            Type = type,
            Position = new NodePosition
            {
                X = 100 + column * HorizontalGap,
                Y = 100 + row * VerticalGap
            },
            Data = new WorkflowNodeData
            {
                DisplayName = displayName,
                Parameters = parameters
            }
        });
    }

    private static Dictionary<string, JsonElement> Parameters(
        params (string Name, object Value)[] values) =>
        values.ToDictionary(
            value => value.Name,
            value => JsonSerializer.SerializeToElement(value.Value, value.Value.GetType(), ProjectJson.Options),
            StringComparer.Ordinal);

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
