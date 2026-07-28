using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Tests;

public sealed class AutomationProjectTests
{
    [Fact]
    public void SetActiveScriptKeepsRuntimeViewConsistent()
    {
        var project = new AutomationProject();
        var script = new AutomationScript
        {
            Id = "target",
            DefaultNodeDelayMs = 25,
            WindowScope = new() { Enabled = true }
        };

        project.SetActiveScript(script);

        Assert.Equal(script.Id, project.ActiveScriptId);
        Assert.Same(script.Variables, project.Variables);
        Assert.Same(script.Nodes, project.Nodes);
        Assert.Same(script.Edges, project.Edges);
        Assert.Same(script.WindowScope, project.WindowScope);
        Assert.Equal(script.DefaultNodeDelayMs, project.DefaultNodeDelayMs);
    }
}
