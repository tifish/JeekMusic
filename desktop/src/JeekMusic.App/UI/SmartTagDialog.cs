using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using JeekMusic.Core.Analysis;
using JeekMusic.Library;

namespace JeekMusic.UI;

/// <summary>四个二元分类在同一面板编辑；只有应用时才提交明确选中的维度。</summary>
internal sealed class SmartTagDialog : Window
{
    readonly Dictionary<TagDimension, Tag?> _changes = [];
    bool _saving;

    public SmartTagDialog(AppServices app, IReadOnlyList<Track> tracks)
    {
        var targets = tracks.ToArray();
        var current = app.SmartTags.Read();
        Title = L.T("修正智能标签");
        Width = 560;
        MinWidth = 340;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 720;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = targets.Length == 1 ? targets[0].Title : L.F($"已选择 {targets.Length} 首歌曲"),
            TextWrapping = TextWrapping.Wrap, MaxHeight = 52, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var apply = new Button { Name = "ApplyTags", Content = L.T("应用"), IsDefault = true, IsEnabled = false, MinWidth = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center };
        apply.Classes.Add("accent");
        var cancel = new Button { Name = "CancelTags", Content = L.T("取消"), IsCancel = true, MinWidth = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center };
        RadioButton? first = null;
        foreach (var dimension in Enum.GetValues<TagDimension>())
        {
            var name = DimensionName(dimension);
            var group = new StackPanel { Spacing = 6 };
            group.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold });
            group.Children.Add(new TextBlock { Name = "Current" + dimension, Text = Summary(targets, current, dimension),
                TextWrapping = TextWrapping.Wrap, Opacity = .7 });
            var options = new WrapPanel();
            var radios = new List<RadioButton>();
            void Add(string label, string suffix, bool keep, Tag? tag)
            {
                var radio = new RadioButton { Name = dimension + suffix, Content = label, GroupName = dimension.ToString(),
                    IsChecked = keep, Margin = new Thickness(0, 0, 16, 8) };
                AutomationProperties.SetName(radio, name + ": " + label);
                radio.IsCheckedChanged += (_, _) =>
                {
                    if (radio.IsChecked != true) return;
                    if (keep) _changes.Remove(dimension); else _changes[dimension] = tag;
                    apply.IsEnabled = _changes.Count > 0 && !_saving;
                };
                radio.KeyDown += (_, e) =>
                {
                    if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
                    var step = e.Key is Key.Left or Key.Up ? -1 : 1;
                    var next = radios[(radios.IndexOf(radio) + step + radios.Count) % radios.Count];
                    next.IsChecked = true; next.Focus(); e.Handled = true;
                };
                first ??= radio;
                radios.Add(radio); options.Children.Add(radio);
            }
            Add(L.T("保持不变"), "Keep", true, null);
            foreach (var tag in Core.Analysis.Tags.Of(dimension)) Add(Text.TagName(tag.ToString()), tag.ToString(), false, tag);
            Add(L.T("恢复自动"), "Auto", false, null);
            group.Children.Add(options); panel.Children.Add(group);
        }
        var error = new TextBlock { Name = "TagSaveError", TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0), Children = { error,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { apply, cancel } } } };
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new Border { Padding = new Thickness(20), Child = new DockPanel { Children = { footer,
            new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = panel } } } };
        Opened += (_, _) => first?.Focus();
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
        cancel.Click += (_, _) => Close();
        apply.Click += async (_, _) =>
        {
            if (_saving || _changes.Count == 0) return;
            _saving = true; apply.IsEnabled = false; cancel.IsEnabled = false; panel.IsEnabled = false; error.IsVisible = false;
            try
            {
                await app.SmartTags.Override(targets, _changes);
                _saving = false;
                Close();
            }
            catch (Exception e)
            {
                Infrastructure.Log.Warn("analysis", "保存智能标签失败", e);
                error.Text = L.T("保存失败：") + e.Message; error.IsVisible = true;
            }
            finally
            {
                _saving = false; apply.IsEnabled = _changes.Count > 0; cancel.IsEnabled = true; panel.IsEnabled = true;
            }
        };
    }

    static string DimensionName(TagDimension dimension) => dimension switch
    {
        TagDimension.MOOD => L.T("运动"), TagDimension.VOICE => L.T("人声"), TagDimension.CALMNESS => L.T("平静程度"), _ => L.T("曲风"),
    };

    internal static string Summary(IReadOnlyList<Track> tracks, IReadOnlyDictionary<string, SongTags> tags, TagDimension dimension)
    {
        var states = tracks.Select(t =>
        {
            var state = tags.GetValueOrDefault(t.TrackKey);
            var value = state?[dimension.ToString()];
            var manual = state is not null && dimension switch
            {
                TagDimension.MOOD => state.MoodManual, TagDimension.VOICE => state.VoiceManual, TagDimension.CALMNESS => state.CalmnessManual, _ => state.GenreManual,
            };
            return (value, manual);
        }).Distinct().ToArray();
        var values = states.Select(s => s.value).Distinct().ToArray();
        var sources = states.Select(s => s.manual).Distinct().ToArray();
        var label = values.Length != 1 ? L.T("多个值") : values[0] is { } value
            ? Text.TagName(Core.Analysis.Tags.Of(dimension, value)?.ToString()) : L.T("无分析结果");
        var source = sources.Length != 1 ? L.T("自动与手动混合") : sources[0] ? L.T("手动") : L.T("自动");
        return L.F($"当前：{label}（{source}）");
    }
}
