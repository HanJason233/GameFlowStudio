using System.Windows;

namespace GameFlowStudio.Desktop.Views;

/// <summary>
/// 将 Win32 物理屏幕像素转换为显示中的 WPF 拾取窗口使用的设备无关坐标空间。
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
