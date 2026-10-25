using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// A click-through, in-place copy of a clipped navigation text run. It lives in the
/// existing window overlay (no popup/window/focus/capture), outside the pane's clip.
/// Only the text is copied: the original icon, count and input targets stay in place.
/// </summary>
internal sealed class NavigationOverflowReveal : IDisposable
{
    readonly Window _window;
    readonly TreeView _tree;
    readonly Func<bool> _collapsed;
    readonly DispatcherTimer _readingTimer;
    readonly Stopwatch _readingClock = new();
    INotifyCollectionChanged? _items;
    ScrollViewer? _readingScroll;
    TextBlock? _source;
    TreeViewItem? _row;
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

    public NavigationOverflowReveal(Window window, TreeView tree, Func<bool> collapsed)
    {
        _window = window; _tree = tree; _collapsed = collapsed;
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
        tree.LayoutUpdated += OnLayout;
        tree.EffectiveViewportChanged += OnViewport;
        tree.AddHandler(ScrollViewer.ScrollChangedEvent, OnScroll, RoutingStrategies.Bubble, true);
        tree.PropertyChanged += OnTreeChanged;
        ObserveItems();
    }

    public JsonObject Diagnostics() => new()
    {
        ["visible"] = _surface is not null, ["pending"] = false,
        ["reason"] = _reason, ["naturalWidth"] = _naturalWidth,
        ["visibleTextWidth"] = _visibleRect.Width, ["wrapped"] = _wrapped,
        ["overlayWidth"] = _surface?.Bounds.Width ?? 0, ["overlayHeight"] = _surface?.Bounds.Height ?? 0,
        ["automaticReading"] = _readingScroll is not null, ["readingOffset"] = _readingScroll?.Offset.Y ?? 0,
        ["hitTestVisible"] = false, ["delayMs"] = 0,
        ["theme"] = _theme, ["background"] = _background, ["foreground"] = _foreground,
        ["contrastRatio"] = _contrast, ["foregroundAdjusted"] = _foregroundAdjusted
    };

    void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetCurrentPoint(_window);
        if (_disposed || _collapsed() || e.Pointer.Type != PointerType.Mouse || p.Properties.IsLeftButtonPressed || p.Properties.IsRightButtonPressed || p.Properties.IsMiddleButtonPressed)
        { Close("input"); return; }
        var visual = e.Source as Visual;
        var row = visual?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (row is null || row.FindAncestorOfType<TreeView>() != _tree) { Close("pointer-left"); return; }
        var fields = row.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("navtitle") && ReferenceEquals(t.DataContext, row.DataContext)).ToArray();
        // Hovering the icon/row reveals its name; hovering a clipped count reveals that count.
        var field = fields.FirstOrDefault(t => t.Classes.Contains("nav-count") && t.IsEffectivelyVisible &&
            new Rect(t.Bounds.Size).Contains(e.GetPosition(t)))
            ?? fields.FirstOrDefault(t => t.Classes.Contains("nav-name"));
        if (ReferenceEquals(field, _source)) return;
        Close("candidate-changed");
        if (field is null || !TryClipped(field, out var source, out var visible, out var width)) { _reason = "fits"; return; }
        _source = field; _row = row; _identity = row.DataContext;
        _sourceRect = source; _visibleRect = visible; _naturalWidth = width;
        field.PropertyChanged += OnSourceChanged;
        field.DetachedFromVisualTree += OnSourceDetached;
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
            if (ancestor.ClipToBounds || ancestor == _tree || ancestor == _window)
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
        _layer = OverlayLayer.GetOverlayLayer(_tree);
        if (_layer is null || _window.TransformToVisual(_layer) is not { } toLayer) { Close("no-overlay"); return; }

        var text = new TextBlock
        {
            Text = source.Text, FontFamily = source.FontFamily, FontSize = source.FontSize, FontWeight = source.FontWeight,
            FontStyle = source.FontStyle, FontStretch = source.FontStretch, FontFeatures = source.FontFeatures,
            Foreground = source.Foreground, FlowDirection = source.FlowDirection, LetterSpacing = source.LetterSpacing,
            LineHeight = source.LineHeight, LineSpacing = source.LineSpacing, TextDecorations = source.TextDecorations,
            Padding = source.Padding, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap,
            Focusable = false, IsHitTestVisible = false, Name = "NavigationOverflowText"
        };
        // Preserve effective text opacity (e.g. secondary counts), not only its brush.
        for (Visual? v = source; v is not null && v != _window; v = v.GetVisualParent()) text.Opacity *= v.Opacity;

        // Resource lookup without a ThemeVariant passes null, NOT ActualThemeVariant.
        // That mixed the default light backdrop with dark-row white text. Resolve the
        // same actual theme as the source, then flatten its visual backgrounds onto
        // an opaque base: the overlay cannot inherit native Mica's backdrop pixels.
        var palette = ResolvePalette(source, text.Opacity);
        text.Foreground = new SolidColorBrush(palette.Foreground);
        _theme = _tree.ActualThemeVariant.ToString();
        _background = palette.Background.ToString(); _foreground = palette.Foreground.ToString();
        _contrast = palette.Contrast; _foregroundAdjusted = palette.Adjusted;
        var fill = new Border { Child = text, Padding = new Thickness(1), IsHitTestVisible = false };
        _surface = new Border
        {
            Name = "NavigationOverflowReveal", Background = new SolidColorBrush(palette.Background), Child = fill, IsHitTestVisible = false, Focusable = false,
            BorderBrush = _tree.TryFindResource("SystemControlForegroundBaseLowBrush", _tree.ActualThemeVariant, out var line) ? line as IBrush : null,
            BorderThickness = new Thickness(1), ClipToBounds = true
        };

        // Keep the normal one-line text origin/baseline. For an exceptional long run,
        // wrap within the intersection of the client and monitor work area, never scale.
        var available = AvailableClientRect();
        if (available.Width < 40 || available.Height < 20) { Close("no-space"); return; }
        var x = Math.Clamp(_sourceRect.X - 2, available.Left, Math.Max(available.Left, available.Right - 40));
        var y = _sourceRect.Y - 2;
        var maxWidth = Math.Max(1, available.Right - x);
        _wrapped = _naturalWidth + 4 > maxWidth;
        if (_wrapped) text.TextWrapping = TextWrapping.Wrap;
        _surface.MaxWidth = maxWidth;
        _surface.Measure(new Size(maxWidth, double.PositiveInfinity));
        // If a very deep/long name needs the whole client width, use it before moving
        // upward. The typography stays unchanged and the complete run remains readable.
        if (_surface.DesiredSize.Height > available.Height && _wrapped)
        {
            x = available.Left; maxWidth = available.Width;
            _surface.MaxWidth = maxWidth;
            _surface.Measure(new Size(maxWidth, double.PositiveInfinity));
        }
        y = Math.Clamp(y, available.Top, Math.Max(available.Top, available.Bottom - _surface.DesiredSize.Height));
        _surface.MaxHeight = available.Height;
        if (_surface.DesiredSize.Height > available.Height)
        {
            // Arbitrarily long playlist names can exceed even the full monitor height.
            // Slowly read through the complete run without intercepting the original
            // wheel/click targets or reducing the font. Normal names never animate.
            fill.Child = null;
            _readingScroll = new ScrollViewer
            {
                Content = text, IsHitTestVisible = false, Focusable = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
            };
            fill.Child = _readingScroll;
            _readingClock.Restart(); _readingTimer.Start();
        }
        var location = new Point(x, y).Transform(toLayer);
        Canvas.SetLeft(_surface, location.X); Canvas.SetTop(_surface, location.Y);
        _layer.Children.Add(_surface);
        _reason = "revealed";
    }

    (Color Background, Color Foreground, double Contrast, bool Adjusted) ResolvePalette(TextBlock source, double opacity)
    {
        var theme = _tree.ActualThemeVariant;
        var fallback = theme == ThemeVariant.Dark ? Color.Parse("#202020") : Colors.White;
        // Mica is transparent in the visual tree. Its opaque theme counterpart avoids
        // both text showing through from below and an unrelated default-theme colour.
        var backdropKey = _window.ActualTransparencyLevel == WindowTransparencyLevel.Mica
            ? "SystemControlBackgroundChromeMediumLowBrush" : "SystemControlBackgroundAltHighBrush";
        var background = ResourceColor(backdropKey, fallback);
        foreach (var visual in source.GetVisualAncestors().Reverse())
        {
            IBrush? brush = visual switch
            {
                Border b => b.Background, Panel p => p.Background,
                TemplatedControl t => t.Background, _ => null
            };
            if (brush is ISolidColorBrush solid) background = Composite(BrushColor(solid), background);
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

        Color ResourceColor(string key, Color alternative) => _tree.TryFindResource(key, theme, out var resource) && resource is ISolidColorBrush solid
            ? Composite(BrushColor(solid), fallback) : alternative;
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
        var client = new Rect(_window.ClientSize).Deflate(4);
        if (_window.Screens.ScreenFromWindow(_window) is not { } screen) return client;
        var origin = _window.PointToScreen(default);
        var work = screen.WorkingArea;
        var scale = _window.RenderScaling;
        return client.Intersect(new Rect((work.X - origin.X) / scale, (work.Y - origin.Y) / scale, work.Width / scale, work.Height / scale));
    }

    bool StillValid() => !_disposed && !_collapsed() && _source is { } source && _row is { } row &&
        ReferenceEquals(row.DataContext, _identity) && row.IsEffectivelyVisible && row.IsPointerOver &&
        TryClipped(source, out var rect, out var visible, out _) && rect == _sourceRect && visible == _visibleRect;

    void OnLayout(object? sender, EventArgs e) { if (_source is not null && !StillValid()) Close("layout-changed"); }
    void OnSourceChanged(object? sender, AvaloniaPropertyChangedEventArgs e) => Close("text-or-style-changed");
    void OnSourceDetached(object? sender, VisualTreeAttachmentEventArgs e) => Close("container-detached");
    void OnReadTick(object? sender, EventArgs e)
    {
        if (_readingScroll is not { } scroll) return;
        var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var cycleSeconds = maximum / 20 + 3;
        var seconds = _readingClock.Elapsed.TotalSeconds % cycleSeconds;
        scroll.Offset = new Vector(0, Math.Clamp((seconds - 1) * 20, 0, maximum));
    }
    void ObserveItems()
    {
        if (_items is not null) _items.CollectionChanged -= OnItemsChanged;
        _items = _tree.ItemsSource as INotifyCollectionChanged;
        if (_items is not null) _items.CollectionChanged += OnItemsChanged;
    }
    void OnTreeChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ItemsControl.ItemsSourceProperty) ObserveItems();
        if (e.Property == ItemsControl.ItemsSourceProperty || e.Property == TreeView.SelectedItemProperty || e.Property == Visual.IsVisibleProperty) Close("tree-changed");
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
        _surface = null; _layer = null; _source = null; _row = null; _identity = null;
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
        _tree.LayoutUpdated -= OnLayout;
        _tree.EffectiveViewportChanged -= OnViewport;
        _tree.RemoveHandler(ScrollViewer.ScrollChangedEvent, OnScroll);
        _tree.PropertyChanged -= OnTreeChanged;
        if (_items is not null) _items.CollectionChanged -= OnItemsChanged;
        _items = null;
    }
}
