using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Executors;

internal static class ActionExecutionLogging
{
    public static void Write(WorkflowExecutionContext context, WorkflowNode node, string message, WorkflowLogLevel level = WorkflowLogLevel.Information) =>
        context.Log(new(DateTimeOffset.Now, level, context.Workflow.Project.Id, node.Id, node.Data.DisplayName, message));

    public static string Outcome(bool matched) => matched ? "匹配" : "不匹配";

}
