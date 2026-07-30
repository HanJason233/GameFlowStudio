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
}
