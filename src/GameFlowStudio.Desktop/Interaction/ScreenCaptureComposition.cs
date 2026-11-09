using GameFlowStudio.Desktop.Interaction.Interop;

namespace GameFlowStudio.Desktop.Interaction;

/// <summary>协调拾取窗口与 Windows 桌面合成器。</summary>
public static class ScreenCaptureComposition
{
    public static bool TryExcludeWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero ||
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return false;

        return NativeMethods.SetWindowDisplayAffinity(
            windowHandle,
            NativeMethods.WdaExcludeFromCapture);
    }

    public static bool TryFlush()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6))
            return false;

        return NativeMethods.DwmFlush() >= 0;
    }
}

