using System.Globalization;
using System.Text.Json;
using GameFlowStudio.Contracts.States;

namespace GameFlowStudio.Workflow.Detectors;

internal static class DetectorParameterReader
{
    public static string String(DetectorDefinition detector, string name, string? fallback = null)
    {
        if (!detector.Parameters.TryGetValue(name, out var value))
        {
            if (fallback is not null) return fallback;
            throw new InvalidOperationException($"检测器“{detector.Name}”缺少参数 {name}。");
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    public static int Int(DetectorDefinition detector, string name, int? fallback = null)
    {
        if (!detector.Parameters.TryGetValue(name, out var value))
        {
            if (fallback.HasValue) return fallback.Value;
            throw new InvalidOperationException($"检测器“{detector.Name}”缺少参数 {name}。");
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), CultureInfo.InvariantCulture, out number)) return number;
        throw new InvalidOperationException($"检测器“{detector.Name}”的参数 {name} 必须是整数。");
    }

    public static double Double(DetectorDefinition detector, string name, double fallback)
    {
        if (!detector.Parameters.TryGetValue(name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        throw new InvalidOperationException($"检测器“{detector.Name}”的参数 {name} 必须是数字。");
    }
}
