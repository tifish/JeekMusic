using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using JeekMusic.Library;

namespace JeekMusic.UI;

/// <summary>歌曲信息（同移动端 U-3、M-7）：路径、格式、码率、采样率、位深、时长、全部标签、智能标签和分析结果。</summary>
public static class SongInfoWindow
{
    public static async Task Show(Window owner, AppServices app, Track t)
    {
        var rows = new List<(string, string)>
        {
            (L.T("标题"), t.Title),
            (L.T("艺术家"), Text.Artist(t) + (t.ArtistInferred ? L.T("（推断自文件名）") : "")),
            (L.T("拆分后"), string.Join("、", t.Artists)),
            (L.T("专辑"), t.Album ?? ""),
            (L.T("专辑艺术家"), t.AlbumArtist ?? ""),
            (L.T("音轨号"), t.TrackNo?.ToString() ?? ""),
            (L.T("碟号"), t.DiscNo?.ToString() ?? ""),
            (L.T("年份"), t.Year?.ToString() ?? ""),
            (L.T("流派"), t.Genre ?? ""),
            (L.T("时长"), Text.Duration(t.DurationMs)),
            (L.T("格式"), L.F($"{t.Codec?.ToUpperInvariant()}  {t.SampleRate} Hz  {(t.BitsPerSample is { } b ? b + L.T(" 位") : "")}  {t.Channels} 声道  {(t.Bitrate is { } br ? br + " kbps" : "")}")),
            (L.T("路径"), app.Library.PathOf(t) ?? t.RelativePath + L.T("（所在的盘不可用）")),
            (L.T("大小"), $"{t.Size / 1048576.0:F1} MB"),
            (L.T("修改时间"), Text.Modified(t.Mtime)),
        };
        if (t.IsCue) rows.Add((L.T("CUE 整轨"), L.F($"第 {t.CueTrackNo} 首，{Text.Duration(t.StartMs ?? 0)} – {(t.EndMs is { } e ? Text.Duration(e) : L.T("结尾"))}")));
        if (app.Library.Snapshot.Tags.TryGetValue(t.TrackKey, out var tags))
            rows.Add((L.T("智能标签"), string.Join("  ", new[]
            {
                (tags.Mood, tags.MoodManual), (tags.Voice, tags.VoiceManual), (tags.Genre, tags.GenreManual), (tags.Calmness, tags.CalmnessManual),
            }.Where(x => x.Item1 is not null).Select(x => Text.TagName(x.Item1) + (x.Item2 ? L.T("（手动修正）") : "")))));
        ExtraRows?.Invoke(t, rows);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = rows[i].Item1, Opacity = 0.65, Margin = new Thickness(0, 3, 16, 3) };
            var value = new SelectableTextBlock { Text = rows[i].Item2, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3) };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        var close = new Button { Content = L.T("关闭"), IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), MinWidth = 80 };
        var w = new Window
        {
            Title = L.T("歌曲信息"), Width = 560, SizeToContent = SizeToContent.Height, MaxHeight = 760, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(20), Children = { grid, close } } },
        };
        close.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    /// <summary>其他功能补充的信息（智能标签的分析得分等）。</summary>
    public static Action<Track, List<(string, string)>>? ExtraRows;
}
