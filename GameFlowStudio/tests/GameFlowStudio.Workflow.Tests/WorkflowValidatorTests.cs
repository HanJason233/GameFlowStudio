using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Workflow.Validation;
using System.Text.Json;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Tests;

public sealed class WorkflowValidatorTests
{
    private readonly WorkflowValidator _validator = new();

    [Fact] public void MissingStartFails() { var p = WorkflowEngineTests.NewProject(); p.Nodes.Add(WorkflowEngineTests.Node("end", NodeTypes.End)); Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "start_missing"); }
    [Fact] public void MultipleStartsFail() { var p = WorkflowEngineTests.NewProject(); p.Nodes = [WorkflowEngineTests.Node("a",NodeTypes.Start), WorkflowEngineTests.Node("b",NodeTypes.Start)]; Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "start_multiple"); }
    [Fact] public void InvalidEdgeTargetFails() { var p = Valid(); p.Edges[0].Target = "missing"; Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "edge_target"); }
    [Fact] public void OrphanNodeFails() { var p = Valid(); p.Nodes.Add(WorkflowEngineTests.Log("orphan")); Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "orphan"); }
    [Fact] public void NegativeWaitFails() { var wait = WorkflowEngineTests.Node("wait",NodeTypes.Wait,("durationMs",-1)); var p = ValidWith(wait); Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "parameters"); }
    [Fact] public void InvalidCoordinatesFail() { var p = Valid(); p.Nodes[0].Position.X = double.NaN; Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "position"); }
    [Fact] public void StructuralNodeCannotBeDisabled() { var p = Valid(); p.Nodes[0].Data.Enabled = false; Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "structural_disabled"); }
    [Fact] public void OrdinaryCycleFails() { var p = ValidWith(WorkflowEngineTests.Log("a"), WorkflowEngineTests.Log("b")); p.Edges[^1].Target = "a"; Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "cycle"); }
    [Fact] public async Task SuppliedSampleIsValid() { await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory,"samples","basic-example.gflow")); var project = await JsonSerializer.DeserializeAsync<AutomationProject>(stream,ProjectJson.Options); Assert.NotNull(project); var script = Assert.Single(project.Scripts); var workflow = new AutomationProject { SchemaVersion = project.SchemaVersion, Project = project.Project, Variables = script.Variables, Nodes = script.Nodes, Edges = script.Edges }; Assert.True(_validator.Validate(workflow).IsValid, string.Join(" | ",_validator.Validate(workflow).Errors.Select(e=>e.Message))); }
    [Fact] public void InvalidWindowScopePatternFails() { var scope = WorkflowEngineTests.Node("scope", NodeTypes.WindowScope, ("titlePattern", "["), ("outputVariable", "windowHandles")); var p = ValidWith(scope); Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "parameters" && e.NodeId == "scope"); }
    [Fact] public void InvalidLogLevelFails() { var log = WorkflowEngineTests.Node("log", NodeTypes.Log, ("message", "test"), ("level", "unknown")); var p = ValidWith(log); Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "parameters" && e.NodeId == "log"); }
    [Fact] public void InvalidInlineColorParametersFail() { var color = WorkflowEngineTests.Node("color", NodeTypes.ColorCondition, ("targetColor", "not-a-color"), ("state", 1)); var p = ValidWith(color); Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "detector_parameters" && e.NodeId == "color"); }
    private static Contracts.Workflows.AutomationProject Valid() => ValidWith();
    private static Contracts.Workflows.AutomationProject ValidWith(params Contracts.Workflows.WorkflowNode[] middle)
    {
        var p = WorkflowEngineTests.NewProject(); var start = WorkflowEngineTests.Node("start",NodeTypes.Start); var end = WorkflowEngineTests.Node("end",NodeTypes.End); p.Nodes = [start, .. middle, end];
        var nodes = p.Nodes; for (var i=0;i<nodes.Count-1;i++) p.Edges.Add(WorkflowEngineTests.Edge(nodes[i],"output",nodes[i+1])); return p;
    }
}
