using System.Runtime.InteropServices;
using System.Text;

namespace GameFlowStudio.Desktop.Interaction.Interop;

internal static class NativeMethods
{
    public static readonly nint DpiAwarenessContextPerMonitorAware = new(-3);
    public static readonly nint DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    public const uint InputMouse = 0;
    public const uint InputKeyboard = 1;
    public const uint KeyUp = 0x0002;
    public const uint KeyUnicode = 0x0004;
    public const uint MouseLeftDown = 0x0002;
    public const uint MouseLeftUp = 0x0004;
    public const uint MouseRightDown = 0x0008;
    public const uint MouseRightUp = 0x0010;
    public const uint MouseMiddleDown = 0x0020;
    public const uint MouseMiddleUp = 0x0040;
    public const uint MouseWheel = 0x0800;
    public const int SwRestore = 9;
    public const int SwMinimize = 6;
    public const uint WmClose = 0x0010;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const uint WdaExcludeFromCapture = 0x00000011;
    public const uint WmMouseMove = 0x0200;
    public const uint WmLeftButtonDown = 0x0201;
    public const uint WmLeftButtonUp = 0x0202;
    public const uint WmLeftButtonDoubleClick = 0x0203;
    public const uint WmRightButtonDown = 0x0204;
    public const uint WmRightButtonUp = 0x0205;
    public const uint WmRightButtonDoubleClick = 0x0206;
    public const uint WmMiddleButtonDown = 0x0207;
    public const uint WmMiddleButtonUp = 0x0208;
    public const uint WmMiddleButtonDoubleClick = 0x0209;
    public const uint WmMouseWheel = 0x020A;
    public const uint WmKeyDown = 0x0100;
    public const uint WmKeyUp = 0x0101;
    public const uint WmChar = 0x0102;
    public const uint WmSysKeyDown = 0x0104;
    public const uint WmSysKeyUp = 0x0105;
    public const uint MouseKeyLeftButton = 0x0001;
    public const uint MouseKeyRightButton = 0x0002;
    public const uint MouseKeyMiddleButton = 0x0010;
    public const uint MapVirtualKeyVkToScanCodeExtended = 4;
    public const ushort VirtualKeyMenu = 0x12;
    public const ushort VirtualKeyF10 = 0x79;
    public const uint ChildWindowSkipInvisible = 0x0001;
    public const uint ChildWindowSkipTransparent = 0x0004;
    public const int ClassStyleIndex = -26;
    public const nuint ClassStyleDoubleClicks = 0x0008;
    public const int WhKeyboardLowLevel = 13;
    public const int WhMouseLowLevel = 14;
    public const uint LowLevelMouseInjected = 0x00000001;
    public const uint LowLevelKeyboardInjected = 0x00000010;
    public const uint GetAncestorRoot = 2;

    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetPhysicalCursorPos(out POINT point);
    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetThreadDpiAwarenessContext(nint dpiContext);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(nint deviceContext, nint clipRectangle, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetClientRect(nint window, out RECT rectangle);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(nint window, out RECT rectangle);
    [DllImport("user32.dll", SetLastError = true)] public static extern int MapWindowPoints(nint fromWindow, nint toWindow, ref POINT point, uint pointCount);
    [DllImport("user32.dll")] public static extern nint ChildWindowFromPointEx(nint parent, POINT point, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("user32.dll", EntryPoint = "GetClassLongW")] public static extern uint GetClassLong32(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] public static extern nint GetClassLongPtr64(nint window, int index);
    [DllImport("kernel32.dll", EntryPoint = "SetLastError")] public static extern void SetLastErrorCode(uint errorCode);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookEx(int hookId, LowLevelHookProc callback, nint module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsChild(nint parent, nint window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO information);
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW", ExactSpelling = true)] public static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint GetModuleHandle(string? moduleName);
    [DllImport("dwmapi.dll", PreserveSig = true)] public static extern int DwmFlush();

    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool MonitorEnumProc(nint monitor, nint monitorDeviceContext, ref RECT monitorRectangle, nint data);

    public delegate bool EnumWindowsProc(nint window, nint parameter);
    public delegate nint LowLevelHookProc(int code, nint message, nint data);
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X; public int Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct GUITHREADINFO
{
    public uint Size;
    public uint Flags;
    public nint ActiveWindow;
    public nint FocusWindow;
    public nint CaptureWindow;
    public nint MenuOwnerWindow;
    public nint MoveSizeWindow;
    public nint CaretWindow;
    public RECT CaretRectangle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT
{
    public POINT Point;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KBDLLHOOKSTRUCT
{
    public uint VirtualKey;
    public uint ScanCode;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct INPUT { public uint Type; public InputUnion Data; }

[StructLayout(LayoutKind.Explicit)]
internal struct InputUnion
{
    [FieldOffset(0)] public MOUSEINPUT Mouse;
    [FieldOffset(0)] public KEYBDINPUT Keyboard;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT
{
    public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public nuint ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KEYBDINPUT
{
    public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public nuint ExtraInfo;
}

