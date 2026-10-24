using Avalonia.Controls;
using JeekMusic.Library;

namespace JeekMusic.UI;

public partial class MainWindow
{
    partial void AddTagActions(ContextMenu menu, IReadOnlyList<Track> tracks) => menu.Items.Add(TagCorrectionItem(tracks));

    internal MenuItem TagCorrectionItem(IReadOnlyList<Track> tracks)
    {
        var item = new MenuItem { Header = L.T("修正智能标签…"), IsEnabled = tracks.Count > 0 };
        item.Click += async (_, _) => await new SmartTagDialog(_app, tracks).ShowDialog(this);
        return item;
    }
}
