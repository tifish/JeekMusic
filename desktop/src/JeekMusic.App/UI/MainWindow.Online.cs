using Avalonia.Controls;
using JeekMusic.Library;
using JeekMusic.Online;

namespace JeekMusic.UI;

public partial class MainWindow
{
    partial void AddListExtras(MenuFlyout menu)
    {
        if (_app.Online is not { } feature) return;
        var tracks = _vm.CaptureResourceTracks();
        var source = _vm.ContentTitle;
        menu.Items.Add(new Separator());
        foreach (var (label, kind) in new[] { ("获取全部封面", DownloadKind.Covers), ("获取全部歌词", DownloadKind.Lyrics) })
        {
            var item = new MenuItem { Header = L.T(label), IsEnabled = tracks.Length > 0 && !feature.Batches.Busy };
            ToolTip.SetTip(item, feature.Batches.Busy ? L.T("已有批量获取任务，请等待完成或取消。") :
                L.F($"当前列表全部 {tracks.Length} 首歌曲（遵循筛选；已有资源优先，不强制覆盖）"));
            item.Click += async (_, _) => await feature.Batch(this, tracks, kind, "current_list", source);
            menu.Items.Add(item);
        }
        if (feature.Batches.Busy)
        {
            var cancel = new MenuItem { Header = L.T("取消批量获取") };
            cancel.Click += (_, _) => feature.Batches.Cancel();
            menu.Items.Add(cancel);
        }
    }

    partial void AddOnlineActions(ContextMenu menu, IReadOnlyList<Track> tracks)
    {
        if (_app.Online is not { } feature) return;
        menu.Items.Add(new Separator());
        void Item(string name, Func<Task> action, bool enabled = true)
        {
            var item = new MenuItem { Header = name, IsEnabled = enabled };
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
        }
        if (tracks.Count == 1)
        {
            Item(L.T("搜索歌词…"), () => feature.SearchLyrics(this, tracks[0]));
            Item(L.T("搜索封面…"), () => feature.SearchCover(this, tracks[0]));
        }
        Item(L.T("获取缺失歌词"), () => feature.Batch(this, tracks, DownloadKind.Lyrics), tracks.Count > 0 && !feature.Batches.Busy);
        Item(L.T("获取缺失封面"), () => feature.Batch(this, tracks, DownloadKind.Covers), tracks.Count > 0 && !feature.Batches.Busy);
    }
}

public sealed partial class Shell
{
    partial void CoverPathOf(Track track, ref string? path) => path = OnlineFeature.Current?.Covers.CachedPathIfReady(track);
    public void RefreshMediaCover() => UpdateSystemMedia(trackChanged: true);
}
