using Avalonia.Controls;
using JeekMusic.Library;

namespace JeekMusic.UI;

/// <summary>右键菜单里各功能的项（智能标签修正、歌曲信息、在线歌词 / 封面、文件操作）。</summary>
public partial class MainWindow
{
    partial void AddSongActions(ContextMenu menu, IReadOnlyList<Track> tracks)
    {
        AddTagActions(menu, tracks);
        AddOnlineActions(menu, tracks);
        AddFileActions(menu, tracks);
        if (tracks.Count == 1)
        {
            menu.Items.Add(new Separator());
            var info = new MenuItem { Header = "歌曲信息" };
            info.Click += (_, _) => _ = SongInfoWindow.Show(this, _app, tracks[0]);
            menu.Items.Add(info);
        }
    }

    partial void AddTagActions(ContextMenu menu, IReadOnlyList<Track> tracks);
    partial void AddOnlineActions(ContextMenu menu, IReadOnlyList<Track> tracks);
}
