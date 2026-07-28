using System.Windows;

namespace GameFlowStudio.Desktop.Views;

/// <summary>
/// Converts Win32 physical screen pixels to the device-independent coordinate space
/// used by a displayed WPF picker window.
/// </summary>
internal static class PhysicalScreenCoordinateMapper
{
    public static Point ToLocalDip(Window window, int physicalX, int physicalY)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (PresentationSource.FromVisual(window)?.CompositionTarget is null)
            throw new InvalidOperationException("拾取窗口尚未连接到可视化源，无法转换屏幕坐标。");

        return window.PointFromScreen(new Point(physicalX, physicalY));
    }
}
