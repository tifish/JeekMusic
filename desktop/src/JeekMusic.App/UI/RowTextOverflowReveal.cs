using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace JeekMusic.UI;

/// <summary>
/// A click-through, in-place copy of a clipped row text run. It lives in the
/// existing window overlay (no popup/window/focus/capture), outside the pane's clip.
/// Only the text is copied: the original icon, count and input targets stay in place.
/// </summary>
internal sealed class RowTextOverflowReveal : IDisposable
{
    readonly Window _window;
    readonly Control _host;
    readonly bool _songs;
    readonly Func<bool> _collapsed;
    readonly DispatcherTimer _readingTimer;
    readonly Stopwatch _readingClock = new();
    INotifyCollectionChanged? _items;
    ScrollViewer? _readingScroll;
    TextBlock? _source;
    Control? _row;
    Visual? _rowBackground;
    Color _rowColor;
    Rect _rowRect;
    object? _identity;
    OverlayLayer? _layer;
    Border? _surface;
    Rect _sourceRect, _visibleRect;
    bool _disposed;
    string _reason = "none";
    double _naturalWidth;
    bool _wrapped;
    string? _theme, _background, _foreground;
    double _contrast;
    bool _foregroundAdjusted;

    public RowTextOverflowReveal(Window window, Control host, Func<bool> collapsed)
    {
        _window = window; _host = host; _songs = host is DataGrid; _collapsed = collapsed;
        _readingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _readingTimer.Tick += OnReadTick;
        window.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, true);
        window.AddHandler(InputElement.PointerPressedEvent, OnPress, RoutingStrategies.Tunnel, true);
        window.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel, true);
        window.AddHandler(InputElement.KeyDownEvent, OnKey, RoutingStrategies.Tunnel, true);
        window.PointerExited += OnWindowExited;
        window.Deactivated += OnDeactivated;
        window.SizeChanged += OnSizeChanged;
        window.PositionChanged += OnPositionChanged;
        window.PropertyChanged += OnWindowChanged;
        window.Closed += OnClosed;
        host.LayoutUpdated += OnLayout;
        host.EffectiveViewportChanged += OnViewport;
        host.AddHandler(ScrollViewer.ScrollChangedEvent, OnScroll, RoutingStrategies.Bubble, true);
        host.PropertyChanged += OnTreeChanged;
        ObserveItems();
    }

    public JsonObject Diagnostics() => new()
    {
        ["visible"] = _surface is not null, ["pending"] = false,
        ["reason"] = _reason, ["naturalWidth"] = _naturalWidth,
        ["visibleTextWidth"] = _visibleRect.Width, ["wrapped"] = _wrapped,
        ["overlayWidth"] = _surface?.Bounds.Width ?? 0, ["overlayHeight"] = _surface?.Bounds.Height ?? 0,
        ["automaticReading"] = _readingScroll is not null, ["readingOffset"] = _readingScroll?.Offset.X ?? 0,
        ["hitTestVisible"] = false, ["delayMs"] = 0,
        ["rowHeight"] = _rowRect.Height, ["borderThickness"] = 0, ["readingAxis"] = "horizontal",
        ["theme"] = _theme, ["background"] = _background, ["foreground"] = _foreground,
        ["contrastRatio"] = _contrast, ["foregroundAdjusted"] = _foregroundAdjusted
    };

    void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetCurrentPoint(_window);
        if (_disposed || _collapsed() || e.Pointer.Type != PointerType.Mouse || p.Properties.IsLeftButtonPressed || p.Properties.IsRightButtonPressed || p.Properties.IsMiddleButtonPressed)
        { Close("input"); return; }
        var visual = e.Source as Visual;
        Control? row;
        TextBlock? field;
        if (_songs)
        {
            row = visual?.FindAncestorOfType<DataGridRow>(includeSelf: true);
            if (row?.FindAncestorOfType<DataGrid>() != _host) { Close("pointer-left"); return; }
            field = row.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("song-title") &&
                new Rect(t.Bounds.Size).Contains(e.GetPosition(t)));
        }
        else
        {
            row = visual?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
            if (row?.FindAncestorOfType<TreeView>() != _host) { Close("pointer-left"); return; }
            var fields = row.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("navtitle") && ReferenceEquals(t.DataContext, row.DataContext)).ToArray();
            // Hovering the icon/row reveals its name; a clipped count reveals that count.
            field = fields.FirstOrDefault(t => t.Classes.Contains("nav-count") && t.IsEffectivelyVisible &&
                new Rect(t.Bounds.Size).Contains(e.GetPosition(t)))
                ?? fields.FirstOrDefault(t => t.Classes.Contains("nav-name"));
        }
        if (ReferenceEquals(field, _source)) return;
        Close("candidate-changed");
        if (field is null || !TryClipped(field, out var source, out var visible, out var width)) { _reason = "fits"; return; }
        _source = field; _row = row; _identity = row.DataContext;
        _rowBackground = RowBackground(field, row);
        if (_rowBackground.TransformToVisual(_window) is not { } rowTransform) { Close("no-row"); return; }
        _rowRect = new Rect(_rowBackground.Bounds.Size).TransformToAABB(rowTransform);
        _sourceRect = source; _visibleRect = visible; _naturalWidth = width;
        field.PropertyChanged += OnSourceChanged;
        field.DetachedFromVisualTree += OnSourceDetached;
        _rowBackground.PropertyChanged += OnRowBackgroundChanged;
        Show();
    }

    // Intersect the actual run with every ancestor viewport/clip, not just the text's
    // allocated width. Deep indentation and oversized headers can clip without ellipsis.
    bool TryClipped(TextBlock text, out Rect source, out Rect visible, out double naturalWidth)
    {
        source = visible = default; naturalWidth = 0;
        if (!text.IsEffectivelyVisible || string.IsNullOrEmpty(text.Text) || text.Bounds.Height <= 0) return false;
        if (text.TransformToVisual(_window) is not { } matrix) return false;
        source = new Rect(text.Bounds.Size).TransformToAABB(matrix);
        var clipRect = new Rect(_window.ClientSize);
        foreach (var ancestor in text.GetVisualAncestors())
        {
            if (!ancestor.IsVisible || ancestor.Opacity <= 0) return false;
            if (ancestor.TransformToVisual(_window) is not { } transform) return false;
            if (ancestor.ClipToBounds || ancestor == _host || ancestor == _window)
                clipRect = clipRect.Intersect(new Rect(ancestor.Bounds.Size).TransformToAABB(transform));
            if (ancestor.Clip is { } clip) clipRect = clipRect.Intersect(clip.Bounds.TransformToAABB(transform));
            if (ancestor == _window) break;
        }
        // A zero-width name can still be revealed by hovering its row/icon. Keep the
        // vertical intersection even if deep indentation hides the entire text run.
        visible = new Rect(Math.Max(source.Left, clipRect.Left), Math.Max(source.Top, clipRect.Top),
            Math.Max(0, Math.Min(source.Right, clipRect.Right) - Math.Max(source.Left, clipRect.Left)),
            Math.Max(0, Math.Min(source.Bottom, clipRect.Bottom) - Math.Max(source.Top, clipRect.Top)));
        if (visible.Height <= .5) return false;
        using var layout = Measure(text);
        naturalWidth = layout.WidthIncludingTrailingWhitespace + text.Padding.Left + text.Padding.Right;
        var tolerance = .5 / Math.Max(1, _window.RenderScaling);
        return naturalWidth > visible.Width + tolerance || visible.Left > source.Left + tolerance ||
            visible.Top > source.Top + tolerance || visible.Bottom < source.Bottom - tolerance;
    }

    static TextLayout Measure(TextBlock text) => new(text.Text,
        new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground,
        flowDirection: text.FlowDirection, lineHeight: text.LineHeight, letterSpacing: text.LetterSpacing, fontFeatures: text.FontFeatures);

    void Show()
    {
        if (!StillValid() || _source is not { } source || _row is null) { Close("stale"); return; }
        _layer = OverlayLayer.GetOverlayLayer(_host);
        if (_layer is null || _window.TransformToVisual(_layer) is not { } toLayer) { Close("no-overlay"); return; }

        var text = new TextBlock
        {
            Text = source.Text, FontFamily = source.FontFamily, FontSize = source.FontSize, FontWeight = source.FontWeight,
            FontStyle = source.FontStyle, FontStretch = source.FontStretch, FontFeatures = source.FontFeatures,
            Foreground = source.Foreground, FlowDirection = source.FlowDirection, LetterSpacing = source.LetterSpacing,
            LineHeight = source.LineHeight, LineSpacing = source.LineSpacing, TextDecorations = source.TextDecorations,
            Padding = source.Padding, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap,
            Focusable = false, IsHitTestVisible = false, Name = _songs ? "SongOverflowText" : "NavigationOverflowText"
        };
        // Preserve effective text opacity (e.g. secondary counts), not only its brush.
        for (Visual? v = source; v is not null && v != _window; v = v.GetVisualParent()) text.Opacity *= v.Opacity;

        // Resource lookup without a ThemeVariant passes null, NOT ActualThemeVariant.
        // That mixed the default light backdrop with dark-row white text. Resolve the
        // same actual theme as the source, then flatten its visual backgrounds onto
        // an opaque base: the overlay cannot inherit native Mica's backdrop pixels.
        var palette = ResolvePalette(source, text.Opacity);
        text.Foreground = new SolidColorBrush(palette.Foreground);
        _theme = _host.ActualThemeVariant.ToString();
        _background = palette.Background.ToString(); _foreground = palette.Foreground.ToString();
        _contrast = palette.Contrast; _foregroundAdjusted = palette.Adjusted;
        _rowColor = palette.Background;
        // The surface continues the painted row, without tooltip chrome or padding.
        // Position the text explicitly so its baseline is independent of row height.
        var content = new Canvas { IsHitTestVisible = false };
        Canvas.SetTop(text, _sourceRect.Top - _rowRect.Top);
        _surface = new Border
        {
            Name = _songs ? "SongOverflowReveal" : "NavigationOverflowReveal",
            Background = new SolidColorBrush(palette.Background), Child = content,
            IsHitTestVisible = false, Focusable = false, ClipToBounds = true,
            BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Height = _rowRect.Height
        };
        var available = AvailableClientRect();
        if (available.Width < 40 || available.Height < _rowRect.Height ||
            _rowRect.Top < available.Top || _rowRect.Bottom > available.Bottom) { Close("no-space"); return; }
        // Snap inward at the monitor/client edge before Avalonia rounds layout;
        // rounding a fractional natural width otherwise leaks one physical pixel.
        var scale = _window.RenderScaling;
        var left = Math.Ceiling(available.Left * scale) / scale;
        var right = Math.Floor(available.Right * scale) / scale;
        var naturalWidth = Math.Ceiling(_naturalWidth * scale) / scale;
        var x = Math.Clamp(Math.Floor(_sourceRect.X * scale) / scale, left, Math.Max(left, right - 40));
        var y = _rowRect.Top;
        // Prefer moving left over shortening a run near the right screen edge.
        if (naturalWidth > right - x) x = Math.Max(left, right - naturalWidth);
        var width = Math.Min(naturalWidth, right - x);
        _wrapped = false;
        _surface.Width = width;
        if (_row is DataGridRow songRow)
        {
            // Leave holes over existing playback and cell-focus outlines only.
            // The continuation itself never paints a border.
            var edges = new GeometryGroup();
            foreach (var outline in songRow.GetVisualDescendants().OfType<Rectangle>().Where(r =>
                r.Stroke is not null && r.StrokeThickness > 0 && r.IsEffectivelyVisible && r.Bounds.Width > 0 &&
                (ReferenceEquals(r, _rowBackground) || r.GetVisualAncestors().OfType<Control>().Any(c => c.Name == "FocusVisual"))))
            {
                if (outline.TransformToVisual(_window) is not { } transform) continue;
                var rect = new Rect(outline.Bounds.Size).TransformToAABB(transform).Translate(new Vector(-x, -y));
                edges.Children.Add(new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(rect), new RectangleGeometry(rect.Deflate(outline.StrokeThickness))));
            }
            if (edges.Children.Count > 0)
                _surface.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(new Rect(new Size(width, _rowRect.Height))), edges);
        }
        text.Width = _naturalWidth;
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        text.Height = text.DesiredSize.Height;
        if (_naturalWidth > width + .5 / Math.Max(1, _window.RenderScaling))
        {
            // Keep the row height even for arbitrarily long text. Read horizontally
            // at the original font size rather than growing a second card over rows.
            _readingScroll = new ScrollViewer
            {
                Content = text, Width = width, Height = text.Height,
                IsHitTestVisible = false, Focusable = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Canvas.SetTop(_readingScroll, _sourceRect.Top - _rowRect.Top);
            content.Children.Add(_readingScroll);
            _readingClock.Restart(); _readingTimer.Start();
        }
        else content.Children.Add(text);
        var location = new Point(x, y).Transform(toLayer);
        Canvas.SetLeft(_surface, location.X); Canvas.SetTop(_surface, location.Y);
        _layer.Children.Add(_surface);
        _reason = "revealed";
    }

    static IBrush? BackgroundBrush(Visual visual) => visual switch
    {
        Border b => b.Background, Panel p => p.Background,
        ContentPresenter p => p.Background, TemplatedControl t => t.Background,
        Rectangle r => r.Fill, _ => null
    };

    static Visual RowBackground(TextBlock source, Control row)
    {
        if (row is DataGridRow)
            return row.GetVisualDescendants().OfType<Rectangle>().FirstOrDefault(r =>
                r.Name == "BackgroundRectangle" && ReferenceEquals(r.TemplatedParent, row)) ?? (Visual)row;
        // TreeViewItem.Bounds includes expanded children. Its own themed header
        // presenter is the painted selection/hover area, independent of descendants.
        return source.GetVisualAncestors().OfType<Border>().FirstOrDefault(v =>
            ReferenceEquals(v.TemplatedParent, row) && v.Name == "PART_LayoutRoot") ?? (Visual)row;
    }

    (Color Background, Color Foreground, double Contrast, bool Adjusted) ResolvePalette(TextBlock source, double opacity)
    {
        var theme = _host.ActualThemeVariant;
        var fallback = theme == ThemeVariant.Dark ? Color.Parse("#202020") : Colors.White;
        // Mica is transparent in the visual host. Its opaque theme counterpart avoids
        // both text showing through from below and an unrelated default-theme colour.
        var backdropKey = _window.ActualTransparencyLevel == WindowTransparencyLevel.Mica
            ? "SystemControlBackgroundChromeMediumLowBrush" : "SystemControlBackgroundAltHighBrush";
        var background = ResourceColor(backdropKey, fallback);
        foreach (var visual in source.GetVisualAncestors().Reverse())
        {
            if (BackgroundBrush(visual) is ISolidColorBrush solid)
                background = Composite(WithOpacity(BrushColor(solid), EffectiveOpacity(visual)), background);
            // DataGrid paints row state in a sibling Rectangle, outside the text's
            // ancestor chain. Its actual opacity differs for hover/selection/focus.
            if (ReferenceEquals(visual, _row) && _row is DataGridRow && _rowBackground is { } painted &&
                BackgroundBrush(painted) is ISolidColorBrush fill)
                background = Composite(WithOpacity(BrushColor(fill), EffectiveOpacity(painted)), background);
        }
        var foreground = source.Foreground is ISolidColorBrush actual ? BrushColor(actual)
            : ResourceColor("SystemControlForegroundBaseHighBrush", theme == ThemeVariant.Dark ? Colors.White : Colors.Black);
        var ratio = Contrast(Composite(WithOpacity(foreground, opacity), background), background);
        var adjusted = ratio < 3;
        if (adjusted)
        {
            // Preserve the original foreground unless the combined palette is unreadable.
            // This also protects custom/translucent themes and unusually pale accents.
            var blackRatio = Contrast(Composite(WithOpacity(Colors.Black, opacity), background), background);
            var whiteRatio = Contrast(Composite(WithOpacity(Colors.White, opacity), background), background);
            foreground = blackRatio >= whiteRatio ? Colors.Black : Colors.White;
            ratio = Math.Max(blackRatio, whiteRatio);
        }
        return (background, foreground, ratio, adjusted);

        Color ResourceColor(string key, Color alternative) => _host.TryFindResource(key, theme, out var resource) && resource is ISolidColorBrush solid
            ? Composite(BrushColor(solid), fallback) : alternative;
    }

    double EffectiveOpacity(Visual visual)
    {
        var opacity = 1d;
        for (Visual? v = visual; v is not null && v != _window; v = v.GetVisualParent()) opacity *= v.Opacity;
        return opacity;
    }

    static Color BrushColor(ISolidColorBrush brush) => WithOpacity(brush.Color, brush.Opacity);
    static Color WithOpacity(Color color, double opacity) => Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B);
    static Color Composite(Color foreground, Color opaqueBackground)
    {
        var alpha = foreground.A / 255d;
        return Color.FromRgb((byte)Math.Round(foreground.R * alpha + opaqueBackground.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + opaqueBackground.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + opaqueBackground.B * (1 - alpha)));
    }
    static double Contrast(Color a, Color b)
    {
        var first = Luminance(a); var second = Luminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
        static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        static double Linear(byte value) => value / 255d <= .04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + .055) / 1.055, 2.4);
    }

    Rect AvailableClientRect()
    {
        var client = new Rect(_window.ClientSize);
        if (_window.Screens.ScreenFromWindow(_window) is not { } screen) return client;
        var origin = _window.PointToScreen(default);
        var work = screen.WorkingArea;
        var scale = _window.RenderScaling;
        return client.Intersect(new Rect((work.X - origin.X) / scale, (work.Y - origin.Y) / scale, work.Width / scale, work.Height / scale));
    }

    bool StillValid() => !_disposed && !_collapsed() && _source is { } source && _row is { } row &&
        ReferenceEquals(row.DataContext, _identity) && row.IsEffectivelyVisible && row.IsPointerOver &&
        TryClipped(source, out var rect, out var visible, out _) && rect == _sourceRect && visible == _visibleRect;

    void OnLayout(object? sender, EventArgs e)
    {
        if (_source is null) return;
        if (!StillValid() || _rowBackground?.TransformToVisual(_window) is not { } transform ||
            new Rect(_rowBackground.Bounds.Size).TransformToAABB(transform) != _rowRect)
        { Close("layout-changed"); return; }
        // Selection, playback identity, focus and hover can change without moving
        // the pointer. Never leave a previous row state's palette on the surface.
        if (ResolvePalette(_source, EffectiveOpacity(_source)).Background != _rowColor) Close("row-state-changed");
    }
    void OnSourceChanged(object? sender, AvaloniaPropertyChangedEventArgs e) => Close("text-or-style-changed");
    void OnRowBackgroundChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name is "Background" or "Fill" or "Opacity" or "Stroke" or "StrokeThickness") Close("row-state-changed");
    }
    void OnSourceDetached(object? sender, VisualTreeAttachmentEventArgs e) => Close("container-detached");
    void OnReadTick(object? sender, EventArgs e)
    {
        if (_readingScroll is not { } scroll) return;
        var maximum = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
        var cycleSeconds = maximum / 40 + 3;
        var seconds = _readingClock.Elapsed.TotalSeconds % cycleSeconds;
        scroll.Offset = new Vector(Math.Clamp((seconds - 1) * 40, 0, maximum), 0);
    }
    void ObserveItems()
    {
        if (_items is not null) _items.CollectionChanged -= OnItemsChanged;
        _items = (_host is DataGrid grid ? grid.ItemsSource : (_host as ItemsControl)?.ItemsSource) as INotifyCollectionChanged;
        if (_items is not null) _items.CollectionChanged += OnItemsChanged;
    }
    void OnTreeChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == "ItemsSource") ObserveItems();
        if (e.Property.Name == "ItemsSource" || e.Property == TreeView.SelectedItemProperty || e.Property == DataGrid.SelectedItemProperty || e.Property == Visual.IsVisibleProperty) Close("tree-changed");
    }
    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Close("items-changed");
    void OnViewport(object? sender, EffectiveViewportChangedEventArgs e) => Close("viewport-changed");
    void OnScroll(object? sender, ScrollChangedEventArgs e) => Close("scroll");
    void OnPress(object? sender, PointerPressedEventArgs e) => Close("pointer-pressed");
    void OnWheel(object? sender, PointerWheelEventArgs e) => Close("wheel");
    void OnKey(object? sender, KeyEventArgs e) => Close("key");
    void OnWindowExited(object? sender, PointerEventArgs e) => Close("pointer-left");
    void OnDeactivated(object? sender, EventArgs e) => Close("window-inactive");
    void OnSizeChanged(object? sender, SizeChangedEventArgs e) => Close("window-resized");
    void OnPositionChanged(object? sender, PixelPointEventArgs e) => Close("window-moved");
    void OnWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    { if (e.Property == Window.IsVisibleProperty || e.Property.Name is "RenderScaling" or "ActualThemeVariant") Close("window-changed"); }
    void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Close(string reason)
    {
        _readingTimer.Stop(); _readingClock.Reset(); _readingScroll = null;
        if (_source is not null)
        {
            _source.PropertyChanged -= OnSourceChanged;
            _source.DetachedFromVisualTree -= OnSourceDetached;
        }
        if (_surface is not null) _layer?.Children.Remove(_surface);
        if (_rowBackground is not null) _rowBackground.PropertyChanged -= OnRowBackgroundChanged;
        _surface = null; _layer = null; _source = null; _row = null; _rowBackground = null; _identity = null;
        _reason = reason;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Close("closed");
        _readingTimer.Tick -= OnReadTick;
        _window.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
        _window.RemoveHandler(InputElement.PointerPressedEvent, OnPress);
        _window.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
        _window.RemoveHandler(InputElement.KeyDownEvent, OnKey);
        _window.PointerExited -= OnWindowExited;
        _window.Deactivated -= OnDeactivated;
        _window.SizeChanged -= OnSizeChanged;
        _window.PositionChanged -= OnPositionChanged;
        _window.PropertyChanged -= OnWindowChanged;
        _window.Closed -= OnClosed;
        _host.LayoutUpdated -= OnLayout;
        _host.EffectiveViewportChanged -= OnViewport;
        _host.RemoveHandler(ScrollViewer.ScrollChangedEvent, OnScroll);
        _host.PropertyChanged -= OnTreeChanged;
        if (_items is not null) _items.CollectionChanged -= OnItemsChanged;
        _items = null;
    }
}
