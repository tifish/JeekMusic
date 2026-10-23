using System.Collections.Specialized;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using JeekMusic.Library;

namespace JeekMusic.UI;

public sealed partial class ArtistLetter(string letter, int firstIndex, int count) : ObservableObject
{
    public string Letter { get; } = letter;
    public int FirstIndex { get; } = firstIndex;
    public int Count { get; } = count;
    [ObservableProperty] bool _isCurrent;
}

/// <summary>ArtistInfo remains the selection unit; headers decorate existing containers.
/// Only realized containers are inspected after layout, never estimated scroll percentages.</summary>
public sealed class ArtistListBox : ListBox
{
    public static readonly AttachedProperty<string?> GroupHeaderProperty =
        AvaloniaProperty.RegisterAttached<ArtistListBox, Control, string?>("GroupHeader");
    public static readonly AttachedProperty<bool> HasGroupHeaderProperty =
        AvaloniaProperty.RegisterAttached<ArtistListBox, Control, bool>("HasGroupHeader");
    public static string? GetGroupHeader(Control control) => control.GetValue(GroupHeaderProperty);
    public static void SetGroupHeader(Control control, string? value) => control.SetValue(GroupHeaderProperty, value);
    public static bool GetHasGroupHeader(Control control) => control.GetValue(HasGroupHeaderProperty);
    public static void SetHasGroupHeader(Control control, bool value) => control.SetValue(HasGroupHeaderProperty, value);

    public static readonly DirectProperty<ArtistListBox, string?> CurrentLetterProperty =
        AvaloniaProperty.RegisterDirect<ArtistListBox, string?>(nameof(CurrentLetter), o => o.CurrentLetter);
    public static readonly DirectProperty<ArtistListBox, IReadOnlyList<ArtistLetter>> GroupsProperty =
        AvaloniaProperty.RegisterDirect<ArtistListBox, IReadOnlyList<ArtistLetter>>(nameof(Groups), o => o.Groups);
    string? _currentLetter;
    IReadOnlyList<ArtistLetter> _groups = [];
    public string? CurrentLetter => _currentLetter;
    public IReadOnlyList<ArtistLetter> Groups => _groups;
    ScrollViewer? _scroll;
    ScrollContentPresenter? _viewport;
    bool _queued, _attached;
    int? _targetIndex;
    int _alignmentPasses;
    int _firstVisible = -1, _lastVisible = -1;

    protected override Type StyleKeyOverride => typeof(ListBox);

    public ArtistListBox()
    {
        Items.CollectionChanged += OnItemsChanged;
        // Tunnel handlers also see input consumed by the scrollbar or touch recognizer.
        AddHandler(PointerPressedEvent, (_, _) => _targetIndex = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, (_, _) => _targetIndex = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, _) => _targetIndex = null, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _targetIndex = null;
        var groups = new List<ArtistLetter>();
        for (var i = 0; i < ItemsView.Count;)
        {
            if (ItemsView[i] is not ArtistInfo artist) { i++; continue; }
            var first = i++;
            while (i < ItemsView.Count && ItemsView[i] is ArtistInfo next && next.IndexLetter == artist.IndexLetter) i++;
            groups.Add(new ArtistLetter(artist.IndexLetter, first, i - first));
        }
        SetAndRaise(GroupsProperty, ref _groups, groups);
        SetCurrent(null);
        _firstVisible = _lastVisible = -1;
        foreach (var container in GetRealizedContainers()) Decorate(container, IndexFromContainer(container));
        QueueRead();
    }

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        Decorate(container, index);
    }

    protected override void ContainerIndexChangedOverride(Control container, int oldIndex, int newIndex)
    {
        base.ContainerIndexChangedOverride(container, oldIndex, newIndex);
        Decorate(container, newIndex);
    }

    void Decorate(Control container, int index)
    {
        var letter = index >= 0 && index < ItemsView.Count && ItemsView[index] is ArtistInfo artist &&
            (index == 0 || ItemsView[index - 1] is not ArtistInfo previous || previous.IndexLetter != artist.IndexLetter)
            ? artist.IndexLetter : null;
        SetGroupHeader(container, letter);
        SetHasGroupHeader(container, letter is not null);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
        base.OnApplyTemplate(e);
        _scroll = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
        _viewport = null;
        if (_attached && _scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
        QueueRead();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        LayoutUpdated += OnLayoutUpdated;
        if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
        QueueRead();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _targetIndex = null;
        LayoutUpdated -= OnLayoutUpdated;
        if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
        base.OnDetachedFromVisualTree(e);
    }

    void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => QueueRead();
    void OnLayoutUpdated(object? sender, EventArgs e) => QueueRead();

    void QueueRead()
    {
        if (_queued || !_attached || !IsEffectivelyVisible) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            if (!_attached || !IsEffectivelyVisible) { _targetIndex = null; return; }
            ReadViewport();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Reveal a group without changing selection, then align its realized header.
    /// At the bottom the offset is clamped; the visible top, not the requested letter, wins.</summary>
    public void NavigateToLetter(string letter)
    {
        if (!_attached || !IsEffectivelyVisible || Groups.FirstOrDefault(g => g.Letter == letter) is not { } group) return;
        _targetIndex = group.FirstIndex;
        _alignmentPasses = 0;
        ScrollIntoView(group.FirstIndex);
        QueueRead();
    }

    void ReadViewport()
    {
        _viewport ??= _scroll?.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault();
        if (_viewport is null || _scroll is null || _viewport.Bounds.Height <= 0) return;
        if (_targetIndex is { } target)
        {
            // Variable-height virtualization can refine the extent after realization.
            // Bounded corrections run only for an explicit index click, never ordinary scrolling.
            if (ContainerFromIndex(target) is { } container && container.TranslatePoint(default, _viewport) is { } p)
            {
                var next = Math.Clamp(_scroll.Offset.Y + p.Y, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height));
                if (Math.Abs(next - _scroll.Offset.Y) > .5 && _alignmentPasses++ < 4)
                {
                    _scroll.Offset = new Vector(_scroll.Offset.X, next);
                    QueueRead();
                    return;
                }
            }
            _targetIndex = null;
        }
        var first = -1;
        var last = -1;
        var top = double.PositiveInfinity;
        foreach (var container in GetRealizedContainers())
        {
            if (container.TranslatePoint(default, _viewport) is not { } p ||
                p.Y + container.Bounds.Height <= .5 || p.Y >= _viewport.Bounds.Height - .5) continue;
            var index = IndexFromContainer(container);
            if (index < 0) continue;
            if (p.Y < top) { top = p.Y; first = index; }
            last = Math.Max(last, index);
        }
        _firstVisible = first;
        _lastVisible = last;
        SetCurrent(first >= 0 && ItemsView[first] is ArtistInfo artist ? artist.IndexLetter : null);
    }

    void SetCurrent(string? letter)
    {
        if (_currentLetter == letter) return;
        SetAndRaise(CurrentLetterProperty, ref _currentLetter, letter);
        foreach (var group in Groups) group.IsCurrent = group.Letter == letter;
    }

    internal JsonObject Diagnostics() => new()
    {
        ["created"] = true, ["visible"] = _attached && IsEffectivelyVisible,
        ["currentLetter"] = CurrentLetter,
        ["groups"] = new JsonArray(Groups.Select(g => (JsonNode)new JsonObject
        { ["letter"] = g.Letter, ["firstIndex"] = g.FirstIndex, ["count"] = g.Count }).ToArray()),
        ["artistCount"] = ItemsView.Count, ["firstVisibleIndex"] = _firstVisible, ["lastVisibleIndex"] = _lastVisible,
        ["scrollOffsetY"] = _scroll?.Offset.Y ?? 0, ["viewportHeight"] = _scroll?.Viewport.Height ?? 0,
        ["realizedCount"] = GetRealizedContainers().Count(), ["positionPending"] = _targetIndex is not null
    };
}
