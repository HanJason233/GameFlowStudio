using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Recording;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Workflow.Recording;
using GameFlowStudio.Workflow.Validation;

namespace GameFlowStudio.Workflow.Tests;

public sealed class RecordedScriptFactoryTests
{
    [Fact]
    public void RecordedActionsCreateAValidLinearScriptWithCapturedTiming()
    {
        RecordedInputAction[] actions =
        [
            new()
            {
                Kind = RecordedInputActionKind.MouseClick,
                OffsetMilliseconds = 100,
                X = -25,
                Y = 40,
                Button = MouseButtonType.Right
            },
            new()
            {
                Kind = RecordedInputActionKind.MouseDrag,
                OffsetMilliseconds = 1000,
                DurationMilliseconds = 300,
                X = 10,
                Y = 20,
                EndX = 200,
                EndY = 220
            },
            new()
            {
                Kind = RecordedInputActionKind.KeyPress,
                OffsetMilliseconds = 1400,
                DurationMilliseconds = 100,
                Key = "Ctrl+A"
            },
            new()
            {
                Kind = RecordedInputActionKind.MouseScroll,
                OffsetMilliseconds = 1600,
                X = 300,
                Y = 400,
                Delta = -120
            }
        ];

        var script = RecordedScriptFactory.Create("录制 1", "2", actions);
        var project = new AutomationProject();
        project.Scripts.Add(script);
        project.SetActiveScript(script);

        Assert.Equal(0, script.DefaultNodeDelayMs);
        Assert.Equal(
            [
                NodeTypes.Start,
                NodeTypes.MouseClick,
                NodeTypes.Wait,
                NodeTypes.MouseDrag,
                NodeTypes.Wait,
                NodeTypes.KeyPress,
                NodeTypes.Wait,
                NodeTypes.MouseScroll,
                NodeTypes.End
            ],
            script.Nodes.Select(node => node.Type));
        Assert.Equal(900, script.Nodes[2].Data.Parameters["durationMs"].GetInt32());
        Assert.Equal(100, script.Nodes[4].Data.Parameters["durationMs"].GetInt32());
        Assert.Equal(100, script.Nodes[6].Data.Parameters["durationMs"].GetInt32());
        Assert.Equal("absolute", script.Nodes[1].Data.Parameters["coordinateMode"].GetString());
        Assert.Equal(ScriptInteractionMode.Foreground, script.InteractionMode);
        Assert.DoesNotContain("clickMode", script.Nodes[1].Data.Parameters.Keys);
        Assert.DoesNotContain("inputMode", script.Nodes[3].Data.Parameters.Keys);
        Assert.DoesNotContain("inputMode", script.Nodes[5].Data.Parameters.Keys);
        Assert.DoesNotContain("inputMode", script.Nodes[7].Data.Parameters.Keys);
        Assert.Equal(-25, script.Nodes[1].Data.Parameters["x"].GetInt32());
        Assert.Equal("right", script.Nodes[1].Data.Parameters["button"].GetString());
        Assert.Equal(script.Nodes.Count - 1, script.Edges.Count);
        Assert.All(
            script.Edges.Select((edge, index) => (edge, index)),
            item =>
            {
                Assert.Equal(script.Nodes[item.index].Id, item.edge.Source);
                Assert.Equal(script.Nodes[item.index + 1].Id, item.edge.Target);
            });
        Assert.True(new WorkflowValidator().Validate(project).IsValid);
    }

    [Fact]
    public void EmptyRecordingStillCreatesAValidStartToEndScript()
    {
        var script = RecordedScriptFactory.Create("空录制", "3", []);
        var project = new AutomationProject();
        project.Scripts.Add(script);
        project.SetActiveScript(script);

        Assert.Equal([NodeTypes.Start, NodeTypes.End], script.Nodes.Select(node => node.Type));
        Assert.Single(script.Edges);
        Assert.True(new WorkflowValidator().Validate(project).IsValid);
    }

    [Fact]
    public void SubThresholdGapDoesNotCreateAWaitNode()
    {
        RecordedInputAction[] actions =
        [
            new()
            {
                Kind = RecordedInputActionKind.KeyPress,
                OffsetMilliseconds = 100,
                DurationMilliseconds = 50,
                Key = "A"
            },
            new()
            {
                Kind = RecordedInputActionKind.KeyPress,
                OffsetMilliseconds = 165,
                DurationMilliseconds = 50,
                Key = "B"
            }
        ];

        var script = RecordedScriptFactory.Create("快速按键", "4", actions);

        Assert.Equal(
            [NodeTypes.Start, NodeTypes.KeyPress, NodeTypes.KeyPress, NodeTypes.End],
            script.Nodes.Select(node => node.Type));
    }

    [Fact]
    public void RecordedScriptUsesCompactLeftToRightRows()
    {
        var actions = Enumerable
            .Range(0, 13)
            .Select(index => new RecordedInputAction
            {
                Kind = RecordedInputActionKind.KeyPress,
                OffsetMilliseconds = index * 10,
                Key = "A"
            })
            .ToArray();

        var script = RecordedScriptFactory.Create("紧凑录制", "5", actions);
        Assert.All(
            script.Nodes.Select((node, index) => (node, index)),
            item =>
            {
                Assert.Equal(48 + item.index % 6 * 190, item.node.Position.X);
                Assert.Equal(48 + item.index / 6 * 120, item.node.Position.Y);
            });
    }

    [Fact]
    public void RecordingAboveTheActionLimitIsRejected()
    {
        var action = new RecordedInputAction
        {
            Kind = RecordedInputActionKind.KeyPress,
            Key = "A"
        };
        var actions = Enumerable
            .Repeat(action, InputRecordingLimits.MaxActions + 1)
            .ToArray();

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => RecordedScriptFactory.Create("过长录制", "6", actions));

        Assert.Contains(InputRecordingLimits.MaxActions.ToString(), error.Message);
    }

    [Fact]
    public void ScreenRecordingIsAppendedBeforeCurrentEndWithoutChangingScriptSettings()
    {
        var source = CreateBaseScript();
        source.DefaultNodeDelayMs = 1350;
        source.InteractionMode = ScriptInteractionMode.Background;
        source.WindowScope = new()
        {
            Enabled = true,
            MatchPattern = "^原窗口$",
            ExcludePattern = "测试窗口$",
            ExecutionMode = WindowExecutionMode.Parallel
        };
        source.Variables["保留"] = new()
        {
            Type = VariableType.Number,
            Value = ProjectJson.Element(7)
        };
        RecordedInputAction[] actions =
        [
            new()
            {
                Kind = RecordedInputActionKind.MouseClick,
                OffsetMilliseconds = 120,
                X = 320,
                Y = 240
            }
        ];

        var appended = RecordedScriptFactory.Append(
            source,
            actions,
            new()
            {
                TargetEndNodeId = "end",
                Scope = InputRecordingScope.Screen,
                SessionId = "screen-session"
            });

        Assert.Equal("script", appended.Id);
        Assert.Equal("当前脚本", appended.Name);
        Assert.Equal(1350, appended.DefaultNodeDelayMs);
        Assert.Equal(ScriptInteractionMode.Background, appended.InteractionMode);
        Assert.True(appended.WindowScope.Enabled);
        Assert.Equal("^原窗口$", appended.WindowScope.MatchPattern);
        Assert.Equal("测试窗口$", appended.WindowScope.ExcludePattern);
        Assert.Equal(WindowExecutionMode.Parallel, appended.WindowScope.ExecutionMode);
        Assert.Equal(7, appended.Variables["保留"].Value.GetInt32());
        Assert.Equal([NodeTypes.Start, NodeTypes.End], source.Nodes.Select(node => node.Type));
        Assert.Equal("end", source.Edges.Single().Target);

        var ordered = FollowLinearPath(appended);
        Assert.Equal(
            [NodeTypes.Start, NodeTypes.Wait, NodeTypes.Wait, NodeTypes.MouseClick, NodeTypes.Wait, NodeTypes.End],
            ordered.Select(node => node.Type));
        Assert.Equal("录制开始 · 整个屏幕", ordered[1].Data.DisplayName);
        Assert.Equal(120, ordered[2].Data.Parameters["durationMs"].GetInt32());
        Assert.Equal("absolute", ordered[3].Data.Parameters["coordinateMode"].GetString());
        Assert.Equal("录制结束 · 整个屏幕", ordered[4].Data.DisplayName);
        Assert.All(
            ordered.Skip(1).Take(4),
            node => Assert.Equal(
                "screen-session",
                node.Data.Parameters[RecordedScriptFactory.RecordingSessionParameter].GetString()));
        Assert.True(Validate(appended).IsValid);
    }

    [Fact]
    public void WindowRecordingUsesExistingWindowScopeNodeAndRelativeCoordinates()
    {
        var source = CreateBaseScript();
        var action = new RecordedInputAction
        {
            Kind = RecordedInputActionKind.MouseDrag,
            X = 15,
            Y = 25,
            EndX = 115,
            EndY = 125,
            DurationMilliseconds = 300
        };

        var appended = RecordedScriptFactory.Append(
            source,
            [action],
            new()
            {
                TargetEndNodeId = "end",
                Scope = InputRecordingScope.Window,
                WindowTitle = "Game [1].*",
                SessionId = "window-session"
            });

        var ordered = FollowLinearPath(appended);
        Assert.Equal(
            [NodeTypes.Start, NodeTypes.WindowScope, NodeTypes.MouseDrag, NodeTypes.Wait, NodeTypes.End],
            ordered.Select(node => node.Type));
        var scope = ordered[1];
        Assert.Equal("录制开始 · 窗口：Game [1].*", scope.Data.DisplayName);
        Assert.Equal(@"^Game\ \[1]\.\*$", scope.Data.Parameters["titlePattern"].GetString());
        Assert.True(scope.Data.Parameters["singleWindow"].GetBoolean());
        Assert.False(scope.Data.Parameters["activateWindow"].GetBoolean());
        Assert.Equal("relative", ordered[2].Data.Parameters["coordinateMode"].GetString());
        Assert.Equal(15, ordered[2].Data.Parameters["startX"].GetInt32());
        Assert.Equal(125, ordered[2].Data.Parameters["endY"].GetInt32());
        Assert.Equal("录制结束 · 窗口：Game [1].*", ordered[3].Data.DisplayName);
        Assert.True(Validate(appended).IsValid);
    }

    [Fact]
    public void AppendingBeforeSharedEndPreservesConditionalSourceHandles()
    {
        var source = CreateBranchedScript();

        var appended = RecordedScriptFactory.Append(
            source,
            [
                new RecordedInputAction
                {
                    Kind = RecordedInputActionKind.KeyPress,
                    Key = "A"
                }
            ],
            new()
            {
                TargetEndNodeId = "end",
                Scope = InputRecordingScope.Screen,
                SessionId = "branch-session"
            });

        var boundary = appended.Nodes.Single(node =>
            node.Data.Parameters.TryGetValue(
                RecordedScriptFactory.RecordingBoundaryParameter,
                out var value)
            && value.GetString() == "start");
        var redirected = appended.Edges
            .Where(edge => edge.Source == "condition")
            .OrderBy(edge => edge.SourceHandle)
            .ToArray();
        Assert.Equal(2, redirected.Length);
        Assert.All(redirected, edge => Assert.Equal(boundary.Id, edge.Target));
        Assert.Equal(["false", "true"], redirected.Select(edge => edge.SourceHandle));
        Assert.True(Validate(appended).IsValid);
    }

    [Fact]
    public void AppendRejectsAnEndWithoutAnIncomingConnection()
    {
        var source = CreateBaseScript();
        source.Edges.Clear();

        var error = Assert.Throws<InvalidOperationException>(() =>
            RecordedScriptFactory.Append(
                source,
                [new RecordedInputAction { Kind = RecordedInputActionKind.KeyPress, Key = "A" }],
                new()
                {
                    TargetEndNodeId = "end",
                    Scope = InputRecordingScope.Screen,
                    SessionId = "invalid-session"
                }));

        Assert.Contains("输入连接", error.Message);
    }

    private static AutomationScript CreateBaseScript()
    {
        var script = new AutomationScript
        {
            Id = "script",
            Name = "当前脚本",
            Nodes =
            [
                new()
                {
                    Id = "start",
                    Type = NodeTypes.Start,
                    Position = new() { X = 48, Y = 48 },
                    Data = new() { DisplayName = "开始" }
                },
                new()
                {
                    Id = "end",
                    Type = NodeTypes.End,
                    Position = new() { X = 238, Y = 48 },
                    Data = new() { DisplayName = "结束" }
                }
            ],
            Edges =
            [
                new() { Id = "start-end", Source = "start", Target = "end" }
            ]
        };
        return script;
    }

    private static AutomationScript CreateBranchedScript()
    {
        var script = CreateBaseScript();
        script.Nodes.Insert(1, new WorkflowNode
        {
            Id = "condition",
            Type = NodeTypes.ColorCondition,
            Position = new() { X = 238, Y = 48 },
            Data = new()
            {
                DisplayName = "判断",
                Parameters = new()
                {
                    ["coordinateMode"] = ProjectJson.Element("absolute")
                }
            }
        });
        script.Edges =
        [
            new() { Id = "start-condition", Source = "start", Target = "condition" },
            new() { Id = "condition-true", Source = "condition", SourceHandle = "true", Target = "end" },
            new() { Id = "condition-false", Source = "condition", SourceHandle = "false", Target = "end" }
        ];
        return script;
    }

    private static IReadOnlyList<WorkflowNode> FollowLinearPath(AutomationScript script)
    {
        var byId = script.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var outgoing = script.Edges
            .GroupBy(edge => edge.Source, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var result = new List<WorkflowNode>();
        var current = script.Nodes.Single(node => node.Type == NodeTypes.Start);
        while (true)
        {
            result.Add(current);
            if (!outgoing.TryGetValue(current.Id, out var edges) || edges.Length == 0) break;
            Assert.Single(edges);
            current = byId[edges[0].Target];
        }
        return result;
    }

    private static Contracts.Results.WorkflowValidationResult Validate(AutomationScript script)
    {
        var project = new AutomationProject();
        project.Scripts.Add(script);
        project.SetActiveScript(script);
        return new WorkflowValidator().Validate(project);
    }
}
