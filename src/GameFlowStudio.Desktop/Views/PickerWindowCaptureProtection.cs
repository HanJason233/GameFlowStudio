using System.Windows;
using System.Windows.Interop;
using GameFlowStudio.Desktop.Interaction;

namespace GameFlowStudio.Desktop.Views;

internal static class PickerWindowCaptureProtection
{
    public static void Attach(Window window, Action<bool>? onApplied = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var applied = ScreenCaptureComposition.TryExcludeWindow(handle);
            onApplied?.Invoke(applied);
        };
    }
}
