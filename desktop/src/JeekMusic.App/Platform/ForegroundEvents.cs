using System.Runtime.InteropServices;

namespace JeekMusic.Platform;

/// <summary>前台程序切换或尺寸变化时唤醒分析调度器；空闲时无需轮询。</summary>
public sealed class ForegroundEvents : IDisposable
{
    readonly Callback _callback;
    readonly nint _foreground, _location;
    public event Action? Changed;
    public ForegroundEvents()
    {
        _callback = (_, evt, hwnd, objectId, _, _, _) =>
        {
            if (evt == 3 || objectId == 0 && hwnd == GetForegroundWindow()) Changed?.Invoke();
        };
        _foreground = SetWinEventHook(3, 3, 0, _callback, 0, 0, 0);
        _location = SetWinEventHook(0x800B, 0x800B, 0, _callback, 0, 0, 0);
    }
    public void Dispose() { if (_foreground != 0) UnhookWinEvent(_foreground); if (_location != 0) UnhookWinEvent(_location); }
    delegate void Callback(nint hook, uint evt, nint hwnd, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll")] static extern nint SetWinEventHook(uint min, uint max, nint module, Callback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
}
