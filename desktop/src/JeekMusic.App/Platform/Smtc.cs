using JeekMusic.Infrastructure;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace JeekMusic.Platform;

/// <summary>
/// 系统媒体传输控件（需求 O-1，设计 §7.5）：Windows 音量弹窗和锁屏上的媒体卡片、键盘媒体键、蓝牙耳机按键都走这里。
/// 非 UWP 程序要通过窗口句柄取得（验证过）。时间线只在换歌、跳转、暂停时更新，不随进度刷新（省电，设计 §13）。
/// </summary>
public sealed class Smtc
{
    readonly SystemMediaTransportControls _controls;
    string? _coverPath;

    public event Action<string>? ButtonPressed;

    public Smtc(nint hwnd)
    {
        _controls = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsNextEnabled = true;
        _controls.IsPreviousEnabled = true;
        _controls.ButtonPressed += (_, e) => ButtonPressed?.Invoke(e.Button.ToString());
        _controls.PlaybackPositionChangeRequested += (_, e) => PositionRequested?.Invoke(e.RequestedPlaybackPosition);
    }

    /// <summary>媒体卡片上拖动进度（部分系统界面支持）。</summary>
    public event Action<TimeSpan>? PositionRequested;

    public async void Show(string title, string artist, string album, string? coverPath)
    {
        try
        {
            var updater = _controls.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = title;
            updater.MusicProperties.Artist = artist;
            updater.MusicProperties.AlbumTitle = album;
            if (coverPath != _coverPath)
            {
                _coverPath = coverPath;
                updater.Thumbnail = coverPath is null
                    ? null
                    : RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(coverPath));
            }
            updater.Update();
        }
        catch (Exception e)
        {
            Log.Warn("smtc", "更新媒体信息失败", e);
        }
    }

    public void Clear()
    {
        _controls.DisplayUpdater.ClearAll();
        _controls.DisplayUpdater.Update();
        _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        _coverPath = null;
    }

    public void SetState(bool playing, TimeSpan position, TimeSpan duration)
    {
        _controls.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        _controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            Position = position,
            MaxSeekTime = duration,
            EndTime = duration,
        });
    }
}
