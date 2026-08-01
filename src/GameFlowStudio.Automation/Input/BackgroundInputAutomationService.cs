using System.ComponentModel;
using System.Runtime.InteropServices;
using GameFlowStudio.Automation.Interop;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Automation.Input;

/// <summary>
/// 向唯一目标窗口投递传统 Win32 键鼠消息。该实现不移动真实鼠标，也不主动激活目标窗口。
/// 目标程序仍可忽略消息，依赖 Raw Input、DirectInput 或真实键盘状态的程序通常无法兼容。
/// </summary>
public sealed class BackgroundInputAutomationService : IBackgroundInputAutomationService
{
    private const int MaximumChildDepth = 32;
    private const int MaximumDragSteps = 1000;
    private const uint ChildSearchFlags =
        NativeMethods.ChildWindowSkipInvisible
        | NativeMethods.ChildWindowSkipTransparent;

    public Task MoveMouseAsync(
        nint windowHandle,
        int screenX,
        int screenY,
        int durationMs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (durationMs < 0)
            throw new ArgumentOutOfRangeException(nameof(durationMs), "鼠标移动时间不能小于 0。");

        var target = ResolveMessageTarget(windowHandle, screenX, screenY);
        Post(
            target.Handle,
            NativeMethods.WmMouseMove,
            0,
            PackClientPoint(target.ClientX, target.ClientY),
            "后台鼠标移动");
        return Task.CompletedTask;
    }

    public async Task ClickAsync(
        nint windowHandle,
        int screenX,
        int screenY,
        MouseButtonType button,
        int clickCount,
        int intervalMs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureValidRootWindow(windowHandle, "后台点击");
        if (clickCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(clickCount), "点击次数必须大于 0。");
        if (clickCount > MouseClickLimits.MaximumBackgroundClickCount)
            throw new ArgumentOutOfRangeException(
                nameof(clickCount),
                $"单个后台点击节点最多允许点击 {MouseClickLimits.MaximumBackgroundClickCount} 次。");
        if (intervalMs < 0)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "点击间隔不能小于 0。");

        var messages = ButtonMessages(button);
        var withinDoubleClickTime = (uint)intervalMs <= NativeMethods.GetDoubleClickTime();
        var clickSequence = new BackgroundClickSequence(nint.Zero, 0);

        for (var index = 0; index < clickCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ResolveMessageTarget(windowHandle, screenX, screenY);
            var packedPoint = PackClientPoint(target.ClientX, target.ClientY);
            clickSequence = AdvanceClickSequence(clickSequence, target.Handle);
            var useDoubleClickMessage = clickSequence.ConsecutiveClickCount % 2 == 0
                && withinDoubleClickTime
                && SupportsDoubleClickMessages(target.Handle);

            Post(target.Handle, NativeMethods.WmMouseMove, 0, packedPoint, "后台点击");
            Post(
                target.Handle,
                useDoubleClickMessage ? messages.DoubleClick : messages.Down,
                messages.DownKeyState,
                packedPoint,
                "后台点击");
            try
            {
                Post(target.Handle, messages.Up, 0, packedPoint, "后台点击");
            }
            catch
            {
                // DOWN 已经入队后仍尽力补发 UP，避免目标程序长期保留按下状态。
                _ = NativeMethods.PostMessage(target.Handle, messages.Up, 0, packedPoint);
                throw;
            }

            if (index + 1 < clickCount && intervalMs > 0)
                await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
            else if (index + 1 < clickCount && (index + 1) % 16 == 0)
                await Task.Yield();
        }
    }

    public async Task DragMouseAsync(
        nint windowHandle,
        int startScreenX,
        int startScreenY,
        int endScreenX,
        int endScreenY,
        MouseButtonType button,
        int durationMs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureValidRootWindow(windowHandle, "后台拖动");
        if (durationMs < 0)
            throw new ArgumentOutOfRangeException(nameof(durationMs), "拖动时间不能小于 0。");

        var target = ResolveMessageTarget(windowHandle, startScreenX, startScreenY);
        EnsureScreenPointInsideClientArea(windowHandle, endScreenX, endScreenY, "后台拖动终点");

        var messages = ButtonMessages(button);
        var lastPoint = PackClientPoint(target.ClientX, target.ClientY);
        var buttonIsDown = false;

        Post(target.Handle, NativeMethods.WmMouseMove, 0, lastPoint, "后台拖动");
        try
        {
            Post(target.Handle, messages.Down, messages.DownKeyState, lastPoint, "后台拖动");
            buttonIsDown = true;

            var duration = Math.Max(0, durationMs);
            var steps = duration == 0
                ? 1
                : Math.Clamp(Math.Max(1, duration / 10), 1, MaximumDragSteps);
            var stepDelay = duration == 0 ? 0 : Math.Max(1, duration / steps);

            for (var step = 1; step <= steps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var screenX = Interpolate(startScreenX, endScreenX, step, steps);
                var screenY = Interpolate(startScreenY, endScreenY, step, steps);
                var clientPoint = ToClient(target.Handle, screenX, screenY);
                lastPoint = PackSignedPoint(clientPoint.X, clientPoint.Y);
                Post(
                    target.Handle,
                    NativeMethods.WmMouseMove,
                    messages.DownKeyState,
                    lastPoint,
                    "后台拖动");

                if (stepDelay > 0)
                    await Task.Delay(stepDelay, cancellationToken).ConfigureAwait(false);
                else if (step % 16 == 0)
                    await Task.Yield();
            }

            Post(target.Handle, messages.Up, 0, lastPoint, "后台拖动");
            buttonIsDown = false;
        }
        finally
        {
            if (buttonIsDown)
                _ = NativeMethods.PostMessage(target.Handle, messages.Up, 0, lastPoint);
        }
    }

    public Task ScrollMouseAsync(
        nint windowHandle,
        int screenX,
        int screenY,
        int delta,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureValidRootWindow(windowHandle, "后台滚动");
        if (delta == 0)
            throw new ArgumentOutOfRangeException(nameof(delta), "滚动量不能为 0。");
        if (delta is < short.MinValue or > short.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(delta),
                $"后台滚动量必须位于 {short.MinValue} 到 {short.MaxValue} 之间。");

        var target = ResolveMessageTarget(windowHandle, screenX, screenY);
        Post(
            target.Handle,
            NativeMethods.WmMouseMove,
            0,
            PackClientPoint(target.ClientX, target.ClientY),
            "后台滚动");
        Post(
            target.Handle,
            NativeMethods.WmMouseWheel,
            PackWheelParameter(delta),
            PackSignedPoint(screenX, screenY),
            "后台滚动");
        return Task.CompletedTask;
    }

    public async Task PressKeyAsync(
        nint windowHandle,
        string key,
        int durationMs,
        int repeatCount,
        int intervalMs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureValidRootWindow(windowHandle, "后台按键");
        if (durationMs < 0)
            throw new ArgumentOutOfRangeException(nameof(durationMs), "按下时间不能小于 0。");
        if (repeatCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(repeatCount), "重复次数必须大于 0。");
        if (intervalMs < 0)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "按键间隔不能小于 0。");

        var keys = KeyboardInputParser.ParseChord(key);
        for (var repeat = 0; repeat < repeatCount; repeat++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ResolveKeyboardMessageTarget(windowHandle);
            await PressChordAsync(target, keys, durationMs, cancellationToken).ConfigureAwait(false);

            if (repeat + 1 < repeatCount && intervalMs > 0)
                await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
            else if (repeat + 1 < repeatCount && (repeat + 1) % 16 == 0)
                await Task.Yield();
        }
    }

    public async Task TypeTextAsync(
        nint windowHandle,
        string text,
        int intervalMs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureValidRootWindow(windowHandle, "后台文本输入");
        if (intervalMs < 0)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "字符间隔不能小于 0。");

        for (var index = 0; index < text.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ResolveKeyboardMessageTarget(windowHandle);
            Post(
                target,
                NativeMethods.WmChar,
                (nint)text[index],
                1,
                "后台文本输入");

            if (index + 1 < text.Length && intervalMs > 0)
                await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
            else if (index + 1 < text.Length && (index + 1) % 32 == 0)
                await Task.Yield();
        }
    }

    internal static nint PackClientPoint(int x, int y)
    {
        if (x is < 0 or > short.MaxValue || y is < 0 or > short.MaxValue)
            throw new InvalidOperationException(
                "后台操作的客户区坐标超出 Windows 鼠标消息支持的范围。");
        return PackSignedPoint(x, y);
    }

    internal static nint PackSignedPoint(int x, int y)
    {
        if (x is < short.MinValue or > short.MaxValue
            || y is < short.MinValue or > short.MaxValue)
            throw new InvalidOperationException(
                "后台操作坐标超出 Windows 消息支持的有符号 16 位范围。");

        return (nint)((uint)(ushort)(short)x | ((uint)(ushort)(short)y << 16));
    }

    internal static nint PackWheelParameter(int delta)
    {
        if (delta is < short.MinValue or > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(delta));
        return (nint)((uint)(ushort)(short)delta << 16);
    }

    internal static nint PackKeyMessageParameter(
        uint mappedScanCode,
        bool keyUp,
        bool altDown)
    {
        var value = 1u | ((mappedScanCode & 0xFFu) << 16);
        var prefix = (mappedScanCode >> 8) & 0xFFu;
        if (prefix is 0xE0 or 0xE1)
            value |= 1u << 24;
        if (altDown)
            value |= 1u << 29;
        if (keyUp)
            value |= (1u << 30) | (1u << 31);
        return unchecked((nint)value);
    }

    internal static bool UsesSystemKeyMessage(ushort virtualKey, bool altDown) =>
        virtualKey is NativeMethods.VirtualKeyMenu or NativeMethods.VirtualKeyF10
        || altDown;

    internal static bool IsAltDownAfterTransition(
        IReadOnlyList<ushort> pressedKeys,
        ushort virtualKey,
        bool keyUp)
    {
        if (!keyUp)
            return virtualKey == NativeMethods.VirtualKeyMenu
                || pressedKeys.Contains(NativeMethods.VirtualKeyMenu);

        if (virtualKey != NativeMethods.VirtualKeyMenu)
            return pressedKeys.Contains(NativeMethods.VirtualKeyMenu);

        return pressedKeys.Count(key => key == NativeMethods.VirtualKeyMenu) > 1;
    }

    internal static BackgroundClickSequence AdvanceClickSequence(
        BackgroundClickSequence previous,
        nint currentTarget) =>
        previous.TargetHandle == currentTarget
            ? new(currentTarget, previous.ConsecutiveClickCount + 1)
            : new(currentTarget, 1);

    internal static BackgroundButtonMessages ButtonMessages(MouseButtonType button) => button switch
    {
        MouseButtonType.Left => new(
            NativeMethods.WmLeftButtonDown,
            NativeMethods.WmLeftButtonUp,
            NativeMethods.WmLeftButtonDoubleClick,
            (nint)NativeMethods.MouseKeyLeftButton),
        MouseButtonType.Right => new(
            NativeMethods.WmRightButtonDown,
            NativeMethods.WmRightButtonUp,
            NativeMethods.WmRightButtonDoubleClick,
            (nint)NativeMethods.MouseKeyRightButton),
        MouseButtonType.Middle => new(
            NativeMethods.WmMiddleButtonDown,
            NativeMethods.WmMiddleButtonUp,
            NativeMethods.WmMiddleButtonDoubleClick,
            (nint)NativeMethods.MouseKeyMiddleButton),
        _ => throw new ArgumentOutOfRangeException(
            nameof(button),
            button,
            "不支持的鼠标按键。")
    };

    private static async Task PressChordAsync(
        nint target,
        IReadOnlyList<ushort> keys,
        int durationMs,
        CancellationToken cancellationToken)
    {
        var pressed = new List<ushort>(keys.Count);
        try
        {
            foreach (var virtualKey in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var altDown = IsAltDownAfterTransition(
                    pressed,
                    virtualKey,
                    keyUp: false);
                PostKey(target, virtualKey, keyUp: false, altDown);
                pressed.Add(virtualKey);
            }

            if (durationMs > 0)
                await Task.Delay(durationMs, cancellationToken).ConfigureAwait(false);

            while (pressed.Count > 0)
            {
                var virtualKey = pressed[^1];
                var altDown = IsAltDownAfterTransition(
                    pressed,
                    virtualKey,
                    keyUp: true);
                PostKey(target, virtualKey, keyUp: true, altDown);
                pressed.RemoveAt(pressed.Count - 1);
            }
        }
        finally
        {
            // 即使取消、目标关闭或某次投递失败，也尽力按逆序释放已经投递过 DOWN 的键。
            while (pressed.Count > 0)
            {
                var virtualKey = pressed[^1];
                var altDown = IsAltDownAfterTransition(
                    pressed,
                    virtualKey,
                    keyUp: true);
                BestEffortPostKeyUp(target, virtualKey, altDown);
                pressed.RemoveAt(pressed.Count - 1);
            }
        }
    }

    private static void PostKey(
        nint target,
        ushort virtualKey,
        bool keyUp,
        bool altDown)
    {
        var systemMessage = UsesSystemKeyMessage(virtualKey, altDown);
        var message = systemMessage
            ? keyUp ? NativeMethods.WmSysKeyUp : NativeMethods.WmSysKeyDown
            : keyUp ? NativeMethods.WmKeyUp : NativeMethods.WmKeyDown;
        var mappedScanCode = NativeMethods.MapVirtualKey(
            virtualKey,
            NativeMethods.MapVirtualKeyVkToScanCodeExtended);
        Post(
            target,
            message,
            (nint)virtualKey,
            PackKeyMessageParameter(mappedScanCode, keyUp, altDown),
            "后台按键");
    }

    private static void BestEffortPostKeyUp(
        nint target,
        ushort virtualKey,
        bool altDown)
    {
        var systemMessage = UsesSystemKeyMessage(virtualKey, altDown);
        var message = systemMessage ? NativeMethods.WmSysKeyUp : NativeMethods.WmKeyUp;
        var mappedScanCode = NativeMethods.MapVirtualKey(
            virtualKey,
            NativeMethods.MapVirtualKeyVkToScanCodeExtended);
        _ = NativeMethods.PostMessage(
            target,
            message,
            (nint)virtualKey,
            PackKeyMessageParameter(mappedScanCode, keyUp: true, altDown));
    }

    private static BackgroundMessageTarget ResolveMessageTarget(
        nint root,
        int screenX,
        int screenY)
    {
        EnsureValidRootWindow(root, "后台鼠标操作");

        var current = root;
        for (var depth = 0; depth < MaximumChildDepth; depth++)
        {
            if (!NativeMethods.IsWindowEnabled(current))
                throw new InvalidOperationException(
                    "后台操作坐标命中了已禁用的窗口或控件，已停止投递。");

            var point = ToClient(current, screenX, screenY);
            EnsureInsideClientArea(current, point, "后台操作坐标");
            var child = NativeMethods.ChildWindowFromPointEx(current, point, ChildSearchFlags);
            if (child == nint.Zero || child == current)
                return new(current, point.X, point.Y);

            current = child;
        }

        throw new InvalidOperationException(
            "后台操作的目标窗口子控件层级过深，已停止投递。");
    }

    private static nint ResolveKeyboardMessageTarget(nint root)
    {
        EnsureValidRootWindow(root, "后台键盘操作");
        var threadId = NativeMethods.GetWindowThreadProcessId(root, out _);
        if (threadId == 0)
            return root;

        var information = new GUITHREADINFO
        {
            Size = (uint)Marshal.SizeOf<GUITHREADINFO>()
        };
        if (!NativeMethods.GetGUIThreadInfo(threadId, ref information))
            return root;

        var focus = information.FocusWindow;
        if (focus != nint.Zero
            && NativeMethods.IsWindow(focus)
            && NativeMethods.IsWindowEnabled(focus)
            && (focus == root || NativeMethods.IsChild(root, focus)))
            return focus;

        return root;
    }

    private static bool SupportsDoubleClickMessages(nint window)
    {
        var style = Environment.Is64BitProcess
            ? unchecked((nuint)NativeMethods.GetClassLongPtr64(
                window,
                NativeMethods.ClassStyleIndex).ToInt64())
            : NativeMethods.GetClassLong32(window, NativeMethods.ClassStyleIndex);
        return (style & NativeMethods.ClassStyleDoubleClicks) != 0;
    }

    private static POINT ToClient(nint window, int screenX, int screenY)
    {
        using var dpiScope = ThreadDpiAwarenessScope.EnterPerMonitorAware();
        var point = new POINT { X = screenX, Y = screenY };
        NativeMethods.SetLastErrorCode(0);
        var offset = NativeMethods.MapWindowPoints(nint.Zero, window, ref point, 1);
        var error = Marshal.GetLastWin32Error();
        if (offset == 0 && error != 0)
            throw new Win32Exception(
                error,
                "无法把后台操作坐标转换到目标窗口客户区。");
        return point;
    }

    private static void EnsureScreenPointInsideClientArea(
        nint window,
        int screenX,
        int screenY,
        string description)
    {
        EnsureInsideClientArea(window, ToClient(window, screenX, screenY), description);
    }

    private static void EnsureInsideClientArea(
        nint window,
        POINT point,
        string description)
    {
        if (!NativeMethods.GetClientRect(window, out var client))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "无法读取后台操作目标窗口的客户区。");
        if (point.X < client.Left
            || point.X >= client.Right
            || point.Y < client.Top
            || point.Y >= client.Bottom)
            throw new InvalidOperationException(
                $"{description}必须位于目标窗口客户区内，不能位于标题栏、边框或窗口外部。");
    }

    private static void EnsureValidRootWindow(nint window, string operation)
    {
        if (window == nint.Zero || !NativeMethods.IsWindow(window))
            throw new InvalidOperationException($"{operation}的目标窗口已经失效。");
        if (!NativeMethods.IsWindowEnabled(window))
            throw new InvalidOperationException($"{operation}的目标窗口已经禁用。");
    }

    private static int Interpolate(int start, int end, int step, int steps) =>
        checked((int)(start + ((long)end - start) * step / steps));

    private static void Post(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        string operation)
    {
        if (!NativeMethods.PostMessage(window, message, wParam, lParam))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"{operation}消息投递失败。目标程序可能以更高权限运行，或窗口已经关闭。");
    }
}

internal readonly record struct BackgroundButtonMessages(
    uint Down,
    uint Up,
    uint DoubleClick,
    nint DownKeyState);

internal readonly record struct BackgroundMessageTarget(
    nint Handle,
    int ClientX,
    int ClientY);

internal readonly record struct BackgroundClickSequence(
    nint TargetHandle,
    int ConsecutiveClickCount);
