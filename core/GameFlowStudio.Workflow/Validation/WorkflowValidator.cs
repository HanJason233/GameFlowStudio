using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Workflow.Validation;

/// <summary>按独立规则验证工作流，并合并全部错误。</summary>
public sealed class WorkflowValidator : IWorkflowValidator
{
    private readonly IReadOnlyList<IWorkflowValidationRule> _rules;

    public WorkflowValidator()
    {
        var parameterValidator = new NodeParameterValidator();
        _rules =
        [
            new ProjectStructureRule(parameterValidator),
            new EdgeReferenceRule(),
            new NodeConnectionRule(),
            new GraphTopologyRule(),
            new JumpTargetRule(),
            new StateConfigurationRule()
        ];
    }

    internal WorkflowValidator(IEnumerable<IWorkflowValidationRule> rules) => _rules = rules.ToList();

    public WorkflowValidationResult Validate(AutomationProject workflow)
    {
        var result = new WorkflowValidationResult();
        var context = new WorkflowValidationContext(workflow);
        foreach (var rule in _rules) rule.Validate(context, result);
        return result;
    }
}
