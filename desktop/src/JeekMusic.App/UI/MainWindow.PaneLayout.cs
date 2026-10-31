using System.ComponentModel;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.UI;

public partial class MainWindow
{
    const double NavMinimum = 160, SongMinimum = 260, SplitterWidth = 6;
    const double ArtistMinimum = 120, LyricsMinimum = 180;
    bool _layoutReady, _layoutUpdating, _layoutClosed, _navDragging;
    long _autoNavVersion;
    bool _autoNavPending;
    double _lastNavContentWidth;
    int _lastNavMeasuredNodes;
    string _autoNavResult = "none";
    Func<JsonObject>? _layoutDiagnostic;
    RowTextOverflowReveal? _navigationOverflow, _songOverflow;

    internal static double SafeNavWidth(double width) => double.IsFinite(width) && width >= NavMinimum ? width : 230;

    void InitMainLayout()
    {
        _layoutReady = true;
        _navigationOverflow = new RowTextOverflowReveal(this, NavTree, () => _navCollapsed);
        _songOverflow = new RowTextOverflowReveal(this, Table, () => !Table.IsEffectivelyVisible);
        MainArea.SizeChanged += (_, _) => RefreshLayoutBudget();
        RightArea.SizeChanged += (_, _) => RefreshLayoutBudget();
        _vm.PropertyChanged += OnLayoutViewModelChanged;
        PropertyChanged += (_, e) =>
        {
            // Recompute constraints once when inherited typography or render scale changes;
            // ordinary layout never requests content fitting or writes a snapshot.
            if (e.Property == FontSizeProperty || e.Property == FontFamilyProperty || e.Property.Name == "RenderScaling")
                RefreshLayoutBudget();
        };
        MainArea.ColumnDefinitions[0].PropertyChanged += (_, e) =>
        {
            if (!_layoutUpdating && !_navCollapsed && e.Property == ColumnDefinition.WidthProperty)
            {
                _navWidth = SafeNavWidth(MainArea.ColumnDefinitions[0].Width.Value);
                CancelAutomaticNavWidth();
            }
        };
        NavSplitter.AddHandler(PointerPressedEvent, (_, _) => CancelAutomaticNavWidth(), RoutingStrategies.Tunnel);
        NavSplitter.DragStarted += (_, _) => { _navDragging = true; CancelAutomaticNavWidth(); };
        NavSplitter.DragCompleted += (_, _) => { _navDragging = false; RefreshLayoutBudget(); };
        LyricsSplitter.DragCompleted += (_, _) =>
        {
            _lyricsWidth = RightArea.ColumnDefinitions[3].ActualWidth;
            RefreshLayoutBudget();
        };
        _layoutDiagnostic = MainLayoutDiagnostics;
        _app.MainLayoutDiagnostics = _layoutDiagnostic;
        Closed += (_, _) =>
        {
            _layoutClosed = true;
            CancelAutomaticNavWidth();
            _vm.PropertyChanged -= OnLayoutViewModelChanged;
            if (_app.MainLayoutDiagnostics == _layoutDiagnostic) _app.MainLayoutDiagnostics = null;
        };
        RefreshLayoutBudget();
    }

    void OnLayoutViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowArtistPane)) RefreshLayoutBudget();
    }

    void RefreshLayoutBudget()
    {
        if (!_layoutReady || _layoutUpdating || _layoutClosed) return;
        _layoutUpdating = true;
        try
        {
            var width = MainArea.Bounds.Width > 0 ? MainArea.Bounds.Width : Math.Max(MinWidth, Width);
            var full = _lyricsFullPage && LyricsHost.IsVisible;
            var artist = _vm.ShowArtistPane && !full;
            var lyrics = LyricsHost.IsVisible && !full;
            var separator = _navCollapsed ? 0 : SplitterWidth;
            var minimum = _navCollapsed ? 56 : NavMinimum;
            var rightMinimum = full ? 260 : SongMinimum + (artist ? ArtistMinimum : 0) + (lyrics ? LyricsMinimum + SplitterWidth : 0);
            var maximum = Math.Max(minimum, Math.Min(width * .45, width - separator - rightMinimum));
            var nav = MainArea.ColumnDefinitions[0];
            nav.MinWidth = minimum;
            nav.MaxWidth = _navCollapsed ? minimum : maximum;
            MainArea.ColumnDefinitions[2].MinWidth = Math.Max(0, Math.Min(rightMinimum, width - separator - minimum));
            if (!_navDragging) nav.Width = new GridLength(_navCollapsed ? 56 : Math.Clamp(SafeNavWidth(_navWidth), minimum, maximum));

            var rightWidth = Math.Max(0, width - separator - Math.Clamp(nav.Width.Value, minimum, maximum));
            var preferredLyrics = double.IsFinite(_lyricsWidth) ? Math.Clamp(_lyricsWidth, LyricsMinimum, 720) : 360;
            var artistNeed = artist ? 230 - ArtistMinimum : 0;
            var lyricsNeed = lyrics ? preferredLyrics - LyricsMinimum : 0;
            var extra = Math.Max(0, rightWidth - rightMinimum);
            var fraction = artistNeed + lyricsNeed > 0 ? Math.Min(1, extra / (artistNeed + lyricsNeed)) : 0;
            var artistWidth = artist ? ArtistMinimum + artistNeed * fraction : 0;
            var lyricsWidth = lyrics ? LyricsMinimum + lyricsNeed * fraction : 0;
            RightArea.ColumnDefinitions[0].Width = new GridLength(artistWidth);
            RightArea.ColumnDefinitions[1].MinWidth = full ? 0 : SongMinimum;
            RightArea.ColumnDefinitions[1].Width = full ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            var lyricColumn = RightArea.ColumnDefinitions[3];
            lyricColumn.MinWidth = lyrics ? LyricsMinimum : 0;
            lyricColumn.MaxWidth = lyrics ? Math.Max(LyricsMinimum, rightWidth - artistWidth - SongMinimum - SplitterWidth) : double.PositiveInfinity;
            lyricColumn.Width = full ? new GridLength(1, GridUnitType.Star) : new GridLength(lyricsWidth);
        }
        finally { _layoutUpdating = false; }
    }

    void CancelAutomaticNavWidth()
    {
        ++_autoNavVersion;
        if (_autoNavPending) _autoNavResult = "cancelled";
        _autoNavPending = false;
    }

    void OnFolderExpansionWidth(NavNode node, bool expanded)
    {
        CancelAutomaticNavWidth();
        if (!expanded || _navCollapsed || _layoutClosed || _navDragging) return;
        var key = node.Key;
        var version = _autoNavVersion;
        _autoNavPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (_layoutClosed || version != _autoNavVersion || _navCollapsed || _navDragging) return;
            _autoNavPending = false;
            // Read the latest tree, never a captured container from before an async rebuild.
            var visible = VisibleFolders().ToArray();
            if (!visible.Any(n => n.Node.Key == key && n.Node.IsExpanded)) { _autoNavResult = "stale-expansion"; return; }
            NavTree.UpdateLayout();
            var needed = MeasureVisibleFolderWidth(visible);
            _lastNavContentWidth = needed;
            var current = MainArea.ColumnDefinitions[0].ActualWidth;
            var target = Math.Min(needed, MainArea.ColumnDefinitions[0].MaxWidth);
            // Round upward to a device pixel, and ignore subpixel measurement noise.
            var scale = Math.Max(1, RenderScaling);
            target = Math.Ceiling(target * scale) / scale;
            target = Math.Min(target, MainArea.ColumnDefinitions[0].MaxWidth);
            if (target > current + 1 / scale)
            {
                _navWidth = target;
                RefreshLayoutBudget();
                _autoNavResult = needed > target + 1 ? "grown-to-limit" : "grown";
            }
            else _autoNavResult = needed > target + 1 ? "at-limit" : "already-fits";
        }, DispatcherPriority.Background);
    }

    IEnumerable<(NavNode Node, int Depth)> VisibleFolders()
    {
        foreach (var root in _vm.Nav.Where(n => n.Kind == NavKind.Folders))
            foreach (var item in Walk(root, 0)) yield return item;
        static IEnumerable<(NavNode, int)> Walk(NavNode node, int depth)
        {
            yield return (node, depth);
            if (node.IsExpanded)
                foreach (var child in node.Children)
                    foreach (var item in Walk(child, depth + 1)) yield return item;
        }
    }

    double MeasureVisibleFolderWidth((NavNode Node, int Depth)[] visible)
    {
        var rows = NavTree.GetVisualDescendants().OfType<TreeViewItem>()
            .Where(c => c.IsEffectivelyVisible && c.DataContext is NavNode).ToArray();
        var byNode = rows.GroupBy(c => (NavNode)c.DataContext!).ToDictionary(g => g.Key, g => g.First());
        var sample = rows.FirstOrDefault();
        if (sample is null) { _lastNavMeasuredNodes = 0; return 0; }
        var sampleName = Part(sample, "nav-name");
        var sampleCount = Part(sample, "nav-count");
        var sampleIcon = Part(sample, "nav-icon");
        if (sampleName is null || sampleCount is null || sampleIcon is null) return 0;
        var indent = NavTree.TryFindResource("TreeViewItemIndent", out var resource) && resource is double d ? d : 0;
        var chevron = sample.GetVisualDescendants().OfType<Panel>().FirstOrDefault(c => c.Name == "PART_ExpandCollapseChevronContainer" && ReferenceEquals(c.TemplatedParent, sample));
        var scrollbar = NavTree.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(c => c.Orientation == Avalonia.Layout.Orientation.Vertical);
        // Reserve the actual themed scrollbar target, even if it is not needed yet.
        var scrollWidth = scrollbar is null ? 0 : Math.Max(scrollbar.Bounds.Width, scrollbar.MinWidth);
        var chrome = NavTree.Margin.Left + NavTree.Margin.Right + NavTree.Padding.Left + NavTree.Padding.Right +
            sample.Padding.Left + sample.Padding.Right + (chevron?.DesiredSize.Width ?? 0) + scrollWidth;
        var widest = NavMinimum;
        _lastNavMeasuredNodes = visible.Length;
        foreach (var (node, depth) in visible)
        {
            byNode.TryGetValue(node, out var row);
            var name = row is null ? sampleName : Part(row, "nav-name") ?? sampleName;
            var count = row is null ? sampleCount : Part(row, "nav-count") ?? sampleCount;
            var icon = row is null ? sampleIcon : Part(row, "nav-icon") ?? sampleIcon;
            widest = Math.Max(widest, chrome + depth * indent + TextWidth(node.Icon, icon) + icon.Margin.Left + icon.Margin.Right +
                TextWidth(node.Title, name) + name.Margin.Left + name.Margin.Right +
                TextWidth(node.CountText, count) + count.Margin.Left + count.Margin.Right);
        }
        return widest;

        static TextBlock? Part(TreeViewItem row, string role) => row.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Classes.Contains(role) && ReferenceEquals(t.DataContext, row.DataContext));
        static double TextWidth(string text, TextBlock style)
        {
            using var layout = new TextLayout(text, new Typeface(style.FontFamily, style.FontStyle, style.FontWeight, style.FontStretch),
                style.FontSize, style.Foreground, flowDirection: style.FlowDirection, letterSpacing: style.LetterSpacing, fontFeatures: style.FontFeatures);
            return layout.WidthIncludingTrailingWhitespace;
        }
    }

    JsonObject MainLayoutDiagnostics() => new()
    {
        ["created"] = !_layoutClosed,
        ["windowWidth"] = MainArea.Bounds.Width, ["renderScaling"] = RenderScaling,
        ["navigationWidth"] = NavPanel.Bounds.Width, ["navigationVisible"] = NavPanel.IsEffectivelyVisible,
        ["navigationCollapsed"] = _navCollapsed, ["preferredNavigationWidth"] = _navWidth,
        ["navigationMinWidth"] = MainArea.ColumnDefinitions[0].MinWidth, ["navigationMaxWidth"] = MainArea.ColumnDefinitions[0].MaxWidth,
        ["contentWidth"] = RightArea.Bounds.Width, ["songPanelWidth"] = SongPanel.Bounds.Width,
        ["songPanelVisible"] = SongPanel.IsEffectivelyVisible, ["songMinimumWidth"] = SongMinimum - 16,
        ["artistPanelWidth"] = ArtistPane.Bounds.Width, ["artistPanelVisible"] = ArtistPane.IsEffectivelyVisible && ArtistPane.Bounds.Width > 0,
        ["lyricsPanelWidth"] = LyricsHost.Bounds.Width, ["lyricsPanelVisible"] = LyricsHost.IsEffectivelyVisible,
        ["splitterWidth"] = NavSplitter.Bounds.Width, ["splitterVisible"] = NavSplitter.IsEffectivelyVisible,
        ["splitterDragging"] = _navDragging, ["automaticWidthPending"] = _autoNavPending,
        ["lastMeasuredContentWidth"] = _lastNavContentWidth, ["lastMeasuredFolderCount"] = _lastNavMeasuredNodes,
        ["automaticWidthResult"] = _autoNavResult,
        ["navigationOverflow"] = _navigationOverflow?.Diagnostics(),
        ["songOverflow"] = _songOverflow?.Diagnostics(),
        ["songCopy"] = SongCopyDiagnostics()
    };
}
