using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using GameFlowStudio.Automation.Interop;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Automation.Windows;

public sealed class WindowAutomationService : IWindowAutomationService
{
    public IReadOnlyList<WindowInfo> GetOpenWindows()
    {
        var windows = new List<WindowInfo>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(handle)) return true;
            var length = NativeMethods.GetWindowTextLength(handle);
            if (length == 0) return true;
            var title = new StringBuilder(length + 1);
            NativeMethods.GetWindowText(handle, title, title.Capacity);
            if (title.Length > 0) windows.Add(new(handle, title.ToString()));
            return true;
        }, 0);
        return windows;
    }

    public bool ActivateWindow(nint handle)
    {
        if (handle == nint.Zero) return false;
        NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
        return NativeMethods.SetForegroundWindow(handle);
    }

    public bool CloseWindow(nint handle) => handle != nint.Zero && NativeMethods.PostMessage(handle, NativeMethods.WmClose, 0, 0);

    public bool MinimizeWindow(nint handle) => handle != nint.Zero && NativeMethods.ShowWindow(handle, NativeMethods.SwMinimize);

    public bool ArrangeWindows(IReadOnlyList<nint> handles, int x, int y, int width, int height, int columns, int gapX, int gapY)
    {
        if (handles.Count == 0 || columns <= 0 || width <= 0 || height <= 0) return false;
        var succeeded = true;
        for (var index = 0; index < handles.Count; index++)
        {
            var column = index % columns;
            var row = index / columns;
            var moved = NativeMethods.SetWindowPos(handles[index], 0, x + column * (width + gapX), y + row * (height + gapY), width, height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
            succeeded &= moved;
        }
        return succeeded;
    }

    public bool SetWindowResolution(nint handle, int width, int height)
    {
        if (!TryGetPosition(handle, out var bounds) || !NativeMethods.GetClientRect(handle, out var client)) return false;
        var frameWidth = bounds.Right - bounds.Left - client.Right;
        var frameHeight = bounds.Bottom - bounds.Top - client.Bottom;
        return NativeMethods.SetWindowPos(handle, 0, bounds.Left, bounds.Top, width + frameWidth, height + frameHeight,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    public WindowBounds GetWindowBounds(nint handle)
    {
        if (handle == nint.Zero) throw new ArgumentException("窗口句柄不能为空。", nameof(handle));
        if (!NativeMethods.GetWindowRect(handle, out var rectangle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法获取目标窗口边界。");
        return new(handle, rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
    }

    private static bool TryGetPosition(nint handle, out WindowBounds bounds)
    {
        if (handle == nint.Zero || !NativeMethods.GetWindowRect(handle, out var rectangle))
        {
            bounds = default!;
            return false;
        }
        bounds = new(handle, rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        return true;
    }
}
