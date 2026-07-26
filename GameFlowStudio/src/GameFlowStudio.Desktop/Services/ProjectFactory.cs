using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Desktop.Services;

internal static class ProjectFactory
{
    public static AutomationProject CreateNew()
    {
        var project = new AutomationProject();
        var script = CreateScript("主流程");
        project.Scripts.Add(script);
        project.Variables = script.Variables;
        project.Nodes = script.Nodes;
        project.Edges = script.Edges;
        return project;
    }

    public static AutomationScript CreateScript(string name)
    {
        var script = new AutomationScript { Name = name };
        script.Nodes.Add(new() { Id = $"{script.Id}-start", Type = NodeTypes.Start, Position = new() { X = 120, Y = 180 }, Data = new() { DisplayName = "开始" } });
        script.Nodes.Add(new() { Id = $"{script.Id}-end", Type = NodeTypes.End, Position = new() { X = 450, Y = 180 }, Data = new() { DisplayName = "结束" } });
        script.Edges.Add(new() { Id = $"{script.Id}-edge-start-end", Source = script.Nodes[0].Id, Target = script.Nodes[1].Id });
        return script;
    }
}
