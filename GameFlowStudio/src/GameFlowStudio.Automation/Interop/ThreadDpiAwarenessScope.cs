using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GameFlowStudio.Automation.Interop;

/// <summary>Temporarily runs coordinate-sensitive Win32 calls in a per-monitor DPI context.</summary>
internal readonly struct ThreadDpiAwarenessScope : IDisposable
{
    private readonly nint _previousContext;

    private ThreadDpiAwarenessScope(nint previousContext) => _previousContext = previousContext;

    public static ThreadDpiAwarenessScope EnterPerMonitorAware()
    {
        var previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAwareV2);
        if (previous == nint.Zero)
            previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAware);
        if (previous == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法切换到 Per-Monitor DPI 坐标上下文。");

        return new ThreadDpiAwarenessScope(previous);
    }

    public void Dispose()
    {
        if (_previousContext != nint.Zero)
            _ = NativeMethods.SetThreadDpiAwarenessContext(_previousContext);
    }
}
