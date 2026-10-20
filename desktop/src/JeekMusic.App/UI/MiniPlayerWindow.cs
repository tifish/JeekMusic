using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.UI;

/// <summary>
/// 迷你播放器（需求 PC-4，设计 §8.3）：封面、歌名、艺术家、进度和播放按钮；可以设为总在最前；和主窗口二选一显示。
/// 用同一个 PlayerViewModel，状态天然一致。
/// </summary>
public sealed class MiniPlayerWindow : Window
{
    public event Action? RestoreRequested;

    public MiniPlayerWindow(PlayerViewModel vm, bool topmost)
    {
        DataContext = vm;
        Title = "JeekMusic";
        Width = 360;
        Height = 112;
        CanResize = false;
        Topmost = topmost;
        SystemDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur];
        Background = Brushes.Transparent;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://JeekMusic/Assets/JeekMusic.ico")));

        Button IconButton(string glyph, string tip, string command, double size = 16)
        {
            var b = new Button { Classes = { "icon" }, FontSize = size, Content = glyph };
            ToolTip.SetTip(b, tip);
            b.Bind(Button.CommandProperty, new Binding(command));
            return b;
        }
        var play = IconButton(Icons.Play, "播放 / 暂停", nameof(PlayerViewModel.PlayPauseCommand), 20);
        play.Bind(ContentProperty, new Binding(nameof(PlayerViewModel.PlayIcon)));
        var restore = new Button { Classes = { "icon" }, Content = Icons.Expand };
        ToolTip.SetTip(restore, "回到主窗口");
        restore.Click += (_, _) => RestoreRequested?.Invoke();
        var pin = new ToggleButton { Classes = { "icon" }, Content = Icons.Pin, IsChecked = topmost };
        ToolTip.SetTip(pin, "总在最前");
        pin.IsCheckedChanged += (_, _) => { Topmost = pin.IsChecked == true; TopmostChanged?.Invoke(Topmost); };

        var cover = new Image { Stretch = Stretch.UniformToFill };
        cover.Bind(Image.SourceProperty, new Binding(nameof(PlayerViewModel.Cover)));
        var title = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(PlayerViewModel.Title)));
        var artist = new TextBlock { Opacity = 0.65, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        artist.Bind(TextBlock.TextProperty, new Binding(nameof(PlayerViewModel.Artist)));
        var progress = new ProgressBar { Height = 3, MinHeight = 3, Margin = new Thickness(0, 6, 0, 0) };
        progress.Bind(RangeBase.MaximumProperty, new Binding(nameof(PlayerViewModel.DurationSeconds)));
        progress.Bind(RangeBase.ValueProperty, new Binding(nameof(PlayerViewModel.PositionSeconds)));

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0), Children = { title, artist, progress } };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { IconButton(Icons.Previous, "上一首", nameof(PlayerViewModel.PreviousCommand)), play, IconButton(Icons.Next, "下一首", nameof(PlayerViewModel.NextCommand)), pin, restore },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(10, 10, 10, 4) };
        var coverBox = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = cover, Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)) };
        Grid.SetRowSpan(coverBox, 2);
        grid.Children.Add(coverBox);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        Grid.SetColumn(buttons, 1);
        Grid.SetRow(buttons, 1);
        grid.Children.Add(buttons);
        Content = grid;
        // 整个窗口都可以拖动
        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button) BeginMoveDrag(e); };
    }

    public event Action<bool>? TopmostChanged;
}
