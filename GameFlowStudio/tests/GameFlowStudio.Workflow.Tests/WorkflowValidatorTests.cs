using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Enums;
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
    [Fact] public void OrphanNodeFails() { var p = Valid(); p.Nodes.Add(WorkflowEngineTests.Action("orphan")); Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "orphan"); }
    [Fact] public void NegativeWaitFails() { var wait = WorkflowEngineTests.Node("wait",NodeTypes.Wait,("durationMs",-1)); var p = ValidWith(wait); Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "parameters"); }
    [Fact] public void InvalidCoordinatesFail() { var p = Valid(); p.Nodes[0].Position.X = double.NaN; Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "position"); }
    [Fact] public void OrdinaryCycleFails() { var p = ValidWith(WorkflowEngineTests.Action("a"), WorkflowEngineTests.Action("b")); p.Edges[^1].Target = "a"; Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "cycle"); }
    [Fact] public async Task SuppliedSampleIsValid() { await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory,"samples","basic-example.gflow")); var project = await JsonSerializer.DeserializeAsync<AutomationProject>(stream,ProjectJson.Options); Assert.NotNull(project); var script = Assert.Single(project.Scripts); var workflow = new AutomationProject { SchemaVersion = project.SchemaVersion, Project = project.Project, Variables = script.Variables, Nodes = script.Nodes, Edges = script.Edges }; Assert.True(_validator.Validate(workflow).IsValid, string.Join(" | ",_validator.Validate(workflow).Errors.Select(e=>e.Message))); }
    [Fact] public void InvalidWindowScopePatternFails() { var scope = WorkflowEngineTests.Node("scope", NodeTypes.WindowScope, ("titlePattern", "["), ("outputVariable", "windowHandles")); var p = ValidWith(scope); Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "parameters" && e.NodeId == "scope"); }
    [Fact] public void InvalidInlineColorParametersFail() { var color = WorkflowEngineTests.Node("color", NodeTypes.ColorCondition, ("targetColor", "not-a-color")); var p = ValidWith(color); Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "detector_parameters" && e.NodeId == "color"); }
    [Fact] public void LegacyMouseClickWithoutModeRemainsValid() { var click = WorkflowEngineTests.Node("click", NodeTypes.MouseClick, ("x", 10), ("y", 20)); Assert.True(_validator.Validate(ValidWith(click)).IsValid); }
    [Fact] public void LegacyMouseClickModeIsIgnored() { var click = WorkflowEngineTests.Node("click", NodeTypes.MouseClick, ("clickMode", "unsafe"), ("x", 10), ("y", 20)); Assert.True(_validator.Validate(ValidWith(click)).IsValid); }
    [Fact] public void NumericMouseButtonOutsideEnumFails() { var click = WorkflowEngineTests.Node("click", NodeTypes.MouseClick, ("button", "999"), ("x", 10), ("y", 20)); Assert.Contains(_validator.Validate(ValidWith(click)).Errors, e => e.Code == "parameters" && e.NodeId == "click"); }
    [Fact] public void BackgroundMouseClickCountAboveSafetyLimitFails() { var click = WorkflowEngineTests.Node("click", NodeTypes.MouseClick, ("x", 10), ("y", 20), ("clickCount", 101)); var project = ValidWith(click); project.InteractionMode = ScriptInteractionMode.Background; Assert.Contains(_validator.Validate(project).Errors, e => e.Code == "parameters" && e.NodeId == "click"); }
    [Fact] public void ForegroundMouseClickKeepsExistingLargeCountCompatibility() { var click = WorkflowEngineTests.Node("click", NodeTypes.MouseClick, ("x", 10), ("y", 20), ("clickCount", 101)); var project = ValidWith(click); project.InteractionMode = ScriptInteractionMode.Foreground; Assert.True(_validator.Validate(project).IsValid); }
    [Fact] public void LegacyInputModeIsIgnored() { var move = WorkflowEngineTests.Node("move", NodeTypes.MouseMove, ("inputMode", "unsafe"), ("x", 10), ("y", 20)); Assert.True(_validator.Validate(ValidWith(move)).IsValid); }
    [Fact] public void BackgroundMouseScrollOutsideMessageRangeFails() { var scroll = WorkflowEngineTests.Node("scroll", NodeTypes.MouseScroll, ("x", 10), ("y", 20), ("delta", 40000)); var project = ValidWith(scroll); project.InteractionMode = ScriptInteractionMode.Background; Assert.Contains(_validator.Validate(project).Errors, e => e.Code == "parameters" && e.NodeId == "scroll"); }
    [Fact] public void ForegroundMouseScrollKeepsLargeDeltaCompatibility() { var scroll = WorkflowEngineTests.Node("scroll", NodeTypes.MouseScroll, ("x", 10), ("y", 20), ("delta", 40000)); var project = ValidWith(scroll); project.InteractionMode = ScriptInteractionMode.Foreground; Assert.True(_validator.Validate(project).IsValid); }
    [Fact]
    public void BackgroundTargetScriptLimitsAreValidatedBeforeJumpExecution()
    {
        var jump = WorkflowEngineTests.Node(
            "jump",
            NodeTypes.Jump,
            ("scriptId", "target"),
            ("actionId", "target-click"));
        var project = ValidWith(jump);
        var source = new AutomationScript { Id = "source", Name = "源脚本", Nodes = project.Nodes, Edges = project.Edges };
        var targetClick = WorkflowEngineTests.Node("target-click", NodeTypes.MouseClick, ("x", 10), ("y", 20), ("clickCount", 101));
        var target = new AutomationScript
        {
            Id = "target",
            Name = "目标脚本",
            InteractionMode = ScriptInteractionMode.Background,
            Nodes = [targetClick]
        };
        project.Scripts = [source, target];
        project.ActiveScriptId = source.Id;

        var result = _validator.Validate(project);

        Assert.Contains(result.Errors, error => error.Code == "parameters" && error.NodeId == targetClick.Id);
    }
    [Fact]
    public void UnrelatedDraftScriptDoesNotBlockActiveScript()
    {
        var project = Valid();
        var source = new AutomationScript { Id = "source", Name = "源脚本", Nodes = project.Nodes, Edges = project.Edges };
        var draft = new AutomationScript
        {
            Id = "draft",
            Name = "草稿脚本",
            Nodes = [WorkflowEngineTests.Node("unfinished", NodeTypes.RunProgram, ("path", ""))]
        };
        project.Scripts = [source, draft];
        project.ActiveScriptId = source.Id;

        Assert.True(_validator.Validate(project).IsValid);
    }
    [Fact] public void DuplicateScriptIdsFail() { var p = Valid(); p.Scripts = [new() { Id = "same" }, new() { Id = "same" }]; Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "script_id"); }
    [Fact] public void MissingActiveScriptFails() { var p = Valid(); p.Scripts = [new() { Id = "one" }]; p.ActiveScriptId = "missing"; Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "active_script"); }
    [Fact] public void ActiveScriptWithoutScriptsFails() { var p = Valid(); p.ActiveScriptId = "missing"; Assert.Contains(_validator.Validate(p).Errors, e => e.Code == "active_script"); }
    [Fact]
    public void EmptyEdgeEndpointsAreAggregatedAsValidationErrors()
    {
        var p = Valid();
        p.Edges[0].Source = null!;
        p.Edges[0].Target = null!;

        var result = _validator.Validate(p);

        Assert.Contains(result.Errors, error => error.Code == "edge_source");
        Assert.Contains(result.Errors, error => error.Code == "edge_target");
    }
    [Fact]
    public void EmptyNodeIdIsReportedWithoutInterruptingValidation()
    {
        var p = Valid();
        p.Nodes[0].Id = null!;

        var result = _validator.Validate(p);

        Assert.Contains(result.Errors, error => error.Code == "node_id");
    }
    private static Contracts.Workflows.AutomationProject Valid() => ValidWith();
    private static Contracts.Workflows.AutomationProject ValidWith(params Contracts.Workflows.WorkflowNode[] middle)
    {
        var p = WorkflowEngineTests.NewProject(); var start = WorkflowEngineTests.Node("start",NodeTypes.Start); var end = WorkflowEngineTests.Node("end",NodeTypes.End); p.Nodes = [start, .. middle, end];
        var nodes = p.Nodes; for (var i=0;i<nodes.Count-1;i++) p.Edges.Add(WorkflowEngineTests.Edge(nodes[i],"output",nodes[i+1])); return p;
    }
}
