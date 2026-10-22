using Avalonia.Controls;
using Avalonia.VisualTree;
using JeekMusic.Covers;
using JeekMusic.Infrastructure;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.UI;

public partial class MainWindow
{
    CoverService? _covers;
    readonly Dictionary<DataGridRow, CancellationTokenSource> _rowCoverRequests = new();
    readonly SemaphoreSlim _rowCoverSlots = new(4);

    /// <summary>只给表格已实现的行加载封面，滚出屏幕时取消并释放行持有的图片。</summary>
    public void SetCoverService(CoverService covers)
    {
        if (_covers is not null) _covers.Changed -= RefreshRowCovers;
        else Closed += (_, _) =>
        {
            if (_covers is not null) _covers.Changed -= RefreshRowCovers;
            foreach (var container in _rowCoverRequests.Keys.ToList()) CancelRowCover(container);
        };
        _covers = covers;
        _covers.Changed += RefreshRowCovers;
        RefreshRowCovers(null);
    }

    void RefreshRowCovers(string? key)
    {
        foreach (var container in Table.GetVisualDescendants().OfType<DataGridRow>())
            if (container.Tag is SongRow { Track: { } track } row && (key is null || CoverService.CacheKey(track) == key))
                LoadRowCover(container, row);
    }

    void CancelRowCover(DataGridRow container)
    {
        if (!_rowCoverRequests.Remove(container, out var request)) return;
        request.Cancel();
        request.Dispose();
    }

    async void LoadRowCover(DataGridRow container, SongRow row)
    {
        CancelRowCover(container);
        row.Cover = null;
        if (_covers is not { } covers || row.Track is not { } track) return;
        var request = new CancellationTokenSource();
        var ct = request.Token;
        _rowCoverRequests[container] = request;
        try
        {
            await _rowCoverSlots.WaitAsync(ct);
            try
            {
                var bitmap = await covers.BitmapAsync(track, large: false, ct);
                if (!ct.IsCancellationRequested && ReferenceEquals(container.Tag, row)) row.Cover = bitmap;
            }
            finally { _rowCoverSlots.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Warn("cover", "加载歌曲列表封面失败", e); }
        finally
        {
            if (_rowCoverRequests.TryGetValue(container, out var active) && ReferenceEquals(active, request))
            {
                _rowCoverRequests.Remove(container);
                request.Dispose();
            }
        }
    }
}
