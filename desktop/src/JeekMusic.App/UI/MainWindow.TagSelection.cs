using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekMusic.Core.Analysis;
using JeekMusic.Library;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.UI;

public partial class MainWindow
{
    // Playlist entries can repeat a recording. Other views use recording identity across reordering.
    internal readonly record struct TagRowKey(string? Key, int Entry);
    internal sealed class TagSelectionContext(TagRowKey[] order, int anchor, long view, long selection)
    {
        internal readonly TagRowKey[] Order = order;
        internal readonly int Anchor = anchor;
        internal readonly long View = view;
        internal long Selection = selection;
    }
    TagRowKey RowKey(SongRow row) => new(row.Track?.TrackKey ?? row.MissingKey,
        _vm.IsPlaylistView ? row.Position : -1);
    long _songSelectionRevision, _displayedViewRevision, _tagCorrectionRevision;
    bool _replacingSongs, _settingSongSelection;
    TagRowKey[] _retainedSelection = [];
    (TagRowKey Key, double Y)? _retainedViewport;
    (TagSelectionContext Context, int Snapshot)? _pendingTagSelection;
    string _tagSelectionResult = "idle";

    void InitTagSelection()
    {
        Table.SelectionChanged += (_, _) =>
        {
            if (_replacingSongs || _settingSongSelection) return;
            _songSelectionRevision++;
            if (_pendingTagSelection is not null) _tagSelectionResult = "selection-changed";
            _pendingTagSelection = null;
        };
        _vm.RowsReplacing += BeforeSongRowsReplace;
        _vm.RowsChanged += AfterSongRowsReplace;
        _app.CorrectSmartTags = (tracks, changes, reason) => ApplyTagCorrection(tracks, changes, CaptureTagSelection(tracks), reason);
        Closed += (_, _) =>
        {
            _pendingTagSelection = null;
            _vm.RowsReplacing -= BeforeSongRowsReplace;
            _vm.RowsChanged -= AfterSongRowsReplace;
            _app.CorrectSmartTags = null;
        };
    }

    internal TagSelectionContext CaptureTagSelection(IReadOnlyList<Track> targets)
    {
        var ids = targets.Select(t => t.Id).ToHashSet();
        var rows = _vm.Rows;
        // SelectedItems has selection/insertion order, which need not match the visible list order.
        var anchor = _displayedViewRevision == _vm.ViewRevision
            ? Enumerable.Range(0, rows.Count).FirstOrDefault(i => rows[i].Track is { } t && ids.Contains(t.Id), -1) : -1;
        return new(rows.Select(RowKey).ToArray(), anchor, _vm.ViewRevision, _songSelectionRevision);
    }

    internal async Task ApplyTagCorrection(IReadOnlyList<Track> targets,
        IReadOnlyDictionary<TagDimension, Tag?> changes, TagSelectionContext context, string? reason = null)
    {
        var operation = ++_tagCorrectionRevision;
        _vm.TagCorrectionDepth++;
        try
        {
            await _app.SmartTags.Override(targets, changes, reason);
            // RefreshAsync posts the committed snapshot before completing. Process that post before
            // selecting; a newer/cancelled compute must never use the old pre-save filtered rows.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (context.Anchor < 0 || operation != _tagCorrectionRevision) return;
            _pendingTagSelection = (context, _app.Library.Snapshot.Version);
            _tagSelectionResult = "waiting-for-rows";
            TryCompleteTagSelection();
        }
        finally { _vm.TagCorrectionDepth--; }
    }

    void BeforeSongRowsReplace()
    {
        _replacingSongs = true;
        _retainedSelection = _displayedViewRevision == _vm.ViewRevision
            ? Table.SelectedItems.OfType<SongRow>().Select(RowKey).ToArray() : [];
        _retainedViewport = null;
        if (_displayedViewRevision != _vm.ViewRevision || SongViewport() is not { } viewport) return;
        var first = Table.GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.DataContext is SongRow && r.TranslatePoint(default, viewport) is { } p && p.Y + r.Bounds.Height > .5 && p.Y < viewport.Bounds.Height)
            .OrderBy(r => r.TranslatePoint(default, viewport)!.Value.Y).FirstOrDefault();
        if (first?.DataContext is SongRow row)
            _retainedViewport = (RowKey(row), first.TranslatePoint(default, viewport)!.Value.Y);
    }

    void AfterSongRowsReplace()
    {
        try
        {
            // Compiled binding delivery can trail RowsChanged. Apply the identical value now,
            // keeping the binding, so its deferred DataGrid reset cannot clear our new selection.
            Table.SetCurrentValue(DataGrid.ItemsSourceProperty, _vm.Rows);
            Table.UpdateLayout(); // Flush DataGrid's deferred row reset even when the old top row vanished.
            Table.SelectedItems.Clear();
            var wanted = _retainedSelection.ToHashSet();
            foreach (var row in _vm.Rows.Where(r => wanted.Contains(RowKey(r)))) Table.SelectedItems.Add(row);
            if (_retainedViewport is { } retained && _vm.Rows.FirstOrDefault(r => RowKey(r) == retained.Key) is { } top)
            {
                Table.ScrollIntoView(top, null);
                Table.UpdateLayout();
                if (SongViewport() is { } viewport && SongContainer(top) is { } container && container.TranslatePoint(default, viewport) is { } point)
                    viewport.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(), new Vector(0, point.Y - retained.Y)));
            }
        }
        finally
        {
            _replacingSongs = false;
            _displayedViewRevision = _vm.ViewRevision;
            _retainedSelection = [];
            _retainedViewport = null;
        }
        TryCompleteTagSelection();
    }

    void TryCompleteTagSelection()
    {
        if (_pendingTagSelection is not { } pending) return;
        var context = pending.Context;
        if (context.View != _vm.ViewRevision || context.Selection != _songSelectionRevision || _songsClosed)
        {
            _pendingTagSelection = null;
            _tagSelectionResult = context.View != _vm.ViewRevision ? "view-changed" : "selection-changed";
            return;
        }
        if (_vm.ComputingRows || _vm.RowsSnapshotVersion < pending.Snapshot) return;
        _pendingTagSelection = null; // Consume once, before selection events or further refreshes.
        var visible = _vm.Rows.Where(r => r.Available).ToDictionary(RowKey);
        SongRow? next = null;
        for (var i = context.Anchor + 1; i < context.Order.Length && next is null; i++) visible.TryGetValue(context.Order[i], out next);
        if (next is null)
            for (var i = context.Anchor - 1; i >= 0 && next is null; i--) visible.TryGetValue(context.Order[i], out next);
        if (next is null) visible.TryGetValue(context.Order[context.Anchor], out next);
        _settingSongSelection = true;
        try
        {
            Table.SelectedItems.Clear();
            Table.SelectedItem = next;
            // Do not take keyboard focus from an open correction menu/dialog. Its targets remain
            // captured for subsequent dimensions; the next menu will use this new selection.
            if (next is not null && !SongFullyVisible(next)) Table.ScrollIntoView(next, null);
            _tagSelectionResult = next is null ? "empty-or-no-surviving-neighbor" : "selected-neighbor";
        }
        finally { _settingSongSelection = false; }
    }

    DataGridRowsPresenter? SongViewport() => Table.GetVisualDescendants().OfType<DataGridRowsPresenter>().FirstOrDefault();
    DataGridRow? SongContainer(SongRow row) => Table.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault(r => ReferenceEquals(r.DataContext, row));
    bool SongFullyVisible(SongRow row) => SongViewport() is { } viewport && SongContainer(row) is { } container &&
        container.TranslatePoint(default, viewport) is { } p && p.Y >= -.5 && p.Y + container.Bounds.Height <= viewport.Bounds.Height + .5;

    JsonObject TagSelectionDiagnostics() => new()
    {
        ["policy"] = "first-target-in-original-list-next-survivor-then-previous-then-anchor",
        ["result"] = _tagSelectionResult, ["pending"] = _pendingTagSelection is not null,
        ["selectedTrackIds"] = new JsonArray(Table.SelectedItems.OfType<SongRow>().Where(r => r.Track is not null).Select(r => (JsonNode?)JsonValue.Create(r.Track!.Id)).ToArray()),
        ["selectedFullyVisible"] = Table.SelectedItem is SongRow row && SongFullyVisible(row),
        ["rowsGeneration"] = _vm.RowsGeneration, ["computingRows"] = _vm.ComputingRows,
    };
}
