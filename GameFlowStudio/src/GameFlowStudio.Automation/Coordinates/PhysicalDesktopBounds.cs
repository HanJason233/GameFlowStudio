using System.ComponentModel;
using System.Runtime.InteropServices;
using GameFlowStudio.Automation.Interop;
using GameFlowStudio.Contracts.Results;

namespace GameFlowStudio.Automation.Coordinates;

/// <summary>查询并合并显示器在虚拟桌面物理像素坐标系中的边界。</summary>
internal static class PhysicalDesktopBounds
{
    public static CaptureRegion GetCurrent()
    {
        using var dpiScope = ThreadDpiAwarenessScope.EnterPerMonitorAware();
        var monitors = new List<CaptureRegion>();
        NativeMethods.MonitorEnumProc callback = (nint monitor, nint monitorDeviceContext, ref RECT rectangle, nint data) =>
        {
            var width = (long)rectangle.Right - rectangle.Left;
            var height = (long)rectangle.Bottom - rectangle.Top;
            if (width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue)
                monitors.Add(new CaptureRegion(rectangle.Left, rectangle.Top, (int)width, (int)height));
            return true;
        };

        if (!NativeMethods.EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法枚举显示器物理边界。");

        return Union(monitors);
    }

    internal static CaptureRegion Union(IEnumerable<CaptureRegion> monitorBounds)
    {
        ArgumentNullException.ThrowIfNull(monitorBounds);
        var validBounds = monitorBounds.Where(region => region.IsValid).ToArray();
        if (validBounds.Length == 0)
            throw new InvalidOperationException("无法确定虚拟屏幕物理边界。");

        var left = validBounds.Min(region => region.X);
        var top = validBounds.Min(region => region.Y);
        var right = validBounds.Max(region => checked((long)region.X + region.Width));
        var bottom = validBounds.Max(region => checked((long)region.Y + region.Height));
        var width = right - left;
        var height = bottom - top;
        if (width > int.MaxValue || height > int.MaxValue)
            throw new InvalidOperationException("虚拟屏幕物理边界超出支持范围。");

        return new CaptureRegion(left, top, (int)width, (int)height);
    }
}
