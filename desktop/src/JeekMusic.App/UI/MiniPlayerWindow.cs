using Avalonia.Controls;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.UI;

/// <summary>
/// 迷你播放器（需求 PC-4，设计 §8.3）：封面、歌名、艺术家、进度和播放按钮；可以设为总在最前；和主窗口二选一显示。
/// 用同一个 PlayerViewModel，状态天然一致。
/// </summary>
public partial class MiniPlayerWindow : Window
{
    public event Action? RestoreRequested;
    public event Action<bool>? TopmostChanged;

    public MiniPlayerWindow() => InitializeComponent();

    public MiniPlayerWindow(PlayerViewModel vm, bool topmost) : this()
    {
        DataContext = vm;
        PlaybackShortcuts.Attach(this, vm.Service.Previous, vm.Service.Next);
        Topmost = topmost;
        PinButton.IsChecked = topmost;
        PinButton.IsCheckedChanged += (_, _) => { Topmost = PinButton.IsChecked == true; TopmostChanged?.Invoke(Topmost); };
        RestoreButton.Click += (_, _) => RestoreRequested?.Invoke();
        // 整个窗口都可以拖动
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button) BeginMoveDrag(e);
        };
    }
}
