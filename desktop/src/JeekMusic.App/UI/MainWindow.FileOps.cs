using Avalonia.Controls;
using Avalonia.Platform.Storage;
using JeekMusic.Library;

namespace JeekMusic.UI;

/// <summary>改名、移动、删除（需求 F-10、G-6）。</summary>
public partial class MainWindow
{
    FileOperations? _fileOps;
    FileOperations FileOps => _fileOps ??= new FileOperations(_app.Library);

    long? PlayingId => _app.Player.CurrentTrack?.Id;

    async Task RenameSelected()
    {
        var tracks = SelectedTracks();
        if (tracks.Count != 1) return;
        var t = tracks[0];
        if (t.IsCue) { _vm.ShowToast(L.T("CUE 整轨里的歌共用一个文件，不能单独改名")); return; }
        if (t.Id == PlayingId) { _vm.ShowToast(L.T("正在播放的歌不能改名")); return; }
        var stem = Path.GetFileNameWithoutExtension(t.FileName);
        var name = await Dialogs.Prompt(this, L.T("改名"), L.T("新的文件名（不含扩展名；同名的歌词、图片一起改）"), stem);
        if (name is null || name == stem) return;
        var r = FileOps.Rename(t, name, PlayingId);
        _vm.ShowToast(r.Summary(L.T("改名")));
    }

    async Task MoveSelected()
    {
        var tracks = SelectedTracks();
        if (tracks.Count == 0) return;
        var start = _app.Library.Snapshot.Folder(tracks[0].FolderId)?.Root;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.T("移动到（只能选音乐文件夹或其中的子文件夹）"),
            SuggestedStartLocation = start is null ? null : await StorageProvider.TryGetFolderFromPathAsync(start),
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } target) return;
        var r = FileOps.Move(tracks, target, PlayingId);
        _vm.ShowToast(r.Summary(L.T("移动")));
    }

    async Task DeleteSelected()
    {
        var tracks = SelectedTracks();
        if (tracks.Count == 0) return;
        var what = tracks.Count == 1 ? $"「{tracks[0].Title}」" : L.F($"{tracks.Count} 首歌");
        if (!await Dialogs.Confirm(this, L.T("删除"), L.F($"把{what}移到回收站？同名的歌词、图片一起移过去，可以从回收站恢复。"), L.T("删除"), danger: true)) return;
        var r = FileOps.Delete(tracks, PlayingId, TryGetPlatformHandle()?.Handle ?? 0);
        _vm.ShowToast(r.Summary(L.T("删除")));
    }

    void AddFileActions(ContextMenu menu, IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        menu.Items.Add(new Separator());
        void Add(string header, Func<Task> action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
        }
        Add(L.T("改名…（F2）"), RenameSelected, tracks.Count == 1 && !tracks[0].IsCue);
        Add(L.T("移动到…"), MoveSelected, tracks.Any(t => !t.IsCue));
        Add(L.T("删除…（Delete）"), DeleteSelected, tracks.Any(t => !t.IsCue));
    }
}
