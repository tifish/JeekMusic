using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Avalonia.Platform;
using JeekMusic.Infrastructure;

namespace JeekMusic.Platform;

/// <summary>
/// 任务栏（需求 PC-6、W-5，设计 §7.5）：缩略图上的上一首 / 播放暂停 / 下一首按钮，以及任务栏图标上的播放进度。
/// 用 ITaskbarList3（COM，[GeneratedComInterface]，兼容 AOT）。按钮点击以 WM_COMMAND 发给主窗口。
/// </summary>
public sealed unsafe partial class Taskbar
{
    const int WM_COMMAND = 0x0111;
    const int THBN_CLICKED = 0x1800;
    const uint IdPrevious = 1, IdPlayPause = 2, IdNext = 3;

    ITaskbarList3? _list;
    nint _hwnd;
    nint _iconPlay, _iconPause, _iconPrev, _iconNext;
    bool _buttonsAdded;
    bool _playing;

    public event Action? PreviousClicked, PlayPauseClicked, NextClicked;

    /// <summary>窗口显示出来之后调用（任务栏按钮要在任务栏图标创建之后才能加）。</summary>
    public void Attach(nint hwnd, WindowMessages messages)
    {
        _hwnd = hwnd;
        try
        {
            var clsid = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");
            var iid = typeof(ITaskbarList3).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, iid, out var ptr));
            _list = (ITaskbarList3)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None);
            Marshal.Release(ptr);
            _list.HrInit();
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            Log.Warn("taskbar", "ITaskbarList3 不可用", e);
            _list = null;
            return;
        }
        _iconPlay = LoadIcon("play");
        _iconPause = LoadIcon("pause");
        _iconPrev = LoadIcon("previous");
        _iconNext = LoadIcon("next");
        messages.Add((_, msg, wParam, _) =>
        {
            if (msg != WM_COMMAND || (wParam.ToInt64() >> 16 & 0xFFFF) != THBN_CLICKED) return false;
            switch ((uint)(wParam.ToInt64() & 0xFFFF))
            {
                case IdPrevious: PreviousClicked?.Invoke(); break;
                case IdPlayPause: PlayPauseClicked?.Invoke(); break;
                case IdNext: NextClicked?.Invoke(); break;
                default: return false;
            }
            return true;
        });
        AddButtons();
    }

    static nint LoadIcon(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://JeekMusic/Assets/Taskbar/{name}.png"));
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        var size = GetSystemMetrics(49 /* SM_CXSMICON */);
        fixed (byte* p = bytes) return CreateIconFromResourceEx(p, (uint)bytes.Length, 1, 0x00030000, size, size, 0);
    }

    void AddButtons()
    {
        if (_list is null || _buttonsAdded) return;
        var buttons = stackalloc THUMBBUTTON[3];
        buttons[0] = Button(IdPrevious, _iconPrev, L.T("上一首"));
        buttons[1] = Button(IdPlayPause, _playing ? _iconPause : _iconPlay, _playing ? L.T("暂停") : L.T("播放"));
        buttons[2] = Button(IdNext, _iconNext, L.T("下一首"));
        _buttonsAdded = _list.ThumbBarAddButtons(_hwnd, 3, buttons) >= 0;
    }

    static THUMBBUTTON Button(uint id, nint icon, string tip)
    {
        var b = new THUMBBUTTON { dwMask = 0x2 | 0x4 | 0x8 /* ICON | TOOLTIP | FLAGS */, iId = id, hIcon = icon, dwFlags = 0 };
        var n = Math.Min(tip.Length, 259);
        for (var i = 0; i < n; i++) b.szTip[i] = tip[i];
        b.szTip[n] = '\0';
        return b;
    }

    /// <summary>播放状态变了：更新播放 / 暂停按钮的图标。</summary>
    public void SetPlaying(bool playing)
    {
        if (_playing == playing && _buttonsAdded) return;
        _playing = playing;
        if (_list is null) return;
        if (!_buttonsAdded) { AddButtons(); return; }
        var button = Button(IdPlayPause, playing ? _iconPause : _iconPlay, playing ? L.T("暂停") : L.T("播放"));
        _list.ThumbBarUpdateButtons(_hwnd, 1, &button);
    }

    /// <summary>任务栏图标上的进度；<paramref name="fraction"/> 为 null 时不显示（需求 W-5，可以在设置里关闭）。</summary>
    public void SetProgress(double? fraction, bool paused)
    {
        if (_list is null) return;
        if (fraction is not { } f)
        {
            _list.SetProgressState(_hwnd, 0 /* TBPF_NOPROGRESS */);
            return;
        }
        _list.SetProgressState(_hwnd, paused ? 8 /* TBPF_PAUSED */ : 2 /* TBPF_NORMAL */);
        _list.SetProgressValue(_hwnd, (ulong)(Math.Clamp(f, 0, 1) * 1000), 1000);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct THUMBBUTTON
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public nint hIcon;
        public fixed char szTip[260];
        public uint dwFlags;
    }

    [GeneratedComInterface]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    internal partial interface ITaskbarList3
    {
        // ITaskbarList
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(nint hwnd);
        [PreserveSig] int DeleteTab(nint hwnd);
        [PreserveSig] int ActivateTab(nint hwnd);
        [PreserveSig] int SetActiveAlt(nint hwnd);
        // ITaskbarList2
        [PreserveSig] int MarkFullscreenWindow(nint hwnd, int fullscreen);
        // ITaskbarList3
        [PreserveSig] int SetProgressValue(nint hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(nint hwnd, int flags);
        [PreserveSig] int RegisterTab(nint hwndTab, nint hwndMdi);
        [PreserveSig] int UnregisterTab(nint hwndTab);
        [PreserveSig] int SetTabOrder(nint hwndTab, nint hwndInsertBefore);
        [PreserveSig] int SetTabActive(nint hwndTab, nint hwndMdi, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(nint hwnd, uint count, THUMBBUTTON* buttons);
        [PreserveSig] int ThumbBarUpdateButtons(nint hwnd, uint count, THUMBBUTTON* buttons);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint ppv);

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconFromResourceEx(byte* bits, uint size, int icon, uint version, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
