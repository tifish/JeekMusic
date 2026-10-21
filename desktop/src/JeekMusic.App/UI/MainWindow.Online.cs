using Avalonia.Controls;
using JeekMusic.Library;
using JeekMusic.Online;

namespace JeekMusic.UI;

public partial class MainWindow
{
    partial void AddOnlineActions(ContextMenu menu, IReadOnlyList<Track> tracks)
    {
        if (OnlineFeature.Current is not { } feature) return;
        menu.Items.Add(new Separator());
        void Item(string name, Func<Task> action)
        {
            var item = new MenuItem { Header = name };
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
        }
        if (tracks.Count == 1)
        {
            Item(L.T("搜索歌词…"), () => feature.SearchLyrics(this, tracks[0]));
            Item(L.T("搜索封面…"), () => feature.SearchCover(this, tracks[0]));
        }
        Item(L.T("获取缺失歌词"), () => feature.Batch(this, tracks, DownloadKind.Lyrics));
        Item(L.T("获取缺失封面"), () => feature.Batch(this, tracks, DownloadKind.Covers));
    }
}

public sealed partial class Shell
{
    partial void CoverPathOf(Track track, ref string? path) => path = OnlineFeature.Current?.Covers.CachedPathIfReady(track);
    public void RefreshMediaCover() => UpdateSystemMedia(trackChanged: true);
}
