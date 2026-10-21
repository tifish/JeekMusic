using Avalonia.Controls;
using JeekMusic.Analysis;
using JeekMusic.Core.Analysis;
using JeekMusic.Library;

namespace JeekMusic.UI;

public partial class MainWindow
{
    partial void AddTagActions(ContextMenu menu, IReadOnlyList<Track> tracks)
    {
        var root = new MenuItem { Header = L.T("修正智能标签") };
        foreach (var dimension in Enum.GetValues<TagDimension>())
        {
            var group = new MenuItem { Header = dimension switch { TagDimension.MOOD => L.T("运动"), TagDimension.VOICE => L.T("人声"), _ => L.T("曲风") } };
            foreach (var tag in Tags.Of(dimension).Select(t => (Tag?)t).Append(null))
            {
                var item = new MenuItem { Header = tag is { } value ? Text.TagName(value.ToString()) : L.T("恢复自动") };
                item.Click += async (_, _) => { if (AnalysisFeature.Current is { } feature) await feature.Override(tracks, dimension, tag); };
                group.Items.Add(item);
            }
            root.Items.Add(group);
        }
        menu.Items.Add(root);
    }
}
