using System.Text.Json;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Storage.Serialization;

/// <summary>
/// 定义 .gflow 文档可信的持久化边界。
/// 编辑器桥接消息继续使用更宽松的共享 JSON 配置。
/// </summary>
internal static class ProjectDocumentSerializer
{
    internal const string CurrentSchemaVersion = "1.7";

    private static readonly JsonSerializerOptions StorageOptions = new(ProjectJson.Options);

    public static async Task WriteAsync(
        Stream stream,
        AutomationProject project,
        CancellationToken cancellationToken)
    {
        Validate(project);
        await JsonSerializer.SerializeAsync(stream, project, StorageOptions, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<AutomationProject> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        ValidateEnvelope(document.RootElement);

        var project = document.RootElement.Deserialize<AutomationProject>(StorageOptions)
            ?? throw new InvalidDataException("项目文件内容为空。");
        Validate(project);
        return project;
    }

    public static void Validate(AutomationProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ValidateShape(project);
    }

    public static AutomationProject CreateSnapshot(AutomationProject project, DateTimeOffset updatedAt)
    {
        Validate(project);
        var snapshot = JsonSerializer.SerializeToElement(project, StorageOptions)
            .Deserialize<AutomationProject>(StorageOptions)
            ?? throw new InvalidDataException("无法创建项目保存快照。");
        snapshot.Project.UpdatedAt = updatedAt;
        return snapshot;
    }

    private static void ValidateEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("项目文件根节点必须是 JSON 对象。");

        if (!root.TryGetProperty("schemaVersion", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(versionElement.GetString()))
            throw new InvalidDataException("项目文件缺少必需的 schemaVersion。");

        var version = versionElement.GetString();
        if (!string.Equals(version, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"不支持的项目架构版本：{version}。当前版本仅支持 {CurrentSchemaVersion}；文件未被打开以避免保存时丢失数据。");

        _ = RequireProperty(root, "project", JsonValueKind.Object, "项目文件");
        var scripts = RequireProperty(root, "scripts", JsonValueKind.Array, "项目文件");
        var scriptIndex = 0;
        foreach (var script in scripts.EnumerateArray())
        {
            var scriptPath = $"scripts[{scriptIndex}]";
            RequireObject(script, scriptPath);
            var windowScope = RequireProperty(script, "windowScope", JsonValueKind.Object, scriptPath);
            _ = RequireProperty(windowScope, "executionMode", JsonValueKind.String, $"{scriptPath}.windowScope");
            _ = RequireProperty(script, "interactionMode", JsonValueKind.String, scriptPath);
            _ = RequireProperty(script, "variables", JsonValueKind.Object, scriptPath);
            var nodes = RequireProperty(script, "nodes", JsonValueKind.Array, scriptPath);
            var edges = RequireProperty(script, "edges", JsonValueKind.Array, scriptPath);

            var nodeIndex = 0;
            foreach (var node in nodes.EnumerateArray())
            {
                var nodePath = $"{scriptPath}.nodes[{nodeIndex}]";
                RequireObject(node, nodePath);
                _ = RequireProperty(node, "position", JsonValueKind.Object, nodePath);
                var data = RequireProperty(node, "data", JsonValueKind.Object, nodePath);
                _ = RequireProperty(data, "parameters", JsonValueKind.Object, $"{nodePath}.data");
                nodeIndex++;
            }

            var edgeIndex = 0;
            foreach (var edge in edges.EnumerateArray())
            {
                RequireObject(edge, $"{scriptPath}.edges[{edgeIndex}]");
                edgeIndex++;
            }

            scriptIndex++;
        }
    }

    private static JsonElement RequireProperty(
        JsonElement owner,
        string propertyName,
        JsonValueKind expectedKind,
        string ownerPath)
    {
        if (!owner.TryGetProperty(propertyName, out var value))
            throw new InvalidDataException($"{ownerPath} 缺少必需的 {propertyName}。");
        if (value.ValueKind != expectedKind)
            throw new InvalidDataException($"{ownerPath}.{propertyName} 必须是 {Describe(expectedKind)}。");
        return value;
    }

    private static void RequireObject(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path} 必须是 JSON 对象。");
    }

    private static string Describe(JsonValueKind kind) =>
        kind == JsonValueKind.Array ? "JSON 数组" : "JSON 对象";

    private static void ValidateShape(AutomationProject project)
    {
        if (!string.Equals(project.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"项目架构版本必须为 {CurrentSchemaVersion}。");
        if (project.Project is null)
            throw new InvalidDataException("项目文件的 project 不能为空。");
        if (project.Project.Id is null)
            throw new InvalidDataException("项目文件的 project.id 不能为空。");
        if (project.Project.Name is null)
            throw new InvalidDataException("项目文件的 project.name 不能为空。");
        if (project.Scripts is null)
            throw new InvalidDataException("项目文件的 scripts 不能为空。");

        for (var scriptIndex = 0; scriptIndex < project.Scripts.Count; scriptIndex++)
        {
            var script = project.Scripts[scriptIndex]
                ?? throw new InvalidDataException($"项目文件的 scripts[{scriptIndex}] 不能为空。");
            if (script.Id is null)
                throw new InvalidDataException($"脚本 {scriptIndex + 1} 的 id 不能为空。");
            if (script.Name is null)
                throw new InvalidDataException($"脚本 {scriptIndex + 1} 的 name 不能为空。");
            if (script.WindowScope is null)
                throw new InvalidDataException($"脚本 {scriptIndex + 1} 的 windowScope 不能为空。");
            if (script.WindowScope.TitlePattern is null)
                throw new InvalidDataException($"脚本 {scriptIndex + 1} 的 windowScope.titlePattern 不能为空。");
            if (!Enum.IsDefined(script.WindowScope.ExecutionMode))
                throw new InvalidDataException($"脚本 {scriptIndex + 1} 的 windowScope.executionMode 无效。");
            if (!Enum.IsDefined(script.InteractionMode))
                throw new InvalidDataException($"脚本 {scriptIndex + 1} 的 interactionMode 无效。");
            if (!IsValidExecutionMode(script.InteractionMode, script.WindowScope.ExecutionMode))
                throw new InvalidDataException(
                    $"脚本 {scriptIndex + 1} 的 interactionMode 与 windowScope.executionMode 组合无效：" +
                    "前台仅支持 serial 或 pseudoParallel，后台仅支持 parallel。");
            if (script.Variables is null)
                throw new InvalidDataException($"脚本 {scriptIndex + 1} 的 variables 不能为空。");
            if (script.Nodes is null)
                throw new InvalidDataException($"脚本 {scriptIndex + 1} 的 nodes 不能为空。");
            if (script.Edges is null)
                throw new InvalidDataException($"脚本 {scriptIndex + 1} 的 edges 不能为空。");

            foreach (var (name, variable) in script.Variables)
                if (variable is null)
                    throw new InvalidDataException($"脚本 {scriptIndex + 1} 的变量 {name} 不能为空。");

            for (var nodeIndex = 0; nodeIndex < script.Nodes.Count; nodeIndex++)
            {
                var node = script.Nodes[nodeIndex]
                    ?? throw new InvalidDataException($"脚本 {scriptIndex + 1} 的 nodes[{nodeIndex}] 不能为空。");
                if (node.Id is null)
                    throw new InvalidDataException($"节点 {nodeIndex + 1} 的 id 不能为空。");
                if (node.Type is null)
                    throw new InvalidDataException($"节点 {nodeIndex + 1} 的 type 不能为空。");
                if (node.Position is null)
                    throw new InvalidDataException($"节点 {nodeIndex + 1} 的 position 不能为空。");
                if (node.Data is null)
                    throw new InvalidDataException($"节点 {nodeIndex + 1} 的 data 不能为空。");
                if (node.Data.DisplayName is null)
                    throw new InvalidDataException($"节点 {nodeIndex + 1} 的 displayName 不能为空。");
                if (node.Data.Parameters is null)
                    throw new InvalidDataException($"节点 {nodeIndex + 1} 的 parameters 不能为空。");
            }

            for (var edgeIndex = 0; edgeIndex < script.Edges.Count; edgeIndex++)
            {
                var edge = script.Edges[edgeIndex]
                    ?? throw new InvalidDataException($"脚本 {scriptIndex + 1} 的 edges[{edgeIndex}] 不能为空。");
                if (edge.Id is null)
                    throw new InvalidDataException($"连接 {edgeIndex + 1} 的 id 不能为空。");
                if (edge.Source is null)
                    throw new InvalidDataException($"连接 {edgeIndex + 1} 的 source 不能为空。");
                if (edge.SourceHandle is null)
                    throw new InvalidDataException($"连接 {edgeIndex + 1} 的 sourceHandle 不能为空。");
                if (edge.Target is null)
                    throw new InvalidDataException($"连接 {edgeIndex + 1} 的 target 不能为空。");
                if (edge.TargetHandle is null)
                    throw new InvalidDataException($"连接 {edgeIndex + 1} 的 targetHandle 不能为空。");
            }
        }
    }

    private static bool IsValidExecutionMode(
        ScriptInteractionMode interactionMode,
        WindowExecutionMode executionMode) => interactionMode switch
        {
            ScriptInteractionMode.Foreground => executionMode is
                WindowExecutionMode.Serial or WindowExecutionMode.PseudoParallel,
            ScriptInteractionMode.Background => executionMode == WindowExecutionMode.Parallel,
            _ => false
        };
}
