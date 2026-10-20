using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace JeekMusic.UI;

/// <summary>歌词面板（需求 W-6）、播放栏的其他按钮、拖放（§3.7）。</summary>
public partial class MainWindow
{
    LyricsPanel? _lyricsPanel;
    bool _lyricsFullPage;

    void InitExtras()
    {
        SettingsButton.Click += (_, _) => SettingsWindow.ShowFor(this);
        LyricsToggle.IsCheckedChanged += (_, _) => SetLyricsPanel(LyricsToggle.IsChecked == true);
        MiniButton.Click += (_, _) => Shell.Current.ShowMiniPlayer();
        SleepButton.Click += (_, _) => _ = SleepDialog.Show(this, _app);
        QueueButton.Click += (_, _) => QueueFlyout.Show(this, _app, QueueButton);

        // 拖放：音乐文件拖进窗口直接播放，文件夹添加为音乐文件夹（§3.7）
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0) Shell.Current.Open(paths);
        });
    }

    void SetLyricsPanel(bool visible)
    {
        if (visible && _lyricsPanel is null)
        {
            _lyricsPanel = new LyricsPanel(_app, _vm.Player, _app.Lyrics);
            _lyricsPanel.FullPageToggled += () => SetLyricsFullPage(!_lyricsFullPage);
            _lyricsPanel.AlbumRequested += GoToAlbum;
            LyricsPanelCreated?.Invoke(_lyricsPanel);
            LyricsHost.Content = _lyricsPanel;
        }
        LyricsHost.IsVisible = visible;
        LyricsSplitter.IsVisible = visible && !_lyricsFullPage;
        if (LyricsToggle.IsChecked != visible) LyricsToggle.IsChecked = visible;
        ApplyLyricsLayout();
    }

    /// <summary>歌词面板创建好了（在线歌词等功能接上它的事件）。</summary>
    public event Action<LyricsPanel>? LyricsPanelCreated;

    void SetLyricsFullPage(bool full)
    {
        _lyricsFullPage = full;
        ApplyLyricsLayout();
    }

    void ApplyLyricsLayout()
    {
        var full = _lyricsFullPage && LyricsHost.IsVisible;
        // 整页：歌词占满导航栏右边的区域
        MainArea.ColumnDefinitions[3].Width = full ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        MainArea.ColumnDefinitions[5].Width = full ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        LyricsHost.Width = full ? double.NaN : Math.Clamp(_lyricsWidth, 260, 720);
        LyricsSplitter.IsVisible = LyricsHost.IsVisible && !full;
        if (_lyricsPanel is not null) _lyricsPanel.FullPage = full;
    }

    double _lyricsWidth = 360;

    partial void RestoreExtraLayout(UiState ui)
    {
        _lyricsWidth = ui.LyricsWidth;
        _lyricsFullPage = ui.LyricsFullPage;
        if (ui.LyricsPanel) Avalonia.Threading.Dispatcher.UIThread.Post(() => SetLyricsPanel(true));
    }

    partial void SaveExtraLayout(UiState ui)
    {
        ui.LyricsPanel = LyricsHost.IsVisible;
        ui.LyricsFullPage = _lyricsFullPage;
        if (LyricsHost.IsVisible && !_lyricsFullPage) ui.LyricsWidth = LyricsHost.Bounds.Width;
    }
}
