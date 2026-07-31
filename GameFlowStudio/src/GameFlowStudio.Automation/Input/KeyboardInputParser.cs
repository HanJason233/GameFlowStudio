namespace GameFlowStudio.Automation.Input;

/// <summary>
/// Parses the key names shared by foreground SendInput and background window-message input.
/// Keeping one parser prevents the two delivery modes from accepting different scripts.
/// </summary>
internal static class KeyboardInputParser
{
    private static readonly IReadOnlyDictionary<string, ushort> NamedKeys = BuildKeyMap();

    internal static ushort[] ParseChord(string key)
    {
        var parts = key.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new InvalidOperationException("按键名称不能为空。");

        return parts.Select(ParseKey).ToArray();
    }

    private static ushort ParseKey(string key)
    {
        var normalized = key.Trim();
        if (normalized.Length == 1)
        {
            var value = char.ToUpperInvariant(normalized[0]);
            if (value is >= 'A' and <= 'Z' or >= '0' and <= '9')
                return value;
        }

        if (NamedKeys.TryGetValue(normalized, out var result))
            return result;

        throw new InvalidOperationException($"不支持的按键：{key}。");
    }

    private static IReadOnlyDictionary<string, ushort> BuildKeyMap()
    {
        var map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ctrl"] = 0x11,
            ["Control"] = 0x11,
            ["Shift"] = 0x10,
            ["Alt"] = 0x12,
            ["Enter"] = 0x0D,
            ["Escape"] = 0x1B,
            ["Esc"] = 0x1B,
            ["Space"] = 0x20,
            ["Tab"] = 0x09,
            ["Backspace"] = 0x08,
            ["Delete"] = 0x2E,
            ["Home"] = 0x24,
            ["End"] = 0x23,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["Left"] = 0x25,
            ["Up"] = 0x26,
            ["Right"] = 0x27,
            ["Down"] = 0x28
        };
        for (var index = 1; index <= 24; index++)
            map[$"F{index}"] = (ushort)(0x6F + index);
        return map;
    }
}
