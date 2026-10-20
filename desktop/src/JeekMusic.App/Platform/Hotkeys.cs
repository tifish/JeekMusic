namespace JeekMusic.Platform;

/// <summary>
/// 全局快捷键（需求 PC-3，设计 §8.2）：RegisterHotKey 注册在主窗口上，窗口不在前台、隐藏到托盘时也有效。
/// 快捷键文本如 "Ctrl+Alt+Space"、"Ctrl+Shift+F5"。注册失败（被别的程序占用）的记在 <see cref="Failed"/> 里。
/// </summary>
public sealed class Hotkeys
{
    readonly Dictionary<int, string> _registered = new();
    readonly HashSet<string> _failed = new();
    nint _hwnd;

    public event Action<string>? Pressed;

    /// <summary>注册失败的动作名。</summary>
    public IReadOnlySet<string> Failed => _failed;

    public void Attach(WindowMessages messages)
    {
        _hwnd = messages.Hwnd;
        messages.Add((_, msg, wParam, _) =>
        {
            if (msg != Win32.WM_HOTKEY) return false;
            if (_registered.TryGetValue((int)wParam, out var action)) Pressed?.Invoke(action);
            return true;
        });
    }

    /// <summary>按设置重新注册全部快捷键；<paramref name="enabled"/> 为 false 时全部取消。</summary>
    public void Apply(bool enabled, IReadOnlyDictionary<string, string> bindings)
    {
        foreach (var id in _registered.Keys) Win32.UnregisterHotKey(_hwnd, id);
        _registered.Clear();
        _failed.Clear();
        if (!enabled || _hwnd == 0) return;
        var nextId = 0x4A00;
        foreach (var (action, text) in bindings)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!TryParse(text, out var mods, out var vk)) { _failed.Add(action); continue; }
            var id = nextId++;
            if (Win32.RegisterHotKey(_hwnd, id, mods | Win32.MOD_NOREPEAT, vk)) _registered[id] = action;
            else _failed.Add(action);
        }
    }

    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Win32.MOD_CONTROL; break;
                case "alt": modifiers |= Win32.MOD_ALT; break;
                case "shift": modifiers |= Win32.MOD_SHIFT; break;
                case "win" or "meta": modifiers |= Win32.MOD_WIN; break;
                default:
                    if (VirtualKey(raw) is not { } key) return false;
                    vk = key;
                    break;
            }
        }
        // 至少要有一个修饰键（单独的字母键会让别的程序没法打字）；F 键和媒体键例外
        return vk != 0 && (modifiers != 0 || vk is >= 0x70 and <= 0x87 or >= 0xAD and <= 0xB3);
    }

    static uint? VirtualKey(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Length == 1 && n[0] is >= 'a' and <= 'z') return (uint)char.ToUpperInvariant(n[0]);
        if (n.Length == 1 && n[0] is >= '0' and <= '9') return n[0];
        if (n.Length >= 2 && n[0] == 'f' && int.TryParse(n[1..], out var f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        return n switch
        {
            "space" => 0x20, "enter" or "return" => 0x0D, "tab" => 0x09, "esc" or "escape" => 0x1B,
            "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28,
            "pageup" or "prior" => 0x21, "pagedown" or "next" => 0x22, "home" => 0x24, "end" => 0x23,
            "insert" => 0x2D, "delete" => 0x2E, "backspace" or "back" => 0x08,
            "oemcomma" or "," => 0xBC, "oemperiod" or "." => 0xBE, "oemminus" or "-" => 0xBD, "oemplus" or "=" => 0xBB,
            "oem2" or "/" => 0xBF, "oem1" or ";" => 0xBA, "oem7" or "'" => 0xDE, "oem4" or "[" => 0xDB, "oem6" or "]" => 0xDD,
            "mediaplaypause" => 0xB3, "medianexttrack" => 0xB0, "mediaprevioustrack" => 0xB1, "mediastop" => 0xB2,
            "volumemute" => 0xAD, "volumedown" => 0xAE, "volumeup" => 0xAF,
            _ => null,
        };
    }
}
