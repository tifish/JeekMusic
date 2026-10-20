using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Control;

namespace JeekMusic.Spike;

/// <summary>
/// 系统媒体传输控件：Win11 音量弹窗 / 锁屏上的媒体卡片、键盘媒体键、蓝牙耳机按键都走这里。
/// 非 UWP 程序要通过窗口句柄取得（ISystemMediaTransportControlsInterop）。
/// </summary>
sealed class Smtc
{
    readonly SystemMediaTransportControls _controls;

    public event Action<string>? ButtonPressed;

    public Smtc(IntPtr hwnd)
    {
        _controls = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsNextEnabled = true;
        _controls.IsPreviousEnabled = true;
        _controls.ButtonPressed += (_, e) => ButtonPressed?.Invoke(e.Button.ToString());
    }

    public void Show(Track track, bool playing)
    {
        var updater = _controls.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = track.Title;
        updater.MusicProperties.Artist = track.Artist;
        updater.MusicProperties.AlbumTitle = track.Album;
        updater.Update();
        SetPlaying(playing);
    }

    public void SetPlaying(bool playing) =>
        _controls.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

    /// <summary>
    /// 用系统的会话管理器（和音量弹窗看到的是同一份）找本程序的会话，再从那里点"播放 / 暂停"。
    /// 不发真实的媒体键：如果当前会话是别的程序，会把用户正在放的东西暂停。
    /// </summary>
    public static async Task<(bool found, string title, string appId, bool toggled)> ProbeAsync(string expectedTitle)
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        foreach (var session in manager.GetSessions())
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props.Title != expectedTitle) continue;
            var toggled = await session.TryTogglePlayPauseAsync();
            return (true, props.Title, session.SourceAppUserModelId, toggled);
        }
        var others = string.Join(", ", manager.GetSessions().Select(s => s.SourceAppUserModelId));
        return (false, "", others, false);
    }
}
