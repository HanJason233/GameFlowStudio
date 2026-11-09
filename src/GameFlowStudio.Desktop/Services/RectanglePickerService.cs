using GameFlowStudio.Desktop.Interaction;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Desktop.Views;

namespace GameFlowStudio.Desktop.Services;

public readonly record struct RectangleCaptureResult(CaptureRegion Region, CapturedFrame Frame);

public interface IRectanglePickerService
{
    Task<CaptureRegion?> PickAsync(WindowBounds? relativeTo = null);
    Task<RectangleCaptureResult?> PickAndCaptureAsync(WindowBounds? relativeTo = null);
}

public sealed class RectanglePickerService(
    CursorPositionService cursor,
    IDesktopCaptureService capture) : IRectanglePickerService
{
    public Task<CaptureRegion?> PickAsync(WindowBounds? relativeTo = null) =>
        RunPickerAsync(relativeTo, static region => Task.FromResult(region));

    public Task<RectangleCaptureResult?> PickAndCaptureAsync(WindowBounds? relativeTo = null) =>
        RunPickerAsync(relativeTo, async region =>
        {
            var frame = await capture.CaptureAsync(region, CancellationToken.None).ConfigureAwait(true);
            return new RectangleCaptureResult(region, frame);
        });

    private async Task<TResult?> RunPickerAsync<TResult>(
        WindowBounds? relativeTo,
        Func<CaptureRegion, Task<TResult>> completeSelection)
        where TResult : struct
    {
        ArgumentNullException.ThrowIfNull(completeSelection);
        using var session = DesktopPickerSession.Start();
        await session.WaitForDesktopUpdateAsync();
        var picker = new RectanglePickerWindow(cursor, capture, relativeTo);
        picker.ShowDialog();
        if (picker.SelectedRegion is not { } region) return null;

        await session.WaitForDesktopUpdateAsync();
        var result = new CaptureRegion(region.X, region.Y, region.Width, region.Height);
        return await completeSelection(result).ConfigureAwait(true);
    }
}

