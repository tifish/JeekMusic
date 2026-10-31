using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using JeekMusic.Playback;

namespace JeekMusic.UI;

/// <summary>窗口内空格播放/暂停、方向键调整进度/音量、Z/X 切歌；在控件之前响应，保留编辑和菜单操作。</summary>
internal static class PlaybackShortcuts
{
    public static void Attach(Window window, PlayerService player, Slider? progress = null, Slider? volume = null) =>
        Attach(window, player.Previous, player.Next, seconds => player.SeekBy(TimeSpan.FromSeconds(seconds)), player.AdjustVolume, progress, volume, player.PlayPause);

    internal static void Attach(Window window, Action previous, Action next, Action<double> seekBy,
        Action<double> adjustVolume, Slider? progress = null, Slider? volume = null, Action? playPause = null)
    {
        // A press belongs to its initial target/modifiers, even if focus changes while held.
        // Avalonia KeyEventArgs has no repeat flag: release, not a timer, rearms Space.
        var spaceDown = false;
        var spaceHandled = false;
        void ReleaseSpace() { spaceDown = false; spaceHandled = false; }
        window.Deactivated += (_, _) => ReleaseSpace();
        window.Closed += (_, _) => ReleaseSpace();
        window.AddHandler(InputElement.KeyUpEvent, (_, e) =>
        {
            if (e.Key != Key.Space) return;
            if (spaceHandled) e.Handled = true; // Buttons also act on KeyUp.
            ReleaseSpace();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Space)
            {
                if (spaceDown)
                {
                    if (spaceHandled) e.Handled = true;
                    return;
                }
                spaceDown = true;
                var target = e.Source as Visual ?? window.FocusManager?.GetFocusedElement() as Visual;
                var focused = window.FocusManager?.GetFocusedElement() as Visual;
                if (e.KeyModifiers != KeyModifiers.None || playPause is null ||
                    IsEditingOrPopup(target) || IsEditingOrPopup(focused)) return;
                spaceHandled = true;
                e.Handled = true;
                playPause();
                return;
            }
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

        bool IsEditingOrPopup(Visual? target) => target is not null &&
            (TopLevel.GetTopLevel(target) != window || target.GetSelfAndVisualAncestors().Any(v =>
                v is TextBox or AutoCompleteBox or ComboBox or NumericUpDown or MenuBase or MenuItem or OverlayPopupHost ||
                v is Button button && button.Classes.Contains("tag-cell")));
    }
}
