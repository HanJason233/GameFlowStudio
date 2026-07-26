using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Desktop.Services;

internal static class ProjectFactory
{
    public static AutomationProject CreateNew()
    {
        var project = new AutomationProject();
        var script = CreateScript("主流程", "1");
        project.Scripts.Add(script);
        project.ActiveScriptId = script.Id;
        project.Variables = script.Variables;
        project.Nodes = script.Nodes;
        project.Edges = script.Edges;
        return project;
    }

    public static AutomationScript CreateScript(string name, string id)
    {
        var script = new AutomationScript { Id = id, Name = name };
        script.Nodes.Add(new() { Id = "1", Type = NodeTypes.Start, Position = new() { X = 120, Y = 180 }, Data = new() { DisplayName = "开始" } });
        script.Nodes.Add(new() { Id = "2", Type = NodeTypes.End, Position = new() { X = 450, Y = 180 }, Data = new() { DisplayName = "结束" } });
        script.Edges.Add(new() { Id = $"{script.Id}-edge-start-end", Source = script.Nodes[0].Id, Target = script.Nodes[1].Id });
        return script;
    }
}
