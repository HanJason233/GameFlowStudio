using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Executors;

internal enum InteractionMode
{
    Foreground,
    Background
}

/// <summary>统一读取输入、窗口读取和窗口动作的前后台模式，旧节点缺省保持前台。</summary>
internal static class InteractionModeParameterReader
{
    public static InteractionMode Read(WorkflowNode node, string parameterName)
    {
        var value = NodeParameterReader.String(node, parameterName, "foreground").Trim();
        return value.ToLowerInvariant() switch
        {
            "" or "foreground" => InteractionMode.Foreground,
            "background" => InteractionMode.Background,
            _ => throw new InvalidOperationException(
                $"{(parameterName == "clickMode" ? "点击模式" : $"参数 {parameterName}")}只支持 foreground 或 background，当前值为「{value}」。")
        };
    }

    public static bool IsBackground(WorkflowNode node, string parameterName) =>
        Read(node, parameterName) == InteractionMode.Background;
}
