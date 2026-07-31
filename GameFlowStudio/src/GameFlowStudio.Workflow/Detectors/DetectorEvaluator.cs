using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.States;

namespace GameFlowStudio.Workflow.Detectors;

public sealed class DetectorEvaluator(
    IStateDetectorService detectorService,
    IDesktopCaptureService capture,
    IImageTemplateProvider? images = null,
    IWindowAutomationService? windows = null,
    IWindowCaptureService? windowCapture = null) : IDetectorEvaluator
{
    public async Task<DetectorEvaluationResult> EvaluateAsync(
        DetectorDefinition detector,
        IReadOnlyList<nint> windowHandles,
        CancellationToken cancellationToken)
    {
        var context = new DetectorEvaluationContext
        {
            Capture = capture,
            WindowCapture = windowCapture,
            Images = images,
            Windows = windows,
            WindowHandles = windowHandles
        };
        return await detectorService.EvaluateAsync(detector, context, cancellationToken).ConfigureAwait(false);
    }
}
