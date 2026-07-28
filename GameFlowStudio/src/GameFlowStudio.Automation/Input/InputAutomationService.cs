using System.ComponentModel;
using System.Runtime.InteropServices;
using GameFlowStudio.Automation.Interop;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Automation.Input;

/// <summary>基于 SendInput 的用户态键鼠自动化服务。</summary>
public sealed class InputAutomationService : IInputAutomationService
{
    private static readonly IReadOnlyDictionary<string, ushort> NamedKeys = BuildKeyMap();

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
        var parts = key.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new InvalidOperationException("按键名称不能为空。");
        var keys = parts.Select(ParseKey).ToArray();
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

    private static ushort ParseKey(string key)
    {
        var normalized = key.Trim();
        if (normalized.Length == 1)
        {
            var value = char.ToUpperInvariant(normalized[0]);
            if (value is >= 'A' and <= 'Z' or >= '0' and <= '9') return value;
        }
        if (NamedKeys.TryGetValue(normalized, out var result)) return result;
        throw new InvalidOperationException($"不支持的按键：{key}。");
    }

    private static IReadOnlyDictionary<string, ushort> BuildKeyMap()
    {
        var map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ctrl"] = 0x11, ["Control"] = 0x11, ["Shift"] = 0x10, ["Alt"] = 0x12,
            ["Enter"] = 0x0D, ["Escape"] = 0x1B, ["Esc"] = 0x1B, ["Space"] = 0x20,
            ["Tab"] = 0x09, ["Backspace"] = 0x08, ["Delete"] = 0x2E,
            ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
            ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28
        };
        for (var index = 1; index <= 24; index++) map[$"F{index}"] = (ushort)(0x6F + index);
        return map;
    }
}
