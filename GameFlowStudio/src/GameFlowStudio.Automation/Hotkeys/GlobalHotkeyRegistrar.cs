using System.ComponentModel;
using System.Runtime.InteropServices;
using GameFlowStudio.Automation.Interop;

namespace GameFlowStudio.Automation.Hotkeys;

public sealed class GlobalHotkeyRegistrar : IDisposable
{
    private readonly HashSet<int> _ids = [];
    private nint _window;

    public void Initialize(nint window) => _window = window;

    public void Register(int id, string key)
    {
        if (_window == 0) throw new InvalidOperationException("快捷键服务尚未初始化。");
        var virtualKey = ParseFunctionKey(key);
        if (!NativeMethods.RegisterHotKey(_window, id, 0, virtualKey))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"注册快捷键 {key} 失败，它可能已被其他程序占用。");
        _ids.Add(id);
    }

    public void Clear()
    {
        foreach (var id in _ids) NativeMethods.UnregisterHotKey(_window, id);
        _ids.Clear();
    }

    public void Dispose() { Clear(); GC.SuppressFinalize(this); }

    private static uint ParseFunctionKey(string key)
    {
        if (key.Length > 1 && key[0] is 'F' or 'f' && int.TryParse(key[1..], out var value) && value is >= 1 and <= 24)
            return (uint)(0x6F + value);
        throw new InvalidOperationException("MVP 快捷键仅支持 F1 到 F24。");
    }
}
