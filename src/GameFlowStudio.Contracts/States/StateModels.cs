using System.Text.Json;
using GameFlowStudio.Contracts.Enums;

namespace GameFlowStudio.Contracts.States;

/// <summary>逻辑判断使用的检测器定义。</summary>
public sealed class DetectorDefinition
{
    public string Id { get; set; } = $"detector-{Guid.NewGuid():N}";
    public string Name { get; set; } = "新检测器";
    public DetectorType Type { get; set; } = DetectorType.Color;
    public bool Enabled { get; set; } = true;
    public Dictionary<string, JsonElement> Parameters { get; set; } = new(StringComparer.Ordinal);
}
