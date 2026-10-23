using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using JeekMusic.Covers;
using JeekMusic.Infrastructure;
using JeekMusic.Library;

namespace JeekMusic.UI;

public partial class MainWindow
{
    readonly Dictionary<Image, CancellationTokenSource> _artistAvatarRequests = new();

    void OnArtistAvatarLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Image image) LoadArtistAvatar(image);
    }

    void OnArtistAvatarUnloaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Image image) CancelArtistAvatar(image);
    }

    void OnArtistAvatarDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is Image image) LoadArtistAvatar(image);
    }

    void RefreshArtistAvatars()
    {
        foreach (var image in ArtistList.GetVisualDescendants().OfType<Image>().Where(i => i.Name == "ArtistAvatarImage"))
            LoadArtistAvatar(image);
    }

    void CancelArtistAvatar(Image image)
    {
        if (_artistAvatarRequests.Remove(image, out var request))
        {
            request.Cancel();
            request.Dispose();
        }
        image.Source = null;
    }

    async void LoadArtistAvatar(Image image)
    {
        CancelArtistAvatar(image);
        if (!image.IsLoaded || _covers is not { } covers || image.DataContext is not ArtistInfo artist) return;
        var request = new CancellationTokenSource();
        var ct = request.Token;
        _artistAvatarRequests[image] = request;
        try
        {
            await _rowCoverSlots.WaitAsync(ct);
            try
            {
                // 优先取含内嵌封面的歌曲；只尝试少量代表歌曲，避免为了头像扫描整位艺术家的文件。
                var candidates = artist.Tracks.Where(t => t.HasEmbeddedCover).Take(1)
                    .Concat(artist.Tracks.Take(3)).DistinctBy(CoverService.CacheKey);
                foreach (var track in candidates)
                {
                    var bitmap = await covers.BitmapAsync(track, large: false, ct);
                    if (ct.IsCancellationRequested || !ReferenceEquals(image.DataContext, artist)) return;
                    if (bitmap is null) continue;
                    image.Source = bitmap;
                    break;
                }
            }
            finally { _rowCoverSlots.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Warn("cover", "加载艺术家头像失败", e); }
        finally
        {
            if (_artistAvatarRequests.TryGetValue(image, out var active) && ReferenceEquals(active, request))
            {
                _artistAvatarRequests.Remove(image);
                request.Dispose();
            }
        }
    }
}
