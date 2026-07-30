using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GameFlowStudio.Automation.Interop;
using GameFlowStudio.Contracts.Recording;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Automation.Input;

/// <summary>使用 Windows 低级钩子按屏幕或指定窗口范围录制键鼠操作。</summary>
public sealed class GlobalInputRecordingService : IInputRecordingService
{
    internal const int MaxRecordedActions = InputRecordingLimits.MaxActions;
    private const uint GetAncestorRootOwner = 3;
    private readonly object _sync = new();
    private readonly InputRecordingNormalizer _normalizer = new();
    private readonly List<RecordedInputAction> _actions = [];
    private readonly NativeMethods.LowLevelHookProc _mouseCallback;
    private readonly NativeMethods.LowLevelHookProc _keyboardCallback;
    private readonly uint _currentProcessId = (uint)Environment.ProcessId;
    private nint _mouseHook;
    private nint _keyboardHook;
    private long _startTimestamp;
    private SynchronizationContext? _eventContext;
    private InputRecordingScope _scope = InputRecordingScope.Screen;
    private nint _targetWindowHandle;
    private bool _isRecording;
    private bool _disposed;

    public GlobalInputRecordingService()
    {
        _mouseCallback = OnMouseHook;
        _keyboardCallback = OnKeyboardHook;
    }

    public bool IsRecording
    {
        get { lock (_sync) return _isRecording; }
    }

    public event EventHandler<RecordedInputActionEventArgs>? ActionRecorded;
    public event EventHandler? StopRequested;

    public void Start(InputRecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("操作录制仅支持 Windows。");
        if (SynchronizationContext.Current is null)
            throw new InvalidOperationException("操作录制必须从带消息循环的 UI 线程启动。");

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRecording) throw new InvalidOperationException("操作录制已经开始。");
            if (_mouseHook != 0 || _keyboardHook != 0)
            {
                TryReleaseHook(ref _keyboardHook);
                TryReleaseHook(ref _mouseHook);
                if (_mouseHook != 0 || _keyboardHook != 0)
                    throw new InvalidOperationException("上一次录制的系统钩子尚未释放。");
            }

            _targetWindowHandle = ResolveTargetWindow(options);
            _scope = options.Scope;
            _actions.Clear();
            _normalizer.Reset();
            _eventContext = SynchronizationContext.Current;
            _startTimestamp = Stopwatch.GetTimestamp();
            var module = NativeMethods.GetModuleHandle(null);
            if (module == 0)
            {
                ResetRecordingScope();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前程序模块句柄。");
            }

            _mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WhMouseLowLevel,
                _mouseCallback,
                module,
                0);
            if (_mouseHook == 0)
            {
                ResetRecordingScope();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动全局鼠标录制。");
            }

            _keyboardHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WhKeyboardLowLevel,
                _keyboardCallback,
                module,
                0);
            if (_keyboardHook == 0)
            {
                var error = Marshal.GetLastWin32Error();
                TryReleaseHook(ref _mouseHook);
                ResetRecordingScope();
                throw new Win32Exception(error, "无法启动全局键盘录制。");
            }

            _isRecording = true;
        }
    }

    public IReadOnlyList<RecordedInputAction> Stop()
    {
        IReadOnlyList<RecordedInputAction> snapshot;
        lock (_sync)
        {
            if (!_isRecording) return [];
            _isRecording = false;
            var remaining = MaxRecordedActions - _actions.Count;
            if (remaining > 0)
                _actions.AddRange(_normalizer.Flush(ElapsedMilliseconds()).Take(remaining));
            else
                _normalizer.Reset();
            snapshot = Array.AsReadOnly(
                _actions
                    .OrderBy(action => action.OffsetMilliseconds)
                    .ToArray());
            _actions.Clear();
            ResetRecordingScope();
        }

        TryReleaseHook(ref _keyboardHook);
        TryReleaseHook(ref _mouseHook);
        return snapshot;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _isRecording = false;
            _normalizer.Reset();
            _actions.Clear();
            _eventContext = null;
            ResetRecordingScope();
            _disposed = true;
        }

        TryReleaseHook(ref _keyboardHook);
        TryReleaseHook(ref _mouseHook);
        GC.SuppressFinalize(this);
    }

    private nint OnMouseHook(int code, nint message, nint data)
    {
        try
        {
            if (code >= 0 && data != 0)
            {
                var messageId = unchecked((uint)message.ToInt64());
                if (ShouldProcessMouseMessage(messageId))
                {
                    var nativeEvent = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(data);
                    if ((nativeEvent.Flags & NativeMethods.LowLevelMouseInjected) == 0)
                    {
                        RecordedInputAction? action = null;
                        var stopAtLimit = false;
                        lock (_sync)
                        {
                            if (_isRecording)
                            {
                                if (TryPrepareMouseEvent(
                                    messageId,
                                    nativeEvent.Point,
                                    out var x,
                                    out var y,
                                    out var capture))
                                {
                                    action = _normalizer.ProcessMouse(
                                        messageId,
                                        x,
                                        y,
                                        nativeEvent.MouseData,
                                        ElapsedMilliseconds(),
                                        capture);
                                }
                                else
                                {
                                    // 窗口在事件期间失效时丢弃未完成的鼠标操作，避免混用屏幕坐标。
                                    _normalizer.CancelMouse(messageId);
                                }
                                if (action is not null)
                                {
                                    if (_actions.Count < MaxRecordedActions)
                                    {
                                        _actions.Add(action);
                                        stopAtLimit = _actions.Count == MaxRecordedActions;
                                    }
                                    else
                                    {
                                        action = null;
                                    }
                                }
                            }
                        }
                        if (action is not null) RaiseActionRecorded(action);
                        if (stopAtLimit) RaiseStopRequested();
                    }
                    else
                    {
                        lock (_sync)
                            if (_isRecording) _normalizer.CancelMouse(messageId);
                    }
                }
            }
        }
        catch
        {
            // 异常不得穿过 native hook 回调边界，否则 Windows 会移除钩子。
        }
        return NativeMethods.CallNextHookEx(_mouseHook, code, message, data);
    }

    private nint OnKeyboardHook(int code, nint message, nint data)
    {
        try
        {
            if (code >= 0 && data != 0 && IsRecording)
            {
                var nativeEvent = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(data);
                if ((nativeEvent.Flags & NativeMethods.LowLevelKeyboardInjected) == 0)
                {
                    KeyboardNormalizationResult result = default;
                    var stopAtLimit = false;
                    lock (_sync)
                    {
                        if (_isRecording)
                        {
                            result = _normalizer.ProcessKeyboard(
                                unchecked((uint)message.ToInt64()),
                                nativeEvent.VirtualKey,
                                ElapsedMilliseconds(),
                                ShouldCaptureKeyboard());
                            if (result.Action is not null)
                            {
                                if (_actions.Count < MaxRecordedActions)
                                {
                                    _actions.Add(result.Action);
                                    stopAtLimit = _actions.Count == MaxRecordedActions;
                                }
                                else
                                {
                                    result = result with { Action = null };
                                }
                            }
                        }
                    }
                    if (result.Action is not null) RaiseActionRecorded(result.Action);
                    if (result.StopRequested || stopAtLimit) RaiseStopRequested();
                    if (result.Suppress) return 1;
                }
                else
                {
                    lock (_sync)
                        if (_isRecording)
                            _normalizer.CancelKeyboard(
                                unchecked((uint)message.ToInt64()),
                                nativeEvent.VirtualKey);
                }
            }
        }
        catch
        {
            // 异常不得穿过 native hook 回调边界，否则 Windows 会移除钩子。
        }
        return NativeMethods.CallNextHookEx(_keyboardHook, code, message, data);
    }

    private bool IsCurrentProcessWindow(nint window)
    {
        if (window == 0) return false;
        var root = NativeMethods.GetAncestor(window, NativeMethods.GetAncestorRoot);
        if (root != 0) window = root;
        _ = NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return processId == _currentProcessId;
    }

    private nint ResolveTargetWindow(InputRecordingOptions options)
    {
        if (options.Scope == InputRecordingScope.Screen) return 0;
        if (options.Scope != InputRecordingScope.Window)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Scope,
                "不支持指定的操作录制范围。");
        if (options.TargetWindowHandle == 0)
            throw new ArgumentException("窗口录制必须指定有效的目标窗口句柄。", nameof(options));

        using var dpiScope = ThreadDpiAwarenessScope.EnterPerMonitorAware();
        // 保留用户实际选中的顶级窗口，避免 owned dialog 的坐标原点被提升到主窗口。
        var targetWindow = NativeMethods.GetAncestor(
            options.TargetWindowHandle,
            NativeMethods.GetAncestorRoot);
        if (targetWindow == 0
            || NativeMethods.GetWindowThreadProcessId(targetWindow, out var processId) == 0
            || processId == 0
            || !NativeMethods.GetWindowRect(targetWindow, out _))
        {
            throw new ArgumentException("指定的目标窗口句柄无效或窗口已经关闭。", nameof(options));
        }
        if (processId == _currentProcessId)
            throw new InvalidOperationException("不能录制 GameFlowStudio 自身窗口，请选择其他应用窗口。");
        return targetWindow;
    }

    private bool TryPrepareMouseEvent(
        uint message,
        POINT screenPoint,
        out int x,
        out int y,
        out bool capture)
    {
        x = screenPoint.X;
        y = screenPoint.Y;
        if (_scope == InputRecordingScope.Screen)
        {
            capture = message == InputRecordingMessages.MouseMove
                || !IsCurrentProcessWindow(NativeMethods.WindowFromPoint(screenPoint));
            return true;
        }

        using var dpiScope = ThreadDpiAwarenessScope.EnterPerMonitorAware();
        if (!NativeMethods.GetWindowRect(_targetWindowHandle, out var windowRectangle))
        {
            capture = false;
            return false;
        }

        (x, y) = WindowInputRecordingRules.ToRelativePoint(
            screenPoint.X,
            screenPoint.Y,
            windowRectangle.Left,
            windowRectangle.Top);
        var eventWindow = NativeMethods.WindowFromPoint(screenPoint);
        capture = WindowInputRecordingRules.ShouldCaptureMouse(
            message,
            _normalizer.HasActiveMouseButton,
            _targetWindowHandle,
            GetRootWindow(eventWindow),
            GetRootOwnerWindow(eventWindow));
        return true;
    }

    private bool ShouldCaptureKeyboard()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        return _scope == InputRecordingScope.Screen
            ? !IsCurrentProcessWindow(foregroundWindow)
            : WindowInputRecordingRules.IsTargetWindow(
                _targetWindowHandle,
                GetRootWindow(foregroundWindow),
                GetRootOwnerWindow(foregroundWindow));
    }

    private static nint GetRootWindow(nint window)
    {
        if (window == 0) return 0;
        return NativeMethods.GetAncestor(window, NativeMethods.GetAncestorRoot);
    }

    private static nint GetRootOwnerWindow(nint window)
    {
        if (window == 0) return 0;
        return NativeMethods.GetAncestor(window, GetAncestorRootOwner);
    }

    private void ResetRecordingScope()
    {
        _scope = InputRecordingScope.Screen;
        _targetWindowHandle = 0;
    }

    private long ElapsedMilliseconds() =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds);

    private bool ShouldProcessMouseMessage(uint message)
    {
        lock (_sync)
        {
            if (!_isRecording) return false;
            if (InputRecordingMessages.IsRecordableMouseMessage(message)) return true;
            return message == InputRecordingMessages.MouseMove
                && _normalizer.HasActiveMouseButton;
        }
    }

    private void RaiseActionRecorded(RecordedInputAction action)
    {
        var context = _eventContext;
        if (context is null) return;
        context.Post(_ => DeliverActionRecorded(action), null);
    }

    private void DeliverActionRecorded(RecordedInputAction action)
    {
        var handlers = ActionRecorded;
        if (handlers is null) return;
        var args = new RecordedInputActionEventArgs(action);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<RecordedInputActionEventArgs>>())
            try { handler(this, args); } catch { }
    }

    private void RaiseStopRequested()
    {
        var context = _eventContext;
        if (context is null) return;
        context.Post(_ => DeliverStopRequested(), null);
    }

    private void DeliverStopRequested()
    {
        var handlers = StopRequested;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
            try { handler(this, EventArgs.Empty); } catch { }
    }

    private static void TryReleaseHook(ref nint hook)
    {
        if (hook == 0) return;
        if (NativeMethods.UnhookWindowsHookEx(hook)) hook = 0;
    }
}
