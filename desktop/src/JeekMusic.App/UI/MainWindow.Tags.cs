using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using ToggleButton = Avalonia.Controls.Primitives.ToggleButton;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using JeekMusic.UI.ViewModels;
using JeekMusic.Core.Analysis;
using JeekMusic.Library;

namespace JeekMusic.UI;

public partial class MainWindow
{
    internal static readonly (string Column, TagDimension Dimension, string Label)[] TagColumns =
    [
        ("Mood", TagDimension.MOOD, "运动"), ("Voice", TagDimension.VOICE, "人声"),
        ("Genre", TagDimension.GENRE, "古典"), ("Calmness", TagDimension.CALMNESS, "安静"),
    ];
    bool _quickTagSaving;
    string _quickTagState = "idle";
    string? _quickTagDimension;
    int _quickTagCount;

    partial void AddTagActions(ContextMenu menu, IReadOnlyList<Track> tracks)
    {
        menu.Items.Add(QuickTagCorrectionItem(tracks));
        menu.Items.Add(TagCorrectionItem(tracks));
    }

    internal MenuItem QuickTagCorrectionItem(IReadOnlyList<Track> tracks)
    {
        // Keep the original targets even if a correction removes them from the filtered list.
        var targets = tracks.DistinctBy(t => t.Id).ToArray();
        var current = _app.SmartTags.Read();
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4) };
        panel.Children.Add(new TextBlock { Text = L.T("快速修正标签") + " · " + L.F($"已选择 {targets.Length} 首歌曲"),
            FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var root = new MenuItem { Header = panel, Name = "QuickTagPanel", StaysOpenOnClick = true,
            IsEnabled = targets.Length > 0 && !_quickTagSaving };
        AutomationProperties.SetName(root, L.T("快速修正标签"));
        var buttons = new List<ToggleButton>();
        var error = new TextBlock { Name = "QuickTagError", IsVisible = false, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        foreach (var (_, dimension, label) in TagColumns)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(L.IsEnglish ? "156,76,104,80" : "188,72,72,84"), ColumnSpacing = 4, MinHeight = 44 };
            row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = {
                new TextBlock { Text = L.T(label), FontWeight = FontWeight.SemiBold },
                new TextBlock { Name = "QuickCurrent" + dimension, Text = SmartTagDialog.Summary(targets, current, dimension),
                    FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap,
                    [ToolTip.TipProperty] = SmartTagDialog.Summary(targets, current, dimension) } } });
            var column = 1;
            foreach (var (value, text, marked) in TagChoices(targets, current, dimension))
            {
                var button = new ToggleButton { Name = "Quick" + dimension + (value?.ToString() ?? "Auto"), Content = value is null && L.IsEnglish ? L.T("自动") : text,
                    IsChecked = marked, Padding = new Thickness(6, 4), MinHeight = 30,
                    HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetAutomationId(button, button.Name);
                AutomationProperties.SetName(button, L.T(label) + ": " + text);
                AutomationProperties.SetHelpText(button, SmartTagDialog.Summary(targets, current, dimension));
                ToolTip.SetTip(button, text);
                Grid.SetColumn(button, column++); row.Children.Add(button); buttons.Add(button);
                button.Click += async (_, e) =>
                {
                    e.Handled = true;
                    if (_quickTagSaving) return;
                    panel.IsEnabled = false; error.IsVisible = false;
                    await ApplyQuickTag(targets, dimension, value);
                    panel.IsEnabled = true;
                    // Close only after success; a failed transaction stays available for retry or Escape.
                    if (_quickTagState == "saved") root.FindAncestorOfType<ContextMenu>()?.Close();
                    else { button.IsChecked = marked; error.Text = L.T("保存失败，请重试"); error.IsVisible = true; button.Focus(); }
                };
            }
            panel.Children.Add(row);
        }
        panel.Children.Add(error);
        root.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
            var focused = (e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true)
                ?? TopLevel.GetTopLevel(root)?.FocusManager?.GetFocusedElement() as ToggleButton;
            var index = focused is null ? -1 : buttons.IndexOf(focused);
            if (index < 0)
            {
                if (e.Key is not (Key.Enter or Key.Space or Key.Right or Key.Tab)) return;
                buttons[0].Focus(); e.Handled = true; return;
            }
            if (e.Key is Key.Enter or Key.Space)
            {
                focused!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; return;
            }
            var next = e.Key switch { Key.Left => index - 1, Key.Right => index + 1, Key.Up => index - 3,
                Key.Down => index + 3, Key.Tab => index + (e.KeyModifiers == KeyModifiers.Shift ? -1 : 1), _ => index };
            if (next == index) return;
            if (next >= 0 && next < buttons.Count) buttons[next].Focus();
            else if (root.FindAncestorOfType<ContextMenu>() is { } menu)
            {
                var items = menu.Items.OfType<MenuItem>().Where(i => i.IsEnabled && i.IsVisible).ToArray();
                var adjacent = Array.IndexOf(items, root) + (next < 0 ? -1 : 1);
                if (adjacent >= 0 && adjacent < items.Length) items[adjacent].Focus();
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        return root;
    }

    static IEnumerable<(Tag? Value, string Text, bool Marked)> TagChoices(Track[] targets, IReadOnlyDictionary<string, SongTags> current, TagDimension dimension)
    {
        var states = targets.Select(t => current.GetValueOrDefault(t.TrackKey)).ToArray();
        foreach (var value in Core.Analysis.Tags.Of(dimension).Select(t => (Tag?)t).Append(null))
            yield return (value, value is { } tag ? Text.TagName(tag.ToString()) : L.T("恢复自动"),
                states.Length > 0 && states.All(s => value is null ? !Text.TagManual(s, dimension)
                    : Text.TagManual(s, dimension) && Text.TagValue(s, dimension) == value));
    }

    IEnumerable<Control> TagOptions(Track[] targets, IReadOnlyDictionary<string, SongTags> current, TagDimension dimension)
    {
        yield return new MenuItem { Header = SmartTagDialog.Summary(targets, current, dimension), IsEnabled = false };
        yield return new Separator();
        foreach (var (value, text, marked) in TagChoices(targets, current, dimension))
        {
            var item = new MenuItem { Header = text, Name = "Quick" + dimension + (value?.ToString() ?? "Auto"),
                ToggleType = MenuItemToggleType.Radio, IsChecked = marked, IsEnabled = !_quickTagSaving };
            item.Click += async (_, _) => await ApplyQuickTag(targets, dimension, value);
            yield return item;
        }
    }

    ContextMenu? _tagCellMenu;
    void OnTagCellClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button) ShowTagCellOptions(button);
        e.Handled = true;
    }

    void OnTagCellKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Enter or Key.Space)) return;
        if (sender is Button button) ShowTagCellOptions(button);
        e.Handled = true;
    }

    void OnTagCellDoubleTapped(object? sender, TappedEventArgs e) => e.Handled = true;

    internal void ShowTagCellOptions(Button button)
    {
        if (button.DataContext is not SongRow { Track: { } track } || button.Tag is not string name ||
            Core.Analysis.Tags.ParseDimension(name) is not { } dimension || _quickTagSaving) return;
        _tagCellMenu?.Close();
        // Capture both before opening: virtualized cells may be rebound while this menu is open.
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft, PlacementTarget = button };
        foreach (var item in TagOptions([track], _app.SmartTags.Read(), dimension)) menu.Items.Add(item);
        _tagCellMenu = menu;
        menu.Closed += (_, _) => { if (_tagCellMenu == menu) _tagCellMenu = null; };
        menu.Open(button);
    }

    internal async Task ApplyQuickTag(IReadOnlyList<Track> targets, TagDimension dimension, Tag? value)
    {
        if (_quickTagSaving || targets.Count == 0) return;
        _quickTagSaving = true; _quickTagState = "saving"; _quickTagDimension = dimension.ToString(); _quickTagCount = targets.Count;
        try
        {
            await _app.SmartTags.Override(targets, new Dictionary<TagDimension, Tag?> { [dimension] = value });
            _quickTagState = "saved";
        }
        catch (Exception e)
        {
            _quickTagState = "failed";
            Infrastructure.Log.Warn("analysis", "保存智能标签失败", e);
            _vm.ShowToast(L.T("保存失败：") + e.Message);
        }
        finally { _quickTagSaving = false; }
    }

    internal MenuItem TagCorrectionItem(IReadOnlyList<Track> tracks)
    {
        var targets = tracks.ToArray();
        var item = new MenuItem { Header = L.T("修正智能标签…"), IsEnabled = targets.Length > 0 };
        item.Click += async (_, _) => await new SmartTagDialog(_app, targets).ShowDialog(this);
        return item;
    }

    JsonObject SmartTagColumnDiagnostics() => new()
    {
        ["missingValue"] = "—", ["sourceDisplay"] = "cell-tooltip-and-menu", ["cellAction"] = "single-track-dimension-menu", ["cellKeyboard"] = "Enter/Space", ["batchAction"] = "inline-four-row-panel", ["manualCheckPolicy"] = "manual-overrides-only",
        ["columns"] = new JsonArray(TagColumns.Select(def =>
        {
            var column = Table.Columns.Single(c => c.Tag as string == def.Column);
            return (JsonNode?)new JsonObject { ["tag"] = def.Column, ["dimension"] = def.Dimension.ToString(),
                ["visible"] = column.IsVisible, ["width"] = column.ActualWidth, ["order"] = column.DisplayIndex };
        }).ToArray()),
        ["quickCorrection"] = new JsonObject { ["state"] = _quickTagState, ["dimension"] = _quickTagDimension,
            ["targetCount"] = _quickTagCount, ["busy"] = _quickTagSaving }
    };
}
