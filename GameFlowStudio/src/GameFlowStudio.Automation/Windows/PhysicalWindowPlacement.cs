using System.ComponentModel;
using System.Runtime.InteropServices;
using GameFlowStudio.Automation.Interop;
using GameFlowStudio.Contracts.Results;

namespace GameFlowStudio.Automation.Windows;

/// <summary>使用虚拟桌面物理像素放置无边框拾取窗口 HWND。</summary>
public static class PhysicalWindowPlacement
{
    public static void Apply(nint windowHandle, CaptureRegion bounds)
    {
        if (windowHandle == nint.Zero)
            throw new ArgumentException("窗口句柄不能为空。", nameof(windowHandle));
        if (!bounds.IsValid)
            throw new ArgumentException("窗口物理像素边界无效。", nameof(bounds));

        using var dpiScope = ThreadDpiAwarenessScope.EnterPerMonitorAware();
        if (!NativeMethods.SetWindowPos(
                windowHandle,
                nint.Zero,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置拾取窗口物理像素边界。");
    }
}
