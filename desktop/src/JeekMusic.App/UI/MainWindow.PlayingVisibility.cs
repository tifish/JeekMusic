using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekMusic.Library;

namespace JeekMusic.UI;

public partial class MainWindow
{
    PlayingRowVisibility _songVisibility = null!;
    bool _songsClosed;

    void InitPlayingVisibility()
    {
        _songVisibility = new PlayingRowVisibility(Table,
            () => Table.GetVisualDescendants().OfType<DataGridRowsPresenter>().FirstOrDefault(),
            id => _vm.Rows.Any(r => r.Track?.Id == id),
            id => Table.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault(r =>
                r.DataContext is ViewModels.SongRow song && song.Track?.Id == id),
            id => { if (_vm.Rows.FirstOrDefault(r => r.Track?.Id == id) is { } row) Table.ScrollIntoView(row, null); },
            () => !_vm.ComputingRows, () => _vm.RowsGeneration, () => _app.Player.CurrentTrack?.Id);
        _app.SongVisibility = _songVisibility;
        _app.Player.PlaybackStarted += RevealPlayingRow;
        _vm.RowsInvalidated += CancelPlayingReveal;
        _vm.RowsChanged += _songVisibility.Refresh;
        Closed += (_, _) =>
        {
            _songsClosed = true;
            _app.Player.PlaybackStarted -= RevealPlayingRow;
            _vm.RowsInvalidated -= CancelPlayingReveal;
            _vm.RowsChanged -= _songVisibility.Refresh;
            _songVisibility.Dispose();
            if (ReferenceEquals(_app.SongVisibility, _songVisibility)) _app.SongVisibility = null;
        };
    }

    void RevealPlayingRow(Track track) => _songVisibility.Request(track.Id);
    void CancelPlayingReveal() => _songVisibility.Cancel("view-changed");

    void OnSongsNavigated()
    {
        var generation = _vm.RowsGeneration;
        var request = _songVisibility.RequestVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_songsClosed && _vm.RowsGeneration == generation && _songVisibility.RequestVersion == request && Table.ItemsSource is not null)
                ScrollToTop();
        }, DispatcherPriority.Background);
    }
}
