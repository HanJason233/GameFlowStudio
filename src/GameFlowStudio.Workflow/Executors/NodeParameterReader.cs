using System.Globalization;
using System.Text.Json;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Executors;

internal static class NodeParameterReader
{
    public static string String(WorkflowNode node, string name, string? fallback = null)
    {
        if (!node.Data.Parameters.TryGetValue(name, out var value))
        {
            if (fallback is not null) return fallback;
            throw new InvalidOperationException($"节点“{node.Data.DisplayName}”缺少参数 {name}。");
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    public static int Int(WorkflowNode node, string name, int? fallback = null)
    {
        if (!node.Data.Parameters.TryGetValue(name, out var value))
        {
            if (fallback.HasValue) return fallback.Value;
            throw new InvalidOperationException($"节点“{node.Data.DisplayName}”缺少参数 {name}。");
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), CultureInfo.InvariantCulture, out number)) return number;
        throw new InvalidOperationException($"节点“{node.Data.DisplayName}”的参数 {name} 必须是整数。");
    }

    public static bool Bool(WorkflowNode node, string name, bool? fallback = null)
    {
        if (!node.Data.Parameters.TryGetValue(name, out var value))
        {
            if (fallback.HasValue) return fallback.Value;
            throw new InvalidOperationException($"节点“{node.Data.DisplayName}”缺少参数 {name}。");
        }
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var boolean)) return boolean;
        throw new InvalidOperationException($"节点“{node.Data.DisplayName}”的参数 {name} 必须是布尔值。");
    }

    public static bool Has(WorkflowNode node, string name) =>
        node.Data.Parameters.TryGetValue(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    public static JsonElement Element(WorkflowNode node, string name)
    {
        if (!node.Data.Parameters.TryGetValue(name, out var value))
            throw new InvalidOperationException($"节点“{node.Data.DisplayName}”缺少参数 {name}。");
        return value;
    }
}
