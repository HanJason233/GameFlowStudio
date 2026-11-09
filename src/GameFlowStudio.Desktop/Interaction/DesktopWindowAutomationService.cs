using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Desktop.Interaction.Interop;

namespace GameFlowStudio.Desktop.Interaction;

/// <summary>Desktop-only window operations used by window selection and screen pickers.</summary>
public interface IWindowPickerService
{
    IReadOnlyList<WindowInfo> GetOpenWindows();
    WindowBounds GetWindowBounds(nint handle);
}

public sealed class DesktopWindowPickerService : IWindowPickerService
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

    public WindowBounds GetWindowBounds(nint handle)
    {
        using var dpiScope = ThreadDpiAwarenessScope.EnterPerMonitorAware();
        if (handle == nint.Zero) throw new ArgumentException("窗口句柄不能为空。", nameof(handle));
        if (!NativeMethods.GetWindowRect(handle, out var rectangle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法获取目标窗口边界。");
        return new(handle, rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
    }

}
