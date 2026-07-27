using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.States;

namespace GameFlowStudio.Workflow.Detectors;

public sealed class StateConditionEvaluator(
    IStateDetectorService detectorService,
    IDesktopCaptureService capture,
    IImageTemplateProvider? images = null,
    IWindowAutomationService? windows = null) : IStateConditionEvaluator
{
    public async Task<StateConditionEvaluation> EvaluateAsync(
        IReadOnlyList<DetectorDefinition> detectors,
        IReadOnlyList<nint> windowHandles,
        CancellationToken cancellationToken)
    {
        var context = new DetectorEvaluationContext { Capture = capture, Images = images, Windows = windows, WindowHandles = windowHandles };
        var results = new Dictionary<string, DetectorEvaluationResult>(StringComparer.Ordinal);
        var matched = detectors.Count > 0;
        foreach (var detector in detectors)
        {
            var result = await detectorService.EvaluateAsync(detector, context, cancellationToken).ConfigureAwait(false);
            results[detector.Id] = result;
            if (!result.Supported || result.State != 1) matched = false;
        }
        return new(matched, results);
    }
}
