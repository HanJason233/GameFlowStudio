using System.ComponentModel;
using System.Runtime.InteropServices;
using GameFlowStudio.Desktop.Interaction.Interop;

namespace GameFlowStudio.Desktop.Interaction;

public sealed class CursorPositionService
{
    /// <summary>
    /// 获取鼠标在虚拟桌面中的位置。返回值始终使用屏幕物理像素，可包含负坐标，
    /// 不随调用线程或显示器的 DPI 缩放比例改变。
    /// </summary>
    public (int X, int Y) GetPosition()
    {
        if (!NativeMethods.GetPhysicalCursorPos(out var point))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取鼠标物理像素坐标。");
        return (point.X, point.Y);
    }
}

