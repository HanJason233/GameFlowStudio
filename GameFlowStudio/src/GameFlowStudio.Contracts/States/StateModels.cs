using System.Text.Json;
using GameFlowStudio.Contracts.Enums;

namespace GameFlowStudio.Contracts.States;

/// <summary>一个可以被脚本逻辑判断引用的状态检测器。</summary>
public sealed class DetectorDefinition
{
    public string Id { get; set; } = $"detector-{Guid.NewGuid():N}";
    public string Name { get; set; } = "新检测器";
    public DetectorType Type { get; set; } = DetectorType.Color;
    public bool Enabled { get; set; } = true;
    public Dictionary<string, JsonElement> Parameters { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>逻辑判断中一个检测器的期望状态，State 只能是 0 或 1。</summary>
public sealed class StateDetectorRule
{
    public string DetectorId { get; set; } = string.Empty;
    public int State { get; set; } = 1;
}
