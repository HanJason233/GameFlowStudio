using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.States;

namespace GameFlowStudio.Workflow.Detectors;

public sealed class StateConditionEvaluator(
    IStateDetectorService detectorService,
    IDesktopCaptureService capture,
    IImageTemplateProvider? images = null) : IStateConditionEvaluator
{
    public async Task<StateConditionEvaluation> EvaluateAsync(
        IReadOnlyList<DetectorDefinition> detectors,
        IReadOnlyList<StateDetectorRule> rules,
        CancellationToken cancellationToken)
    {
        var detectorMap = detectors.ToDictionary(detector => detector.Id, StringComparer.Ordinal);
        var context = new DetectorEvaluationContext { Capture = capture, Images = images };
        var results = new Dictionary<string, DetectorEvaluationResult>(StringComparer.Ordinal);
        var matched = rules.Count > 0;
        foreach (var rule in rules)
        {
            if (rule.State is not (0 or 1)) throw new InvalidOperationException($"检测器规则 {rule.DetectorId} 的状态必须是 0 或 1。");
            if (!detectorMap.TryGetValue(rule.DetectorId, out var detector))
                throw new InvalidOperationException($"找不到检测器 {rule.DetectorId}。");
            var result = await detectorService.EvaluateAsync(detector, context, cancellationToken).ConfigureAwait(false);
            results[rule.DetectorId] = result;
            if (!result.Supported || result.State != rule.State) matched = false;
        }
        return new(matched, results);
    }
}
