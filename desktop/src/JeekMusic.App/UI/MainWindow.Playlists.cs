using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using JeekMusic.Core.Library;
using JeekMusic.Core.Text;

namespace JeekMusic.UI;

/// <summary>播放列表的 m3u / m3u8 导入导出（需求 PL-4，规则同移动端 M3u.kt）。</summary>
public partial class MainWindow
{
    static readonly FilePickerFileType M3uType = new("播放列表") { Patterns = ["*.m3u", "*.m3u8"] };

    partial void AddPlaylistsExtras(ContextMenu menu)
    {
        var item = new MenuItem { Header = "导入 m3u…" };
        item.Click += async (_, _) => await ImportM3u();
        menu.Items.Add(item);
    }

    partial void AddPlaylistNodeExtras(ContextMenu menu, long playlistId)
    {
        var item = new MenuItem { Header = "导出为 m3u…" };
        item.Click += async (_, _) => await ExportM3u(playlistId);
        menu.Items.Add(item);
    }

    partial void AddPlaylistExtras(MenuFlyout menu, long playlistId)
    {
        var export = new MenuItem { Header = "导出为 m3u…" };
        export.Click += async (_, _) => await ExportM3u(playlistId);
        menu.Items.Add(export);
    }

    async Task ImportM3u()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "导入播放列表", AllowMultiple = true, FileTypeFilter = [M3uType] });
        var snapshot = _app.Library.Snapshot;
        // 曲库里每首歌的路径（"/" 分隔、去掉盘符），用后缀匹配对上 m3u 里的路径（同移动端）
        var refs = new Dictionary<string, string>();
        foreach (var t in snapshot.Tracks.Where(t => !t.IsCue))
            if (snapshot.PathOf(t) is { } path) refs.TryAdd(string.Join('/', path.Replace('\\', '/').Split('/').Where(s => s.Length > 0 && !s.EndsWith(':'))), t.TrackKey);
        var matcher = new BackupRefs.Matcher(refs);
        foreach (var f in files)
        {
            if (f.TryGetLocalPath() is not { } path) continue;
            var text = CharsetDetector.Decode(await File.ReadAllBytesAsync(path));
            var entries = M3u.Parse(text);
            var tracks = new List<Track>();
            var missing = 0;
            foreach (var segments in entries)
            {
                if (matcher.Match(string.Join('/', segments)) is { } key && snapshot.ByKey.TryGetValue(key, out var t)) tracks.Add(t);
                else missing++;
            }
            var id = _app.Playlists.Create(Path.GetFileNameWithoutExtension(path));
            _app.Playlists.Add(id, tracks);
            _vm.ShowToast($"已导入「{_app.Playlists.Get(id)?.Name}」：{tracks.Count} 首" + (missing > 0 ? $"，{missing} 首在曲库里找不到" : ""));
        }
    }

    async Task ExportM3u(long playlistId)
    {
        var playlist = _app.Playlists.Get(playlistId);
        if (playlist is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出播放列表", SuggestedFileName = playlist.Name + ".m3u8", DefaultExtension = "m3u8", FileTypeChoices = [M3uType],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        var entries = new List<M3u.Entry>();
        var skipped = 0;
        foreach (var e in _app.Playlists.Entries(playlistId))
        {
            // CUE 整轨里的歌 m3u 表示不了，不可用的条目也跳过
            if (e.Track is not { IsCue: false } t || _app.Library.PathOf(t) is not { } p) { skipped++; continue; }
            entries.Add(new M3u.Entry(p, t.DurationMs / 1000, $"{Text.Artist(t)} - {t.Title}"));
        }
        await File.WriteAllTextAsync(path, M3u.Format(entries), new UTF8Encoding(false));
        _vm.ShowToast($"已导出 {entries.Count} 首" + (skipped > 0 ? $"，跳过 {skipped} 首（CUE 整轨或不可用）" : ""));
    }
}
