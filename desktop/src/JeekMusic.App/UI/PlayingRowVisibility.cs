using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace JeekMusic.UI;

/// <summary>A bounded, layout-aware reveal. Reading layout never creates a new request.
/// Each owner supplies its existing virtualization/scroll operation and real viewport.</summary>
internal sealed class PlayingRowVisibility : IDisposable
{
    readonly Control _host;
    readonly Func<Control?> _viewport;
    readonly Func<long, bool> _contains;
    readonly Func<long, Control?> _row;
    readonly Action<long> _reveal;
    readonly Func<double, bool> _scrollBy;
    readonly Func<bool> _ready;
    readonly Func<int> _generation;
    readonly Func<long?> _current;
    long? _target, _lastTrack;
    int _view, _passes, _layout, _lastLayout = -1;
    bool _queued, _disposed;
    bool? _center;
    int _decisionLayout;
    string _result = "no-request";
    public long RequestVersion { get; private set; }

    public PlayingRowVisibility(Control host, Func<Control?> viewport, Func<long, bool> contains,
        Func<long, Control?> row, Action<long> reveal, Func<double, bool> scrollBy,
        Func<bool> ready, Func<int> generation, Func<long?> current)
    {
        (_host, _viewport, _contains, _row, _reveal, _ready, _generation, _current) =
            (host, viewport, contains, row, reveal, ready, generation, current);
        _scrollBy = scrollBy;
        host.LayoutUpdated += OnLayout;
        host.AddHandler(InputElement.PointerPressedEvent, OnPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        host.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        host.AddHandler(InputElement.KeyDownEvent, OnKey, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public void Request(long trackId)
    {
        if (_disposed) return;
        RequestVersion++;
        _target = _lastTrack = trackId;
        _view = _generation();
        _passes = 0;
        _lastLayout = -1;
        // Freeze the original visibility decision before ScrollIntoView realizes/moves anything.
        // For replacement rows, wait for a layout of the new source before deciding.
        _decisionLayout = _layout;
        _center = _ready() && HostLayoutValid() && _viewport() is { Bounds.Height: > 0 }
            ? !FullyVisible(trackId) : null;
        _result = "pending";
        Refresh();
    }

    bool HostLayoutValid() => _host.IsMeasureValid && _host.IsArrangeValid;

    public void Cancel(string reason)
    {
        if (_target is null) return;
        _target = null;
        _result = reason;
    }

    void OnPointer(object? sender, PointerPressedEventArgs e) => Cancel("user-input");
    void OnWheel(object? sender, PointerWheelEventArgs e) => Cancel("user-input");
    void OnKey(object? sender, KeyEventArgs e) => Cancel("user-input");
    void OnLayout(object? sender, EventArgs e) { _layout++; Refresh(); }

    public void Refresh()
    {
        if (_queued || _disposed || _target is null) return;
        _queued = true;
        var version = RequestVersion;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            if (_disposed) return;
            if (version != RequestVersion) { Refresh(); return; }
            Apply();
        }, DispatcherPriority.Loaded);
    }

    bool FullyVisible(long id)
    {
        if (!_host.IsEffectivelyVisible || _viewport() is not { Bounds.Height: > 0 } viewport ||
            _row(id) is not { } row || !row.IsEffectivelyVisible || row.Bounds.Height <= 0 ||
            row.TranslatePoint(default, viewport) is not { } p) return false;
        return p.Y >= -.5 && p.Y + row.Bounds.Height <= viewport.Bounds.Height + .5;
    }

    void Apply()
    {
        if (_target is not { } id) return;
        if (_view != _generation()) { Cancel("view-changed"); return; }
        if (_current() != id) { Cancel("track-changed"); return; }
        if (!_host.IsEffectivelyVisible) { Cancel("view-hidden"); return; }
        if (!_ready()) return; // RowsChanged wakes this request after the async computation.
        if (!_contains(id)) { Cancel("not-in-current-view"); return; }
        if (_viewport() is not { Bounds.Height: > 0 } viewport) return; // First layout wakes it.
        if (_center is null)
        {
            if (_layout == _decisionLayout || !HostLayoutValid()) { _host.InvalidateMeasure(); return; }
            _center = !FullyVisible(id);
        }
        if (_center == false) { Cancel("already-visible"); return; }
        if (_row(id) is { } row && row.Bounds.Height > viewport.Bounds.Height + .5)
        {
            _reveal(id);
            Cancel("row-taller-than-viewport");
            return;
        }
        if (_passes > 0 && _lastLayout == _layout) return;
        if (_passes >= 6) { Cancel("layout-limit"); return; }
        _lastLayout = _layout;
        _passes++;
        if (_row(id) is { Bounds.Height: > 0, IsEffectivelyVisible: true } realized &&
            realized.TranslatePoint(default, viewport) is { } p)
        {
            var delta = p.Y + realized.Bounds.Height / 2 - viewport.Bounds.Height / 2;
            if (Math.Abs(delta) <= .5) { Cancel("centered"); return; }
            if (!_scrollBy(delta)) { Cancel(FullyVisible(id) ? "clamped" : "layout-limit"); return; }
        }
        else _reveal(id);
        // A virtualized row can be realized now and arranged in the following layout.
        Refresh();
    }

    internal JsonObject Diagnostics()
    {
        var id = _current();
        return new JsonObject
        {
            ["created"] = !_disposed, ["visible"] = !_disposed && _host.IsEffectivelyVisible,
            ["currentTrackId"] = id, ["containsCurrent"] = id is { } t && _ready() && _contains(t),
            ["currentFullyVisible"] = !_disposed && _ready() && id is { } c && FullyVisible(c),
            ["requestedTrackId"] = _lastTrack, ["requestVersion"] = RequestVersion,
            ["positionPending"] = _target is not null, ["result"] = _result,
            ["layoutPasses"] = _passes, ["rowsReady"] = _ready(),
            ["alignment"] = _lastTrack is null ? "none" : _center is null ? "awaiting-layout" : _center.Value ? "center" : "keep",
            ["initiallyFullyVisible"] = _center is null ? null : !_center.Value,
            ["centerErrorY"] = id is { } song && _ready() && _viewport() is { Bounds.Height: > 0 } vp &&
                _row(song) is { Bounds.Height: > 0 } r && r.TranslatePoint(default, vp) is { } point
                    ? point.Y + r.Bounds.Height / 2 - vp.Bounds.Height / 2 : (double?)null
        };
    }

    public void Dispose()
    {
        Cancel("closed");
        _disposed = true;
        _host.LayoutUpdated -= OnLayout;
        _host.RemoveHandler(InputElement.PointerPressedEvent, OnPointer);
        _host.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
        _host.RemoveHandler(InputElement.KeyDownEvent, OnKey);
    }
}
