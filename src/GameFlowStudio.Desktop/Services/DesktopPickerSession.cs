using System.Windows;
using System.Windows.Threading;
using GameFlowStudio.Automation.Windows;

namespace GameFlowStudio.Desktop.Services;

/// <summary>让主窗口不出现在拾取截图中，并在结束后可靠恢复。</summary>
internal sealed class DesktopPickerSession : IDisposable
{
    private static readonly SemaphoreSlim SessionGate = new(1, 1);

    private readonly Window _mainWindow;
    private readonly bool _wasVisible;
    private int _disposed;

    private DesktopPickerSession(Window mainWindow)
    {
        _mainWindow = mainWindow;
        _mainWindow.Dispatcher.VerifyAccess();
        _wasVisible = mainWindow.IsVisible;
        if (_wasVisible) mainWindow.Hide();
    }

    public static DesktopPickerSession Start()
    {
        if (!SessionGate.Wait(0))
            throw new InvalidOperationException("已有屏幕拾取操作正在进行。");

        var mainWindow = Application.Current?.MainWindow;
        if (mainWindow is null)
        {
            SessionGate.Release();
            throw new InvalidOperationException("主窗口尚未初始化，无法启动屏幕拾取。");
        }

        try
        {
            return new DesktopPickerSession(mainWindow);
        }
        catch
        {
            SessionGate.Release();
            throw;
        }
    }

    public async Task WaitForDesktopUpdateAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _mainWindow.Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.ContextIdle);
        _ = ScreenCaptureComposition.TryFlush();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_mainWindow.Dispatcher.CheckAccess())
            {
                RestoreMainWindow();
            }
            else if (!_mainWindow.Dispatcher.HasShutdownStarted &&
                     !_mainWindow.Dispatcher.HasShutdownFinished)
            {
                _mainWindow.Dispatcher.Invoke(RestoreMainWindow);
            }
        }
        finally
        {
            SessionGate.Release();
        }
    }

    private void RestoreMainWindow()
    {
        if (!_wasVisible) return;
        _mainWindow.Show();
        if (_mainWindow.WindowState != WindowState.Minimized)
            _mainWindow.Activate();
    }
}
