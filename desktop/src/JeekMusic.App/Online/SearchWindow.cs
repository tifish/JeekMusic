using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using JeekMusic.Library;
using JeekMusic.UI;

namespace JeekMusic.Online;

/// <summary>用户确认候选后才保存；换查询和关闭窗口时取消旧请求。</summary>
public static class SearchWindow
{
    public static Task ShowLyrics(Window owner, OnlineFeature feature, Track track) => Show(owner, feature, track, false);
    public static Task ShowCover(Window owner, OnlineFeature feature, Track track) => Show(owner, feature, track, true);

    static async Task Show(Window owner, OnlineFeature feature, Track track, bool cover)
    {
        using var lifetime = new CancellationTokenSource();
        var sources = cover ? feature.CoverClient.EnabledSources() : feature.LyricsClient.EnabledSources();
        var title = new TextBox { Text = track.Title, PlaceholderText = L.T("歌名"), Width = 260 };
        var artist = new TextBox { Text = track.ArtistDisplay, PlaceholderText = L.T("艺术家"), Width = 200 };
        var source = new ComboBox { ItemsSource = new[] { L.T("所有来源") }.Concat(sources.Select(Sources.Name)).ToList(), SelectedIndex = 0, MinWidth = 160 };
        var results = new ListBox { MinHeight = 150 };
        var preview = new ContentControl();
        var status = SettingsWindow.Note(L.T("选择来源后搜索；选中结果可预览。"));
        var search = new Button { Content = L.T("搜索") };
        var save = new Button { Content = L.T("保存所选"), IsEnabled = false };
        var window = new Window { Title = cover ? L.T("搜索封面") : L.T("搜索歌词"), Width = 780, Height = 680, MinWidth = 650, MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,2*,3*,Auto"), Margin = new Thickness(16) };
        root.Children.Add(SettingsWindow.Row(title, artist, search));
        void Add(Control control, int row) { Grid.SetRow(control, row); root.Children.Add(control); }
        Add(source, 1); Add(results, 2); Add(preview, 3); Add(SettingsWindow.Row(save, status), 4);
        window.Content = root;
        var candidates = new List<object>();
        byte[]? selectedImage = null;
        Bitmap? bitmap = null;
        var thumbnails = new List<Bitmap>();
        int queryGeneration = 0;
        async Task LoadThumbnail(Image image, string url)
        {
            var query = queryGeneration;
            try
            {
                var bytes = await Http.BytesAsync(url, ct: lifetime.Token);
                if (bytes is null || lifetime.IsCancellationRequested || query != queryGeneration) return;
                var scaled = await Task.Run(() => Covers.CoverImages.Scaled(bytes, 72), lifetime.Token);
                if (scaled is null || lifetime.IsCancellationRequested || query != queryGeneration) return;
                using var stream = new MemoryStream(scaled);
                var thumb = new Bitmap(stream); thumbnails.Add(thumb); image.Source = thumb;
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }
        results.ItemTemplate = new FuncDataTemplate<object>((candidate, scope) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(4) };
            if (candidate is CoverCandidate art)
            {
                var thumb = new Image { Width = 48, Height = 48, Stretch = Stretch.UniformToFill };
                row.Children.Add(thumb); _ = LoadThumbnail(thumb, art.ThumbUrl);
                row.Children.Add(new TextBlock { Text = $"{art.Title} — {art.Artist}\n{Sources.Name(art.Source)} · {art.Album}", VerticalAlignment = VerticalAlignment.Center });
            }
            else if (candidate is OnlineLyrics lyric)
                row.Children.Add(new TextBlock { Text = $"{lyric.TrackName} — {lyric.ArtistName}\n{Sources.Name(lyric.Source)} · {(lyric.IsSynced ? L.T("时间轴") : L.T("纯文本"))}" });
            return row;
        });
        int generation = 0;
        search.Click += async (_, _) =>
        {
            if (source.SelectedIndex < 0) return;
            generation++;
            queryGeneration++;
            selectedImage = null;
            search.IsEnabled = false; save.IsEnabled = false;
            results.ItemsSource = null; candidates.Clear(); preview.Content = null;
            bitmap?.Dispose(); bitmap = null;
            foreach (var thumb in thumbnails) thumb.Dispose();
            thumbnails.Clear();
            status.Text = L.T("搜索中…");
            try
            {
                var selected = source.SelectedIndex == 0 ? sources : new[] { sources[source.SelectedIndex - 1] };
                int failed = 0;
                var pending = selected.Select(async s =>
                {
                    try
                    {
                        return cover ? (await feature.CoverClient.SearchAsync(s, title.Text ?? "", artist.Text, lifetime.Token)).Cast<object>().ToList()
                            : (await feature.LyricsClient.SearchAsync(s, title.Text ?? "", artist.Text, lifetime.Token)).Cast<object>().ToList();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { failed++; return new List<object>(); }
                }).ToList();
                var sourceCount = pending.Count;
                while (pending.Count > 0)
                {
                    var finished = await Task.WhenAny(pending);
                    pending.Remove(finished);
                    var incoming = await finished;
                    if (lifetime.IsCancellationRequested) return;
                    var chosen = results.SelectedItem;
                    candidates = candidates.Concat(incoming).OrderByDescending(c => c is CoverCandidate art ? art.Score : c is OnlineLyrics lyric && lyric.Matched ? 2 : 0).ToList();
                    results.ItemsSource = candidates;
                    if (chosen is not null) results.SelectedItem = chosen;
                    status.Text = L.F($"找到 {candidates.Count} 项");
                }
                if (failed == sourceCount) throw new IOException(L.T("连接失败"));
                status.Text = candidates.Count == 0 ? L.T("没有找到") : L.F($"找到 {candidates.Count} 项");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { status.Text = L.T("搜索失败：") + e.Message; }
            finally { search.IsEnabled = true; }
        };
        results.SelectionChanged += async (_, _) =>
        {
            var version = ++generation;
            save.IsEnabled = false; selectedImage = null; preview.Content = null;
            bitmap?.Dispose(); bitmap = null;
            if (results.SelectedIndex < 0 || results.SelectedIndex >= candidates.Count) return;
            var candidate = candidates[results.SelectedIndex];
            if (candidate is OnlineLyrics l)
            {
                preview.Content = new ScrollViewer { Content = new TextBlock { Text = l.Text, TextWrapping = TextWrapping.Wrap } };
                save.IsEnabled = l.Text is not null;
            }
            else if (candidate is CoverCandidate c)
            {
                try
                {
                    var bytes = await feature.CoverClient.DownloadImageAsync(c, lifetime.Token);
                    if (version != generation || lifetime.IsCancellationRequested) return;
                    if (bytes is null) { status.Text = L.T("图片下载失败"); return; }
                    using var stream = new MemoryStream(bytes);
                    bitmap = new Bitmap(stream); selectedImage = bytes;
                    preview.Content = new Image { Source = bitmap, Stretch = Stretch.Uniform };
                    save.IsEnabled = true;
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { status.Text = L.T("预览失败：") + e.Message; }
            }
        };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                if (cover && selectedImage is { } bytes) await feature.SaveCover(track, bytes);
                else if (results.SelectedIndex >= 0 && candidates[results.SelectedIndex] is OnlineLyrics { Text: { } text }) await feature.SaveLyrics(track, text);
                else return;
                window.Close();
            }
            catch (Exception e) { status.Text = L.T("保存失败：") + e.Message; save.IsEnabled = true; }
        };
        window.Closed += (_, _) => { lifetime.Cancel(); bitmap?.Dispose(); foreach (var thumb in thumbnails) thumb.Dispose(); };
        await window.ShowDialog(owner);
    }
}
