using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Engine;

/// <summary>集中定义脚本调度与跨脚本跳转的兼容规则。</summary>
internal static class ScriptExecutionCompatibility
{
    public static bool IsOperationFlowValid(
        ScriptInteractionMode interactionMode,
        WindowExecutionMode operationFlow) => interactionMode switch
        {
            ScriptInteractionMode.Foreground => operationFlow is
                WindowExecutionMode.Serial or WindowExecutionMode.PseudoParallel,
            ScriptInteractionMode.Background => operationFlow == WindowExecutionMode.Parallel,
            _ => false
        };

    public static bool TryValidateJump(
        AutomationScript source,
        AutomationScript target,
        out string error)
    {
        if (source.InteractionMode != target.InteractionMode)
        {
            error = "来源脚本与目标脚本的前台/后台模式不同。";
            return false;
        }

        if (source.WindowScope.ExecutionMode != target.WindowScope.ExecutionMode)
        {
            error = "来源脚本与目标脚本的操作流程不同。";
            return false;
        }

        if (source.WindowScope.Enabled != target.WindowScope.Enabled)
        {
            error = "来源脚本与目标脚本的窗口作用域启用状态不同。";
            return false;
        }

        if (!string.Equals(
                NormalizeTitlePattern(source.WindowScope.TitlePattern),
                NormalizeTitlePattern(target.WindowScope.TitlePattern),
                StringComparison.Ordinal))
        {
            error = "来源脚本与目标脚本的窗口标题规则不同。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string NormalizeTitlePattern(string? titlePattern) =>
        string.IsNullOrWhiteSpace(titlePattern) ? ".*" : titlePattern;
}
