using Avalonia.Controls;

namespace JeekMusic.Platform;

/// <summary>
/// 主窗口的 Win32 消息（WM_HOTKEY、WM_COMMAND、WM_POWERBROADCAST、WM_DEVICECHANGE 等）分发给各个功能（设计 §8）。
/// 主窗口隐藏到托盘时句柄还在，照样收到消息。
/// </summary>
public sealed class WindowMessages
{
    readonly List<Func<nint, uint, nint, nint, bool>> _handlers = new();

    public nint Hwnd { get; private set; }

    public void Attach(Window window)
    {
        Hwnd = window.TryGetPlatformHandle()?.Handle ?? 0;
        Win32Properties.AddWndProcHookCallback(window, Hook);
    }

    /// <summary>添加处理函数；返回 true 表示消息已处理。</summary>
    public void Add(Func<nint, uint, nint, nint, bool> handler) => _handlers.Add(handler);

    nint Hook(nint hwnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        foreach (var h in _handlers)
            if (h(hwnd, msg, wParam, lParam)) { handled = true; return 0; }
        return 0;
    }
}
