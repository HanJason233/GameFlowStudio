using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Desktop.Interaction;

namespace GameFlowStudio.Desktop.Views;

/// <summary>让无边框覆盖窗口 HWND 与物理虚拟桌面对齐。</summary>
internal static class PhysicalOverlayWindowPlacement
{
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;

    public static void Attach(
        Window window,
        Func<CaptureRegion> boundsProvider,
        Action<CaptureRegion>? onBoundsApplied = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(boundsProvider);
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        HwndSource? source = null;
        CaptureRegion? lastAppliedBounds = null;
        var updatePending = false;
        var closed = false;

        void ApplyBounds()
        {
            if (source is null) return;
            var bounds = boundsProvider();
            PhysicalWindowPlacement.Apply(source.Handle, bounds);
            lastAppliedBounds = bounds;
            onBoundsApplied?.Invoke(bounds);
        }

        async Task ReapplyBoundsAfterDisplayChangeAsync()
        {
            try
            {
                await window.Dispatcher.InvokeAsync(
                    static () => { },
                    DispatcherPriority.ContextIdle);
                for (var attempt = 0; attempt < 3 && !closed; attempt++)
                {
                    try
                    {
                        ApplyBounds();
                        return;
                    }
                    catch when (attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)));
                    }
                }
            }
            catch
            {
                if (!closed && source is not null && lastAppliedBounds is { } fallback)
                {
                    try { PhysicalWindowPlacement.Apply(source.Handle, fallback); }
                    catch { /* 窗口关闭或显示配置仍不稳定时保留当前 HWND 状态。 */ }
                }
            }
            finally
            {
                updatePending = false;
            }
        }

        nint WindowHook(nint windowHandle, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message is not (WmDisplayChange or WmDpiChanged) || updatePending)
                return nint.Zero;

            updatePending = true;
            _ = ReapplyBoundsAfterDisplayChangeAsync();
            return nint.Zero;
        }

        window.SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
                ?? throw new InvalidOperationException("无法连接拾取窗口的 HWND 可视化源。");
            source.AddHook(WindowHook);
            ApplyBounds();
        };
        window.Loaded += (_, _) => ApplyBounds();
        window.Closed += (_, _) =>
        {
            closed = true;
            source?.RemoveHook(WindowHook);
            source = null;
        };
    }
}
