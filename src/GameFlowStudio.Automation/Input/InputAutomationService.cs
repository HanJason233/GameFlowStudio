using System.ComponentModel;
using System.Runtime.InteropServices;
using GameFlowStudio.Automation.Interop;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Automation.Input;

/// <summary>基于 SendInput 的用户态键鼠自动化服务。</summary>
public sealed class InputAutomationService : IInputAutomationService
{
    public Task MoveMouseAsync(int x, int y, int durationMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeMethods.SetPhysicalCursorPos(x, y))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "移动鼠标到物理像素坐标失败。");
        return Task.CompletedTask;
    }

    public async Task ClickAsync(int x, int y, MouseButtonType button, int clickCount, int intervalMs, CancellationToken cancellationToken)
    {
        await MoveMouseAsync(x, y, 0, cancellationToken).ConfigureAwait(false);
        var (down, up) = MouseButtonFlags(button);
        for (var index = 0; index < clickCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Send(Mouse(down), Mouse(up));
            if (index + 1 < clickCount && intervalMs > 0) await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DragMouseAsync(int startX, int startY, int endX, int endY, MouseButtonType button, int durationMs, CancellationToken cancellationToken)
    {
        await MoveMouseAsync(startX, startY, 0, cancellationToken).ConfigureAwait(false);
        var (down, up) = MouseButtonFlags(button);
        Send(Mouse(down));
        try
        {
            var duration = Math.Max(0, durationMs);
            var steps = duration == 0 ? 1 : Math.Max(1, duration / 10);
            for (var step = 1; step <= steps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var x = startX + (endX - startX) * step / steps;
                var y = startY + (endY - startY) * step / steps;
                await MoveMouseAsync(x, y, 0, cancellationToken).ConfigureAwait(false);
                if (duration > 0) await Task.Delay(Math.Max(1, duration / steps), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Send(Mouse(up));
        }
    }

    public async Task ScrollMouseAsync(int x, int y, int delta, CancellationToken cancellationToken)
    {
        await MoveMouseAsync(x, y, 0, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Send(Mouse(NativeMethods.MouseWheel, unchecked((uint)delta)));
    }

    public async Task PressKeyAsync(string key, int durationMs, int repeatCount, int intervalMs, CancellationToken cancellationToken)
    {
        var keys = KeyboardInputParser.ParseChord(key);
        for (var repeat = 0; repeat < repeatCount; repeat++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Send(keys.Select(KeyDown).ToArray());
            try { if (durationMs > 0) await Task.Delay(durationMs, cancellationToken).ConfigureAwait(false); }
            finally { Send(keys.Reverse().Select(KeyUp).ToArray()); }
            if (repeat + 1 < repeatCount && intervalMs > 0) await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task TypeTextAsync(string text, int intervalMs, CancellationToken cancellationToken)
    {
        foreach (var character in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Send(Unicode(character, false), Unicode(character, true));
            if (intervalMs > 0) await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private static INPUT Mouse(uint flag, uint data = 0) => new() { Type = NativeMethods.InputMouse, Data = new() { Mouse = new() { MouseData = data, Flags = flag } } };
    private static (uint Down, uint Up) MouseButtonFlags(MouseButtonType button) => button switch
    {
        MouseButtonType.Right => (NativeMethods.MouseRightDown, NativeMethods.MouseRightUp),
        MouseButtonType.Middle => (NativeMethods.MouseMiddleDown, NativeMethods.MouseMiddleUp),
        _ => (NativeMethods.MouseLeftDown, NativeMethods.MouseLeftUp)
    };
    private static INPUT KeyDown(ushort key) => new() { Type = NativeMethods.InputKeyboard, Data = new() { Keyboard = new() { VirtualKey = key } } };
    private static INPUT KeyUp(ushort key) => new() { Type = NativeMethods.InputKeyboard, Data = new() { Keyboard = new() { VirtualKey = key, Flags = NativeMethods.KeyUp } } };
    private static INPUT Unicode(char value, bool up) => new() { Type = NativeMethods.InputKeyboard, Data = new() { Keyboard = new() { ScanCode = value, Flags = NativeMethods.KeyUnicode | (up ? NativeMethods.KeyUp : 0) } } };

    private static void Send(params INPUT[] inputs)
    {
        if (NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "发送输入失败。");
    }

}
