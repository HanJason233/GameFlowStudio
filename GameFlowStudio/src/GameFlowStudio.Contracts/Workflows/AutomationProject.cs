using System.Text.Json;
using System.Text.Json.Serialization;
using GameFlowStudio.Contracts.Enums;

namespace GameFlowStudio.Contracts.Workflows;

/// <summary>一个可保存和执行的 GameFlow 项目。</summary>
public sealed class AutomationProject
{
    public string SchemaVersion { get; set; } = "1.8";
    public ProjectMetadata Project { get; set; } = new();
    public List<AutomationScript> Scripts { get; set; } = [];

    // 仅运行时使用的活动脚本视图；项目文件实际保存的是脚本集合。
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
    [JsonIgnore]
    public ScriptInteractionMode InteractionMode { get; set; } = ScriptInteractionMode.Foreground;

    /// <summary>
    /// 将编辑器和执行器使用的运行时平面视图同步到一个持久化脚本。
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
        InteractionMode = script.InteractionMode;
    }
}

public sealed class AutomationScript
{
    public const int DefaultNodeDelayMilliseconds = 2000;
    public string Id { get; set; } = "1";
    public string Name { get; set; } = "主流程";
    public ScriptWindowScope WindowScope { get; set; } = new();
    public ScriptInteractionMode InteractionMode { get; set; } = ScriptInteractionMode.Foreground;
    public int DefaultNodeDelayMs { get; set; } = DefaultNodeDelayMilliseconds;
    public Dictionary<string, ProjectVariable> Variables { get; set; } = new(StringComparer.Ordinal);
    public List<WorkflowNode> Nodes { get; set; } = [];
    public List<WorkflowEdge> Edges { get; set; } = [];
    public List<WorkflowGroup> Groups { get; set; } = [];
}

public sealed class ScriptWindowScope
{
    public bool Enabled { get; set; }
    public string MatchPattern { get; set; } = ".*";
    public string ExcludePattern { get; set; } = string.Empty;
    public WindowExecutionMode ExecutionMode { get; set; } = WindowExecutionMode.Serial;
}

/// <summary>当前脚本与 React 编辑器之间传输的平面工作流快照。</summary>
public sealed class WorkflowDocument
{
    public string SchemaVersion { get; set; } = "1.8";
    public ProjectMetadata Project { get; set; } = new();
    public Dictionary<string, ProjectVariable> Variables { get; set; } = new(StringComparer.Ordinal);
    public List<WorkflowNode> Nodes { get; set; } = [];
    public List<WorkflowEdge> Edges { get; set; } = [];
    public List<WorkflowGroup> Groups { get; set; } = [];
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

/// <summary>编辑器画布中的卡片组，仅用于组织和折叠节点，不参与运行时执行。</summary>
public sealed class WorkflowGroup
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = "卡片组";
    public List<string> NodeIds { get; set; } = [];
    public NodePosition Position { get; set; } = new();
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 260;
    public bool Collapsed { get; set; }
    public List<WorkflowEdge> ExternalEdges { get; set; } = [];
}

/// <summary>项目 JSON 的唯一序列化配置。</summary>
public static class ProjectJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
