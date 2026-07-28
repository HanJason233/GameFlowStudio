using System.Text.Json;
using System.Text.Json.Serialization;
using GameFlowStudio.Contracts.Enums;

namespace GameFlowStudio.Contracts.Workflows;

/// <summary>一个可保存和执行的 GameFlow 项目。</summary>
public sealed class AutomationProject
{
    public string SchemaVersion { get; set; } = "1.4";
    public ProjectMetadata Project { get; set; } = new();
    public List<AutomationScript> Scripts { get; set; } = [];

    // Runtime-only view of the active script. The project file persists scripts instead.
    [JsonIgnore]
    public Dictionary<string, ProjectVariable> Variables { get; set; } = new(StringComparer.Ordinal);
    [JsonIgnore]
    public List<WorkflowNode> Nodes { get; set; } = [];
    [JsonIgnore]
    public List<WorkflowEdge> Edges { get; set; } = [];
    [JsonIgnore]
    public string? ActiveScriptId { get; set; }
    [JsonIgnore]
    public ScriptWindowScope? WindowScope { get; set; }
    [JsonIgnore]
    public int? DefaultNodeDelayMs { get; set; }

    /// <summary>
    /// Synchronizes the runtime-only flat view used by the editor and executor
    /// with one persisted script.
    /// </summary>
    public void SetActiveScript(AutomationScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        ActiveScriptId = script.Id;
        Variables = script.Variables;
        Nodes = script.Nodes;
        Edges = script.Edges;
        WindowScope = script.WindowScope;
        DefaultNodeDelayMs = script.DefaultNodeDelayMs;
    }
}

public sealed class AutomationScript
{
    public string Id { get; set; } = "1";
    public string Name { get; set; } = "主流程";
    public ScriptWindowScope WindowScope { get; set; } = new();
    public int DefaultNodeDelayMs { get; set; }
    public Dictionary<string, ProjectVariable> Variables { get; set; } = new(StringComparer.Ordinal);
    public List<WorkflowNode> Nodes { get; set; } = [];
    public List<WorkflowEdge> Edges { get; set; } = [];
}

public sealed class ScriptWindowScope
{
    public bool Enabled { get; set; }
    public string TitlePattern { get; set; } = ".*";
    public WindowExecutionMode ExecutionMode { get; set; } = WindowExecutionMode.FirstOnly;
}

/// <summary>当前脚本与 React 编辑器之间传输的平面工作流快照。</summary>
public sealed class WorkflowDocument
{
    public string SchemaVersion { get; set; } = "1.4";
    public ProjectMetadata Project { get; set; } = new();
    public Dictionary<string, ProjectVariable> Variables { get; set; } = new(StringComparer.Ordinal);
    public List<WorkflowNode> Nodes { get; set; } = [];
    public List<WorkflowEdge> Edges { get; set; } = [];
}

public sealed class ProjectMetadata
{
    public string Id { get; set; } = $"project-{Guid.NewGuid():N}";
    public string Name { get; set; } = "未命名项目";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProjectVariable
{
    [JsonConverter(typeof(JsonStringEnumConverter<VariableType>))]
    public VariableType Type { get; set; }
    public JsonElement Value { get; set; }
}

public sealed class WorkflowNode
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public NodePosition Position { get; set; } = new();
    public WorkflowNodeData Data { get; set; } = new();
}

public sealed class NodePosition
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class WorkflowNodeData
{
    public string DisplayName { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> Parameters { get; set; } = new(StringComparer.Ordinal);
}

public sealed class WorkflowEdge
{
    public string Id { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SourceHandle { get; set; } = "output";
    public string Target { get; set; } = string.Empty;
    public string TargetHandle { get; set; } = "input";
}

/// <summary>项目 JSON 的唯一序列化配置。</summary>
public static class ProjectJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
