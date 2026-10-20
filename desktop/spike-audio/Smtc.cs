using System;
using Windows.Media;

namespace JeekMusic.AudioSpike;

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

    public void Show(string title, string artist, string album)
    {
        var updater = _controls.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = title;
        updater.MusicProperties.Artist = artist;
        updater.MusicProperties.AlbumTitle = album;
        updater.Update();
    }

    public void SetPlaying(bool playing) =>
        _controls.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
}
