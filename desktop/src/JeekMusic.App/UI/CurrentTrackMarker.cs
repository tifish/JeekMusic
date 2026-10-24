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
        FontFamily = new FontFamily(Icons.FontFamily), FontSize = 11, Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    public CurrentTrackMarker()
    {
        Width = Height = 18;
        CornerRadius = new CornerRadius(9);
        Child = _glyph;
        UpdateState();
    }

    internal static string Status(bool playing) => playing ? L.T("正在播放") : L.T("当前歌曲（已暂停）");

    void UpdateState()
    {
        _glyph.Text = IsPlaying ? Icons.Play : Icons.Pause;
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
