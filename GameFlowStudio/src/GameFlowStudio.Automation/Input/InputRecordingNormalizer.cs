using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Recording;

namespace GameFlowStudio.Automation.Input;

internal static class InputRecordingMessages
{
    public const uint KeyDown = 0x0100;
    public const uint KeyUp = 0x0101;
    public const uint SystemKeyDown = 0x0104;
    public const uint SystemKeyUp = 0x0105;
    public const uint MouseMove = 0x0200;
    public const uint LeftButtonDown = 0x0201;
    public const uint LeftButtonUp = 0x0202;
    public const uint RightButtonDown = 0x0204;
    public const uint RightButtonUp = 0x0205;
    public const uint MiddleButtonDown = 0x0207;
    public const uint MiddleButtonUp = 0x0208;
    public const uint MouseWheel = 0x020A;

    public static bool IsRecordableMouseMessage(uint message) =>
        message is LeftButtonDown or LeftButtonUp
            or RightButtonDown or RightButtonUp
            or MiddleButtonDown or MiddleButtonUp
            or MouseWheel;
}

internal readonly record struct KeyboardNormalizationResult(
    RecordedInputAction? Action,
    bool StopRequested,
    bool Suppress);

/// <summary>
/// 将低级钩子消息归并为工作流能够顺序回放的输入操作。
/// 此类不调用 user32，便于独立验证录制规则。
/// </summary>
internal sealed class InputRecordingNormalizer
{
    internal const uint StopVirtualKey = 0x76; // F7
    internal const int DragThresholdPixels = 5;

    private const uint VirtualKeyControl = 0x11;
    private const uint VirtualKeyShift = 0x10;
    private const uint VirtualKeyAlt = 0x12;
    private const uint VirtualKeyLeftControl = 0xA2;
    private const uint VirtualKeyRightControl = 0xA3;
    private const uint VirtualKeyLeftShift = 0xA0;
    private const uint VirtualKeyRightShift = 0xA1;
    private const uint VirtualKeyLeftAlt = 0xA4;
    private const uint VirtualKeyRightAlt = 0xA5;
    private const uint VirtualKeyLeftWindows = 0x5B;
    private const uint VirtualKeyRightWindows = 0x5C;

    private static readonly IReadOnlyDictionary<uint, string> SupportedKeys = BuildSupportedKeys();
    private readonly Dictionary<MouseButtonType, MouseDownState> _mouseButtons = [];
    private readonly Dictionary<uint, KeyDownState> _keys = [];
    private readonly HashSet<uint> _modifiers = [];
    private bool _stopKeyDown;

    public bool HasActiveMouseButton => _mouseButtons.Count > 0;

    public RecordedInputAction? ProcessMouse(
        uint message,
        int x,
        int y,
        uint mouseData,
        long elapsedMilliseconds,
        bool capture)
    {
        var elapsed = Math.Max(0, elapsedMilliseconds);
        if (message == InputRecordingMessages.MouseMove)
        {
            foreach (var activeButton in _mouseButtons.Keys.ToArray())
            {
                var down = _mouseButtons[activeButton];
                _mouseButtons[activeButton] = down with
                {
                    LastX = x,
                    LastY = y,
                    ExceededDragThreshold = down.ExceededDragThreshold
                        || ExceedsDragThreshold(down.X, down.Y, x, y)
                };
            }
            return null;
        }

        if (TryGetMouseButton(message, out var button, out var isDown))
        {
            if (isDown)
            {
                if (capture && !_mouseButtons.ContainsKey(button))
                    _mouseButtons.Add(button, new(x, y, x, y, elapsed, false));
                return null;
            }

            if (!_mouseButtons.Remove(button, out var down)) return null;
            if (!capture) return null;
            var duration = Duration(down.OffsetMilliseconds, elapsed);
            var isDrag = down.ExceededDragThreshold
                || ExceedsDragThreshold(down.X, down.Y, x, y);
            return isDrag
                ? new RecordedInputAction
                {
                    Kind = RecordedInputActionKind.MouseDrag,
                    OffsetMilliseconds = down.OffsetMilliseconds,
                    DurationMilliseconds = duration,
                    X = down.X,
                    Y = down.Y,
                    EndX = x,
                    EndY = y,
                    Button = button
                }
                : new RecordedInputAction
                {
                    Kind = RecordedInputActionKind.MouseClick,
                    OffsetMilliseconds = down.OffsetMilliseconds,
                    DurationMilliseconds = duration,
                    X = x,
                    Y = y,
                    Button = button
                };
        }

        if (message != InputRecordingMessages.MouseWheel || !capture) return null;
        var delta = unchecked((short)((mouseData >> 16) & 0xFFFF));
        if (delta == 0) return null;
        return new RecordedInputAction
        {
            Kind = RecordedInputActionKind.MouseScroll,
            OffsetMilliseconds = elapsed,
            X = x,
            Y = y,
            Delta = delta
        };
    }

    public KeyboardNormalizationResult ProcessKeyboard(
        uint message,
        uint virtualKey,
        long elapsedMilliseconds,
        bool capture)
    {
        var isDown = message is InputRecordingMessages.KeyDown or InputRecordingMessages.SystemKeyDown;
        var isUp = message is InputRecordingMessages.KeyUp or InputRecordingMessages.SystemKeyUp;
        if (!isDown && !isUp) return default;

        if (virtualKey == StopVirtualKey)
        {
            if (isUp)
            {
                var requested = _stopKeyDown;
                _stopKeyDown = false;
                return new(null, requested, true);
            }

            if (_stopKeyDown) return new(null, false, true);
            _stopKeyDown = true;
            return new(null, false, true);
        }

        if (IsModifier(virtualKey))
        {
            if (isDown) _modifiers.Add(virtualKey);
            else _modifiers.Remove(virtualKey);
            return default;
        }

        var elapsed = Math.Max(0, elapsedMilliseconds);
        if (isDown)
        {
            if (!capture || _keys.ContainsKey(virtualKey)) return default;
            var key = BuildKeyName(virtualKey);
            if (key is null) return default;
            _keys.Add(virtualKey, new(key, elapsed));
            return default;
        }

        if (!_keys.Remove(virtualKey, out var down)) return default;
        return new(new RecordedInputAction
        {
            Kind = RecordedInputActionKind.KeyPress,
            OffsetMilliseconds = down.OffsetMilliseconds,
            DurationMilliseconds = Duration(down.OffsetMilliseconds, elapsed),
            Key = down.Key
        }, false, false);
    }

    public void CancelMouse(uint message)
    {
        if (TryGetMouseButton(message, out var button, out var isDown) && !isDown)
            _mouseButtons.Remove(button);
    }

    public void CancelKeyboard(uint message, uint virtualKey)
    {
        if (message is not (InputRecordingMessages.KeyUp or InputRecordingMessages.SystemKeyUp)) return;
        if (virtualKey == StopVirtualKey) _stopKeyDown = false;
        else if (IsModifier(virtualKey)) _modifiers.Remove(virtualKey);
        else _keys.Remove(virtualKey);
    }

    public IReadOnlyList<RecordedInputAction> Flush(long elapsedMilliseconds)
    {
        var elapsed = Math.Max(0, elapsedMilliseconds);
        var actions = _keys.Values
            .Select(down => new RecordedInputAction
            {
                Kind = RecordedInputActionKind.KeyPress,
                OffsetMilliseconds = down.OffsetMilliseconds,
                DurationMilliseconds = Duration(down.OffsetMilliseconds, elapsed),
                Key = down.Key
            })
            .Concat(_mouseButtons.Select(pair =>
            {
                var down = pair.Value;
                return down.ExceededDragThreshold
                    ? new RecordedInputAction
                    {
                        Kind = RecordedInputActionKind.MouseDrag,
                        OffsetMilliseconds = down.OffsetMilliseconds,
                        DurationMilliseconds = Duration(down.OffsetMilliseconds, elapsed),
                        X = down.X,
                        Y = down.Y,
                        EndX = down.LastX,
                        EndY = down.LastY,
                        Button = pair.Key
                    }
                    : new RecordedInputAction
                    {
                        Kind = RecordedInputActionKind.MouseClick,
                        OffsetMilliseconds = down.OffsetMilliseconds,
                        DurationMilliseconds = Duration(down.OffsetMilliseconds, elapsed),
                        X = down.LastX,
                        Y = down.LastY,
                        Button = pair.Key
                    };
            }))
            .OrderBy(action => action.OffsetMilliseconds)
            .ToArray();
        Reset();
        return actions;
    }

    public void Reset()
    {
        _mouseButtons.Clear();
        _keys.Clear();
        _modifiers.Clear();
        _stopKeyDown = false;
    }

    private string? BuildKeyName(uint virtualKey)
    {
        if (!SupportedKeys.TryGetValue(virtualKey, out var key)) return null;
        if (HasWindowsModifier()) return null;
        if (key == "Tab" && HasModifier(VirtualKeyAlt, VirtualKeyLeftAlt, VirtualKeyRightAlt)) return null;

        var parts = new List<string>(4);
        if (HasModifier(VirtualKeyControl, VirtualKeyLeftControl, VirtualKeyRightControl)) parts.Add("Ctrl");
        if (HasModifier(VirtualKeyShift, VirtualKeyLeftShift, VirtualKeyRightShift)) parts.Add("Shift");
        if (HasModifier(VirtualKeyAlt, VirtualKeyLeftAlt, VirtualKeyRightAlt)) parts.Add("Alt");
        parts.Add(key);
        return string.Join('+', parts);
    }

    private bool HasWindowsModifier() =>
        _modifiers.Contains(VirtualKeyLeftWindows) || _modifiers.Contains(VirtualKeyRightWindows);

    private bool HasModifier(params uint[] virtualKeys) =>
        virtualKeys.Any(_modifiers.Contains);

    private static bool IsModifier(uint virtualKey) =>
        virtualKey is VirtualKeyControl or VirtualKeyShift or VirtualKeyAlt
            or VirtualKeyLeftControl or VirtualKeyRightControl
            or VirtualKeyLeftShift or VirtualKeyRightShift
            or VirtualKeyLeftAlt or VirtualKeyRightAlt
            or VirtualKeyLeftWindows or VirtualKeyRightWindows;

    private static bool TryGetMouseButton(
        uint message,
        out MouseButtonType button,
        out bool isDown)
    {
        (button, isDown) = message switch
        {
            InputRecordingMessages.LeftButtonDown => (MouseButtonType.Left, true),
            InputRecordingMessages.LeftButtonUp => (MouseButtonType.Left, false),
            InputRecordingMessages.RightButtonDown => (MouseButtonType.Right, true),
            InputRecordingMessages.RightButtonUp => (MouseButtonType.Right, false),
            InputRecordingMessages.MiddleButtonDown => (MouseButtonType.Middle, true),
            InputRecordingMessages.MiddleButtonUp => (MouseButtonType.Middle, false),
            _ => default
        };
        return message is InputRecordingMessages.LeftButtonDown or InputRecordingMessages.LeftButtonUp
            or InputRecordingMessages.RightButtonDown or InputRecordingMessages.RightButtonUp
            or InputRecordingMessages.MiddleButtonDown or InputRecordingMessages.MiddleButtonUp;
    }

    private static int Duration(long start, long end) =>
        (int)Math.Clamp(end - start, 0, int.MaxValue);

    private static bool ExceedsDragThreshold(int startX, int startY, int x, int y)
    {
        var deltaX = (long)x - startX;
        var deltaY = (long)y - startY;
        return deltaX * deltaX + deltaY * deltaY
            >= DragThresholdPixels * DragThresholdPixels;
    }

    private static IReadOnlyDictionary<uint, string> BuildSupportedKeys()
    {
        var keys = new Dictionary<uint, string>
        {
            [0x0D] = "Enter",
            [0x1B] = "Escape",
            [0x20] = "Space",
            [0x09] = "Tab",
            [0x08] = "Backspace",
            [0x2E] = "Delete",
            [0x24] = "Home",
            [0x23] = "End",
            [0x21] = "PageUp",
            [0x22] = "PageDown",
            [0x25] = "Left",
            [0x26] = "Up",
            [0x27] = "Right",
            [0x28] = "Down"
        };
        for (uint key = '0'; key <= '9'; key++) keys[key] = ((char)key).ToString();
        for (uint key = 'A'; key <= 'Z'; key++) keys[key] = ((char)key).ToString();
        for (uint index = 1; index <= 24; index++) keys[0x6F + index] = $"F{index}";
        keys.Remove(StopVirtualKey);
        return keys;
    }

    private readonly record struct MouseDownState(
        int X,
        int Y,
        int LastX,
        int LastY,
        long OffsetMilliseconds,
        bool ExceededDragThreshold);
    private readonly record struct KeyDownState(string Key, long OffsetMilliseconds);
}
