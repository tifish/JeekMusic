using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace JeekMusic.UI;

/// <summary>Current-track identity is independent of selection; the glyph reports real playback state.</summary>
public sealed class CurrentTrackMarker : Border
{
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<CurrentTrackMarker, bool>(nameof(IsPlaying));
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    readonly TextBlock _glyph = new()
    {
        FontFamily = new FontFamily(Icons.FontFamily), FontSize = 9, Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    readonly TextBlock _label = new()
    {
        FontSize = 11, LineHeight = 14, Foreground = Brushes.White,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
    };

    public CurrentTrackMarker()
    {
        Height = 16;
        Padding = new Thickness(5, 0);
        CornerRadius = new CornerRadius(3);
        ClipToBounds = true;
        HorizontalAlignment = HorizontalAlignment.Left;
        _glyph.Margin = new Thickness(0, 0, 4, 0);
        Grid.SetColumn(_label, 1);
        Child = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children = { _glyph, _label }
        };
        UpdateState();
    }

    internal static string Status(bool playing) => playing ? L.T("正在播放") : L.T("当前歌曲（已暂停）");

    void UpdateState()
    {
        _glyph.Text = IsPlaying ? Icons.Play : Icons.Pause;
        _label.Text = IsPlaying ? L.T("正在播放") : L.T("已暂停");
        var status = Status(IsPlaying);
        AutomationProperties.SetName(this, status);
        ToolTip.SetTip(this, status);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPlayingProperty) UpdateState();
    }
}
