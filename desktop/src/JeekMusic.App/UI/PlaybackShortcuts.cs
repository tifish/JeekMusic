using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using JeekMusic.Playback;

namespace JeekMusic.UI;

/// <summary>窗口内左右键调整进度、上下键调整音量、Z/X 切歌；在表格之前响应，保留编辑和控件导航。</summary>
internal static class PlaybackShortcuts
{
    public static void Attach(Window window, PlayerService player, Slider? progress = null, Slider? volume = null) =>
        Attach(window, player.Previous, player.Next, seconds => player.SeekBy(TimeSpan.FromSeconds(seconds)), player.AdjustVolume, progress, volume);

    internal static void Attach(Window window, Action previous, Action next, Action<double> seekBy,
        Action<double> adjustVolume, Slider? progress = null, Slider? volume = null)
    {
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Z or Key.X)) return;
            var source = e.Source as Visual ?? window.FocusManager?.GetFocusedElement() as Visual;
            var ancestors = source?.GetSelfAndVisualAncestors().ToList();
            if (ancestors?.Any(v => v is TextBox or ComboBox or NumericUpDown or MenuBase or MenuItem) == true) return;
            if (e.Key is Key.Left or Key.Right && ancestors?.Any(v => v is TreeView || v is Slider && v != progress) == true) return;
            if (e.Key is Key.Up or Key.Down && ancestors?.Any(v => v is DataGrid or ListBox or TreeView ||
                v is Slider && v != volume && v != progress) == true) return;

            switch (e.Key)
            {
                case Key.Left: seekBy(-5); break;
                case Key.Right: seekBy(5); break;
                case Key.Up: adjustVolume(5); break;
                case Key.Down: adjustVolume(-5); break;
                case Key.Z: previous(); break;
                case Key.X: next(); break;
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }
}
