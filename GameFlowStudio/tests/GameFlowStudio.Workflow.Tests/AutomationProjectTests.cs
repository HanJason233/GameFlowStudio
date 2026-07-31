using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Tests;

public sealed class AutomationProjectTests
{
    [Fact]
    public void NewScriptUsesTwoSecondDefaultDelay()
    {
        Assert.Equal(2000, new AutomationScript().DefaultNodeDelayMs);
    }

    [Fact]
    public void NewScriptUsesForegroundInteractionMode()
    {
        Assert.Equal(ScriptInteractionMode.Foreground, new AutomationScript().InteractionMode);
    }

    [Fact]
    public void NewScriptUsesWindowPriorityExecutionMode()
    {
        Assert.Equal(WindowExecutionMode.WindowPriority, new AutomationScript().WindowScope.ExecutionMode);
    }

    [Fact]
    public void SetActiveScriptKeepsRuntimeViewConsistent()
    {
        var project = new AutomationProject();
        var script = new AutomationScript
        {
            Id = "target",
            DefaultNodeDelayMs = 25,
            InteractionMode = ScriptInteractionMode.Background,
            WindowScope = new() { Enabled = true }
        };

        project.SetActiveScript(script);

        Assert.Equal(script.Id, project.ActiveScriptId);
        Assert.Same(script.Variables, project.Variables);
        Assert.Same(script.Nodes, project.Nodes);
        Assert.Same(script.Edges, project.Edges);
        Assert.Same(script.WindowScope, project.WindowScope);
        Assert.Equal(script.DefaultNodeDelayMs, project.DefaultNodeDelayMs);
        Assert.Equal(script.InteractionMode, project.InteractionMode);
    }
}
